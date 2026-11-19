---
name: ReferenceRAG
description: "本地多知识库语义检索服务。从 Obsidian 笔记、技术文档库中检索架构设计、源码剖析、配置说明与最佳实践。触发规则：(1)强制触发：rag:关键词、/rag 关键词 (2)意图触发：查询知识库/笔记、查阅相关技术文档 (3)自然提问：笔记里有没有写过、帮我找下相关配置。NOT for: 天气、新闻、实时股票等外部互联网信息。"
allowed-tools:
  - Read
  - Bash
---

# ReferenceRAG 知识库检索服务

支持物理隔离多知识库的混合检索（向量 + BM25 + RRF 融合）、目录树浏览、正文行切片读取、Wiki-links 关系图谱与自主智能问答。

```
服务地址：http://127.0.0.1:5123
鉴权凭据：X-API-Key: rag_live_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx
```

---

## ⚡ 三轨调用方式（优先 MCP 原生工具，CLI 脚本保底）

### 轨道 1：原生 MCP 工具调用（若环境已注册 ReferenceRAG MCP，最高优先）
服务共暴露 **26 个强类型 MCP 工具**（JSON-RPC 2.0），常用核心工具：
- `knowledge_search`：**统一检索主力工具（首选）**。支持 `mode: "auto"` (默认智能路由)、`"hybrid"` (向量+词法融合)、`"semantic"` (语义召回)、`"lexical"` (精确词法)。可指定 `knowledgeBaseIds`。
- `knowledge_base_list`：列出当前 Key 授权的物理隔离知识库清单（ID 与模式）。
- `knowledge_base_drill_down`：检索结果切片被截断时，按 `knowledgeBaseId` + `chunkId`/`documentId` 精确展开完整切片正文。
- `get_note_outline` / `read_note_content`：针对长文档，先看大纲（H1-H6 标题树），再按行范围精准提取。
- `get_file_tree`：逐层浏览指定数据源或子目录的文件与目录树结构。
- `search_file_content`：ripgrep 风格的磁盘文件正文正则/文本快速检索（带匹配行号与上下文）。
- `graph_search` / `graph_search_by_title`：Wiki-links / 双链与标签关系图谱遍历与节点模糊检索。
- `lexical_search` / `semantic_search` / `hybrid_search`：经典检索工具兼容别名（内部自动路由至 `knowledge_search`）。

### 轨道 2：CLI 脚本门面调用（跨平台安全保底）
在终端下直接运行内置 Python 门面（零外部依赖，跨平台安全降级，建议附带 `-X utf8`）：

```bash
# 1. 统一知识库检索 (默认走权威 MCP 通道，支持 auto/hybrid/semantic/lexical)
python -X utf8 <skill-dir>/scripts/rag_cli.py search "工作流状态机设计" --mode auto --top-k 5

# 2. 精简输出模式 (--format brief，大幅缩减上下文 Token 预算)
python -X utf8 <skill-dir>/scripts/rag_cli.py search "依赖注入生命周期" --format brief

# 3. 检索质量策略 (--policy exact|concept|balanced，默认 balanced)
# exact: 纯词法严格匹配无凑底; concept: 语义概念保召回; balanced: 智能折中
python -X utf8 <skill-dir>/scripts/rag_cli.py search "IndexOutOfRangeException" --policy exact

# 4. 指定知识库检索 (例如限定 Notes 纯词法库: 40745ffb28444648)
python -X utf8 <skill-dir>/scripts/rag_cli.py search "状态机设计" --kb 40745ffb28444648

# 5. 精确切片下钻 (当搜索切片正文截断时，按 chunkId 展开全文，无须物理文件路径)
python -X utf8 <skill-dir>/scripts/rag_cli.py chunk <chunkId> [--kb <kbId>]

# 6. 查看大纲与按行读取长文档
python -X utf8 <skill-dir>/scripts/rag_cli.py outline "知识库/PR含义.md" --source "Temp"
python -X utf8 <skill-dir>/scripts/rag_cli.py read "知识库/PR含义.md" --source "Temp" --start-line 1 --end-line 30

# 7. 目录树层级浏览 (tree) 与磁盘正文正则检索 (grep)
python -X utf8 <skill-dir>/scripts/rag_cli.py tree "知识库" --source "Temp"
python -X utf8 <skill-dir>/scripts/rag_cli.py grep "Pull Request" --source "Temp" --limit 10

# 8. 文档与切片元数据查询 (doc-info)
python -X utf8 <skill-dir>/scripts/rag_cli.py doc-info --chunk <chunkId>

# 9. 知识图谱遍历与关系检索 (graph)
python -X utf8 <skill-dir>/scripts/rag_cli.py graph "#authors-note" --depth 1
python -X utf8 <skill-dir>/scripts/rag_cli.py graph "架构设计" --by-title

# 10. 查看后台任务与系统健康度 (status / stats / list-kbs / list-sources)
python -X utf8 <skill-dir>/scripts/rag_cli.py status
python -X utf8 <skill-dir>/scripts/rag_cli.py stats
python -X utf8 <skill-dir>/scripts/rag_cli.py list-kbs
python -X utf8 <skill-dir>/scripts/rag_cli.py list-sources

# 11. 自主智能问答 (ask，调用 OpenAI 兼容规范的 /v1/chat/completions)
python -X utf8 <skill-dir>/scripts/rag_cli.py ask "什么是 ReferenceRAG 的核心定位？"
```

### CLI 退出码规范（自动化脚本与 Agent 可靠编排）
| 退出码 | 标识 | 说明 | 处理指引 |
|:---|:---|:---|:---|
| **`0`** | `EXIT_OK` | 成功执行并命中可用内容 | 正常解析正文或结构化 JSON |
| **`2`** | `EXIT_USAGE` | 参数缺失或数值边界超限 (如 `start > end`) | 检查传入参数规格 |
| **`3`** | `EXIT_NO_MATCH` | 检索未命中 (NoMatch 判定) | 提取专有名词改写查询或切换 `--mode lexical` 重试 |
| **`4`** | `EXIT_NOT_FOUND` | 文件/切片不存在或物理源未挂载 | 检查源挂载状态或改用 `chunk` 展开 |
| **`5`** | `EXIT_AUTH` | API Key 鉴权失败或无权限访问该知识库 | 检查密钥配置与授权范围 |
| **`6`** | `EXIT_NETWORK` | 网络不可达或 HTTP 请求超时 | 自动指数退避重试或检查服务端口 |
| **`7`** | `EXIT_SERVER` | 服务端内部异常或 MCP 协议错误 | 检查服务端日志或运行 `status` 查看健康度 |

### 轨道 3：底层 REST API 映射（自动化流水线或无 CLI 环境）
- 跨库通用检索：`POST /api/knowledge-base-search`
- 知识库清单与状态：`GET /api/knowledge-bases`
- 数据源与长文按行读取：`GET /api/sources`, `POST /api/sources/file/lines`, `GET /api/sources/files/info`
- 异步作业队列与状态：`GET /api/jobs`
- OpenAI 兼容自主问答：`POST /v1/chat/completions` (模型名 `reference-rag`)

---

## 🧭 Agent 决策与执行状态机

```
[用户提问]
   │
   ▼ 提取核心概念词 (同义词、中英混排，去口语化助词)
[策略路由]
   ├── 查代码/错误码/API/配置项 ──> 词法优先策略 (lexical 或 --policy exact)
   └── 查架构设计/教程/原理     ──> 混合融合策略 (auto/hybrid 或 --policy concept)
   │
   ▼
[执行检索]
   │
   ▼ 检查响应中的 NoMatch 信号 (退出码 3 或 NoMatch: true)
[质量门禁]
   ├── NoMatch == true / Exit 3 ──> 判定为未命中。提取核心专有名词改写重试 1 次；若仍未命中则诚实告知用户未收录。
   └── NoMatch == false / Exit 0 ──> 获取命中切片正文 (Text)
   │
   ▼
[按需下钻]
   ├── 切片内容足够 ──> 直接采信
   └── 切片内容截断 ──> (首选) 调用 chunk 直接展开完整切片正文 (无须本地文件路径)
                     └── (长文档通读) 调用 outline 查看结构，再调用 read 精确提取目标章节
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
3. **未挂载物理源防护**：若某个源物理目录未挂载（`fileCount == 0`，如 `Notes - TNote`），文件级读取命令（`read`/`outline`）会返回 Exit 4；此时请直接使用 `chunk <chunkId>` 从 SQLite 事实源展开正文。
4. **不可采信未命中凑数结果**：当检索结果标明 `NoMatch: true` 或返回 Exit 3 时，切片为服务端兜底放行，内容与查询无关，严禁基于此编造答案。
5. **重排降级事实**：生产环境重排模型已平滑降级（`health: degraded`），排序以 RRF 融合分数为准，切勿依据重排绝对分进行硬过滤。

---

## 深入参考

- 完整 26 个 MCP 工具参数契约：[references/mcp-tools.md](references/mcp-tools.md)
- 物理知识库架构与特性说明：[references/knowledge-bases.md](references/knowledge-bases.md)
- REST 与 OpenAI 兼容端点参考：[references/api-reference.md](references/api-reference.md)
- 故障排查与未命中诊断手册：[references/troubleshooting.md](references/troubleshooting.md)
- 架构优化调研报告与评测分析：[docs/2026-11-18-reference-rag-optimization-research.md](docs/2026-11-18-reference-rag-optimization-research.md)
