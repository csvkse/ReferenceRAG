# 知识库隔离、多连接器与多模型执行架构规范

> 版本：v3.3；统一审核与修订日期：2026-10-31（Asia/Hong_Kong）。
> 跨专题核心契约、状态/版本词典与实施顺序以[统一契约与实施计划](unified-architecture-contracts-and-implementation-plan.md)为准；本文保留总体架构职责。
> 状态：目标架构与实施设计，尚未实现；本轮只修改本文，不修改业务代码、不安装依赖、不清理数据。
> 决策前提：允许破坏性重构，不承担旧配置、旧 API、历史索引格式与历史 ID 的兼容义务。
> 证据边界：仓库源码、项目包声明与官方资料；没有通过 GPU 实测或迁移演练，不承诺性能倍数、复用比例、工期和无损升级。

## 1. 目标、范围与已修正的结论

目标是将系统从全局存储、全局嵌入模型和全局聊天客户端，演进为显式知识库上下文、可组合连接器、按请求选择模型及受预算约束的执行系统。

首版必须完成：
1. 一个 KnowledgeBase 聚合多个 Connector；存储、索引配置、访问授权以知识库为边界。
2. 每库绑定一个不可变逻辑 EmbeddingSpec；Query/Document 可分别路由本地或线上已验证实现，不因仅更换执行端点重建已有向量；不同知识库可使用不同逻辑模型与维度。
3. 本地 ONNX/GGUF 与远端 API 共用能力契约；聊天按会话记忆选择、按单次执行持有资源。
4. 跨库检索有候选预算、排序策略、超时与部分成功语义。
5. 索引、重建、删除、模型禁用均有任务状态、资源排空与失败反馈。
6. Web、HTTP、IPC、MCP、聊天工具、启动同步和后台维护使用同一业务编排入口。
7. 支持物理格式解析与内容画像分离的处理管道，以及可动态发布的识别/切片配置、外部适配器服务和后续受信任 worker 代码包；详见[解析与适配器管理专题](document-parsing-content-profiles-and-adapter-management-architecture.md)。
8. 支持基于实际编码配方、角色与历史生产者的兼容准入，区分无需重建、数值近似和真正空间变更；详见[嵌入兼容与双路由专题](embedding-compatibility-and-query-document-routing-architecture.md)。

明确不做：旧版配置桥、全局 Store 兼容门面、旧路由双写、历史索引搬迁、自动保留旧数据库格式；首版也不建设分布式调度、自研推理引擎、通用脚本工作流平台或主进程任意插件热加载沙箱。后续动态扩展采用受信任服务/worker 与版本化配置，不把 AssemblyLoadContext 当安全隔离。

允许首次部署新架构时显式重置旧索引、重新接入数据；这是未来部署操作的设计，不是本次已经执行的数据删除授权。源文件不属于派生索引，重置知识库不得隐式删除外部源文件。

原方案的修正：
| 原结论 | 本版决策 |
| :--- | :--- |
| 保持旧业务 API 不变，门面透明路由 | 重写契约，显式传递知识库、权限范围和索引版本 |
| 三库物理拆分必然最优 | 每库单个 SQLite 文件为首选；先验证事务、向量扩展和竞争，再决定拆分 |
| 嵌入常驻两个、聊天常驻一个即可防 OOM | 按设备统一预留预算；模型数量只是可调的附加上限 |
| 每库绑定模型后永远匹配 | 校验嵌入配置指纹、实际维度与索引代次 |
| 重建零锁表、零碎片、完全不影响其他库 | 允许目标库维护停顿，控制共享 CPU/GPU/磁盘竞争，不作绝对保证 |
| 任意模型不可用都自动回退 | 默认严格执行显式选择；回退需策略授权，禁止跨嵌入空间替换 |
| 六个视图改造即可闭环 | 按前台、工具、维护、后台、协议入口共同验收 |
| 确定复用率与固定天数 | 只列可复用边界与里程碑退出条件，工作量由实施时验证确定 |

## 2. 业界方案调研与取舍

以下仅归纳官方文档明确支持的能力；“本项目采用”是本次设计判断，并非这些系统保证相同部署下的效果。来源链接与适用边界集中列在第 15 节。

| 方案 | 已确认的能力 | 借鉴到本项目 | 不直接照搬的部分 |
| :--- | :--- | :--- | :--- |
| Dify Classic Knowledge Retrieval [S1] | 多知识库召回后再按节点级设置处理，区分知识库和节点两层设置 | 单库召回与跨库融合分层；保留来源和元数据过滤 | 不据此宣称所有多库场景必须 Rerank，也不照搬工作流平台 |
| LlamaIndex IngestionPipeline [S2] | 转换流水线、文档 ID/内容哈希管理与转换缓存 | 文档版本与处理配置共同决定增量失效；连接器输出统一契约 | 不为复用 Python 框架强行引入 Python 常驻服务 |
| Ollama [S3] | 模型驻留、请求并发和等待队列分别治理；并发上下文影响内存 | 区分驻留预算、推理槽位和排队；借鉴空闲卸载 | 不把固定常驻数量当作跨设备内存证明 |
| llama-swap [S4] | 按模型路由、TTL、并发限制及分组/容量规则 | 可选外部推理进程管理；借鉴资源组与置换成本 | 它是服务/代理，不是直接链接进 .NET 的库；容量声明不等于自动显存探测 |
| vLLM [S5] | KV Cache 压力下抢占与重算；并发量影响缓存需求 | 预算包含 KV、上下文和并发；区分首字与吞吐 | 不在 LLamaSharp 适配层重造 PagedAttention 或 token 调度 |
| Qdrant [S6] | 命名向量空间可分别配置维度和距离度量 | 用明确的向量空间身份隔离配置；保留向量后端扩展点 | 不再声称异构维度必然只能拆 Collection；是否引入服务取决于规模 |
| Elasticsearch RRF [S7] | 按各召回列表中的名次融合 | 不混加异构余弦分数；分数类型显式化 | RRF 不会将各库第一名自动变成同等相关，更不是相关性概率 |
| Microsoft.Extensions.AI [S8] | IChatClient、IEmbeddingGenerator 及调用中间件 | 优先使用已有标准消息与能力接口，在外层补资源租约 | 通用调用抽象不替代本项目的知识库授权与设备预算 |

### 2.1 三条实现路线

| 路线 | 优势 | 成本与限制 | 本版选择 |
| :--- | :--- | :--- | :--- |
| A：保留 .NET 嵌入式推理与 SQLite，重建编排边界 | 复用现有部署和硬件后端，离线可用，无额外服务必需项 | 需实现模型预算、索引任务和本地资源生命周期 | 默认路线 |
| B：知识库留在本机，推理经 Ollama/llama-swap/vLLM | 原生推理故障更易按进程隔离，可借用成熟服务调度 | 增加安装、端口、进程管理与服务健康检查；服务能力不同 | 同一模型契约下可选，不强制用户部署 |
| C：外部推理与 Qdrant 等独立检索服务 | 适合更大规模和独立扩容 | 网络与运维成本增加，备份和一致性更复杂 | 测得本地后端达不到目标后再实施 |

不并行维护两套业务模型。无论 A/B/C，知识库身份、授权、索引版本、检索结果与任务契约保持一致；后端自身可采用不同存储实现。

## 3. 当前系统资产与依赖核对

### 3.1 已有基础设施

下面的“已有”指源码存在且已检查相应职责，不代表已经满足多知识库目标。相对链接均以本文目录为起点。

| 资产与证据 | 已有能力 | 复用与必须改动的边界 |
| :--- | :--- | :--- |
| [模型后端适配器](../Infrastructure/ReferenceRAG.Infrastructure/Features/ModelManagement/Adapters/IEmbeddingBackendAdapter.cs)、[重排适配器](../Infrastructure/ReferenceRAG.Infrastructure/Features/ModelManagement/Adapters/IRerankBackendAdapter.cs) | ONNX/LLama/OpenAI 嵌入及重排适配、初始化、批处理、释放 | 复用引擎交互；改为不可变配置构造和按运行实例管理，取消全局模型依赖 |
| [ModelBackendStrategyFactory](../Infrastructure/ReferenceRAG.Infrastructure/Features/ModelManagement/Strategies/ModelBackendStrategyFactory.cs) | 按后端创建适配器，已有硬件规划入口 | 复用工厂选择；工厂不得绕过中央预算自行加载 |
| [ModelLifecycleStateMachine](../Infrastructure/ReferenceRAG.Infrastructure/Features/ModelManagement/StateMachine/ModelLifecycleStateMachine.cs)、[ModelLease](../Infrastructure/ReferenceRAG.Infrastructure/Features/ModelManagement/StateMachine/ModelLease.cs) | 生命周期、活动借用计数与空闲卸载 | 每个 RuntimeKey 独立实例；补加载合并、排空结果、取消和调度协调；当前卸载等待超时可返回而未释放，不能当成成功 |
| [GpuMemoryManager](../Infrastructure/ReferenceRAG.Infrastructure/Features/ModelManagement/Services/GpuMemoryManager.cs) | 推理计数、空闲释放、nvidia-smi 查询与缓存 | 复用为观测器；不是预算仲裁器，不能证明并行加载安全；非 NVIDIA 需对应设备探测或保守静态预算 |
| [ChatPerformancePlanner](../Infrastructure/ReferenceRAG.Infrastructure/Features/HardwareAcceleration/ChatPerformancePlanner.cs) | 聊天配置与硬件执行规划 | 提供预算估计输入，须用实际加载结果校正 |
| [LocalLlamaChatClient](../Business/ReferenceRAG.Business/Features/Chat/Services/LocalLlamaChatClient.cs)、[MafChatService](../Business/ReferenceRAG.Business/Features/Chat/Services/MafChatService.cs) | IChatClient、本地与线上聊天、工具调用、会话与流式输出 | 复用消息处理；移除单一 _client、全局上下文配置和全局模型切换 |
| [ToolCallingExecutionStateMachine](../Business/ReferenceRAG.Business/Features/Chat/Services/ToolCallingExecutionStateMachine.cs) | 路由、工具执行与回答阶段 | 明确推理阶段租约边界，自动工具调用中间件不能隐藏资源等待 |
| [FileIndexPipeline](../Business/ReferenceRAG.Business/Features/Indexing/Services/FileIndexPipeline.cs)、[IndexingPipeline](../Business/ReferenceRAG.Business/Features/Indexing/Services/IndexingPipeline.cs) | 内容哈希、分块、推理写入及有界 Channel | 复用处理算法；改输入契约、配置快照、任务代次和提交事务；现有实现有全量 ToList，流式入口不等于全链路有界内存 |
| [索引策略](../Business/ReferenceRAG.Business/Features/Indexing/Strategies/IIndexExecutionStrategy.cs)、[索引状态机](../Business/ReferenceRAG.Business/Features/Indexing/StateMachine/IndexJobStateMachine.cs) | 增量/重建/重切片/仅向量的策略与进度广播 | 沿用策略分类；补持久化任务、检查点、幂等提交和中断恢复，不再另造同义策略体系 |
| [FileEventChannel](../Infrastructure/ReferenceRAG.Infrastructure/Features/FileMonitor/Services/FileEventChannel.cs)、[FileMonitorService](../Infrastructure/ReferenceRAG.Infrastructure/Features/FileMonitor/Services/FileMonitorService.cs) | 有界事件通道、监控、防抖 | 包装为本地连接器；新增 kbId/connectorId、溢出补扫和 durable checkpoint；内存 Channel 不持久化 |
| [SqliteVectorStore](../Infrastructure/ReferenceRAG.Infrastructure/Features/Database/SqliteVectorStore.cs)、[Fts5BM25Store](../Infrastructure/ReferenceRAG.Infrastructure/Features/Database/Fts5BM25Store.cs)、[SqliteGraphStore](../Infrastructure/ReferenceRAG.Infrastructure/Features/Database/SqliteGraphStore.cs) | sqlite-vec、全文和图谱存储 | 复用 SQL 与算法，重新组织每库连接与事务；vectors.db 当前也包含 files/chunks，并非可随意删除的纯向量缓存 |
| [StorageExtensions](../Infrastructure/ReferenceRAG.Infrastructure/Features/Database/Extensions/StorageExtensions.cs)、[SharedSqliteConnection](../Infrastructure/ReferenceRAG.Infrastructure/Features/Database/SharedSqliteConnection.cs) | 当前三类存储全局单例；共享连接可串行访问 | 改每库句柄和租约；现注册是三份数据库，不能根据 SharedSqliteConnection 注释误判已经共享事务 |
| [搜索策略](../Business/ReferenceRAG.Business/Features/Search/Strategies/SearchStrategyFactory.cs)、[HybridSearchService](../Business/ReferenceRAG.Business/Features/Search/Services/HybridSearchService.cs)、[SearchService](../Business/ReferenceRAG.Business/Features/Search/Services/SearchService.cs) | 检索适配、RRF/加权融合、重排、图谱扩展和耗时 | 增加跨库编排；拆开向量失败与全文成功；清除全局模型遥测依赖 |
| [ModelTelemetryService](../Infrastructure/ReferenceRAG.Infrastructure/Features/ModelManagement/Telemetry/ModelTelemetryService.cs) | ONNX/LLama/OpenAI 遥测策略 | 按 RuntimeKey、设备和请求关联；观测缺失返回 Unknown，不能伪造线上显存 |
| [ApiKeyPermissionMatrix](../Infrastructure/ReferenceRAG.Infrastructure/Features/Security/ApiKeyPermissionMatrix.cs) | API Key、动作与旧源范围控制 | 复用验证基础；新权限只认稳定 kbId 与动作，不保留源名称兼容规则 |

当前协议事实也需纠正：[OpenAIModelsController](../Host/ReferenceRAG.ApiHost/Controllers/OpenAIModelsController.cs)已列出多类模型并提供 include_all；[OpenAIChatController](../Host/ReferenceRAG.ApiHost/Controllers/OpenAIChatController.cs)当前普通生成路径没有用请求 model 选择实际客户端。目标是把发现、校验、执行与响应统一，而非仅扩大列表。

### 3.2 已声明第三方包与候选库

包版本来自 [Directory.Packages.props](../Directory.Packages.props)，引用来自 Infrastructure/Business/Host 的 csproj；这里确认的是仓库声明与源码使用，没有执行 restore 或证明所有平台本机原生依赖均能加载。

| 库/机制 | 当前状态 | 决策 |
| :--- | :--- | :--- |
| Microsoft.Extensions.AI / AI.OpenAI 10.10.0 | Business 已直接引用，IChatClient 已使用 | 作为聊天基础抽象；嵌入接口能覆盖现有语义时接 IEmbeddingGenerator，Query/Document 前缀与指纹仍由本项目定义 [S8] |
| LLamaSharp 0.30.0 与多种后端 | Infrastructure 引用，按构建条件选择 | 保留；能力、线程安全和上下文隔离按具体适配器验证，不升级原生后端来凑统一接口 |
| ONNX Runtime 1.24.4，GenAI.Managed 0.14.1 | 按构建条件引用 | 保留现有执行引擎；不假设一个加载实例可以任意并发 |
| Microsoft.Data.Sqlite 10.0.12 | Infrastructure 已引用 | 默认持久化与事务基础；SQLite async 方法不是原生异步 I/O [S13] |
| Microsoft.SemanticKernel.Connectors.SqliteVec 1.74.0-preview；VectorData.Abstractions 10.10.0 | 已引用；当前向量存储自写 SQL 并加载扩展 | 不等于已采用完整标准连接器；先验证锁定版本。当前官方连接器页面已显示 CommunityToolkit.VectorData.SqliteVec，且无 HybridSearch，不能直接替换整个搜索管道 [S9] |
| System.Threading.Channels / BackgroundService / TimeProvider | 平台能力，Channel 已使用 | 首版队列、worker、可测试计时器；任务持久化自行落库 [S10] |
| System.Threading.RateLimiting / ASP.NET Core RateLimiting | 平台能力；本次未确认现系统已接入相应限流策略 | 可用于入口和远端配额；速率限制与并发限制分开，不能只用 SemaphoreSlim [S14] |
| Microsoft.Extensions.Http.Resilience / Polly | 未在包声明中发现直接引用 | 建议新增前做小型验证；负责超时、熔断和有限重试，禁用 SDK 与外层重复重试 [S11] |
| Stateless | 未在包声明中发现直接引用 | 可选，首版优先扩展已有状态机；其异步状态机不是线程安全并发锁，也不自动持久化 [S12] |
| Quartz.NET | 未在包声明中发现直接引用 | 多类定时连接器真正落地时评估；不拿定时器库替代索引任务检查点和幂等性 [S15] |
| AI RoutingChatClient / OrderedFailoverChatClient | 官方有实验类型；未确认仓库锁定包是否可用 | 仅借鉴请求路由/首个输出前回退；经 API 与生命周期验证后可选，不成为首版硬依赖 [S16] |

不同时引入 Stateless、Quartz、通用消息总线和第二套 Agent 框架。每新增库必须证明填补明确缺口，核对许可证、锁定版本、目标 .NET 与原生平台依赖；本轮不执行安装。

## 4. 核心架构与设计模式

~~~mermaid
flowchart TB
    Entry["Web / HTTP / IPC / MCP / Chat Tools"] --> App["KnowledgeBase / Search / Chat / Job 应用编排"]
    App --> Auth["按 kbId 与动作授权"]
    App --> Catalog["Catalog: 配置、任务、权限"]
    App --> Storage["按库获取 StorageLease"]
    App --> Router["ModelRouter: 校验能力与解析配置"]
    Router --> Scheduler["资源预算与请求调度"]
    Scheduler --> Local["ONNX / LLama 本地适配器"]
    Scheduler --> Remote["API / 外部推理服务适配器"]
    Storage --> KB["每库 kb.db: 文档、分块、FTS、图谱、向量"]
    App --> Fusion["单库召回与跨库融合策略"]
    Workers["连接器 / 索引 worker"] --> App
~~~

所有业务入口调用相同应用服务；Controller、MCP Tool 和 UI 不直接拼模型路径、枚举数据库文件或驱逐模型。领域对象只含 ID、值对象、状态与版本，不持有 SqliteConnection、watcher、HttpClient 或运行实例。

| 问题 | 模式 | 使用位置与约束 |
| :--- | :--- | :--- |
| 来源与模型引擎差异 | Adapter + Factory | ConnectorFactory、现有 BackendStrategyFactory；新增一种后端不修改检索业务 |
| 召回、排序、同步和回退差异 | Strategy | 既有搜索/索引策略延伸；选择规则确定、可记录，不串起多套隐式回退 |
| 任务、连接器和模型生命周期 | State Machine | 各自状态机分别管理，不用一个状态枚举混合所有生命周期 |
| 资源与句柄占用 | Lease + Reference Counting | 模型、存储均异步借用、幂等归还；禁止请求端释放池拥有的客户端 |
| 输入背压与阶段处理 | Bounded Producer–Consumer | Channel + worker；队列满时等待或明确拒绝，不静默丢事件 |
| 外部 API 稳健性 | Decorator / Resilience Pipeline | timeout、rate limit、circuit breaker；不掩盖认证与配置错误 |
| 跨文件控制操作 | 显式过程管理器 | Catalog 的操作日志 + kb.db 应用版本；不伪装成跨库 ACID |
| 请求上下文 | Immutable Request Context | 权限、配置修订和 deadline 显式传递，不采用静态 CurrentKnowledgeBase |
| 对外业务入口 | Application Service | 聚合领域步骤；不为“模式完整”再加通用 Mediator/EventBus |

## 5. 领域身份、版本与持久化权威

### 5.1 领域契约

| 对象 | 必要字段与不变量 |
| :--- | :--- |
| KnowledgeBase | Id、Name、Enabled、DesiredConfigRevision/EffectiveConfigRevision、EmbeddingSpecId、ProcessingProfileRevision；名称可改，ID 不随路径变化 |
| ConnectorDefinition | Id、KnowledgeBaseId、Kind、配置修订、CredentialRef、SyncPolicy；一个连接器属于一个库 |
| Document | 每库内 DocumentId、ConnectorId、SourceKey、Locator、ContentHash、SourceRevision；唯一约束 (ConnectorId, SourceKey) |
| Chunk | DocumentId、DocumentVersion、ContentGeneration、Ordinal、EvidenceText/EmbeddingText、BlockRefs、SourceAnchors；完整引用包含 kbId、generation 和文档版本 |
| ProviderDefinition | Id、ProtocolKind、BaseUri、CredentialRef、Enabled、Revision、远端配额策略；密钥不进入业务返回 |
| ModelDefinition | 稳定内部 Id、ProviderId、RemoteModelName 或 AssetRevisionId、Capabilities；显示名称不承担路由身份 |
| ModelAssetRevision | 权重/Tokenizer/mmproj 文件指纹、格式、量化、大小；仅本地资产使用 |
| EmbeddingSpec | 不可变逻辑编码契约：参考模型/配对修订、RepresentationKind、维度/metric、Query/Document 角色配方；不包含 Provider/URL/Key |
| EncodingImplementationRevision / CorpusCohort | 前者记录真实权重/导出/Tokenizer/精度/pooling/截断/角色处理；后者记录文档向量生产者。不能把同名或同维度当作兼容 |
| EmbeddingRoutePolicy / CompatibilityAdmission | Query 与 Document 主/备用端点、QoS/出站/路由修订，以及 QueryEncodingRevision 到全部现存 corpus cohort 的有向准入 |
| ProcessingProfile / ProcessingPlan | 前者保存文档/区域路由与默认策略；后者固定每文档实际 Parser/Profiler/Chunker/Renderer/GraphProjector 修订、IR schema、tokenizer 与预算，不假设所有文档共享一套切片参数 |
| ExecutionProfile | DevicePolicy、ContextWindow、BatchTokens、Parallelism、后端选项；修订不可原地改变运行实例 |
| RuntimeInstance | RuntimeKey、RuntimeState、ActiveLeases、预算预留、测量值；进程内信息，不作为配置持久化 |
| IndexManifest | IndexEpoch、ReadRevision、ContentGeneration、Active/TargetEmbeddingSpecHash、VectorGeneration、实际 cohort 引用数/集合修订、每文档 Plan/各产物修订/编码溯源、实际/目标配置与能力状态 |
| Conversation | Id、所选 ModelId/ProfileRevision、知识库集合、消息与会话版本；无永久模型租约 |
| Job | Id、Operation、kbId、ExpectedIndexEpoch、语义计划快照、JobStatus/OperationPhase、Attempt、检查点、计数、错误、幂等键 |

RuntimeKey 至少包含具体模型/编码实现修订、执行配置修订、设备与后端；同名模型不同量化/上下文配置不共享不兼容实例。EmbeddingSpecHash 用稳定序列化后 SHA-256 生成，标识逻辑 Query/Document 编码契约，不把 ProviderId/URL/密钥或运行设备混入语义身份。实际权重转换、量化、精度与截断差异记录在 EncodingImplementationRevision，经角色/语料准入后才可实现同契约替代；不能通过手工填相同 SpecId 绕过校验。

版本字段采用[统一计划第 3 节](unified-architecture-contracts-and-implementation-plan.md)：ExpectedIndexEpoch/StorageLease.IndexEpochSnapshot 仅指写入栅栏；ContentGeneration 指整库内容代次，DocumentVersion 指单文档证据/分块版本，VectorGeneration 指整库向量代次，局部向量/全文/图输出各自有产物修订。ReadRevision 随任何可查询事实发布/删除/失效同事务递增，不能用 cohort-set 或 generation 代替普通数据变化。正式引用包含 kbId、内容代次、文档版本与块/来源锚点。

远端浮动模型别名不能完全锁定权重，界面必须标明“提供商未提供不可变版本”；维度校验与固定探针只能发现部分漂移，不能宣称证明模型未变。可选择固定修订或显式重建。

纯执行端点路由切换使用 Query/Document RouteRevision 与 ProducerAdmissionEpoch，不推进 IndexEpoch、ContentGeneration 或 VectorGeneration；在途请求/批次持有快照，撤销后迟到写入必须被门控拒绝。逻辑编码契约改变才触发空间重建；有限测试只支持注明范围的兼容结论，不保证任意未来输入逐位或排序不变。

### 5.2 文档身份与来源

- Connector 输出 SourceKey 与 Locator 两项；SourceKey 是该连接器命名空间内的稳定键，Locator 是当前地址。
- 本地首版 SourceKey 使用规范化根目录相对路径；目录大小写与路径比较遵循目标文件系统，不能对所有 URI 统一转小写。
- 确认的 Rename 事件可以维护 DocumentId；仅靠全量扫描无法确认移动时，按删除+新增处理，不承诺跨移动稳定。
- 网页按连接器明确的 canonical URL 规则；Git 优先 repository/ref/path 定位，commit 记录为 SourceRevision。协议适配器解释来源，不伪装成本地文件。
- 同一资源由两个连接器接入时，首版保留两份 provenance；删除其中一项不能删除另一项。可提示重复接入，但不以内容哈希合并授权。
- 内容去重只在已授权候选内用于展示，可合并来源；不跨知识库进行写时共享和隐式物理去重。

### 5.3 唯一权威与目录

~~~text
data/
  system/catalog.db           # 配置定义、授权、会话、任务、控制操作日志
  kbs/{opaque-kb-id}/kb.db     # 文档元数据、规范文本、chunks、FTS、graph、vec、实际 manifest、检查点
  models/{asset-revision}/    # 权重与配套文件
~~~

appsettings 只保留服务启动、DataRoot 和宿主配置。动态 KnowledgeBase、Provider、Model、绑定和策略均写 Catalog；不再同时维护可写 kb_catalog.json/meta.json。备份 manifest 仅为导出产物。

Catalog 保存不可变定义、DesiredConfigRevision、路由/准入与作业；kb.db 保存 EffectiveConfigRevision、已发布事实、实际活动 cohort 计数/集合、ReadRevision 与 LastAppliedOperation。配置命令先记 OperationId/期望，再以库事务应用，最后 Catalog 确认；重启按实际应用记录对账。Catalog 中的定义“已激活”不等于该库已经切换或重建完成。

全局库和每库间没有跨文件原子提交保证。建立新库时可记录 Provisioning，失败后重试同一操作；文件夹存在不代表知识库 Ready。配置更新使用 ExpectedRevision，冲突返回 409。

### 5.4 多格式解析、领域画像与动态管理扩展

处理顺序为 FormatDetector → DocumentParser → ContentProfiler → ProcessingPlanner → ChunkStrategy/Renderer → GraphProjector → 校验与版本发布。小说/合同是内容画像，PDF/DOCX/TXT 是物理格式；表格既可能是格式，也可能是文档区域。一个合同中的附件表格必须可采用块级策略，不依赖扩展名决定整篇切法。

统一 ParsedArtifact IR 保存章节/条款/表格/单元格及来源坐标；规范文本不再是唯一解析缓存。PDF 页/坐标、DOCX part、工作表/单元格不能伪装成普通文本行号。原始快照与暂存产物位于知识库授权范围内的受控 artifacts 存储，引用与配额由宿主管理，外部插件不获得任意路径读写权限。

动态管理分层交付：L0 在线发布声明式画像/切片规则，L1 注册新适配器服务，L2 受信任代码包在独立 worker 中激活和排空。管理模块包含目录、画像路由、处理配置、预览评测、发布与依赖；“发布可用”和“绑定知识库并重处理”是两个操作。

画像切换沿用 IndexEpoch 栅栏与任务快照；实际处理版本逐文档保存，同一 EmbeddingSpec 下允许暂时混合处理修订并明确报告。Parser/清洗改变触发 Reparse，边界策略改变触发 Rechunk；仅 EmbeddingText 改变触发同空间局部 RefreshVectors，仅 RetrievalText 改变触发 RefreshRetrievalView，图投影改变触发 ReprojectGraph。改变逻辑模型/全库编码指令等空间规范才触发整库 Reembed，已验证的执行端点替代不触发重建；只改图不重算正文向量。原 IR/原始输入缺失且来源不可访问时明确拒绝相应重处理。

所有核心契约、小说/合同/表格策略、服务协议、候选库、验收与 A0–A5 阶段见[多格式解析、内容画像与动态适配器管理架构](document-parsing-content-profiles-and-adapter-management-architecture.md)。语义图谱抽取是独立能力，不声称切片优化会自动建立真实实体关系。

### 5.5 同类编码实现替代与 Query/Document 分路

一个逻辑嵌入契约可有多个经验证端点，例如本地 ONNX Query 与线上固定修订 Document。选择依据是实际编码配方与交叉检索，不是“本地/线上”或“m3”名称。当前 GGUF 工厂传 Mean pooling、单条输入受 512 批次 token 截断，远端服务 mode 重载忽略 Query/Document；均须在准入前核对，不能直接宣称当前本地 M3 与官方/线上 M3 等价。

知识库维护实际生产者 cohort 集合；激活新 Query 必须覆盖全部现有 cohort，新增 Document producer 必须被所有正式 Query 主/备用覆盖。准入按角色和具体修订记录，不可传递性合并；不通过时保留旧路由、降级全文或明确失败，不能自动混空间写入。角色 Adapter 负责 prompt/input_type/task 的单次准确映射。

验证、缓存/任务切换/撤销门控、UI/API、候选参考库与 B0–B4 阶段见[嵌入模型兼容切换与 Query/Document 双路由架构](embedding-compatibility-and-query-document-routing-architecture.md)。此专题补全 EmbeddingSpec 的语义，不改变“真正不同空间禁止直接混用”的约束。

## 6. 存储、索引一致性与破坏性重建

### 6.1 存储路线与技术验证门

默认每库单个 kb.db，让规范文本、chunks、FTS 与图谱写入在短事务中维护；向量表也在同文件内，按 generation/spec 关联。首版每库只保留一个正式索引代次，不强制影子索引或双写。

选择理由：减少跨文件一致性协议和句柄，不代表单库文件无竞争。WAL、受限读连接与单 writer 是目标组织方式；不在线程间并发使用同一 SqliteConnection。不要在数据库事务或连接锁内进行网络、模型加载或推理。

G0 必须验证锁定版本的 sqlite-vec 与 FTS5 在单库共存、事务回滚、并发读写、过滤和备份恢复。若失败，先解决具体原因；确有必要才拆 vector 文件，并补 outbox、epoch 对账和读取一致性契约，不能悄悄改回三库却仍声称原子。

现有 SharedSqliteConnection 的单锁实现可作为串行原型，不作为最终并发性能证明。Microsoft.Data.Sqlite 的 Async API 实际同步执行 [S13]；跨库并发需要有限 worker，不把加 Task.WhenAll 当作数据库异步化。

### 6.2 索引提交契约

1. 连接器抓取、解析、分块、向量计算在事务外执行，输入配置使用任务启动时快照。
2. 每文档产生一个 IndexBatch：ExpectedIndexEpoch、ExpectedDocumentVersion、SourceRevision、内容/输入视图哈希、语义 ProcessingPlanHash、EmbeddingSpecHash、实际模型调用的 AttemptEnvelope、chunks、vectors、图谱更新。ProcessingPlan 固定语义配方/预算而非物理 Provider；每次调用记录端点/实现/路由/准入/cohort。提交时重检生产者准入，撤销后的迟到响应不能污染索引。
3. 提交时检查控制栅栏与代次；重建前启动的旧任务不得写进新代次。同一文档还需 ExpectedDocumentVersion 条件更新；来源修订可能是不透明字符串，不能按字符串大小判新旧，旧扫描结果不得覆盖后到事件。发生冲突重新读取当前来源，不能盲目重放陈旧内容。
4. 文档元数据、分块、FTS、graph provenance 与可用向量采用单文档事务发布；大文档先有界写 staging，最后短事务切换已验证的文档版本指针，未发布数据不得进入召回。批次重复执行不得增加重复文档或分块。
5. 若嵌入暂时失败，可单独提交完整的文本/分块/全文/图谱版本并标记 VectorPending；旧文档向量同时失效，不能关联到新 chunks。
6. 仅已提交文档进入检索；图谱边需要 document/chunk provenance，删除文档仅移除其贡献，共享实体按剩余引用保留。
7. 任务完成计数取已提交结果，不取“已排队”或“已计算”。每次正式发布/删除同时更新 ReadRevision、各输出指针与实际 cohort 引用数；首次/末次活动引用改变集合修订。事务成功后广播事件，广播失败不回滚事实。可选向量/图未完成时记录能力 Pending 与持久化补偿/人工动作，Job 不伪成功。

增量失效条件 = 内容/来源版本变化，或 Parser/Profiler/Chunker/Renderer/GraphProjector/EmbeddingSpec 的相关指纹变化；分阶段失效避免只改图谱却重跑解析与嵌入。不能只比较文本哈希跳过换模型或换适配器后的工作。

### 6.3 三类重建操作

| 操作 | 保留 | 破坏与不可用范围 | 失败处理 |
| :--- | :--- | :--- | :--- |
| Reembed | 规范文本、chunks、FTS、graph | 失效并重算向量；目标库保留 lexical/graph 检索，响应标记 degraded | 持久化 VectorPending，恢复当前任务；不继续使用旧嵌入空间 |
| Rechunk | 连接器定义、文档身份与规范文本 | 重做 chunks、FTS、graph provenance、vectors；首版目标库暂不查询 | 目标库 NeedsRebuild，可重试；不承诺回滚旧索引 |
| Reingest | 知识库/连接器配置和身份映射 | 重新读取源、解析并构建全部派生数据；目标库暂不查询 | 源不可读时明确失败，不能以空库报成功 |

Reembed 仅当已有分块满足目标编码/预算时可选，否则先 Rechunk，禁止静默截断。Rechunk 需要对应已验证 IR，纯文本来源可使用其有来源映射的文本 IR；不能只凭规范文本恢复 PDF/工作表的完整结构。Reingest 必须可重读来源或受控原始快照。目标 spec/cohort 在 staging 构建，最终通过验证后才切换 ActiveEmbeddingSpecHash/VectorGeneration；失败/取消不自动恢复已失效的旧空间。

上表 Reembed 指整库空间重建。保持 EmbeddingSpecHash 的局部增强输入刷新另称 RefreshVectors，仅原子更新受影响文档的 VectorArtifactRevision，不推进库级 VectorGeneration、不删除其他文档向量。仅派生展示/全文视图改变使用 RefreshRetrievalView，推进 RetrievalViewRevision 并同步 FTS；不改变证据视图与引用。多视图同时变化按各自输出哈希并集执行，规则见解析专题第 8 节。

重建是 JobStatus=Running 下的 OperationPhase：Quiescing → Invalidating → Building → Validating → Publishing；任务按 Succeeded/PartiallySucceeded/Failed/Cancelled 终结，Ready/NeedsRebuild 只写入各检索能力清单。失效前取消保留原能力，失效后失败/取消保留真实目标 manifest 与 NeedsRebuild。目标库维护栅栏暂停写入，排空失败不执行破坏步骤；其他库仍受共享硬件预算约束。

Reembed 的“保持全文可用”通过只失效向量表实现，不能删除 kb.db。完全删除知识库是独立操作：阻止新借用 → 停止连接器/任务 → 排空租约 → 关闭连接 → 删除明确库目录 → Catalog 标记完成。失败留下可重试 Deleting，不删除外部原始来源。

### 6.4 存储借用与快照

~~~csharp
// 目标契约示意；不是现存可编译实现，相关值对象在实施时定义。
public interface IKnowledgeBaseStorageManager
{
    ValueTask<IStorageLease> AcquireAsync(
        KnowledgeBaseId id, StorageAccess access, CancellationToken ct);
}
public interface IStorageLease : IAsyncDisposable
{
    KnowledgeBaseId KnowledgeBaseId { get; }
    long IndexEpochSnapshot { get; }
    IKnowledgeBaseStore Store { get; }
}
public interface IKnowledgeBaseStore
{
    Task<CommitResult> CommitDocumentAsync(IndexBatch batch, CancellationToken ct);
    Task<RecallSnapshot> RecallAsync(RecallRequest request, CancellationToken ct);
}
~~~

管理器拥有连接和 Store，调用者只归还 Lease。池淘汰需要零借用且无维护任务；不返回“所有活跃连接”作为业务库列表。索引在模型/网络等待前释放数据库借用，用语义计划/版本令牌在提交时重取短借用校验；维护令牌与连接借用区分，避免排空自身持有的资源。查询按获准 Effective 配置选择库，按需打开。

每库候选读取使用一致快照，返回 generation 与引用；模型推理前后不长期持有数据库事务。跨库结果不保证同一时间点快照，但每项来源可追溯。引用若代次已过期，文件读取端返回可识别的 stale_reference，不能按旧 chunkId 错取新内容。

重建操作还包含局部 Reparse（重新解析原始输入并执行受影响下游）与 ReprojectGraph（只替换图投影修订）；两者的输入依赖、文档版本与证据映射按第 5.4 节专题执行。全库与局部任务都不能混用旧图证据和新 chunks。

备份使用 SQLite Backup API [S17]，附 schema、spec、generation 与校验信息；不能仅复制运行中的主 db 文件。外部模型权重、源文件和密钥默认不随知识库导出，恢复报告缺失依赖。

## 7. 连接器与后台任务核心契约

~~~csharp
public enum ChangeKind { Upsert, Delete, Rename }
public sealed record SourceChange(
    string ConnectorId, string SourceKey, ChangeKind Kind,
    string? Locator, string? SourceRevision, string? PreviousSourceKey);
public interface IDataConnector : IAsyncDisposable
{
    IAsyncEnumerable<SourceChange> ScanAsync(
        SyncRequest request, CancellationToken ct);
    ValueTask<Stream> OpenReadAsync(SourceChange item, CancellationToken ct);
}
~~~

接口只描述可重放的变化；Scan 完成标记、游标及分页 token 由 SyncRequest/持久化检查点协议承载。首版本地 Folder + SingleFile；Web/Git 后续按同一契约实现，不把未实现类型展示成可用选项。

- full scan 采用 ScanId + seen 集合；只有扫描完整成功才判定缺失项删除。超时、断网、分页失败或目录不可访问不等于源文档全部被删除。
- 轮询游标只能在相应文档提交后推进；事件至少一次交付，按 (ConnectorId, SourceKey, SourceRevision/ContentHash, Generation) 幂等。
- 本地 watcher 是更新提示，不是数据真相。溢出、TryWrite 失败和重启触发补扫；防抖按连接器/文档归并，不能吞掉删除语义。
- 新增连接器先验证访问与解析支持；目录重叠展示警告及归属结果。删除连接器先停生产者，再按该连接器 provenance 清理。
- 连接器状态为 Disabled、Idle、Scanning、Watching、Backoff、Faulted；扫描失败不影响其他连接器，但共享模型失败会影响多个连接器，必须分别报告。
- 远端 OpenRead 支持 cancellation、大小上限和类型验证；规范文本保存在 kb.db 供重嵌入，原始二进制是否快照可后续配置。

JobStatus 统一为 Queued/Running/WaitingForInput/Cancelling/Succeeded/PartiallySucceeded/Failed/Cancelled；OperationPhase、AttemptStatus、检索 CapabilityManifest 独立记录，禁止将 Parsing、Ready 或 Completed 当 JobStatus。分类待审是 WaitingForInput + ClassificationReview，释放资源；可选输出失败是 PartiallySucceeded + Pending/补偿动作。计数注明 document/block/vector 单位，未知总数只显示已提交数；合法迁移见统一计划第 5 节。

后台执行用有界 Channel 传 JobId，任务真相在 Catalog，队列丢失可按状态重投。首版同一 DataRoot 仅一个写进程；Web/Desktop 共享目录时必须使用宿主单实例机制或明确拒绝第二写者，不支持多实例并写。

每库一个维护操作；普通文档更新按文档串行、跨文档有限并行。重启将遗留执行 Attempt 标记 Interrupted，先核对正式发布事实，再使 Job 终结或 Queued 恢复；保留原取消意图，不盲目续跑。恢复重检计划、输入、IndexEpoch/文档版本与准入，不把任务消失当成功；不承诺分布式“恰好一次”。

## 8. 模型路由、资源预算与生命周期

### 8.1 路由与实例所有权

~~~csharp
public interface IModelRouter
{
    ValueTask<IModelLease<TClient>> AcquireAsync<TClient>(
        ModelSelection selection, WorkloadContext workload, CancellationToken ct);
}
public interface IModelLease<out TClient> : IAsyncDisposable
{
    TClient Client { get; }
    ModelExecutionIdentity Identity { get; }
}
public interface IResourceScheduler
{
    ValueTask<IResourceReservation> ReserveAsync(
        ResourceDemand demand, WorkloadContext workload, CancellationToken ct);
}
~~~

ModelSelection 包含内部 ModelId、配置修订和用途；WorkloadContext 包含优先级、deadline、RequestId。TClient 由 Chat/Embedding/Rerank 能力约束验证；不能仅用 IsEnabled 判断可执行。响应 Identity 指向实际执行模型与配置。

嵌入用途先经 IEmbeddingRouter 按逻辑 spec + Query/Document 角色解析端点和兼容准入，再使用本节通用运行实例/预算/租约；不让搜索和后台各自维护一个旁路模型单例。接口细节见第 5.5 节专题。

运行实例状态：Unloaded → Loading → Ready → Draining → Unloading → Unloaded；加载/执行可进入 Faulted。ActiveLeases 与推理槽位是计数维度，不把一个请求完成等同于整体 Ready。Enabled、AssetAvailable、ProviderHealth 分别记录，不合并进 RuntimeState。

每个 RuntimeKey 加载 single-flight。调用者取消只取消自己的等待；最后一个等待者取消后才决定取消尚未必要的加载。加载失败释放全部预留，其他等待者收到同一分类错误。

Unload 返回 Released、Busy 或 Failed；只有 Released 才能释放预算。排空超时不能返回普通完成让调度器继续超额加载。实例正在 Draining 时拒绝新借用；所有释放路径幂等，异常也归还推理槽位。

### 8.2 预算、背压与调度策略

设备预算覆盖权重、KV Cache、工作区、加载峰值和安全余量。已驻留模型使用计入预算的存量，新加载原子预留增量，活跃请求再预留可增长部分；避免同一权重重复记账。

观测值可能过期、未知或受其他进程影响。使用“配置硬预算 + 已分配账本 + 设备观测”交叉约束；不能只读空闲显存后并行放行。统一内存设备同时受 RAM 与共享内存预算约束。预算只能降低 OOM 风险，不能保证外部进程不抢占或原生后端不崩溃。

调度默认：
- 本地低显存设备可配置单重型模型；不把 Chat=1 写成所有设备和远端的永久限制。
- 远端不占本机 VRAM，但需要 Provider 并发、RPM/TPM 和等待队列预算。
- 前台检索/聊天优先于索引，加入等待老化避免后台永久饿死；索引在批次边界让出资源，不强杀在途推理。
- 同模型邻近任务可短时间合批以减少抖动，设置最大等待公平窗口，不能无限绕过其他模型。
- CPU fallback 必须有兼容适配器、RAM 预算及允许策略；失败后拒绝/降级，不无界转移负载。
- 任何队列均有容量；用户请求队满返回 429 与可用的 Retry-After，资源无法满足/后端不可用返回 503，deadline 到期返回 504。取消不触发自动重试。

首版建议起点（待 G0/G9 实测校正）：每设备推理并发 1；全局交互等待上限 64；待调度后台任务上限 256，满时提交明确失败；内存中只保留有界任务 ID 与批次。搜索总预算 60 秒、交互排队最多 10 秒、后台批次 2 分钟；子操作取自身上限与剩余总预算的较小值。冷模型无法及时加载时明确失败并提供手动预热，不暗中延长 deadline。

这些值是保守配置起点，不是性能验收承诺；内存缓冲还需字节/Token 上限，不能只按“批次数”限制超大文档。

### 8.3 工具调用与死锁消除

固定规则：不持有模型租约等待另一个尚未获准的模型资源；不持有数据库事务等待模型；不在预算锁内等待推理排空。调度锁只修改账本，耗时加载和卸载在锁外，靠状态与预留防竞争。

~~~mermaid
sequenceDiagram
    participant Turn as ChatTurn
    participant Chat as ChatLease
    participant Tools as ToolExecutor
    participant Search as Retrieval
    Turn->>Chat: 获取并执行工具决策阶段
    Chat-->>Turn: tool_calls 与可序列化消息
    Turn->>Chat: 释放生成槽位与租约
    Turn->>Tools: 执行已授权工具
    Tools->>Search: 获取嵌入/重排租约并检索
    Search-->>Tools: 结果与来源
    Tools-->>Turn: tool_results
    Turn->>Chat: 重新获取同一选定模型生成回答
    Chat-->>Turn: 流式输出
    Turn->>Chat: 完成/异常/取消均释放
~~~

不能直接把 UseFunctionInvocation 包在整个长期持有的模型租约内。目标是显式驱动生成—工具—生成循环，或实现可证明阶段释放的中间件。小显存下模型可能重新加载，必须反映冷启动代价；完整历史以消息保存，不依赖无法重建的原生 KV 上下文。

每会话同一时刻仅一个 Turn；新发送或改模型在忙碌时返回 409，用户先停止当前生成。最大工具轮数与输出 token 预算有限，避免工具循环无限占用资源。

### 8.4 禁用、删除与远端稳健性

普通禁用/删除先提供依赖预览：知识库绑定、默认选择、活动任务、模型资产变体；后端必须重检并与绑定命令共用同一控制序列，避免检查后新增依赖。

强制操作流程：标记模型/Provider Draining → 阻止新绑定/新借用 → 按策略取消任务并排空 → 重新评估依赖角色的已验证替代路径 → 安全卸载 → 删除指定资产或禁用配置。存在合规 Query/Document 替代端点时保留该能力；无替代时仅标记实际受影响的角色/向量能力不可用，不因后台 producer 停用就声称旧索引全部失效。排空失败则操作失败，不强行释放仍被使用的原生对象；删除 Provider 也不能绕过守卫。

远端稳健性采用共享 Provider 级限流、总 deadline、抖动退避、Retry-After 与熔断 [S11]。401/403、能力/维度错误不重试。嵌入可重试纯计算但仍可能重复计费；聊天 POST 默认不自动重试或 hedge，只有提供商支持的幂等保障与显式策略才允许。流式内容一旦输出不换模型、不从头重播。

## 9. 检索契约、排序与故障语义

### 9.1 请求和结果

SearchRequest 必须包含 Query、KnowledgeBaseIds、Mode、TopK、Filters、Deadline 与 FailurePolicy；调用者身份由可信宿主建立，不接受请求体自行声明权限。

新权限按 kbId + read/search/manage/export 动作；“全库”意为全部获准且启用的库。显式请求未授权库返回 403，不把“交集为空”解释成无限制。图谱扩展、引用读取、MCP 与聊天工具使用相同授权后的集合，授权先于召回和候选排序。

SearchResponse 必须包含：
- requestId、每库 generation/spec、每项复合引用与 provenance。
- 完整/部分成功状态、成功库与失败库、失败阶段及分类。
- 实际执行模型、排序策略、降级原因与每段耗时。
- 原始分数及 ScoreKind（vector_distance / cosine / bm25 / rrf / rerank），不混用阈值。

### 9.2 执行与成本控制

1. 解析授权库和 manifest，创建每库工作计划；拒绝非法过滤及维度不一致。
2. 按实际 QueryEncodingRevision/角色配方与出站策略分组，只有输出获准查询各库全部 cohort 才复用。Query 向量缓存含输入/spec/实际实现，兼容但数值有差异的端点不共键。结果缓存另含完整规范化请求、当前授权修订、各库 ReadRevision/generation/cohort-set、排序/路由及准入撤销版本；普通同 cohort 更新/删除也必须使旧结果失效。
3. 每库 BM25、vector、graph 是可独立成功的分支，只有实际需要的分支请求模型；纯全文不启动嵌入。
4. 召回限制内推送来源/目录/元数据过滤。若锁定 sqlite-vec 不支持目标过滤，G0 验证受限过召回与补取算法；返回不足不能谎称穷尽检索。
5. 单库采用已有 RRF/加权策略，异构跨库不相加原始分数；生成带来源候选后执行去重与全局排序。
6. 优先采用已配置的统一 reranker 重排获准候选；不可用时按策略采用 rank-based 融合并标记降级，或严格失败。
7. 所有分支受相同总 deadline 约束；逐分支收集错误，不让一个 Task.WhenAll 故障吞掉其他库的成功结果。

首版建议 TopK 限 1–50、单次最多 8 库；每库候选 clamp(4 × TopK, 20, 100)，全局最多 200 候选。全局截断先按各库配额取样，不能先拿异构余弦分取总 Top-200；这些为可调起点，需质量集验证。Rerank 再受总输入 token 与 Provider 批次限制。

库数超限返回校验错误并要求缩小选择，不静默遗漏库。后续可加显式 KnowledgeBaseSelectionStrategy 做语义路由，但首版无需为所有查询额外调用一次 LLM。

### 9.3 排序边界与部分成功

默认跨库策略为 ConfiguredRerankElseRankFusion：配置且可用时统一重排，否则分配库配额后使用加权名次融合。RRF 参数与库权重存入策略修订；同分按库内 rank、kbId、chunkId 稳定排序。未配置库权重时等权。

不重叠知识库中各第一名可能均为 1/(k+1)，因此 fallback 只提供可解释排序，不保证全局语义优劣。Rerank 分数也不天然是概率；其阈值由具体模型配置，不能照搬余弦阈值。复合引用去重仅在同一 generation 内按身份执行，内容去重保留所有来源。

默认 FailurePolicy=AllowPartial：至少一个获准库成功执行可返回 200 与 partial=true；某库向量失败但 BM25 成功标 degraded；零命中与分支故障分别表达。所有库执行失败按主因返回 503/504；Strict 模式任一指定库失败即整体失败，已计算内容不作为完整成功返回。

上下文构建按实际目标 Chat 的 tokenizer/预算裁剪，显示哪些来源被截断。调用线上的 reranker 或 chat 可能发送文档内容，必须符合请求/知识库的出站策略；local_only 阻止所有远端嵌入、重排、生成及工具外发，不自动降级到云端。

## 10. API、交互与用户可观察流程

### 10.1 新 API 契约与协议边界

业务 API 统一新设计，不承诺旧路径：
| 端点 | 必要语义 |
| :--- | :--- |
| POST /api/knowledge-bases | 校验模型/spec/连接器，返回创建任务与配置修订 |
| POST /api/knowledge-bases/{id}/connectors | 新增来源并返回同步任务，检查 Kind 和访问能力 |
| POST /api/knowledge-bases/{id}/jobs | operation=reembed/rechunk/reingest/reparse/refreshVectors/refreshRetrievalView/reprojectGraph/sync，文档范围、幂等键、预期修订、返回 202 + jobId |
| GET /api/jobs/{id}；POST /api/jobs/{id}/cancel | 从持久化事实读取进度；取消可重复，完成后的取消不反转结果 |
| POST /api/search | 第 9 节的明确范围、失败策略、预算与分数契约 |
| POST /api/conversations；POST /api/conversations/{id}/turns | 保存模型与库选择；每轮配置快照；忙碌冲突明确返回 |
| GET /api/models；GET /api/model-runtimes | 管理配置与运行状态分开查询 |
| POST /api/models/{id}/operations | enable/disable/delete，revision、force 与依赖影响清单 |

Job 以操作幂等键去重；相同键但不同 payload 返回 409。取消与发布点竞争时以事务提交为准：提交成功的结果保留，未提交工作停止；UI 不把“已发取消请求”当作“已取消”。

保留 OpenAI 格式作为新系统的目标功能，而非旧实现兼容包袱：
- /v1/models 只列调用者可用的模型；管理 API 提供 capabilities。/v1/models/{id} 真正查注册表，不存在返回模型不存在错误。
- 内部 ModelId 使用不含斜杠的稳定 ID，RemoteModelName 单独保存，避免 provider/model 名称破坏路径路由。
- /v1/chat/completions、/v1/embeddings、重排接口均解析 model、验证能力、实际路由、回传执行身份。嵌入同时校验 dimensions/input 限制，格式化支持范围明确。
- 未给 model 可按用途默认配置处理；给了不存在/禁用/错误能力模型则明确报错，不能回显请求模型却执行另一个。
- reference-rag 如继续提供，应是独立 RAG 助手定义，知识库范围按服务端授权配置；不得混同普通模型名称。
- 原生会话支持完整 queue/loading 事件；OpenAI SSE 在首个内容前使用兼容注释心跳，标准 delta 不混入自定义进度对象。提交响应头后故障采用明确流内 error 并关闭，不能改 HTTP 状态或继续输出假成功。
- 客户端断开传播取消并释放资源；unsupported 参数显式报错或按文档忽略，不能宣称未实现的工具/视觉能力。

### 10.2 六个前台模块与后台闭环

| 模块 | 交互流程与必要反馈 |
| :--- | :--- |
| Settings / Providers | 添加提供商 → 保存凭据引用 → 按能力测试 → 导入模型；连通、鉴权、模型能力测试分开，/models 列表不能自动证明能力 |
| 编码路由与兼容管理 | Query 与 Document 分别选择端点，测试配方/交叉召回/历史 producer 覆盖，显示“可复用向量/需复验/需重编码”；仅改端点不清空数据 |
| Models / 下载与删除 | 展示资产、启用、健康、加载、排队状态；禁用/删除先显示影响，普通与强制操作文案区分，操作失败保留原因 |
| Sources / 知识库 | 向导选择库名 → 模型/spec → 一个或多个连接器 → 校验 → 创建任务；卡片显示绑定、可用检索分支、同步时间和失败数 |
| 更换嵌入模型 | 选择新 spec → 预检查分块/token/来源条件 → 明确重建方式与停用范围 → 提交 → 进度/取消/失败重试；提交前的选择不改变实际索引绑定 |
| Search / 调试 | 选择获准库 → 展示各库就绪能力 → 查询 → 显示部分失败/降级、排序策略、来源和耗时；不再用一个全局模型状态代表所有库 |
| Chat | 每会话保存模型与库集合；发送后依次 queue/loading/tool/generating，支持停止；换模型重新计算上下文与工具能力，历史不兼容时要求新会话 |
| Performance | 单模型冷/热测试与并发负载分开；同设备多模型默认顺序评测；远端 Token 未提供时标估算，未测显存/RTT 显示 Unknown |
| 解析与切片适配器管理 | 在线新增画像/规则，注册服务，后续安装 worker 包；预览来源结构/切片/token/图证据，校验后发布，再绑定并创建必要的重处理任务 |

“启用”仅表示允许选择；Downloaded/Validated、ProviderHealth、RuntimeState 分开显示。忙碌按钮防重复，异步任务断线重连后按 jobId 继续读取。前端推送只是刷新提示，不能成为任务状态唯一来源。

重建确认展示目标库、重建类型、不可查询的能力、是否需要重读源与可取消范围；首版不承诺在线无损切换。允许破坏性架构不等于隐藏用户运行中的破坏性操作。

### 10.3 必须同步重写的后台入口

- StartupSync / AutoIndex / IndexingPipeline / FileMonitor：显式绑定 kbId、connectorId、配置和任务代次。
- IndexController、IndexJobs、BM25Index、GraphController：重建/清理以目标库 manifest 和引用完整性判断，取消“只有当前全局模型有效”的清理逻辑。
- AIQuery、SearchService、ContextBuilder 与图谱扩展：授权范围、失败策略、复合引用一致传递。
- 文件浏览/读取/写入、Obsidian 引用、RagSearchTools、KnowledgeFileTools、聊天内工具：定位和权限不依赖唯一全局文件路径，远端引用使用来源适配器。
- OpenAI models/chat/embeddings/rerank 与 ApiHelp 示例：发现、错误、能力、执行结果一致。
- 模型下载变体、mmproj/Tokenizer 配套资产、Provider 删除：按实际资产依赖守卫，不能只检查模型名。
- HTTP、IPC 和 MCP 不各自保留一套默认模型/权限解释。

## 11. 安全、恢复与可运维性

不保留旧权限格式，但新权限必须从第一阶段生效。新模型、来源与任务的管理端点仅授权管理者调用。

Provider 凭据使用 CredentialRef，通过宿主 secret store/受保护配置解析；API 返回 hasCredential 与掩码，不将掩码回写为新密钥。平台加密方案与密钥备份分开定义，不能只写“AES-256”就声称安全落地。

Provider BaseUri 与 Web 连接器由明确授权的管理员配置；禁止客户端每请求任意指定地址。内网/本机端点可显式允许，但要校验 scheme、重定向及目的地址策略，避免自动抓取越界。请求日志不记录 Key 和默认完整文档内容。

知识库路径使用服务端生成的 opaque ID，校验路径位于 DataRoot，拒绝路径穿越和未授权 reparse point。删除只操作目标库目录；模型资产与外部来源属于不同资源，不能相互联动删除。

重启恢复规则：
| 遗留状态 | 恢复行为 |
| :--- | :--- |
| Model Loading/Ready | 进程内实例归零，重建账本，不信任持久化“已加载” |
| Job Running/Cancelling | 执行 Attempt 标 Interrupted，按发布事实对账后终结/重新 Queued；取消意图不丢失 |
| MaintenancePhase Quiescing/Building | 依据 Job/各能力 actual-target manifest 继续维护/重试，不自动改为全部 Ready |
| Provider/Model Draining | 继续已记录控制操作，确认零租约后完成 |
| KB Deleting | 安全核对目标路径并重试，未完成前不重新接入查询 |

观测关联字段：RequestId、TurnId、JobId、kbId、connectorId、generation、ModelId、RuntimeKey、DeviceId、ProviderId。指标包括队列深度/等待、加载与卸载、预留和观测内存、活跃租约、Token/批次、检索各分支、重排、降级与失败率。

搜索耗时报告 wall-clock 与各分支 span，不把并行时长相加当端到端延迟；TTFT 分离排队、加载和首个内容时间，不把模型生成时间称网络 RTT。远端不可观测内存不造数。

## 12. 性能评估与容量设计

不根据“物理隔离”推断吞吐提升。性能验收按设备、模型修订、上下文、语料规模与并发量分别记录。

至少覆盖以下维度：
| 场景 | 关注项 |
| :--- | :--- |
| 单库全文/向量/混合 | p50/p95/p99、过滤正确性、SQLite 锁等待、磁盘读取 |
| 多库同 spec / 异 spec | Query 编码次数、扇出耗时、Rerank token、召回质量 |
| 多本地模型交替 | 冷加载次数、抖动、队列公平性、显存峰值 |
| 索引与前台混合负载 | 前台尾延迟、后台进展、内存队列字节数 |
| 远端慢/429/断流 | deadline、重试次数、重复调用计费风险、部分结果 |
| 大文档/大量库 | 峰值 RAM、解析流式程度、打开连接数、分页与候选上限 |

原始 float32 向量有效载荷约 N × D × 4 字节；10 万 × 1024 约 391 MiB，仅是向量值，不含索引、数据库、文本与缓存。不得据此估算总内存。SQLite 向量后端的检索算法与复杂过滤能力须以锁定版本为准，不默认存在 HNSW。

质量使用固定、版本化查询集测 Recall@K、MRR/nDCG 与来源正确性，报告单库和跨库分组结果。性能比较保持相同输入、输出预算及冷热状态；不同 tokenizer 的 TPS 与不同远端服务指标不能无条件横比。

只有测得本地后端不能达到已设目标，才推进外部 Qdrant/推理服务或 ANN 专项；不提前把分布式基础设施变成首版依赖。

## 13. 统一实施计划与技术验收门

实施顺序唯一采用[统一契约与实施计划第 6 节](unified-architecture-contracts-and-implementation-plan.md)：G0 技术与契约 → G1 持久化文本闭环 → G2 单端点嵌入与预算；G3 内容适配管理与 G4 兼容双路由在 G2 后推进，依赖满足后交付 G5 多库/会话、G6 多格式/L1、G7 全协议、G8 可选受信任代码包，最后 G9 发布质量与容量证明。

旧 P0–P5、专题 A0–A5/B0–B4 仅作工作包归属索引，不再代表独立排期。每个功能门同步交付最小管理/API/界面、授权、取消/恢复与行为证据，不能把 A1 的发布/预览或 B2 的准入管理统一拖至“后期 UI 阶段”。

G0 需验证锁定后端的同库事务/回滚/读取快照/备份、原生加载释放与 Busy、工具阶段租约释放、IR/来源与 token 约束、当前 M3 编码差异。G2 正式引入隔离样本的初始配对准入，空库不能靠空集合覆盖跳过认证。具体退出条件、依赖 DAG 和跨层 T01–T14 场景全部以统一计划为准；本轮没有执行任何实施门。

### 13.1 最小行为验收矩阵

| 行为 | 验收证据 |
| :--- | :--- |
| 两个连接器包含同名文件 | 两份身份与内容独立，删其中一个不删另一个 |
| 扫描中途失败/来源暂不可达 | 不触发整源删除，任务记录错误并可重试 |
| 同模型名但 spec 改变 | 拒绝混用旧向量，触发相应重建状态 |
| Reembed 中搜索 | 全文可用、向量明确降级；完成校验后再使用新向量 |
| Rechunk 时旧任务提交 | generation/fence 拒绝，不能复活旧 chunks |
| 存储淘汰/库删除遇在途请求 | 排空或明确 Busy，无连接已释放异常 |
| 两次并发请求同冷模型 | 一次加载，等待者取消不误杀他人请求 |
| 等待/加载/推理中断开 | 取消传递、槽位和预留归还；无人使用的加载最终可回收 |
| 跨库一库超时 | AllowPartial 返回可解释结果；Strict 明确失败 |
| Unauthorized/空权限集合 | 不访问任何未授权候选，工具和引用读取相同 |
| 本地模型不可用 | 不自动外发；显式模型不被悄悄替换 |
| 删除 Provider/资产变体 | 依赖与在途任务都被纳入，不能绕过模型守卫 |
| 作业中途进程重启 | 已提交内容不重复、未完成任务可识别与恢复 |
| 新旧模型交替压力 | 有界等待、无长期饿死、无后台无限队列 |
| OpenAI model 字段 | 发现/单项查询/能力校验/实际执行/返回身份一致 |

优先扩展现有 ModelLifecycleStateMachineTests、GpuMemoryManagerTests、FileIndexPipelineTests、SqliteVectorStoreTests、Fts5BM25StoreTests、MafChatServiceTests 和 OpenAICompatControllersTests。新调度和连接器用可控时间/假适配器验证竞态，再用最小真实 GPU/HTTP 路径验证原生行为；mock 不证明显存释放。

文档阶段不运行完整业务构建、不伪造这些验收结果。实施完成后按阶段跑对应测试与构建，发现无关失败独立记录。

### 13.2 未来部署与清理规则

新架构使用明确的新 schema version；发现旧 DataRoot 时返回“不支持旧格式，请选择重建或新目录”，不自动转换，也不自动删除。发布说明列出旧配置/API/索引会失效，给出新建配置和重新索引步骤。

若用户未来选择删除旧索引，需明确目标、预览、停止占用后执行；只清理派生数据库，不触碰外部来源及未授权模型目录。本次只授权文档重构，未执行这些操作。

## 14. 已确定决策、后续扩展与剩余不确定性

本版建议作为实施默认：显式知识库上下文；单写进程；每库单 SQLite 文件（G0 验证门）；Catalog 单一配置权威；不可变嵌入 spec；本地预算与远端限流分离；破坏性重建允许目标库停用；聊天分阶段租约；有界跨库检索与明确部分成功。

可自然扩展但不提前实现：
- 新 ConnectorKind、模型 Provider/协议、排序策略通过对应适配器/策略注册；新物理格式 Parser、内容画像、Chunker 与 GraphProjector 按第 5.4 节专题独立版本化。
- 新存储后端实现相同身份、代次与提交契约；分布式时重新设计事务/outbox，不能假设 SQLite 事务跨服务生效。
- 需要不停机换模型时再引入双代次影子索引和切换；这是功能升级，不是历史格式兼容层。
- 更复杂定时接入可用 Quartz；状态图复杂到维护成本明显上升时再评估 Stateless。
- 多模态扩展增加内容类型与能力契约，mmproj 作为资产依赖；不把文本 SourceDocument 强行当作所有模态终局。

仍需实测：锁定 sqlite-vec 过滤/事务/容量；不同设备内存探测与加载峰值；LLama/ONNX 具体实例的并发/释放；工具消息恢复；远端 Token/错误/协议差异。明确承认这些不确定性，避免承诺“完全消除 OOM”“全部源码直接复用”或“固定若干天上线”。

## 15. 官方参考资料

调研日期均为 2026-10-31；在线资料描述当前上游，不等于仓库锁定包已具备相同 API。只借鉴明确能力，不据此承诺本项目性能。

- [S1 Dify Classic Knowledge Retrieval](https://docs.dify.ai/en/cloud/use-dify/nodes/knowledge-retrieval)：多库召回、库级与节点级检索配置。
- [S2 LlamaIndex IngestionPipeline](https://developers.llamaindex.ai/python/framework/module_guides/loading/ingestion_pipeline/)：转换缓存、文档 ID 与内容哈希管理。
- [S3 Ollama FAQ](https://docs.ollama.com/faq)：并发、驻留、队列与 keep_alive。
- [S4 llama-swap 官方配置](https://github.com/mostlygeek/llama-swap/blob/main/docs/config.example.yaml)：TTL、资源组、容量规则和并发。
- [S5 vLLM Optimization and Tuning](https://docs.vllm.ai/en/latest/configuration/optimization/)：KV 缓存压力、抢占与并发调优。
- [S6 Qdrant Collections](https://qdrant.tech/documentation/manage-data/collections/)：向量空间与命名向量维度/度量。
- [S7 Elasticsearch RRF](https://www.elastic.co/docs/reference/elasticsearch/rest-apis/reciprocal-rank-fusion)：名次融合公式。
- [S8 Microsoft.Extensions.AI](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai)：标准聊天与嵌入抽象。
- [S9 SQLite Vector Store connector（Preview）](https://learn.microsoft.com/en-us/semantic-kernel/concepts/vector-store-connectors/out-of-the-box-connectors/sqlite-connector)：当前包名、过滤和 HybridSearch 能力边界。
- [S10 .NET Channels](https://learn.microsoft.com/en-us/dotnet/core/extensions/channels)：有界通道与背压。
- [S11 .NET HTTP Resilience](https://learn.microsoft.com/en-us/dotnet/core/resilience/http-resilience)：超时、重试、熔断和非安全方法重试控制。
- [S12 Stateless 官方 README](https://github.com/dotnet-state-machine/stateless)：状态迁移、异步触发与并发限制。
- [S13 Microsoft.Data.Sqlite Async limitations](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async)：异步方法同步执行。
- [S14 ASP.NET Core Rate Limiting](https://learn.microsoft.com/en-us/aspnet/core/performance/rate-limit?view=aspnetcore-10.0)：入口限流。
- [S15 Quartz.NET Clustering](https://www.quartz-scheduler.net/documentation/quartz-4.x/tutorial/advanced-enterprise-features.html)：持久化调度与集群条件，首版不引入。
- [S16 Microsoft.Extensions.AI Routing and Failover](https://devblogs.microsoft.com/dotnet/routing-and-failover-for-microsoft-extensions-ai/)：实验路由/回退类型，仅供评估。
- [S17 SQLite Backup API](https://www.sqlite.org/backup.html)：一致数据库备份机制。
- [S18 SQLite FTS5](https://www.sqlite.org/fts5.html)：全文表、分词、BM25 与维护。
- [S19 sqlite-vec 官方文档](https://alexgarcia.xyz/sqlite-vec/)：向量扩展与版本入口，具体能力以锁定原生版本验证。
