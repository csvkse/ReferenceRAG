---
name: ReferenceRAG
description: "本地知识库语义检索服务。从 Obsidian 笔记库检索技术教程、配置说明、最佳实践等内容。触发规则：(1)强制触发：rag:关键词、/rag 关键词 (2)组合触发：领域词(知识库/笔记/vault/obsidian/文档) + 动作词(搜/查/找/检索/查询) (3)意图触发：笔记里有没有、帮我搜一下笔记、在知识库中查找。NOT for: 天气、新闻、股票等实时信息。"
allowed-tools:
  - Read
  - Bash
---

# ReferenceRAG 知识库检索

本地知识库检索服务，支持 Obsidian 笔记和 Markdown 文档的语义搜索、关键词搜索、知识图谱查询。

```
BASE_URL=http://localhost:7897
```

端口由 `appsettings.json` 的 `ReferenceRAG:Service:port` 决定，非 7897 时替换下文所有 URL。
自定义地址可写入 `~/.agents/.env` 的 `OBSIDIAN_RAG_API_URL`。

**认证**：当前实例未配置 API Key，所有端点**无需认证头**。
若服务端配置了 Key，对 `/api/*`、`/v1/*`、`/indexHub` 需附 `X-API-Key: <key>` 或 `Authorization: Bearer <key>`。

---

## ⚠️ 调用编码（必读）

请求体中的中文字符串**必须按 UTF-8 字节发出**，否则服务端报错：

- 旧版本：`500` 且 `Content-Length: 0`（空白 500，无任何信息）
- 新版本：`400` + `must be valid UTF-8 encoded text`

Git Bash 的 `curl -d '{"Query":"中文"}'` 会把中文按控制台代码页（GBK）发出，必然触发。

```bash
# Git Bash 正确写法：heredoc + --data-binary
curl -s -X POST "http://localhost:7897/api/ai/query" \
  -H "Content-Type: application/json; charset=utf-8" \
  --data-binary @- << 'EOF'
{"Query": "Git 分支管理 branch 版本控制", "Mode": "HybridRerank", "TopK": 5}
EOF
```

```powershell
# PowerShell 正确写法：显式 UTF-8 字节
$body = '{"Query": "Git 分支管理", "Mode": "HybridRerank", "TopK": 5}'
$bytes = [System.Text.Encoding]::UTF8.GetBytes($body)
Invoke-WebRequest -Uri "http://localhost:7897/api/ai/query" `
  -Method Post -Body $bytes -ContentType "application/json; charset=utf-8"
```

纯 ASCII 查询词无此问题。

---

## ⚠️ 字段大小写

服务端 `PropertyNamingPolicy = null`，**`/api/*` 的请求和响应都是 PascalCase**：

```json
{ "Query": "...", "Mode": "HybridRerank", "TopK": 5, "Slim": true }
```

响应同样是 `Chunks`、`RefId`、`FilePath`、`BM25Score`。

**例外**：`/v1/*` OpenAI 兼容端点是 camelCase（`query`、`documents`、`input`）。
详见 [references/openai-compat.md](references/openai-compat.md)。

模型绑定不区分大小写，小写 `query` 通常也能工作，但 `Slim`/`EnableRerank` 等 bool 字段建议按 PascalCase 传，避免歧义。

---

## 当前实例状态（2026-10-22 实测）

| 项 | 值 |
|---|---|
| 数据源 | `新建文件夹` → `E:\Content\Down\新建文件夹` |
| 文件数 / 分块数 | 8 文件 / 8985 分块 |
| 嵌入模型 | `bge-m3`（Q4_0 GGUF，1024 维，CUDA / RTX 4060 Laptop） |
| 重排模型 | `bge-reranker-base`（Q4_K_M GGUF，同 GPU） |
| 图谱 | 21 节点、**0 边** |
| 最后索引 | 2026-10-20 21:24 |

两个影响使用的注意点：

1. **图谱不可用**：数据源无 `[[双链]]`，`.txt` 不解析链接，`EdgeCount` 恒为 0。
   `/api/graph/neighbors`、`/api/graph/subgraph` 会返回空结果，**不要依赖图谱做关联探索**，
   直接用 `/api/ai/query` 检索。可用 `GET /api/graph/stats` 确认。
2. **索引混杂小说**：源目录同时含技术文档和中文小说（`操虫师.txt`、`剑来.txt`）。
   检索无技术内容的词会召回小说正文，属预期噪声，非故障。

---

## Agent 决策树

```
用户意图
├── 通用语义查询（绝大多数情况）
│   └── POST /api/ai/query  [Mode: HybridRerank]
│       ├── 结果相关但内容被截断 → POST /api/ai/drill-down   （见 api-reference.md）
│       ├── 需要某文件特定章节 → files/info 拿目录 → file/lines 取行范围
│       └── 已知文件路径要全文 → POST /api/sources/file/lines  (0,0)
│
├── 精确关键词 / 错误码 / API 名 / 变量名
│   └── GET /api/bm25index/search
│
├── 按笔记标题找某篇具体文档
│   └── GET /api/graph/search?q=标题关键词   （见 api-reference.md）
│
└── 列出知识库有哪些文件
    └── GET /api/sources  →  GET /api/sources/{name}/files
```

图谱关联接口（`neighbors`/`subgraph`）当前实例边数为 0，未列入主路径。

---

## 查询前处理（调用任何搜索 API 前必须执行）

调用 `/api/ai/query` 或 `/api/bm25index/search` 前，**必须先把用户输入扩展为丰富查询串**：

1. 提取核心名词和技术术语
2. 补充中英文同义词、缩写、别名
3. 补充上位/下位概念、相关操作动词
4. 拼接为单个字符串（空格分隔，无需引号）

| 用户输入 | 扩展后查询 |
|----------|-----------|
| `docker 怎么配置网络` | `docker 网络配置 network docker-compose 容器网络 bridge overlay port mapping 端口映射` |
| `git 撤销提交` | `git 撤销 revert reset undo 回滚 commit 提交 撤回 版本控制` |
| `Python 读取配置文件` | `Python config 配置文件 读取 解析 yaml json ini configparser settings` |
| `如何做好需求分析` | `需求分析 需求文档 PRD 用户故事 功能规格 业务需求 需求评审 需求收集` |

> 简单 2-3 词查询不扩展，召回率明显下降。

---

## 核心接口

### 1. 语义查询（主力，90% 场景）

```
POST /api/ai/query
```

```bash
curl -s -X POST "http://localhost:7897/api/ai/query" \
  -H "Content-Type: application/json; charset=utf-8" \
  --data-binary @- << 'EOF'
{"Query": "Git 分支管理 branch 版本控制", "Mode": "HybridRerank", "TopK": 5, "Slim": true}
EOF
```

**响应直接用 `Context` 字段回答**，系统已拼装去重并附 `[[文件#L行号]]` 来源标注。

`Slim: true` 时 `Chunks[*].Content` 是空串，但 `Context` 仍有全文 —— 想要内容就别开 Slim。

`Mode` 取值 `HybridRerank`（推荐）/ `Hybrid` / `Standard` / `Quick` / `Deep`，
未显式传 `TopK` 时用模式默认值（`HybridRerank`=10、`Deep`=20）。
完整参数表与五种模式的默认值见 [references/api-reference.md](references/api-reference.md)。

**耗时预期**：`Stats.Phases.ModelLoadMs` 常占 3-5 秒（模型冷加载），首次查询必慢，属正常。

### 2. 取文件原始正文

```
POST /api/sources/file/lines
```

```bash
curl -s -X POST "http://localhost:7897/api/sources/file/lines" \
  -H "Content-Type: application/json; charset=utf-8" \
  --data-binary @- << 'EOF'
{"Items": [{"Path": "E:/Content/Down/新建文件夹/note.md", "StartLine": 1, "EndLine": 10}]}
EOF
```

- `StartLine`/`EndLine` 均传 `0` = 整个文件所有 chunk
- 单次最多 20 条；路径含 `..` 或为空 → 该条目 `Error: "路径无效"`，其余正常
- 响应 `Chunks[*].Content` 是**原始 Markdown 正文**

> ✅ **这是拿原文的正确接口。** BM25 接口返回的 `Content` 是分词后的空格分隔文本，不能直接引用。

### 3. 精确关键词搜索

```
GET /api/bm25index/search?query=关键词&topK=10
```

适合错误码、API 名、变量名、专有名词。

```bash
curl -s "http://localhost:7897/api/bm25index/search?query=IndexOutOfRangeException&topK=5"
```

响应 `Results[*]` 含 `ChunkId`、`Content`（**分词结果**）、`Score`（BM25 原始分，查询间不可比）、`Rank`。
要原文需拿 `ChunkId` 对应 `FilePath` 走 `file/lines`。

### 4. 列出数据源与文件

```bash
# 源列表（含文件数、分块数、最后索引时间）
curl -s "http://localhost:7897/api/sources"

# 指定源的文件，分页。源名含中文需 URL 编码（新建文件夹 → %E6%96%B0%E5%BB%BA%E6%96%87%E4%BB%B6%E5%A4%B9）
curl -s "http://localhost:7897/api/sources/%E6%96%B0%E5%BB%BA%E6%96%87%E4%BB%B6%E5%A4%B9/files?page=1&pageSize=50"
```

`/files` 只有 `page`/`pageSize` 参数，**不支持关键词过滤**，只能全量翻页。

---

## 多步查询策略

### A. 通用查询（最常用）
1. 扩展查询词 → 2. `POST /api/ai/query`（`HybridRerank`，显式 `TopK`，`Slim: true`）→ 3. 用 `Context` 回答

### B. 需要某篇原文的特定章节（最省 token）
1. 搜索 → 拿 `FilePath` → 2. `POST /api/sources/files/info` 拿章节目录（无正文）→ 3. 按 `HeadingPath` + 行号挑章节 → 4. `POST /api/sources/file/lines` 只取目标行范围 → 5. 用原始正文回答

### C. 读取指定文件完整内容
1. 拿 `FilePath` → 2. `POST /api/sources/file/lines` 传 `StartLine: 0, EndLine: 0` → 3. 拼接 `Chunks[*].Content`

> 先用 `files/info` 看结构再决定是否全文。小说类文件可达 37 万行、200+ chunk，全文会撑爆上下文。

### D. 结果不够详细
1. `POST /api/ai/query` → 2. 取 `Chunks[*].RefId` → 3. `POST /api/ai/drill-down` → 4. 用 `FullContext` 回答

> `RefIds` 必须是 `/api/ai/query` 返回的 `RefId`（GUID）。传 `FileId` 或序号会得到空结果且不报错。

### E. 精确术语 + 语义组合
1. `GET /api/bm25index/search` 锁定 chunk → 2. `POST /api/ai/query`（`Deep`）补语义内容 → 3. 合并回答

---

## 深入参考

| 需求 | 文档 |
|------|------|
| 全部 `/api/*` 端点完整参数表、模式默认值、响应字段 | [references/api-reference.md](references/api-reference.md) |
| `/v1/*` OpenAI 兼容端点（chat / embeddings / rerank / models） | [references/openai-compat.md](references/openai-compat.md) |
| 故障排查表、字段名映射、易错点详解 | [references/troubleshooting.md](references/troubleshooting.md) |
| 服务状态自检、索引规模查询 | [references/api-reference.md](references/api-reference.md#10-服务状态自检) |

**调用报错或结果异常时，先看 [troubleshooting.md](references/troubleshooting.md)。**

## GitHub

https://github.com/csvkse/ReferenceRAG
