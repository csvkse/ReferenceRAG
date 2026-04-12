# Obsidian RAG Service
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 5000
EXPOSE 5001

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution and project files
COPY ["ReferenceRAG.sln", "./"]
COPY ["src/ReferenceRAG.Service/ReferenceRAG.Service.csproj", "src/ReferenceRAG.Service/"]
COPY ["src/ReferenceRAG.Core/ReferenceRAG.Core.csproj", "src/ReferenceRAG.Core/"]
COPY ["src/ReferenceRAG.Storage/ReferenceRAG.Storage.csproj", "src/ReferenceRAG.Storage/"]
COPY ["tests/ReferenceRAG.Tests/ReferenceRAG.Tests.csproj", "tests/ReferenceRAG.Tests/"]

# Restore dependencies
RUN dotnet restore

# Copy source code
COPY ["src/", "src/"]
COPY ["tests/", "tests/"]

# Build
WORKDIR "/src/src/ReferenceRAG.Service"
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

# Health check (using wget as curl may not be available in aspnet base image)
HEALTHCHECK --interval=30s --timeout=10s --start-period=5s --retries=3 \
    CMD wget --no-verbose --tries=1 --spider http://localhost:5000/api/system/health || exit 1

# Create non-root user and set permissions
RUN adduser --disabled-password --gecko '' appuser \
    && chown -R appuser:appuser /app
USER appuser

ENTRYPOINT ["dotnet", "ReferenceRAG.Service.dll"]
