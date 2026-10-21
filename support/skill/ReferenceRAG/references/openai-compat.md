# OpenAI 兼容端点（/v1/*）

供第三方客户端（Cherry Studio、NextChat、Chatbox、Cursor、LangChain 等）调用。

**字段命名是 camelCase**（`query`、`documents`、`input`），与 `/api/*` 的 PascalCase 相反。

所有端点遵循 `ReferenceRAG:Service:apiKey` 认证规则（当前实例未配置，无需认证头）。

---

## GET /v1/models — 模型列表

```bash
curl -s "http://localhost:7897/v1/models"
```

**响应：**
```json
{
  "object": "list",
  "data": [
    { "id": "reference-rag", "object": "model", "created": 1792599236, "owned_by": "reference-rag",
      "description": "ReferenceRAG 知识库混合检索增强生成助手 (Hybrid RAG Assistant)" },
    { "id": "Qwen3.5-2B-Instruct-GGUF", "object": "model", "created": 1792599236, "owned_by": "reference-rag" },
    { "id": "bge-m3", "object": "model", "created": 1792599236, "owned_by": "reference-rag" },
    { "id": "bge-reranker-base", "object": "model", "created": 1792599236, "owned_by": "reference-rag" }
  ]
}
```

可用 ID：`reference-rag`（RAG 助手）、当前 chat 模型名、`bge-m3`、`bge-reranker-base`。
全部 `owned_by` 为 `reference-rag`。

单模型查询：`GET /v1/models/{modelId}`，返回 `{ "id": "...", "object": "model", "owned_by": "reference-rag" }`。

---

## POST /v1/chat/completions — 聊天补全

```bash
curl -s -X POST "http://localhost:7897/v1/chat/completions" \
  -H "Content-Type: application/json" \
  --data-binary @- << 'EOF'
{"model": "reference-rag", "stream": false, "messages": [{"role": "user", "content": "向量检索的原理"}]}
EOF
```

| 参数 | 类型 | 说明 |
|------|------|------|
| `model` | string | 见下方 RAG 模式说明。**缺省按 `reference-rag` 处理** |
| `messages` | array | `[{ "role": "user", "content": "..." }]`，必填，最多 200 条 |
| `stream` | bool | `true` = SSE 流式 |
| `max_tokens` | int | 生成上限 |

**安全边界**（服务端硬限制）：

- `messages` 最多 200 条，超出 400 `messages array cannot exceed 200 items`
- 全部消息内容合计最多 256000 字符，超出 400 `Total messages content exceeds maximum length`
- `messages` 为空 400 `messages array cannot be empty`
- 缺 `messages` 400 `messages is required and must be an array of message objects`

### RAG 模式触发条件（重要）

**仅 `model` 精确等于 `reference-rag`**（不区分大小写，首尾空白会被 trim）才启用知识库增强：

```csharp
const string RagAssistantModelId = "reference-rag";
bool isRagMode = string.Equals(model, RagAssistantModelId, StringComparison.OrdinalIgnoreCase);
```

| `model` 值 | 是否触发 RAG |
|------------|--------------|
| `reference-rag` | ✅ 是 |
| `REFERENCE-RAG` | ✅ 是（不区分大小写） |
| ` reference-rag ` | ✅ 是（已 trim） |
| `storage-rag-7b` | ❌ 否（含 "rag" 但不相交） |
| `my-rag-model` | ❌ 否 |
| 缺省（未传 `model`） | ✅ 是（按 `reference-rag` 处理） |

> 历史版本用 `model.Contains("rag")` 判定，导致名字含 "rag" 的普通模型被强行注入知识库上下文。
> 现已收紧为精确匹配。

RAG 模式会调 `RetrieveRagContextAsync(latestUserQuery, topK: 5)`，把知识库检索结果作为 system 消息插入消息列表头部。

### 实际使用的后端

`/v1/chat/completions` 本身不指定后端，实际用的聊天模型由 `chat.activeBackend` 决定：

| `chat.activeBackend` | 实际后端 |
|----------------------|----------|
| `LocalGguf` | 本地 GGUF 模型（llama.cpp） |
| `LocalOnnxGenAi` / `LocalOnnx` | 本地 ONNX Runtime GenAI |
| `OpenAiApi` | 外部 OpenAI 兼容端点 |

用 `GET /api/chat/capabilities` 确认：

```bash
curl -s "http://localhost:7897/api/chat/capabilities"
```

`BackendType` 为 `LocalGguf` 即本地，`OpenAiApi` 即外部。

> ⚠️ **响应里的 `model` 字段回显请求传入的值，不是实际推理用的模型。**
> 请求传 `stable-code-latest` 而本地跑的是 `Qwen3.5-2B-Instruct-GGUF`，响应 `model` 仍是 `stable-code-latest`。
> 要知道实际模型，查 `/api/chat/capabilities` 的 `ModelName`。

### 响应格式（非流式）

```json
{
  "id": "chatcmpl-e9f10b2de78442e5",
  "object": "chat.completion",
  "created": 1792600299,
  "model": "reference-rag",
  "choices": [
    { "index": 0, "message": { "role": "assistant", "content": "..." }, "finish_reason": "stop" }
  ],
  "usage": { "prompt_tokens": 14, "completion_tokens": 69, "total_tokens": 83 }
}
```

### 响应格式（流式 SSE）

```
data: {"id":"chatcmpl-...","object":"chat.completion.chunk","created":...,"model":"...","choices":[{"index":0,"delta":{"role":"assistant","content":""},"finish_reason":null}]}

data: {"id":"chatcmpl-...","object":"chat.completion.chunk","created":...,"model":"...","choices":[{"index":0,"delta":{"content":"正文片段"},"finish_reason":null}]}

data: {"id":"chatcmpl-...","object":"chat.completion.chunk","created":...,"model":"...","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{...}}

data: [DONE]
```

首 chunk 声明 role，中间 chunk 增量 content，末 chunk 带 `finish_reason: "stop"` 和 `usage`，最后 `data: [DONE]`。

错误时在流内写 `data: {"error":{"message":"...","type":"server_error"}}` 后跟 `data: [DONE]`。

### 503 服务未就绪

`_chatService.IsConfigured` 为 false 时返回 503：

```json
{ "error": { "message": "Chat API Key is not configured...", "type": "service_unavailable" } }
```

---

## POST /v1/embeddings — 向量嵌入

```bash
curl -s -X POST "http://localhost:7897/v1/embeddings" \
  -H "Content-Type: application/json" \
  --data-binary @- << 'EOF'
{"model": "bge-m3", "input": "测试中文嵌入 embedding 向量"}
EOF
```

| 参数 | 类型 | 说明 |
|------|------|------|
| `model` | string | 模型名，如 `bge-m3` |
| `input` | string 或 string[] | 必填，支持单文本或数组 |
| `encoding_mode` | string | `symmetric`（默认）/ `query` / `document` |
| `debug` | bool | `true` 时响应附加 norm、相似度矩阵等调试信息 |

**安全边界：**

- `input` 缺失或 null → 400 `input is required`
- `input` 数组最多 256 条，超出 400 `input array cannot exceed 256 items`
- 单条最长 32000 字符，超出 400 `input[i] exceeds maximum length of 32000 characters`
- 数组含非字符串元素 → 400 `input array element must be a string, got <Kind>`
- 非 UTF-8 字节 → 400 `input must be valid UTF-8 encoded text`

**响应：**
```json
{
  "object": "list",
  "model": "bge-m3",
  "data": [
    { "object": "embedding", "embedding": [0.0129, -0.0097, "..."], "index": 0 }
  ],
  "usage": { "prompt_tokens": 13, "total_tokens": 13 }
}
```

当前实例 `bge-m3` 返回 **1024 维**。

---

## POST /v1/rerank — 批量重排

Cohere / Jina 标准格式。

```bash
curl -s -X POST "http://localhost:7897/v1/rerank" \
  -H "Content-Type: application/json" \
  --data-binary @- << 'EOF'
{"model": "bge-reranker-base", "query": "Git 分支管理", "documents": ["git revert 撤销提交", "git reset --hard 强制回退", "docker compose 网络配置"]}
EOF
```

| 参数 | 类型 | 说明 |
|------|------|------|
| `query` | string | 必填，缺失或非字符串 → 400 `query is required and must be a string` |
| `documents` | array | 必填，字符串数组或 `[{ "text": "..." }]` 对象数组 |
| `model` | string | 模型名 |
| `top_n` | int | 限制返回条数，省略 = 全部 |
| `return_documents` | bool | 是否回显原文，默认 `true` |

**安全边界：**

- `documents` 缺失或非数组 → 400 `documents is required and must be an array`
- `documents` 为空 → 400 `documents array cannot be empty`
- 最多 256 条 → 400 `documents array cannot exceed 256 items`
- 单条最长 32000 字符 → 400 `documents[i] exceeds maximum length of 32000 characters`
- 元素既非字符串也非含 `text` 的对象 → 400 `documents[i] must be a string or an object with a "text" property`
- 非 UTF-8 字节 → 400 `documents contains an item that is not valid UTF-8 encoded text`

**响应：**
```json
{
  "object": "list",
  "model": "bge-reranker-base",
  "results": [
    { "index": 0, "relevance_score": 0.85, "document": "git revert 撤销提交" },
    { "index": 1, "relevance_score": 0.7333, "document": "git reset --hard 强制回退" },
    { "index": 2, "relevance_score": 0.6167, "document": "docker compose 网络配置" }
  ],
  "usage": { "prompt_tokens": 29, "total_tokens": 29 }
}
```

`relevance_score` 范围 0-1，降序排列。

---

## POST /v1/rerank/single — 单文档重排

简化版，一次评一个 query-document 对。

```bash
curl -s -X POST "http://localhost:7897/v1/rerank/single" \
  -H "Content-Type: application/json" \
  --data-binary @- << 'EOF'
{"model": "bge-reranker-base", "query": "Git 分支管理", "document": "Git 分支管理是版本控制的核心功能"}
EOF
```

| 参数 | 类型 | 说明 |
|------|------|------|
| `query` | string | 必填 |
| `document` | string | 必填 |
| `model` | string | 模型名 |

**响应：**
```json
{
  "object": "rerank_result",
  "model": "bge-reranker-base",
  "relevance_score": 0.85,
  "usage": { "prompt_tokens": 20, "total_tokens": 20 }
}
```

注意 `object` 是 `rerank_result`，不是 `list`。

---

## 常见错误响应格式

`/v1/*` 端点统一返回 OpenAI 风格错误体：

```json
{ "error": { "message": "具体原因", "type": "invalid_request_error" } }
```

`type` 取值：

| type | HTTP | 场景 |
|------|------|------|
| `invalid_request_error` | 400 | 参数缺失、格式错、非 UTF-8 |
| `server_error` | 500 | 服务端内部错误 |
| `service_unavailable` | 503 | 聊天后端未配置 |

> 服务端已全局捕获未处理异常。旧版本会返回 `500` 且 `Content-Length: 0`（空白响应），
> 现已统一为上述 JSON 结构。若仍遇到空白 500，说明服务端是旧二进制，需重启。
