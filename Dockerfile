# ==========================================================
# LiveStreamGateway Multi-Stage Dockerfile (.NET 10 + FFmpeg)
# Supports: linux/amd64, linux/arm64
# ==========================================================

# 1. Build Stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project file and restore dependencies
COPY ["LiveStreamGateway.csproj", "global.json", "./"]
RUN dotnet restore "LiveStreamGateway.csproj"

# Copy source code and wwwroot
COPY . .
RUN dotnet publish "LiveStreamGateway.csproj" -c Release -o /app/publish /p:UseAppHost=false

# 2. Runtime Stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Install FFmpeg, CA certificates, Timezone data and curl (for container healthcheck)
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
        ffmpeg \
        ca-certificates \
        tzdata \
        curl && \
        rm -rf /var/lib/apt/lists/*

# Set default timezone to Asia/Shanghai
ENV TZ=Asia/Shanghai
RUN ln -snf /usr/share/zoneinfo/$TZ /etc/localtime && echo $TZ > /etc/timezone

# Copy published application
COPY --from=build /app/publish .

# Create directory for HLS stream segments
RUN mkdir -p /app/hls_stream

# Expose HTTP service port
EXPOSE 9898

# Start the gateway
ENTRYPOINT ["dotnet", "LiveStreamGateway.dll"]
