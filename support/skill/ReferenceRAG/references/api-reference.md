# ReferenceRAG REST API 完整参考 (v2)

> **服务地址**：`http://127.0.0.1:5123`
> **鉴权方式**：所有端点均须包含 `X-API-Key: rag_live_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx` 或 `Authorization: Bearer <key>`
> **字段命名规则**：
> - `/api/*` 端点请求体与响应体为 **PascalCase**（如 `Query`、`KnowledgeBaseIds`、`TopK`、`Items`）
> - `/api/mcp` 为 JSON-RPC 2.0，入参与返回使用标准 **camelCase**
> - `/v1/*` OpenAI 兼容端点为 **camelCase**

---

## 1. POST /api/knowledge-base-search — 物理知识库检索（v2 主力）

在指定或全部物理隔离知识库中执行向量检索与 BM25 词法检索，并进行 RRF 多路融合。

### 请求体 (PascalCase)
```json
{
  "Query": "异步状态机 MoveNext",
  "KnowledgeBaseIds": ["40745ffb28444648"],
  "TopK": 5,
  "UseLexicalTokenizer": false,
  "AllowDegrade": false,
  "FailurePolicy": 0
}
```

| 参数 | 类型 | 必填 | 说明 |
|:---|:---|:---|:---|
| `Query` | string | 是 | 搜索查询文本（中文须确保 UTF-8 编码）。 |
| `KnowledgeBaseIds` | string[] | 是 | 目标知识库 ID 数组。传 `[]` 表示在所有已启用的知识库中跨库检索。 |
| `TopK` | int | 否 | 返回数量（默认 10）。 |
| `UseLexicalTokenizer`| bool | 否 | 强制走纯词法检索（免向量计算，适合错误码、API 名精确查找）。 |
| `FailurePolicy` | int | 否 | 部分库失败策略：0 = AllowPartial（允许部分成功），1 = Strict。 |

### 响应体关键字段
```json
{
  "Query": "异步状态机 MoveNext",
  "Items": [
    {
      "ChunkId": "0e5b7c84...",
      "DocumentId": "b18ca72e...",
      "KnowledgeBaseId": "40745ffb28444648",
      "SourceKey": "1 工作/笔记/NET/Csharp进阶语法/7.3 异步的状态机设计.md",
      "Locator": "/lwt/r7/BackUp/D/D/Notes/Note/...",
      "Text": "切片原始 Markdown 正文...",
      "Score": 0.0123,
      "ScoreKind": "RRF",
      "Channels": ["Lexical"],
      "StartLine": 325,
      "EndLine": 325,
      "HeadingPath": "3. 最终正确结论表（必收藏）/状态机详情：C# 异步状态机完整逻辑剖析"
    }
  ],
  "SuccessfulKnowledgeBaseIds": ["40745ffb28444648"],
  "FailedKnowledgeBaseIds": [],
  "ScoreKind": "RRF",
  "Elapsed": "00:00:00.250",
  "Degraded": false,
  "IndexDegraded": false,
  "NoMatch": false,
  "NoMatchReason": null,
  "KbChannels": {
    "40745ffb28444648": "lexical",
    "dadd847d18b54162": "hybrid"
  }
}
```

> ⚠️ **关键裁决字段 `NoMatch`**：当且仅当检索出的切片正文中未包含查询核心二元组或英文标识时，服务端标记 `NoMatch = true`。调用方必须识别该字段，未命中时不得采信切片内容生成回答。

---

## 2. 知识库与切片管理端点

### 2.1 GET /api/knowledge-bases — 列出已注册知识库
- **响应**：知识库对象数组（包含 `Id`、`Name`、`Description`、`Enabled`、`EmbeddingSpecId`、`EffectiveConfigRevision`）。

### 2.2 GET /api/knowledge-bases/{id}/documents — 知识库文档分页
- **Query 参数**：
  - `page` (`int`，默认 1)
  - `pageSize` (`int`，默认 50)
  - `search` (`string`，可选，按文件名模糊过滤)
  - `connectorId` (`string`，可选)

### 2.3 GET /api/knowledge-bases/{id}/chunks — 文档切片列表
- **Query 参数**：
  - `documentId` (`string`，必填)
  - `page` (`int`)
  - `pageSize` (`int`)
- **用途**：在已知某文档某章节相关时，按切片顺序分页提取上下文。

### 2.4 GET /api/knowledge-bases/{id}/chunks/{chunkId} — 单个切片全文
- **用途**：当搜索返回的切片被截断时，按 `chunkId` 获取完整正文。

---

## 3. POST /api/mcp — MCP JSON-RPC 2.0 端点

提供 25 个强类型 MCP 工具调用服务。

```bash
curl -s -X POST "http://127.0.0.1:5123/api/mcp" \
  -H "Content-Type: application/json; charset=utf-8" \
  -H "X-API-Key: rag_live_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx" \
  -d '{
    "jsonrpc": "2.0",
    "id": 1,
    "method": "tools/call",
    "params": {
      "name": "hybrid_search",
      "arguments": { "query": "测试", "topK": 3 }
    }
  }'
```

详见 [mcp-tools.md](mcp-tools.md)。

---

## 4. GET /api/system/stats — 系统运行状态与模型遥测

返回存储路径、挂载源统计、各库文档/切片/向量汇总、嵌入模型规格、重排健康度（`health: "ok" | "degraded"`）。


