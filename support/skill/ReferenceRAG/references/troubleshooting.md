# 故障排查与易错点

调用报错或结果异常时先查这里。

---

## 快速自检顺序

```
1. GET /api/system/health          服务是否活着
2. GET /api/ai/status              索引/模型是否就绪（IndexRequiresRebuild、RerankEnabled、IsGpu）
3. GET /api/sources                数据源是否存在、FileCount 是否 > 0
4. GET /api/bm25index/summary      索引规模（TotalIndexedDocuments）
5. GET /api/graph/stats            EdgeCount 是否 > 0（判断图谱能否用）
```

`IndexRequiresRebuild: true` 时检索结果可能过期，需 `/api/index/rebuild`。

---

## 故障排查表

| 现象 | 原因 | 处理 |
|------|------|------|
| 空白 500，`Content-Length: 0` | 请求体非 UTF-8 编码（旧版本服务） | 用 heredoc + `--data-binary`，或 PowerShell UTF-8 字节。见下 |
| 400 `must be valid UTF-8 encoded text` | 同上（新版本服务） | 同上 |
| 400 `input must be valid UTF-8 encoded text` | `/v1/embeddings` 的 `input` 非 UTF-8 | 同上 |
| 400 `documents contains an item that is not valid UTF-8 encoded text` | `/v1/rerank` 的 `documents` 非 UTF-8 | 同上 |
| 400 `The q field is required.` | `/api/graph/search` 缺 `q` | 加 `q` 参数 |
| 400 `The nodeId field is required.` | `/api/graph/node` 参数名写错 | 该接口参数是 `nodeId`，**不是 `id`** |
| 404 `节点不存在: <乱码路径>` | `nodeId` 含中文未 URL 编码 | 用 `curl --get --data-urlencode "nodeId=..."` |
| 400 `query is required and must be a string` | `/v1/rerank` 缺 `query` 或类型错 | 补 `query`，必须是字符串 |
| 400 `documents is required and must be an array` | `/v1/rerank` 缺 `documents` | 补 `documents` 数组 |
| 400 `items 不能为空` | `/api/sources/file/lines` 的 `Items` 空 | 至少一条 |
| 400 `路径无效` | 路径为空或含 `..` | 用绝对路径，去掉 `..` |
| `Edges` 恒为空数组 | 数据源无 `[[双链]]`，`.txt` 不解析链接 | 图谱未建边。改用 `/api/ai/query` 检索 |
| 召回小说正文 | 索引含非技术文件（`操虫师.txt`、`剑来.txt`） | 预期噪声。用更精确的技术词 |
| `FullContext` 为空字符串 | `RefIds` 传错（传了 `FileId` 或 chunk 序号） | 必须用 `/api/ai/query` 返回的 `RefId` GUID |
| BM25 的 `Content` 是乱码空格文本 | 那是分词结果，不是原文 | 拿 `ChunkId` 对应 `FilePath` 走 `/api/sources/file/lines` |
| `Chunks[*].Content` 全空串 | 开了 `Slim: true` | 想要内容就别开 Slim，`Context` 字段仍有全文 |
| 首次查询 3-5 秒 | 模型冷加载 | 正常，看 `Stats.Phases.ModelLoadMs` |
| 小写 `query`/`topK` 无效 | 字段名大小写 | `/api/*` 用 PascalCase |
| `TopK` 不是预期值 | 未显式传，用了模式默认 | 显式传 `TopK` |
| 503 `Chat API Key is not configured` | 聊天后端未就绪 | 配置 `chat.activeBackend` 对应后端的模型/Key |
| Web UI `http://localhost:7897` 打不开 | DesktopHost 前端走 `app://` 协议 | 启动 DesktopHost 窗口，或改用 WebHost |
| 端点不在 Swagger 里 | Swagger 未覆盖全部端点 | `file/lines`、`files/info`、`bm25index/search` 都不在，但确实存在 |

---

## UTF-8 编码问题详解

这是最容易踩的坑，单独说明。

**根因**：Windows 控制台默认代码页是 936（GBK）。Git Bash 的 `curl -d '{"Query":"中文"}'`
把命令行里的中文字节按 GBK 原样发出，服务端 `System.Text.Json` 按 UTF-8 解析，
遇到 GBK 字节序列（如 `分` = `0xB7 0xD6`）抛：

```
System.InvalidOperationException: Cannot transcode invalid UTF-8 JSON text to UTF-16 string.
 ---> System.Text.DecoderFallbackException: Unable to translate bytes [B7] at index 4
   at System.Text.Json.JsonReaderHelper.TranscodeHelper
   at System.Text.Json.JsonElement.GetString()
```

`GetString()` 调用在控制器的 `try` 块**之外**，异常逃逸控制器，
Kestrel 直接返回 `500` 且 `Content-Length: 0`，控制器内已有的 `catch` 永远不执行。

**新版服务已修**：控制器改为 `TryGetString` + 返回 400 指明原因，并加了全局异常中间件。
但客户端仍应发正确的 UTF-8。

### 正确写法

```bash
# Git Bash：heredoc + --data-binary（文件内容是 UTF-8）
curl -s -X POST "http://localhost:7897/api/ai/query" \
  -H "Content-Type: application/json; charset=utf-8" \
  --data-binary @- << 'EOF'
{"Query": "Git 分支管理 branch 版本控制", "Mode": "HybridRerank", "TopK": 5}
EOF
```

```powershell
# PowerShell：显式转 UTF-8 字节
$body = '{"Query": "Git 分支管理", "Mode": "HybridRerank", "TopK": 5}'
$bytes = [System.Text.Encoding]::UTF8.GetBytes($body)
Invoke-WebRequest -Uri "http://localhost:7897/api/ai/query" `
  -Method Post -Body $bytes -ContentType "application/json; charset=utf-8"
```

**纯 ASCII 查询词无此问题**，`curl -d '{"Query":"docker network"}'` 可正常工作。

---

## 字段名大小写映射

服务端 `PropertyNamingPolicy = null`，`/api/*` 全部 PascalCase。
`/v1/*` 是 camelCase。

| `/api/*`（PascalCase） | `/v1/*`（camelCase） |
|------------------------|----------------------|
| `Query` | `query` |
| `Mode` | — |
| `TopK` | `top_n`（仅 rerank） |
| `Sources` | — |
| `Filters.Tags` | — |
| `Filters.Folders` | — |
| `Slim` | — |
| `EnableRerank` | — |
| `Paths` | — |
| `Items` | — |
| `Path` | — |
| `StartLine` | — |
| `EndLine` | — |
| — | `documents` |
| — | `input` |
| — | `messages` |
| — | `return_documents` |
| — | `encoding_mode` |

响应字段同样遵循此规则：

| `/api/*` | 含义 |
|----------|------|
| `Chunks` | 结果分块数组 |
| `RefId` | drill-down 用（GUID） |
| `FileId` | 文件 ID（**不能**用于 drill-down） |
| `FilePath` | 文件绝对路径 |
| `HeadingPath` | 章节路径 |
| `BM25Score` | BM25 分 |
| `EmbeddingScore` | 向量相似度 |
| `RerankScore` | 重排分 |
| `StartLine` / `EndLine` | chunk 行范围 |
| `ObsidianLink` | `[[文件#L行号]]` 链接 |
| `Context` | 拼装好的上下文（直接用） |
| `Stats.Phases` | 各阶段耗时 |
| `RerankStats` | 重排统计 |

---

## 图谱接口特殊情况

当前实例（2026-10-22）`EdgeCount` 为 0，原因是：

1. 数据源里 `.md` 文件没有 `[[双链]]`
2. `.txt` 小说不做链接解析
3. `WikiLinkExtractor` 只识别 `[[wikilink]]`、`![[embed]]`、`#tag` 三种边

**表现**：`/api/graph/neighbors` 和 `/api/graph/subgraph` 返回 `Nodes: []`、`Edges: []`，
不报错，看起来像调用成功但没数据。

**判断方法**：
```bash
curl -s "http://localhost:7897/api/graph/stats"
```
`EdgeCount > 0` 才值得用图谱接口。

**替代方案**：直接用 `/api/ai/query` 做语义检索，不依赖图谱关联。

### 节点 ID 格式

`/api/graph/search` 返回的 `Id` 是 `文件路径#标题` 复合串：

```
E:/Content/Down/新建文件夹/note.md#章节标题
```

- 传给 `/api/graph/neighbors` 要 URL 编码（含中文和 `#`）
- 传给 `/api/graph/subgraph` 的 `RootIds` 建议用**纯文件路径**（不带 `#标题`），能命中 `document` 节点
- `/api/graph/node` 用 `nodeId` 参数，不是 `id`

---

## 认证

当前实例 `ReferenceRAG:Service:apiKey` 为空字符串，`ApiKeyMiddleware` 直接放行，
**所有端点无需认证头**。

配置了 Key 后，以下路径需要认证：

- `/api/*`
- `/v1/*`
- `/indexHub`（SignalR）

支持三种传法：

```bash
# 1. X-API-Key 头
curl -H "X-API-Key: <key>" ...

# 2. Authorization Bearer
curl -H "Authorization: Bearer <key>" ...

# 3. SignalR 用 query string
/IndexHub?access_token=<key>
```

缺失返回 401，错误返回 403。

> `ApiKeyMiddleware` 对每个请求都重新读 `ConfigManager.Load()`，
> 改 Key 后立即生效，无需重启。

---

## Swagger 覆盖不全

`http://localhost:7897/swagger` 可达（DesktopHost 返回 301 重定向，跟随后缀）。

但以下端点**不在 Swagger 里**，不代表不存在，本文档已实测可用：

- `POST /api/sources/file/lines`
- `POST /api/sources/files/info`
- `GET /api/bm25index/search`
- `GET /api/bm25index/summary`

Web UI `http://localhost:7897` **返回 404** —— DesktopHost 前端由 `app://` 自定义协议提供，
不经 HTTP。要看界面需启动 DesktopHost 窗口，或改用 WebHost。
