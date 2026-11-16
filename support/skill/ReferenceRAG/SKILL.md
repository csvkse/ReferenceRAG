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

## 服务自检（不要写死具体数字）

实例的数据量、模型、图谱规模都会变化，**不要依赖文档里的历史快照**。需要时现查：

```bash
# 知识库清单（含 id / 名称 / 启用状态 / 绑定模型规格）
curl -s "http://localhost:7897/api/knowledge-bases"

# 某库的连接器与真实文件数/分块数
curl -s "http://localhost:7897/api/knowledge-bases/{id}/connectors"

# 索引与模型状态
curl -s "http://localhost:7897/api/index/models/current"
curl -s "http://localhost:7897/api/graph/stats"
```

两个通用注意点：

1. **图谱可能为空**：只有解析出 `[[双链]]` 的 Markdown 才产生边。`GET /api/graph/stats` 的
   `EdgeCount` 为 0 时不要依赖 `neighbors`/`subgraph` 做关联探索。
2. **同库混装语料会有噪声**：一个库同时挂技术文档与小说时，模糊词会召回无关正文，属预期行为；
   建议按主题拆分知识库，或用 `UseLexicalTokenizer` 走精确词法。

---

## Agent 决策树

v2 起检索单元是**知识库**（每库独立 `kb.db`）。**先选库，再检索**：

```
用户意图
├── 不确定有哪些库 / 用户提到"某个知识库"
│   └── GET /api/knowledge-bases              ← 先取 id + name 清单
│
├── 通用语义查询（绝大多数情况）
│   └── POST /api/knowledge-base-search
│         body: {"Query":"...", "KnowledgeBaseIds":["<kbId>"], "TopK":8}
│       ├── 内容被截断 → 同上提高 TopK，或读 chunks 端点取全文
│       └── 跨库对比 → 传多个 KnowledgeBaseIds（RRF 融合）
│
├── 精确关键词 / 错误码 / API 名 / 变量名
│   └── 同上，但 body 加 "UseLexicalTokenizer": true（免向量词法）
│
├── 按笔记标题找某篇具体文档
│   └── GET /api/graph/search?q=标题关键词
│
└── 列出某库有哪些文件
    └── GET /api/knowledge-bases/{id}/documents?page=1&pageSize=50
```

**禁止**把 `/api/ai/query` 当唯一入口：它仍合并旧单库召回，在 v2 架构下可能返回过期内容
（旧库已停止写入）。多库场景一律走 `/api/knowledge-base-search`。

### 文档与切片

| 需求 | 端点 |
| :--- | :--- |
| 某库的文档列表（分页 / 关键词） | `GET /api/knowledge-bases/{id}/documents?page=&pageSize=&search=` |
| 某文档的全部切片 | `GET /api/knowledge-bases/{id}/chunks?documentId=&page=&pageSize=` |
| 单个切片全文 | `GET /api/knowledge-bases/{id}/chunks/{chunkId}` |
| 目录树与连接器路由 | `GET /api/knowledge-bases/{id}/paths` |

### 配置漂移与重嵌入

- `GET /api/knowledge-bases/{id}/drift` — `Aligned` / `Customized` / `Drifted`。
  `Drifted` 表示该库固化的模型与全局默认不同；模型身份经规范化比较，展示名差异不会误报。
- `POST /api/knowledge-bases/{id}/reindex` — 作业化蓝绿重构，`{"targetEmbeddingSpecId":"..."}`，
  返回 202 后用 `GET /api/jobs/{jobId}` 轮询。
- 重新分段用 `POST /api/knowledge-bases/{id}/rechunk`。

---

## 查询前处理（调用任何搜索 API 前必须执行）

调用 `/api/knowledge-base-search` 前，**必须先把用户输入扩展为丰富查询串**：

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

### 1. 跨库语义检索（主力，90% 场景）

```
POST /api/knowledge-base-search
```

```bash
curl -s -X POST "http://localhost:7897/api/knowledge-base-search" \
  -H "Content-Type: application/json; charset=utf-8" \
  --data-binary @- << 'EOF'
{"Query": "Git 分支管理 branch 版本控制", "KnowledgeBaseIds": ["<kbId>"], "TopK": 8}
EOF
```

响应字段（PascalCase）：
- `Items[*]`：`ChunkId`、`DocumentId`、`KnowledgeBaseId`、`SourceKey`、`Text`、`Score`、
  `ScoreKind`（RRF / Cosine / BM25）、`StartLine`、`EndLine`、`HeadingPath`
- `SuccessfulKnowledgeBaseIds` / `FailedKnowledgeBaseIds`：扇出结果；`FailurePolicy: AllowPartial`
  时部分库失败也会返回其余结果
- `UsedRecallFloor`：为 `true` 表示查询词在本语料中不存在，只给了宽松兜底，**必须如实告知用户**

`KnowledgeBaseIds` 留空 = 检索所有已启用库（最多 8 库扇出）。
要原文直接用 `Text`（已是原始正文，不是分词结果）。

**耗时预期**：首查可能因模型冷加载慢 3-5 秒；`IndexDegraded: true` 表示词法索引仍在后台补齐。

### 2. 精确关键词（免向量词法）

同上端点，body 加 `"UseLexicalTokenizer": true`。适合错误码、API 名、变量名、专有名词；
不依赖嵌入模型，延迟显著更低。

### 3. 取知识库文档与切片

```bash
# 文档列表（search 可按文件名/路径过滤）
curl -s "http://localhost:7897/api/knowledge-bases/{id}/documents?page=1&pageSize=50&search=关键词"

# 某文档的全部切片
curl -s "http://localhost:7897/api/knowledge-bases/{id}/chunks?documentId={docId}&page=1&pageSize=50"

# 单切片全文
curl -s "http://localhost:7897/api/knowledge-bases/{id}/chunks/{chunkId}"
```

### 4. 知识库清单与状态

```bash
curl -s "http://localhost:7897/api/knowledge-bases"                 # id / 名称 / 启用 / 绑定模型
curl -s "http://localhost:7897/api/knowledge-bases/{id}/connectors"  # 真实文件数 / 分块数
curl -s "http://localhost:7897/api/knowledge-bases/{id}/drift"       # Aligned / Customized / Drifted
```

> 旧接口 `GET /api/bm25index/search`、`POST /api/sources/file/lines` 仍可用，但只覆盖
> **未迁移的历史单库数据**；v2 入库内容一律通过知识库端点读取。

---

## 多步查询策略

### A. 通用查询（最常用）
1. 扩展查询词 → 2. `POST /api/knowledge-base-search`（显式 `KnowledgeBaseIds` + `TopK`）→ 3. 用 `Items[*].Text` 回答

### B. 需要某文档特定章节（最省 token）
1. 检索 → 拿 `DocumentId` + `KnowledgeBaseId` → 2. `GET .../chunks?documentId=` 拿切片与 `HeadingPath`/行号 → 3. 用 `Text` 回答

### C. 读取切片完整正文
1. 检索结果 `Text` 被截断 → 2. `GET /api/knowledge-bases/{id}/chunks/{chunkId}` → 3. 用完整 `Text`

> 大文件（小说类可达 37 万行）不要一次拉全文，按文档切片分页取。

### D. 结果不够详细
1. 提高 `TopK` 再检索一次；2. 若仍不足，先 `GET .../chunks?documentId=` 定位切片，再点取单切片全文。

### E. 精确术语 + 语义组合
1. `UseLexicalTokenizer: true` 锁定 chunk → 2. 同一批 `KnowledgeBaseIds` 做普通语义检索 → 3. 合并回答

---

## 深入参考

| 需求 | 文档 |
|------|------|
| **多知识库架构：选库、跨库检索、切片读取、漂移** | [references/knowledge-base.md](references/knowledge-base.md) |
| 全部 `/api/*` 端点完整参数表、模式默认值、响应字段 | [references/api-reference.md](references/api-reference.md) |
| `/v1/*` OpenAI 兼容端点（chat / embeddings / rerank / models） | [references/openai-compat.md](references/openai-compat.md) |
| 故障排查表、字段名映射、易错点详解 | [references/troubleshooting.md](references/troubleshooting.md) |
| 服务状态自检、索引规模查询 | [references/api-reference.md](references/api-reference.md#10-服务状态自检) |

**调用报错或结果异常时，先看 [troubleshooting.md](references/troubleshooting.md)。**

## GitHub

https://github.com/csvkse/ReferenceRAG
