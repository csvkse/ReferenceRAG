# 架构统一契约、审核结论与分阶段实施计划

> 版本：v1.0；统一审核日期：2026-10-31（Asia/Hong_Kong）。
> 状态：文档设计与统一审核完成，所有实施门均尚未执行；本轮仅修改 docs。
> 适用：总体知识库/模型规范、解析与适配器专题、嵌入兼容与双路由专题。允许破坏性重构，不承担历史配置/API/索引兼容。
> 本文是跨专题核心契约和实施顺序的唯一权威；算法、来源调研与领域细节仍在专题维护。出现冲突时先修订文档，不让实施者从多个阶段表自行选择。

## 1. 文档职责与统一审核范围

| 文档 | 负责内容 | 不再独立定义 |
| :--- | :--- | :--- |
| [总体规范](per-source-data-isolation-and-multi-model-dynamic-lifecycle-architecture.md) | 领域边界、存储/模型/搜索编排、全入口与运维 | 全局版本/状态枚举与另一套阶段顺序 |
| [解析与适配器专题](document-parsing-content-profiles-and-adapter-management-architecture.md) | IR、内容画像、解析/切片/图投影、动态扩展 | 另一套 JobStatus、端点路由语义与交付顺序 |
| [编码兼容专题](embedding-compatibility-and-query-document-routing-architecture.md) | 逻辑编码契约、角色路由、验证与 cohort 准入 | 与正式数据不一致的 cohort 权威、另一套缓存/实施规则 |
| 本文 | 名词/版本/状态权威、操作影响、跨层并发不变量、唯一实施门 | 具体第三方库 API 或未经实测的性能收益 |
| [原自适应分段设计](adaptive-chunking-architecture-and-embedding-optimization-design.md) | 既有算法背景 | 新架构契约、兼容承诺、收益保证与排期 |

本轮不扩展到工作区并行修改的授权/MCP 业务实现；这里定义的新架构授权要求仍需贯穿所有入口，相关实现变化不由本文宣称完成。

### 1.1 已发现并处理的冲突/遗漏

| 编号 | 问题 | 统一决策 |
| :--- | :--- | :--- |
| U01 | 同 cohort 普通更新不改 generation，结果缓存可能永久陈旧 | 增加 ReadRevision，随每次可查询事实发布同事务递增 |
| U02 | 空库没有 cohort，Query/Document 互相等待认证或空集合绕过 | 固定参考配方与隔离样本的初始配对准入，再激活首对路由 |
| U03 | JobStatus、Parsing 阶段、Ready/NeedsRebuild、Interrupted 混用 | 分离 JobStatus、OperationPhase、CapabilityManifest 与 AttemptStatus |
| U04 | 实际 cohort 集合依赖最新值，但未规定谁维护 | kb.db 维护正式向量引用数/集合/修订，随发布与删除同事务更新 |
| U05 | ProcessingPlan 固定模型与实现，但允许 Document 批次换端点 | 固定语义处理计划，独立记录每次执行 AttemptEnvelope |
| U06 | 全库重建、局部重处理、派生视图刷新版本边界不一致 | 统一操作/版本影响矩阵，禁止在不同专题重新解释 generation |
| U07 | Catalog 配置成功不等于 kb.db 已应用 | Desired 与 Effective 分离，以操作日志和 LastAppliedOperation 对账 |
| U08 | 首版接入的 A1/B1 依赖后置管理页/API，P/A/B 多套顺序冲突 | 单一 G0–G9 实施门；每个纵向功能同步交付最小 API/界面与恢复 |
| U09 | Job/查询仍可能长时间持有存储借用等待模型，阻碍维护排空 | 模型/网络等待前释放连接租约，用版本令牌与短读写快照重检 |
| U10 | 只图更新、只全文更新与可选阶段失败的完成语义不统一 | 各输出独立修订/能力状态，父任务只有满足全部必需输出才成功 |
| U11 | 发布后推送失败、取消竞争、扫描删除遗漏了持久化事实优先级 | 已提交事实为准，事件/进度可对账，取消不反转提交 |
| U12 | 在途旧 Query 可遇见只兼容新 Query 的新 cohort | 编码后读栅栏绑定准入集合与真实读取快照，失败有界重新规划 |

这份清单是设计问题的处置记录，不是已运行竞态测试或性能测试的结果。

## 2. 持久化权威、最小快照与契约归属

### 2.1 唯一权威矩阵

| 数据 | 权威存储/所有者 | 规则 |
| :--- | :--- | :--- |
| KB/Connector/Provider/Model/Adapter 定义、画像和逻辑 EmbeddingSpec | Catalog | 不可变定义修订；名称/描述不充当数据身份 |
| 期望 KB 绑定、管理命令、路由与准入、作业/Attempt 元数据 | Catalog | DesiredRevision、ExpectedRevision CAS、OperationId；不伪装成已建好索引 |
| 已应用的内容/处理绑定、manifest、文档发布指针 | kb.db | EffectiveConfigRevision、IndexEpoch、LastAppliedOperation；查询只消费已应用事实 |
| 正式向量、活跃 cohort 引用数、集合修订与 ReadRevision | kb.db | 同一发布/删除/失效事务维护；Catalog 可以缓存，但不是第二权威 |
| IR/原始快照/staging 的内容 | 受控 artifact 存储及 kb.db manifest | 校验哈希/权限/配额；只有正式指针引用的版本进入读取 |
| 模型实例、租约、设备预算、worker 运行状态 | 当前单写进程/资源调度器 | 重启清零并重新探测，不信任持久化的“仍在显存” |
| UI 推送与进度缓存 | 派生投影 | 断线、丢事件或广播失败不改变正式结果，按 jobId/manifest 重取 |

动态配置的“Catalog 唯一来源”特指定义和期望值；索引实际应用状态由 kb.db 证明。多文件之间没有跨文件 ACID。

### 2.2 配置应用与对账

1. 管理命令校验定义、能力、权限、依赖及 ExpectedRevision，在 Catalog 写 DesiredRevision + OperationId。
2. 应用 worker 在库级控制序列内检查当前 Effective 状态，以短 kb.db 事务应用栅栏/绑定/维护状态，写 LastAppliedOperation 和结果摘要。
3. Catalog 最终确认 Applied/Failed；崩溃后先读 kb.db 的 LastAppliedOperation/发布事实，再补状态，不能因 Catalog 仍 Running 就重复破坏。
4. 纯端点路由是 Catalog 控制面更新，通过相同控制门与 cohort 读写协调；不改变 kb.db 内容代次。不能因操作日志不同而给路由偷偷分配 IndexEpoch。
5. 一次不相关配置成功不能覆盖其他操作结果；同库维护串行，失败后重试同一 OperationId。另一个语义不同的命令使用新 ID。
6. 不可变定义修订的“激活”只是可被选择；对某库生效需绑定命令。查询与生产必须从该库 Effective 绑定构建快照。

### 2.3 ProcessingPlan 与执行实现分离

ProcessingPlanHash 固定：Source/IR 身份、解析/画像/切块/渲染/图投影修订、逻辑 EmbeddingSpec/角色输入配方、tokenizer 语义、允许输入域与预算、输出要求和允许执行实现的约束。实际 Provider/地址/设备选择不成为“只换端点就全量失效”的内容指纹。

每次模型调用另持久化 EmbeddingAttemptEnvelope：JobId/AttemptId/批次输入 ID 与视图哈希、RouteRevision、实际 EndpointRevision、EncodingImplementationRevision、角色、AdmissionId/RevocationRevision、producer cohort、输出摘要。批次可按有效新路由换实现，旧批次仍有真实来源；不能改写既有 Attempt 冒充新实现。

Parser/OCR/图抽取模型若改变输出语义或产物，属于相应处理阶段指纹；不能借“只是换 Provider”绕过解析/抽取版本检查。只有经过准入、满足同一输入配方与预算的嵌入实现替代走免重建路径。

连接/模型/worker 借用均由所有者管理。索引在模型等待前只持有版本令牌和有界输入，不持有数据库连接/维护阻塞租约；计算后重新获取短写借用并校验栅栏。读取先编码再获取短快照。连接池租约与“维护操作所有权令牌”是不同资源，后者不能反向阻塞操作自身排空连接。

## 3. 统一版本词典与操作影响

### 3.1 字段与单调性

| 字段 | 粒度/含义 | 何时变更 |
| :--- | :--- | :--- |
| DefinitionRevision / DesiredConfigRevision | 定义/期望配置的不可变修订 | 发布新定义/管理目标，不代表数据已更新 |
| EffectiveConfigRevision | 某库已应用的索引/处理绑定 | kb.db 完成对应控制操作时 |
| IndexEpoch | 某库内容写入栅栏 | 索引语义绑定/维护失效生效时；纯端点路由不变 |
| ContentGeneration | 整库内容/分块代次 | 全库 Rechunk/Reingest 的失效步骤；取消不回减 |
| DocumentVersion | 每文档已发布的证据/结构/分块版本 | 普通内容更新、局部 Reparse/Rechunk 正式发布；不因只换向量端点改变 |
| ParsedArtifactRevision | 解析产物身份/修订 | parser/输入改变产生新 IR；staging 不意味着正式内容版本已发布 |
| VectorGeneration | 每库正式向量空间/整库向量重建代次 | 全库 Reembed/全库重切片等导致全部向量失效的操作 |
| VectorArtifactRevision | 每文档活动向量产物版本 | 同空间 RefreshVectors 或文档新向量发布 |
| RetrievalViewRevision | 每文档派生全文/展示版本 | RefreshRetrievalView 正式发布 |
| GraphArtifactRevision | 每文档图投影产物版本 | ReprojectGraph/文档结构图正式发布；GraphProjectionSpecRevision 另指配置 |
| CorpusCohortSetRevision | 某库某正式 VectorGeneration 的活动生产者集合版本 | 正式成员增删；同 cohort 内数量变化不必递增此字段 |
| ReadRevision | 每库可查询事实单调版本 | 任意正式内容/向量/FTS/图/删除/能力失效/发布事务 |
| QueryRouteRevision / DocumentRouteRevision | 用途路由版本 | 经校验替换主/备用端点或策略 |
| ProducerAdmissionEpoch / RevocationRevision | 生产者准入栅栏/撤销版本 | 控制面准入收紧、撤销、生产者切换；不得伪装内容代次 |
| AttemptId | 一次执行尝试身份 | 重试、恢复重新调用；不是正式索引版本 |

C# 契约中 ExpectedGeneration/StorageLease.Generation 统一改为 ExpectedIndexEpoch/IndexEpochSnapshot，避免与 ContentGeneration 混淆；不承担旧接口兼容。通用 ArtifactRevision 必须在存储 schema/DTO 中指明所属产物，不能作为所有阶段共用的计数器。

正式引用 = (kbId, ContentGeneration, DocumentId, DocumentVersion, chunkId/SourceAnchor)。Reembed、图/向量/全文刷新不改变直接证据引用；局部证据改变和整库换代使相应旧引用失效。历史引用不可自动映射到同名新块。

### 3.2 操作影响矩阵

| 操作 | IndexEpoch | 内容/文档身份 | 向量/图/全文 | 读取与缓存 |
| :--- | :--- | :--- | :--- | :--- |
| 新增/更新/删除单文档 | 不因普通数据变化递增 | 更新递增 DocumentVersion；删除发布 tombstone | 同事务切换可用输出/移除失效输出及 cohort 引用 | ReadRevision 递增 |
| 全库 Reingest/Rechunk | 失效时递增 | ContentGeneration 递增，重做文档输出 | 全部分块派生索引失效，VectorGeneration 递增 | 目标库暂停相应能力，ReadRevision 递增 |
| 局部 Reparse/Rechunk | 仅绑定/维护栅栏改变时递增 | 正式发布时递增目标 DocumentVersion | 重建受影响输出，其他文档保留 | 发布时 ReadRevision 递增 |
| 全库 Reembed | 失效时递增 | 保留 ContentGeneration/DocumentVersion | TargetSpec/VectorGeneration 新建，旧向量不可查询 | 文本/结构图可用；失效/最终发布均改 ReadRevision |
| 同空间 RefreshVectors | 无新增语义绑定时不变 | 保留证据版本 | 只改目标 VectorArtifactRevision/cohort 引用 | ReadRevision 递增 |
| RefreshRetrievalView | 同上 | 保留证据版本 | 更新 FTS/派生展示与 RetrievalViewRevision | ReadRevision 递增 |
| ReprojectGraph | 同上 | 保留证据版本 | 更新 GraphArtifactRevision/证据映射，不重算正文向量 | ReadRevision 递增 |
| 已认证 Query/Document 端点替换 | 不变 | 不变 | 旧数据不改；未来批次记录实际 producer | RouteRevision 改变；结果缓存重新校验路由 |
| 撤销准入/权限 | 不自动删除数据 | 不变 | 收紧允许查询/生产的路径 | 撤销/权限版本变化即禁止旧缓存绕过 |
| 只改显示名/纯 UI 样式 | 不变 | 不变 | 不重处理内容；如果显示名进入可查询正文则不属于纯显示 | 按是否为结果字段更新响应/元数据缓存 |

所有实际改变可查询事实的分支都维护 ReadRevision；表中“同上”不豁免该义务。绑定控制操作已推进 IndexEpoch 时，任务使用新的已应用值，不在每批次重复递增。删除后重新接入同一 SourceKey 需要新发布版本/身份规则，不能复活已清理旧内容引用。

### 3.3 结果缓存与读一致性

Query 向量缓存继续按实际 QueryEncodingRevision/输入/角色配方隔离，不因纯语料更新失效。检索结果缓存必须另含：规范化请求（范围、过滤、模式、TopK 等）、授权范围与授权修订、每库 Content/VectorGeneration + ReadRevision + cohort-set、排序/投影配置及实际路由/准入撤销版本。不能只靠 generation 或 TTL。

读取缓存前先按当前可信身份重新授权，并校验当前控制版本；无权访问时不能从缓存返回来源内容。单写进程可用只读 manifest 镜像取得 token：发布在控制门内完成库事务并更新镜像后才开放新读；镜像更新失败标记 Dirty，后续读绕过缓存直接核对数据库，不能继续信任旧 token。重启清空进程缓存并从数据库恢复；跨库缓存用逐库 token 向量，不假设共享一个 ReadRevision。

读栅栏保持编码专题规则：先在事务外完成编码，短控制门内验证真实 Q 对该库当前活动 cohort 的覆盖并建立读取快照，门外只读取此快照。新 producer 与撤销不能插进校验/快照之间。过时 Q 有界重规划，不能静默忽略新 cohort。图扩展和引用读取继续核对授权/文档版本，不消费与召回版本无关的新图。

## 4. 空库启动、cohort 事务与整库向量重建

### 4.1 空库 bootstrap

空库可先提供全文/结构图；向量能力是 NotConfigured/Pending，不要求先有模型才能创建文本库。

正式启动首个向量空间前：
1. 固定逻辑 EmbeddingSpec、参考实现/角色配方、候选 Q_ref/D_ref、验证配置与输入域。
2. 在隔离 ValidationJob 中使用合成/获准样本建立参考文档向量和已标注查询，对候选实现做结构、配方、重复性与检索验证。即使 Q/D 为同端点也不能省略参考合规检查。
3. 通过后创建 ReferenceAdmission + 初始候选 cohort 定义，在控制门内激活配对路由/目标空间。没有正式 cohort 仅意味着不需要覆盖历史成员，不意味着认证自动通过。
4. 第一批正式向量发布时将 producer 纳入 kb.db 活动集合，并复核全部正式 Query 主/备用覆盖；没有备用时也必须有获准主 Query。
5. 不依赖旧生产索引或完整私有全库作为首次认证前置。样例通过仍有数据域边界，不构成任意未来输入无损保证。

上游元数据不明、返回模拟向量或参考配方错误时保持 VectorUnavailable；不能先污染正式库再把其结果当“参考基线”。

### 4.2 活动 cohort 集合

Catalog 维护候选 producer 定义和准入，不维护实际向量计数。kb.db 的 PublishedVectorManifest 保存每个正式向量的 producer cohort，并维护 ActiveVectorCount 与 CorpusCohortSetRevision：

- 只有正式发布指针指向、当前 VectorGeneration 可见且未失效的向量贡献引用数；staging、候选报告和历史失效向量不计入。
- 首次有效向量加入 cohort、最后一个引用删除/替换/失效时，集合成员与集合修订在同一 kb.db 事务更新；每次发布也更新 ReadRevision。
- 原 cohort 被新 cohort 替换时先计算净引用差异，再提交指针/计数，不能留下瞬时错误集合；Query 看到的是事务后集合或事务前快照。
- 准入撤销不等于数据消失，不能把仍有活动向量的 cohort 从集合删除来绕过覆盖检查。没有合规 Q 时整体向量分支降级/失败。
- 重启核对活动发布指针/计数，可在维护状态按正式向量重建派生计数；对账未完成不能宣称覆盖完整。Catalog 投影过期不影响数据真相。

控制门不执行模型/网络等待。写入在计算后取得短发布门与存储借用，校验 IndexEpoch、ExpectedDocumentVersion、plan/input 摘要及当前准入，再发布。纯路由变更允许旧有效批次提交，但撤销和新 cohort 不可绕过现行覆盖规则。

### 4.3 全库 Reembed 的实际状态

预检先验证目标 spec/Query+Document 配对、输入 token 和资源条件，再进入破坏步骤。失效步骤记录 TargetEmbeddingSpecHash/TargetVectorGeneration，关闭旧向量可见性并推进栅栏；文本和结构图仍可用，目标向量先写 staging。

最终校验所有必需文档/输入版本、目标 producer 集合和目标 Query 路由后，短控制门+事务发布 ActiveEmbeddingSpecHash/VectorGeneration/可见指针/集合/ReadRevision。此时才恢复向量能力；Catalog Desired 成功不能提前替代 Active。

取消/失败发生在失效前，保留原能力；发生在失效后，保留 Target 状态和可验证检查点，向量能力 NeedsRebuild，不回减代次、不自动复活旧空间。重试同一目标可接续已验证 staging；新目标使用新 OperationId。这是破坏式重建，不要求旧空间在线或保留软件历史兼容层。

## 5. 统一状态机与恢复语义

### 5.1 状态维度必须拆开

| 状态维度 | 统一取值/含义 |
| :--- | :--- |
| JobStatus | Queued、Running、WaitingForInput、Cancelling、Succeeded、PartiallySucceeded、Failed、Cancelled |
| OperationPhase | 按操作选择 Probing/Parsing/Profiling/Planning/Chunking/Rendering/Embedding/Projecting/Validating/Publishing 等，不作为 JobStatus |
| MaintenancePhase | Quiescing/Invalidating/Building/Validating/Publishing；归属于该 Job 的 OperationPhase，不新增第二套任务终态 |
| AttemptStatus | Created/Running/Succeeded/Failed/Interrupted/Cancelled；重启中断记录在 Attempt |
| KnowledgeBaseLifecycle | Provisioning/Active/Deleting/Deleted/Faulted；Active 不表示所有检索能力就绪 |
| CapabilityManifest | Text/Vector/Graph 各自 NotConfigured/Building/Ready/Pending/Unavailable/NeedsRebuild，含原因和 actual/target 修订 |
| ModelRuntimeState | Unloaded/Loading/Ready/Draining/Unloading/Faulted，另有 ActiveLeases/推理槽位 |
| AdapterRevisionState | Draft/Validating/Validated/ValidationFailed/Active/Draining/DrainFailed/Retired；Health 独立 |
| AdmissionStatus | Declared/Verifying/Validated/Active/Suspended/Expired/Revoked；验证结论/适用域与状态独立 |
| ConnectorState | Disabled/Idle/Scanning/Watching/Backoff/Faulted；由其同步 Job 记录进度 |

Ready、NeedsRebuild 是能力状态；Completed 是 parser/service 流结束事件，不能写成 JobStatus。Paused 的业务语义统一使用 WaitingForInput + WaitReason（例如 ClassificationReview/DependencyUnavailable），同时释放模型/worker/数据库租约。定时重试是 Queued + NextAttemptAt，不挂着 Running 无限等待。

### 5.2 合法迁移与阶段要求

~~~mermaid
stateDiagram-v2
    [*] --> Queued
    Queued --> Running
    Queued --> Cancelled
    Running --> WaitingForInput
    WaitingForInput --> Queued
    WaitingForInput --> Cancelled
    Running --> Cancelling
    Running --> Succeeded
    Running --> PartiallySucceeded
    Running --> Failed
    Cancelling --> Cancelled
    Cancelling --> Succeeded
    Cancelling --> PartiallySucceeded
    Cancelling --> Failed
~~~

取消与发布竞争：短事务先提交的正式数据保留；控制序列以一次终态 CAS 记录先后。所有目标及声明输出已在取消生效前结算，则按正常结果 Succeeded/PartiallySucceeded 终结；取消生效时尚有未结算工作，默认终态为 Cancelled，即使已经部分发布也只在结果清单报告 publishedCount，不再自由选择 PartiallySucceeded。不可恢复的执行故障可终结 Failed，保存相应事件次序与原因。终态不可反转，后续重试按新 Attempt/Job 规则执行。Queued/WaitingForInput 取消无在途工作，非法迁移拒绝并记录诊断。

OperationPhase 的正常文本向量路径为 Probing → Parsing → Profiling → Planning → Chunking → Rendering → Embedding/Projecting → Validating → Publishing。Embedding 和 Projecting 可在 ChunkMap 确定后独立调度，但发布必须等待所声明的必需输出。只刷新向量、全文或图时只执行对应阶段，不伪造解析阶段。任何跳过都有 SkipReason。

job RequiredOutputs 与 OptionalOutputs 启动时固定：
- 所有目标的必需输出发布成功且没有遗留工作才 Succeeded。
- 已发布可用文本、但可选向量/图失败为 PartiallySucceeded，能力分别 Pending，并记录补偿 JobId 或明确人工重试动作。
- 严格画像的必需图失败，未发布则 Failed；多个文档已完成一部分时 PartiallySucceeded，不能把失败图冒充空图完成。
- 子任务未结束时父任务不能宣称整体成功；自动后台补偿必须持久化，不能仅在内存 Task.Run。
- Progress 指明单位与范围（document/block/vector），未知总数只显示已提交数；不混用“已编码”与“已发布”。

### 5.3 重启、排空与清理

遗留运行 Attempt → Interrupted；先依据 kb.db 的 OperationId/发布清单/检查点对账，再将 Job 终结或 Queued 等待恢复，不把 Interrupted 当作无上下文的任务终态。若原 Job 已处于 Cancelling，则恢复取消意图，不自动续跑。

恢复必须验证有效 spec/plan、源输入摘要、IndexEpoch/文档版本、准入和依赖；不满足时重新规划新 Attempt 或 WaitingForInput/Failed，不盲目重放。已发布结果幂等复用，未完成 stage 流没有 Completed 则不发布。

维护/删除排空顺序为阻止新对应借用 → 停生产者/取消或等待任务 → 排空模型/存储/worker → 关闭实际句柄 → 执行明确目标操作。超时返回 Busy/DrainFailed，不记已释放、不跨过引用计数强删。后台任务和作业暂停不长期占用连接租约。

产物 GC 仅删除超过保留策略且没有正式发布指针、读快照、活动任务或可恢复 Attempt 引用的产物。单库删除与全局模型包/外部源文件分开；worker 包删除检查其运行/任务依赖。相应删除为未来实施操作，本次未执行。

## 6. 唯一分阶段实施计划

编号 G0–G9 是实施门，不承诺固定天数。每门含契约/schema、应用服务、最小 API/界面、权限、错误/取消/恢复与验证证据；这些不是可以统一拖到最后的工作。

### 6.1 依赖图

~~~mermaid
flowchart LR
    G0["G0 技术门与契约"] --> G1["G1 持久化文本闭环"]
    G1 --> G2["G2 单端点嵌入与预算"]
    G2 --> G3["G3 L0内容适配管理"]
    G2 --> G4["G4 兼容双路由"]
    G3 --> G5["G5 多库与会话"]
    G4 --> G5
    G3 --> G6["G6 Office/PDF与L1服务"]
    G2 --> G6
    G5 --> G7["G7 全协议入口"]
    G6 --> G7
    G6 --> G8["G8 受信任代码包"]
    G7 --> G9["G9 发布质量与容量"]
    G8 --> G9
~~~

G3/G4 在 G2 后可独立推进；G6 不必等待全部多库功能，按示图依赖实施。G8 是独立扩展发布门；不承诺动态代码包的基础版可在 G7 后进入 G9，必须声明未交付 L2。若产品要宣称本轮全部规划已实现，则 G8 不可跳过。

### 6.2 实施包、验收与放行条件

| 门 | 必须交付 | 放行证据 |
| :--- | :--- | :--- |
| G0：验证与契约定稿 | 版本/状态 DTO、快照/事务 spike、IR/来源锚点、当前 M3 配方差异、工具阶段资源释放、能力矩阵 | 固定平台/模型/库版本；sqlite-vec/FTS/graph 回滚与快照可行；参考输入不会静默截断；失败先修订设计 |
| G1：单库文本与任务 | Catalog/kb.db、单写宿主、权限、ReadRevision/文档发布、Folder/SingleFile、通用文本/Markdown/结构图、Job/Attempt、最小建库/同步/检索/引用/取消 API 与任务界面 | 多来源同名文件隔离；失败扫描不删除整源；幂等发布/删除/缓存失效；重启恢复；无需模型也能全文检索 |
| G2：单端点双角色与预算 | 逻辑 spec/编码溯源、初始参考准入、Query/Document 同端点、设备/Provider 预算与租约、向量提交/cohort 事务、Reembed | 从空库启动首对路由；实际嵌入与召回闭环；模拟/错误配方被拒绝；预算/取消/重建恢复与向量缓存正确 |
| G3：L0 内容适配管理 | TXT/CSV、小说/合同/表格/通用画像、区域策略、不可变 Registry/Plan、预览/发布/绑定 API 与管理页、局部 Reparse/Rechunk/各视图刷新 | 无需改代码增加 TXT 规则；同扩展不同内容/混合附件正确处理；token/证据覆盖、旧任务栅栏和局部缓存失效通过 |
| G4：兼容切换与混合生产者 | 候选端点/报告/准入、双路由管理 UI/API、混合 cohort、Query 读栅栏、ProducerAdmissionEpoch/撤销与批次快照 | 免重建 Query 切换；后台线上/检索本地；全部历史和备用路径覆盖；旧 Q/新 cohort、迟到响应与撤销缓存竞态通过 |
| G5：跨库与多会话 | 授权后 fan-out、候选/重排预算、部分成功、父/邻居有界取回、会话模型/库集合与工具阶段释放，最小 Chat/Search 界面 | 异构空间不比分；单库失败不吞成功；低显存工具调用无租约环；并发会话与引用版本正确 |
| G6：多格式与 L1 | 分别验证 DOCX/XLSX/文本 PDF；至少一个 OCR/外部 parser 服务；能力握手、固定版本、worker 配额、预览坐标与断流恢复 | 编号/修订/宽表/扫描件有真实样本；运行中注册新服务完成解析→切片→索引；未知格式不假空成功 |
| G7：全入口与领域扩展 | HTTP/IPC/MCP/OpenAI models/chat/embeddings/rerank、后台清理/启动同步、ApiHelp 与一致授权；已选领域图 schema/证据与质量集 | 全入口走同一编排/准入；列表/选择/执行一致；旧全局模型清理逻辑不可达；未选 TableQuery/高级图功能明确标后续 |
| G8：L2 受信任代码包 | 代码包 manifest/依赖/哈希验证、独立 worker 安装自检/激活/排空、导入管理与回收 | 新测试格式包无需主进程加载代码，完成真实检索引用；不兼容协议/失效包不能被激活 |
| G9：发布验收 | 故障注入、备份恢复、资源/质量基线、硬件容量与发布说明、明确交付能力档位 | 所有宣称能力的门与跨层测试通过；列出未实测/不支持项；不以进程启动或生成日志代替行为证明 |

G0 的 spike 是隔离技术证据，不直接作为正式数据或生产认证。G1 早期只有通用结构图；G7 领域人物/义务关系是独立功能，不倒置为全文索引的前置。

每门必须给出“输入条件 → 行为 → 可观察结果 → 测试/日志证据”，并维护 AllowedCapability 标志。尚未重写的旧全局入口应关闭或明确拒绝，不能在后续入口适配前继续旁路写入新存储。

### 6.3 原 P/A/B 工作包的归属

旧编号仅用于定位原专题工作，不再代表另一条时间线：
| 原工作包 | 统一归属 |
| :--- | :--- |
| P0 / A0 / B0 | G0；B0 的参考证据在 G2 正式 bootstrap 前使用，不在 G2 重复做成另一阶段 |
| P1 | G1；其画像管理细分到 G3，不能要求 G1 先交付整个 L0 管理 |
| P2 / B1 | G2；B2/B3 及多端点免重建在 G4 |
| P3 | G5；G4 的准入先于异构端点混合生产 |
| P4 | 最小操作界面随 G1–G6 同步；全协议补齐在 G7，不是全部 UI 后置 |
| P5 / B4 | 运维最小能力贯穿，集中容量/发布证据在 G9；路由管理必须随 G4 交付 |
| A1 | G3（依赖 G1/G2 基础），是首个完整动态配置交付门 |
| A2/A3 | G6，多格式逐项验收，L1 动态服务与相应管理同时交付 |
| A4 | G5 的有界扩展与 G7 的领域事实；TableQuery 是受选能力，不预置任意 SQL |
| A5 | G8；没有该门不得宣传动态代码适配器 |
| B2/B3 | G4；不能在未有 G2 角色/producer 溯源时实现免重建按钮 |

专题保留工作包范围与样本矩阵，原阶段表统一替换为上述映射或链接，不再维护相互重叠的 P/A/B 排期。

## 7. 跨文档回归验收与实施记录

| 编号 | 必测行为 | 验收门 |
| :--- | :--- | :--- |
| T01 | 同 cohort 普通更新/删除命中旧结果缓存时，被 ReadRevision 拦截；Query 向量缓存仍可复用 | G1/G2 |
| T02 | 空库不能靠空集合覆盖跳过认证；未配置模型仍可全文检索 | G1/G2 |
| T03 | cohort 首次发布/最后引用删除、替换和重启对账，集合与向量快照一致 | G2/G4 |
| T04 | Plan 固定逻辑输入，批次换已准入端点只改 Attempt；改变 token 语义必须重新规划 | G3/G4 |
| T05 | WaitingForInput/重启 Interrupted/取消与发布竞争，不出现双成功或任务消失 | G1–G4 |
| T06 | 只刷新向量、全文、图不改变证据引用，各自 cache/read token 正确 | G3 |
| T07 | 全库 Reembed 在失效前后失败分别保留/降级相应能力；目标空间未发布不可查询 | G2/G4 |
| T08 | Q_old 在途、新路由移除它、新 producer 仅兼容 Q_new，旧 Q 不读到未获准集合 | G4 |
| T09 | 绑定 Desired 成功后、kb.db 应用后、Catalog 确认前分别崩溃，可幂等对账 | G1/G3/G4 |
| T10 | parser 流无 Completed、图可选失败、VectorPending，JobStatus 与能力清单一致 | G3/G6 |
| T11 | 模型/网络等待时无数据库租约；维护操作不等待它自身永久持有的借用 | G2/G5 |
| T12 | 任意入口/缓存/图扩展/引用无法绕过授权、local_only 和准入撤销 | 各门及 G7 |
| T13 | 新发布规则/服务/代码包仅对新计划生效，旧 Attempt 可追溯且可安全排空 | G3/G6/G8 |
| T14 | 备份恢复包含 manifest/活动 cohort/read token；外部凭据/源/模型缺失准确报告 | G9 |

每个实现 PR/变更集记录 GateId、关联契约、Schema/错误/事件版本、实际测试命令与结果、真实后端/硬件证据、剩余限制。SchemaVersion、IRSchemaVersion、外部 adapter ProtocolVersion 与业务计数器分开，不以“允许破坏性升级”省略内部协议验证。

设计阶段检查可验证文档引用、表格、围栏、状态与计划映射；无法证明实际 GPU 释放、SQLite 快照、云模型兼容、召回收益或线上故障恢复。本轮不执行这些业务验证，也不将任何 G 门标记通过。
