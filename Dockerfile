# ── Stage 1: Build frontend ───────────────────────────────────────────────────
FROM node:24-slim AS frontend-build

WORKDIR /app/frontend
COPY frontend/package*.json ./
RUN npm ci

COPY frontend/ ./
RUN npm run build

# ── Stage 2: Build backend ────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend-build

WORKDIR /app
COPY IoTSpy.sln nuget.config ./
COPY src/ ./src/

RUN dotnet restore
RUN dotnet publish src/IoTSpy.Api/IoTSpy.Api.csproj \
      -c Release \
      -o /publish \
      --no-restore

# ── Stage 3: Runtime ──────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# SharpPcap / PacketDotNet require libpcap at runtime on Linux
RUN apt-get update && apt-get install -y --no-install-recommends \
        libpcap0.8 \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

COPY --from=backend-build /publish ./
COPY --from=frontend-build /app/frontend/dist ./wwwroot

# 5000 = REST API / SignalR / static frontend (HTTP)
# 5001 = REST API / SignalR (HTTPS — requires Kestrel:Certificate or LetsEncrypt config)
# 8888 = explicit HTTP/HTTPS proxy listener
EXPOSE 5000 5001 8888

ENV ASPNETCORE_URLS=http://+:5000
ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "IoTSpy.Api.dll"]
