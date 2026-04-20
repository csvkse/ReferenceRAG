# ReferenceRAG - Multi-stage Docker Build
# Supports both CPU and GPU (CUDA) modes

# ============================================================
# Stage 1: Build Vue Frontend
# ============================================================
FROM node:22-alpine AS frontend-build
WORKDIR /frontend

# Copy Vue frontend source
COPY ["dashboard-vue/package.json", "dashboard-vue/package-lock.json*", "./"]
COPY dashboard-vue/ ./

# Install dependencies and build
RUN npm ci --prefer-offline && npm run build

# ============================================================
# Stage 2: Build and Publish .NET application
# ============================================================
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files and restore dependencies
COPY src/ReferenceRAG.Core/ReferenceRAG.Core.csproj src/ReferenceRAG.Core/
COPY src/ReferenceRAG.Storage/ReferenceRAG.Storage.csproj src/ReferenceRAG.Storage/
COPY src/ReferenceRAG.Service/ReferenceRAG.Service.csproj src/ReferenceRAG.Service/
COPY Directory.Build.props ./

# Restore dependencies
RUN dotnet restore src/ReferenceRAG.Service/ReferenceRAG.Service.csproj

# Copy source code
COPY src/ ./src/

# Build and publish
WORKDIR /src/src/ReferenceRAG.Service
RUN dotnet publish -c Release -o /app/publish \
    /p:UseAppHost=false \
    /p:DebugType=none \
    /p:DebugSymbols=false

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
COPY appsettings.Docker.json ./appsettings.Production.json

# Copy pre-built Vue frontend (from frontend-build stage)
COPY --from=frontend-build /frontend/dist ./wwwroot

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

ENTRYPOINT ["dotnet", "ReferenceRAG.Service.dll"]

# ============================================================
# GPU variant: build with
#   docker build -t reference-rag:gpu --build-arg VARIANT=gpu .
# ============================================================
