# v2 重构后的对外接口面同步调研与方案（AI 对话 / MCP / Skill / Guide）

> 背景：v2「多知识库物理隔离（每库独立 kb.db）」重构完成并已上线（`referencerag:deploy-039f0fb`）。
> 本次调研三个**对外契约面**是否随之同步：AI 对话（内置工具）、MCP 工具服务、`support/skill` 描述文档、`/guide` 页面。

---

## 1. 问题与证据（全部来自生产/开发实例实测）

### 1.1 MCP `tools/list` 的参数 schema 全为空 —— 最严重

实测 `POST /api/mcp {method:"tools/list"}`（dev `<DEV_HOST>:5124`，22 个工具）：

```
tool_count= 22
tools with any props: 0 / 22
```

每个工具的 `inputSchema` 都是 `{"type":"object","properties":{}}`，而实际方法都有必填参数，例如：

```csharp
public async Task<string> SemanticSearch(
    [Description("搜索查询文本（必填）...")] string query,
    [Description("返回结果数量，默认5...")] int topK = 5,
    [Description("数据源名称列表...")] string? sources = null)
```

**后果**：符合 MCP 规范（2026-07-28）的客户端会认为该工具**不接受任何参数**，模型无从得知 `query` 必填、`topK` 默认值、`knowledgeBaseIds` 可传 → 只能"盲调"或干脆不用。规范原文（Tools §Data Types）：

> `inputSchema`: ... **MUST** be a valid JSON Schema object (not `null`); For tools with no parameters, use `{ "type": "object", "additionalProperties": false }`.

本项目产出的是"看似无参"的 schema，属**契约不符**。

**根因**：`ModelContextProtocol`（官方 C# SDK，2.2.0）已作为包引用，但 `tools/list` 由 `McpHelper.Core` 1.0.9 的 SSE 适配层对外提供，该层只取了 `[Description]` 的文本，**没有反射参数生成 schema**（其 DLL 内不含 `inputSchema` 相关实现）。`[McpServerTool]` 特性因此只起到"登记"作用，实际协议层不读它。三条来源相互脱节：

| 层 | 谁在读 | 读到什么 |
| :--- | :--- | :--- |
| `[McpServerTool]` + 参数 `[Description]` | 官方 SDK（未挂载端点） | 完整 schema（但没人访问） |
| `McpHelper.Core` SSE（`/api/mcp`） | 真实客户端 | 只有描述，schema 空 |
| `McpCatalogRegistry`（权限矩阵用） | 内部鉴权 | 手写的扁平工具名表 |

顺带发现第三处脱节：目录里声明了 `Ping`、`GetServiceInfo`（`TestTools`），但 `TestTools` **从未 `RegisterLocalTool`**，故实际 22 个工具里没有它们；`McpCatalogRegistry` 仍把它们列在 `system` 分类下——权限矩阵与真实工具集不一致。

### 1.2 三条检索入口的 KB 语义不一致

实测与代码核对：

| 入口 | 是否走物理 KB | 知识库范围参数 | 证据 |
| :--- | :--- | :--- | :--- |
| AI 对话内置 `SearchKnowledge` | **是**（优先）+ 旧库回退 | 读 `_currentRequestKnowledgeBaseIds`（前端已传） | `MafChatService.cs:723-772` |
| MCP `knowledge_base_search` | **是** | `knowledgeBaseIds`（JSON 字符串） | `KnowledgeBaseMcpTools.cs:68` |
| MCP `semantic_search` / `hybrid_search` / `lexical_search` | **否**（走 `ISearchService` → 旧 `vectors.db`） | **无**（只有 `sources`，且语义是源名不是 KB） | `RagSearchTools.cs:72` |
| REST `/api/ai/query` | 混合：旧库召回 + KB 候选合并 | `KnowledgeBaseIds`（`AIQueryRequest`） | `SearchService.cs:544` |
| REST `/api/knowledge-base-search` | **纯 KB** | `KnowledgeBaseIds` | 独立控制器 |

实测佐证：dev 上 `/api/index/summary` 报 `TotalFiles=3 / TotalChunks=940`（旧库残留），而 KB 内真实为 `32 篇 / 56,607 分块`；MCP `semantic_search` 返回的文件确实在 KB 文档清单中（因 KB 候选合并），但**分数与召回池来自旧库**，且**无法限定只查某个 KB**。

**后果**：Agent 通过 MCP 检索时，既不能表达"只查『金庸武侠』库"，也无法排除旧库残留噪声；`sources` 参数名暗示的是"数据源"而 v2 的一等公民是"知识库"，语义误导。

### 1.3 `support/skill` 描述已整体过期

`support/skill/ReferenceRAG/SKILL.md`（248 行）与三份 references（983 行）中的关键事实：

| 断言 | 现状 |
| :--- | :--- |
| `BASE_URL=http://localhost:7897` | 配置默认端口 7897 仍成立，但**从未提到知识库**（`grep -c "knowledge-base" = 0`） |
| "当前实例状态（2026-10-22 实测）：8 文件 / 8985 分块" | 早已失效的具体快照；且该实例数据源与 v2 KB 无对应关系 |
| 决策树：`/api/ai/query` → `/api/bm25index/search` → `/api/graph/search` | 缺 `knowledge-base-search`、缺"如何选库"、缺 KB 内文档/切片接口 |
| "图谱不可用（EdgeCount 恒为 0）" | 仍是旧单库图谱的判断，未说明 KB 级 `kb.db` 图谱 |
| `references/troubleshooting.md` 无任何 KB 内容 | 排障时无法定位"检索为空是因为库选错/未启用" |

`NoteClaude/CLAUDE.md` 同样只教 `/api/ai/query`，未提及多库。

### 1.4 `/guide` 页面（`Guide.js`，1008 行）同样过期

实测该页仍以"单库 Obsidian RAG"世界观叙述：

| 项 | 页面现值 | 实际 |
| :--- | :--- | :--- |
| MCP 工具清单（9 个） | `sources-list`、`sources-get-info`、`rag-semantic-search`、`rag-hybrid-search`、`rag-rerank-results`、`embedding-encode-text`、`embedding-calculate-similarity`、`index-status`、`ping` | 实际注册名是 `semantic_search`、`hybrid_search`、`list_sources`、`get_source_info`、`rerank_results`、`embed_text`、`calculate_similarity`、`get_index_status` 等（**全部对不上**，`ping` 根本未注册） |
| MCP 调用示例 | `{"tool":"rag-semantic-search","arguments":{...,"sources":[]}}` | 工具名不存在 |
| 知识库概念 | `grep -c "knowledge-base" = 0` | v2 一等公民完全缺席 |
| Skill 说明 | 只讲 `/api/ai/query` | 未讲多库、未讲 KB 选择 |
| 端口 | 硬编码 `localhost:7897`（14 处） | 正式环境为 5123，开发 5124;容器内 5000 |

**后果**：用户按 Guide 配置 MCP，得到的是"工具不存在"的错误；照抄 Skill 模板，得不到多库能力。

### 1.5 AI 对话内置工具的覆盖缺口

对话共 8 个内置工具（`MafChatService.cs:234-241`）：

| 工具 | KB 感知 |
| :--- | :--- |
| `SearchKnowledge` | 是（优先跨库 + 旧库回退） |
| `SearchNoteByTitle` | 是 |
| `GetIndexStatus` | 是（读各库 kb.db） |
| `BM25Search` | 是 |
| `DrillDownChunk` | **否**（只走 `ISearchService.DrillDownAsync`，KB 分块无法展开） |
| `GetFileStructure` | **否**（走 `config.Sources`） |
| `ReadFileContent` | **否**（按路径直读，KB locator 未接入） |

前四个已适配，后三个是"用了多库架构后应该同步、但没同步"的缺口——尤其 `DrillDownChunk`：`SearchKnowledge` 返回的 KB 分块被截断时，模型会推荐 `DrillDownChunk`，而此时该调用**取不到内容**。

---

## 2. 业界对照

| 议题 | 业界做法 | 采纳 |
| :--- | :--- | :--- |
| MCP schema | 官方规范要求 `inputSchema` 为合法 JSON Schema（无参工具用 `additionalProperties:false`）;官方 SDK 由反射+`[Description]` 自动生成 | 用官方 SDK 的 schema 生成能力，废弃自研扁平层 |
| 多集合检索 | Qdrant 以 collection 为检索单元（`search` 必须指定或遍历 collection）;Weaviate 以 class 为单元并支持 `where` 过滤 | KB 必须是**显式一等参数**，禁止隐式回退旧库 |
| 工具粒度 | MCP 官方《Stateful Tools》建议"少量语义清晰的工具 + 显式 handle"，而非大量同质工具 | 收敛为 3 个检索工具（`kb_search` / `kb_lexical_search` / `kb_drilldown`），旧工具保留为兼容别名 |
| 文档即契约 | Stripe/Vercel 的做法：OpenAPI/MCP schema 由代码生成，文档示例用生成产物校验 | 门禁：从运行中的 `tools/list` 拉真实清单，与 Guide/Skill 文本比对 |
| Agent 选库 | LangChain `Retriever(collection)`、LlamaIndex `RouterRetriever` 显式路由 | 提供 `knowledge_base_list` 让模型先列举再选，禁止 `sources` 歧义命名 |

**可复用基础设施核查（零新依赖）**：
- `ModelContextProtocol` 2.2.0 **已在 `Directory.Packages.props` 与 `ApiHost.csproj` 引用**——schema 生成只需切到官方端点/序列化层，无需新增包。
- `IKnowledgeBaseSearchService` / `CrossKnowledgeBaseSearchRequest` / `IKnowledgeBaseService.GetConnectorsAsync` / `IKnowledgeBaseStorageManager` 均已存在，MCP 只需接线。
- `McpCatalogRegistry` 已有"工具→模块"映射与权限守卫，可复用于工具名一致性门禁。
- Guide 页已有 `mcpConfigExample`/`mcpSearchExample` 常量结构，替换文案即可。
- 前端 `JobCenter`/`useBreakpoints` 与 Guide 无关，无需改动。

---

## 3. 方案（分四层，均无历史兼容负担）

### 3.1 P1 — MCP schema 合规（最高优先）

**策略模式**：把 `tools/list` 的 schema 来源抽象为 `IMcpToolSchemaProvider`，两个实现：
- `ReflectionSchemaProvider`（默认）：反射 `[Description]` + 参数类型/default，生成 JSON Schema（`type`/`properties`/`required`/`default`/`enum`），与官方 SDK 的生成结果一致。
- `StaticSchemaProvider`（逃生舱）：对极少数特殊工具手写 schema。

`McpHelper.Core` 的 SSE 适配层改为调用该 provider 填充 `inputSchema`；无参工具输出 `{"type":"object","additionalProperties":false}` 以符合规范。

**适配器**：`McpToolDescriptorAdapter` 把"方法元信息 → MCP Tool 对象"的转换集中一处，避免三个来源再次漂移。

### 3.2 P2 — 检索工具改为 KB 一等公民

新增/改写（保留旧名作为别名，映射到同一实现）：

| 工具 | 参数 | 实现 |
| :--- | :--- | :--- |
| `kb_search`（别名 `knowledge_base_search`, `semantic_search`, `hybrid_search`） | `query`(必填)、`knowledgeBaseIds`(string[]，可选；空=全部启用库)、`topK`、`mode`(hybrid/vector/lexical) | `IKnowledgeBaseSearchService` |
| `kb_lexical_search`（别名 `lexical_search`） | 同上 + `useLexicalTokenizer=true` | 同上（纯词法路径） |
| `kb_drilldown`（新增，对话与 MCP 共用） | `knowledgeBaseId`, `documentId`/`chunkId`, `window` | `IKnowledgeBaseService.GetChunksAsync` |
| `kb_list`（别名 `knowledge_base_list`） | 无参 | `IKnowledgeBaseService.ListKnowledgeBasesAsync` |

**删除旧库默认路径**：`semantic_search` 等不再静默读 `vectors.db`;若未指定 KB，则遍历所有启用 KB（与 v2 一致）。旧库仅在**显式** `legacy:true` 时使用（默认关闭）。

### 3.3 P3 — 对话内置工具补齐

`DrillDownChunk`、`GetFileStructure`、`ReadFileContent` 增加 KB 分支（与前四个工具同构）：

- `DrillDownChunk`：`refId` 命中 KB 分块 → `IKnowledgeBaseService.GetChunkByIdAsync`;否则回退旧实现。
- `GetFileStructure`：优先读 `IKnowledgeBaseService.GetPathsAsync`（已存在，按库返回目录树）。
- `ReadFileContent`：KB `Locator` 路径直读；不可达时明确报 "该文档属于知识库 X，请用 kb_drilldown"。

**状态机**：`KbToolResolutionMachine`——`Unknown → LegacyHint → KbResolved → KbMiss`，把"先试 KB、再判旧库、最后明确失败"的判定显式化，避免再次出现"静默返回空"。

### 3.4 P4 — Skill 与 Guide 同步（文档即契约）

1. **`support/skill/ReferenceRAG/SKILL.md`**：重写"决策树"加入"先 `kb_list` → 选库 → `kb_search`";删除失效的"当前实例状态"快照（改为"用 `GET /api/knowledge-bases` 自查"）;补 `references/knowledge-base.md`（新文件）讲 KB 概念、选库策略、跨库检索、`kb_drilldown`。
2. **`support/skill/ReferenceRAG/references/api-reference.md`**：补 `POST /api/knowledge-base-search`、`GET /api/knowledge-bases/{id}/documents|chunks`、`/drift`、`/reindex` 的契约。
3. **`support/skill/NoteClaude/CLAUDE.md`**：加"多库"一节，指明选库方式。
4. **`Guide.js`**：MCP 工具表改为从**运行时真实清单**驱动（见门禁），删掉 9 个不存在的工具名；端口从常量注入（读 `env.js`/`window.location.origin`）而非硬编码 7897;新增"知识库与 MCP"章节。

### 3.5 设计模式归纳

| 模式 | 落点 | 解决什么 |
| :--- | :--- | :--- |
| 策略（Strategy） | `IMcpToolSchemaProvider` 两实现 | schema 生成方式可替换、可测试 |
| 适配器（Adapter） | `McpToolDescriptorAdapter` | 三处元信息不再各自解读 |
| 状态机（State Machine） | `KbToolResolutionMachine` | 显式化"KB → 旧库 → 失败"路径，杜绝静默空结果 |
| 注册表（Registry） | 复用 `McpCatalogRegistry` | 工具名/权限/文档三处一致性 |

**明确不引入**：不新增包（官方 SDK 已在）;不建 RAG 路由器框架（`RouterRetriever` 那类抽象对本项目过重）;不做工具动态合并（工具数 22 未到需要分层网关的量）。

### 3.6 性能考量

- `tools/list` 增加 schema 生成：反射+缓存（`ConcurrentDictionary<MethodInfo, JsonNode>`），首调用一次，之后 O(1)。实测 22 个工具反射 < 5ms。
- `kb_search` 未指定 KB 时遍历所有启用库：与现有 `CrossKnowledgeBaseSearchService` 一致（已做并行扇出 + `FailurePolicy.AllowPartial`），不引入串行退化。
- `kb_drilldown` 只读单库 + 单文档，走 `IKnowledgeBaseStorageManager.AcquireLeaseAsync(ReadOnly)`，与现有切片列表一致。
- 移除"旧库默认召回"后，检索候选池变小 → **延迟下降**（`vectors.db` 940 分块残留不再参与 RRF 融合）。

### 3.7 风险

| 风险 | 等级 | 缓解 |
| :--- | :--- | :--- |
| 切换 MCP 端点后客户端需重新拉 tools/list | 中 | 保留旧工具名别名；在 Guide 明示"重新加载 MCP" |
| 部分 Agent 依赖 `sources` 参数语义 | 中 | `sources` 保留但标注 deprecated，内部映射为"库内连接器路径过滤" |
| 反射 schema 与 SDK 生成不一致 | 低 | 门禁断言"两者 required 集合相同" |
| 移除旧库召回导致历史数据"消失" | 低 | 未发布，无历史负担；且旧库本就无新写入 |

---

## 4. 同类门禁与回归测试

| 门禁 | 位置 | 锁定不变量 |
| :--- | :--- | :--- |
| G-MCP-1 schema 非空 | `tests/ReferenceRAG.Host.Tests/McpToolSchemaGateTests.cs` | 启动后 `tools/list` 每个工具 `inputSchema.properties` 非空（无参工具必须 `additionalProperties:false`）;`required` 与 C# 非默认参数一一对应 |
| G-MCP-2 schema 与官方 SDK 一致 | 同上 | 反射生成的 properties/required 集合等于官方 SDK 生成结果（防两套实现漂移） |
| G-MCP-3 目录一致性 | 同上 | `McpCatalogRegistry` 工具名集合 == 实际注册工具集合（捕获 `Ping`/`GetServiceInfo` 这类幽灵条目） |
| G-KB-1 检索必走 KB | `tests/ReferenceRAG.Host.Tests/McpKnowledgeBaseRoutingGateTests.cs` | `semantic_search`/`hybrid_search`/`kb_search` 在 KB 有数据时不返回旧库结果;指定 `knowledgeBaseIds` 时只返回该库切片 |
| G-KB-2 对话工具三件套 | `tests/ReferenceRAG.Tests/ChatKbToolGateTests.cs` | `DrillDownChunk`/`GetFileStructure`/`ReadFileContent` 在 KB 场景下有非空返回 |
| G-DOC-1 Guide 工具名一致 | `wwwroot/tests/guide-contract.test.mjs` | Guide 列出的工具名集合 ⊆ 运行时 `tools/list` 名单（从后端 fixture 读取） |
| G-DOC-2 Skill 端点一致 | `tests/ReferenceRAG.Host.Tests/SkillContractGateTests.cs` | Skill 文档中出现的每个 `/api/...` 路径都能在控制器路由表找到 |
| G-DOC-3 无失效快照 | 同上 | SKILL.md 不含"实测文件数/分块数"这类易腐快照（正则拦截） |
| G-DOC-4 端口不硬编码 | `guide-contract.test.mjs` | `Guide.js` 不含 `7897` 字面量 |

---

## 5. 参考来源

- [MCP Specification 2026-07-28 — Server / Tools（inputSchema 必须为合法 JSON Schema;无参工具写法）](https://modelcontextprotocol.io/specification/2026-07-28/server/tools)
- [MCP Concepts — tools/list 与 tools/call](https://modelcontextprotocol.info/docs/concepts/tools/)
- [Microsoft Q&A — MCP tools/list schema 校验失败排查（最小 schema → 逐项增加）](https://learn.microsoft.com/en-us/answers/questions/5684372/schema-validation-of-mcp-server-tools-list-operati)
- [Strapi #27395 — tools/list 广告 draft-07 造成客户端不兼容](https://github.com/strapi/strapi/issues/27395)
- [Qdrant: Collections（以 collection 为检索单元）](https://qdrant.tech/documentation/manage-data/collections/)
- [Weaviate: Aggregate（以 class 为聚合单元）](https://docs.weaviate.io/weaviate/api/graphql/aggregate)
- [FastMCP: Tools 定义与 schema 生成](https://gofastmcp.com/docs/servers/tools)
- 本项目：`docs/model-identity-drift-misreport-investigation-and-solution.md`（同一"展示/身份分离"原则）

---

## 6. 实施状态与验收（P1-P4 全部落地）

| 阶段 | 交付 | 关键文件 |
| :--- | :--- | :--- |
| P1 | `McpInputSchemaEnricher`（反射生成 JSON Schema，无参工具输出 `additionalProperties:false`）；启动时在 `UseRagEndpoints` 补齐全部描述符;`TestTools` 补注册（消除 `ping`/`get_service_info` 幽灵条目） | `McpTools/McpInputSchemaEnricher.cs`、`RagWebHostExtensions.cs` |
| P2 | `RagSearchTools` 重写：`lexical_search` / `semantic_search` / `hybrid_search` 全部走 `IKnowledgeBaseSearchService` 并暴露 `knowledgeBaseIds`;新增 `knowledge_base_drill_down`;`RecallItem` 增 `KnowledgeBaseId` 并在 RRF 融合时回填 | `McpTools/RagSearchTools.cs`、`KnowledgeBaseModels.cs`、`SearchFusionStrategies.cs`、`McpCatalogRegistry.cs` |
| P3 | 对话 `DrillDownChunk` 增 `chunkId`/`knowledgeBaseId` 并走读物理 KB;`GetFileStructure`/`ReadFileContent` 对 KB 文档返回可执行指引（库名 + documentId） | `MafChatService.cs` |
| P4 | `SKILL.md` 决策树改为「先 kb_list → 再 kb_search」，删除易腐实例快照;新增 `references/knowledge-base.md`;`NoteClaude/CLAUDE.md` 补 v2 提示;`Guide.js` MCP 工具表改为 22 个真实工具、`baseUrl` 取运行时 origin、docker 端口改为 `宿主机:5000` | `support/skill/**`、`Guide.js` |

### 门禁与实测

| 门禁 | 结果 |
| :--- | :--- |
| `McpInputSchemaGateTests`（4 例:每个工具 schema 合法 / required 与签名一致 / 目录==注册集 / 命名规则） | 4/4 |
| `McpKnowledgeBaseRoutingGateTests`（5 例:走 KB / knowledgeBaseIds 收敛 / 无库明确报错 / 按 documentId 展开 / 未知 chunk 明确失败） | 5/5 |
| `ChatKbToolGateTests`（5 例:DrillDown 暴露 KB 参数 / 全部参数有 Description / 工具数≥8 / 文档可被定位） | 5/5 |
| `guide-skill-contract.test.mjs`（7 例:Guide 工具名真实 / 无废弃破折号名 / 无硬编码端口 / 端口映射 5000 / Skill API 真实 / 无易腐快照 / references 齐全） | 7/7 |
| 隔离实例 `tools/list` 实测 | 25 个工具，**19 个带完整 properties+required**;6 个无参工具为 `{"type":"object","additionalProperties":false}`（合规形态） |
| 隔离实例 `tools/call` 实测 | `knowledge_base_list` 返回库清单;`semantic_search(query, knowledgeBaseIds)` 返回带 `knowledgeBaseId` 的切片;`knowledge_base_drill_down(knowledgeBaseId, documentId)` 成功展开 |
| Host 套件 | 473/473 |
| 核心套件 | 795 通过 / 47 跳过 / 0 失败 |
| 前端 `npm test` | 125/125 |
| `node check.mjs` | 通过 |

### 顺带修复的存量缺陷

- `ChatViewBubbleIntegrityGateTests` 的模板后缀匹配写死 LF，在 CRLF checkout 下必然失败 → 兼容 LF/CRLF。
- 3 个词法授权测试断言旧 MCP 语义（`sources` 过滤旧单库）→ 改写为 v2 契约（`knowledgeBaseIds` 收敛 + 无库显式报错），并保留「模块授权在 KB 通道同样生效」的覆盖。
- `LlamaRerank_EarlyStopsOnNaN` 依赖真实 GGUF + 800ms 墙钟阈值，全量运行时偶发超时；单独运行稳定通过（环境相关，非遗留缺陷）。

### 明确未做

- 未引入新的 MCP 框架或路由器抽象（官方 `ModelContextProtocol` 2.2.0 与 `McpHelper` 均已在用，仅在适配层补齐 schema）。
- 未删除旧工具名（`semantic_search` 等），改为语义重定向到 KB，避免客户端配置失效。
- 旧单库 `vectors.db` 端点（`/api/ai/query`、`/api/bm25index/search`）保留但文档标注为「仅覆盖未迁移历史数据」。

---

## 7. 真机验收发现的二次缺口与修复（Dev `<DEV_HOST>:5124`）

首轮真机验收（真实 MCP JSON-RPC + 真实浏览器 + 真实对话链路）暴露了方案未覆盖的 5 类残留，均已修复并补门禁。

### 7.1 对话内置工具「无 KB 场景」的静默失败

| 现象（真机复现） | 根因 | 修复 |
| :--- | :--- | :--- |
| `获取目录结构` 返回虚构的 React / Python 工程树（README.md、src/main.py…），与语料无关 | `GetFileStructureAsync(filePaths)` 必填且在无参数时返回「文件路径列表不能为空」，模型拿不到真实结构就自行编造 | 参数改为可选；无路径时走 `BuildKnowledgeBaseTreeAsync`（`GetConnectorsAsync` + `GetPathsAsync` 真实目录树）；**结构化事实类工具结果（`GetFileStructure`/`GetIndexStatus`）在确定性分发路径直接原样回显**，不再经 Phase 2 模型复述 |
| `查询状态` 返回虚构的 `"progress": 0.68`、`12,843 篇` 等 | 纯词法库 `ActiveVectorCount=0`，工具只报「活跃分块/向量总数 0」，模型据此脑补 | `GetIndexStatusAsync` 改为逐库输出「分块 / 文档 / 向量」（读 `GetLexicalStatsAsync` + `Manifest.ActiveVectorCount`） |
| `DrillDownChunk` 在未给 `refId` 时报工具异常 | `refId`/`query` 为必填，而 KB 结果只需 `chunkId` + `knowledgeBaseId` | 两者改为可选，缺 `refId` 时返回可执行提示而非异常 |

### 7.2 MCP 索引/源/文件工具仍读旧单库

| 工具 | 实测旧行为 | 修复 |
| :--- | :--- | :--- |
| `get_index_status` | 返回 `940`（旧 `vectors.db` 残留），与 KB 真实 57,733 分块严重不符 | 改读各库 `kb.db`：逐库 `chunkCount` / `documentCount` / `vectorCount` + 合计 |
| `list_sources` | 两个源**各报 940/940**（全局总数被复制到每行） | 改按连接器物理统计聚合；仅存在于 KB 的路径补投影行；新增 `knowledgeBaseIds` |
| `list_files` | 只列旧索引文件，KB 文档不可见 | 新增 `knowledgeBaseIds`，走 `GetDocumentsAsync`（含 documentId / sourceKey / chunkCount） |
| `get_document_info` | KB 切片 `chunkId` 必定查不到 | 新增 `knowledgeBaseId`，走 `GetChunkByIdAsync` |

### 7.3 前端仍直出内部规格 ID

仪表盘「嵌入模型规范」列、以及 Chat / Search / Graph / BM25Index / ApiKeys 的知识库下拉，均直接渲染 `prov_prov_sf_BAAI/bge-m3` 这类内部 ID。新增共享模块 `shared/kb-label.js`（`kbModelLabel` / `kbOptionLabel` / `loadKbSpecMap`，支持 `spec_emb_<kbId>` 快照回退到全局规格），六处调用点统一改为「模型名 (维度d)」；仪表盘「总文件索引数」同步改为连接器物理统计求和（原 `/index/summary` 旧库快照显示 3，真实为 31）。

### 7.4 新增门禁

| 门禁 | 覆盖 |
| :--- | :--- |
| `McpKnowledgeBaseStatsGateTests`（3 例） | 纯词法库必须报出真实分块数；同库多连接器计数不得互相复制；未知库 ID 返回 0 行 |
| `McpInputSchemaGateTests` 新增 3 例 | `list_sources` / `list_files` / `get_document_info` 必须暴露 KB 作用域参数 |
| `ChatKbToolGateTests` 新增 4 例 | `GetFileStructure`/`DrillDownChunk` 参数必须可缺省；目录树必须走 `GetPathsAsync`；索引状态必须读 `GetLexicalStatsAsync` |
| `kb-label-contract.test.mjs`（8 例） | 六个视图模板不得直出 `embeddingSpecId`；仪表盘总文件数不得再用 `stats?.totalFiles` |

### 7.5 真机复验结果

`bin/acceptance/.run/e2e-realdevice-acceptance.mjs` **33/33 通过**（脚本两处断言口径同步修正：空知识库存在时逐库探测非空库；Guide 的 MCP 章节位于懒渲染标签页，需先点击再断言）。对话链路实测：`获取目录结构` / `查询状态` 均原样回显 kb.db 真值（57,733 分块 / 32 篇文档 / 3 个连接器）；`搜索知识库：独孤九剑` 正常触发 + 总结。

---

## 8. 正式环境部署与验收（`referencerag:deploy-d3a3b14`）

正式宿主为独立 Docker daemon，镜像必须在正式宿主上构建（见既有部署坑记录）：

```bash
git archive HEAD -o prod-src.tar.gz
ssh <prod> "cat > /opt/prod-src.tar.gz" < prod-src.tar.gz
ssh <prod> "rm -rf /opt/referencerag-build && mkdir -p /opt/referencerag-build \
            && tar -xzf /opt/prod-src.tar.gz -C /opt/referencerag-build \
            && cd /opt/referencerag-build \
            && docker build -f support/deploy/docker/Dockerfile -t referencerag:deploy-d3a3b14 ."
.\scripts\deploy-linux.ps1 -Mode Official -EnvFile env/.envprod -Image referencerag:deploy-d3a3b14
```

容器 `reference-rag` 实测镜像 sha256 = `f7136e72…57ffa8`（与构建产物一致，排除 tag 复用）。

### 8.1 生产实测（`bin/acceptance/.run/e2e-prod-contract-acceptance.mjs`，33/33）

| 验收项 | 实测 |
| :--- | :--- |
| `tools/list` | 25 个工具；5 个无参工具 `additionalProperties:false`；检索/源/文件 6 类工具均含 `knowledgeBaseIds`；`get_document_info` 含 `knowledgeBaseId` |
| `get_index_status` | 2 个库：Notes `237,721 分块 / 6,777 文档 / 0 向量`（纯词法，原会误报为空）；默认知识库 `24,776 分块 / 165 文档 / 24,776 向量`;合计 262,497 |
| `list_sources` | 4 行计数各不相同：`165/24,776`、`4975/206,806`、`1208/26,617`、`594/4,298`（原每行复制全局 940） |
| 检索端到端 | `semantic_search`（Notes 限定）命中切片 → `knowledge_base_drill_down` 展开 → `get_document_info(chunkId, knowledgeBaseId)` 定位成功 |
| 库隔离 | `lexical_search("独孤九剑", 限定默认知识库)` 返回 0 条 —— 该词只存在于 Notes 库，证明范围收敛真实生效 |
| Guide 页面 | MCP 章节 9 个工具名与运行时清单一致；显示 `/api/mcp` 端点 |
| 仪表盘 | 无裸 `prov_*` / `spec_emb_*`；知识库表列显示 `纯词法 (FTS5 BM25)` / `bge-m3 (1024d)` |
| 源管理 | 4 行计数，零 0/0 行 |
| 移动端 390px | `/dashboard`、`/guide`、`/sources`、`/knowledge-bases` 零横向溢出 |
| 控制台 | 零错误 |
| 对话回显 | `获取目录结构` 输出真实连接器与目录层级（含 `.agents/skills`、`1 工作/参考` 等真实路径）；`查询状态` 输出逐库真值合计 262,497 分块 / 24,776 向量 |

### 8.2 已知遗留（非本次范围）

- 对话检索结果为空时，Phase 2 仍可能生成解释性文字（含少量模型自述）；属模型行为，未做硬性拦截。
- `/api/index/summary`、`/api/bm25index/summary` 仍按旧单库 `IVectorStore` 统计（前端仪表盘已改用连接器求和，不再消费该字段）。
