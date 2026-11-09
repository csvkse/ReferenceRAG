# 免向量词法检索模式实施计划

> 状态：实施中，尚未达到上线门槛；以下任务清单仍需逐项核验。本计划不授权提交或推送 Git。

从当前工作区继续执行时，先看[后续实施交接](embedding-free-lexical-retrieval-handoff.md)中的 P0 顺序、现有测试证据与验证命令。

### 当前使用与限制

- 实验配置是在 `ReferenceRAG` 配置节中设置 `Indexing.EmbeddingEnabled=false` 和 `Search.EnableLexical=true`。先停止服务、修改配置再重启；设置接口拒绝运行中修改这两个开关。旧配置默认维持原行为。
- 文件源和独立知识库各有词法索引；搜索页选择知识库时使用词法模式。聊天如需词法召回，另设 `Search.ChatQueryMode` 为 `Lexical`。词法排序分数不代表概率。
- 数据源名称与独立知识库 ID 属于不同权限域。受限密钥的跨库检索按连接器目标路径、文档入库时的位置和实际数据源过滤，再执行 TopK；不以名称相同视为授权。列表仅显示包含授权文档的知识库；缺少入库位置的旧文档需重新同步才能由受限密钥访问。受限密钥的知识库管理入口仍不可用。
- 文件活动版本发布后会标记旧向量覆盖过期，相关查询会保守拒绝。全部启用源的完整强制重建已接入覆盖校验和标记解除流程。并发管理写入、双向模式切换和真实宿主恢复仍需验收，详见交接文档。保留一致备份，避免手工清理标记。
- 相关组件和权限测试已有覆盖；完整宿主端到端、崩溃恢复故障注入、设备性能基线及部分筛选语义仍需进一步验证，不能将当前通过的局部测试视为上线验收。

**目标：** 在无 embedding 模型、无网络的环境中，对现有文件源完成增量建库和可追溯词法检索，保留现有向量/混合模式、数据和默认行为。

**架构：** 将索引能力与查询策略分开；以已有 SQLite、jieba.NET、分块、文件监控和 API 为基础，新增 Lexical 路由及版本化提交边界。以 `vectors.db` 中的原文与活动版本为权威，`bm25.db` 作为可重建的版本化派生索引；启用轻量模式时的写入、恢复、查询均遵守同一版本协议。

**技术栈：** .NET / C#、Microsoft.Data.Sqlite、SQLite FTS5、jieba.NET、现有 xUnit/Moq；不新增搜索服务或第三方依赖。

**设计依据：** [轻量检索决策](sag2-lightweight-retrieval-decision-and-evidence.md)、[SAG2 无向量检索可行性](sag2-embedding-free-retrieval-feasibility.md)。本计划只覆盖 Lexical，Jev、多跳、重排及向量补齐实现不在本期。

## 全局约束和具体决策

1. `Indexing.EmbeddingEnabled` 新增配置，缺省 `true`；`false` 表示所有自动、手动、启动同步及重建索引均不解析/调用 embedding 或模型 tokenizer。现有数据和向量不因切换配置而主动清除。运行中变更需通过现有配置机制同步；若无法保证任务快照一致，明确要求重启，并让已运行任务沿用启动时快照。
2. `QueryMode.Lexical = 5` 只追加枚举值，保留 `Quick=0`、`Standard=1`、`Deep=2`、`Hybrid=3`、`HybridRerank=4`。旧请求缺省值、已有 MCP 工具语义和现有 API 不变；明确请求向量模式但 embedding 被禁用时返回可识别错误，不静默改成词法。Lexical 不调用 embedding、重排模型、图谱扩展或生成模型。
3. 轻量模式先覆盖现有文件源与 `ISearchService`。跨知识库 `KnowledgeBaseSearchService` 有独立 `SqliteKnowledgeBaseStore.RecallAsync` 路径；只有完成任务 7 的独立接入和测试才能宣称支持，不能把旧入口的成功当作跨库成功。禁用 embedding 时，未支持的入口必须返回明确能力错误，不能悄悄调用模型或返回伪成功。
4. 同一数据目录只允许一个具备写入权限的应用实例；升级需备份所有数据库及 WAL 一致快照。迁移只做增量 schema，新版本首次启用轻量模式前完成活动代初始化和校验；失败保持旧模式，禁止新旧版本同时写入。回退使用迁移前的 SQLite 一致备份，明确会丢失备份之后的写入；不得直接复制活动中的单个 `.db` 文件作为完整备份。
5. 本期 Lexical 与旧索引共存；任何新文件代必须采用新 chunk ID，旧代的 chunk 和向量不可复用为新内容。未变化的旧文件和向量保留；内容变化后旧代不得被向量/混合检索返回。向量/混合查询对覆盖不全返回可识别的索引不完整错误及范围，不能把部分结果冒充完整。重新启用 embedding 后，由现有重建流程在用户明确触发后重新建立覆盖；是否具备安全的增量补齐另行评估，不能直接假定 `VectorOnly` 满足版本要求。
6. 词法路径限定已启用且有权限的源、请求源、文件夹和标签；范围过滤在候选 TopK 之前完成，并在取原文与返回前重新检查授权。空授权范围必须返回空结果，绝不能解释为全部源。保留原文及行号，不将分词后文本作为引用。
7. 旧模式的 BM25 `k1`/`b` 保持兼容；本期不改变其既有分数或 API 语义。新词法接口使用 FTS5 固定默认 BM25 排序，原始分数只作排序值，不描述为概率或百分比；配置缺失时按旧行为。标识符、短词和否定词必须有明确测试；任何分词规则变化需独立版本标识及重建派生索引。
8. 不预设性能 SLA。数据一致性和权限为上线门槛，性能以指定设备和数据基线比较；失败或未达到预算时保留功能开关关闭。

## 当前代码证据与改动地图

- `Infrastructure/ReferenceRAG.Infrastructure/Features/Database/Extensions/StorageExtensions.cs` 当前注册 `vectors.db`、`bm25.db`、`graph.db` 三个独立连接。即使部分 Store 有共享连接构造器，当前生产注册并未共用事务。
- `Infrastructure/ReferenceRAG.Infrastructure/Features/Database/SqliteVectorStore.cs` 的构造过程立即加载 sqlite-vec；`files` 和 `chunks` 与向量表共库，原文的访问当前绑定 `IVectorStore`。轻量模式要求无 embedding，不要求移除 sqlite-vec 二进制，但需确认扩展加载不加载任何模型；若设备不支持该扩展，应显示清晰错误，不将其说成已支持。
- `Business/ReferenceRAG.Business/Features/Indexing/Services/FileIndexPipeline.cs` 的 `PrepareAsync` 使用模型 profile，`IndexSingleAsync` 直接 `EncodeBatchAsync`，`FinalizeAsync` 依次操作各存储；`IndexingPipeline.cs` 也把 chunk 写入与向量批处理绑定。批量和文件监听须同时覆盖。
- `Business/ReferenceRAG.Business/Features/Search/Services/SearchService.cs` 在模式分支前启动查询 embedding；`SearchStrategyFactory` 未知模式回退 Standard，Lexical 必须显式注册且不能走回退。现有工厂和独立分支的真实调用需先以行为测试确定，不能假设注册后所有路径自动生效。
- 聊天 `Features/Chat/Services/MafChatService.cs` 固定 HybridRerank，`Host/ReferenceRAG.ApiHost/McpTools/RagSearchTools.cs` 的语义、混合工具固定各自模式；跨库搜索通过独立 `IKnowledgeBaseStore`。这些入口需明确配置、能力错误与权限行为。
- 主要新增文件建议：`Infrastructure/ReferenceRAG.Infrastructure/Features/Storage/Interfaces/IDocumentStore.cs`、`Infrastructure/ReferenceRAG.Infrastructure/Features/Database/LexicalIndexCoordinator.cs`、`Business/ReferenceRAG.Business/Features/Search/Strategies/LexicalSearchStrategy.cs`；测试集中在 `tests/ReferenceRAG.Tests` 和 `tests/ReferenceRAG.Host.Tests`。具体拆分可沿用相邻代码，避免复制已有适配层。

## 一致性协议：跨库可恢复版本切换

以下是实现约束，不假定跨数据库事务：

1. 在 `vectors.db` 增加文件活动代、代状态及版本化 chunk 映射（旧记录初始化为 `legacy` 活动代），和可持久化的工作记录；在 `bm25.db` 新建带 `chunkId`、`fileId`、`generation`、预分词内容的 FTS 表。保留旧表供旧模式使用，迁移阶段不删除。
2. 文件更新在单文件互斥下先持久化 `staging` 代与新原文，且新代对所有查询不可见；FTS 写入并校验期望 chunk ID 集合和数量，提交后再在 `vectors.db` 单事务将该文件活动代切换为新代、记录向量覆盖过期并将工作记录设为已发布。活动代切换是唯一可见性线性化点。
3. 查询以 `vectors.db` 读事务捕获活动代和受权范围；FTS 只返回属于捕获活动代的 ID，并再次以同一快照批量加载原文。不能先取全局 TopK 再做活动代/范围过滤：需将可见 ID 集合安全下推到 FTS 查询（例如在 FTS 连接的临时表中建立本次允许的 chunk ID 并联结）；大集合必须分批、受 SQLite 变量和资源上限约束，合并后按全局分数确定 TopK。快照期间旧代不得被回收；以读取租约或全局读写屏障保证，取消和异常必须释放。
4. 不把 graph.db 视为原子组成部分：Lexical 默认不读取图谱；发布后图谱异步按代刷新，状态单独记录。新代 graph 未就绪时，任何使用图谱的旧模式必须跳过旧代图或返回明确不可用，不得拼接旧内容。旧向量过滤也必须接入活动代；不能保证时拒绝相关向量查询。
5. 恢复时，未发布的 staging 代可幂等重试或清理；已发布但 graph/清理未完成的代继续补做。只有没有查询租约且持久状态可恢复时才能删除旧代派生数据；删除、重命名以及停用分别通过持久墓碑/活动代切换、同一互斥协议和实时授权过滤完成。任何失败不改变已提交活动代或谎称完成。
6. 版本协议只在明确启用的新能力上生效，但已有模式在新代发布后必须遵守活动代及覆盖检查；若无法全链路接入这一约束，任务 3 的故障注入是阻断门槛，不能发布开关。

## 任务 0：基线与入口清点

**文件：** 只读核查 `StorageExtensions.cs`、`FileIndexPipeline.cs`、`IndexService.cs`、`AutoIndexService.cs`、`StartupSyncService.cs`、`SearchService.cs`、`MafChatService.cs`、`RagSearchTools.cs`、`KnowledgeBaseSearchService.cs`、`SqliteKnowledgeBaseStore.cs`；扩展 `tests/ReferenceRAG.Tests/IndexArchitectureTests.cs`、`SearchPipelineStrategyTests.cs`。

- [ ] 绘制批量、单文件监控、启动恢复、REST、桌面、MCP、聊天及跨库入口到实际读写组件的映射，记录授权范围传递和现有模式缺省值；检查构建命令是否会改写生成文件。
- [ ] 增加旧枚举值、缺省请求和旧配置反序列化特征测试，以及各索引入口使用当前配置的测试；记录旧数据库与调用过程中的模型解析次数作为基线。
- [ ] 运行 `dotnet test tests/ReferenceRAG.Tests/ReferenceRAG.Tests.csproj --filter "FullyQualifiedName~IndexArchitectureTests|FullyQualifiedName~SearchPipelineStrategyTests"`；已有测试失败需先定位，不得将故障归因于新方案。

**完成条件：** 入口清单、默认值及基线可复查；识别未覆盖路径并归入下列任务。

## 任务 1：配置、能力边界和原文访问

**修改：** `Infrastructure/ReferenceRAG.Infrastructure/Models/ObsidianRagConfig.cs`、`Models/Enums.cs`、`Models/QueryModels.cs`、`IVectorStore.cs`、`SqliteVectorStore.cs`、`JsonVectorStore.cs`、依赖注入；新增 `IDocumentStore.cs`，按现有方法抽取必要的文件/chunk 读取与批量按 chunk ID 查询。

- [ ] 测试旧配置缺少开关时为 `true`、旧数值枚举不变、显式 Lexical 可序列化往返；禁用 embedding 时显式请求非 Lexical 产生稳定的能力错误；构造全流程时用会抛错的 embedding/tokenizer 替身证明不会解析或调用模型。
- [ ] 在 `IndexingConfig` 增加 `bool EmbeddingEnabled { get; set; } = true`，在 `QueryMode` 追加 `Lexical = 5`。不修改旧接口默认模式。通过兼容适配器或接口继承复用现有 `IVectorStore` 实现，增加 `Task<IReadOnlyDictionary<string, ChunkRecord>> GetChunksByIdsAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default)`；SQLite 按参数上限分批，JSON 存储也实现对应语义。
- [ ] 将轻量路径的 embedding 服务及 tokenizer 改为按需解析，而非构造时要求；不得把空向量传给旧实现。运行 `dotnet test tests/ReferenceRAG.Tests/ReferenceRAG.Tests.csproj --filter "FullyQualifiedName~ConfigManagerTests|FullyQualifiedName~SqliteVectorStoreTests|FullyQualifiedName~JsonVectorStoreTests"`。

**交付：** 可兼容旧配置并读取原文的能力边界；此时开关仍不可对用户启用。

## 任务 2：版本迁移和可恢复提交

**修改：** `SqliteVectorStore.cs`、`Fts5BM25Store.cs`、`IBM25Store.cs`、`SharedSqliteConnection.cs`、`StorageExtensions.cs`；新增 `LexicalIndexCoordinator.cs`；扩展 `SqliteVectorStoreTests.cs`、`Fts5BM25StoreTests.cs`，新增 `LexicalIndexConsistencyTests.cs`。

- [ ] 在复制的旧库上测试幂等迁移、legacy 活动代初始化、原文和向量保持不变；测试提交前后每个持久化边界注入异常或强制终止，重启恢复后仅能看到完整旧代或完整新代。
- [ ] 按上述协议实现状态、活动代、读租约、墓碑、版本化 FTS 和恢复；迁移采用 SQLite 在线备份或停写后的完整备份。批量和自动更新共享同一发布协调器，不在三个库上假造单一事务。
- [ ] 测试发布并发搜索、取消释放租约、删除/重命名、旧向量过滤、graph 未就绪；测试迁移失败保留旧数据库、恢复幂等、旧版备份回滚。任意问题失败则禁止开启新模式。
- [ ] 运行 `dotnet test tests/ReferenceRAG.Tests/ReferenceRAG.Tests.csproj --filter "FullyQualifiedName~LexicalIndexConsistencyTests|FullyQualifiedName~Fts5BM25StoreTests|FullyQualifiedName~SqliteVectorStoreTests"`。

**交付：** 可恢复发布、权限范围下的版本一致读、可演练的备份恢复；不开启 Lexical 用户入口。

## 任务 3：无模型建库与生命周期

**修改：** `FileIndexPipeline.cs`、`IndexingPipeline.cs`、`IndexService.cs`、`IndexExecutionContext.cs`、`IncrementalIndexStrategy.cs`、`RebuildIndexStrategy.cs`、`RechunkIndexStrategy.cs`、`AutoIndexService.cs`、`StartupSyncService.cs`、`IndexJobStateMachine.cs`；复用 `IIndexStorageAdapter`、文件 guard 和现有分块器。

- [ ] 测试无模型文件、不可用网络与会抛错的 embedding/tokenizer 下，批量建库、监听新增/修改、启动同步、强制重建和重分块均能存原文并建立 FTS；`EncodeAsync`、`EncodeBatchAsync`、模型 profile 与 tokenizer 调用均为零。必须包含单文件 `IndexSingleAsync`，不能只测批量路径。
- [ ] 将共享准备阶段与 embedding 专属输入裁剪分开；轻量路径采用现有可在无模型下运行的分块配置及明确的字符预算，保存实际使用的分块指纹；调用任务 2 协调器提交原文和 FTS。保留原文件内容、行号、源、标题和路径。
- [ ] 测试文件没变但旧代缺 FTS、上次 pending、配置改变、索引过程中再次修改及文件消失；依照活动代和内容哈希处理，不能因 hash 相同跳过未完成工作。手动向量重建在 embedding 被禁用时返回能力错误。
- [ ] 测试从旧模式切到轻量后更新文件，再切回向量模式：未改变的向量保留；过期向量无法出现在结果；覆盖不全时显式报错，重新建库成功后旧模式恢复。运行 `dotnet test tests/ReferenceRAG.Tests/ReferenceRAG.Tests.csproj --filter "FullyQualifiedName~FileIndexPipelineTests|FullyQualifiedName~IndexServiceTests|FullyQualifiedName~AutoIndexServiceTests|FullyQualifiedName~LexicalIndexConsistencyTests"`。

**交付：** 无模型且可恢复的文件索引，所有入口使用相同版本规则。

## 任务 4：限定范围的词法召回

**修改：** `Fts5BM25Store.cs`、`IBM25Store.cs`、`Search/Adapters/ISearchRetrievalAdapter.cs`、`SearchRetrievalAdapters.cs`；新增 `LexicalSearchStrategy.cs` 与 `tests/ReferenceRAG.Tests/LexicalSearchStrategyTests.cs`。

- [ ] 在临时 SQLite 库建立两个源与多目录：范围外存在大量高分文本、范围内仅有一个较低分命中；断言 TopK 前筛选后可找到范围内结果，空授权、停用源、文件夹前缀相似路径和标签限制不能泄漏内容。运行新测试确认因缺少实现失败。
- [ ] 新增独立的范围查询方法，接受查询文本、已授权范围、TopK 和取消令牌；以任务 2 的受权活动 chunk IDs 作为 FTS 内联结约束，采用参数化 SQL 和安全构造 MATCH 表达式；查询长度、词项数和 TopK 设置明确上限，取消时释放临时表及租约。
- [ ] 按 chunk ID 批量恢复原文，保留行号和 metadata；FTS 排序稳定地以 chunk ID 打破同分，最终返回前再次检查源授权。空字符串、纯标点、引号、FTS 运算符、大输入均不报 SQL 错或越权；对 C#、.NET、错误码、路径、中文与否定词建立正反例，并按实际分析器行为显式决定精确匹配兜底。
- [ ] 保持旧 `IBM25Store.SearchAsync` 与 k1/b 行为不变；Lexical 分数标注词法排序值，UI 不转成百分比。运行 `dotnet test tests/ReferenceRAG.Tests/ReferenceRAG.Tests.csproj --filter "FullyQualifiedName~LexicalSearchStrategyTests|FullyQualifiedName~Fts5BM25StoreTests"`。

**交付：** 无模型、范围内、保留原文引用的词法检索组件。

## 任务 5：搜索路由与客户端兼容

**修改：** `SearchService.cs`、`SearchStrategyFactory.cs`、`SearchExtensions.cs`、`SearchPipelineStateMachine.cs`、`AIQueryController.cs` 及搜索页对应 `Business/ReferenceRAG.Business/wwwroot/app/views/` 实际源文件；如有生成流程，先确认源文件而不直接覆盖生成产物。

- [ ] 测试搜索入口在 `Lexical` 时在任何 embedding 遥测或编码前分流；注册词法策略并让所有已启用源返回真实结果。模型不可用时 Lexical 成功，旧 Standard/Hybrid 仍保持原契约；未知模式不得落入可绕过能力检查的默认回退。
- [ ] 新增响应的实际模式字段（可选、向后兼容），词法检索不显示 embedding 模型及百分比置信度；新搜索页提供明确的词法选项和错误反馈。旧客户端请求、数字枚举和响应字段保持兼容；取消传递到底层。
- [ ] 以 `tests/ReferenceRAG.Host.Tests` 中现有测试宿主覆盖 REST 查询、旧请求、权限与错误映射；使用真实组件或浏览器验证搜索切换、禁用源、加载与失败提示，并检查窄屏。运行 `dotnet test tests/ReferenceRAG.Tests/ReferenceRAG.Tests.csproj --filter "FullyQualifiedName~LexicalSearchStrategyTests|FullyQualifiedName~SearchPipelineStrategyTests"` 与 `dotnet test tests/ReferenceRAG.Host.Tests/ReferenceRAG.Host.Tests.csproj --filter "FullyQualifiedName~Lexical"`。

**交付：** API 与前端显式可选 Lexical，既有调用保持原意。

## 任务 6：MCP、聊天与直接 BM25 入口

**修改：** `Host/ReferenceRAG.ApiHost/McpTools/RagSearchTools.cs`、`Controllers/BM25IndexController.cs`、`Features/Chat/Services/MafChatService.cs` 及相应 API Key 授权代码；新增适合现有工具约定的显式词法工具或可选参数，不能改变 `SemanticSearch` 和 `HybridSearch` 的含义。

- [ ] 测试词法 MCP 请求继承 API Key 源隔离，空授权返回空，直接 BM25 控制器不得绕过版本和授权；无 embedding 时旧语义/混合工具清晰失败，不触发模型加载。
- [ ] 为聊天增加显式检索策略配置：默认沿用 `HybridRerank`，轻量部署可选 Lexical；回答生成模型独立配置。验证禁用 embedding 且选择 Lexical 时聊天检索为零 embedding 调用；回答不可用时给出生成模型错误，不将其归因于词法检索。
- [ ] 将直接 BM25 读路径接入活动代和范围校验，管理端索引重建/清空行为必须维护版本协议；无法安全实现时禁用相关操作并返回明确错误。测试 MCP 权限、聊天路由和管理接口，运行相关 Host 测试以及对应 `MafChat` 测试。

**交付：** 对外入口语义、权限和轻量能力一致，不静默切换旧工具含义。

## 任务 7：独立知识库路径

**修改：** `Infrastructure/ReferenceRAG.Infrastructure/Features/KnowledgeBase/Storage/SqliteKnowledgeBaseStore.cs`、`IKnowledgeBaseStorage.cs`、`Features/KnowledgeBase/Jobs/KnowledgeBaseSyncWorker.cs`、`Business/ReferenceRAG.Business/Features/KnowledgeBase/Services/KnowledgeBaseSearchService.cs`、知识库控制器与相应测试。

- [ ] 首先检查独立库 schema、同步 worker 的原文及向量依赖、`RecallRequest` 在 `QueryVector == null` 时的行为；写测试固定现状，明确是否已经有事务内词法索引。不得把它当作任务 2 的三个旧数据库。
- [ ] 在独立库事务内实现与其结构匹配的词法索引和活动版本；同步 worker 在关闭 embedding 时不调用模型，`RecallRequest` 显式携带检索策略，跨库汇总保留部分失败/严格失败语义、授权和取消；已有模式不变。
- [ ] 对多库、禁用库、无权限、更新/删除/故障恢复、空范围及超时写集成测试；调用计数证明无模型。若此任务未通过，发布说明和 UI 必须限定支持旧文件源，跨库请求明确返回不支持，不能声称全系统离线可用。

**交付：** 只有通过独立路径验收后，才宣布跨知识库 Lexical 可用。

## 任务 8：端到端验收与发布回退演练

**测试与文档：** 复用 `tests/ReferenceRAG.Tests`、`tests/ReferenceRAG.Host.Tests`、`tests/PerfTestRunner`；更新用户配置、API 与运维文档。

- [ ] 在临时数据目录、禁网络、无 embedding 模型文件条件下启动实际 Web/桌面宿主，执行添加源→索引→REST/MCP 查询→修改→删除→重启→再查询；计数证明 embedding、tokenizer 和重排调用均为零。确认 UI 结果原文、路径和行号可用。
- [ ] 使用临时数据库副本完成每个发布步骤故障注入、并发读取、断电模拟、关闭开关与一致快照回退演练；比对版本状态与向量覆盖。不得操作用户实际数据。
- [ ] 在同一标注语料、TopK、来源范围下对比现有混合和 Lexical，记录设备、冷/热缓存、建库吞吐、索引体积、峰值内存、p50/p95、Recall@K 与标识符/中文/跨语言分类；结果与样本数入报告。没有真实低功耗设备时标记未验证，不套用其他项目数据。
- [ ] 运行各任务相关测试及与跨模块变更匹配的项目构建和回归；执行构建前检查脚本副作用。汇总实际通过、失败及未测项。上线前完成默认关闭、旧配置兼容、授权隔离、无模型、数据一致性与回滚六项门槛。

## 重点审查映射

| 风险输入或故障 | 所属测试任务 |
| --- | --- |
| 旧配置/数字枚举、未知模式导致意外调用向量 | 0、1、5 |
| 三库中途失败、重启、并发查询与旧代回收 | 2、3、8 |
| 范围外高分结果挤占候选，空授权或路径前缀泄漏 | 4、6、7 |
| 文件被更新后旧向量或旧图谱进入搜索 | 2、3、8 |
| 启动监听、聊天、MCP、跨库绕过无模型分流 | 3、5、6、7、8 |

性能、图谱恢复策略和数据迁移耗时必须在实现后的真实设备/数据库上测量；本计划不声称已有性能收益或代码已验证。
