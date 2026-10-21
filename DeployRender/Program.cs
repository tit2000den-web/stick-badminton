// Stick Badminton – server trung chuyển phòng chơi online.
//
// Server KHÔNG chạy logic game. Nó chỉ:
//   1. Tạo phòng với mã 4 ký tự, ghép 2 người (chủ phòng + người vào) theo mã.
//   2. Chuyển nguyên văn mọi tin nhắn từ người này sang người kia.
//
// Giao thức (WebSocket, tin nhắn dạng chữ):
//   Client -> server   {"t":"create"}                tạo phòng
//                      {"t":"join","code":"ABCD"}    vào phòng
//                      {"t":"leave"}                 rời phòng
//   Server -> client   {"t":"created","code":"ABCD"} (cho chủ phòng)
//                      {"t":"peer"}                   (cho chủ phòng: có người vào)
//                      {"t":"joined"}                 (cho người vào)
//                      {"t":"left"}                   (đối phương đã thoát)
//                      {"t":"error","msg":"..."}      (không tìm thấy phòng / phòng đầy ...)
//   Khi đã đủ 2 người, tin nhắn KHÔNG bắt đầu bằng '{' được chuyển thẳng cho đối phương.

using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // bỏ I, O, 0, 1 cho khỏi nhầm
const int MaxMessageBytes = 16 * 1024;
const int MaxRooms = 5000;
var waitingRoomTimeout = TimeSpan.FromMinutes(20);

var builder = WebApplication.CreateBuilder(args);
string port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
var app = builder.Build();

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

var rooms = new ConcurrentDictionary<string, Room>();

// Thư mục chứa bản WebGL của game (nếu có) để server phát luôn game: mọi người chỉ cần mở một đường link là chơi,
// và game tự kết nối lại về chính server này. Tìm theo thứ tự: biến WEB_DIR, ./web, rồi Builds/StickBadminton_WebGL của project.
string? webDir = FindWebDir();

// WebSocket phải được xử lý TRƯỚC phần phát file, nếu không yêu cầu nâng cấp tới "/" sẽ bị trả về index.html.
app.Use(async (ctx, next) =>
{
    if (ctx.WebSockets.IsWebSocketRequest)
    {
        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        await HandleConnection(ws);
        return;
    }
    if (ctx.Request.Path == "/health") { await ctx.Response.WriteAsync("ok"); return; }
    if (webDir == null && ctx.Request.Path == "/") { await ctx.Response.WriteAsync($"Stick Badminton relay server is running. Rooms: {rooms.Count}"); return; }
    await next();
});

if (webDir != null)
{
    var types = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
    types.Mappings[".data"] = "application/octet-stream";
    var files = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(webDir);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = files, ContentTypeProvider = types, ServeUnknownFileTypes = true });
    Console.WriteLine($"Serving game from {webDir}");
}

// Dọn các phòng chờ quá lâu mà không có ai vào.
_ = Task.Run(async () =>
{
    while (true)
    {
        await Task.Delay(TimeSpan.FromMinutes(1));
        foreach (var kv in rooms)
        {
            var r = kv.Value;
            if (r.Guest == null && DateTime.UtcNow - r.Created > waitingRoomTimeout)
            {
                if (rooms.TryRemove(kv.Key, out _))
                {
                    r.Host.Room = null;
                    await r.Host.CloseAsync();
                }
            }
        }
    }
});

Console.WriteLine($"Relay server listening on port {port}");
app.Run();

// ---------------------------------------------------------------------------------------------

async Task HandleConnection(WebSocket ws)
{
    var peer = new Peer(ws);
    var buffer = new byte[MaxMessageBytes];
    try
    {
        while (ws.State == WebSocketState.Open)
        {
            int total = 0;
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer, total, buffer.Length - total), CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close) return;
                total += result.Count;
                if (total >= buffer.Length && !result.EndOfMessage)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.MessageTooBig, "too big", CancellationToken.None);
                    return;
                }
            } while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text) continue;
            string msg = Encoding.UTF8.GetString(buffer, 0, total);
            await OnMessage(peer, msg);
        }
    }
    catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException)
    {
        // kết nối đứt đột ngột – bỏ qua
    }
    finally
    {
        await LeaveRoom(peer);
    }
}

async Task OnMessage(Peer peer, string msg)
{
    // Tin nhắn game: chuyển thẳng cho đối phương
    if (msg.Length == 0 || msg[0] != '{')
    {
        var other = peer.Other();
        if (other != null) await other.SendAsync(msg);
        return;
    }

    string type = "", code = "";
    try
    {
        using var doc = JsonDocument.Parse(msg);
        if (doc.RootElement.TryGetProperty("t", out var t)) type = t.GetString() ?? "";
        if (doc.RootElement.TryGetProperty("code", out var c)) code = (c.GetString() ?? "").Trim().ToUpperInvariant();
    }
    catch (JsonException)
    {
        return;
    }

    switch (type)
    {
        case "create":
            if (peer.Room != null) { await peer.SendAsync(Error("Bạn đang ở trong một phòng")); return; }
            if (rooms.Count >= MaxRooms) { await peer.SendAsync(Error("Server đang quá tải, thử lại sau")); return; }
            var room = CreateRoom(peer);
            await peer.SendAsync($"{{\"t\":\"created\",\"code\":\"{room.Code}\"}}");
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] room {room.Code} created (rooms: {rooms.Count})");
            break;

        case "join":
            if (peer.Room != null) { await peer.SendAsync(Error("Bạn đang ở trong một phòng")); return; }
            if (!rooms.TryGetValue(code, out var target)) { await peer.SendAsync(Error("Không tìm thấy phòng " + code)); return; }
            lock (target)
            {
                if (target.Guest != null) { _ = peer.SendAsync(Error("Phòng đã đủ người")); return; }
                target.Guest = peer;
                peer.Room = target;
                peer.IsHost = false;
            }
            await peer.SendAsync("{\"t\":\"joined\"}");
            await target.Host.SendAsync("{\"t\":\"peer\"}");
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] room {target.Code} full");
            break;

        case "leave":
            await LeaveRoom(peer);
            break;
    }
}

Room CreateRoom(Peer host)
{
    var rng = Random.Shared;
    while (true)
    {
        var chars = new char[4];
        for (int i = 0; i < chars.Length; i++) chars[i] = CodeAlphabet[rng.Next(CodeAlphabet.Length)];
        string code = new string(chars);
        var room = new Room(code, host);
        if (rooms.TryAdd(code, room))
        {
            host.Room = room;
            host.IsHost = true;
            return room;
        }
    }
}

async Task LeaveRoom(Peer peer)
{
    var room = peer.Room;
    if (room == null) return;
    peer.Room = null;
    if (!rooms.TryRemove(room.Code, out _)) return;

    var other = peer.IsHost ? room.Guest : room.Host;
    if (other != null)
    {
        other.Room = null;
        await other.SendAsync("{\"t\":\"left\"}");
    }
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] room {room.Code} closed (rooms: {rooms.Count})");
}

string? FindWebDir()
{
    var candidates = new List<string>();
    var env = Environment.GetEnvironmentVariable("WEB_DIR");
    if (!string.IsNullOrWhiteSpace(env)) candidates.Add(env);
    candidates.Add(Path.Combine(app.Environment.ContentRootPath, "web"));
    candidates.Add(Path.Combine(AppContext.BaseDirectory, "web"));
    for (var dir = new DirectoryInfo(app.Environment.ContentRootPath); dir != null; dir = dir.Parent)
        candidates.Add(Path.Combine(dir.FullName, "Builds", "StickBadminton_WebGL"));
    foreach (var c in candidates)
        if (File.Exists(Path.Combine(c, "index.html"))) return Path.GetFullPath(c);
    return null;
}

static string Error(string msg) => "{\"t\":\"error\",\"msg\":" + JsonSerializer.Serialize(msg, Json.RawText) + "}";

sealed class Room
{
    public string Code { get; }
    public Peer Host { get; }
    public Peer? Guest { get; set; }
    public DateTime Created { get; } = DateTime.UtcNow;
    public Room(string code, Peer host) { Code = code; Host = host; }
}

sealed class Peer
{
    readonly WebSocket ws;
    readonly SemaphoreSlim sendLock = new(1, 1);
    public Room? Room { get; set; }
    public bool IsHost { get; set; }

    public Peer(WebSocket ws) { this.ws = ws; }

    public Peer? Other()
    {
        var r = Room;
        if (r == null) return null;
        return IsHost ? r.Guest : r.Host;
    }

    public async Task SendAsync(string text)
    {
        if (ws.State != WebSocketState.Open) return;
        await sendLock.WaitAsync();
        try
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException or IOException or InvalidOperationException)
        {
            // đối phương đã đứt kết nối – bỏ qua
        }
        finally
        {
            sendLock.Release();
        }
    }

    public async Task CloseAsync()
    {
        try
        {
            if (ws.State == WebSocketState.Open)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None);
        }
        catch { }
    }
}

static class Json
{
    public static readonly JsonSerializerOptions RawText = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}
