# ReferenceRAG Skill Document

## Project Overview

ReferenceRAG is a high-performance local knowledge base retrieval system designed for Obsidian vaults and Markdown documents. It uses a hybrid retrieval architecture combining vector semantic search with keyword search, supporting automatic indexing, real-time file change monitoring, and precise semantic retrieval.

### Core Features

- **Multi-source Support**: Index multiple folders simultaneously - Obsidian vaults, plain Markdown, document directories
- **Hybrid Retrieval**: BM25 keyword search + Embedding semantic search with score-level weighted fusion
- **Two-stage Search**: Recall + Rerank architecture with Rerank model secondary sorting
- **Auto Indexing**: Real-time file change monitoring with incremental vector index updates
- **GPU Acceleration**: CUDA support for vector computation and rerank inference
- **Local Deployment**: Fully local, data never leaves your machine

---

## Initialization Configuration

Before using this skill, you need to provide the following configuration:

### Required Parameters

| Parameter | Description | Example |
|-----------|-------------|---------|
| `API_BASE_URL` | ReferenceRAG service address | `http://localhost:5000` |
| `API_KEY` | Authentication key (if enabled) | `your-api-key-here` |

### Configuration File Location

Configuration will be saved to: `~/.agents/.env`

### How to Get API Key

1. Start ReferenceRAG service
2. Open Settings page: `http://localhost:5000/settings`
3. Find "Service" section, set API Key
4. Save configuration

### Example Configuration

```env
# ReferenceRAG Configuration
OBSIDIAN_RAG_API_URL=http://localhost:5000
OBSIDIAN_RAG_API_KEY=your-api-key-here
```

---

## API Authentication

### Authentication Method

All API requests require authentication when API Key is configured:

- **Header**: `X-API-Key: your-api-key`
- **Response**: 401 Unauthorized if missing or invalid

### Authentication Flow

```
1. Check if API Key is configured
2. Add X-API-Key header to request
3. Server validates the key
4. Return response or 401/403 error
```

### Error Responses

| Status | Error | Description |
|--------|-------|-------------|
| 401 | Missing API Key | No X-API-Key header provided |
| 403 | Invalid API Key | API Key does not match |

---

## API Reference

### Base URL

```
{API_BASE_URL}/api
```

### Common Headers

```http
Content-Type: application/json
X-API-Key: your-api-key-here
```

### Search API

#### Basic Query

```http
POST /api/ai/query
Content-Type: application/json
X-API-Key: your-api-key

{
  "query": "search keywords",
  "mode": "Hybrid",
  "topK": 10,
  "sources": ["My Notes"]
}
```

**Query Modes**:
| Mode | Description | TopK | MaxTokens |
|------|-------------|------|-----------|
| Quick | Fast query | 3 | 1000 |
| Standard | Standard query | 10 | 3000 |
| Hybrid | Hybrid retrieval | 15 | 4000 |
| HybridRerank | Hybrid + rerank | 10 | 4000 |
| Deep | Deep query | 20 | 6000 |

#### Response Structure

```json
{
  "query": "search keywords",
  "mode": "HybridRerank",
  "chunks": [{
    "refId": "@1",
    "source": "My Notes",
    "filePath": "/path/to/note.md",
    "content": "...",
    "score": 0.89,
    "bm25Score": 0.75,
    "embeddingScore": 0.82,
    "rerankScore": 0.89
  }],
  "context": "# Related Content\n...",
  "stats": {
    "totalMatches": 10,
    "durationMs": 150
  },
  "rerankApplied": true
}
```

#### Drill-down Query

Get expanded context for hit results:

```http
POST /api/ai/drill-down
Content-Type: application/json
X-API-Key: your-api-key

{
  "refIds": ["@1", "@2"],
  "expandContext": 2
}
```

### Index API

#### Start Indexing

```http
POST /api/index
Content-Type: application/json
X-API-Key: your-api-key

{
  "sources": ["My Notes"],
  "force": false
}
```

#### Check Index Status

```http
GET /api/index/{indexId}/status
X-API-Key: your-api-key
```

### Model Management API

#### Get Available Models

```http
GET /api/models
X-API-Key: your-api-key
```

#### Switch Model

```http
POST /api/models/switch
Content-Type: application/json
X-API-Key: your-api-key

{
  "modelName": "bge-large-zh-v1.5",
  "deleteOldVectors": false
}
```

#### Download Model

```http
POST /api/models/download/{modelName}
X-API-Key: your-api-key
```

#### Get Download Progress

```http
GET /api/models/download/{modelName}/progress
X-API-Key: your-api-key
```

#### Rerank Model Management

```http
# Get rerank model list
GET /api/models/rerank
X-API-Key: your-api-key

# Switch rerank model
POST /api/models/rerank/switch
Content-Type: application/json
X-API-Key: your-api-key

{
  "modelName": "bge-reranker-large"
}
```

### Source Management API

#### Get All Sources

```http
GET /api/sources
X-API-Key: your-api-key
```

#### Add Source

```http
POST /api/sources
Content-Type: application/json
X-API-Key: your-api-key

{
  "path": "/path/to/documents",
  "name": "Document Library",
  "type": "Markdown",
  "recursive": true,
  "filePatterns": ["*.md", "*.txt"]
}
```

#### Scan Source Files

```http
GET /api/sources/{name}/scan
X-API-Key: your-api-key
```

### Settings API

#### Get Configuration

```http
GET /api/settings
X-API-Key: your-api-key
```

#### Save Configuration

```http
POST /api/settings
Content-Type: application/json
X-API-Key: your-api-key

{
  "sources": [...],
  "embedding": {...},
  "search": {...}
}
```

### System API

#### Get System Status

```http
GET /api/system/status
X-API-Key: your-api-key
```

#### Get System Metrics

```http
GET /api/system/metrics
X-API-Key: your-api-key
```

---

## Supported Embedding Models

| Model | Dimension | Language | Description |
|-------|-----------|----------|-------------|
| bge-small-zh-v1.5 | 512 | Chinese | Lightweight, fast retrieval |
| bge-base-zh-v1.5 | 768 | Chinese | Balanced performance |
| bge-large-zh-v1.5 | 1024 | Chinese | High precision, GPU recommended |
| bge-m3 | 1024 | Multilingual | Chinese-English mixed |
| bge-base-en-v1.5 | 768 | English | English only |

## Supported Rerank Models

| Model | Description |
|-------|-------------|
| bge-reranker-base | Base rerank model |
| bge-reranker-large | Large rerank model, higher precision |

---

## Configuration Reference

### Complete Configuration Example

```json
{
  "dataPath": "data",
  "sources": [{
    "path": "/Users/name/Obsidian/MyVault",
    "name": "My Notes",
    "enabled": true,
    "type": "Obsidian",
    "filePatterns": ["*.md"],
    "recursive": true,
    "excludeDirs": [".obsidian", ".trash", ".git"],
    "priority": 10
  }],
  "embedding": {
    "modelPath": "models/bge-small-zh-v1.5/model.onnx",
    "modelName": "bge-small-zh-v1.5",
    "useCuda": false,
    "cudaDeviceId": 0,
    "maxSequenceLength": 512,
    "batchSize": 32
  },
  "chunking": {
    "maxTokens": 512,
    "minTokens": 50,
    "overlapTokens": 50,
    "preserveHeadings": true,
    "preserveCodeBlocks": true
  },
  "search": {
    "defaultTopK": 10,
    "contextWindow": 1,
    "similarityThreshold": 0.5,
    "bm25Provider": "fts5"
  },
  "rerank": {
    "enabled": false,
    "modelName": "bge-reranker-base",
    "topN": 10,
    "recallFactor": 3
  },
  "service": {
    "port": 5000,
    "host": "localhost",
    "apiKey": "your-api-key-here"
  }
}
```

---

## Performance Optimization

### Batch Indexing

**Recommended Settings**:
- Batch size: 32-128 (adjust based on GPU memory)
- Concurrent file processing: 4 (avoid file lock conflicts)
- RTX 4060 recommended BatchSize: 64-128

### GPU Acceleration

**CUDA Configuration**:
```json
{
  "embedding": {
    "useCuda": true,
    "cudaDeviceId": 0,
    "cudaLibraryPath": "/usr/local/cuda/lib64"
  },
  "rerank": {
    "useCuda": true,
    "cudaDeviceId": 0
  }
}
```

### Memory Recommendations

| Document Count | BatchSize | Recommended Model | GPU Memory |
|----------------|-----------|-------------------|------------|
| < 1000 | 32 | bge-small-zh | 4GB |
| 1000-10000 | 64 | bge-base-zh | 6GB |
| > 10000 | 128 | bge-large-zh | 8GB+ |

---

## FAQ

### Q: How to choose the right embedding model?

A: Choose based on scenario:
- **Fast retrieval**: bge-small-zh-v1.5 (512 dim, fast)
- **Balanced**: bge-base-zh-v1.5 (768 dim, recommended)
- **High precision**: bge-large-zh-v1.5 (1024 dim, requires GPU)

### Q: How to enable two-stage search?

A: Set `rerank.enabled = true` and download rerank model:
```bash
# Download rerank model
POST /api/models/rerank/download/bge-reranker-base

# Query with HybridRerank mode
POST /api/ai/query
{
  "query": "search content",
  "mode": "HybridRerank"
}
```

### Q: How to handle authentication errors?

A: Check the following:
1. API Key is configured in service settings
2. X-API-Key header is included in request
3. API Key value matches the configured key

---

## Related Links

- [API Documentation](http://localhost:5000/swagger) - Access Swagger UI after starting service
- [GitHub Repository](https://github.com/hlrlive/ReferenceRAG)
