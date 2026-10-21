---
name: ReferenceRAG
description: "本地知识库语义检索服务。从 Obsidian 笔记库检索技术教程、配置说明、最佳实践等内容。触发规则：(1)强制触发：rag:关键词、/rag 关键词 (2)组合触发：领域词(知识库/笔记/vault/obsidian/文档) + 动作词(搜/查/找/检索/查询) (3)意图触发：笔记里有没有、帮我搜一下笔记、在知识库中查找。NOT for: 天气、新闻、股票等实时信息。"
allowed-tools:
  - Read
  - Bash
---

# ReferenceRAG 知识库检索

本地知识库检索服务，支持 Obsidian 笔记和 Markdown 文档的语义搜索、关键词搜索、知识图谱查询。

## 服务地址

```
BASE_URL=http://localhost:7897
```

端口由 `appsettings.json` 的 `ReferenceRAG:Service:port` 决定，非 7897 时自行替换下文所有 URL。
自定义地址可写入 `~/.agents/.env` 的 `OBSIDIAN_RAG_API_URL`。

## 认证

服务可配置 API Key。当前实例 `ReferenceRAG:Service:apiKey` 为空，**所有端点无需认证头**。

若服务端配置了 Key，则对 `/api/*`、`/v1/*`、`/indexHub` 路径需附带头部：
```
X-API-Key: <ApiKey>
```
或
```
Authorization: Bearer <ApiKey>
```

---

## 调用编码（必读，踩过坑）

请求体中的中文字符串**必须按 UTF-8 字节发出**。用错误编码会得到两种结果之一：

- **旧版本服务**：返回 `500` 且 `Content-Length: 0`（空白 500，无任何错误信息）
- **新版本服务**：返回 `400` 并提示 `must be valid UTF-8 encoded text`

Git Bash 的 `curl -d '{"query":"中文"}'` 会把中文按控制台代码页（GBK）发出，触发上述问题。
PowerShell 同样不能用 `-d` 直接传中文。

**推荐做法**：用 heredoc + `--data-binary`（Git Bash），或先转 UTF-8 字节再发。

```bash
# Git Bash：heredoc + --data-binary，文件内容是 UTF-8
curl -s -X POST "http://localhost:7897/api/ai/query" \
  -H "Content-Type: application/json; charset=utf-8" \
  --data-binary @- << 'EOF'
{"Query": "Git 分支管理 branch 版本控制", "Mode": "HybridRerank", "TopK": 5}
EOF
```

```powershell
# PowerShell：显式 UTF-8 编码
$body = '{"Query": "Git 分支管理", "Mode": "HybridRerank", "TopK": 5}'
$bytes = [System.Text.Encoding]::UTF8.GetBytes($body)
Invoke-WebRequest -Uri "http://localhost:7897/api/ai/query" `
  -Method Post -Body $bytes -ContentType "application/json; charset=utf-8"
```

查询词含 ASCII 纯英文/数字/标点时无此问题，两种方式均可。

---

## 当前实例状态（2026-10-22 实测）

| 项 | 值 |
|---|---|
| 数据源 | `新建文件夹` → `E:\Content\Down\新建文件夹` |
| 文件数 / 分块数 | 8 文件 / 8985 分块 |
| 嵌入模型 | `bge-m3`（Q4_0 GGUF，1024 维，CUDA / RTX 4060 Laptop） |
| 重排模型 | `bge-reranker-base`（Q4_K_M GGUF，同 GPU） |
| 图谱 | 21 节点（8 文档 + 13 标题）、0 边 |
| 最后索引 | 2026-10-20 21:24 |

**图谱边数为 0**：当前数据源的 `.md` 文件内没有 `[[双链]]`，`.txt` 小说不解析链接。
因此 `/api/graph/neighbors`、`/api/graph/subgraph` 会返回空 `Nodes`/`Edges`，
**在图谱建边前不要依赖图谱接口做关联探索**，直接用 `/api/ai/query` 检索。

**索引内容混杂**：源目录同时含技术文档和中文小说（`操虫师.txt`、`剑来.txt` 等）。
检索无技术内容的词（如"你好"）会召回小说正文，属预期噪声，不是故障。

---

## 字段命名大小写（易错）

服务端 `Program.cs` 设置 `PropertyNamingPolicy = null`，**JSON 字段是 PascalCase**：

```json
{ "Query": "...", "Mode": "HybridRerank", "TopK": 5, "Slim": true }
```

而响应也是 PascalCase（`Chunks`、`RefId`、`HeadingPath`、`BM25Score`）。

`/v1/*` OpenAI 兼容端点是例外，用 camelCase（`query`、`documents`、`input`）。

**`/api/ai/query` 用 `query` 小写也能工作**（模型绑定不区分大小写），
但 `slim` 这类 bool 字段建议用文档一致的 `Slim`，避免歧义。

---

## Agent 决策树

```
用户意图
├── 通用语义查询（绝大多数情况）
│   └── POST /api/ai/query  [Mode: HybridRerank]
│       ├── 结果相关但内容不够 → POST /api/ai/drill-down
│       ├── 需要某文件特定章节 → POST /api/sources/files/info 拿目录 → POST /api/sources/file/lines 取行范围
│       └── 已知文件路径要全文 → POST /api/sources/file/lines  (0,0)
│
├── 精确关键词 / 错误码 / API 名 / 变量名
│   └── GET /api/bm25index/search
│
├── 按笔记标题找某篇具体文档
│   └── GET /api/graph/search?q=标题关键词
│       └── 按行范围取内容 → POST /api/sources/file/lines
│
└── 列出知识库有哪些文件
    └── GET /api/sources  →  GET /api/sources/{name}/files
```

> 图谱关联接口（neighbors/subgraph）当前实例边数为 0，暂不列入主路径。

---

## 查询前处理（调用任何搜索 API 前必须执行）

**调用 `/api/ai/query` 或 `/api/bm25index/search` 前，必须先把用户输入扩展为丰富查询串。**

1. 提取核心名词和技术术语
2. 补充中英文同义词、缩写、别名
3. 补充上位/下位概念、相关操作动词
4. 拼接为单个字符串（空格分隔，无需引号）

| 用户输入 | 扩展后查询 |
|----------|-----------|
| `docker 怎么配置网络` | `docker 网络配置 network docker-compose 容器网络 bridge overlay port mapping 端口映射` |
| `项目笔记整理方法` | `笔记整理 知识管理 PKM 项目管理 Obsidian 工作流 笔记结构 分类 归档` |
| `git 撤销提交` | `git 撤销 revert reset undo 回滚 commit 提交 撤回 版本控制` |
| `Python 读取配置文件` | `Python config 配置文件 读取 解析 yaml json ini configparser settings` |
| `如何做好需求分析` | `需求分析 需求文档 PRD 用户故事 功能规格 业务需求 需求评审 需求收集` |

> 简单 2-3 词查询不扩展，召回率明显下降。扩展后再搜。

---

## 精读策略（按需使用）

### 触发条件

- 用户明确要求"完整内容"/"读全文"/"这篇文档说了什么"
- 搜索结果来自同一文件多个碎片 chunk（score 差异 < 0.1）
- 需要某文件特定章节，但 chunk 被截断且 drill-down 仍不完整
- 已知文件路径，需精确取某行范围

### 执行步骤

```
步骤 1：从搜索结果取 FilePath（绝对路径，反斜杠 Windows 格式）

步骤 2：POST /api/sources/files/info
        → 拿章节目录（HeadingPath + StartLine + EndLine）
        → 不返回正文，几乎不耗 token
        → 单次最多 20 条路径

步骤 3：按 HeadingPath 和行号判断哪些章节相关

步骤 4：POST /api/sources/file/lines
        → Items: [{ Path, StartLine, EndLine }]
        → 只取目标章节，精准省 token
        → 单次最多 20 条

步骤 5：用取到的内容回答
```

### 全文读取

`StartLine: 0, EndLine: 0` 返回整个文件所有 chunk：

```json
{ "Items": [{ "Path": "E:/Content/Down/新建文件夹/note.md", "StartLine": 0, "EndLine": 0 }] }
```

> 先用 `files/info` 看结构，确认需要全文再用 `0,0`。
> 小说类文件可达 37 万行、200+ chunk，全文会撑爆上下文。

---

## API 参考

### 1. 通用语义查询（主力接口）

```
POST /api/ai/query
```

| 参数 | 类型 | 说明 |
|------|------|------|
| Query | string | 查询词（必填，建议先扩展） |
| Mode | string | `HybridRerank`（默认推荐）/ `Hybrid` / `Standard` / `Quick` / `Deep` |
| TopK | int | 返回数量。**显式传值**，否则用模式默认值 |
| ContextWindow | int | 上下文窗口，默认 1 |
| MaxTokens | int | 上下文最大 token，默认按模式 |
| Sources | string[] | 限定源名，省略 = 全部 |
| Filters.Tags | string[] | 按标签过滤 |
| Filters.Folders | string[] | 按文件夹过滤 |
| EnableRerank | bool | 是否重排。`HybridRerank`/`Deep` 模式强制为 true |
| RerankTopN | int | 重排候选数 |
| Slim | bool | `true` 省略 `Prompt` 和 `Chunks[*].Content`，**Agent 调用强烈推荐** |

**模式默认参数**（来自 `AdjustRequestByMode`，未显式传参时生效）：

| Mode | TopK | ContextWindow | MaxTokens | EnableRerank |
|------|------|---------------|-----------|--------------|
| `Quick` | 3 | 0 | 1000 | false |
| `Standard` | 10 | 1 | 3000 | false |
| `Hybrid` | 15 | 1 | 4000 | false |
| `Deep` | 20 | 2 | 6000 | true |
| `HybridRerank` | 10 | 1 | 4000 | true |

- `HybridRerank`：BM25 + 向量混合召回 + rerank 精排，**准确率最高**，日常首选
- `Hybrid`：混合召回不带 rerank，快于 HybridRerank
- `Standard`：纯向量
- `Quick`：纯向量，快速试探
- `Deep`：纯向量 20 条，探索性查询

**响应关键字段：**
```json
{
  "Query": "原始查询",
  "Mode": "HybridRerank",
  "Context": "拼装好的上下文文本，直接用这个回答",
  "Chunks": [
    {
      "RefId": "drill-down 用",
      "Title": "文件标题",
      "HeadingPath": "章节路径",
      "Content": "片段内容（Slim=true 时为空串）",
      "Score": 0.85,
      "BM25Score": 12.3,
      "EmbeddingScore": 0.58,
      "RerankScore": 0.85,
      "FilePath": "E:\\Content\\Down\\新建文件夹\\note.md",
      "Source": "源名称",
      "StartLine": 39,
      "EndLine": 56,
      "ObsidianLink": "[[note.md#L39-L56]]"
    }
  ],
  "Files": [{ "Id": "...", "Path": "...", "Title": "...", "ChunkCount": 3 }],
  "Stats": {
    "TotalMatches": 6,
    "DurationMs": 5342,
    "EstimatedTokens": 3210,
    "Phases": { "ModelLoadMs": 4405, "EmbedMs": 79, "Bm25Ms": 35, "VectorMs": 604, "HybridMs": 640, "RerankMs": 212 }
  },
  "RerankApplied": true,
  "RerankStats": { "CandidatesCount": 6, "RerankDurationMs": 1997, "ModelName": "bge-reranker-base", "AverageScore": 0.70 }
}
```

> ⚡ **优先用 `Context` 字段**回答，系统已拼装去重并附 `[[文件#L行号]]` 来源标注。
> `Slim: true` 时 `Chunks[*].Content` 是空串，但 `Context` 仍有全文 —— 想要内容就别开 Slim。

**耗时预期**：`Stats.Phases.ModelLoadMs` 常占 3-5 秒（模型冷加载）。模型卸载后首次查询必慢，属正常。

```bash
curl -s -X POST "http://localhost:7897/api/ai/query" \
  -H "Content-Type: application/json; charset=utf-8" \
  --data-binary @- << 'EOF'
{"Query": "Git 分支管理 branch 版本控制", "Mode": "HybridRerank", "TopK": 5, "Slim": true}
EOF
```

---

### 2. 深入展开上下文

chunk 看起来相关但被截断时，用 `RefId` 取完整上下文。

```
POST /api/ai/drill-down
```

| 参数 | 类型 | 说明 |
|------|------|------|
| Query | string | 原始查询词 |
| RefIds | string[] | 来自 /query 的 `Chunks[*].RefId` |
| ExpandContext | int | 扩展窗口，默认 2 |

**响应：** `{ "ExpandedChunks": [...], "FullContext": "..." }`

> ⚠️ `RefIds` 必须是 `/api/ai/query` 返回的 `RefId`（GUID）。
> 传 `FileId` 或 chunk 序号会得到 `{"ExpandedChunks":[],"FullContext":""}` 空结果，不报错。

```bash
curl -s -X POST "http://localhost:7897/api/ai/drill-down" \
  -H "Content-Type: application/json" \
  --data-binary @- << 'EOF'
{"Query": "Git 分支", "RefIds": ["81acc4c4-0d78-41e2-a1ef-b6bb2ed5f1a4"], "ExpandContext": 2}
EOF
```

---

### 3. BM25 关键词搜索

精确词匹配：变量名、错误码、API 名、专有名词。

```
GET /api/bm25index/search?query=关键词&topK=10
```

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

> ⚠️ 返回的 `Content` 是**分词后的空格分隔文本**（如 `referencerag 聊天 模型 工具 调用`），
> 不是原始 Markdown。适合定位，不适合直接引用给用户。
> 要原文请拿 `ChunkId` 对应的 `FilePath` 走 `/api/sources/file/lines`。
>
> `Score` 是 BM25 原始分，不同查询间不可比。

```bash
curl -s "http://localhost:7897/api/bm25index/search?query=IndexOutOfRangeException&topK=5"
```

辅助：`GET /api/bm25index/summary` 返回索引规模
（`TotalIndexedDocuments`、`TotalFiles`、`TotalChunks`、`AverageDocLength`）。

---

### 4. 图谱节点搜索

按笔记标题关键词查找节点，适合"有没有一篇关于 XXX 的笔记"。

```
GET /api/graph/search?q=标题关键词&limit=10
```

- `q` 必填，`limit` 范围 1-50，默认 10

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

> 注意 `Id` 是 `文件路径#标题` 复合串，且带 `#` 片段。
> 传给 `/api/graph/neighbors` 时要用 URL 编码。
> `Type` 当前实例只有 `document` 和 `heading` 两种。

```bash
curl -s --get "http://localhost:7897/api/graph/search" --data-urlencode "q=Docker 部署" --data-urlencode "limit=5"
```

---

### 5. 图谱关联关系（邻居节点）

```
GET /api/graph/neighbors?nodeId=<id>&depth=1&edgeTypes=wikilink
```

| 参数 | 说明 |
|------|------|
| nodeId | 必填，来自 graph/search 的 `Id` |
| depth | 1-3，默认 1（自动 Clamp） |
| edgeTypes | 逗号分隔，可选 `wikilink` / `embed` / `tag`，省略 = 全部 |

**响应：** `{ "RootId": "...", "Depth": 1, "Nodes": [...], "Edges": [...] }`

> 当前实例 `Edges` 恒为空（见「当前实例状态」）。调了也拿不到关联。

---

### 6. 图谱子图（批量邻居遍历）

已知多个节点 ID，一次取所有邻居关系，自动去重合并。

```
POST /api/graph/subgraph
```

**请求体：**
```json
{ "RootIds": ["E:/Content/Down/新建文件夹/a.md"], "Depth": 1 }
```

**响应：** `{ "RootId": "", "Depth": 1, "Nodes": [...], "Edges": [...] }`

> `RootIds` 传 `文件路径`（不带 `#标题`）能命中 `document` 节点；
> 传 `文件路径#标题` 复合串可能查不到节点。
> 同理，当前实例 `Edges` 恒为空。

辅助：`GET /api/graph/stats` 返回 `NodeCount`/`DocCount`/`TagCount`/`HeadingCount`/`EdgeCount`。
建边前 `EdgeCount` 为 0，可据此判断图谱是否可用。

---

### 7. 批量获取文件结构（目录级，不含正文）

已知一批文件路径，拿每个文件的章节目录（标题 + 行号范围），**不返回正文**。

```
POST /api/sources/files/info
```

**请求体：**
```json
{ "Paths": ["E:/Content/Down/新建文件夹/note.md"] }
```

- 单次最多 20 条路径
- 路径可用正斜杠或反斜杠

**响应：**
```json
{
  "Results": [
    {
      "Path": "...",
      "Title": "note",
      "Source": "新建文件夹",
      "TotalChunks": 5,
      "TotalLines": 48,
      "Sections": [
        { "Index": 0, "HeadingPath": "标题", "StartLine": 1, "EndLine": 10 },
        { "Index": 4, "HeadingPath": "标题", "StartLine": 39, "EndLine": 48 }
      ],
      "Error": null
    }
  ]
}
```

> `HeadingPath` 可能是空串（文件无 Markdown 标题，如 `.txt` 小说）。
> `Error` 非空表示该路径失败（未索引、越权等），其余条目不受影响。

---

### 8. 按行范围批量获取内容（含全文）

已知文件路径和行号范围，精准取出对应分段。

```
POST /api/sources/file/lines
```

**请求体：**
```json
{
  "Items": [
    { "Path": "E:/Content/Down/新建文件夹/note.md", "StartLine": 1, "EndLine": 10 },
    { "Path": "E:/Content/Down/新建文件夹/other.md", "StartLine": 0, "EndLine": 0 }
  ]
}
```

- `StartLine` / `EndLine` 均传 0 = 整个文件所有 chunk
- 行范围按 chunk 的 `StartLine`/`EndLine` 重叠匹配（`chunkStart <= EndLine && chunkEnd >= StartLine`）
- 单次最多 20 条
- 路径含 `..` 或为空 → 该条目 `Error: "路径无效"`，其余条目正常

**响应：**
```json
{
  "Results": [
    {
      "Path": "...",
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

> ✅ **这是拿原始正文的正确接口**。BM25 的 `Content` 是分词结果，这个 `Content` 是原文。

---

### 9. 文件列表

回答"知识库里有哪些笔记"。

```
# 源列表
GET /api/sources

# 指定源的文件（分页）
GET /api/sources/{name}/files?page=1&pageSize=50
```

源名含中文时需 URL 编码（`新建文件夹` → `%E6%96%B0%E5%BB%BA%E6%96%87%E4%BB%B6%E5%A4%B9`）。

**`/api/sources` 响应：**
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

**`/files` 响应：** `[{ "Path": "...", "ChunkCount": 5, "LastModified": "...", "Hash": "F17A2E55" }]`

> `files` 端点**不支持 `q` 或关键词过滤参数**，只有 `page`/`pageSize`，只能全量翻页。

---

### 10. 服务状态自检

调用报错或结果异常时，先自检。

```
GET /api/ai/status
```

**响应关键字段：**
```json
{
  "EmbeddingModel": "bge-m3",
  "EmbeddingVariant": "bge-m3-Q4_0.gguf",
  "EmbeddingDevice": "NVIDIA CUDA 12 (NVIDIA GeForce RTX 4060 Laptop GPU)",
  "EmbeddingIsGpu": true,
  "EmbeddingDimension": 1024,
  "RerankModel": "bge-reranker-base",
  "RerankEnabled": true,
  "IndexRequiresRebuild": false,
  "Bm25IndexedDocuments": 8985,
  "VectorIndexedChunks": 8985,
  "TotalFiles": 8
}
```

`IndexRequiresRebuild: true` 时检索结果可能过期，需重建索引。

---

## OpenAI 兼容端点（/v1/*）

供第三方客户端（Cherry Studio、NextChat、Chatbox、LangChain 等）调用。
**字段是 camelCase**，与 `/api/*` 的 PascalCase 不同。

### POST /v1/chat/completions

```
POST /v1/chat/completions
```

| 参数 | 说明 |
|------|------|
| model | 模型名。`reference-rag` = 启用知识库增强；其他名 = 直连当前聊天后端 |
| messages | `[{ "role": "user", "content": "..." }]` |
| stream | `true` = SSE 流式 |
| max_tokens | 生成上限 |

**关键行为：**

- **`model` 精确等于 `reference-rag`**（不区分大小写）才触发 RAG 模式，注入知识库上下文。
  名字含 "rag" 但不相等的模型名（如 `storage-rag-7b`）**不会**触发，直接透传。
- **`model` 缺省时按 `reference-rag` 处理**（即启用 RAG）。
- 实际使用的聊天后端由 `chat.activeBackend` 决定：`LocalGguf` 走本地 GGUF 模型，
  `OpenAiApi` 走外部端点。用 `GET /api/chat/capabilities` 查当前后端：
  `BackendType` 为 `LocalGguf` 即本地。
- 响应里的 `model` 字段回显**请求传入的值**，不是实际推理用的模型。
  要知道实际模型，查 `/api/chat/capabilities` 的 `ModelName`。

```bash
curl -s -X POST "http://localhost:7897/v1/chat/completions" \
  -H "Content-Type: application/json" \
  --data-binary @- << 'EOF'
{"model": "reference-rag", "stream": false, "messages": [{"role": "user", "content": "向量检索的原理"}]}
EOF
```

### POST /v1/embeddings

```json
{ "model": "bge-m3", "input": "文本" }
```

`input` 支持单字符串或字符串数组。响应 `data[*].embedding` 是 1024 维 float 数组。

可选 `encoding_mode`：`symmetric`（默认）/ `query` / `document`。

### POST /v1/rerank 和 POST /v1/rerank/single

```json
{ "model": "bge-reranker-base", "query": "Git 分支管理", "documents": ["文本1", "文本2"] }
```

`documents` 支持字符串数组或 `[{ "text": "..." }]` 对象数组（Cohere/Jina 格式）。
单次最多 256 条，单条最长 32000 字符。`top_n` 可限制返回条数，`return_documents` 控制是否回显原文。

响应：`{ "object": "list", "model": "...", "results": [{ "index": 0, "relevance_score": 0.85, "document": "..." }], "usage": {...} }`

**`/v1/rerank/single`** 是单文档简化版：
```json
{ "model": "bge-reranker-base", "query": "...", "document": "..." }
```
响应 `{ "object": "rerank_result", "relevance_score": 0.85, "usage": {...} }`

### GET /v1/models

列出可用模型 ID：`reference-rag`（RAG 助手）、当前 chat 模型、`bge-m3`、`bge-reranker-base`。
全部 `owned_by` 为 `reference-rag`。

---

## 多步查询策略

### 场景 A：通用查询（最常用）
1. 扩展查询词
2. `POST /api/ai/query`（`Mode: HybridRerank`，显式 `TopK`，`Slim: true`）
3. 用响应 `Context` 字段回答

### 场景 B：结果不够详细
1. `POST /api/ai/query` → 找相关 chunk
2. 取 `Chunks[*].RefId` → `POST /api/ai/drill-down`
3. 用 `FullContext` 回答

### 场景 C：需要某篇原文的特定章节（最省 token）
1. 搜索 → 拿 `FilePath`
2. `POST /api/sources/files/info` → 拿章节目录（无正文）
3. 按 `HeadingPath` + 行号挑相关章节
4. `POST /api/sources/file/lines` → 只取目标行范围
5. 用原始正文回答

### 场景 D：精确术语 + 语义组合
1. `GET /api/bm25index/search?query=精确术语` → 锁定 chunk
2. `POST /api/ai/query`（`Deep`）→ 补语义相关内容
3. 合并回答。注意 BM25 的 `Content` 是分词结果，引用原文走 `file/lines`

### 场景 E：读取指定文件完整内容
1. 从搜索结果或文件列表拿 `FilePath`
2. `POST /api/sources/file/lines` 传 `StartLine: 0, EndLine: 0`
3. 拼接 `Chunks[*].Content`

### 场景 F：列出知识库内容
1. `GET /api/sources` → 拿源名和规模
2. `GET /api/sources/{name}/files?page=1&pageSize=50` → 翻页列文件

---

## 故障排查

| 现象 | 原因 | 处理 |
|------|------|------|
| 空白 500，`Content-Length: 0` | 请求体非 UTF-8 编码 | 用 heredoc + `--data-binary` 或 PowerShell UTF-8 字节 |
| 400 `must be valid UTF-8 encoded text` | 同上 | 同上 |
| 400 `q field is required` | `/api/graph/search` 缺 `q` | 加 `q` 参数 |
| 400 `nodeId field is required` | `/api/graph/node` 缺 `nodeId` | 该接口参数名是 `nodeId`，不是 `id` |
| 404 `节点不存在: <乱码路径>` | `nodeId` 含中文未 URL 编码 | 用 `curl --get --data-urlencode` |
| `Edges` 恒为空 | 数据源无 `[[双链]]` | 图谱未建边，改用 `/api/ai/query` |
| 召回小说正文 | 索引含非技术文件 | 预期噪声，用更精确的技术词 |
| `FullContext` 为空 | `RefIds` 传错（传了 FileId 或序号） | 必须用 `/api/ai/query` 返回的 `RefId` GUID |
| 首次查询 3-5 秒 | 模型冷加载 | 正常，看 `Stats.Phases.ModelLoadMs` |
| 小写 `query`/`topK` 无效 | 字段名大小写 | `/api/*` 用 PascalCase |

---

## 支持的模型

**Embedding（本地 GGUF/ONNX）**：bge-small-zh-v1.5、bge-base-zh-v1.5、bge-large-zh-v1.5、bge-m3
**Rerank（本地）**：bge-reranker-base、bge-reranker-large
**OpenAI 兼容 API**：支持任意 Ollama / vLLM / Xinference / LM Studio / TEI 等兼容模型，在设置页切换推理模式后配置。

当前实例：嵌入 `bge-m3`（1024 维）+ 重排 `bge-reranker-base`，均跑在 CUDA / RTX 4060 Laptop。

## Web UI / Swagger

- Swagger: `http://localhost:7897/swagger`（DesktopHost 是 301 重定向，跟随后缀）
- Web UI: `http://localhost:7897` **当前返回 404** —— DesktopHost 的前端由 `app://` 自定义协议提供，
  不经 HTTP。要看界面需启动 DesktopHost 窗口，或改用 WebHost。

> Swagger 只覆盖部分端点。`/api/sources/file/lines`、`/api/sources/files/info`、
> `/api/bm25index/search` 未出现在 Swagger 里，不代表不存在 —— 本文档已实测。

## GitHub

https://github.com/csvkse/ReferenceRAG
