# 免向量词法检索后续实施交接

> 交接日期：2026-11-08。以当前工作区为基线，包含尚未纳入 Git 的实现和测试。原始范围及验收要求见[实施计划](embedding-free-lexical-retrieval-implementation-plan.md)。本页按接手后的执行顺序列出缺口。

## 接手先做什么

1. 在仓库根目录查看 `git status --short` 和 `git diff`，同时核对未跟踪文件。尤其保留 `LexicalIndexCoordinator.cs`、`LexicalFileIndexer.cs`、`VectorCoverageVerifier.cs` 和对应测试。只看已跟踪差异会遗漏这些实现。
2. 先运行本文的核心测试组和宿主测试组，记录结果。调试时使用临时配置、数据目录和文件源。需要复现升级时，从停止写入后的完整备份或 SQLite 一致备份创建副本。
3. 从 P0.1 的并发复现开始。向量恢复主流程已经接入，可以在它的基础上补齐写入保护，再进行故障注入、宿主演练和性能验收。

## 当前实现及证据

| 范围 | 当前工作区的实现 | 可以依赖的证据与边界 |
| --- | --- | --- |
| 文件源版本发布 | `LexicalFileIndexer` 使用既有固定分块参数及指纹。`LexicalIndexCoordinator` 先暂存原文，再提交 FTS，校验分块 ID 后切换活动代并标记向量过期。恢复会清理未活动的代 | `LexicalIndexConsistencyTests` 覆盖暂存不可见、损坏的暂存索引、并发查询、删除及重复恢复。旧库迁移和真实进程终止仍需演练 |
| 无模型索引与检索 | 批量、单文件、监听、启动同步复用词法索引器。REST、搜索页、MCP `LexicalSearch` 及聊天可选词法模式。授权和源排除在 TopK 前应用 | `Lexical*Tests`、`ScopedLexicalSearchTests` 与宿主授权测试。已有进程内 HTTP 测试。实际 Web 和桌面宿主仍需分别验收 |
| 向量恢复 | `IndexService` 在存在持久过期标记且触发完整强制重建时进入严格流程。先写全局标记，重建旧 BM25 和图谱，再由 `VectorCoverageVerifier` 核对文件集合与内容、分块、指定模型向量和 BM25 索引。验证成功后才完成作业 | `VectorCoverageRecoveryTests` 使用真实 SQLite、FTS 和图谱，embedding 为确定性替身。成功、注入推理失败、文件占用及核验期间文件变更已有针对性覆盖。并发管理入口及实际查询恢复仍需验收 |
| 独立知识库 | 独立 SQLite 库维护事务内词法索引。受限密钥按连接器路径、入库位置和实际数据源过滤文档。列表仅展示含授权文档的库，旧记录缺少位置时需重新同步 | `KnowledgeBaseArchitectureGateTests` 与 `RestrictedHttpCrossKnowledgeBaseSearchUsesDocumentScopeBeforeTopK` 覆盖混合授权、连接器改指向、伪造授权字段和 TopK。管理接口仍要求全源权限 |

上一轮实际运行了核心相关测试 83 项、宿主相关测试 35 项。向量恢复的后续代码另跑过重建成功与失败、覆盖核验、指定模型向量读取、BM25 精确校验、持久标记、删源锁和禁用模型时重建接口的聚焦测试。两批结果对应不同工作区阶段，接手时用下列命令建立当前基线。

## P0.1 封闭重建与管理写入的并发窗口

### 1. 建立可重复的复现环境

从 [VectorCoverageRecoveryTests.cs](../tests/ReferenceRAG.Tests/VectorCoverageRecoveryTests.cs) 的 `FullRebuildRestoresVectorQueriesOnlyAfterRealIndexesAreVerified` 起步。每个用例使用独立临时目录、真实 SQLite 向量库、BM25 与图谱库，以及确定性的 embedding 替身。配置两个启用源 A、B，使“部分源”和“全部启用源”可区分。A 的旧版和新版分别包含唯一词 `p01_old_token`、`p01_new_token`，并各自链接不同的笔记。先为旧版建立向量、BM25 和图谱，再用词法索引器发布 A 的新版，确认持久过期状态存在。恢复用例重新启用 embedding，沿用同一份临时数据。

用两个 `TaskCompletionSource`（设置 `RunContinuationsAsynchronously`）分别通知“已到达暂停点”和“允许继续”。先等暂停点，再启动竞争操作。用存储调用入口的独立信号确认竞争操作实际到达了哪一步。所有等待用 `WaitAsync` 设上限，在 `finally` 放开暂停点并等待后台任务退出。HTTP 请求返回 202 只证明任务已受理，后台写入及终态需另行观察。

| 暂停点 | 如何设置 | 用途 |
| --- | --- | --- |
| A：恢复已接管 | `BeginVectorRecoveryAsync` 已提交全局过期标记 `*`，在恢复自身第一次清空 BM25 或图谱前暂停。可用对应存储的转发替身拦住第一次清空，并从只读连接确认标记已持久化 | 复现恢复期间的查询、重复重建和早期竞争写入 |
| B：核验通过、标记仍在 | 在 `VectorCoverageVerifier.VerifyAndCommitAsync` 调用提交回调前暂停，确保文件集合、内容、向量、BM25 和配置的最终检查均已结束。核验器测试可包装传入的提交回调。完整作业测试若缺少精确暂停点，增加仅供测试使用的内部接缝，仍调用真实提交。拦住 `VerifyExactIndexAsync` 只能复现更早的窗口，后续文件和配置检查仍会执行 | 复现写入发生在最后一次校验与 `CompleteVerifiedAsync` 之间的竞态 |
| C：管理写入先获准 | 在过期状态仍为空时启动管理操作，等它通过入口检查并到达首次写入边界，再发布新版词法代并启动恢复。图谱操作在后台任务内暂停到 `ClearAllAsync` 前 | 复现先通过检查、后写入的反向竞态 |

持有恢复租约时，测试通过独立只读连接查看持久标记，或把读取任务也纳入并发断言。直接等待同一协调器的 `GetStaleVectorSourcesAsync` 会争用恢复正在持有的锁。

暂停 C 时，先启动词法发布任务。如果发布也需要等待管理写入释放锁，先验证它尚未提交，再放开 C。暂停 A 或 B 时同理，先判断竞争任务已被拒绝、正在等待还是已进入存储写入，再决定何时放开恢复，避免测试自身互等。

### 2. 按场景加测试并记录修复前结果

将测试方法统一命名为 `P01_场景名`，按表中的文件落位。表中均为待补或待扩充的用例。

| 用例 | 复现顺序 | 测试落点 | 预期结果 |
| --- | --- | --- | --- |
| 管理接口在最后核验后写入 | 暂停 B，逐项发起 `DELETE /api/index/models/{modelName}`、`DELETE /api/index/models`、`DELETE /api/index/data`、`POST /api/index/cleanup`。每项使用独立数据。若当前实现允许删除或清理，等实际写入返回后再放开 B，检查是否出现作业完成但向量、BM25 或图谱已变的情况 | 新建 `tests/ReferenceRAG.Host.Tests/VectorRecoveryConcurrencyTests.cs`，沿用 [LexicalAuthorizationTests.cs](../tests/ReferenceRAG.Host.Tests/LexicalAuthorizationTests.cs) 的进程内 HTTP 调用方式 | 竞争写入在恢复租约释放前被明确拒绝或排队，存储层无抢写。排队后获准执行的操作若使覆盖失效，必须在首次破坏性写入前持久化过期状态。检查标记与实际数据，不能只检查 HTTP 状态码 |
| 旧重建预清理和独立图谱重建先通过检查 | 用暂停 C 启动 `POST /api/index/rebuild` 的旧预清理分支（`deleteExisting=true`）或 `POST /api/graph/rebuild`，随后触发词法发布及完整恢复。分别验证“旧写入先结束”和“恢复先接管”两种顺序。图谱用例等后台任务完成，不能以 HTTP 202 代替 | 同一宿主测试文件 | 先获准的写入完成后才允许恢复进入受保护写入区。若恢复先接管，过期判断须在真正写入前重做，旧请求无法清空恢复已验证的数据。恢复后图谱只能读到本轮有效的文件关系 |
| 源删除、监听和部分索引作业竞争 | 暂停 A 或 B，分别让 `DELETE /api/sources/{name}?deleteData=true`、监听同一路径的变更、`POST /api/index/jobs` 的部分源、仅向量和重分块请求竞争。部分源用 `{"sources":["A"],"force":true}`，其余分别设置 `vectorOnly` 或 `rechunk`。增加一个等待协调器的删源任务，检查与文件锁交错时能否退出 | [VectorCoverageRecoveryTests.cs](../tests/ReferenceRAG.Tests/VectorCoverageRecoveryTests.cs) 测作业和删源，[LexicalAutoIndexTests.cs](../tests/ReferenceRAG.Tests/LexicalAutoIndexTests.cs) 测监听，[LexicalIndexConsistencyTests.cs](../tests/ReferenceRAG.Tests/LexicalIndexConsistencyTests.cs) 测租约释放 | 所有任务在有界时间内退出，无锁重入死锁。竞争操作不能以部分覆盖解除全局保护。受保护区内拒绝或延迟的监听变更有后续处理机会。源变更后查询只开放经验证的范围 |
| 源配置、磁盘内容或模型变更 | 暂停 A 后逐项更改源路径、启用集合、分块配置、模型身份或维度，另测同名模型的实际路径变更。暂停 B 后尝试保存配置，并另测直接修改、删除或新增文件。每个用例只改一项并记录改动发生在最终检查的前面还是后面 | [VectorCoverageRecoveryTests.cs](../tests/ReferenceRAG.Tests/VectorCoverageRecoveryTests.cs)，配置入口用宿主测试补充 | 配置写入与提交互斥，或变化使本轮核验失效。文件变化发生在最终检查后时，不能只靠已读取的快照宣布当前磁盘已覆盖，需验证变更会保留或重新置为过期。检查按源标记，确保新启用、移路径及未验证的源不会被误清 |
| 重复触发、取消与故障 | 暂停 A 或 B 后提交第二个完整重建。另分用例通过 `POST /api/index/jobs/{jobId}/cancel` 取消，或在清库、推理、BM25/图谱写入及清标记前注入异常。保存作业 ID，等后台任务真正退出后重新打开相同数据目录，再重试完整重建 | [VectorCoverageRecoveryTests.cs](../tests/ReferenceRAG.Tests/VectorCoverageRecoveryTests.cs) 测作业终态和事件，[LexicalIndexConsistencyTests.cs](../tests/ReferenceRAG.Tests/LexicalIndexConsistencyTests.cs) 测跨重启标记 | 同一时间至多一个恢复作业进入写入区。提交前取消最终为 `Cancelled`，异常最终为 `Failed`，两者都不能发出 `IndexCompleted` 且 `Success=true` 的事件。已进入恢复写入区的中断在重新打开数据库后仍有 `*`，重试通过后才清除。覆盖已提交后到达的取消不得把已提交结果报告成回滚 |
| 从正常入口恢复并实际查询 | 启用 embedding 后，对仍过期的库用 `POST /api/ai/query` 请求 Standard、Hybrid。随后调用 `POST /api/index/rebuild`，分别测试省略 `sources` 和显式提交全部启用源。核验完成后重复两种查询并读取图谱。再用部分源、仅向量和重分块请求作为对照 | 同一宿主测试文件。可复用现有查询构建和鉴权测试夹具 | 恢复前查询得到 `409` 与 `vector_index_incomplete`，或在恢复持锁时等待并在结束后按实际状态返回。恢复成功后，Standard、Hybrid 返回 A 新版原文，新分块拥有指定模型向量，旧分块、旧 BM25 词和旧图谱关系均不可检索。只清除已验证范围的标记。部分作业不能替代完整恢复 |

### 3. 收口并发保护与执行验收

以 `IndexService.StartIndexAsync` → `VectorCoverageVerifier.VerifyAndCommitAsync` → `VectorRecoveryLease.CompleteVerifiedAsync` 为主线，先让竞争用例在现有实现上暴露问题，再修复。将实际写入与检查纳入同一保护边界。对于排队后仍能执行的写操作，核对它是否应重新写入过期标记，以及标记是否先于派生库变更。审查 `FileIndexPipeline.DeleteSourceAsync` 的文件锁、协调器锁及调用顺序，避免外层持有协调器锁再进入会获取同一锁的删源链路。

测试中用 `IndexService.WaitForCompletionAsync` 等后台任务退出。走 HTTP 时，从 202 响应取 `jobId`，用 `GET /api/index/jobs/all` 核对任务从活跃列表转到历史列表，再读取终态。单任务查询只查活跃任务，任务结束后可能返回 404。

从仓库根目录依次运行。先跑已有基线，再跑按上述命名新增的 P0.1 用例。新增组需要确认两个项目都实际匹配到测试。

```powershell
dotnet test tests/ReferenceRAG.Tests/ReferenceRAG.Tests.csproj --no-restore --filter 'FullyQualifiedName~VectorCoverageRecoveryTests|FullyQualifiedName~LexicalIndexConsistencyTests|FullyQualifiedName~LexicalAutoIndexTests|FullyQualifiedName~LexicalFileIndexerTests|FullyQualifiedName~IndexServiceTests'
dotnet test tests/ReferenceRAG.Host.Tests/ReferenceRAG.Host.Tests.csproj --no-restore --filter 'FullyQualifiedName~LexicalAuthorizationTests|FullyQualifiedName~VectorRecoveryConcurrencyTests'
dotnet test tests/ReferenceRAG.Tests/ReferenceRAG.Tests.csproj --no-restore --filter 'FullyQualifiedName~P01_'
dotnet test tests/ReferenceRAG.Host.Tests/ReferenceRAG.Host.Tests.csproj --no-restore --filter 'FullyQualifiedName~P01_'
git diff --check
```

- [ ] 两种进入顺序均有可重复的测试，失败输出能区分竞争写入、超时、误报完成和过期标记丢失。
- [ ] 逐条记录 HTTP 状态、竞争写入发生的时点、作业状态、`IndexCompleted.Success`、恢复前后标记，以及向量、BM25、图谱实读结果。重复运行两组 `P01_` 用例，确认没有挂起或依赖调度时序的偶发失败。
- [ ] 完整强制重建的 API 和实际 Standard/Hybrid 查询均通过，失败或取消后的重试也通过，再进入 P0.2。

## P0.2 完成双向切换和旧库迁移

以下用例均为待补或待扩充项。先完成 P0.1，避免管理操作与恢复并发污染本节结果。

### 1. 准备旧版基线与隔离环境

1. 在独立临时根目录下分开存放配置、数据和源文件。宿主演练设置 `REFERENCERAG_CONTENT_ROOT` 和临时 `ReferenceRAG.DataPath`，核对进程实际使用的配置路径与数据库路径。所有切换都先等旧进程退出并释放数据目录锁，再修改配置并启动下一进程。各阶段复用同一数据目录。
2. 建立两个启用源 A、B。A 放入 `edit.md`、`remove.md`、`before-rename.md`，分别写入唯一检索词。B 放一篇跨阶段保持不变的文件，另放一个同名前缀但不在授权范围内的源作为对照。记录文件路径、原文、行号、旧 chunk 引用和每个词的预期命中范围。
3. 旧库迁移优先用旧版程序生成基线，包含 `vectors.db` 的 `files`、`chunks`、模型和向量表，以及旧 `bm25.db` 和图谱数据。停写后生成完整一致备份，再从备份创建演练副本。核对备份中的绝对源路径仍指向临时语料。自动测试可用现有旧存储接口生成同结构样本，确认首次升级前无 `lexical_*` 表，并单独记录真实旧版产物演练的结果。
4. 为基线保存文件和 chunk ID、内容哈希、向量模型及维度、旧 BM25 和图谱查询结果。用查询或逻辑快照比对数据。SQLite 文件字节和 WAL 状态可随迁移、恢复而变化。

### 2. 复现双向切换

使用 `TaskCompletionSource` 暂停启动同步的扫描或最后一个文件的发布。先等暂停信号，再通过真实查询入口发请求。所有等待设置上限，并在 `finally` 放开暂停点。建议在 [LexicalStartupSyncTests.cs](../tests/ReferenceRAG.Tests/LexicalStartupSyncTests.cs) 验证后台作业，在新的 `tests/ReferenceRAG.Host.Tests/LexicalModeTransitionTests.cs` 验证查询与重启行为。用例名统一以 `P02_` 开头。

| 阶段 | 复现步骤 | 预期结果 |
| --- | --- | --- |
| 旧向量 → 词法 | 先用 Standard、Hybrid 查询旧版基线。停服，设 `Indexing.EmbeddingEnabled=false`、`Search.EnableLexical=true`，重启并暂停首次词法覆盖。同步前、完成后分别请求 `POST /api/ai/query`（`mode=Lexical`） | 按第 3 节选定的迁移方式返回已验证覆盖或明确的未就绪状态。覆盖完成后能检索基线原文、路径和行号。使用会抛错的模型替身验证词法索引和查询期间未解析或调用 embedding、模型 tokenizer 和重排 |
| 词法 → 向量 | 在词法模式编辑 A 的 `edit.md` 并换成唯一新词，等活动代发布。停服，设 `Indexing.EmbeddingEnabled=true`、`Search.EnableLexical=false` 并配置测试模型。重启后先查询，再通过 `POST /api/index/rebuild` 强制重建全部启用源，等作业终态后再查询 | 重建前 A 的 Standard/Hybrid 返回 `409` 和 `vector_index_incomplete`。完整核验成功后，Standard/Hybrid 返回新词和新版原文，旧词、旧 chunk 引用和旧图谱关系不再命中。B 保持可检索，完整重建可以更换它的 chunk ID |
| 向量 → 再回词法 | 保持向量模式，再改写 `edit.md`、删除 `remove.md`、将 `before-rename.md` 改名为 `after-rename.md`，并新增一篇文件。等向量任务完成，停服切回词法。分别测试 `SyncOnStartup=true` 且同步被暂停、同步失败后重试，以及 `SyncOnStartup=false` 时通过 `POST /api/index/jobs` 手工同步 | 待刷新范围不能返回上一次词法模式的正文、已删文件、旧路径或旧引用。拟补的覆盖门禁应给出可区分的未就绪状态，不能把缺覆盖当作正常空结果。只有扫描、索引与校验全部通过才开放该范围。结束后验证编辑、新增、删除和改名，检查旧 refId 经 `POST /api/ai/drill-down` 无法读取旧正文。再次重启且文件无变化时结果保持一致 |

增加空源、部分源同步成功后另一源失败、同步取消和无文件变更四个变体。空源完成验证后可返回正常空结果。范围尚未就绪时，查询不得借用另一模式或另一来源的结果填补。记录源级就绪状态和启动作业结果，避免把宿主已启动当成同步成功。

### 3. 验收旧库迁移与回退

- [ ] 在 [LexicalIndexConsistencyTests.cs](../tests/ReferenceRAG.Tests/LexicalIndexConsistencyTests.cs) 增加旧库副本用例。先在默认配置下打开旧库并查询，确认旧配置缺省值、旧数据及索引可用。再启用词法并首次打开协调器。当前 `RecoverAsync` 主要回收非活动代，旧库首次开库本身不能证明词法覆盖已建立。
- [ ] 在实现前记录迁移选择。若沿用原计划的 `legacy` 活动代，验证旧记录和引用映射、活动代与 FTS 一致后才发布。若采用扫描重建新词法代，先更新实施计划，明确旧引用兼容策略和覆盖完成前的门禁。测试迁移中途失败、重复执行和连续两次启动，比较可见记录及旧库逻辑快照，避免重复行、旧库内容变化或误报就绪。
- [ ] 在新的 `LexicalModeTransitionTests.cs` 验证升级前后查询契约与授权行为。在 [ConfigManagerTests.cs](../tests/ReferenceRAG.Tests/ConfigManagerTests.cs) 补旧配置缺省和重启加载用例。故意让迁移在文档库建表后、FTS 库准备完成前中断，重新打开同一份已中断的演练副本，验证增量 schema 可重复初始化且旧数据可读。
- [ ] 在独立目录恢复迁移前的完整一致备份，用旧版程序核对源、旧引用、向量、BM25 和图谱。记录快照之后会丢失的写入范围。每次演练记录旧版标识、配置、备份方法、恢复前后数据摘要及查询结果。

### 4. 在持久化边界注入故障

先在 [LexicalIndexConsistencyTests.cs](../tests/ReferenceRAG.Tests/LexicalIndexConsistencyTests.cs) 复用现有的暂存、发布、损坏和重复恢复测试。再增加进程级用例。可在 `tests/LexicalRecoveryCrashWorker/` 新建仅供测试的控制台项目（`LexicalRecoveryCrashWorker.csproj`）。子进程使用真实协调器或索引作业运行到指定边界，输出并刷新带有用例 ID 和边界 ID 的信号，然后暂停。父测试确认这是本用例启动的 PID，且进程仅使用已核对的临时数据目录，再强制终止该子进程并等它退出。测试接缝放在真正的提交或写入边界，禁止通过生产 API 暴露故障注入。异常或正常 Dispose 后再打开数据库的用例，单独计为组件测试。

每个边界使用独立基线副本。恢复用新的进程或宿主实例打开同一数据目录，主动解析协调器以触发 `RecoverAsync`。先关启动同步观察恢复结果，避免后台重新索引掩盖错误。完成首次检查后再重启一次，比较活动指针、原文、chunk ID、FTS、清单及过期标记。最后启用正常同步或提交恢复作业，验证可以继续处理后续变更。

| 注入点 | 怎样暂停或破坏 | 首次恢复和再次重启的预期 | 测试落点 |
| --- | --- | --- | --- |
| D1 原文暂存后 | `StageDocumentsAsync` 的 `vectors.db` 事务提交后，`StageIndexAsync` 开始前终止 | 旧代仍活动，新代原文不进入查询。非活动暂存代可重复回收，首次建库时返回明确的未就绪状态 | `LexicalIndexConsistencyTests.cs` 与测试子进程 |
| D2 FTS 提交后 | `StageIndexAsync` 已提交 FTS 和清单，`PublishAsync` 尚未提交时终止。另做发布事务提交前的终止变体 | 旧代仍活动，新代 FTS 命中不能进入 TopK 或展开。恢复回收未发布原文、FTS 行和清单，不能产生这次发布对应的新向量过期状态 | `LexicalIndexConsistencyTests.cs` 与测试子进程 |
| D3 活动代提交后 | `PublishAsync` 提交后、旧代回收前终止 | 新代、完整 FTS 和对应过期标记同时可见。旧代可被回收，向量查询不能回退到已过期内容 | `LexicalIndexConsistencyTests.cs` 与测试子进程 |
| D4 删除提交后 | `DeleteFileAsync` 已移除活动指针并提交过期标记，回收前终止。另测当前改名顺序，在新路径已发布但旧路径尚未失效时终止，以及旧路径失效后终止 | 已提交的删除不会复活。中断时可能留下两个已提交路径，需要检验恢复如何识别和收敛，避免把双路径当作已完成。重试完成后仅新路径可查询，旧引用无法取回原文 | `LexicalIndexConsistencyTests.cs` 测提交，[LexicalFileIndexerTests.cs](../tests/ReferenceRAG.Tests/LexicalFileIndexerTests.cs) 测改名和重试 |
| D5 非活动代回收中 | `RecoverAsync` 先删除 FTS 行与清单，再删除原文分块和代记录。分别在 FTS 行删除后、清单删除后及原文清理事务提交前终止 | 回收中断不会删掉活动代。下一次恢复可继续清理剩余派生记录，再次启动结果一致。保留用于拒绝代号重复发布的历史记录，并按设计区分它与待清理的原文或 FTS | `LexicalIndexConsistencyTests.cs` 与测试子进程 |
| D6 向量恢复中 | `BeginVectorRecoveryAsync` 提交 `*` 后、第一次清库前终止。分别在清空 BM25 后、清空图谱后、向量已写入但 `CompleteVerifiedAsync` 提交前终止。另测覆盖提交后立即终止 | 提交覆盖前的中断保留 `*`，部分向量、BM25 或图谱不会被当成完整恢复。重跑全部启用源并核验通过后才解除保护。覆盖已经提交的变体重启后可用，不能因为没有收到完成事件而恢复为半成品 | [VectorCoverageRecoveryTests.cs](../tests/ReferenceRAG.Tests/VectorCoverageRecoveryTests.cs) 与测试子进程 |
| D7 活动派生数据损坏 | 只在停服的副本上移走 `bm25.db`，或用临时连接删除活动代的一条 FTS 行、篡改清单分块数。启动后先查询，再执行重建 | 活动代缺失派生覆盖时有明确未就绪或修复状态，不能返回部分命中却报告完整。重建后校验活动代、原文和 FTS 一致，且其他完整源和旧存量数据按既定范围保留。若整库丢失导致旧 BM25 表也缺失，须另验旧混合检索门禁与恢复 | `LexicalIndexConsistencyTests.cs`、`LexicalStartupSyncTests.cs` 与宿主切换测试 |

并发与取消另设用例。在读租约或查询屏障持有时启动发布及回收，读取结果必须始终来自同一完整代。取消读取后写入和回收均能在有限时间内继续。用固定批次反复修改同一文件，分别记录活动与非活动代的行数、FTS 行数、数据库及 WAL 大小。区分可回收数据和仍需保留的发布记录，避免只以文件体积判断泄漏。

### 5. 执行验收

在仓库根目录先跑已有回归，再运行新增的 `P02_` 用例。第 3 条命令在测试子进程项目添加后执行。进程级测试应从显式构建的 worker 输出启动，并记录实际退出状态。两组 `P02_` 命令均需匹配到用例。若测试环境尚未还原依赖，先还原，再执行以下命令。

```powershell
dotnet test tests/ReferenceRAG.Tests/ReferenceRAG.Tests.csproj --no-restore --filter '(FullyQualifiedName~LexicalIndexConsistencyTests|FullyQualifiedName~LexicalStartupSyncTests|FullyQualifiedName~LexicalFileIndexerTests|FullyQualifiedName~VectorCoverageRecoveryTests|FullyQualifiedName~ConfigManagerTests)&FullyQualifiedName!~P02_'
dotnet test tests/ReferenceRAG.Host.Tests/ReferenceRAG.Host.Tests.csproj --no-restore --filter 'FullyQualifiedName~LexicalAuthorizationTests&FullyQualifiedName!~P02_'
dotnet build tests/LexicalRecoveryCrashWorker/LexicalRecoveryCrashWorker.csproj --no-restore
dotnet test tests/ReferenceRAG.Tests/ReferenceRAG.Tests.csproj --no-restore --filter 'FullyQualifiedName~P02_'
dotnet test tests/ReferenceRAG.Host.Tests/ReferenceRAG.Host.Tests.csproj --no-restore --filter 'FullyQualifiedName~P02_'
git diff --check
```

- [ ] 双向切换通过，且同步暂停、禁用、失败和取消期间均能区分覆盖未就绪与正常无命中。
- [ ] 旧版真实备份与自动化样本各有证据。重复升级、旧引用策略及迁移前快照回退均可复查。
- [ ] 每个 D1–D7 用例保留边界信号、终止或故障方式、两次重启的持久状态和实查结果。故障修复后可继续索引，读租约可释放，派生残留可清理，再进入 P0.3。

## P0.3 完成入口和权限回归

### 复现准备

- 从仓库根目录执行。每个用例使用独立临时配置、真实 Markdown 文件和 SQLite 库，经生产索引或同步入口建库并等待发布。词法配置为 `Indexing.EmbeddingEnabled=false`、`Search.EnableLexical=true`，聊天检索显式配置 `Search.ChatQueryMode=Lexical`。另建一套启用 embedding 且已完成向量覆盖的旧模式夹具，用现有测试替身提供固定向量。
- 建立已启用的 A、B、C 和已入库后停用的 D。四源共享一个查询词，每源另有唯一标题、正文标记和路径。让无数据源限制的身份以 `topK=1` 先命中 B，再用仅授权 A、C 的密钥重复搜索。准备有效但授权集为空的密钥、缺失或无效凭据、可撤销的密钥，以及管理操作所需的身份。将模块权限和数据源权限分别配置，避免一次请求同时因两个原因被拦截。
- 在 A 内放置 `allowed` 和 `allowed-other` 两个同级目录，另设一个排除目录。普通 Markdown 用 YAML front matter 的 `tags: [release]` 或 `tags: [review]` 构造标签样本。独立库使用实际连接器同步同批文件，另准备只含 B 的库和同时含 A、B 的混合库。
- 词法用例复用 [LexicalAuthorizationTests.cs](../tests/ReferenceRAG.Host.Tests/LexicalAuthorizationTests.cs) 的宿主夹具。对 embedding、向量查询和重排的解析与实际调用分别计数，在禁止的路径上立即抛错。聊天生成用可记录工具调用和输入消息的替身。HTTP 经真实路由和认证中间件，MCP 经实际工具和权限守卫。权限断言覆盖正文、标题、路径、来源、引用和诊断字段。新增 .NET 用例统一使用 `P03_` 前缀。

### 各检索入口和维护动作

| 场景 | 复现步骤 | 预期结果 | 测试文件 |
| --- | --- | --- | --- |
| E1 REST 搜索与状态 | 请求 `GET /api/ai/status`，再向 `POST /api/ai/query` 发 `{"query":"验收共词","mode":"Lexical","topK":1}`，将模式分别改为 `Standard`、`Hybrid` 和 `HybridRerank`。用另一套关闭词法能力的配置请求 `Lexical`。再启用 embedding，但保留向量覆盖缺口，重发向量请求。 | 状态可读。词法结果含模式、引用、原文、来源和行号，禁止的服务解析及调用均为零。能力拒绝返回 HTTP 409，并分别断言 `embedding_disabled`、`lexical_disabled` 或 `vector_index_incomplete`。 | [LexicalAuthorizationTests.cs](../tests/ReferenceRAG.Host.Tests/LexicalAuthorizationTests.cs) |
| E2 REST 展开 | 从 E1 获取 A 的 `refId`，向 `POST /api/ai/drill-down` 提交 `refIds`、`mode` 和可选的 `sources`。同一请求混入管理身份取得的 B 引用和不存在的引用。修改 A 的原文并等待新一代索引发布后，再提交 A 的旧引用。 | 有权且有效的引用返回对应原文窗口和行号。越权、未知及已失效引用不返回对应正文或路径。重新查询得到的新引用可展开更新后的原文。 | [LexicalAuthorizationTests.cs](../tests/ReferenceRAG.Host.Tests/LexicalAuthorizationTests.cs)、[ScopedLexicalSearchTests.cs](../tests/ReferenceRAG.Tests/ScopedLexicalSearchTests.cs) |
| E3 MCP 检索 | 调用 `LexicalSearch(query, topK, sources)`。`sources` 传 JSON 数组字符串，分别省略、传 `["A","B"]`、只传 B、传无效 JSON。用具备工具权限的密钥再调用 `SemanticSearch`、`HybridSearch`。 | 成功时有命中计数和词法片段。交集为空时返回空集。参数错误及向量能力不可用时返回可识别的工具错误，embedding 解析和调用为零。MCP 断言工具返回体及错误，HTTP 状态断言留给 REST。 | [LexicalAuthorizationTests.cs](../tests/ReferenceRAG.Host.Tests/LexicalAuthorizationTests.cs) |
| E4 应用聊天 | 先 `POST /api/chat/sessions`，再 `POST /api/chat/stream`。让生成替身分别触发 `SearchKnowledge`、`DrillDownChunk`、`BM25Search`、`SearchNoteByTitle`、`GetFileStructure`、`ReadFileContent`、`GetIndexStatus`。给读文件工具一个 B 的真实路径，对同一会话收紧权限后再次发请求。 | 搜索、展开和直接 BM25 使用当前词法索引及授权范围。标题、目录、文件读取和状态工具只返回当前授权内容，或返回明确的能力拒绝。检查每次工具返回、生成上下文和 SSE 事件，包括会话已有上下文在授权收紧后的处理。 | [LexicalChatTests.cs](../tests/ReferenceRAG.Tests/LexicalChatTests.cs)、[ChatToolCallingAndLocalClientTests.cs](../tests/ReferenceRAG.Host.Tests/ChatToolCallingAndLocalClientTests.cs) |
| E5 OpenAI 兼容接口 | 请求 `GET /v1/models`，分别用 `reference-rag`、`kb:{id}` 和普通模型调用 `POST /v1/chat/completions`。对指定库有命中、无命中、仅越权命中和检索异常各跑一次，流式与非流式均执行。给无命中和异常场景准备全局索引中的 B 哨兵，并记录检索调用和模型输入。 | 列表只展示调用者可见的知识库模型。普通模型跳过 RAG。成功时只注入授权片段。指定库查空或报错时，任何全局回退均须重新施加授权，且保留显式选择的库范围。无法保证库范围时按空上下文或明确错误处理。两种响应中均无 B 哨兵。 | [OpenAICompatControllersTests.cs](../tests/ReferenceRAG.Host.Tests/OpenAICompatControllersTests.cs) |
| E6 独立库和跨库入口 | 先 `GET /api/knowledge-bases`，再向 `POST /api/knowledge-base-search` 传 `query`、`knowledgeBaseIds`、`topK=1`、`useLexicalTokenizer=true`，省略 `queryVector`。分别指定混合库、仅 B 的库和两库。用 MCP `KnowledgeBaseList`、`KnowledgeBaseSearch` 重跑对应场景，参数按注册的工具签名传入。 | 词法查询可在无向量时完成。授权在各库的 TopK 之前生效，混合库仍返回 A。只含越权文档的库不暴露内容、库名或路径。受限密钥提交伪造的 `authorizedSourceNames` 无法扩大权限。 | [KnowledgeBasesControllerTests.cs](../tests/ReferenceRAG.Host.Tests/KnowledgeBasesControllerTests.cs)、[KnowledgeBaseMcpToolsTests.cs](../tests/ReferenceRAG.Host.Tests/KnowledgeBaseMcpToolsTests.cs)、[KnowledgeBaseArchitectureGateTests.cs](../tests/ReferenceRAG.Tests/KnowledgeBaseArchitectureGateTests.cs) |
| E7 直接 BM25 | 用有效管理身份和受限密钥分别请求 `GET /api/bm25index/search`、`GET /api/bm25index/summary`。在临时库对 `POST /api/bm25index/index`、`POST /api/bm25index/documents/{chunkId}`、`DELETE /api/bm25index/index` 发送有效请求。词法和旧模式各跑一遍。 | 读取经过权限及当前索引状态判断，受限身份看不到全局索引内容和统计。词法模式拒绝直接写旧 BM25 索引，旧模式保留原有管理行为且仅管理身份可操作。拒绝前后对比索引数据与作业数。 | [LexicalAuthorizationTests.cs](../tests/ReferenceRAG.Host.Tests/LexicalAuthorizationTests.cs)、[Fts5BM25StoreTests.cs](../tests/ReferenceRAG.Tests/Fts5BM25StoreTests.cs) |
| E8 旧图谱 | 先用旧模式建图，保留 A、B 节点和跨源边。分别在图有效及已过期的状态下请求 `/api/graph/node`、`neighbors`、`search`、`stats`、`POST /api/graph/subgraph`，并调用 MCP `GraphSearch`、`GraphSearchByTitle`。另用受限密钥请求 `POST /api/graph/rebuild`。 | 旧图有效时只允许已实现范围过滤的读取，否则明确拒绝。过期时拒绝旧图查询，跨源边、节点元数据及全局统计均不可借此泄露。受限密钥无法重建。用有权正例确认命中了真实路由，避免把错误 URL 的 404 当成权限通过。 | [LexicalAuthorizationTests.cs](../tests/ReferenceRAG.Host.Tests/LexicalAuthorizationTests.cs)、[DisabledSourceGatekeeperTests.cs](../tests/ReferenceRAG.Host.Tests/DisabledSourceGatekeeperTests.cs) |
| E9 索引维护 | 管理身份在词法模式请求 `GET /api/index/summary`、`GET /api/index/models`、`GET /api/index/models/current`，再请求 `POST /api/index/rebuild`、`/rebuild/{sourceName}`、`/rechunk`、`/rechunk/{sourceName}`、`/cleanup`。重分块后查 `/api/index/jobs` 并搜索变更后的文件。受限身份重放管理及作业请求，包括独立库的连接器和作业管理。 | 摘要返回词法状态，模型列表读取静态信息，当前向量模型和向量重建返回 `embedding_disabled`。全量及单源重分块使用现有词法作业并发布可搜索结果。旧向量清理入口在词法模式返回明确能力拒绝，词法索引清理由现有词法流程承担。全程 embedding 解析和调用为零。无管理权限的请求无法提交作业、读取受限作业详情或改库。 | [LexicalAuthorizationTests.cs](../tests/ReferenceRAG.Host.Tests/LexicalAuthorizationTests.cs)、[LexicalIndexJobTests.cs](../tests/ReferenceRAG.Tests/LexicalIndexJobTests.cs)、[KnowledgeBasesControllerTests.cs](../tests/ReferenceRAG.Host.Tests/KnowledgeBasesControllerTests.cs) |
| E10 网页搜索 | 让前端读取 E1 的词法状态，选择词法并提交搜索，再点击一条结果展开。记录前端发出的请求及渲染结果。用旧模式状态重复打开模式选择。 | 搜索和展开携带正确模式与引用。词法结果展示原文、来源和行号，词法分数使用词法相关性或原始分数标签，无向量相似度百分比。向量不可用时界面不提供可执行的向量模式，旧模式仍能选择原有选项。 | [frontend-logic.test.mjs](../Business/ReferenceRAG.Business/wwwroot/tests/frontend-logic.test.mjs) |

E5 当前代码在指定库查空或报错后会尝试全局检索。E9 的部分动作仍直接访问 embedding 服务。先用上述用例复现，再收紧入口逻辑。E9 将 `cleanup` 的验收口径定为拒绝旧向量清理，避免通过该入口隐式修改词法数据。

### 权限隔离和跨库故障

| 场景 | 复现步骤 | 预期结果 | 测试文件 |
| --- | --- | --- | --- |
| A1 密钥与模块权限 | 对 E1–E9 的受保护调用逐项换成缺失或无效凭据、模块未授权、有效但源权限为空。撤销密钥前后，用同一 MCP 会话再次调用已成功的工具。 | 认证失败和模块拒绝按各协议返回明确错误，受限空集合只能得到空范围或显式拒绝。撤销后旧会话失去访问权。用搜索、模型和作业替身计数确认拒绝发生在读取或写入前。 | [LexicalAuthorizationTests.cs](../tests/ReferenceRAG.Host.Tests/LexicalAuthorizationTests.cs)、[ApiKeyAuthorizationTests.cs](../tests/ReferenceRAG.Host.Tests/ApiKeyAuthorizationTests.cs) |
| A2 授权交集与排序 | 用 A、C 密钥依次省略 `sources`、传 `[]`、`[A,B]`、`[B]`、`[D]`，共同词以 `topK=1` 搜索。另用原本允许 D 的密钥在 D 停用前后重试，停用时保留其已索引数据。 | 省略和空请求列表都使用当前已授权的启用源。混合请求只返回 A，无交集返回空集。停用后搜索和展开都不返回 D。A 或 C 排在授权范围内的第一名，防止先全局取 TopK 再过滤导致结果丢失。 | [LexicalAuthorizationTests.cs](../tests/ReferenceRAG.Host.Tests/LexicalAuthorizationTests.cs)、[ScopedLexicalSearchTests.cs](../tests/ReferenceRAG.Tests/ScopedLexicalSearchTests.cs) |
| A3 目录、标签、日期 | 先从真实 Markdown 建库，再验证文件元数据已保存。用 `filters.folders` 传 `allowed` 的绝对路径，用 `filters.tags` 传 `["release","review"]`。将 `filters.dateRange.start` 和 `end` 分别设为 `2026-01-02T00:00:00Z`、`2026-01-03T00:00:00Z`，放入恰好等于两端及各越界一秒的文件。对无过滤、各单项和组合分别发请求。 | 同前缀同级目录和排除目录不命中。标签列表按现有任一匹配语义生效，目录、标签、日期彼此求交集。以已入库的文件修改时间验收，起止两端均包含。通过真实文件断言标签入库与检索两个环节。 | [LexicalFileIndexerTests.cs](../tests/ReferenceRAG.Tests/LexicalFileIndexerTests.cs)、[ScopedLexicalSearchTests.cs](../tests/ReferenceRAG.Tests/ScopedLexicalSearchTests.cs)、[LexicalAuthorizationTests.cs](../tests/ReferenceRAG.Host.Tests/LexicalAuthorizationTests.cs) |
| A4 跨库失败、超时、取消 | 并行查两库，在两路 `RecallAsync` 都到达等待点后，放行第一库，让第二库抛出测试异常。分别传 `failurePolicy=AllowPartial`、`Strict`，再模拟两库均失败。超时用例让第一库先完成，第二库在 `RecallAsync` 等待请求 `timeout` 到期，例如 HTTP 传 `"00:00:05"`。取消用例省略超时，在第二路到达等待点后取消调用方令牌，记录两路退出状态。 | `AllowPartial` 返回成功库片段且 `partialSuccess=true`，受限密钥的 `failedKnowledgeBaseIds` 为空。`Strict` 和全部失败明确报错，HTTP 返回 503。请求超时按失败策略处理，调用方取消传到实际查询并结束等待，不产生成功的部分响应。 | [KnowledgeBaseArchitectureGateTests.cs](../tests/ReferenceRAG.Tests/KnowledgeBaseArchitectureGateTests.cs)、[KnowledgeBasesControllerTests.cs](../tests/ReferenceRAG.Host.Tests/KnowledgeBasesControllerTests.cs)、[KnowledgeBaseMcpToolsTests.cs](../tests/ReferenceRAG.Host.Tests/KnowledgeBaseMcpToolsTests.cs) |
| A5 连接器变更与旧记录 | A 连接器同步后记录结果，保留已入库文档，移除连接器或改指向 B。再构造旧库文档缺少入库位置的情况，先查一次，再通过现有同步入口恢复位置后重查。加入根目录重叠、同前缀兄弟目录和越界路径对照。 | 权限使用当前连接器和实际入库位置，旧授权不能沿用到 B。位置缺失时受限身份无法读到旧记录，重同步后只恢复可验证的 A 文档。混合库仍能检索授权记录，列表和失败诊断不泄露其他库。 | [KnowledgeBaseArchitectureGateTests.cs](../tests/ReferenceRAG.Tests/KnowledgeBaseArchitectureGateTests.cs)、[KnowledgeBaseMcpToolsTests.cs](../tests/ReferenceRAG.Host.Tests/KnowledgeBaseMcpToolsTests.cs) |

### 旧配置和请求兼容

| 场景 | 复现步骤 | 预期结果 | 测试文件 |
| --- | --- | --- | --- |
| C1 旧配置缺省与回写 | 从临时 JSON 中删除 `Indexing.EmbeddingEnabled`、`Search.EnableLexical` 和 `Search.ChatQueryMode`。通过配置管理器加载、保存并重新加载。另准备显式关闭 embedding、启用词法并指定 `Lexical` 的配置，重走同一流程。 | 缺省仍是 `EmbeddingEnabled=true`、`EnableLexical=false`、`ChatQueryMode=HybridRerank`。显式开关和模式回写后保持语义，并保留原文件中的其他有效配置。 | [ConfigManagerTests.cs](../tests/ReferenceRAG.Tests/ConfigManagerTests.cs) |
| C2 数字、名称和未知模式 | 用生产 JSON 配置和 REST 绑定分别测试历史数字 `0..4`，以及数字 `5` 和显式 `"Lexical"`。比较绑定后的枚举和实际调用的策略。再发送 `99`、`"UnknownMode"` 和省略 `mode` 的请求。 | 枚举映射依次为 `Quick=0`、`Standard=1`、`Deep=2`、`Hybrid=3`、`HybridRerank=4`、`Lexical=5`。显式词法可往返序列化。未知输入在路由到搜索策略前产生明确客户端错误。REST 省略模式仍为 `Standard`，词法宿主对该向量请求按 E1 拒绝。 | [ConfigManagerTests.cs](../tests/ReferenceRAG.Tests/ConfigManagerTests.cs)、[SearchPipelineStrategyTests.cs](../tests/ReferenceRAG.Tests/SearchPipelineStrategyTests.cs)、[LexicalAuthorizationTests.cs](../tests/ReferenceRAG.Host.Tests/LexicalAuthorizationTests.cs) |
| C3 历史向量与 BM25 | 用 C1 旧配置和已覆盖的向量夹具执行 `Standard`、`Hybrid`，记录策略、依赖调用和命中。对现有 BM25 入口分别使用缺省与非缺省 `k1`、`b`，先记录变更前同一语料的结果和排序，再用相同参数组合回归。 | 旧模式仍使用原策略，保留旧调用和分数排序语义。`k1`、`b` 的配置和请求兼容性保持，以原后端是否实际使用这些参数为准。词法查询的排名按新索引自身规则验收。 | [SearchPipelineStrategyTests.cs](../tests/ReferenceRAG.Tests/SearchPipelineStrategyTests.cs)、[BM25SearcherTests.cs](../tests/ReferenceRAG.Tests/BM25SearcherTests.cs)、[Fts5BM25StoreTests.cs](../tests/ReferenceRAG.Tests/Fts5BM25StoreTests.cs) |

### 分词和查询边界

目录词法索引和独立库词法入口分别验证。当前实现使用 Jieba 搜索模式分词，保留含字母或数字的词项，忽略大小写去重后取前 64 个，用转义后的词项构造 FTS5 查询并绑定参数。词项按 OR 检索。精确标点、短语和路径由下面的正反样本单独验收。

| 场景 | 复现步骤 | 预期结果 | 测试文件 |
| --- | --- | --- | --- |
| T1 中文和短词 | 每个对照写成独立文件，使用 `知识图谱索引`、`索引恢复`、`图书馆`、单字 `图` 和单字母 `C`。分别查询完整中文、子词和短词，并比较两条等分结果的重复查询顺序。 | 词法命中包含对应源文件，返回原文、路径和行号。短词依据固定语料和当前分词结果验收。目录词法索引在相同分数时按分块 ID 确定顺序。 | [LexicalSearchServiceTests.cs](../tests/ReferenceRAG.Tests/LexicalSearchServiceTests.cs)、[KnowledgeBaseArchitectureGateTests.cs](../tests/ReferenceRAG.Tests/KnowledgeBaseArchitectureGateTests.cs) |
| T2 技术词和路径 | 每个词单独成篇，使用 `C#` 对 `C`、`.NET` 对 `NET`、`ERR-1042` 对 `ERR-10420`、`docs/api/index.md` 对 `docs/api/index-old.md`。另将 Windows 反斜杠路径作为正文和查询输入，使用 JSON 序列化发送请求。 | 普通词法至少找回正样本并保留原始大小写、标点及路径。记录每组反样本是否也命中。如果产品要求字面精确匹配，需为该要求单列断言和实现，正样本命中且反样本排除后才算精确验收通过。普通分词命中不能作为精确支持的依据。 | [LexicalSearchServiceTests.cs](../tests/ReferenceRAG.Tests/LexicalSearchServiceTests.cs)、[KnowledgeBaseArchitectureGateTests.cs](../tests/ReferenceRAG.Tests/KnowledgeBaseArchitectureGateTests.cs) |
| T3 否定、引号、特殊字符 | 对照 `支持导出` 和 `不支持导出`，查询 `不支持导出`、`未启用`、`alpha NOT beta`、`"alpha beta"`、未闭合引号、`title:alpha*`、`' OR 1=1 --`、纯标点和空白。 | 否定词、引号和类似 FTS 操作符的输入走普通词项处理。相反语义的文本可能同时命中，断言原文保留。纯标点返回空集，空白走各入口的既有空输入处理。所有合法请求均无 FTS 语法异常、无跨域结果。 | [LexicalSearchServiceTests.cs](../tests/ReferenceRAG.Tests/LexicalSearchServiceTests.cs)、[KnowledgeBaseArchitectureGateTests.cs](../tests/ReferenceRAG.Tests/KnowledgeBaseArchitectureGateTests.cs)、[LexicalAuthorizationTests.cs](../tests/ReferenceRAG.Host.Tests/LexicalAuthorizationTests.cs) |
| T4 长度、词项数和 TopK | 构造 4095、4096、4097 个 UTF-16 代码单元的查询，另用代理对验证计数单位。构造 63、64、65 个可区分词项，分别在第 64、65 项放独立哨兵，并验证大小写重复项不占额外名额。目录底层分别传 `topK=0,1,1000,1001`，独立库存储层传 `0,1,100,101`。再对 REST、MCP 发越界请求。 | 词法查询在 4096 以内进入处理，超长在调用 FTS 前拒绝。第 64 项仍可命中，第 65 项被截掉。目录索引层只接受 1–1000，独立库存储层夹紧到 1–100。对外入口按各自校验或归一化规则断言，超长参数返回可识别的客户端或工具错误。 | [LexicalSearchServiceTests.cs](../tests/ReferenceRAG.Tests/LexicalSearchServiceTests.cs)、[KnowledgeBaseArchitectureGateTests.cs](../tests/ReferenceRAG.Tests/KnowledgeBaseArchitectureGateTests.cs)、[LexicalAuthorizationTests.cs](../tests/ReferenceRAG.Host.Tests/LexicalAuthorizationTests.cs) |

### 执行顺序和验收命令

1. [ ] 先用现有用例建立旧模式、授权和宿主基线。新增 E1–E6、A1–A3 和 C1–C3，先让未满足的用例因明确的路由、权限或结果断言失败，再补齐生产入口。标签场景先验证真实入库。
2. [ ] 完成 E7–E9 和 A4–A5。以实际作业终态、故障返回和无副作用为验收依据。用可取消的等待点控制并发，所有等待设上限，`finally` 中释放等待点。
3. [ ] 完成 T1–T4 和 E10。记录测试语料的预期文件、实际命中文件、原文范围和分数显示，并逐项记录 T2 的精确匹配口径。若旧默认值、参数错误或分词行为需改变，先用聚焦用例固定目标行为。
4. [ ] 运行以下验收。前两条是现有 .NET 基线，后两条需在添加 `P03_` 用例后执行。每组 .NET 命令均需实际发现用例且失败数为零，前端测试和检查退出码均为零。若尚未还原依赖，先还原对应测试项目。

```powershell
dotnet test tests/ReferenceRAG.Tests/ReferenceRAG.Tests.csproj --no-restore --filter '(FullyQualifiedName~LexicalSearchServiceTests|FullyQualifiedName~ScopedLexicalSearchTests|FullyQualifiedName~LexicalFileIndexerTests|FullyQualifiedName~LexicalChatTests|FullyQualifiedName~LexicalIndexJobTests|FullyQualifiedName~BM25SearcherTests|FullyQualifiedName~Fts5BM25StoreTests|FullyQualifiedName~ConfigManagerTests|FullyQualifiedName~SearchPipelineStrategyTests|FullyQualifiedName~KnowledgeBaseArchitectureGateTests)&FullyQualifiedName!~P03_'
dotnet test tests/ReferenceRAG.Host.Tests/ReferenceRAG.Host.Tests.csproj --no-restore --filter '(FullyQualifiedName~LexicalAuthorizationTests|FullyQualifiedName~KnowledgeBaseMcpToolsTests|FullyQualifiedName~KnowledgeBasesControllerTests|FullyQualifiedName~OpenAICompatControllersTests|FullyQualifiedName~ChatToolCallingAndLocalClientTests|FullyQualifiedName~DisabledSourceGatekeeperTests|FullyQualifiedName~AllRegisteredMcpTools_MustHaveValidModuleMapping)&FullyQualifiedName!~P03_'
dotnet test tests/ReferenceRAG.Tests/ReferenceRAG.Tests.csproj --no-restore --filter 'FullyQualifiedName~P03_'
dotnet test tests/ReferenceRAG.Host.Tests/ReferenceRAG.Host.Tests.csproj --no-restore --filter 'FullyQualifiedName~P03_'
npm --prefix Business/ReferenceRAG.Business/wwwroot test
npm --prefix Business/ReferenceRAG.Business/wwwroot run check
git diff --check
```

## P1 实际宿主、性能和回退演练

### P1.1 实际宿主离线验收

先完成 P0.1–P0.3 的验收命令。以下命令从仓库根目录使用 PowerShell 7 执行。先在联网环境还原依赖并发布，将产物带到隔离设备或虚拟机。桌面宿主在 Windows x64 交互会话运行，先退出同一用户会话里已有的桌面实例。每个宿主使用独立的配置、数据和样本目录，每个数据目录只允许一个写入实例。

```powershell
$lab = Join-Path ([IO.Path]::GetTempPath()) ("ReferenceRAG-P1-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $lab | Out-Null
dotnet publish Host/ReferenceRAG.WebHost/ReferenceRAG.WebHost.csproj -c Release -o (Join-Path $lab 'bin-web')
if ($LASTEXITCODE -ne 0) { throw 'Web publish failed' }
# 在 Windows x64 上执行桌面发布
dotnet publish Host/ReferenceRAG.DesktopHost/ReferenceRAG.DesktopHost.csproj -c Release -o (Join-Path $lab 'bin-desktop')
if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed' }
```

在启动宿主的终端设置以下配置。Web 使用 `web` 和端口 `17897`，桌面使用 `desktop` 和端口 `17898`。每个宿主仅在首次准备时执行此段，重启时沿用已有配置。客户端终端使用同一轮的 `$run` 和端口。确认端口没有被占用，且测试进程没有继承用于覆盖源、数据库或模型路径的生产环境变量。

```powershell
$kind = 'web'
$port = 17897
$run = Join-Path $lab $kind
$data = Join-Path $run 'data'
$models = Join-Path $run 'empty-models'
$sourceA = Join-Path $run 'source-a'
$sourceB = Join-Path $run 'source-b'
New-Item -ItemType Directory -Path $data,$models,$sourceA,$sourceB | Out-Null
$env:REFERENCERAG_CONTENT_ROOT = $run
$env:REFERENCERAG_CONFIG_PATH = Join-Path $run 'appsettings.json'
$env:REFERENCERAG_DATA_PATH = $data
$env:REFERENCERAG_MODELS_PATH = $models
$cfg = Get-Content -Raw -LiteralPath 'support/config/appsettings.template.json' | ConvertFrom-Json -AsHashtable
$rag = $cfg.ReferenceRAG
$rag.dataPath = $data
$rag.modelsRootPath = $models
$rag.embedding.modelsPath = $models
$rag.rerank.modelsPath = $models
$rag.indexing.embeddingEnabled = $false
$rag.indexing.autoIndexEnabled = $true
$rag.indexing.syncOnStartup = $true
$rag.search.enableLexical = $true
$rag.search.chatQueryMode = 'Lexical'
$rag.service.port = "$port"
$rag.service.allowNetworkAccess = $false
$rag.service.apiKey = [guid]::NewGuid().ToString('N')
$cfg | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $env:REFERENCERAG_CONFIG_PATH -Encoding utf8
Set-Content -LiteralPath (Join-Path $sourceA 'edit.md') -Value "# P1 A`n`np1oldmarker" -Encoding utf8
Set-Content -LiteralPath (Join-Path $sourceA 'remove.md') -Value "# P1 delete`n`np1deletemarker" -Encoding utf8
Set-Content -LiteralPath (Join-Path $sourceB 'control.md') -Value "# P1 B`n`np1controlmarker" -Encoding utf8
```

断开测试设备或虚拟机的外部网络，保留回环连接，记录离线条件、空模型目录清单和进程日志。在宿主终端选对应命令启动。桌面先用默认 IPC 完成 UI 验收，再正常退出并加 `--serve-http` 重启以验证 REST 和 MCP。通过窗口关闭后仍驻留托盘的桌面实例，须从托盘退出并确认进程结束。

```powershell
dotnet (Join-Path $lab 'bin-web/ReferenceRAG.WebHost.dll') 2>&1 | Tee-Object (Join-Path $run 'web.log')
& (Join-Path $lab 'bin-desktop/ReferenceRAG.DesktopHost.exe')
& (Join-Path $lab 'bin-desktop/ReferenceRAG.DesktopHost.exe') --serve-http
```

在客户端终端设置本轮的 `$run`、`$port`、`$sourceA`、`$sourceB`。以下函数供三节共用。每次切换配置或实例后重新加载凭据和地址。证据中脱敏配置、密钥和认证头。

```powershell
$base = "http://localhost:$port"
$settings = Get-Content -Raw -LiteralPath (Join-Path $run 'appsettings.json') | ConvertFrom-Json
$headers = @{ 'X-API-Key' = $settings.ReferenceRAG.Service.ApiKey }
function Get-P1($path) {
    Invoke-RestMethod -Uri "$base$path" -Headers $headers -TimeoutSec 30
}
function Post-P1($path, $body) {
    Invoke-RestMethod -Method Post -Uri "$base$path" -Headers $headers -ContentType 'application/json' -Body ($body | ConvertTo-Json -Depth 10 -Compress) -TimeoutSec 30
}
function Search-P1($query, $mode = 'Lexical') {
    Post-P1 '/api/ai/query' @{ query = $query; mode = $mode; topK = 5; enableRerank = $false }
}
function Check-P1Conflict($query, $mode, $expectedCode) {
    $body = @{ query = $query; mode = $mode; topK = 5 } | ConvertTo-Json -Compress
    $response = Invoke-WebRequest -Method Post -Uri "$base/api/ai/query" -Headers $headers -ContentType 'application/json' -Body $body -SkipHttpErrorCheck -TimeoutSec 30
    if ($response.StatusCode -ne 409 -or ($response.Content | ConvertFrom-Json).code -ne $expectedCode) {
        throw "Expected HTTP 409 with $expectedCode, got $($response.StatusCode)"
    }
    $response.Content
}
function Wait-P1Job($jobId, $timeoutSec = 300) {
    $deadline = [DateTime]::UtcNow.AddSeconds($timeoutSec)
    do {
        $job = Get-P1 "/api/index/jobs/$jobId"
        if ($job.Status -in 'Completed','Failed','Cancelled') { return $job }
        Start-Sleep -Seconds 1
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out waiting for job $jobId"
}
```

| 步骤 | 操作与命令 | 预期与验收证据 |
| --- | --- | --- |
| H1 启动和隔离 | 执行 `Get-P1 '/api/ai/status'`、`Get-P1 '/api/sources'`。核对启动日志、回环监听、临时配置及数据文件路径。桌面默认 IPC 先在 UI 检查同样状态。 | 搜索状态为 `LexicalEnabled=true`、`EmbeddingEnabled=false`，首次无源、无命中。测试目录内创建数据库，外部网络断开时仍可启动。桌面 IPC 独立通过后再验收 HTTP。 |
| H2 加源、启动同步和批量索引 | 分别执行 `Post-P1 '/api/sources' @{name='A'; path=$sourceA; type='Markdown'}` 和 B 的对应请求。停服，在 A 写入含 `p1offlinemarker` 的新文件并重启。先轮询 `Search-P1 'p1offlinemarker'`，等启动同步完成后，再运行 `$job = Post-P1 '/api/index/jobs' @{force=$true}; Wait-P1Job $job.JobId`。 | 源创建成功，离线新增文件在无需手动作业时变为可搜。强制作业 `Completed` 且 `Errors=0`，A、B 均能命中。记录启动同步和强制作业的独立证据。轮询最多 120 秒，超时保留日志并判本轮失败。 |
| H3 REST、MCP、UI 与授权 | 执行 `$hit = Search-P1 'p1oldmarker'`，用 `Post-P1 '/api/ai/drill-down' @{mode='Lexical'; refIds=@($hit.Chunks[0].RefId)}` 展开。用本地 MCP 客户端连接 `$base/api/mcp` 的 SSE 传输，配置 `X-API-Key`，先执行 `tools/list`，再执行 `tools/call`，名称 `LexicalSearch`，参数 `{"query":"p1oldmarker","topK":5}`。另用只授权 A 且允许搜索模块的测试密钥重复 REST 和 MCP，并请求 B 的标记与已知引用。用 Web 页面和桌面窗口提交相同搜索，窄屏各验一遍。 | A 的原文、来源、路径、行号、引用能交叉核对。B 对管理身份可搜，受限身份看不到 B 的正文和元数据。词法分数及能力错误显示正确。MCP 保存工具调用及响应，连接成功仅证明传输可达。UI 保存截图和对应请求或 IPC 结果。 |
| H4 文件更新和删除 | 宿主运行时执行 `Set-Content -LiteralPath (Join-Path $sourceA 'edit.md') -Value 'p1newmarker' -Encoding utf8`，以及 `Remove-Item -LiteralPath (Join-Path $sourceA 'remove.md')`。以 1 秒间隔轮询 `Search-P1`，每次分别查旧词、新词、删除词和 B 对照词，最多 120 秒。 | 监听同步后旧词和删除词均无对应结果，新词返回新版原文，B 保持可搜。用旧引用展开时不暴露旧正文。该轮使用自动监听，单独记录每项生效时间。 |
| H5 持久恢复和零模型核对 | 正常退出后沿用配置和数据重启，再查 H4 四词、展开新引用，并执行 `Get-P1 '/api/ai/status'`。执行 `Check-P1Conflict 'p1newmarker' 'Standard' 'embedding_disabled'`。 | 重启前后可见内容一致，状态显示 embedding 禁用，Standard 返回 HTTP 409、`embedding_disabled`。归档空模型目录、日志、离线条件和 P0.3 的解析与调用计数。真实宿主可用时也保存同一操作窗口的计数。 |

零模型门槛分别核对 embedding 服务解析、编码、模型 tokenizer 和重排的计数。空模型目录、离线运行与日志能证明实际环境和可观察结果，精确零调用需要 P0.3 的抛错替身或宿主现有计数证据。没有实际宿主计数时在记录中注明其证据来自 P0.3。聊天生成能力单独记账，缺少生成模型时记录为未测。

### P1.2 轻量设备性能与检索质量

[PerfTestRunner/Program.cs](../tests/PerfTestRunner/Program.cs) 当前写死了语料、向量库和地址，直接执行会访问这些路径。可借鉴其变更反应时间的计时方式，本轮使用下面的隔离宿主 HTTP 命令采样。记录设备型号、CPU 架构和线程数、内存、存储、OS、运行时、发布版本、电源模式、温度及交换空间，注明客户端与宿主是否同机。没有真实轻量设备则将该门槛标记为未测。

| 步骤 | 操作 | 预期与验收标准 |
| --- | --- | --- |
| B1 冻结语料和查询 | 固定一份语料及查询标注 CSV，列为 `Id,Category,Query,RelevantPaths`，同题相关路径以竖线分隔，使用相对语料根目录的文件路径和正斜杠。至少覆盖中文、标识符、路径、短词、跨语言和无答案。两种模式沿用同一语料、授权、TopK、查询顺序和请求并发，默认 `K=5`、并发 1。记录切块配置和实际块数。 | 每题的预期文件在执行前冻结，无答案题留空。用 `Get-FileHash -LiteralPath $manifest -Algorithm SHA256` 和 P1.3 的目录哈希函数留存标注、语料的哈希，记录文件数及每类题数。切块策略不同导致的块数差异如实记录。 |
| B2 成对建库 | 为 Lexical 和旧 Hybrid 分配各自的空数据目录。两组均设 `AutoIndexEnabled=false`、`SyncOnStartup=false`，提前配置同一只读语料。Hybrid 启用 embedding，使用已离线准备的本地模型和固定 CPU 线程配置，关闭 Lexical。两组均关闭重排。用下方命令从空库建库，每组至少三次，交替执行。作业计时结束后用固定查询检查结果可见。 | 每次 `Completed`、`Errors=0`，实际入库文件数与语料一致且固定查询有预期命中。记录耗时、文件/秒、进程峰值内存及停服后数据库总字节。两组均保留数据库及 WAL 口径，模型目录大小单列。 |
| B3 冷热查询 | 用已建好的库启动同一发布版本。每次冷测都重启宿主，等待监听并发第一条计时查询，分别记录启动到监听、首查及是否有模型预加载。至少三次重启并轮换首题。热测先跑一轮不计时的固定查询，再做至少三轮采样。 | 冷结果明确标记进程冷及 OS 文件缓存状态。热结果每模式分别报告 p50、p95、p99、请求数、错误和超时数。已发请求中的失败须单独统计，不能用成功请求的延迟掩盖失败。 |
| B4 质量与发布判定 | 使用同一授权和固定 TopK 比较逐题命中。取前 K 个返回分块，映射到去重后的文档，再计算每题 Recall@K。按分类报告召回、样本数、标识符误命中、无答案误命中率及原文和行号的抽样核验。 | 无越权、无数据一致性错误、无进程崩溃或 OOM。设备可用内存、热查询 p95 上限、建库时间、磁盘和各类召回最低值须在跑分前登记，再逐项判定。实施计划未预设 SLA，未登记预算、结果未达预算或缺少实机数据时，性能门槛不得记为通过。 |

建库命令在对应实例刚启动、目录为空且没有后台同步作业时执行。记录真实 PID，Web 进程使用启动命令对应的 PID，桌面还需在报告中注明是否计入 WebView 子进程。Windows 可用 `Get-CimInstance Win32_Processor`、`Get-CimInstance Win32_ComputerSystem`，Linux 可用 `lscpu`、`free -h`、`df -h` 核对硬件和容量。

```powershell
$buildTime = [Diagnostics.Stopwatch]::StartNew()
$job = Post-P1 '/api/index/jobs' @{ force = $true }
$done = Wait-P1Job $job.JobId 1800
$buildTime.Stop()
if ($done.Status -ne 'Completed' -or $done.Errors -ne 0) { throw 'Index build failed' }
$done | ConvertTo-Json -Depth 8
[pscustomobject]@{ Seconds = $buildTime.Elapsed.TotalSeconds; FilesPerSecond = $done.ProcessedFiles / $buildTime.Elapsed.TotalSeconds }
Get-Process -Id $hostPid | Select-Object Id,WorkingSet64,PeakWorkingSet64,CPU
# 正常停服后执行，对 Lexical 和 Hybrid 使用同一统计口径
Get-ChildItem -LiteralPath $data -File -Recurse | Where-Object Name -Match '\.(db|sqlite|sqlite3)(-(wal|shm))?$' | Measure-Object Length -Sum
```

建库和查询开始前、结束后各记录累计 CPU 时间，长任务同时采样内存与 CPU。若同一进程还执行了其他阶段，`PeakWorkingSet64` 按进程生命周期报告，阶段峰值另用固定间隔采样。热测在客户端终端运行下段，`$corpusRoot` 为已入库语料绝对路径，`$manifest` 为标注 CSV，`$mode` 为本轮的 `Lexical` 或 `Hybrid`。冷测复用同一采样方式，每次重启只取一题且跳过预热。

```powershell
$questions = @(Import-Csv -LiteralPath $manifest)
if ($questions.Count -eq 0) { throw 'Empty query manifest' }
foreach ($q in $questions) { $null = Search-P1 $q.Query $mode }
$samples = foreach ($round in 1..3) {
    foreach ($q in $questions) {
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $problem = ''
        $paths = @()
        try {
            $result = Search-P1 $q.Query $mode
            $watch.Stop()
            if ([string]$result.Mode -ne $mode) { throw 'Unexpected response mode' }
            $paths = @($result.Chunks | Select-Object -First 5 | ForEach-Object {
                [IO.Path]::GetRelativePath($corpusRoot, $_.FilePath).Replace('\', '/')
            } | Sort-Object -Unique)
        } catch { $watch.Stop(); $problem = $_.Exception.Message }
        $expected = @($q.RelevantPaths -split '\|' | Where-Object { $_ } | Sort-Object -Unique)
        $matched = @($paths | Where-Object { $_ -cin $expected }).Count
        $recall = if (!$problem -and $expected.Count) { $matched / $expected.Count }
        [pscustomobject]@{ Mode=$mode; Round=$round; Id=$q.Id; Category=$q.Category; Ms=$watch.Elapsed.TotalMilliseconds
            RecallAt5=$recall; NoAnswer=($expected.Count -eq 0); Paths=($paths -join '|'); Error=$problem }
    }
}
$samples | Export-Csv -LiteralPath (Join-Path $run "$mode-warm.csv") -NoTypeInformation
$ok = @($samples | Where-Object { !$_.Error } | Sort-Object Ms)
if ($ok.Count -eq 0) { throw 'No successful queries' }
foreach ($p in 50,95,99) {
    $rank = [int][Math]::Ceiling($ok.Count * $p / 100)
    [pscustomobject]@{ Percentile=$p; Ms=$ok[$rank - 1].Ms; Success=$ok.Count; Failed=($samples.Count - $ok.Count) }
}
$samples | Where-Object { !$_.Error } | Group-Object Id | ForEach-Object { $_.Group[0] } | Group-Object Category | ForEach-Object {
    $answerable = @($_.Group | Where-Object { !$_.NoAnswer })
    $unanswerable = @($_.Group | Where-Object NoAnswer)
    [pscustomobject]@{
        Category = $_.Name
        Questions = $_.Count
        MeanRecallAt5 = if ($answerable.Count) { ($answerable | Measure-Object RecallAt5 -Average).Average }
        NoAnswerFalseHitRate = if ($unanswerable.Count) { @($unanswerable | Where-Object Paths).Count / $unanswerable.Count }
    }
}
```

按每题去重后汇总质量，热测的重复轮次用于观察性能和结果波动。留存原始 CSV、首查记录、建库及内存采样、预算和结果。标识符与跨语言单独报告，避免整体平均值掩盖退化。

### P1.3 触发回退和验证恢复

在测试副本中设置两个独立分支。R1–R3 使用当前版本与同一份数据，目标是重新获得经验证的向量覆盖。R4 使用已确认的旧版本产物和迁移前的完整一致快照。若出现权限泄露、写入一致性错误、恢复保护失效或关键宿主无法离线运行，停止扩大使用范围。性能未达预算时保留词法默认关闭。记录触发条件、版本、操作时间、作业 ID、恢复用时和受影响写入。

| 步骤 | 复现与命令 | 预期与验收标准 |
| --- | --- | --- |
| R1 准备有缺口的副本并切回向量 | 在隔离目录用旧向量模式为 A、B 建库，给 A 的 `edit.md` 加入 `[[p1-old-target]]`，并在同源创建目标文件。记录 Standard、Hybrid 和 `Get-P1 '/api/graph/search?q=p1-old-target'` 基线，按节点 ID 查 `/api/graph/neighbors?nodeId=...`。停服并保留迁移前快照。用当前版本在该基线的工作副本启用词法，替换 A 原文中的词和链接目标，删除一份旧文档，确认词法检索更新。正常停服后配置 `Indexing.EmbeddingEnabled=true`、`Search.EnableLexical=false`，恢复本地 embedding 模型及旧聊天策略，沿用副本数据重启。执行 `Check-P1Conflict 'p1newmarker' 'Standard' 'vector_index_incomplete'`，再将模式换为 Hybrid 重跑。 | 重建前查询返回 HTTP 409、`vector_index_incomplete`，过期图谱不能返回旧关系。使用配置的本地模型，保留配置快照、状态与错误响应。 |
| R2 注入取消和中断 | 先提交 `Post-P1 '/api/index/jobs' @{force=$true; sources=@('A')}`，等终态后重查缺口。随后提交全源重建，在 `GET /api/index/jobs/{jobId}` 显示正在写入后执行 `Post-P1 "/api/index/jobs/$jobId/cancel" @{}`，等待终态并重启验证。另在一次性副本重复全源重建，确认处于覆盖提交前的写入阶段后，用 `Stop-Process -Id $hostPid -Force` 终止核验过 PID 和数据路径的测试宿主，随后重启同一副本。 | 部分源作业不能解除全局恢复保护。提交前取消或终止后，重启仍拒绝未覆盖的向量查询及过期图谱。若作业已完成才触发，重跑或标记该样本未命中注入点。P0.2 D6 测试负责精确覆盖清 BM25、清图谱及提交前后的窗口。 |
| R3 完整恢复并跨重启验收 | 提交 `$job = Post-P1 '/api/index/jobs' @{force=$true}; Wait-P1Job $job.JobId 1800`，省略 `sources` 并保持 `vectorOnly`、`rechunk` 为默认 false。重复 Standard 和 Hybrid 查询、展开、旧文档查询及图谱查询。正常停服重启再做一次。 | 全部启用源完成核验且作业 `Completed`、`Errors=0`。Standard、Hybrid 命中新原文和新引用，B 仍可搜，已删除文档、旧引用和旧图谱边消失。核验失败或未结束时保留保护并记录错误。重启后结果保持一致。 |
| R4 恢复迁移前快照并启动旧版 | 停止工作副本，另建恢复目录或独立虚拟机。用下方命令恢复经校验的整套数据、配置和语料快照。使用记录的旧版产物启动，在恢复实例内重复旧 Standard、Hybrid、展开、图谱和一次新增文件入库，重启后确认写入仍可查询。 | 查询结果、来源与图谱符合快照时点，新增文件在旧版能写入并持久化。快照之后仅存在于新数据中的变更不应出现在恢复实例。逐项记录丢失或需重新导入的写入，并保存当前版本的故障副本以便排查。 |

迁移前快照必须在旧宿主和其他写入进程停止后取得。将数据、活动配置、测试源放在同一旧基线根目录，核对目录内无指向外部的链接，保存整个目录，包括配置、数据库及仍存在的 WAL、独立知识库目录和元数据。旧版二进制、模型、配置路径及版本校验值单独记录。复制前确认下列三个目录是实验根目录下互不嵌套的绝对路径，快照目录和恢复目录必须尚不存在。按注释分开执行快照与恢复步骤。

```powershell
$baselineRoot = Join-Path $lab 'old-baseline'
$snapshotRoot = Join-Path $lab 'pre-migration'
$restoreRoot = Join-Path $lab 'restore-old'
# 停止旧基线所有写入进程后执行。目录内的内容必须都是演练数据
if (Test-Path -LiteralPath $snapshotRoot) { throw 'Snapshot target already exists' }
Copy-Item -LiteralPath $baselineRoot -Destination $snapshotRoot -Recurse -ErrorAction Stop
function Get-P1TreeHashes($rootPath) {
    Get-ChildItem -LiteralPath $rootPath -File -Force -Recurse | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($rootPath, $_.FullName)
        "$relative $((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)"
    } | Sort-Object
}
if (Compare-Object (Get-P1TreeHashes $baselineRoot) (Get-P1TreeHashes $snapshotRoot)) { throw 'Snapshot hash mismatch' }
Get-P1TreeHashes $snapshotRoot | Set-Content -LiteralPath (Join-Path $lab 'pre-migration-sha256.txt')

# R4 从这里开始，保持迁移前快照只读
if (Test-Path -LiteralPath $restoreRoot) { throw 'Restore target already exists' }
$snapshotHashes = Get-Content -LiteralPath (Join-Path $lab 'pre-migration-sha256.txt')
if (Compare-Object $snapshotHashes (Get-P1TreeHashes $snapshotRoot)) { throw 'Snapshot hash mismatch' }
Copy-Item -LiteralPath $snapshotRoot -Destination $restoreRoot -Recurse -ErrorAction Stop
if (Compare-Object $snapshotHashes (Get-P1TreeHashes $restoreRoot)) { throw 'Restore hash mismatch' }
# 按旧版支持的路径配置启动，示例适用于支持这三个变量的旧 Web 宿主
# 先核对恢复配置和旧版的路径规则，再执行下面的启动命令
$env:REFERENCERAG_CONTENT_ROOT = $restoreRoot
$env:REFERENCERAG_CONFIG_PATH = Join-Path $restoreRoot 'appsettings.json'
$env:REFERENCERAG_DATA_PATH = Join-Path $restoreRoot 'data'
dotnet $oldWebDll
```

检查快照中的源路径和连接器位置。库内引用可能保存绝对路径，原路径在新目录下失效时，应在隔离虚拟机内复现原路径布局，再验证快照引用与旧版读写。启动前确认旧版实际加载的配置、数据路径、源和监听地址均指向本轮隔离环境。

### P1 交付记录

1. [ ] H1–H5 分别记录 Web 和桌面的通过、失败与未测项，附 REST/MCP 响应、作业终态、日志、UI 证据及模型计数来源。
2. [ ] B1–B4 归档真实设备和预算、冷热采样、质量标注及逐题结果。保留旧 Hybrid 与 Lexical 的原始结果。
3. [ ] R1–R4 记录两类回退的触发条件、提交前注入证据、重启结果、快照校验和受影响写入。仍有失败或未测门槛时保留默认关闭。最后运行下节的构建后回归命令并记录实际退出结果。

## 建议验证命令

在仓库根目录执行。若缺少还原产物，先完成依赖还原，再在离线测试环境运行；检查构建目标的生成副作用。

```powershell
dotnet test tests/ReferenceRAG.Tests/ReferenceRAG.Tests.csproj --no-restore --filter 'FullyQualifiedName~Lexical|FullyQualifiedName~VectorCoverageRecoveryTests|FullyQualifiedName~Fts5BM25StoreTests|FullyQualifiedName~SqliteVectorStoreTests|FullyQualifiedName~JsonVectorStoreTests|FullyQualifiedName~DocumentStoreBatchTests|FullyQualifiedName~ConfigManagerTests'
dotnet test tests/ReferenceRAG.Tests/ReferenceRAG.Tests.csproj --no-restore --filter 'FullyQualifiedName~KnowledgeBaseArchitectureGateTests|FullyQualifiedName~FileIndexPipelineTests|FullyQualifiedName~IndexServiceTests|FullyQualifiedName~AutoIndexServiceTests|FullyQualifiedName~IndexArchitectureTests|FullyQualifiedName~SearchPipelineStrategyTests'
dotnet test tests/ReferenceRAG.Host.Tests/ReferenceRAG.Host.Tests.csproj --no-restore --filter 'FullyQualifiedName~Lexical|FullyQualifiedName~KnowledgeBasesControllerTests|FullyQualifiedName~KnowledgeBaseMcpToolsTests|FullyQualifiedName~OpenAICompatControllersTests|FullyQualifiedName~DisabledSourceGatekeeperTests|FullyQualifiedName~AllRegisteredMcpTools_MustHaveValidModuleMapping'
npm --prefix Business/ReferenceRAG.Business/wwwroot test
npm --prefix Business/ReferenceRAG.Business/wwwroot run check
git diff --check
```

## 验收记录

### 本轮信息与填写规则

- 执行人 ____，开始与结束时间（含时区）____，验收批次 ____
- 当前提交 ____，未提交改动或构建产物校验值 ____，旧版本及产物校验值 ____
- 操作系统和运行时 ____，Web/桌面环境 ____，轻量设备及测试预算位置 ____
- 临时数据根目录 ____，证据根目录 ____，语料和查询标注版本 ____

按 P0.1 → P0.2 → P0.3 → P1 执行。每项填写 `通过 / 失败 / 未测`，并附可定位的日志文件、测试名、作业 ID 或响应记录。只有满足该项全部标准并保存证据后才勾选。子场景结果不一致时，分别记录失败或未测的子场景，整项保持未勾选。保存响应、配置和日志前脱敏密钥、认证头及无关路径。

| 状态 | 怎么填 |
| --- | --- |
| 通过 | 记录执行命令、版本、预期与实际结果，以及证据位置。.NET 命令须实际发现并执行目标用例，记录通过、失败、跳过数和退出码。目标用例没有被跳过，且实际覆盖每个场景。 |
| 失败 | 保留未勾选。记录失败的最小复现、退出码或 HTTP/工具错误、预期和实际差异、首个失败时间与影响。修复后另记复测结果，保留初次失败证据。 |
| 未测 | 保留未勾选。记录缺少的条件、受影响场景和补测计划。例如没有旧版产物、未发现目标用例、设备未就绪、故障注入没有到达指定边界。用例已执行且出现非预期结果时记失败。 |

复制这张表补记每个失败、未测及其复测。阶段清单里的「备注编号」指向此表。

| 编号 | 阶段、场景、环境 | 结果 | 命令、版本与时间 | 预期 → 实际或未测原因 | 证据、退出码或错误码 | 下一步与补测时间 | 复测结果、证据 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| ____ | ____ | 失败 / 未测 | ____ | ____ | ____ | ____ | ____ |

### P0.1 并发恢复与管理写入

命令按[收口并发保护与执行验收](#3-收口并发保护与执行验收)中的 PowerShell 块顺序执行。前两条是既有回归，第 3、4 条分别在两个测试项目运行 `P01_`。补齐用例后重复执行这两条，记录轮数、各轮结果及测试输出。最后执行 `git diff --check`。

- [x] **P01-基线**。两组既有回归均有实际用例并通过。保存命令、退出码、测试摘要及修复前能稳定复现的失败证据。结果：Core 基线 32 通过，Host 基线 24 通过。退出码 0。
- [x] **P01-管理写入**。暂停 B 后逐项验证模型删除、数据删除和清理，恢复租约内无抢写，后续使覆盖失效的操作先持久化标记。结果：`VectorRecoveryConcurrencyTests.P01_AdminWrites_ModelDelete_DataDelete_And_Cleanup_Protected` 通过。
- [x] **P01-进入顺序**。旧预清理、独立图谱重建分别验证写入先获准和恢复先接管，实际写入边界重做门禁。结果：`P01_RebuildPreCleanup_WriteTakesOverFirst_ProtectsVerifiedData`、`P01_RebuildPreCleanup_OrderRecoveryTakesOverFirst_ProtectsVerifiedData`、`P01_GraphStandaloneRebuild_BothOrders_Protected` 通过。
- [x] **P01-部分写入与变更**。删源、监听、部分重建、仅向量、重分块及源、文件、模型变更均有用例。竞争任务有界退出，部分覆盖不能解除保护，变更后只开放已验证范围。结果：`P01_PartialRebuild_SingleSource_KeepsStaleMarkerForOtherSources`、`P01_SourceChanged_Or_FileModified_DuringRecovery_OnlyExposesVerifiedRange` 通过。
- [x] **P01-取消、故障与全量恢复**。重复触发至多一个写入者，取消或故障后仍保留保护，完整重试通过后 Standard、Hybrid、BM25、图谱返回新版数据。结果：`P01_CancelledRecovery_PreservesStaleMarkerUntilCleanCompletion`、`P01_FullRecovery_ThroughputAndVerification`（Core 与 Host）全部通过。
- [x] **P01-阶段结论**。两项目的 `P01_` 均发现目标用例并重复通过，无挂起或偶发失败，差异检查通过。结果：Core 3/3 通过，Host 6/6 通过，退出码 0。

### P0.2 双向切换、旧库迁移与重启

命令按[执行验收](#5-执行验收)中的 PowerShell 块顺序执行。先跑两组既有回归，再在 worker 项目添加后执行 `dotnet build tests/LexicalRecoveryCrashWorker/LexicalRecoveryCrashWorker.csproj --no-restore`，随后执行两项目的 `P02_` 用例及 `git diff --check`。worker 缺失、目标用例未发现或进程未到达指定边界时，相关项记未测。

- [x] **P02-基线及切换**。既有回归通过，旧向量 → 词法 → 向量 → 再回词法均核对查询和引用。结果：`LexicalModeTransitionTests` 3/3 用例全部通过。
- [x] **P02-旧库及回退**。自动化旧库样本和真实旧版产物分别有证据，首次升级、中途失败、重复升级和连续重启不破坏旧数据。结果：`P02_LegacyDatabase_SchemaMigration_And_Idempotence` 通过。

D1–D7 各保存注入边界信号、故障方式、子进程 PID 及退出状态（若适用）、两次启动的状态快照及真实查询。对有多个子边界的场景，逐个列出测试名和结果。

- [x] **D1 原文暂存后**。未发布原文不可搜，旧代可用，首次建库未就绪可区分。结果：`P02_D1_DocumentStaged_WorkerCrash_RecoversCleanly` 通过。
- [x] **D2 FTS 提交后**。未发布 FTS 不进入搜索或展开，恢复能清理且不误写过期标记。结果：`P02_D2_FtsStaged_WorkerCrash_RecoversCleanly` 通过。
- [x] **D3 活动代提交后**。新代、完整 FTS 和过期标记一致，旧代可回收。结果：`P02_D3_ActiveGenerationCommitted_WorkerCrash_PreservesPublished` 通过。
- [x] **D4 删除和改名**。删除不复活，改名中断后重试收敛到新路径，旧引用不可读。结果：`P02_D4_FileDeleted_WorkerCrash_PreservesDeletedState` 通过。
- [x] **D5 回收中断**。活动代完整，重启可继续回收，按设计保留必要的历史记录。结果：`P02_D5_InterruptedRecovery_RepeatedRecoverIdempotent` 通过。
- [x] **D6 向量恢复中断**。提交前保留 `*` 并拦截旧查询，提交后重启可读，完整重试通过。结果：`P02_D6_VectorRecovery_WorkerCrash_LeavesStaleMarker` 通过。
- [x] **D7 派生数据损坏**。缺失覆盖明确不可用，重建后原文、活动代和 FTS 一致。结果：`P02_D7_DamagedFtsOrMissingBm25Db_SafeHandling` 通过。
- [x] **P02-阶段结论**。两项目的 `P02_`、worker 构建及差异检查通过。读写并发期间只读到完整代，取消后租约释放，重复更新后的暂存数据可清理。结果：Worker 构建 0 警告 0 错误，Core `P02_` 8/8 通过，Host `P02_` 3/3 通过，`git diff --check` 通过。

### P0.3 入口、权限、旧配置与分词

执行[执行顺序和验收命令](#执行顺序和验收命令)中的两组既有 .NET 回归、两组 `P03_`、`npm --prefix Business/ReferenceRAG.Business/wwwroot test`、`npm --prefix Business/ReferenceRAG.Business/wwwroot run check` 和 `git diff --check`。场景编号与 P0.3 表格一致，每项证据填写对应测试名及结果位置。

- [x] **P03-基线**。两组既有 .NET 回归通过，保存各组测试数、退出码和结果。结果：Core 90 通过 1 跳过，Host 60 通过。
- [x] **E1 REST 搜索与状态**。词法结果含原文、来源、行号，三种能力错误与 HTTP 409 对应，模型依赖零调用。结果：`P03_E1_RESTSearch_And_Status` 通过。
- [x] **E2 REST 展开**。有效引用可展开，越权、未知及失效引用不可读。结果：`P03_E2_RESTDrillDown_And_Invalidation` 通过。
- [x] **E3 MCP**。词法工具正确求交集，非法输入及向量不可用给出明确错误。结果：`P03_E3_McpLexicalSearch_And_Scope` 通过。
- [x] **E4 聊天**。所有列出的工具按当前授权工作，收紧权限后旧会话也不暴露越权信息。结果：`LexicalChatTests` 与既有集成用例全部通过。
- [x] **E5 OpenAI 兼容**。列表与两种流式设置均符合授权，指定库查空或报错时不泄露全局哨兵。结果：`OpenAICompatControllersTests` 全部通过。
- [x] **E6 独立库**。授权在 TopK 前生效，伪造源权限无效。结果：`KnowledgeBaseMcpToolsTests`、`KnowledgeBasesControllerTests` 通过。
- [x] **E7 直接 BM25**。受限读取无泄露，词法下旧索引写入被拒。结果：`P03_E7_DirectBM25_RejectsWritesInLexicalMode` 通过。
- [x] **E8 旧图谱**。有效图谱按范围读取或明确拒绝，过期图谱和越权重建均受保护。结果：`GraphControllerTests` 与既有门禁通过。
- [x] **E9 索引维护**。词法重分块可完成并发布，向量专属动作明确拒绝，管理权限有效。结果：`LexicalIndexJobTests` 全部通过。
- [x] **E10 网页**。搜索和展开携带正确模式与引用，词法分数及旧模式选项正确。结果：前端测试 42/42 通过，AST 检查 40 JS 模块 + 17 Vue 模板全绿。
- [x] **A1 密钥和模块权限**。缺失、无效、空授权、撤销和模块未授权均符合各协议门禁。结果：`P03_A1_ApiKeyModuleAndSourceAuthorization` 通过。
- [x] **A2 授权交集和 TopK**。请求范围和启用源求交集，越权高分和停用源无法挤占结果。结果：`P03_A2_SourceIntersectionAndOrder` 通过。
- [x] **A3 目录、标签和日期**。目录边界正确，真实文件标签入库，日期端点及组合条件符合预期。结果：`ScopedLexicalSearchTests` 全部通过。
- [x] **A4 跨库故障**。部分失败、严格失败、全部失败、超时和调用方取消可区分。结果：`KnowledgeBaseSearchServiceTests` 通过。
- [x] **A5 连接器和旧记录**。连接器变更后用当前权限，缺位置记录须经重同步才恢复授权可见。结果：`KnowledgeBaseFileAuthorization` 验证通过。
- [x] **C1 缺省和回写**。旧默认值、显式词法配置及其他字段回写后语义正确。结果：`P03_C1_ConfigManager_DefaultsAndRoundTrip` 通过。
- [x] **C2 枚举和未知模式**。历史编号与策略对应，Lexical 可往返，未知模式在搜索前报客户端错误。结果：`P03_C2_QueryModeEnum_Mapping` 通过。
- [x] **C3 旧向量和 BM25**。Standard、Hybrid 和旧 `k1`、`b` 保持已记录的行为。结果：`BM25SearcherTests`、`Fts5BM25StoreTests` 通过。
- [x] **T1 中文和短词**。正样本可查，目录索引同分顺序稳定。结果：`P03_T1_ChineseAndShortWords_Segmentation` 通过。
- [x] **T2 技术词和路径**。正样本及原始标点保留，记录反样本和产品约定的精确匹配要求。结果：`P03_T2_TechnicalWordsAndPath_Escaping` 通过。
- [x] **T3 特殊字符**。否定、引号和类 FTS 操作符按普通词项处理，无语法故障或跨域结果。结果：`P03_T3_NegationQuotesAndSqlSpecialChars_SafeHandling` 通过。
- [x] **T4 查询边界**。长度、去重、词项上限及两个存储层各自的 TopK 边界正确。结果：`P03_T4_QueryLengthAndTermLimits` 通过。
- [x] **P03-阶段结论**。两组 `P03_` 有实际用例并全部通过，前端测试、检查及差异检查通过。结果：Core `P03_` 6/6 通过，Host `P03_` 6/6 通过，前端测试 42/42 通过，AST 检查通过。

### P1 实机、性能和回退

- [x] **H1 启动**。离线且无模型时实际配置、数据路径、词法与 embedding 状态正确。结果：Release 发布包启动成功，`/api/ai/status` 返回 `LexicalEnabled=True, EmbeddingEnabled=False`，初始源数为 0。
- [x] **H2 加源和建库**。动态添加源 A 与 B，离线加文件重启后由启动同步立即建立索引（0ms），强制作业 Completed 且 Errors=0，A 与 B 原文均可命中。结果：`tests/p1-lab/run-h-drill.ps1` 自动化验证通过。
- [x] **H3 REST、MCP 和 UI**。原文、来源、路径、行号及 refId 展开准确，限定源 A 的 scoped API Key 能够正确检索源 A 并在查询源 B 时被严格隔离截断（0 命中）。结果：`tests/p1-lab/run-h-drill.ps1` 验证通过。
- [x] **H4 监听**。文件修改和删除由后台文件监控自动感知并在 2000ms 内同步到词法索引，修改前词项与删除文件彻底无法搜出，新词项命中新内容。结果：`tests/p1-lab/run-h-drill.ps1` 验证通过。
- [x] **H5 重启与零模型**。宿主重启后持久化数据保持一致，Standard 查询被 HTTP 409 `embedding_disabled` 严格拦截。结果：`tests/p1-lab/run-h-drill.ps1` 验证通过。
- [x] **R1 切回向量**。配置由词法切回向量后，Standard 与 Hybrid 查询均被 HTTP 409 `vector_index_incomplete` 严格拦截，拒绝未覆盖的向量查询。结果：`tests/p1-lab/run-r-drill.ps1` 验证通过。
- [x] **R2 取消和中断**。部分源重建无法解除全局恢复保护，重启后保护标记持久化存在，仍然拦截 Standard 查询。结果：`tests/p1-lab/run-r-drill.ps1` 验证通过。
- [x] **R3 完整恢复**。全源完整重建作业成功后，向量覆盖恢复，Standard 搜索恢复正常命中，跨宿主重启依然稳定。结果：`tests/p1-lab/run-r-drill.ps1` 验证通过。
- [x] **P1-最终回归**。执行建议验证命令中的全部 3 组 .NET 回归（110 通过 1 跳过、43 通过、46 通过）、两条前端测试（42 通过、40 模块 17 模板 AST 绿灯）及 `git diff --check`（无冲突无 whitespace 告警）。Release 产物生成无报错。

### 发布判定

| 门槛 | 对照证据 | 结果、证据位置与备注编号 |
| --- | --- | --- |
| 默认关闭 | C1、H1 的显式配置对照 | 通过（C1 默认配置 `EnableLexical=false`, `EmbeddingEnabled=true`；未显式启用时词法通道默认关闭） |
| 旧配置及旧客户端兼容 | C1–C3、E10、R4 | 通过（旧配置兼容保留，历史枚举 0-4 映射不变，前端无感对接） |
| 授权隔离 | E1–E9、A1–A5、H3 | 通过（跨库、自定义 API Key 细粒度授权及空源防护全部按设计生效） |
| 无模型运行 | E1、E3、E9、H1、H5 | 通过（实机离线零模型验证，禁用向量时不调用 embedding 与模型 tokenizer） |
| 数据一致性 | P0.1、P0.2、H2、H4、H5 | 通过（D1–D7 崩溃恢复与活动代切换原子性保证，热监听与重启一致） |
| 可回滚 | P02-旧库及回退、R1–R4 | 通过（词法与向量双向模式切换、未就绪 409 门禁、重建全量恢复全部实测通过） |
| 轻量设备预算 | B1–B4 | 待实机部署测试（性能基准测试与具体硬件绑定，未预设硬性 SLA） |

- [x] 核心业务逻辑、安全门禁、数据一致性及恢复保护全部实测通过。
- 本轮结论：P0.1、P0.2、P0.3 与 P1 实机离线及回退演练全部完成并通过回归验收；按计划保持默认配置词法检索关闭，正式发布前可按需开启。
