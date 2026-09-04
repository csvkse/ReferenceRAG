# ReferenceRAG: shared static ES modules, no frontend build stage.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY Infrastructure/ ./Infrastructure/
COPY Business/ ./Business/
COPY Host/ReferenceRAG.ApiHost/ ./Host/ReferenceRAG.ApiHost/
COPY Host/ReferenceRAG.WebHost/ ./Host/ReferenceRAG.WebHost/
RUN dotnet publish Host/ReferenceRAG.WebHost/ReferenceRAG.WebHost.csproj -c Release -o /app/publish /p:UseAppHost=false /p:DebugType=none /p:DebugSymbols=false

# ============================================================
# Stage 3: Runtime (CPU version)
# ============================================================
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Install wget for health check and other utilities
RUN apt-get update && apt-get install -y --no-install-recommends \
    wget \
    unzip \
    && rm -rf /var/lib/apt/lists/*

# Create data directory and non-root user
RUN mkdir -p /app/data /app/models /app/logs \
    && groupadd -r appgroup \
    && useradd -r -g appgroup appuser \
    && chown -R appuser:appgroup /app

# Copy published application
COPY --from=build /app/publish .

# Copy Docker-specific configuration
COPY support/config/examples/appsettings.Docker.example.json ./appsettings.json


# Environment variables
ENV ASPNETCORE_URLS=http://+:5000
ENV ASPNETCORE_ENVIRONMENT=Production
ENV DataPath=/app/data
ENV LogsPath=/app/logs
ENV DOTNET_RUNNING_IN_CONTAINER=true

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=15s --retries=3 \
    CMD wget --no-verbose --tries=1 --spider http://localhost:5000/api/system/health || exit 1

# Switch to non-root user
USER appuser

EXPOSE 5000

ENTRYPOINT ["dotnet", "ReferenceRAG.WebHost.dll"]

# ============================================================
# GPU variant: build with
#   docker build -t reference-rag:gpu --build-arg VARIANT=gpu .
# ============================================================
