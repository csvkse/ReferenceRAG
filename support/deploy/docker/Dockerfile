# syntax=docker/dockerfile:1
# ReferenceRAG: shared static ES modules, no frontend build stage.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Stage 1: Restore dependencies (cached independently of source code changes)
COPY Directory.Build.props Directory.Packages.props ./
COPY Infrastructure/ReferenceRAG.Infrastructure/ReferenceRAG.Infrastructure.csproj ./Infrastructure/ReferenceRAG.Infrastructure/
COPY Business/ReferenceRAG.Business/ReferenceRAG.Business.csproj ./Business/ReferenceRAG.Business/
COPY Host/ReferenceRAG.ApiHost/ReferenceRAG.ApiHost.csproj ./Host/ReferenceRAG.ApiHost/
COPY Host/ReferenceRAG.WebHost/ReferenceRAG.WebHost.csproj ./Host/ReferenceRAG.WebHost/
RUN dotnet restore Host/ReferenceRAG.WebHost/ReferenceRAG.WebHost.csproj -p:GpuBackend=cpu

# Stage 2: Build & Publish (prune Windows/macOS native runtimes to shrink Linux image)
COPY Infrastructure/ ./Infrastructure/
COPY Business/ ./Business/
COPY Host/ReferenceRAG.ApiHost/ ./Host/ReferenceRAG.ApiHost/
COPY Host/ReferenceRAG.WebHost/ ./Host/ReferenceRAG.WebHost/
COPY support/config/appsettings.template.json ./support/config/
RUN dotnet publish Host/ReferenceRAG.WebHost/ReferenceRAG.WebHost.csproj \
    -c Release \
    -o /app/publish \
    /p:GpuBackend=cpu \
    /p:UseAppHost=false \
    /p:DebugType=none \
    /p:DebugSymbols=false \
    && rm -rf /app/publish/runtimes/win* /app/publish/runtimes/osx*

# ============================================================
# Stage 3: Runtime (CPU version)
# ============================================================
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Install wget for health check, libgomp1 for native CPU inference, libvulkan1 for Vulkan acceleration, and gosu for user privilege drop
RUN apt-get update && apt-get install -y --no-install-recommends \
    wget \
    libgomp1 \
    libvulkan1 \
    gosu \
    && rm -rf /var/lib/apt/lists/*

# Create data directory, compatibility symlink, and non-root user
RUN mkdir -p /app/data /app/models /app/logs \
    && ln -sf /app/logs /app/Logs \
    && groupadd -r appgroup \
    && useradd -r -g appgroup appuser \
    && chown -R appuser:appgroup /app

# Copy published application and entrypoint script
COPY --from=build /app/publish .
COPY support/deploy/docker/docker-entrypoint.sh ./docker-entrypoint.sh
RUN chmod +x ./docker-entrypoint.sh

# Copy Docker-specific configuration
COPY support/config/examples/appsettings.Docker.example.json ./appsettings.json

# Environment variables
ENV ASPNETCORE_URLS=http://+:5000
ENV ASPNETCORE_ENVIRONMENT=Production
ENV DataPath=/app/data
ENV LogsPath=/app/logs
ENV DOTNET_RUNNING_IN_CONTAINER=true
ENV LANG=C.UTF-8
ENV LC_ALL=C.UTF-8
ENV LD_LIBRARY_PATH="/app:/app/runtimes/linux-x64/native:${LD_LIBRARY_PATH}"

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=15s --retries=3 \
    CMD wget --no-verbose --tries=1 --spider http://localhost:5000/api/system/health || exit 1

EXPOSE 5000

ENTRYPOINT ["/app/docker-entrypoint.sh"]
CMD ["dotnet", "ReferenceRAG.WebHost.dll"]
