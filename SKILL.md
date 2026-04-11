# ObsidianRAG 技能文档

## 项目概述

ObsidianRAG 是一个高性能的本地知识库检索系统，专为 Obsidian 笔记库和 Markdown 文档设计。系统采用向量语义搜索与关键词搜索相结合的混合检索架构，支持自动索引、实时监控文件变更，并提供精准的语义检索能力。

### 核心特性

- **多源支持**：同时索引多个文件夹，支持 Obsidian 笔记库、普通 Markdown、文档目录等
- **混合检索**：BM25 关键词搜索 + Embedding 语义搜索，使用分数级加权融合算法
- **两阶段搜索**：召回 + 精排架构，支持 Rerank 模型二次排序
- **自动索引**：文件变更实时监控，增量更新向量索引
- **GPU 加速**：支持 CUDA 加速向量计算和重排推理
- **本地部署**：完全本地化，数据不出本地

---

## 核心技能说明

### 1. 文件变更自动索引

系统通过 `FileMonitorService` 实现文件变更的实时监控：

**工作原理**：
- 使用 `FileSystemWatcher` 监控 `.md` 文件变更
- per-file 防抖机制（默认 500ms），避免频繁触发
- 支持 Created、Modified、Deleted、Renamed 事件
- 变更事件通过队列异步处理

**关键特性**：
- **Chunk 级 Hash 精筛**：只重新嵌入内容变更的 chunk，跳过未变内容
- **面包屑前置**：嵌入内容带上文件标题 + 标题，提升向量质量
- **启动同步**：mtime 粗筛 + 快照对比，捕获离线期间变更

**配置示例**：
```json
{
  "sources": [{
    "path": "/path/to/vault",
    "name": "我的笔记",
    "type": "Obsidian",
    "filePatterns": ["*.md"],
    "recursive": true,
    "excludeDirs": [".obsidian", ".trash", ".git"]
  }]
}
```

### 2. 向量语义搜索

使用 ONNX Runtime 运行本地 Embedding 模型：

**支持的向量模型**：
| 模型 | 维度 | 语言 | 说明 |
|------|------|------|------|
| bge-small-zh-v1.5 | 512 | 中文 | 轻量级，适合快速检索 |
| bge-base-zh-v1.5 | 768 | 中文 | 平衡性能与精度 |
| bge-large-zh-v1.5 | 1024 | 中文 | 高精度，推荐 GPU |
| bge-m3 | 1024 | 多语言 | 支持中英混合 |
| bge-base-en-v1.5 | 768 | 英文 | 英文专用 |

**非对称编码支持**：
- `Symmetric`：查询和文档使用相同编码
- `Query`：查询文本添加前缀编码
- `Document`：文档文本添加前缀编码

### 3. 混合检索 (BM25 + Embedding)

系统使用 **分数级加权融合 (Score-level Weighted Fusion)** 算法：

**融合公式**：
```
finalScore = w1 * norm(BM25) + w2 * norm(Embedding)
```

**默认权重配置**：
- BM25 权重：0.6（关键词精确匹配）
- Embedding 权重：0.4（语义相似度）

**BM25 参数**：
- `k1`：词频饱和参数，默认 1.5
- `b`：文档长度归一化参数，默认 0.75

**优势**：
- 结合关键词精确匹配和语义理解
- 避免单一检索模式的局限性
- 可调节权重适应不同场景

### 4. Rerank 重排

两阶段搜索架构：召回 → 精排

**工作流程**：
1. **召回阶段**：混合检索返回 `TopK * RecallFactor` 个候选
2. **精排阶段**：Rerank 模型对候选进行精细化评分
3. **返回结果**：按重排分数返回 TopN 个结果

**支持的重排模型**：
- bge-reranker-base
- bge-reranker-large

**配置参数**：
```json
{
  "rerank": {
    "enabled": true,
    "modelName": "bge-reranker-base",
    "topN": 10,
    "recallFactor": 3,
    "autoRerankInHybrid": true,
    "scoreThreshold": 0.0
  }
}
```

---

## API 使用指南

### 搜索 API

#### 基础查询

```http
POST /api/ai/query
Content-Type: application/json

{
  "query": "搜索关键词",
  "mode": "Hybrid",
  "topK": 10,
  "sources": ["我的笔记"]
}
```

**查询模式**：
| Mode | 说明 | TopK | MaxTokens |
|------|------|------|-----------|
| Quick | 快速查询 | 3 | 1000 |
| Standard | 标准查询 | 10 | 3000 |
| Hybrid | 混合检索 | 15 | 4000 |
| HybridRerank | 混合+重排 | 10 | 4000 |
| Deep | 深度查询 | 20 | 6000 |

#### 响应结构

```json
{
  "query": "搜索关键词",
  "mode": "HybridRerank",
  "chunks": [{
    "refId": "@1",
    "source": "我的笔记",
    "filePath": "/path/to/note.md",
    "content": "...",
    "score": 0.89,
    "bm25Score": 0.75,
    "embeddingScore": 0.82,
    "rerankScore": 0.89
  }],
  "context": "# 相关内容\n...",
  "stats": {
    "totalMatches": 10,
    "durationMs": 150
  },
  "rerankApplied": true,
  "rerankStats": {
    "candidatesCount": 30,
    "rerankDurationMs": 45,
    "modelName": "bge-reranker-base"
  }
}
```

#### 深入查询

获取命中结果的上下文扩展：

```http
POST /api/ai/drill-down
Content-Type: application/json

{
  "refIds": ["@1", "@2"],
  "expandContext": 2
}
```

### 索引 API

#### 启动索引

```http
POST /api/index
Content-Type: application/json

{
  "sources": ["我的笔记"],
  "force": false
}
```

#### 查看索引状态

```http
GET /api/index/{indexId}/status
```

### 模型管理 API

#### 获取可用模型

```http
GET /api/models
```

#### 切换模型

```http
POST /api/models/switch
Content-Type: application/json

{
  "modelName": "bge-large-zh-v1.5",
  "deleteOldVectors": false
}
```

#### 下载模型

```http
POST /api/models/download/{modelName}
```

#### 获取下载进度

```http
GET /api/models/download/{modelName}/progress
```

#### 重排模型管理

```http
# 获取重排模型列表
GET /api/models/rerank

# 切换重排模型
POST /api/models/rerank/switch
{
  "modelName": "bge-reranker-large"
}
```

### 源管理 API

#### 获取所有源

```http
GET /api/sources
```

#### 添加源

```http
POST /api/sources
Content-Type: application/json

{
  "path": "/path/to/documents",
  "name": "文档库",
  "type": "Markdown",
  "recursive": true,
  "filePatterns": ["*.md", "*.txt"]
}
```

#### 扫描源文件

```http
GET /api/sources/{name}/scan
```

---

## 配置说明

### 完整配置示例

```json
{
  "dataPath": "data",
  "sources": [
    {
      "path": "/Users/name/Obsidian/MyVault",
      "name": "我的笔记",
      "enabled": true,
      "type": "Obsidian",
      "filePatterns": ["*.md"],
      "recursive": true,
      "excludeDirs": [".obsidian", ".trash", ".git"],
      "priority": 10
    }
  ],
  "embedding": {
    "modelPath": "models/bge-small-zh-v1.5/model.onnx",
    "modelName": "bge-small-zh-v1.5",
    "useCuda": false,
    "cudaDeviceId": 0,
    "maxSequenceLength": 512,
    "batchSize": 32,
    "modelsPath": "models"
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
    "enableMmr": true,
    "mmrLambda": 0.7,
    "bm25Provider": "fts5"
  },
  "rerank": {
    "enabled": false,
    "modelName": "bge-reranker-base",
    "topN": 10,
    "recallFactor": 3,
    "autoRerankInHybrid": true,
    "scoreThreshold": 0.0
  },
  "service": {
    "port": 5000,
    "host": "localhost",
    "enableCors": true,
    "enableSwagger": true
  }
}
```

### 配置项详解

#### EmbeddingConfig

| 字段 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| modelPath | string | "" | 模型文件路径 |
| modelName | string | "bge-small-zh-v1.5" | 模型名称 |
| useCuda | bool | false | 是否使用 CUDA |
| cudaDeviceId | int | 0 | CUDA 设备 ID |
| maxSequenceLength | int | 512 | 最大序列长度 |
| batchSize | int | 32 | 批处理大小 |
| modelsPath | string | "models" | 模型保存根目录 |

#### ChunkingConfig

| 字段 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| maxTokens | int | 512 | 最大 Token 数 |
| minTokens | int | 50 | 最小 Token 数 |
| overlapTokens | int | 50 | 重叠 Token 数 |
| preserveHeadings | bool | true | 保留标题 |
| preserveCodeBlocks | bool | true | 保留代码块 |

#### SearchConfig

| 字段 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| defaultTopK | int | 10 | 默认返回数量 |
| contextWindow | int | 1 | 上下文窗口大小 |
| similarityThreshold | float | 0.5 | 相似度阈值 |
| enableMmr | bool | true | 启用 MMR 多样性 |
| bm25Provider | string | "fts5" | BM25 提供者 |

#### RerankConfig

| 字段 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| enabled | bool | false | 是否启用重排 |
| modelName | string | "bge-reranker-base" | 模型名称 |
| topN | int | 10 | 重排后返回数量 |
| recallFactor | int | 3 | 召回倍数 |
| autoRerankInHybrid | bool | true | Hybrid 模式自动重排 |
| scoreThreshold | float | 0.0 | 分数阈值 |

---

## 性能优化建议

### 批量索引

**推荐配置**：
- 批处理大小：32-128（根据 GPU 显存调整）
- 并发文件处理：4 个（避免文件锁冲突）
- RTX 4060 推荐 BatchSize：64-128

**优化代码**：
```csharp
// 使用 IndexingPipeline 进行批量处理
using var pipeline = new IndexingPipeline(
    embeddingService, 
    vectorStore, 
    batchSize: config.Embedding.BatchSize
);
await pipeline.ExecuteAsync(chunks, sourceName);
```

### GPU 加速

**CUDA 配置**：
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

**CUDA 确定性推理**（解决结果波动）：
```csharp
// ONNX Runtime CUDA 确定性设置
sessionOptions.AddConfigEntry("session.enable_cuda_graph", true);
sessionOptions.AddConfigEntry("cuda.cudnn_conv_algo_search", "EXHAUSTIVE");
```

### 缓存策略

**BM25 索引预热**：
- 服务启动时自动加载已存在的索引
- 新建索引时会索引所有文档
- 避免每次启动重建索引

**向量复用**：
- Chunk 级 Hash 检测内容变更
- 内容未变则保留现有向量
- 减少不必要的 GPU 计算

### 内存优化

**推荐配置**（按文档规模）：

| 文档数量 | BatchSize | 推荐模型 | GPU 显存 |
|----------|-----------|----------|----------|
| < 1000 | 32 | bge-small-zh | 4GB |
| 1000-10000 | 64 | bge-base-zh | 6GB |
| > 10000 | 128 | bge-large-zh | 8GB+ |

### 索引性能对比

| 场景 | 文件数 | 处理时间 | Chunk 数 |
|------|--------|----------|----------|
| 首次索引 | 1000 | ~3 分钟 | ~15000 |
| 增量索引 | 10 | ~5 秒 | ~150 |
| 重排查询 | - | ~50ms | 30 候选 |

---

## 常见问题

### Q: 如何选择合适的向量模型？

A: 根据场景选择：
- **快速检索**：bge-small-zh-v1.5（512 维，速度快）
- **平衡性能**：bge-base-zh-v1.5（768 维，推荐）
- **高精度需求**：bge-large-zh-v1.5（1024 维，需 GPU）

### Q: 混合检索权重如何调整？

A: 通过 `HybridSearchOptions` 配置：
```csharp
var options = new HybridSearchOptions
{
    BM25Weight = 0.6f,      // 关键词精确匹配
    EmbeddingWeight = 0.4f,  // 语义相似度
    BM25Options = { K1 = 1.5f, B = 0.75f }
};
```

### Q: 如何启用两阶段搜索？

A: 设置 `rerank.enabled = true`，并下载重排模型：
```bash
# 下载重排模型
POST /api/models/rerank/download/bge-reranker-base

# 使用 HybridRerank 模式查询
POST /api/ai/query
{
  "query": "搜索内容",
  "mode": "HybridRerank"
}
```

### Q: 如何处理 WSL 路径问题？

A: 系统自动处理路径转换，Windows 路径会转换为 `/mnt/x/` 格式。如遇问题，可检查 `PathUtility.NormalizePath()` 方法。

---

## 相关链接

- [VecWatch 设计文档](docs/VECWATCH_DESIGN.md) - 文件变更索引架构详解
- [多源文件夹配置](docs/MULTI_SOURCE.md) - 多源管理详细指南
- [API 文档](http://localhost:5000/swagger) - 运行服务后访问 Swagger UI
