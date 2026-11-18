---
name: ReferenceRAG
description: "本地多知识库语义检索服务。从 Obsidian 笔记、技术文档库中检索架构设计、源码剖析、配置说明与最佳实践。触发规则：(1)强制触发：rag:关键词、/rag 关键词 (2)意图触发：查询知识库/笔记、查阅相关技术文档 (3)自然提问：笔记里有没有写过、帮我找下相关配置。NOT for: 天气、新闻、实时股票等外部互联网信息。"
allowed-tools:
  - Read
  - Bash
---

# ReferenceRAG 知识库检索服务

支持物理隔离多知识库的混合检索（向量 + BM25 + RRF 融合）、目录树浏览与正文行切片读取。

```
服务地址：http://127.0.0.1:5123
鉴权凭据：X-API-Key: rag_live_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx
```

---

## ⚡ 双轨调用方式（优先 MCP 原生工具，CLI 脚本保底）

### 轨道 1：原生 MCP 工具调用（若环境已注册 ReferenceRAG MCP，最高优先）
- `knowledge_search`：**统一检索主力工具（首选）**。支持 `mode: "auto"` (默认智能路由)、`"hybrid"` (向量+词法融合)、`"semantic"` (语义召回)、`"lexical"` (精确词法)。可指定 `knowledgeBaseIds`。
- `knowledge_base_list`：列出当前 Key 授权的物理隔离知识库清单（ID 与模式）。
- `knowledge_base_drill_down`：检索结果切片被截断时，按 `knowledgeBaseId` + `chunkId`/`documentId` 精确展开完整切片正文。
- `get_note_outline` / `read_note_content`：针对长文档，先看大纲（H1-H6 标题树），再按行范围精准提取。
- `lexical_search` / `semantic_search` / `hybrid_search`：经典检索工具兼容别名（内部自动路由至 `knowledge_search`）。

### 轨道 2：CLI 脚本门面调用（跨平台安全保底）
在终端下直接运行内置 Python 门面（零外部依赖，强制 UTF-8，紧凑格式化）：

```bash
# 1. 统一知识库检索 (默认 auto 智能路由，兼顾语义与代码)
python <skill-dir>/scripts/rag_cli.py search "工作流状态机设计" --mode auto --top-k 5

# 2. 精确错误码 / 类名 / API 检索 (强制走 lexical 精确词法通道)
python <skill-dir>/scripts/rag_cli.py search "IndexOutOfRangeException" --mode lexical

# 3. 指定知识库检索 (例如限定 Notes 纯词法库: 40745ffb28444648)
python <skill-dir>/scripts/rag_cli.py search "状态机设计" --kb 40745ffb28444648

# 4. 精确切片下钻 (当搜索切片内容截断时，按 chunkId 展开完整正文，无需物理文件路径)
python <skill-dir>/scripts/rag_cli.py chunk <chunkId> [--kb <kbId>]

# 5. 查看大纲与按行读取长文档
python <skill-dir>/scripts/rag_cli.py outline "Note/1 工作/笔记/NET/Csharp进阶语法/7.3 异步的状态机设计.md" --source "Notes - Note"
python <skill-dir>/scripts/rag_cli.py read "Note/1 工作/笔记/NET/Csharp进阶语法/7.3 异步的状态机设计.md" --start-line 6 --end-line 50 --source "Notes - Note"

# 6. 查看系统状态与已授权知识库清单
python <skill-dir>/scripts/rag_cli.py list-kbs
python <skill-dir>/scripts/rag_cli.py stats
```

### 轨道 3：底层 REST API 映射（自动化流水线或无 CLI 环境）
- 跨库通用检索：`POST /api/knowledge-base-search`
- 知识库清单与状态：`GET /api/knowledge-bases`
- 数据源与长文按行读取：`GET /api/sources`, `POST /api/sources/file/lines`, `GET /api/sources/files/info`
- 异步作业队列：`GET /api/jobs`

---

## 🧭 Agent 决策与执行状态机

```
[用户提问]
   │
   ▼ 提取核心概念词 (同义词、中英混排，去口语化助词)
[策略路由]
   ├── 查代码/错误码/API/配置项 ──> 词法优先策略 (lexical_search 或 knowledge_search /api/knowledge-base-search)
   └── 查架构设计/教程/原理     ──> 混合融合策略 (hybrid_search / knowledge_search)
   │
   ▼
[执行检索]
   │
   ▼ 检查响应中的 NoMatch 信号
[质量门禁]
   ├── NoMatch == true ──> 判定为未命中。提取核心专有名词改写重试 1 次；若仍未命中则诚实告知用户未收录。
   └── NoMatch == false ──> 获取命中切片正文 (Text)
   │
   ▼
[按需下钻]
   ├── 切片内容足够 ──> 直接采信
   └── 切片内容截断 ──> (首选) 调用 chunk 直接展开完整切片正文
                     └── (次选/长文档通读) 调用 outline 查看结构，再调用 read 精确提取目标章节
   │
   ▼
[回答生成]
   严格基于切片回答，并在结论末尾标注引用来源：`[[来源路径#L起始行-L结束行]]`
```

---

## ⚠️ 核心注意事项与陷阱规避

1. **知识库特性差异**：
   - **`Notes` 库（ID: `40745ffb28444648`，23.7 万切片）**：为**纯词法库**，无向量索引。抽象查询务必先提取出实体关键词。
   - **`默认知识库`（ID: `dadd847d18b54162`，2.4 万切片）**：为**混合向量库**，适合语义联想。
2. **严禁全量拉取长文档**：笔记可能长达数万行，严禁一次性读取全文。务必遵循「**看大纲（`outline`） → 取行段（`read`）**」流程。
3. **不可采信未命中凑数结果**：当检索结果标明 `NoMatch: true` 时，切片为服务端兜底放行，内容与查询无关，严禁基于此编造答案。
4. **重排降级事实**：生产环境重排模型已平滑降级（`health: degraded`），排序以 RRF 融合分数为准，切勿依据重排绝对分进行硬过滤。

---

## 深入参考

- 完整 25 个 MCP 工具参数契约：[references/mcp-tools.md](references/mcp-tools.md)
- 物理知识库架构与特性说明：[references/knowledge-bases.md](references/knowledge-bases.md)
- REST API 完整端点参考：[references/api-reference.md](references/api-reference.md)
- 故障排查与未命中诊断手册：[references/troubleshooting.md](references/troubleshooting.md)


