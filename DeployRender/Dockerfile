# Đóng gói server trung chuyển (kèm bản web của game nếu có thư mục "web") để đưa lên Render / Fly.io / Railway / VPS...
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY RelayServer.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app --no-restore
# Nếu đã chép bản WebGL vào thư mục "web" (xem ChuanBiDeploy.bat) thì server sẽ phát luôn game.
RUN mkdir -p /app/web && if [ -d web ]; then cp -r web/. /app/web/; fi

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# Các nền tảng hosting sẽ đặt biến PORT; mặc định 8080.
ENV PORT=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "RelayServer.dll"]
