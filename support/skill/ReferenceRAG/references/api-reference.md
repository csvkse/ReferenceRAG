# ReferenceRAG API 完整参考

`/api/*` 端点全部参数、模式默认值、响应字段。
**字段命名是 PascalCase**（服务端 `PropertyNamingPolicy = null`），`/v1/*` 例外见 [openai-compat.md](openai-compat.md)。

---

## 1. POST /api/ai/query — 通用语义查询（主力）

| 参数 | 类型 | 说明 |
|------|------|------|
| `Query` | string | 查询词（必填，建议先扩展） |
| `Mode` | string | `HybridRerank` / `Hybrid` / `Standard` / `Quick` / `Deep` |
| `TopK` | int | 返回数量。**建议显式传值**，否则用模式默认 |
| `ContextWindow` | int | 上下文窗口，默认 1 |
| `MaxTokens` | int | 上下文最大 token，默认按模式 |
| `Sources` | string[] | 限定源名，省略 = 全部 |
| `Filters.Tags` | string[] | 按标签过滤 |
| `Filters.Folders` | string[] | 按文件夹过滤 |
| `Filters.DateRange` | object | 按日期范围过滤 |
| `Options.IncludeRecent` | bool | 优先近期文档 |
| `Options.DebiasPopularity` | bool | 降低高频文档权重 |
| `K1` / `B` | float | BM25 调参 |
| `EnableRerank` | bool | 是否重排。`HybridRerank`/`Deep` 强制为 true |
| `RerankTopN` | int | 重排候选数 |
| `Slim` | bool | `true` 省略 `Prompt` 和 `Chunks[*].Content`，**Agent 调用强烈推荐** |

### 模式默认参数

来自服务端 `AdjustRequestByMode`，未显式传参时生效：

| Mode | TopK | ContextWindow | MaxTokens | EnableRerank | 说明 |
|------|------|---------------|-----------|--------------|------|
| `Quick` | 3 | 0 | 1000 | false | 纯向量，快速试探 |
| `Standard` | 10 | 1 | 3000 | false | 纯向量 |
| `Hybrid` | 15 | 1 | 4000 | false | BM25 + 向量混合召回，不带 rerank |
| `Deep` | 20 | 2 | 6000 | **true** | 纯向量 20 条，探索性查询 |
| `HybridRerank` | 10 | 1 | 4000 | **true** | 混合召回 + rerank 精排，**准确率最高，日常首选** |

### 响应字段

```json
{
  "Query": "原始查询",
  "Mode": "HybridRerank",
  "Context": "拼装好的上下文文本，直接用这个回答",
  "Prompt": "发给 LLM 的完整提示词（Slim=true 时省略）",
  "Chunks": [
    {
      "RefId": "81acc4c4-0d78-41e2-a1ef-b6bb2ed5f1a4",
      "FileId": "e7723a0e-129d-42dd-8dff-6557679761c7",
      "Title": "文件标题",
      "HeadingPath": "章节路径",
      "Content": "片段内容（Slim=true 时为空串）",
      "Score": 0.7333,
      "BM25Score": 20.15,
      "EmbeddingScore": 0.594,
      "RerankScore": 0.7333,
      "FilePath": "E:\\Content\\Down\\新建文件夹\\note.md",
      "Source": "新建文件夹",
      "StartLine": 39,
      "EndLine": 48,
      "ObsidianLink": "[[note.md#L39-L48]]"
    }
  ],
  "Files": [
    { "Id": "e7723a0e-...", "Path": "E:\\...\\note.md", "Title": "note", "ChunkCount": 3 }
  ],
  "Stats": {
    "TotalMatches": 6,
    "DurationMs": 5342,
    "EstimatedTokens": 3210,
    "Phases": {
      "ModelLoadMs": 4405,
      "EmbedMs": 79,
      "Bm25Ms": 35,
      "TitleMs": 5,
      "VectorMs": 604,
      "HybridMs": 640,
      "GraphMs": 1,
      "RerankMs": 212
    },
    "PipelineTelemetry": {
      "StrategyName": "HybridRerank",
      "EmbeddingModel": { "ModelName": "bge-m3", "Variant": "Q4_0", "Device": "NVIDIA CUDA 12 ...", "IsGpu": true },
      "RerankModel": { "ModelName": "bge-reranker-base", "Variant": "Q4_K_M", "Device": "...", "IsGpu": true },
      "Steps": [
        { "Phase": "QueryPrepared", "PhaseName": "查询准备", "DurationMs": 2700, "OutputCandidates": 6, "Details": "BM25与Dense向量并行双路检索" },
        { "Phase": "ParallelRetrieving", "PhaseName": "并行召回", "DurationMs": 640 },
        { "Phase": "HybridFusing", "PhaseName": "混合融合", "DurationMs": 1 },
        { "Phase": "GraphExpanding", "PhaseName": "图谱扩展", "DurationMs": 1 },
        { "Phase": "Reranking", "PhaseName": "模型重排", "DurationMs": 1997 },
        { "Phase": "AssemblingResults", "PhaseName": "结果装配", "DurationMs": 1 }
      ]
    }
  },
  "HasMore": false,
  "Suggestion": null,
  "RerankApplied": true,
  "RerankStats": {
    "CandidatesCount": 6,
    "RerankDurationMs": 1997,
    "ModelName": "bge-reranker-base",
    "AverageScore": 0.704
  }
}
```

**用法要点：**

- ⚡ 优先用 `Context` 字段回答，已拼装去重并附 `[[文件#L行号]]` 来源
- `Slim: true` 时 `Chunks[*].Content` 是空串，但 `Context` 仍有全文
- `Stats.Phases.ModelLoadMs` 常占 3-5 秒（模型冷加载），非故障
- `Stats.PipelineTelemetry` 可看实际用的模型、设备、各阶段耗时，排障用

```bash
curl -s -X POST "http://localhost:7897/api/ai/query" \
  -H "Content-Type: application/json; charset=utf-8" \
  --data-binary @- << 'EOF'
{"Query": "Git 分支管理 branch 版本控制", "Mode": "HybridRerank", "TopK": 5, "Slim": true}
EOF
```

---

## 2. POST /api/ai/drill-down — 展开完整上下文

| 参数 | 类型 | 说明 |
|------|------|------|
| `Query` | string | 原始查询词 |
| `RefIds` | string[] | 来自 `/api/ai/query` 的 `Chunks[*].RefId` |
| `ExpandContext` | int | 扩展窗口，默认 2 |

**响应：** `{ "ExpandedChunks": [{ "Content": "...", "Score": 0.9 }], "FullContext": "..." }`

> ⚠️ `RefIds` 必须是 `/api/ai/query` 返回的 `RefId`（GUID 格式）。
> 传 `FileId` 或 chunk 序号会得到 `{"ExpandedChunks":[],"FullContext":""}`，**不报错**。

```bash
curl -s -X POST "http://localhost:7897/api/ai/drill-down" \
  -H "Content-Type: application/json" \
  --data-binary @- << 'EOF'
{"Query": "Git 分支", "RefIds": ["81acc4c4-0d78-41e2-a1ef-b6bb2ed5f1a4"], "ExpandContext": 2}
EOF
```

---

## 3. GET /api/bm25index/search — BM25 关键词搜索

```
GET /api/bm25index/search?query=关键词&topK=10
```

| 参数 | 类型 | 说明 |
|------|------|------|
| `query` | string | 必填，空则 400 `查询语句不能为空` |
| `topK` | int | 默认 10 |

**响应：**
```json
{
  "Query": "ReferenceRAG",
  "TotalResults": 2,
  "DurationMs": 25,
  "Results": [
    { "ChunkId": "...", "Content": "...", "Score": 12.08, "Rank": 1 }
  ]
}
```

> ⚠️ `Content` 是**分词后的空格分隔文本**，不是原始 Markdown。
> 实测形如 `referencerag 聊天 模型 工具 调用 质量 评测 报告`。
> 适合定位，**不适合直接引用给用户**。要原文需拿 `ChunkId` 对应的 `FilePath` 走 `/api/sources/file/lines`。
>
> `Score` 是 BM25 原始分，不同查询间不可比。

**辅助**：`GET /api/bm25index/summary` 返回索引规模：

```json
{
  "TotalIndexedDocuments": 8985,
  "TotalVocabularySize": 0,
  "AverageDocLength": 2661.12,
  "TotalFiles": 31,
  "TotalChunks": 9101
}
```

---

## 4. 图谱接口

当前实例 `EdgeCount` 为 0，这些接口能调但拿不到关联数据。先用 `stats` 判断是否可用。

### GET /api/graph/stats

```json
{ "NodeCount": 21, "DocCount": 8, "TagCount": 0, "HeadingCount": 13, "ExternalCount": 0, "EdgeCount": 0 }
```

`EdgeCount > 0` 才值得调 neighbors/subgraph。

### GET /api/graph/search?q=标题关键词&limit=10

按笔记标题关键词查找节点，适合"有没有一篇关于 XXX 的笔记"。

- `q` 必填，空则 400 `The q field is required.`
- `limit` 范围 1-50，默认 10（自动 Clamp）

**响应：**
```json
[
  {
    "Id": "E:/Content/Down/新建文件夹/note.md#章节标题",
    "Title": "章节标题",
    "Type": "document | heading | tag | external",
    "ChunkIds": ["关联 chunk ID 列表"],
    "Metadata": {}
  }
]
```

> 注意 `Id` 是 `文件路径#标题` 复合串且带 `#` 片段。当前实例 `Type` 只有 `document` 和 `heading`。
> 含中文时传给其他接口必须 URL 编码，否则服务端解出乱码路径报 `节点不存在`。

```bash
curl -s --get "http://localhost:7897/api/graph/search" --data-urlencode "q=Docker 部署" --data-urlencode "limit=5"
```

### GET /api/graph/neighbors?nodeId=<id>&depth=1&edgeTypes=wikilink

| 参数 | 类型 | 说明 |
|------|------|------|
| `nodeId` | string | 必填，空则 400 `nodeId 不能为空` |
| `depth` | int | 1-3，默认 1（自动 Clamp） |
| `edgeTypes` | string | 逗号分隔：`wikilink` / `embed` / `tag`，省略 = 全部 |

**响应：** `{ "RootId": "...", "Depth": 1, "Nodes": [...], "Edges": [...] }`

当前实例 `Nodes`/`Edges` 恒为空数组。

### POST /api/graph/subgraph

已知多个节点 ID，一次取所有邻居关系，自动去重合并。

**请求体：** `{ "RootIds": ["E:/Content/Down/新建文件夹/a.md"], "Depth": 1 }`

- `RootIds` 为空则 400 `rootIds 不能为空`
- `Depth` 1-3，默认 1

**响应：** `{ "RootId": "", "Depth": 1, "Nodes": [...], "Edges": [...] }`

> `RootIds` 传**文件路径**（不带 `#标题`）能命中 `document` 节点。
> 传 `文件路径#标题` 复合串可能查不到。

### GET /api/graph/node?nodeId=<id>

**参数名是 `nodeId`，不是 `id`。** 用 `id` 会报 `The nodeId field is required.`

响应 `{ "Id": "...", "Title": "...", "Type": "...", "ChunkIds": [...], "Metadata": {} }`，
不存在则 404 `节点不存在: <nodeId>`。

### GET /api/graph/rebuild/status

```json
{ "isRebuilding": false }
```

图谱重建中时为 `true`，此时查询结果可能不完整。

---

## 5. POST /api/sources/files/info — 批量取文件结构（无正文）

已知一批文件路径，拿每个文件的章节目录（标题 + 行号范围），**不返回正文**，几乎不耗 token。

**请求体：** `{ "Paths": ["E:/Content/Down/新建文件夹/note.md"] }`

- 单次最多 20 条
- 路径可用正斜杠或反斜杠

**响应：**
```json
{
  "Results": [
    {
      "Path": "E:\\Content\\Down\\新建文件夹\\note.md",
      "Title": "note",
      "Source": "新建文件夹",
      "TotalChunks": 5,
      "TotalLines": 48,
      "Sections": [
        { "Index": 0, "HeadingPath": "ReferenceRAG 聊天模型工具调用与质量评测报告", "StartLine": 1, "EndLine": 10 },
        { "Index": 4, "HeadingPath": "ReferenceRAG 聊天模型工具调用与质量评测报告", "StartLine": 39, "EndLine": 48 }
      ],
      "Error": null
    }
  ]
}
```

> `HeadingPath` 可能是空串（文件无 Markdown 标题，如 `.txt` 小说）。
> `Error` 非空表示该路径失败（未索引、越权等），**其余条目不受影响**。

---

## 6. POST /api/sources/file/lines — 按行范围取内容（含全文）

**请求体：**
```json
{
  "Items": [
    { "Path": "E:/Content/Down/新建文件夹/note.md", "StartLine": 1, "EndLine": 10 },
    { "Path": "E:/Content/Down/新建文件夹/other.md", "StartLine": 0, "EndLine": 0 }
  ]
}
```

| 规则 | 说明 |
|------|------|
| `StartLine`/`EndLine` 均传 `0` | 返回整个文件所有 chunk |
| 行范围匹配 | 按 chunk 的 `StartLine`/`EndLine` 重叠匹配（`chunkStart <= EndLine && chunkEnd >= StartLine`） |
| 单次上限 | 20 条 |
| 路径校验 | 含 `..` 或为空 → 该条目 `Error: "路径无效"`，其余正常 |
| `Items` 为空 | 400 `items 不能为空` |

**响应：**
```json
{
  "Results": [
    {
      "Path": "E:\\Content\\Down\\新建文件夹\\note.md",
      "Title": "note",
      "Source": "新建文件夹",
      "RequestedRange": { "StartLine": 1, "EndLine": 10 },
      "Chunks": [
        { "Index": 0, "HeadingPath": "标题", "StartLine": 1, "EndLine": 10, "Content": "原始 Markdown 正文" }
      ],
      "Error": null
    }
  ]
}
```

> ✅ **这是拿原始正文的正确接口。** `Chunks[*].Content` 是原文，不是分词结果。
> 小说类文件可达 37 万行、200+ chunk，用 `0,0` 会撑爆上下文 —— 先用 `files/info` 看结构。

---

## 7. GET /api/sources — 源列表

**响应：**
```json
[
  {
    "Name": "新建文件夹",
    "Path": "E:\\Content\\Down\\新建文件夹",
    "Type": "Markdown",
    "Enabled": true,
    "FilePatterns": ["*.md", "*.txt"],
    "FileCount": 8,
    "ChunkCount": 8985,
    "LastIndexed": "2026-10-20T21:24:20+08:00"
  }
]
```

## 8. GET /api/sources/{name}/files — 源下文件列表

```
GET /api/sources/{name}/files?page=1&pageSize=50
```

| 参数 | 类型 | 说明 |
|------|------|------|
| `page` | int | 页码，默认 1 |
| `pageSize` | int | 每页条数 |

源名含中文需 URL 编码（`新建文件夹` → `%E6%96%B0%E5%BB%BA%E6%96%87%E4%BB%B6%E5%A4%B9`）。

**响应：**
```json
[
  { "Path": "E:\\Content\\Down\\新建文件夹\\note.md", "ChunkCount": 5, "LastModified": "2026-10-20T21:24:20+08:00", "Hash": "F17A2E55" }
]
```

> **不支持 `q` 或关键词过滤参数**，只有 `page`/`pageSize`，只能全量翻页。

---

## 9. 索引与图谱维护接口

| 端点 | 方法 | 说明 |
|------|------|------|
| `/api/index/summary` | GET | 索引规模摘要 |
| `/api/index/models/current` | GET | 当前嵌入模型（`ModelName`/`Dimension`/`VectorCount`/`ModelExists`） |
| `/api/index/rebuild` | POST | 全量重建索引 |
| `/api/index/rebuild/{sourceName}` | POST | 重建指定源 |
| `/api/index/rechunk` | POST | 重新分块 |
| `/api/index/cleanup` | POST | 清理孤儿数据 |
| `/api/index/jobs` | POST/GET | 索引任务 |
| `/api/index/jobs/all` | GET | 全部任务 |
| `/api/index/jobs/history` | GET/DELETE | 任务历史 |
| `/api/index/jobs/{jobId}` | GET | 单个任务 |
| `/api/index/jobs/{jobId}/cancel` | POST | 取消任务 |
| `/api/graph/rebuild` | POST | 重建图谱 |
| `/api/graph/rebuild/status` | GET | 重建状态 `{ "isRebuilding": bool }` |

> 这些是维护接口，检索场景用不到。`IndexRequiresRebuild`（见下）为 true 时才需要重建。

---

## 10. 服务状态自检

```
GET /api/ai/status
```

**响应关键字段：**
```json
{
  "EmbeddingModel": "bge-m3",
  "EmbeddingVariant": "bge-m3-Q4_0.gguf",
  "EmbeddingFormat": "gguf",
  "EmbeddingDevice": "NVIDIA CUDA 12 (NVIDIA GeForce RTX 4060 Laptop GPU)",
  "EmbeddingIsGpu": true,
  "EmbeddingState": "Unloaded",
  "EmbeddingDimension": 1024,
  "EmbeddingProvider": "gguf",
  "RerankModel": "bge-reranker-base",
  "RerankVariant": "bge-reranker-base-q4_k_m.gguf",
  "RerankIsGpu": true,
  "RerankState": "Unloaded",
  "RerankEnabled": true,
  "RerankLoaded": false,
  "RerankProvider": "gguf",
  "IndexRequiresRebuild": false,
  "Bm25IndexedDocuments": 8985,
  "Bm25HasIndex": true,
  "VectorIndexedChunks": 8985,
  "VectorHasIndex": true,
  "TotalFiles": 8
}
```

**调用报错或结果异常时先查这个：**

- `IndexRequiresRebuild: true` → 索引过期，检索结果不可信，需重建
- `RerankEnabled: false` → `HybridRerank` 模式会退化成 `Hybrid`
- `EmbeddingIsGpu: false` → 嵌入跑在 CPU，查询会明显变慢
- `Bm25HasIndex: false` / `VectorHasIndex: false` → 对应检索通路不可用

---

## 11. 其它辅助端点

| 端点 | 说明 |
|------|------|
| `GET /api/system/health` | `{ "status": "healthy", "version": "1.0.0.0" }` |
| `GET /api/system/status` | 系统运行状态 |
| `GET /api/system/metrics` | 性能指标 |
| `GET /api/system/alerts` | 告警列表 |
| `GET /api/performance/search-trace` | 最近一次检索的分阶段追踪 |
| `GET /api/chat/capabilities` | 当前聊天后端（`BackendType`/`ModelName`/`MaxContextTokens`） |
| `GET /api/paths` | 服务端已知路径列表 |

`/api/chat/capabilities` 用于确认 `/v1/chat/completions` 实际用的后端：

```json
{
  "SupportsVision": false,
  "SupportsToolCalling": true,
  "ModelName": "Qwen3.5-2B-Instruct-GGUF",
  "BackendType": "LocalGguf",
  "MaxContextTokens": 32768,
  "Description": "本地 llama.cpp GGUF 引擎"
}
```

`BackendType` 为 `LocalGguf` = 本地 GGUF 模型；`OpenAiApi` = 外部端点。
