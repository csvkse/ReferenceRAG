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

如需自定义，在 `~/.agents/.env` 中设置 `OBSIDIAN_RAG_API_URL`。

---

## 触发规则

### 强制触发
| 格式 | 示例 |
|------|------|
| `rag:关键词` | `rag:Git 分支管理` |
| `/rag 关键词` | `/rag TypeScript 类型` |

### 组合触发（领域词 + 动作词）
**领域词**：知识库、笔记、vault、obsidian、文档、文库  
**动作词**：搜、查、找、检索、查询、翻一下、看看

### 意图触发
- `笔记里有没有 xxx` / `帮我搜一下笔记` / `在知识库中查找`

### 不触发
- 纯动作词无领域词：`查询 Git`、`搜索配置` ❌  
- 实时信息：天气、新闻、股票 ❌

---

## Agent 决策树

拿到用户意图后，按以下顺序选择 API：

```
用户意图
├── 通用语义查询（绝大多数情况）
│   └── POST /api/ai/query  [mode: HybridRerank]
│       ├── 结果相关但内容不够 → POST /api/ai/drill-down
│       ├── 需要了解该笔记的关联 → GET /api/graph/neighbors/{nodeId}
│       ├── 先看文件结构再决定读哪段（可批量）→ POST /api/sources/files/info
│       └── 按行范围取内容 / 读全文（可批量）→ POST /api/sources/file/lines
│
├── 精确关键词 / 代码 / 错误码 / API 名称
│   └── GET /api/bm25index/search
│
├── 按笔记标题查找某篇具体文档
│   └── GET /api/graph/search?q=标题关键词
│       ├── 单节点探索关联 → GET /api/graph/neighbors?nodeId=<id>
│       ├── 多节点批量探索关联 → POST /api/graph/subgraph
│       └── 按行范围取内容 / 读全文（可批量）→ POST /api/sources/file/lines
│
└── 列出知识库有哪些文件
    └── GET /api/sources  →  GET /api/sources/{name}/files
```

**查询词扩展**：调用前将用户输入扩展为多个相关词（中英文、同义词、相关概念）以提升召回。  
示例：`Git 分支管理` → `Git 分支管理 branch 版本控制 分支策略 git flow`

---

## API 参考

### 1. 通用语义查询（主力接口）

```
POST /api/ai/query
```

| 参数 | 类型 | 说明 |
|------|------|------|
| query | string | 查询词（必填，建议先扩展） |
| mode | string | `HybridRerank`（默认推荐）/ `Quick` / `Deep` |
| topK | int | 返回数量，默认 10 |
| sources | string[] | 限定源，省略 = 全部 |
| filters.folders | string[] | 限定文件夹路径 |

**模式说明：**
- `HybridRerank`：BM25 + 向量混合召回，rerank 精排，**准确率最高**，日常首选
- `Quick`：纯向量，返回 3 条，适合快速试探
- `Deep`：纯向量，返回 20 条，上下文更大，适合探索性查询

**响应关键字段：**
```json
{
  "chunks": [
    {
      "refId": "用于 drill-down 的引用 ID",
      "title": "文件标题",
      "headingPath": "所在章节路径，如 ## 安装 > ### 配置",
      "content": "片段内容",
      "score": 0.95,
      "bm25Score": 12.3,
      "source": "源名称",
      "obsidianLink": "可直接在 Obsidian 中打开的链接"
    }
  ],
  "context": "所有结果拼装的上下文文本（可直接用于回答）",
  "stats": { "totalMatches": 5, "durationMs": 120 }
}
```

**调用示例（Windows Git Bash）：**
```bash
# ⚠️ Git Bash 中文必须用 heredoc + --data-binary，否则乱码
curl -s -X POST "http://localhost:7897/api/ai/query" \
  -H "Content-Type: application/json; charset=utf-8" \
  --data-binary @- << 'EOF'
{"query": "Git 分支管理 branch 版本控制", "mode": "HybridRerank", "topK": 10}
EOF
```

```bash
# PowerShell（无需特殊处理）
curl -X POST "http://localhost:7897/api/ai/query" \
  -H "Content-Type: application/json" \
  -d '{"query": "Git 分支管理", "mode": "HybridRerank"}'
```

---

### 2. 深入展开上下文

当某个 chunk 看起来相关但内容被截断时，用 `refId` 取完整上下文。

```
POST /api/ai/drill-down
```

| 参数 | 类型 | 说明 |
|------|------|------|
| query | string | 原始查询词 |
| refIds | string[] | 来自 /query 结果的 chunk.refId |
| expandContext | int | 扩展窗口大小，默认 2 |

**响应关键字段：**
```json
{
  "fullContext": "展开后的完整上下文文本",
  "expandedChunks": [{ "content": "...", "score": 0.9 }]
}
```

**示例：**
```bash
curl -s -X POST "http://localhost:7897/api/ai/drill-down" \
  -H "Content-Type: application/json" \
  --data-binary @- << 'EOF'
{"query": "Git 分支", "refIds": ["chunk_abc123"], "expandContext": 2}
EOF
```

---

### 3. BM25 关键词搜索

适合精确词匹配：变量名、错误码、API 名、专有名词。

```
GET /api/bm25index/search?query=关键词&topK=10
```

**响应关键字段：**
```json
{
  "results": [
    { "chunkId": "...", "content": "...", "score": 15.2, "rank": 1 }
  ],
  "totalResults": 5
}
```

**示例：**
```bash
curl -s "http://localhost:7897/api/bm25index/search?query=IndexOutOfRangeException&topK=5"
```

---

### 4. 图谱节点搜索

按笔记标题关键词查找节点，适合"有没有一篇关于 XXX 的笔记"。

```
GET /api/graph/search?q=关键词&limit=10
```

**响应关键字段：**
```json
[
  {
    "id": "节点ID（用于 neighbors 查询）",
    "title": "笔记标题",
    "type": "document | tag | heading | external",
    "chunkIds": ["关联的 chunk ID 列表"]
  }
]
```

**示例：**
```bash
curl -s "http://localhost:7897/api/graph/search?q=Docker%20部署&limit=5"
```

---

### 5. 图谱关联关系（邻居节点）

从一个节点出发，探索它链接到哪些笔记、被哪些笔记引用。

```
GET /api/graph/neighbors?nodeId=<id>&depth=1&edgeTypes=wikilink
```

| 参数 | 说明 |
|------|------|
| nodeId | 来自 graph/search 结果的 id（query 参数，自动处理编码） |
| depth | 遍历深度 1-3，默认 1 |
| edgeTypes | 逗号分隔，省略 = 全部；可选：`wikilink`, `tag`, `heading` |

**响应关键字段：**
```json
{
  "nodes": [{ "id": "...", "title": "关联笔记标题", "type": "document" }],
  "edges": [{ "fromId": "...", "toId": "...", "type": "wikilink" }]
}
```

**示例：**
```bash
curl -s --get "http://localhost:7897/api/graph/neighbors" \
  --data-urlencode "nodeId=Projects/Docker/安装教程" \
  --data-urlencode "depth=1"
```

---

### 6. 图谱子图（批量邻居遍历）

已知多个节点 ID，一次获取所有节点的邻居关系，结果自动去重合并。适合"探索多篇相关笔记的关联网络"。

```
POST /api/graph/subgraph
```

**请求体：**
```json
{
  "rootIds": ["Projects/Docker/安装教程.md", "Projects/Docker/配置.md"],
  "depth": 1
}
```

- `rootIds`：节点 ID 列表（来自 graph/search 结果的 `id` 字段）
- `depth`：遍历深度 1-3，默认 1

**响应关键字段：**
```json
{
  "nodes": [
    { "id": "...", "title": "关联笔记标题", "type": "document", "chunkIds": ["..."] }
  ],
  "edges": [
    { "fromId": "...", "toId": "...", "type": "wikilink" }
  ]
}
```

**示例：**
```bash
curl -s -X POST "http://localhost:7897/api/graph/subgraph" \
  -H "Content-Type: application/json" \
  --data-binary @- << 'EOF'
{"rootIds": ["Projects/Docker/安装教程.md", "Projects/Git/分支管理.md"], "depth": 1}
EOF
```

---

### 7. 批量获取文件结构（目录级，不含正文）

已知一批文件路径，先拿到每个文件的章节目录（标题 + 行号范围），**不返回正文内容**，用于规划后续精准读取。

```
POST /api/sources/files/info
```

**请求体：**
```json
{
  "paths": [
    "C:/Vault/Docker/安装教程.md",
    "C:/Vault/Git/分支管理.md"
  ]
}
```

- 单次最多 20 条路径

**响应关键字段：**
```json
{
  "results": [
    {
      "path": "C:/Vault/Docker/安装教程.md",
      "title": "Docker 安装教程",
      "source": "Obsidian",
      "totalChunks": 8,
      "totalLines": 120,
      "sections": [
        { "index": 0, "headingPath": "## 安装",           "startLine": 1,  "endLine": 40 },
        { "index": 1, "headingPath": "## 安装 > ### Windows", "startLine": 41, "endLine": 80 },
        { "index": 2, "headingPath": "## 配置",           "startLine": 81, "endLine": 120 }
      ],
      "error": null
    }
  ]
}
```

**示例：**
```bash
curl -s -X POST "http://localhost:7897/api/sources/files/info" \
  -H "Content-Type: application/json" \
  --data-binary @- << 'EOF'
{"paths": ["C:/Vault/Docker/安装教程.md", "C:/Vault/Git/分支管理.md"]}
EOF
```

---

### 9. 按行范围批量获取内容（含全文）

已知文件路径和行号范围（如来自搜索结果的 `startLine`/`endLine`），精准取出对应的分段。支持一次请求多个文件、多个范围。

```
POST /api/sources/file/lines
```

**请求体：**
```json
{
  "items": [
    { "path": "C:/Vault/Docker/安装教程.md", "startLine": 10, "endLine": 50 },
    { "path": "C:/Vault/Git/分支管理.md",   "startLine": 0,  "endLine": 0  }
  ]
}
```

- `startLine` / `endLine` 均为 0 时等价于返回整个文件（同 `/file/chunks`）
- 行范围按 chunk 的 `StartLine`/`EndLine` 做重叠匹配（`chunkStart <= endLine && chunkEnd >= startLine`）

**响应关键字段：**
```json
{
  "results": [
    {
      "path": "C:/Vault/Docker/安装教程.md",
      "title": "Docker 安装教程",
      "source": "Obsidian",
      "requestedRange": { "startLine": 10, "endLine": 50 },
      "chunks": [
        { "index": 1, "headingPath": "## 安装 > ### Windows", "startLine": 8, "endLine": 52, "content": "..." }
      ],
      "error": null
    },
    {
      "path": "C:/Vault/Git/分支管理.md",
      "chunks": [...],
      "error": null
    }
  ]
}
```

- `error` 非空时表示该条目失败（文件未索引、路径越权等），其余条目不受影响

**示例（Git Bash）：**
```bash
curl -s -X POST "http://localhost:7897/api/sources/file/lines" \
  -H "Content-Type: application/json" \
  --data-binary @- << 'EOF'
{
  "items": [
    { "path": "C:/Vault/Docker/安装教程.md", "startLine": 10, "endLine": 80 }
  ]
}
EOF
```

---

### 10. 文件列表

列出某个源下的所有文件，回答"知识库里有哪些笔记"。

```
# 先获取源列表
GET /api/sources

# 再获取指定源的文件
GET /api/sources/{name}/files?page=1&pageSize=50
```

**示例：**
```bash
# 列出所有源
curl -s "http://localhost:7897/api/sources"

# 列出源 "Obsidian" 下的文件
curl -s "http://localhost:7897/api/sources/Obsidian/files?pageSize=50"
```

---

## 多步查询策略

### 场景 A：通用查询（最常用）
1. 扩展查询词
2. `POST /api/ai/query` (HybridRerank)
3. 直接使用响应中的 `context` 字段回答用户

### 场景 B：结果不够详细
1. `POST /api/ai/query` → 找到相关 chunk
2. 取 `chunk.refId` → `POST /api/ai/drill-down`
3. 用 `fullContext` 回答

### 场景 C：探索某篇笔记的关联网络
1. `GET /api/graph/search?q=笔记标题` → 获取一批 nodeId
2. 单节点：`GET /api/graph/neighbors?nodeId=<id>&depth=2`
   多节点：`POST /api/graph/subgraph` 传入 `rootIds` 批量获取
3. 对感兴趣的节点取 `chunkIds` → `POST /api/ai/query` 补充内容

### 场景 D：精确术语 + 语义组合
1. `GET /api/bm25index/search?query=精确术语` → 锁定相关 chunk
2. `POST /api/ai/query` (Deep) → 补充语义相关内容
3. 合并两路结果回答

### 场景 E：读取指定文件完整内容
1. 通过搜索结果或文件列表拿到 `filePath`（绝对路径）
2. `POST /api/sources/file/lines` 传 `{"items":[{"path":"<filePath>","startLine":0,"endLine":0}]}`
3. 拼接 `chunks[].content` 呈现完整文档

### 场景 F：多文件"先看目录再精读"（最省 token）
1. 搜索 → 得到多个相关 `filePath`
2. `POST /api/sources/files/info` 批量拿所有文件的章节目录（无内容）
3. 根据 `headingPath` + 行号判断哪些章节与问题相关
4. `POST /api/sources/file/lines` 只取目标行范围的内容
5. 用取到的内容回答用户

---

## 支持的模型

**Embedding**：bge-small-zh-v1.5、bge-base-zh-v1.5、bge-large-zh-v1.5、bge-m3  
**Rerank**：bge-reranker-base、bge-reranker-large

## Web UI / Swagger

- Web UI: `http://localhost:7897`
- Swagger: `http://localhost:7897/swagger`

## GitHub

https://github.com/hlrlive/ObsidianRAG
