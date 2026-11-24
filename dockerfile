FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src
COPY DropBoard.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine
WORKDIR /app
COPY --from=build /app/publish .

# Оптимизация потребления RAM для VPS
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_GCServer=0 \
    DOTNET_GCHeapHardLimit=0xC800000

EXPOSE 8080
ENTRYPOINT ["dotnet", "DropBoard.dll"]