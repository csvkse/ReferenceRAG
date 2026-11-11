# 知识库路径过滤孤岛与路由地址脱节根因调研、业界方案对比与全栈贯通方案

> **文档状态**：调研完成 & 落地执行指南  
> **涉及核心模块**：`Search.js`、`lexical-search-template.js`、`api/index.js`、`KnowledgeBasesController`、`SourcesController`、`KnowledgeBaseSearchService`、`SqliteKnowledgeBaseStore`、`IKnowledgeBaseStore`  
> **核心目标**：彻底解决多知识库检索场景下“路径过滤仅有 2 个旧全局源路径”、“无法展示使用中知识库的路由地址/挂载目录”、“勾选路径检索抛出阻断异常”的缺陷，实现知识库连接器路由与目录树的动态联动与毫秒级前置精准过滤。

---

## 一、现象与问题本质（Problem Statement）

### 1.1 现象还原
用户在前端 `/search` 视图中选中了特定的知识库：
- **当前选中的知识库**：`金庸武侠测试知识库 (spec_lexical_only)`
- 展开“高级”检索选项后，“路径过滤”树形组件中仅出现两个路径：
  - ▸ `jinyong_smoke`
  - ▸ `jinyong_core`
- **问题 1**：为什么只有这两个路径？为什么不是当前正在使用的“金庸武侠测试知识库”的路由地址（如该知识库所挂载的数据连接器 TargetPath，或者包含的三联版、新修版、旧版等 32 部全集目录）？
- **问题 2**：如果用户尝试在“路径过滤”中勾选了其中某项并点击搜索，前端直接报错：
  `知识库检索不支持源或路径筛选，请先清除这些筛选条件`。

---

## 二、代码与系统全链路根因溯源（Root Cause Analysis）

通过对前端 Vue 组件生命周期、HTTP API 接口定义以及后端 SQLite 存储引擎的逐行审查，确认问题由以下四层架构脱节与历史债务叠加所致：

### 2.1 根因一：前端数据加载单向绑定旧全局源，无响应式知识库联动
审查 [`Search.js`](file:///e:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/Business/ReferenceRAG.Business/wwwroot/app/views/Search.js#L286-L321)：
```javascript
const loadPaths = async () => {
    pathsLoading.value = true;
    try {
        const response = await pathsApi.getPaths(); // GET /api/paths
        paths.value = response.data.sources;
    } finally {
        pathsLoading.value = false;
    }
};
onMounted(() => {
    loadSources();
    loadPaths();
    loadSearchStatus();
    loadKnowledgeBases();
});
```
- `loadPaths()` 仅在组件挂载时调用一次，打到 `GET /api/paths`；
- 该接口调用的是 [`SourcesController.cs:GetPaths`](file:///e:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/Host/ReferenceRAG.ApiHost/Controllers/SourcesController.cs#L108-L148)，读取的是旧版系统的 `config.Sources` 以及旧向量库 `_vectorStore.GetAllFilesAsync()`。
- 在当前测试环境中，`config.Sources` 中仅配置了两个用于冒烟测试和核心用例的旧源：`jinyong_smoke` 和 `jinyong_core`（仅含 3 个文件）。
- 前端没有任何针对 `selectedKbIds` 的 `watch` 监听器，切换知识库时，“源筛选”与“路径过滤”组件处于**状态死滞（State Stagnation）**，永远只展示旧全局源。

### 2.2 根因二：后端缺少按知识库获取目录树与路由地址的 API
- `KnowledgeBasesController.cs` 提供了连接器的 CRUD（`GetConnectors`、`AddConnector` 等），但**未暴露任何获取该知识库文件路径树或目录层级的接口**；
- 知识库的真实物理文件和目录结构保存在各个独立知识库数据库 `kbs/{id}/kb.db` 的 `documents` 表中（`source_key` 包含 `三联版/天龙八部.md` 等相对路径，`locator` 包含完整物理绝对路径）。由于没有聚合接口将这些路径结构化提取为 `SourcePathInfo` 树形结构，前端即便想联动也无接口可调。

### 2.3 根因三：检索契约未支持路径与连接器过滤
审查 [`IKnowledgeBaseService.cs`](file:///e:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/Business/ReferenceRAG.Business/Features/KnowledgeBase/Services/IKnowledgeBaseService.cs#L165-L179)：
```csharp
public sealed record CrossKnowledgeBaseSearchRequest(
    string Query,
    IReadOnlyList<string> KnowledgeBaseIds,
    int TopK = 10,
    FailurePolicy FailurePolicy = FailurePolicy.AllowPartial,
    TimeSpan? Timeout = null,
    float[]? QueryVector = null,
    bool UseLexicalTokenizer = false,
    bool AllowDegrade = false);
```
- `CrossKnowledgeBaseSearchRequest` **完全没有定义 Folders、Paths 或 ConnectorIds 过滤参数**；
- 底层 [`SqliteKnowledgeBaseStore.cs`](file:///e:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/Infrastructure/ReferenceRAG.Infrastructure/Features/KnowledgeBase/Storage/SqliteKnowledgeBaseStore.cs#L790-L791) 的 `RecallAsync` 中更是硬编码写道：
  ```csharp
  if (request.MetadataFilters is { Count: > 0 })
      throw new NotSupportedException("知识库检索尚不支持元数据过滤。");
  ```
- 正因后端契约不具备路径过滤能力，前端开发者在 [`Search.js:223`](file:///e:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/Business/ReferenceRAG.Business/wwwroot/app/views/Search.js#L223) 中加了防御性抛错：
  ```javascript
  if (searchOptions.value.sources.length || searchOptions.value.folders.length)
      throw new Error('知识库检索不支持源或路径筛选，请先清除这些筛选条件');
  ```
  直接将错误抛给终端用户。

---

## 三、业界主流方案与现有库调研对比 (Industry Benchmark)

针对多知识库架构下的“路由地址组织”、“目录层级呈现”以及“路径过滤下推执行”，调研业界主流 RAG 与搜索引擎实践：

| 维度 | Dify / FastGPT | LlamaIndex / LangChain | Elasticsearch / OpenSearch | 本系统目标方案 |
| :--- | :--- | :--- | :--- | :--- |
| **路由/路径模型** | Dataset $\rightarrow$ Documents/Connectors $\rightarrow$ 相对目录树 | Document Metadata (`file_path`, `category`) | `path_hierarchy` 分词器或虚拟目录字段 | Connector $\rightarrow$ `source_key` 目录树 / `locator` 物理路由 |
| **目录树查询 API** | `GET /datasets/{id}/document-tree` | StorageContext 节点遍历 | Aggregation 聚合 (`terms: { field: "path.tree" }`) | `GET /api/knowledge-bases/{id}/paths` 或 `POST /paths` |
| **过滤下推机制** | SQL/Vector Store **前置过滤 (Pre-filtering)** | MetadataFilter $\rightarrow$ AST $\rightarrow$ SQL `LIKE` | Bool Query `filter: [ { prefix: ... } ]` | SQLite SQL `WHERE (source_key LIKE ... OR locator LIKE ...)` 前置过滤 |
| **过滤候选保证** | 保证 Top-K 在过滤范围内完全填满 | 保证 Top-K 在过滤范围内完全填满 | 保证 Top-K 在过滤范围内完全填满 | **前置过滤**，绝无后置截断饥饿问题 |

### 业界最佳实践结论：
1. **统一路由模型**：每个知识库挂载的 Connector 即是一级路由源（Source Root），Connector 内的文档相对路径切片即是二级/多级目录（Folders）。
2. **严禁后置过滤（Post-filtering）**：如果在检索出 Top 10 之后再通过内存判断路径，极易导致小目录候选集全部被过滤为空（候选集饥饿）。必须将路径过滤作为 SQL 条件在 FTS5 倒排匹配和向量相似度计算时**前置下推（Push-down Pre-filtering）**。

---

## 四、当前系统已有基础设施与复用评估

系统当前已具备完善的基础设施，无需引入任何第三方重型依赖，完全可以 100% 自研闭环复用：

1. **`SqliteKnowledgeBaseStore` 中的 `documents` 表**：
   - 字段包括：`id`, `connector_id`, `source_key`, `locator`。
   - `source_key` 均为规范化相对路径（如 `三联版/天龙八部.md`），`locator` 为操作系统物理绝对路径。
   - `chunks` 与 `chunk_vectors` 检索时天然 `JOIN documents d ON d.id = c.document_id`，可以直接在 `WHERE` 子句中添加路径前缀条件。
2. **`CatalogStore` 连接器管理**：
   - `_catalog.GetConnectorsByKnowledgeBaseAsync(kbId)` 可以极速拉取指定知识库挂载的所有启用连接器（含 `Id`, `TargetPath`, `Kind`）。
3. **前端 `n-tree` 与选中转换逻辑**：
   - `Search.js` 中现有的 `pathTreeData`、`selectedPaths`、`selectedPathFolders` 已经能完美处理 `source:` 与 `folder:` 树形选中映射。只需让数据源响应式绑定到当前激活知识库即可。
4. **路径标准化匹配规则**：
   - 复用 `LexicalSearchScope` 的规范：统一 `/` 分隔符，前缀匹配 `path.StartsWith(folder + "/")` 或全等匹配 `path.Equals(folder)`。

---

## 五、具体重构与落地执行方案

因项目处于未正式发布阶段，**不保留任何兼容性包袱或双系统妥协逻辑**，直接将知识库路径过滤做成第一公民（First-Class Citizen）：

### 5.1 存储层与契约扩展（Infrastructure & Domain）
1. **`RecallRequest` 契约扩充**：
   在 `RecallRequest` 中增加 `IReadOnlyList<string>? Folders = null`。
2. **`SqliteKnowledgeBaseStore` 路径提取与前置过滤**：
   - 实现 `GetPathsAsync(IReadOnlyList<ConnectorDefinition> connectors, CancellationToken ct)`：
     从 `documents` 表中提取唯一的目录路径，组装为 `SourcePathInfo` 列表。
   - 在 `RecallAsync` 中，将 `request.Folders` 转化为参数化 SQL 前置过滤子句：
     ```sql
     AND (
         d.source_key = @folder0 OR d.source_key LIKE @folder0Prefix
         OR d.locator = @folder0 OR d.locator LIKE @folder0Prefix
         OR d.connector_id = @folder0
     )
     ```
     同时作用于 BM25 词法全文检索与向量相似度检索。

### 5.2 业务层与服务编排（Business Layer）
1. **`CrossKnowledgeBaseSearchRequest` 扩展**：
   增加 `IReadOnlyList<string>? Folders = null`。
2. **`KnowledgeBaseSearchService.SearchAsync` 贯通**：
   将 `request.Folders` 透明下传至各库的 `RecallRequest`。
3. **`IKnowledgeBaseService` / `KnowledgeBaseService`**：
   实现 `GetPathsAsync(IReadOnlyList<string> kbIds, CancellationToken ct)`，整合指定知识库的连接器与物理目录结构。

### 5.3 控制器与 API 层（Host Layer）
1. **`KnowledgeBasesController` 新增路径接口**：
   - `GET /api/knowledge-bases/{id}/paths`
   - `POST /api/knowledge-bases/paths`（支持传入 `knowledgeBaseIds` 数组批量获取）
2. **`SourcesController.cs:GetPaths` 扩展**：
   支持可选查询参数 `kbId` / `kbIds`。若传入则直接代理至知识库路径获取逻辑。

### 5.4 前端视图与交互改造（Frontend Layer）
1. **`api/index.js` 扩展**：
   - `knowledgeBasesApi.getPaths(id)`
   - `knowledgeBasesApi.getBatchPaths(kbIds)`
   - `pathsApi.getPaths(params)`
2. **`lexical-search-template.js` 扩展**：
   `knowledgeBaseRequest(query, ids, topK, mode, folders)` 将选中的 `folders` 数组随请求发送。
3. **`Search.js` 响应式改造**：
   - 增加 `watch(() => selectedKbIds.value, async (newIds) => { ... })`：
     - 若 `newIds.length > 0`：调用 `knowledgeBasesApi.getBatchPaths(newIds)` 加载使用中知识库的连接器路由与目录树，并动态更新“源筛选”（Connectors）和“路径过滤”；
     - 若 `newIds.length === 0`：加载全局源路径；
     - 切换时重置已选路径与已选源，避免跨库路径污染。
   - 彻底删除 `Search.js:223` 处的 `throw new Error('知识库检索不支持源或路径筛选...')` 阻断逻辑。
   - 在 `handleSearch` 中，直接将 `searchOptions.value.folders` 注入到 `knowledgeBaseSearchApi.search` 请求中。

---

## 六、质量门禁与防回归自动化测试矩阵

| 门禁分类 | 测试用例名称 | 验证契约与指标 |
| :--- | :--- | :--- |
| **API 门禁** | `KnowledgeBasePathsRetrievalTests` | 验证针对含多个连接器及多层级目录的知识库，接口能正确返回连接器路由与子目录列表，不泄漏旧全局源 |
| **检索过滤门禁** | `KnowledgeBaseSearchPathFilterTests` | 验证在知识库纯词法模式、向量模式和混合模式下，指定特定目录（如 `三联版`），召回切片 100% 属于该目录，不匹配目录的切片命中率为 0（零渗漏） |
| **边界门禁** | `KnowledgeBaseSearchPathEmptyAndWildcardTests` | 验证未勾选路径时全库检索；勾选不存在的虚假路径时安全返回 0 条匹配，绝不触发 500/503 异常 |
| **前端 AST 门禁** | `check.mjs` & `frontend-logic.test.mjs` | 验证 `Search.js` 响应式 `watch`、`folders` 参数传递、移除阻断异常的正确性，确保 40+ 模块语法与符号绑定 100% 通过 |

---

## 七、推进与实施路线图

1. ✅ 撰写调研、业界对比与全栈方案文档至 `docs/`；
2. ✅ 实现 `IKnowledgeBaseStore` / `SqliteKnowledgeBaseStore` 目录树提取与参数化前置 SQL 过滤；
3. ✅ 升级 `CrossKnowledgeBaseSearchRequest`、`IKnowledgeBaseService` 与 `KnowledgeBaseSearchService`；
4. ✅ 暴露 `KnowledgeBasesController` 路径端点及 `SourcesController` 联合代理；
5. ✅ 改造前端 `Search.js`、`lexical-search-template.js`、`api/index.js`，实现响应式路径树刷新与搜索参数透传；
6. ✅ 编写端到端单元测试与自动化门禁，全量运行验证。

---

## 八、未选知识库（默认全库检索模式）统一收敛深度分析与落地

### 8.1 问题根因复盘：为什么未选择知识库时仍显示两个旧路径？
1. **双轨制降级遗留（Dual-Track Residual Fallback）**：
   在最初实现中，前端 `Search.js` 的 `loadPaths` 在 `selectedKbIds.value.length === 0` 时走 `else` 分支调用了 `pathsApi.getPaths()`（即 `GET /api/paths`）。
2. **后端控制器分支脱节**：
   `SourcesController.cs` 的 `GetPaths` 端点原逻辑为：仅在传入非空 `kbId`/`kbIds` 时才委托给 `KnowledgeBaseService`，未传时直接回退读取 `config.Sources`（该配置中只配置了 `jinyong_smoke` 和 `jinyong_core` 两个历史单源）。
3. **心智模型割裂**：
   系统已经全面迈向物理知识库多库架构，但在“不选知识库”的默认初始态下，页面却展示已废弃的单源配置，导致用户混淆。

### 8.2 彻底收敛改造方案（零兼容负担）
秉承“无正式发布历史包袱，一次性全量收敛”的原则，对未指定知识库的场景进行全链路贯通：
1. **前端统一聚合**：
   - `Search.js` 中 `loadPaths` 统一调用 `knowledgeBasesApi.getBatchPaths(selectedKbIds.value)`。当未选知识库时传入空数组 `[]`，直接拉取系统中所有已启用知识库的连接器路由（动态展示 `smoke`、`core`、`full`、`__manual__` 等）；
   - `handleSearch` 中只要系统存在知识库（`knowledgeBases.length > 0`），即使未勾选特定知识库，也直接走 `knowledgeBaseSearchApi.search` 执行现代检索；
   - 占位符文本优化为：`选择知识库（默认检索全部知识库）`。
2. **后端统一聚合与全库扇出**：
   - `SourcesController.cs:GetPaths`：当未传 `kbId` 且系统存在已启用的知识库时，自动委托 `_kbService.GetPathsAsync(Array.Empty<string>())` 返回全部已启用知识库的路由树；
   - `KnowledgeBaseSearchService.cs:SearchAsync`：当 `request.KnowledgeBaseIds` 为空时，自动从 `_catalog` 解析全部已启用的知识库（扇出上限 8 库），执行跨库并发召回。

### 8.3 自动化门禁与实机实测凭证
1. **Host 契约集成门禁**：
   `KnowledgeBasePathsAndFilterTests.cs` 新增用例，严格断言：
   - `sourcesController.GetPaths(enabledOnly: true)` 在未传 `kbId` 时返回所有已启用知识库的路由路径；
   - `kbController.GetBatchPaths` 传入空数组时返回所有已启用知识库的路由路径；
   - `_searchService.SearchAsync` 传入空 `KnowledgeBaseIds` 时自动跨库检索并返回命中片段。
2. **实机容器验证结果**：
   - 远程 Linux 容器（`http://127.0.0.1:5124`）实地验证通过：
     - 未选知识库时，路径过滤树成功列出 `smoke`、`core`、`full`、`__manual__`；
     - 未选知识库时，直接执行词法搜索“郭靖”，4.1s 内完成全库检索，成功命中 10 条真实语料片段（《射雕英雄传》《神雕侠侣》），零报错。
