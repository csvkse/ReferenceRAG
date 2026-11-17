# ReferenceRAG MCP 工具完整参考手册 (25 工具)

> **端点**：`POST http://127.0.0.1:5123/api/mcp`
> **鉴权头**：`X-API-Key: rag_live_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx` 或 `Authorization: Bearer <key>`
> **协议规范**：JSON-RPC 2.0，所有工具均带 `inputSchema`、`outputSchema` 与 annotations（`readOnlyHint: true`, `idempotentHint: true`）。

---

## 1. 工具分类速查表

| 分类 | 工具名 (`name`) | 标题 | 何时使用 / 场景说明 |
|:---|:---|:---|:---|
| **核心检索** | `knowledge_search` | 知识库检索（统一入口） | **主力检索（首选）**。统一通用检索入口，`mode` 支持 auto / hybrid / semantic / lexical。 |
| **核心检索** | `hybrid_search` | 混合检索（向量+词法） | 经典检索别名。向量概念召回 + BM25 词法匹配，双路 RRF 融合。 |
| **核心检索** | `lexical_search` | 词法检索（知识库） | 经典检索别名。精确查找错误码、API 名、函数名、配置项或特定专有名词（零模型延迟）。 |
| **核心检索** | `semantic_search` | 语义检索（向量） | 经典检索别名。抽象技术方案、概念原理解释、自然语言意图检索。 |
| **核心检索** | `knowledge_base_search` | 跨库融合检索 | 跨物理隔离知识库联合检索入口。 |
| **定向下钻** | `knowledge_base_drill_down` | 展开切片正文 | 检索切片内容被截断时，根据 `knowledgeBaseId` + `chunkId` 获取完整正文。 |
| **文档与行读取** | `get_note_outline` | 文件大纲 | 提取 Markdown 文件的 H1-H6 标题树与行号，在大文档通读前先看结构。 |
| **文档与行读取** | `read_note_content` | 按行读取文件 | 根据行号范围（`startLine` ~ `endLine`）读取原始笔记正文。 |
| **文档与行读取** | `get_document_info` | 切片归属文件 | 探查某切片所属文档的完整元数据、切片总数与文件大小。 |
| **文件与目录** | `list_files` | 已索引文件清单 | 分页列出知识库已索引的文件列表，支持按路径关键词过滤。 |
| **文件与目录** | `find_files_by_pattern` | 按模式找文件 | 按 Glob 通配符（如 `*.md`）在源中快速匹配文件路径。 |
| **文件与目录** | `get_file_tree` | 浏览目录树 | 逐层浏览指定数据源或子目录的文件与目录树结构。 |
| **文件与目录** | `search_file_content` | 文件内容检索 | ripgrep 风格的磁盘文件正文正则/文本秒级搜索（带上下文行）。 |
| **图谱探索** | `graph_search` | 图谱邻域遍历 | 从目标节点出发遍历知识图谱关联节点与双链边（深度 1-3）。 |
| **图谱探索** | `graph_search_by_title` | 图谱按标题搜索 | 在图谱节点中按笔记标题或别名模糊匹配文档节点。 |
| **系统与元数据** | `knowledge_base_list` | 知识库清单 | 列出所有注册的物理知识库（ID、名称、启用状态、嵌入规格）。 |
| **系统与元数据** | `list_sources` | 数据源清单 | 列举物理数据源、路径、文件数与切片数。 |
| **系统与元数据** | `get_source_info` | 数据源详情 | 查看单个数据源的挂载路径、排除规则、连接器映射。 |
| **系统与元数据** | `get_system_stats` | 系统统计 | 查看各库分块/文档/向量总计、模型配置与重排健康度。 |
| **系统与元数据** | `get_index_status` | 索引状态 | 查询后台索引构建进度与词法就绪状态。 |
| **系统与元数据** | `get_service_info` | 服务信息 | 获取服务端版本号、协议版本与端点配置。 |
| **系统与元数据** | `ping` | 连通性探活 | 最轻量的双向连通性测试，返回 `pong`。 |
| **向量与重排** | `embed_text` | 文本向量化 | 单段文本生成稠密向量（上限 8000 字符）。 |
| **向量与重排** | `embed_batch` | 批量向量化 | 批量文本生成向量列表（建议单批 ≤ 32 条）。 |
| **向量与重排** | `calculate_similarity` | 文本相似度 | 直接计算两段文本的余弦相似度（-1 ~ 1）。 |
| **向量与重排** | `rerank_results` | 候选重排 | 传入 query 与文档列表进行重排（当前环境 GGUF 重排处于降级态）。 |

---

## 2. 核心检索工具详解

### 2.1 `hybrid_search`（混合检索）
- **入参**：
  - `query` (`string`，必填)：检索查询文本。
  - `topK` (`integer`，可选，默认 5，上限 50)：返回结果数量。
  - `lexicalOnly` (`boolean`，可选，默认 false)：是否跳过向量仅走词法。
  - `knowledgeBaseIds` (`string`，可选)：目标库 ID 列表，传 JSON 数组串如 `["40745ffb28444648"]`；留空表示全库。
- **关键响应字段**：
  - `items[*].text`：原始切片正文。
  - `items[*].channels`：召回通道，如 `["lexical"]` 或 `["lexical", "vector"]`。
  - `items[*].score`：RRF 融合得分。
  - `noMatch` (`boolean`)：**非常重要**。若为 true，说明结果正文未命中查询词，属于凑数切片，模型不可采信！
  - `noMatchReason` (`string`)：未命中的具体原因。

### 2.2 `lexical_search`（词法检索）
- **入参**：
  - `query` (`string`，必填)：关键词、错误码、函数名、API 标识。
  - `topK` (`integer`，可选，默认 5)。
  - `knowledgeBaseIds` (`string`，可选)。
- **适用场景**：精确字面匹配，适合 `Notes` 库（纯词法库）或错误排查，耗时通常仅 10~50ms。

---

## 3. 进阶大纲与切片读取工具

### 3.1 `get_note_outline`
- **入参**：
  - `filePath` (`string`，必填)：文档相对路径，如 `Note/1 工作/笔记/NET/Csharp进阶语法/7.3 异步的状态机设计.md`。
  - `sourceName` (`string`，可选)：如 `Notes - Note`。
- **返回**：`headings[*].{level, title, lineNumber}`、`totalLines`。

### 3.2 `read_note_content`
- **入参**：
  - `filePath` (`string`，必填)。
  - `startLine` (`integer`，必填，从 1 开始)。
  - `endLine` (`integer`，必填)。
  - `sourceName` (`string`，可选)。
- **返回**：`text` (指定行范围正文)、`returnedLines`、`hasMore`。

---

## 4. MCP JSON-RPC 调用范例

```bash
# 调用 hybrid_search
curl -s -X POST "http://127.0.0.1:5123/api/mcp" \
  -H "Content-Type: application/json; charset=utf-8" \
  -H "X-API-Key: rag_live_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx" \
  -d '{
    "jsonrpc": "2.0",
    "id": 1,
    "method": "tools/call",
    "params": {
      "name": "hybrid_search",
      "arguments": {
        "query": "异步状态机 MoveNext",
        "topK": 3
      }
    }
  }'
```


