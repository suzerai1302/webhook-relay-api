# ---- build stage ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore (copy csproj/sln first for layer caching)
COPY WebhookRelayAPI.slnx ./
COPY src/WebhookRelay.Core/WebhookRelay.Core.csproj src/WebhookRelay.Core/
COPY src/WebhookRelay.Infrastructure/WebhookRelay.Infrastructure.csproj src/WebhookRelay.Infrastructure/
COPY src/WebhookRelay.API/WebhookRelay.API.csproj src/WebhookRelay.API/
RUN dotnet restore src/WebhookRelay.API/WebhookRelay.API.csproj

# Build + publish
COPY . .
RUN dotnet publish src/WebhookRelay.API/WebhookRelay.API.csproj -c Release -o /app/publish

# ---- runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "WebhookRelay.API.dll"]
