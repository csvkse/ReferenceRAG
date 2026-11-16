# MCP 服务面生产验证与契约加固调研（工具行为 / 性能 / 安全 / 设计模式）

> 背景：`referencerag:deploy-d3a3b14` 已上线。本轮**直接调用正式环境**的 25 个 MCP 工具（真实 SSE JSON-RPC + `X-API-Key`），而不只是校验 `tools/list` schema。
>
> 结论先行：**21 个工具行为正确，4 个存在真实缺陷**（2 个 P0：任意文件越权读取、工具结果被整段丢弃；2 个 P1：源解析不一致、统计口径矛盾），另有 2 处系统性契约缺口（失败无 `isError`、无 `annotations`/`outputSchema`）与 3 个性能热点（1.2–27.6 秒）。

---

## 1. 生产实测证据

### 1.1 全工具调用矩阵

| 工具 | 结果 | 耗时 | 说明 |
| :--- | :--- | :--- | :--- |
| `ping` / `get_service_info` / `knowledge_base_list` | OK | 4–46ms | — |
| `get_system_stats` | OK 但口径过时 | 42–50ms | `index.totalVectors=26,945`、`totalDocuments=0`、`sourcesCount=1`；与 `list_sources` 的 4 行 / `24,776·165` 矛盾 |
| `get_index_status` | OK（逐库真值） | **1,198–1,515ms** | Notes 237,721 分块 / 默认库 24,776 向量，正确 |
| `list_sources` | OK | **248–904ms**（首调 2,399ms） | 4 行计数各自独立：`165/24,776`、`4975/206,806`、`1208/26,617`、`594/4,298` |
| `list_files` | OK | 19–28ms | 走 kb.db，返回 documentId / locator / chunkCount |
| `knowledge_base_search` | OK | 822ms | — |
| `semantic_search` | OK | 124–252ms | `topK=900` 被静默钳到 50（595ms），响应未回显生效值 |
| `hybrid_search` / `lexical_search` | OK | 56–124ms | — |
| **`get_source_info`** | OK 但**口径错误** | **363–6,232ms** | `vectorCount=26,945`（旧库）、`documentCount=0`；与 `list_sources` 互斥 |
| `embed_text` | OK | 96–100ms | **40,000 字符输入 → 27,587ms**，无长度上限 |
| `embed_batch` / `calculate_similarity` | OK | 323 / 738ms | — |
| `rerank_results` | OK | 206–211ms | — |
| `graph_search_by_title` / `graph_search` | OK | 39–46ms | 图谱可为空属预期 |
| `find_files_by_pattern` / `search_file_content` | OK | 36–1,009ms | 能识别「KB 名称 - 目录」形式的连接器名 |
| **`get_file_tree`** | **FAIL** | 25ms | `未找到指定的数据源或无权访问: Notes - Note` —— 该名字正是 `list_sources` 刚给出的 |
| **`read_note_content`** | **结果被丢弃** | 6–407ms | `{"result":{"content":[]}}`，无任何文本块；与文件大小、行范围、路径均无关 |
| **`get_note_outline`** | OK 但**可越权读任意绝对路径** | 29–53ms | `filePath=/etc/passwd` → `success:true, sizeBytes:966`；`/app/data/appsettings.json` → `sizeBytes:8835` |

### 1.2 传输与协议面

```
initialize(protocolVersion=2026-07-28) → error -32022
        supported=[2024-11-05, 2025-03-26, 2025-06-18, 2025-11-25]
initialize(2025-11-25)               → 成功，但响应无 Mcp-Session-Id 头
Accept: application/json             → HTTP 406 "Client must accept both application/json and text/event-stream"
Accept: text/event-stream            → HTTP 406（同上）
Accept: application/json, text/...   → 200 text/event-stream
DELETE /api/mcp                      → 405（无会话终止语义）
tools/list 元素字段                   → 仅 name / description / inputSchema（无 title / annotations / outputSchema）
resources/prompts/completion/list      → -32601；logging/setLevel → -32603
```

### 1.3 失败信封与参数校验

| 场景 | 实际返回 |
| :--- | :--- |
| 工具内业务失败（`read_note_content` 不存在、`semantic_search` 未知库） | `result.content[0].text = {"success":false,"error":"…"}`，**`isError` 字段缺失** |
| 缺必填参数 `query` | `content[0].text = "缺少必需参数: query"`（纯文本，无结构化 code） |
| 类型错误 `topK:"abc"` | `"调用工具失败: An element of type 'String' cannot be converted to …"`（内部异常原文泄漏） |
| 未知工具 | `"工具 'no_such_tool' 不存在"` |
| 未知参数 `foo:1` | **静默忽略**，仍返回 `success:true` |
| `knowledgeBaseIds:"not-json"` | `success:false，"知识库检索失败: [not-json] 无法完成检索"` —— 串被当成库 ID |

---

## 2. 缺陷根因（逐条落到代码）

### 2.1 P0 — `read_note_content` 结果被整段丢弃

**现象**：任何路径、任意行范围都返回 `content: []`；同一文件的 `get_note_outline` 正常返回 1,301–11,694 字符。

**根因**：`KnowledgeFileTools.ReadNoteContent`（`Host/ReferenceRAG.ApiHost/McpTools/KnowledgeFileTools.cs:103`）返回的 JSON 里有一个顶层键 **`content`**：

```csharp
return JsonSerializer.Serialize(new {
    success = true, filePath, name, startLine, endLine,
    totalLines, returnedLines, hasMore,
    content = rangeResult.Content     // ← 与 MCP CallToolResult.content 同名
});
```

McpHelper 的适配层把工具返回的字符串当作 `CallToolResult` 反序列化（DLL 内可检索到 `ConvertResultToJsonNode` / `ParseToolResult` / `ToJsonString`），于是**工具自己的 `content` 字段被协议层的 `content` 数组覆盖**——字符串进 `JsonArray` 位置直接丢失，最终序列化成空数组。

**验证**：`get_document_info`（`IndexStatusTools.cs:150`）同样有 `chunk.content`，但它是**嵌套**在 `chunk` 对象里，所以不受影响；`get_note_outline` 无 `content` 键，也不受影响。三者的差异恰好只在「顶层是否叫 content」。

**性质**：这是**字段名与协议保留字段冲突**，不是偶发。任何未来新增的工具只要顶层返回 `content` 都会静默失效。同类风险键还有 `result`、`isError`、`structuredContent`、`_meta`。

### 2.2 P0 — `get_note_outline` 任意文件读取

**现象**：`filePath=/etc/passwd` → `success:true`，`sizeBytes:966`；`/app/data/appsettings.json` → `success:true`，`sizeBytes:8835`。

**根因**：`GetNoteOutline`（`KnowledgeFileTools.cs:187`）与 `ReadNoteContent` 同构：当 `sourceName` 为空时直接 `resolvedPath = FileBrowserPath.Normalize(filePath)`，**不做任何根目录包含性校验**：

```csharp
else { resolvedPath = FileBrowserPath.Normalize(filePath); }   // 无 IsWithin
```

只有给了 `sourceName` 的分支才做 `FileBrowserPath.IsWithin`（`Services/FileBrowserPath.cs:119`）。于是 MCP 客户端（任何持有 API Key 的 Agent）可以读取容器内任意文本文件——包括 `appsettings.json`（含 `REFERENCERAG_API_KEY` 等配置）。

**对照**：`read_note_content` 有同样的分支缺陷，只是结果被 2.1 吞掉而未暴露；`get_file_tree` / `search_file_content` 都强制走 source 沙箱。

> ⚠️ **修复顺序约束（重要）**：目前该越权只泄漏**元数据**（存在性 / 字节数 / 行数 / 标题），因为 `read_note_content` 的结果被 2.1 吞掉了。
> **一旦单独修复 F1（结果字段改名），同一个越权缺口立刻升级为"可读取任意文件全文"**——包括 `/app/data/appsettings.json` 里的 `REFERENCERAG_API_KEY`。
> 因此 **F2（沙箱）必须先于或与 F1 同批上线**，不能只修 F1。

### 2.3 P1 — 源解析两套口径

**现象**：`list_sources` 给出 `Notes - Note`；`find_files_by_pattern(sourceName="Notes - Note")` 能命中；`get_file_tree(sourceName="Notes - Note")` 报「未找到指定的数据源」。

**根因**：
- `ManagedFileSearchEngine.GetAllEffectiveSourcesAsync`（`Infrastructure/.../FileSystem/ManagedFileSearchEngine.cs`）**合并** `config.Sources` + 所有 KB 连接器，并把连接器命名为 `"{kb.Name} - {目录名}"`；
- `KnowledgeFileTools.GetFileTree`（`KnowledgeFileTools.cs:47`）**只读** `config.Sources`。

同一"源名"在两个工具里的可解析性相反。`get_source_info` 也是只读 `config.Sources`（`SourceManagementTools.cs:188`），因此对 `Notes - Note` 必然失败。

### 2.4 P1 — `get_source_info` / `get_system_stats` 统计口径仍是旧库

**现象**：`get_source_info(Temp)` → `vectorCount=26,945`、`documentCount=0`，而 `list_sources` 同一路径为 `165 / 24,776`。

**根因**：`list_sources` 已改为 per-KB 连接器快照（`BuildKnowledgeBaseSourceViewAsync`），但 `GetSourceInfo` / `GetSystemStats` 仍走 `IVectorStore.GetVectorStatsAsync()` / `IBM25Store.GetStatsAsync()`（`SourceManagementTools.cs:197,215,288`）。同一 MCP 会话里两个工具对同一实体给出互相矛盾的数字——这是比"数字偏小"更糟的一致性问题。

### 2.5 契约缺口 — 失败不可判别

所有工具失败都返回 `{"success":false,...}` 放在 `content[0].text`，协议层 `result` 里**没有 `isError:true`**。客户端只能解析 JSON 文本才知道失败；按 MCP 规范做错误处理的客户端（以及 UI 的"失败高亮/重试"）全部失效。缺 `annotations` 则客户端无法区分只读与危险操作（本项目 `McpCatalogRegistry` 里明明已有 `RiskLevel`，却没透出到协议层）。

---

### 2.6 P1 补充 — `list_files.sourceId` 名义与语义不符

`IndexStatusTools.ListFiles`（`:249`）在 KB 分支里把 `sourceId` 直接当成了**文档关键词**：

```csharp
var (docs, _) = await kbService.GetDocumentsAsync(kb.Id, page: 1, pageSize: limit, search: sourceId);
```

但参数描述写的是「旧索引的**数据源名称**（可选）」,而 `sourceKey` 是形如 `知识库/…/ECDICT-LICENSE.txt` 的路径。生产实测：`sourceId="知识库"` 返回 3 条（其实是 LIKE 命中路径），`sourceId="不存在的源"` 返回 0 条——都被当成 `success:true`，调用方无法分辨"过滤生效"还是"关键词没命中"。应改名为 `pathKeyword`（或拆成 `sourceName` + `pathKeyword` 两个参数）。

### 2.7 已确认**不存在**的缺陷（避免过度修复）

| 检查项 | 结论 |
| :--- | :--- |
| `search_file_content` 的 `subPath` 越界（`../../../../etc`） | **被沙箱挡住**，返回结果仍全部落在源根内（`matchedFilesCount=2`，扫描 52 个文件） |
| `get_file_tree` 的 `subPath` 越界 | `FileBrowserPath.IsWithin` 生效，返回"路径超出沙箱范围" |
| `topK` 上限 | 钳到 50，不放大 token 成本 |
| 未知参数 `foo` | 被忽略（对只读工具可接受；对写工具应收紧） |

---

## 3. 业界方案对照

| 议题 | 业界做法 | 本项目采纳 |
| :--- | :--- | :--- |
| 工具失败语义 | MCP 规范：`CallToolResult.isError=true` 表示工具执行失败（协议错误用 JSON-RPC error）。官方 C# SDK 的 `McpServerToolCreateOptions` 可自动生成 `OutputSchema` 并填充 `StructuredContent` | 适配层统一包装：业务失败 → `isError:true` + 文本；协议/参数错误 → JSON-RPC error |
| 多内容形态 | 规范区分 `content`（模型可见文本/图像）与 `structuredContent`（应用代码消费，按 `outputSchema` 校验） | 工具保留人类可读文本，另补 `structuredContent` 供客户端解析 |
| 工具风险标注 | `annotations.readOnlyHint / destructiveHint / idempotentHint`；`readOnlyHint:true` 可被客户端自动放行 | 由既有 `McpCatalogRegistry.RiskLevel` 派生注解，零新增人工维护 |
| 结果字段命名 | 各 SDK 都把 `content`/`isError`/`structuredContent` 作为保留字段 | 建立保留字段清单门禁，禁止工具顶层输出同名键 |
| 文件访问沙箱 | 以「注册根目录白名单」为准，路径规范化后做前缀包含校验（CWE-22 标准解法）；不允许客户端传任意绝对路径 | 所有 file 工具统一走 `FileBrowserPath.IsWithin`，绝对路径必须落在某个已注册源根内 |
| 源解析 | DjVu/Elastic 等把"集合（collection）"作为唯一可解析单元，不存在两套命名 | 抽 `ISourceCatalog` 作为唯一解析入口，`config.Sources` 与 KB 连接器合并后统一命名 |
| 参数校验 | 用 JSON Schema 在调用前校验（`additionalProperties:false`），拒绝未知字段 | 对写操作/高成本工具启用严格校验；`tools/list` 显式声明 `additionalProperties` |
| 长任务 | 大输入在工具边界做上限钳制并回显生效值 | `embed_text/embed_batch` 加字符上限 + 明确报错；`topK` 回显实际生效值 |

来源：
- [MCP Specification — Server / Tools](https://modelcontextprotocol.io/specification/draft/server/tools)
- [MCP Tools 概念（tools/list、tools/call、结果与失败）](https://modelcontextprotocol.info/docs/concepts/tools/)
- [MCP C# SDK — structured content 与 outputSchema（2025-06-18 更新）](https://devblogs.microsoft.com/dotnet/mcp-csharp-sdk-2025-06-18-update/)
- [McpServerToolCreateOptions（OutputSchema / StructuredContent）](https://csharp.sdk.modelcontextprotocol.io/api/ModelContextProtocol.Server.McpServerToolCreateOptions.html)
- [Tool Annotations as Risk Vocabulary](https://blog.modelcontextprotocol.io/posts/2026-03-16-tool-annotations/)
- [Testing MCP Tool Annotations](https://sunpeak.ai/blogs/testing-mcp-tool-annotations/)
- [MCP is Not the Problem, It's your Server: Best Practices](https://www.philschmid.de/mcp-best-practices)
- [Streamable HTTP 传输与会话](https://modelcontextprotocol.io/specification/2025-03-26/basic/transports)
- [MCP Goes Stateless](https://www.juststeveking.com/articles/mcp-goes-stateless/)

---

## 4. 可复用基础设施核查（零新增依赖）

| 需求 | 现有可复用件 | 位置 |
| :--- | :--- | :--- |
| 工具风险等级 / 归属模块 | `McpCatalogRegistry`（`RiskLevel` / `ModuleCode`，已是单一事实源） | `Infrastructure/.../Security/McpCatalogRegistry.cs` |
| 权限守卫 + 实时吊销 | `McpPermissionGuard`、`ApiKeyPermissionMatrix` | `Host/.../McpTools/McpPermissionGuard.cs` |
| 工具描述符就地改写 | `McpInputSchemaEnricher.Enrich`（已验证可在启动期改写 `InputSchema`） | `Host/.../McpTools/McpInputSchemaEnricher.cs` |
| 路径沙箱 | `FileBrowserPath.Normalize / Join / IsWithin` | `Host/.../Services/FileBrowserPath.cs` |
| 源合并解析（KB 连接器） | `ManagedFileSearchEngine.GetAllEffectiveSourcesAsync`（已实现合并，但私有） | `Infrastructure/.../FileSystem/ManagedFileSearchEngine.cs` |
| per-KB 连接器统计 | `IKnowledgeBaseService.GetConnectorStatsSnapshotAsync` / `GetConnectorsAsync` | `Business/.../KnowledgeBase/Services/KnowledgeBaseService.cs:537` |
| 逐库切片/文档统计 | `IKnowledgeBaseStore.GetLexicalStatsAsync`（`COUNT(*)` + `AVG(LENGTH(text))`） | `Infrastructure/.../Storage/SqliteKnowledgeBaseStore.cs:317` |
| 带 TTL/版本号的缓存 | `SourceLinkProjectionCache`（30s TTL + 版本号 + 单飞） | `Business/.../KnowledgeBase/Services/SourceLinkProjectionCache.cs` |
| 熔断 | `ResilienceCircuitBreaker`（阈值/半开/关闭三态） | `Infrastructure/.../Resilience/ResilienceCircuitBreaker.cs` |
| 官方 MCP SDK | `ModelContextProtocol` **2.2.0 已引用**；含 `structuredContent` / `outputSchema` / 会话/无状态 | `Directory.Packages.props:53` |

**结论**：P0/P1 修复均可零新增包完成；若要彻底换掉第三方适配层，`ModelContextProtocol.AspNetCore` 2.2.0 已在本地 NuGet 缓存中，可随时接入（见 §6 备选路径）。

---

## 5. 可行性与具体方案

### 5.1 P0 修复（必须先做）

**F1 结果字段保留字（Adapter + 门禁）**

1. `KnowledgeFileTools.ReadNoteContent` 把顶层 `content` 改名为 `text`（或 `body`），`get_note_outline` 无需改动。
2. 新增 `McpReservedResultFields` 常量：`content` / `result` / `isError` / `structuredContent` / `_meta`。
3. 门禁 `McpToolResultShapeGateTests`：反射遍历所有 `[McpServerTool]` 方法的返回匿名对象**无法反射**——改为**契约门禁**：用隔离实例真实 `tools/call` 每个工具的成功/失败路径，断言 `result.content` 非空且 `text` 可 JSON 解析。这样既抓保留字冲突，也抓"静默空结果"。

**F2 文件工具沙箱统一（Decorator）**

```
IFileAccessPolicy.EnsureAllowed(path, allowedRoots) → throw McpPathDeniedException
```
- 所有 file 工具（`get_file_tree` / `read_note_content` / `get_note_outline` / `search_file_content` / `find_files_by_pattern`）统一调用；
- 绝对路径必须落在**某个已注册源根**（`config.Sources` + KB 连接器）之内，否则 `success:false` + 明确提示"请先通过 list_sources 选择合法源"；
- 门禁 `McpFileSandboxGateTests`：`/etc/passwd`、`/app/data/appsettings.json`、`../../../etc/passwd`、`..%2F..` 全部必须被拒。

### 5.2 P1 修复

**F3 单一源解析（Registry + Adapter）**

新增 `ISourceCatalog`：
```csharp
Task<IReadOnlyList<SourceDescriptor>> ListAsync(ct);   // config.Sources ∪ KB 连接器，统一命名
bool TryResolve(string nameOrPath, out SourceDescriptor);
```
`get_file_tree` / `get_source_info` / `read_note_content(sourceName)` / `search_file_content` / `find_files_by_pattern` 全部改走它；`ManagedFileSearchEngine` 内部也改为委托同一实现，彻底消除两套口径。门禁：`list_sources` 返回的每个 `name` 必须能被其余 4 个工具解析（参数化遍历）。

**F4 统计口径收敛**

`get_source_info` / `get_system_stats` 的 `vectorCount`/`documentCount` 改取 per-KB 快照（复用 `GetConnectorStatsSnapshotAsync`），并复用 `SourceLinkProjectionCache` 做 30s 缓存。门禁：同一路径在 `list_sources` 与 `get_source_info` 的 `fileCount/chunkCount` 必须相等。

**F5 失败语义（Decorator）**

`McpToolResultDecorator`：捕获 `McpPermissionGuard` 之外的业务失败 → 返回 `isError:true`；参数解析失败 → JSON-RPC error（-32602 + 字段名）。门禁：对每个工具构造一次失败调用，断言 `isError===true`。

**F6 注解透出**

`McpInputSchemaEnricher` 扩展为 `McpDescriptorEnricher`，把 `McpCatalogRegistry.RiskLevel` 映射成 `annotations`（`Read`→`readOnlyHint:true`；`Execute`→`destructiveHint:false`+`idempotentHint`；`Dangerous`→`destructiveHint:true`）。门禁：25 个工具全部有 `annotations`，且与 registry 一致。

### 5.3 性能

| 热点 | 实测 | 方案 |
| :--- | :--- | :--- |
| `get_index_status` 1.2–1.5s | 每库都做 `GetLexicalStatsAsync`（`COUNT(*)` + `AVG(LENGTH(text))` 全表扫 237k 行） | 用 `SourceLinkProjectionCache` 同款 30s TTL 缓存；或改读 manifest 里已维护的计数（避免 `AVG`） |
| `list_sources` 240–2,400ms | 逐连接器 `GetConnectorStatsAsync`（每连接器 2 个子查询） | 单库一次 `GROUP BY connector_id` 聚合查询（把 N 次往返压成 1 次） |
| `embed_text` 40k 字符 → 27.6s | 无长度上限，模型先截断再推理 | 边界处按 token 上限钳制并显式报错（>8k 字符拒绝或分片） |
| `get_source_info` 6.2s 峰值 | 逐文件 `Directory.EnumerateFiles` + `FileInfo.Length` 全树遍历 | 复用 per-KB `documentCount`，物理文件数只做一次限深枚举或缓存 |

### 5.4 设计模式落点

| 模式 | 落点 | 解决什么 |
| :--- | :--- | :--- |
| **Adapter** | `McpToolResultDecorator` + `McpDescriptorEnricher` | 把「业务结果/静态注册表」适配成 MCP 协议形态（content/isError/annotations），一处收口 |
| **Strategy** | `ISourceCatalog`（合并解析）/ `IFileAccessPolicy`（沙箱策略） | 源解析与访问控制在两个工具族里各自可替换、可测试 |
| **State Machine** | `McpToolInvocationStates`：`Resolved → Authorized → Executed → Succeeded/Failed` | 显式化"参数解析→权限→执行→信封"四段，杜绝"业务失败伪装成成功" |
| **Registry** | 复用 `McpCatalogRegistry` 作为工具→模块→风险等级唯一事实源 | 注解、权限、文档三处不再漂移 |
| **Decorator** | 缓存装饰在 `get_index_status`/`list_sources` 的统计读模型上 | 热点读路径不与写路径耦合 |

**明确不引入**：不自研 MCP 协议层（`ModelContextProtocol` 2.2.0 已在引用）；不建通用"工具网关"框架（25 个工具未到需要分层的量）。

---

## 6. 备选路径：切到官方 SDK（评估结论：暂不切换）

| 维度 | McpHelper 1.0.9（现状） | ModelContextProtocol.AspNetCore 2.2.0 |
| :--- | :--- | :--- |
| inputSchema | 只读描述，需本项目反射补齐 | 自动生成（含 `outputSchema`） |
| 结果信封 | 手工字符串，字段名冲突即静默丢结果（本次 P0） | `CallToolResult` 强类型，`structuredContent` 有位置 |
| 会话 | 无 `Mcp-Session-Id`，`DELETE` 405 | 原生支持 Streamable HTTP + 会话/无状态 |
| 响应协商 | 强制 SSE（`Accept: application/json` 被 406） | 支持 JSON 响应模式 |
| 迁移成本 | — | 端点 `/api/mcp` 不变；需重写 registry 注册、重新验证 25 个工具的权限矩阵与 SSE 事件流 |

**结论**：本次先做 Adapter 层收口（F1/F5/F6），把 P0 修掉；官方 SDK 切换作为**独立后续任务**，因为它会改动传输层与既有 9 份文档中的调用示例，风险面大于收益。

---

## 7. 同类门禁与回归测试

| 门禁 | 位置 | 锁定不变量 |
| :--- | :--- | :--- |
| G-MCP-4 结果非空 | `McpToolResultShapeGateTests` | 每个工具成功路径 `content[0].text` 非空且可 JSON 解析；失败路径 `isError===true` |
| G-MCP-5 保留字段 | 同上 | 工具顶层禁止输出 `content`/`result`/`isError`/`structuredContent`/`_meta`（源文件静态扫描 + 运行期断言双保险） |
| G-MCP-6 文件沙箱 | `McpFileSandboxGateTests` | `/etc/passwd`、`/app/data/appsettings.json`、`../` 越界、`..%2F` 编码越界全部拒绝 |
| G-MCP-7 源解析一致 | `McpSourceResolutionParityGateTests` | `list_sources` 每个名字可被 `get_file_tree` / `get_source_info` / `find_files_by_pattern` / `search_file_content` 解析 |
| G-MCP-8 口径一致 | `McpStatsConsistencyGateTests` | 同路径在 `list_sources` 与 `get_source_info` 的 `fileCount/chunkCount` 相等；`get_system_stats` 与 `get_index_status` 的 totals 相等 |
| G-MCP-9 注解完整 | `McpInputSchemaGateTests` 扩展 | 25 个工具均有 `annotations`，且 `readOnlyHint` 与 registry `RiskLevel` 一致 |
| G-MCP-10 参数边界 | `McpArgumentBoundaryGateTests` | `topK` 回显生效值；`embed_text` 超长输入被拒；未知参数在严格工具上被拒 |

---

## 8. 风险与优先级

| # | 项 | 等级 | 影响面 | 建议 |
| :-- | :--- | :--- | :--- | :--- |
| F2 | 文件工具任意绝对路径读取 | **P0 安全** | 持有任一 API Key 的 Agent 可读容器内任意文本（含 `appsettings.json`） | 立即修 |
| F1 | `read_note_content` 结果被吞 | **P0 功能** | 工具完全不可用，模型只能凭空作答 | 立即修 |
| F3 | 源解析两套口径 | P1 | `get_file_tree` 对连接器名失效；Agent 无法按 `list_sources` 结果继续操作 | 本周 |
| F4 | 统计口径矛盾 | P1 | 同会话内两个工具给出互斥数字，Agent 与用户都失去信任 | 本周 |
| F5 | 失败无 `isError` | P1 契约 | 客户端无法按规范识别失败 | 本周 |
| F6 | 无 `annotations` | P2 | 客户端无法自动放行只读工具、无法对危险操作弹确认 | 次批 |
| 性能 | 索引/源统计 1.2–2.4s、`embed_text` 27.6s | P2 | 首字节慢、Agent 超时 | 次批（缓存 + 边界钳制） |

**不建议做的**：给 `read_note_content` 加内容脱敏（它读的是用户自己的知识库）；把 `topK` 上限从 50 提高（会放大 token 成本）。

---

## 9. 实施状态（F1–F5 已落地，F6 部分受适配层限制）

> 开发环境（`<dev-host>:5124`，`referencerag-dev`）逐项实测；随后按同一契约部署正式环境复验。

### 9.1 交付清单

| 项 | 交付 | 文件 |
| :--- | :--- | :--- |
| F2 沙箱 | 新增 `IFileAccessPolicy` / `SourceScopedFileAccessPolicy`；`get_file_tree` / `read_note_content` / `get_note_outline` / `search_file_content` / `find_files_by_pattern` 全部经其解析；绝对路径必须落在已注册源根内 | `Host/.../McpTools/SourceCatalog.cs`、`KnowledgeFileTools.cs` |
| F1 保留字段 | `read_note_content` 的 `content` 改名 `text`；新增 `McpResult.Sanitize`（把工具顶层保留字段迁到 `payload` 下兜底）；门禁静态扫描禁止顶层 `content` | `KnowledgeFileTools.cs`、`McpTools/McpResult.cs` |
| F3 源解析 | 新增 `ISourceCatalog`（config.Sources ∪ KB 连接器，连接器命名 `{KB} - {目录}`），文件类工具与 `get_source_info` 统一调用 | `SourceCatalog.cs`、`KnowledgeFileTools.cs`、`SourceManagementTools.cs` |
| F4 口径 | `get_source_info` / `get_system_stats` 改读 per-KB 连接器快照与逐库索引统计，与 `list_sources` 一致 | `SourceManagementTools.cs` |
| F5 失败语义 | 新增 `McpResult.Fail`（`{"content":[{"type":"text"...}],"isError":true}`）；所有工具成功结果经 `McpResult.Ok`（中文不转义，避免 `\uXXXX` 膨胀上下文） | `McpResult.cs` + 全部 `McpTools/*.cs` |
| 性能 | 新增 `IMcpStatsCache`（30s TTL + 单飞）覆盖索引统计与连接器统计；`embed_text`/`embed_batch` 8,000 字符上限；检索回显 `requestedTopK` / `effectiveTopK` | `McpStatsCache.cs`、`IndexStatusTools.cs`、`SourceManagementTools.cs`、`EmbeddingTools.cs`、`RagSearchTools.cs` |
| F6 注解 | **受适配层阻断**：McpHelper（1.0.9 与 2.0.1 均）DLL 内无 `annotations` / `readOnlyHint`，`tools/list` 元素只有 `name/description/inputSchema`，`[McpServerTool(ReadOnly=true)]` 不会透出。替代路径：`McpCatalogRegistry.RiskLevel` 已在前端 API Key 授权矩阵呈现；协议级注解需切换到官方 `ModelContextProtocol.AspNetCore`（独立任务） | — |

### 9.2 开发环境实测（`referencerag-dev`）

| 验收项 | 修复前 | 修复后 |
| :--- | :--- | :--- |
| `get_note_outline(/etc/passwd)` | `success:true, sizeBytes:966` | `isError:true` + `path_denied`「路径不在任何已注册数据源内」 |
| `get_note_outline(/app/data/appsettings.json)` | `success:true, sizeBytes:8835` | `isError:true` + `path_denied` |
| `read_note_content(真实 .md, 1-5 行)` | `{"result":{"content":[]}}`（空） | `text` 字段返回真实正文 |
| `list_sources` 三个源名 | `get_file_tree('金庸武侠测试知识库 - full')` → FAIL | 三个名字全部 `ok`（`get_source_info` 亦然） |
| `list_sources` / `get_source_info` 同路径 | `1/46` vs `26,945/0`（旧库） | 两者均 `1/46` |
| 业务失败信封 | `success:false` 纯文本 | `isError:true` + `errorCode`（`path_denied` / `source_not_found` / `too_large`） |
| `get_index_status` | 1,198–1,515ms | **16–17ms**（30s 缓存命中） |
| `list_sources` | 248–2,399ms | **25–35ms** |
| `get_system_stats` | 42–50ms | **8–9ms** |
| `embed_text(40,000 字符)` | 27,587ms 并返回成功 | `isError:true` + `too_large`（上限 8,000） |
| `semantic_search(topK=900)` | 静默钳到 50 | 回显 `requestedTopK:900` / `effectiveTopK:50` |

### 9.3 新增门禁

`tests/ReferenceRAG.Host.Tests/McpToolContractHardeningGateTests.cs`（10 例）：

| 门禁 | 锁定不变量 |
| :--- | :--- |
| `Fail_Envelope_IsProtocolDetectable` | 失败必须是 `isError:true` 信封，内层含 `success:false` + `error` + `errorCode` |
| `Ok_Envelope_RelocatesReservedTopLevelFields` | 顶层 `content` 必须被迁到 `payload`，业务字段不受影响 |
| `ToolSources_MustNotReturnTopLevelContentOrIsError` | 静态扫描：任何工具不得以顶层 `content` 返回 |
| `FileAccessPolicy_RejectsAbsolutePathsOutsideRegisteredSources`（3 组） | `/etc/passwd`、`/app/data/appsettings.json`、`/tmp/../etc/passwd` 全部拒绝 |
| `FileAccessPolicy_AllowsPathsInsideRegisteredSource` | 合法源内路径正常通过（避免修成"全拒"） |
| `FileAccessPolicy_RejectsRelativeTraversalInsideSource` | `../../etc/passwd` 在源沙箱内也被拒 |
| `SourceCatalog_ResolvesKnowledgeBaseConnectorNames` | 连接器名与路径可互相解析 |
| `ListSourcesAndGetSourceInfo_AgreeOnSamePath` | 两工具对同一路径的文档数/分块数必须相等 |

既有门禁同步更新口径（`McpKnowledgeBaseRoutingGateTests` / `LexicalAuthorizationTests` / `LexicalRegressionHostTests`）：从断言裸 `success:false` 改为通过 `McpToolResult.Parse` 断言 `isError`。

### 9.4 套件结果

| 套件 | 结果 |
| :--- | :--- |
| Host | **489/489** |
| 核心 | **799 通过 / 47 跳过 / 0 失败** |

### 9.5 正式环境复验（`referencerag:deploy-fe58970`）

生产宿主独立构建（`docker build` → sha256 `d6b68798…9687aa`），容器 `reference-rag` healthy。

| 验收项 | 实测 |
| :--- | :--- |
| `get_note_outline(/etc/passwd)` | `isError` + `path_denied`（修复前 `success:true, 966B`） |
| `get_note_outline(/app/data/appsettings.json)` | `isError` + `path_denied`（修复前 `success:true, 8835B`） |
| `get_note_outline(/tmp/../etc/passwd)` | `isError`（点段穿越同样被规范化后拒绝） |
| `read_note_content(真实 locator, 1-3 行)` | `ok`，`text` 返回 333 字符真实正文（修复前恒为空） |
| 4 个源名 × 2 工具 | `Temp` / `Notes - Note` / `Notes - 【VNote】` / `Notes - TNote` 全部 `tree=ok info=ok`（修复前 `get_file_tree` 对连接器名 FAIL） |
| 口径一致 | `Temp`：`list_sources 165/24,776` 与 `get_source_info 165/24,776` 完全一致（修复前 `26,945/0`） |
| 失败语义 | 不存在的文件 / 未知库均返回 `isError:true` |
| 性能 | `get_index_status` **16–25ms**（原 1.2–1.5s）、`list_sources` **68–71ms**（原 240–2,400ms）、`get_system_stats` **6–30ms** |
| 输入边界 | `embed_text(40,000 字符)` → `isError` + `too_large`;`topK=900` → 回显 `requestedTopK=900 / effectiveTopK=50` |

### 9.6 明确未做

- **协议级 `annotations`**：McpHelper 1.0.9 与 2.0.1 的 `tools/list` 元素字段均为 `name/description/inputSchema`，DLL 内无 `annotations` / `readOnlyHint` 字符串，`[McpServerTool(ReadOnly=true)]` 不会透出。要补齐必须切到官方 `ModelContextProtocol.AspNetCore` 2.2.0（已在本机 NuGet 缓存中），那会改动传输层与 9 处既有文档示例，单独立项。
- **`Accept: application/json` 单值协商**：McpHelper SSE 层强制要求同时接受 `application/json` 与 `text/event-stream`，否则 406。属适配层行为，未改。
- **`list_files.sourceId` 语义**：参数仍按「旧索引源名 / KB 路径关键词」双重语义，未拆分。已在 §2.6 记录，待与产品确认命名后再改。
- **会话（`Mcp-Session-Id`）与 `DELETE /api/mcp`**：适配层无会话语义，仍为无状态 SSE。对无状态用法无害。
