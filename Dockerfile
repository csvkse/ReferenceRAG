# Obsidian RAG Service
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
EXPOSE 5000
EXPOSE 5001

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy solution and project files
COPY ["ObsidianRAG.sln", "./"]
COPY ["src/ObsidianRAG.Service/ObsidianRAG.Service.csproj", "src/ObsidianRAG.Service/"]
COPY ["src/ObsidianRAG.Core/ObsidianRAG.Core.csproj", "src/ObsidianRAG.Core/"]
COPY ["src/ObsidianRAG.Storage/ObsidianRAG.Storage.csproj", "src/ObsidianRAG.Storage/"]
COPY ["src/ObsidianRAG.CLI/ObsidianRAG.CLI.csproj", "src/ObsidianRAG.CLI/"]
COPY ["tests/ObsidianRAG.Tests/ObsidianRAG.Tests.csproj", "tests/ObsidianRAG.Tests/"]

# Restore dependencies
RUN dotnet restore

# Copy source code
COPY ["src/", "src/"]
COPY ["tests/", "tests/"]

# Build
WORKDIR "/src/src/ObsidianRAG.Service"
RUN dotnet build -c Release -o /app/build

# Publish
FROM build AS publish
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

# Final stage
FROM base AS final
WORKDIR /app

# Create directories
RUN mkdir -p /app/data /app/models

# Copy published app
COPY --from=publish /app/publish .

# Set environment variables
ENV ASPNETCORE_URLS=http://+:5000
ENV ASPNETCORE_ENVIRONMENT=Production
ENV DataPath=/app/data
ENV ModelPath=/app/models/bge-small-zh-v1.5.onnx

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=5s --retries=3 \
    CMD curl -f http://localhost:5000/api/system/health || exit 1

ENTRYPOINT ["dotnet", "ObsidianRAG.Service.dll"]
