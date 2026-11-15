# 知识库/源管理 UX 缺陷调研与修复方案

> 问题提出（生产环境实操反馈，2026-11-15）：
> 1. 数据源列表「文件数 / 分块数」恒为空或为 0；
> 2. 新建知识库向导「模型来源模式」四个选项水平溢出模态框；
> 3. 新建知识库后「绑定模型」显示 `spec_emb_9f85bf3849194299` 这类不透明规格 ID，且不确定能否手动配置；
> 4. 新建知识库后回到源管理，「挂载到知识库」下拉里看不到新库，必须刷新页面；
> 5. （调研中额外发现）直接访问或刷新 `/knowledge-bases` 深链返回 401 JSON，而不是 SPA 页面。

---

## 1. 问题与根因（含生产证据）

### 1.1 源列表文件数/分块数恒空

**现象**：`Temp` 显示 `165 / 26,945`（看似正常），`仙侠` 显示 `0 / 0`；但同一路径在知识库页显示 `17 篇文档 / 44,189 切片`。

**生产证据**（`GET /api/Sources` 与 `GET /api/knowledge-bases/{id}/connectors` 对照）：

| 路径 | `/api/Sources` 计数 | 该路径挂载的 KB 连接器计数 |
| :--- | :--- | :--- |
| `/lwt/r7/TempFileBig/Temp` | 165 / **26,945** | 默认知识库 → 165 / **24,776** |
| `/lwt/55/f/磁盘/.../仙侠` | **0 / 0** | 仙侠 KB → 17 / 44,189；你好 KB → 0 / 0 |

**根因**：`SourcesController.GetAll()` 的计数来自**旧单库向量库** `IVectorStore.GetSourceStatsAsync()`（读取 `vectors.db` 的 `files` 表并按 `source` 名称分组），而系统早已升级为「每库独立 `kb.db`」物理隔离架构，所有新索引写入由 `IKnowledgeBaseStorageManager` 管理的 per-KB 存储。结果：

- 仅在 KB 连接器内入库过的路径，旧库没有 `files` 行 → 恒为 0；
- `Temp` 的 26,945 是旧库遗留快照，与 KB 内真实值 24,776 已经漂移——用户看到的是过期数字；
- 仅由连接器投影出来的行（`Origin=KnowledgeBaseOnly`）在 `GetAll` 中**硬编码为 0**（`SourcesController.cs:153`）。

即**读模型仍指向已被取代的事实源**（同类问题也存在于 `IndexController.summary`、`BM25IndexController` 的旧库统计）。

### 1.2 「模型来源模式」溢出

`KnowledgeBases.js` 使用 `<n-radio-group>` 内嵌 4 个 `<n-radio-button>`。Naive UI 的 `n-radio-group` + `n-radio-button` 是**拼接按钮组**（`radio-group__splitor` 连接、flex 单行、不换行）。四个长标签（含 emoji 与英文括注）总宽约 900px 以上，而模态框宽 680px（`max-width: 95vw`，移动端更窄）→ 必然横向溢出。这是**组件选型与内容长度不匹配**的确定性布局缺陷，而非偶发渲染问题。

### 1.3 绑定模型显示 `spec_emb_<kbId>`

`handleCreateSubmit` 在「系统默认模板」模式发送 `embeddingSpecId: 'default-spec'`；`KnowledgeBaseService.CreateKnowledgeBaseAsync` 随即从全局配置**固化快照** `spec_emb_{kbId}`（含 ModelName/Dimension/MaxTokens），以保证知识库与全局模型解耦。逻辑正确，但卡片直接渲染 `kb.embeddingSpecId`，把内部快照 ID 暴露给用户。

能否手动配置：**可以**——创建向导的「本地已就绪模型 / 在线服务商模型」模式直接选择 spec；创建后可用「更换模型」（蓝绿影子重构作业）或「绑定模型」切换。问题只是**呈现不可读 + 默认模式未说明将使用哪个模型**。

### 1.4 新库未出现在「挂载到知识库」下拉

`Sources.js` 被 `MainLayout` 的 `<keep-alive :max="20">` 缓存（见 `MainLayout.js` 的 router-view）。`onActivated` 只调用 `loadSources(true)`，而 `loadSources` 仅当 `knowledgeBases.value.length === 0` 时才顺带 `loadKnowledgeBases()`。因此：

- 首次进入源管理 → 拉到 KB 列表（非空）；
- 去知识库页新建 KB（源管理实例被缓存，不重新 mount）；
- 回到源管理 → `onActivated` 复用**旧的** KB 列表，新库不在下拉里。

这是典型的「keep-alive 缓存 + 条件式懒加载」导致的**陈旧读模型**。

### 1.5 深链 401（额外发现）

`ApiKeyMiddleware.WhitelistedSpaPageRoutes` 缺少 `/knowledge-bases`、`/file-search`、`/reader`。浏览器直接访问这些路径（非 SPA 内跳转）时，中间件在路由解析前就把请求当受保护 API 拒绝，返回 JSON 401 而非 `index.html`。SPA 内点击不受影响（客户端路由不发请求），所以长期未被发现。

---

## 2. 业界方案对比

| 主题 | 业界做法 | 本项目采纳 |
| :--- | :--- | :--- |
| 统计口径 | Qdrant 以 collection 为统计单元（`GET /collections/{name}` 返回 points_count）；Weaviate 用 `Aggregate` 按 class（≈知识库）聚合；Chroma `collection.count()` 是**每个集合独立**计数 | 计数按「连接器（KB 内的数据源通道）」聚合，跨 KB 同路径求和；不再读已被取代的旧单库 |
| 缓存失效 | TanStack Query 的 `invalidateQueries`：写操作显式失效标签，`onActivated`/focus 时 stale 即重新拉取；Vue 官方 `KeepAlive` 文档要求用 `onActivated` 处理缓存实例的外部变更 | 复用已有 `JobCenter.registerRefresher` + `onActivated` 强制静默刷新 KB 选项（本项目已内建等效 InvalidationsRegistry） |
| 表单控件溢出 | Naive UI 官方对多选题组推荐 `n-select`（dropdown 宽内容）或 `n-checkbox-group` 纵向；拼接按钮组只适合 2–3 个短标签 | 4 个长选项改为 `n-select`，并在下拉内保留完整说明 |
| 内部 ID 呈现 | MLflow Model Registry / vLLM `--served-model-name` 均区分「注册名（身份）」与「展示名」；内部 version/snapshot id 只在详情/调试视图出现 | 卡片显示「模型名（维度）」为主，内部 spec id 降级为 tooltip/次要文本 |

**可复用基础设施核查**：本修复**无需引入任何新依赖**。已具备：`IKnowledgeBaseService.GetConnectorsAsync`（含 per-connector DocumentCount/ChunkCount 与读租约）、`SourceLinkProjectionCache`（连接器投影缓存）、`JobCenter` 失效标签体系、`useBreakpoints`、`ApiKeyMiddleware` 白名单校验测试基座（`ApiKeyMiddlewareWhitelistTests`）。

---

## 3. 具体解决方案

### P1 计数口径改为 per-KB 事实源（后端）

1. `IKnowledgeBaseService` 新增 `GetConnectorStatsSnapshotAsync`：一次遍历 catalog KB + 连接器，按 KB 获取只读租约，调用 `GetConnectorStatsAsync`，返回 `(KnowledgeBaseId, ConnectorId, TargetPath, DocumentCount, ChunkCount, UpdatedAt)` 轻量快照（不做目录可达性探测，避免文件系统 IO）。
2. `SourcesController.GetAll/Get`：以 `NormalizePathKey(TargetPath)` 聚合该快照 → 每行 `FileCount/ChunkCount = Σ(已挂载连接器)`；`LastIndexed` 取最大 `UpdatedAt`。
3. 删除对旧库 `IVectorStore.GetSourceStatsAsync()` 的依赖（`GetAll`/`Get` 不再读它）；连接器投影行不再硬编码 0。

### P2 「模型来源模式」改下拉（前端）

`n-radio-group`+`n-radio-button` → `n-select`（`class="binding-mode-select"`），选项保留完整文案；底部说明区继续按 `bindingMode` 切换。

### P3 绑定模型可读化（前端）

- 建立 `specById` 映射（来自 `embeddingSpecs`），卡片显示 `模型名 (维度d)`，纯词法仍显示 `纯词法 (FTS5 BM25)`；
- 内部 `spec_emb_*` / `local_*` / `prov_*` ID 放到 `n-tooltip` 或次要 `code` 文本；
- 创建向导「系统默认模板」模式增加一条提示：将固化当前全局模型 `<ModelName>`；并保留「更换模型/绑定模型」入口说明。

### P4 keep-alive 陈旧数据修复（前端）

`Sources.js`：
- `loadKnowledgeBases(silent)` 支持静默模式；
- `onActivated` 中始终 `await loadKnowledgeBases(true)`（与 `loadSources(true)` 并行），不再依赖 `length === 0`；
- 注册 `JobCenter.registerRefresher('knowledge_bases', ...)`，让「新建/删除知识库」等作业完成后自动刷新挂载下拉。

### P5 深链白名单补齐（后端）

`ApiKeyMiddleware.WhitelistedSpaPageRoutes` 增加 `/knowledge-bases`、`/file-search`、`/reader`。

---

## 4. 同类门禁与回归测试

| 门禁 | 位置 | 锁定不变量 |
| :--- | :--- | :--- |
| G-1 源计数来源 | `tests/ReferenceRAG.Host.Tests/SourceCountsFromKnowledgeBaseGateTests.cs` | 连接器有 N 篇文档时 `/api/Sources` 返回 N（不再读旧库）；同路径多 KB 求和；无连接器路径为 0；连接器投影行计数非硬编码 0 |
| G-2 表单控件不溢出 | `wwwroot/tests/e2e-kb-create-modal.mjs`（真实浏览器，3 视口） | 新建知识库模态框内所有控件右边界 ≤ 模态框右边界 + 1px；无横向滚动条 |
| G-3 绑定模型可读 | `FrontendViewIntegrityTests`（静态） | KB 卡片渲染代码不得直接输出裸 `kb.embeddingSpecId` 作为主标签；必须经 `kbModelLabel` 映射 |
| G-4 keep-alive 刷新 | `e2e` 场景 + 静态断言 | `Sources.js` 的 `onActivated` 必须调用 `loadKnowledgeBases`；浏览器内「新建 KB → 回源管理」无需刷新即可见新库 |
| G-5 深链白名单 | `ApiKeyMiddlewareWhitelistTests` 新增派生测试 | 从 `app/router/index.js` 解析所有静态页面路径，逐个断言无 Key 时返回 200 且非 JSON 401 |

---

## 5. 实施状态与验收结果

全部 5 项已落地（无历史数据兼容负担）。

### 5.1 交付清单

| 项 | 改动 | 文件 |
| :--- | :--- | :--- |
| P1 计数口径 | 新增 `IKnowledgeBaseService.GetConnectorStatsSnapshotAsync`（逐库只读租约直读 `GetConnectorStatsAsync`，不做目录探测）+ `ConnectorStatsSnapshot` 记录；`SourcesController.GetAll/Get` 改为按 `NormalizePathKey(TargetPath)` 聚合该快照（跨库求和、`LastIndexed` 取最大），连接器投影行不再硬编码 0;`GetAll/Get` 彻底移除对 `IVectorStore.GetSourceStatsAsync()` 的依赖 | `IKnowledgeBaseService.cs`、`KnowledgeBaseService.cs`、`KnowledgeBaseModels.cs`、`SourcesController.cs` |
| P2 溢出 | 4 个长选项 `n-radio-group`+`n-radio-button` → `n-select`（`binding-mode-select`），选项集中在 `bindingModeOptions` | `KnowledgeBases.js` |
| P3 可读化 | `kbModelLabel`/`kbModelHint`（模型名+维度为主标签，内部 ID 进 tooltip）、`chunkingSpecLabel`/`chunkingSpecHint`（分段策略同理）、`defaultTemplateModelLabel` 取自 `/index/models/current` 真实模型（此前误用占位 `default-spec` 的 `bge-small-zh-v1.5/512d`，与实际固化快照 `bge-m3/1024d` 不符）；新建库后追加 `loadEmbeddingSpecs()` 以解析新固化的快照 ID | `KnowledgeBases.js` |
| P4 keep-alive | `loadKnowledgeBases(silent)` 支持静默；删除 `if (length===0)` 条件式懒加载；`onMounted`/`onActivated` 统一走 `refreshSourceAndKbOptions()`（并行拉源+KB）；注册 `JobCenter.registerRefresher('knowledge_bases')`;刷新按钮改走同一函数 | `Sources.js` |
| P5 深链 | 白名单补 `/knowledge-bases`、`/file-search`、`/reader` | `ApiKeyMiddleware.cs` |

### 5.2 门禁与实测结果

| 门禁 | 结果 |
| :--- | :--- |
| `SourceCountsFromKnowledgeBaseGateTests`（6 例：旧库快照污染隔离/跨库求和/未挂载为 0/连接器行非硬编码/单源 Get/空库跳过） | 6/6 通过（旧库 mock 故意返回 777/999，断言只接受 kb.db 真值） |
| `SpaRouteWhitelistParityGateTests`（从 router 解析全部静态路由，逐个比对中间件白名单） | 1/1 通过 |
| `kb-ux-gates.test.mjs`（5 例静态契约：标签必须经映射/分段策略/真实当前模型/keep-alive 刷新/下拉控件） | 5/5 通过 |
| Host 套件 | 464/464 通过 |
| 核心套件 | 784 通过 / 47 跳过 / 0 失败 |
| 前端 `npm test` | 118/118 通过 |
| `node check.mjs` | 通过（59 模块 + 14 模板） |
| 浏览器 E2E `bin/acceptance/.run/e2e-kb-ux.mjs` | **15/15 通过**（真实 Edge 无头 + 隔离 WebHost）：4 个深链返回 HTML;3 视口（1440/900/390px）零控件溢出且无横向滚动；下拉控件替代按钮组；默认模板提示显示真实 `bge-m3 (1024d)`;卡片显示 `bge-m3 (1024d)` / `bge-small-zh-v1.5 (512d)` 且无裸 spec id;**keep-alive 场景下新建「二次新建库」后回源管理，挂载下拉无需刷新即出现**;控制台零错误 |

### 5.3 开发环境（本地 Linux）验收（Dev 部署 `<DEV_HOST>:5124`）

按 `scripts/deploy-linux.ps1 -Mode Dev` 快速开发流程部署到 `referencerag-dev` 容器（健康 200，Smoke 语料同步成功），随后用真实浏览器逐页验收（脚本 `bin/acceptance/.run/e2e-dev-acceptance.mjs`，**33/33 通过**）：

| 验收项 | 实测 |
| :--- | :--- |
| 深链 5 条（/knowledge-bases、/sources、/file-search、/reader、/models） | 均 200 + `text/html` |
| 计数口径（逐行对照连接器求和） | smoke `1/46`、core `2/1080`、full `28/56607`——**完全一致** |
| 连接器投影行 | 由 `-` 改为真实 `28 / 56,607`（截图确认） |
| KB 卡片绑定模型 | `BAAI/bge-m3 (1024d)`、`纯词法 (FTS5 BM25)`，无裸 spec id |
| 漂移判定 | dev 环境历史 Spec 为 `SiliconFlow / BAAI / BGE-M3 (1024d)`，此前**误报 Drifted**;规范化补强后 `Aligned` |
| 新建向导 3 视口（1600/900/390px） | 零控件溢出、零横向滚动（截图确认） |
| 模型来源模式 | 下拉控件，按钮组已移除 |
| 默认模板提示 | `本次将固化为：BAAI/bge-m3`——与 `/index/models/current` 一致 |
| keep-alive 挂载下拉 | 新建「QA挂载校验库」后回源管理，下拉立即可见（无需刷新） |
| 展示名回归（改名/徽标/重置/漂移不受影响） | 全通过;改名期间 drift 保持 `Aligned` |
| 移动端 390px（sources/knowledge-bases/models） | 零横向溢出 |
| 控制台 | 零错误 |

**顺带修复的同类身份问题（根因同一类）**：`ModelIdentity.NormalizeKey` 仅剥离 `" / "` 分隔符，遇到 dev 真实形态 `SiliconFlow / BAAI / BGE-M3 (1024d)` 会失败（多级 `" / "` + 尾部维度括注 + 厂商路径 `BAAI/` 均未处理）。已扩展规范化规则（尾部 `(1024d)` 括注、厂商路径前缀），新增 8 例参数化门禁；`text-embedding-3-small` 与 `text-embedding-3` 仍正确判为不同模型。

### 5.4 正式环境验收（部署 `referencerag:deploy-039f0fb` 后实测）

镜像远端构建（`git archive HEAD` → `docker build`）后以 `-Image` 部署；容器 healthy，health 200，容器内前端含 `kbModelLabel`/`binding-mode-select`/`refreshSourceAndKbOptions` 标记。

| 验收项 | 实测 |
| :--- | :--- |
| 源计数（与连接器求和逐行比对） | `Temp 165/24,776`、`仙侠 17/44,189`——**全部一致**（修复前仙侠为 `0/0`，Temp 为过期的 `26,945`） |
| 深链 4 条 | 均 200 + `text/html`（原返回 401 JSON） |
| 知识库卡片 | `bge-m3 (1024d)` / `纯词法 (FTS5 BM25)`，无裸 spec id;分段策略显示 `SlidingWindow` |
| 「偏离(建议重排)」误报 | 默认知识库徽标消失（`当前组件 bge-m3` 与全局 `bge-m3` 判等）；状态为 `定制`（只因分块策略不同，属预期） |
| 新建向导 | 1600px 与 390px 均零溢出；下拉控件；提示 `本次将固化为：bge-m3` |
| keep-alive 挂载下拉 | 新建临时库后回源管理，下拉立即可见（`["QA临时挂载库","你好","仙侠","默认知识库"]`），随后清理（204） |
| 展示名生产回归 | PUT 成功 → 列表显示自定义名 +「自定义」标记 → 改名期间 drift 保持 `Customized` → DELETE 重置 |
| 移动端 390px（sources/knowledge-bases/models） | 零横向溢出 |
| 控制台 | 零错误 |

浏览器验收脚本：[bin/acceptance/.run/e2e-prod-acceptance.mjs](file:///E:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/bin/acceptance/.run/e2e-prod-acceptance.mjs)（20 项断言全过）。部署后按约定删除上一版镜像，仅保留当前版本。

### 5.5 遗留说明

- `IndexController.summary`、`BM25IndexController` 仍按旧单库 `IVectorStore` 统计（属遗留 v1 检索链路统计口径，非本次 UX 缺陷范围）；其数字在纯 KB 架构下会偏小，后续若纳入仪表盘统一口径可复用本次 `GetConnectorStatsSnapshotAsync`。
- 计数为**跨库求和**语义：同一路径挂到多个知识库时，列表显示所有连接器的合计（与 Qdrant/Weaviate 以集合为统计单元的口径一致）。

---

## 6. 参考来源

- [Qdrant: Collections（collection 级 points_count）](https://qdrant.tech/documentation/manage-data/collections/)
- [Weaviate: Aggregate（按 class 聚合）](https://docs.weaviate.io/weaviate/api/graphql/aggregate)
- [Vue: KeepAlive 与 onActivated 语义](https://vuejs.org/guide/built-ins/keep-alive.html)
- [TanStack Query Vue: Query Invalidation](https://tanstack.com/query/latest/docs/framework/vue/guides/query-invalidation)
- [Naive UI 官方文档](https://www.naiveui.com/)
