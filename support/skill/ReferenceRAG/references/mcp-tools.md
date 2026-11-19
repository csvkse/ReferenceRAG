# ReferenceRAG MCP 工具完整参考手册 (26 工具)

> **端点**：`POST http://127.0.0.1:5123/api/mcp`
> **鉴权头**：`X-API-Key: rag_live_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx` 或 `Authorization: Bearer <key>`
> **协议规范**：JSON-RPC 2.0，所有工具均带 `inputSchema`、`outputSchema` 与 annotations（`readOnlyHint: true`, `idempotentHint: true`）。

---

## 1. 26 个工具分类速查表

| 分类 | 工具名 (`name`) | 标题 | 何时使用 / 场景说明 |
|:---|:---|:---|:---|
| **核心检索** | `knowledge_search` | 知识库通用检索（统一入口） | **主力检索（首选）**。支持 auto 智能路由、hybrid、semantic 与 lexical。 |
| **核心检索** | `hybrid_search` | 混合检索（向量+词法） | 经典检索别名。向量语义召回 + BM25 词法匹配，双路 RRF 融合。 |
| **核心检索** | `lexical_search` | 纯词法检索（免向量） | 精确查找错误码、API 名、函数名、配置项或特定专有名词（零模型延迟）。 |
| **核心检索** | `semantic_search` | 语义检索（向量） | 抽象技术方案、概念原理解释、自然语言意图检索。 |
| **核心检索** | `knowledge_base_search` | 跨库融合检索 | 在指定知识库中执行跨库多路召回与 RRF 融合检索。 |
| **定向下钻** | `knowledge_base_drill_down` | 展开切片正文 | 检索切片内容被截断时，根据 `chunkId` 获取完整正文（无须本地磁盘路径）。 |
| **文档与行读取** | `get_note_outline` | 文件大纲 | 提取 Markdown 文件的 H1-H6 标题树与行号，在大文档通读前先看结构。 |
| **文档与行读取** | `read_note_content` | 按行读取文件 | 根据行号范围（`startLine` ~ `endLine`）读取原始笔记正文。 |
| **文档与行读取** | `get_document_info` | 切片归属反查 | 按 `chunkId` 或 `documentId` 反查其所属文档路径、切片总数与文件大小。 |
| **文件与目录** | `list_files` | 已索引文件清单 | 分页列出知识库已索引的文件列表，支持按路径关键词或连接器过滤。 |
| **文件与目录** | `find_files_by_pattern` | 按模式找文件 | 按 Glob 通配符（如 `*.md`）在数据源中快速匹配文件路径。 |
| **文件与目录** | `get_file_tree` | 浏览目录树 | 逐层浏览指定数据源或子目录的文件与目录树结构。 |
| **文件与目录** | `search_file_content` | 文件正文检索 | ripgrep 风格的磁盘文件正文正则/文本快速搜索（带行号与匹配行）。 |
| **图谱探索** | `graph_search` | 图谱邻域遍历 | 从目标节点出发遍历知识图谱关联节点与双链/标签边（深度 1-3）。 |
| **图谱探索** | `graph_search_by_title` | 图谱按标题搜索 | 在知识图谱中按标题关键词模糊检索节点。 |
| **系统与元数据** | `knowledge_base_list` | 知识库清单 | 列出所有注册的物理知识库（ID、名称、启用状态、嵌入规格）。 |
| **系统与元数据** | `list_sources` | 数据源清单 | 列举物理数据源、路径、文件数与切片数。 |
| **系统与元数据** | `get_source_info` | 数据源详情 | 查看单个数据源的挂载路径、文档/切片计数与物理文件状态。 |
| **系统与元数据** | `get_system_stats` | 系统统计 | 查看各库分块/文档/向量总计、模型配置与重排健康度。 |
| **系统与元数据** | `get_index_status` | 索引状态 | 查询各物理知识库的分块数、文档数、向量数与词法就绪状态。 |
| **系统与元数据** | `get_service_info` | 服务信息 | 获取服务端版本号、协议版本与端点配置。 |
| **系统与元数据** | `ping` | 连通性探活 | 最轻量的双向连通性测试，返回 `pong`。 |
| **向量与重排** | `embed_text` | 文本向量化 | 单段文本生成稠密向量（维度、范数与分量预览）。 |
| **向量与重排** | `embed_batch` | 批量向量化 | 批量文本生成向量列表。 |
| **向量与重排** | `calculate_similarity` | 文本相似度 | 直接计算两段文本的语义余弦相似度（0~1 分数与解释）。 |
| **向量与重排** | `rerank_results` | 候选深度重排 | 传入 query 与文档列表进行深度相关性重排打分。 |

---

## 2. 核心检索工具契约

### 2.1 `knowledge_search`（统一检索入口，首选）
- **入参 (`arguments`)**：
  - `query` (`string`，必填)：检索查询关键词或自然语言问题。
  - `mode` (`string`，可选，默认 `"auto"`)：检索模式，可选 `"auto"`（智能决策）、`"hybrid"`（混合融合）、`"semantic"`（纯语义）、`"lexical"`（纯词法）。
  - `topK` (`integer`，可选，默认 5，范围 1–50)：返回切片数量。
  - `knowledgeBaseIds` (`string`，可选)：限定知识库 ID 列表，传 JSON 数组串如 `'["40745ffb28444648"]'`；留空为全局。
- **关键响应 (`structuredContent`)**：
  - `items[*].chunkId` (`string`)：切片全局唯一 Hex ID。
  - `items[*].text` (`string`)：切片 Markdown 正文。
  - `items[*].score` (`float`)：融合评分（RRF）。
  - `items[*].channels` (`string[]`)：召回通道，如 `["lexical"]` 或 `["lexical", "vector"]`。
  - `items[*].sourceKey` (`string`)：来源文件相对路径。
  - `items[*].startLine` / `endLine` (`int`)：正文在原文件中的行号。
  - `noMatch` (`boolean`)：**核心门禁**。若为 true，说明未命中真实内容，当前切片为兜底放行，严禁基于此编造答案！
  - `noMatchReason` (`string`)：未命中的具体原因。

### 2.2 `knowledge_base_drill_down`（下钻展开完整切片）
- **入参**：
  - `chunkId` (`string`，可选)：目标切片 ID。
  - `documentId` (`string`，可选)：文档 ID（可与 `limit` 搭配获取文档全部切片）。
  - `knowledgeBaseId` (`string`，可选)：知识库 ID。
- **返回**：`chunk.{chunkId, text, tokenCount, ordinal, headingPath, startLine, endLine}`。

---

## 3. 文件、大纲与正则检索

### 3.1 `get_file_tree`（目录树浏览）
- **入参**：
  - `subPath` (`string`，可选，默认空)：子目录相对路径。
  - `sourceName` (`string`，可选)：指定数据源名称（如 `"Temp"`）。
- **返回**：`entries[*].{name, isDirectory, size, modified}`。

### 3.2 `search_file_content`（正文正则检索）
- **入参**：
  - `pattern` (`string`，必填)：检索文本或正则表达式。
  - `maxMatches` (`int`，可选，默认 20)：最大匹配结果数。
  - `contextLines` (`int`，可选，默认 1)：上下文展示行数。
  - `sourceName` (`string`，可选)：限定数据源名称。
  - `subPath` (`string`，可选)：限定子路径。
- **返回**：`totalMatches`, `matchedFilesCount`, `files[*].{relativePath, matchCount, hunks[*].lines[*].{lineNumber, text, isMatch}}`。

### 3.3 `get_note_outline` 与 `read_note_content`
- `get_note_outline`: 入参 `{filePath, sourceName?}`，返回 Markdown 标题树 `{headings[*].{level, title, lineNumber}, totalLines}`。
- `read_note_content`: 入参 `{filePath, startLine, endLine, sourceName?}`，流式返回指定行区间内容。

---

## 4. 知识图谱 (Wiki-links / 双链与标签)

### 4.1 `graph_search`
- **入参**：
  - `nodeId` (`string`，必填)：起始节点 ID（如文档相对路径或标签 `#tag`）。
  - `depth` (`int`，可选，默认 1，范围 1–3)：遍历搜索深度。
  - `knowledgeBaseId` (`string`，可选)。
- **返回**：`rootId`, `totalNodes`, `totalEdges`, `nodes[*].{id, title, type}`, `edges[*].{from, to, type}`。

### 4.2 `graph_search_by_title`
- **入参**：
  - `query` (`string`，必填)：标题关键词。
  - `limit` (`int`，可选，默认 20)。
- **返回**：`query`, `count`, `nodes[*].{id, title, type, knowledgeBaseId}`。

---

## 5. MCP JSON-RPC 调用范例

```bash
# 调用 knowledge_search
curl -s -X POST "http://127.0.0.1:5123/api/mcp" \
  -H "Content-Type: application/json; charset=utf-8" \
  -H "X-API-Key: rag_live_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx" \
  -d '{
    "jsonrpc": "2.0",
    "id": 1,
    "method": "tools/call",
    "params": {
      "name": "knowledge_search",
      "arguments": {
        "query": "异步状态机 MoveNext",
        "mode": "auto",
        "topK": 3
      }
    }
  }'
```
