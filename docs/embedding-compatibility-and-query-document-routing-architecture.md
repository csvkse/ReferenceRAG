# 嵌入模型兼容切换与 Query/Document 双路由架构

> 版本：v1.1；统一审核日期：2026-10-31（Asia/Hong_Kong）。
> 跨专题版本/状态、数据权威和实施顺序以[统一契约与实施计划](unified-architecture-contracts-and-implementation-plan.md)为准，本专题负责编码兼容细节。
> 状态：调研与目标设计；未执行跨后端向量测试、重建索引或修改业务代码。
> 总体约束见[知识库与模型架构](per-source-data-isolation-and-multi-model-dynamic-lifecycle-architecture.md)，处理层见[解析与切片专题](document-parsing-content-profiles-and-adapter-management-architecture.md)。
> “不重建”与“数值/排序完全不变”分别定义；有限测试不能证明对任意未来输入绝对无损。

## 1. 两个需求的直接结论

1. 本地 BGE-M3 换成线上 BGE-M3：**可设计为无需重建已有向量，但必须验证实际编码兼容，不能按模型名称自动放行**。权重修订、pooling、tokenizer、归一化、前缀、截断、精度和输出模式都可能不同。
2. 搜索走本地、后台文档向量走线上：**可行且值得支持**。QueryEncoder 与 DocumentEncoder 分别配置，条件是该查询编码器能正确检索已有及新产生的文档向量。
3. 搜索与后台使用真正无关的不同模型：默认不可直接混用。即使维度相同，向量坐标也未必对齐。只有明确配对训练的双编码器或经过专项验证的映射方案才可成为另一种逻辑编码契约。
4. 若“搜索模型”指 reranker、意图分类或回答 Chat，它们本来就可以独立选择，不要求与嵌入模型同源；不得把这些独立模型与 QueryEncoder 混为一谈。
5. 初版聚焦 dense 单向量。M3 的 sparse/ColBERT 输出不与 dense 1024 维互换，后续按不同 RepresentationKind 单独定义。

### 1.1 “无损”的分级

| 目标 | 能否支持 | 可以承诺的范围 |
| :--- | :--- | :--- |
| 旧向量/分块不删除、不重新编码 | 可以 | 经准入后只改路由与记录，不变 VectorGeneration/内容引用 |
| 输出逐元素完全相同 | 仅有确定实现与执行条件时可能 | 不能由同名/同维度或几个探针推出 |
| 召回质量在验收集不劣于基线 | 可以测量并设准入门 | 报告数据域、长度、后端版本、误差与置信范围 |
| 所有未来查询排序和答案完全一致 | 无通用保证 | 浮点误差、近似检索和重排/生成都会引入变化 |
| 任意模型之间免重建 | 不支持 | 需要新的向量表示、完整/局部重编码或独立索引 |

产品文案采用“已验证兼容，可复用现有向量”或“质量验证通过，存在数值差异”，不把经验兼容称为数学意义的绝对无损。

## 2. 官方依据、主流做法与候选工具

| 官方方案 | 已确认的做法 | 本项目借鉴与边界 |
| :--- | :--- | :--- |
| BGE-M3/FlagEmbedding [E1][E2] | dense/sparse/multi-vector 分开；官方 M3 默认 CLS、归一化，不要求沿用早期 BGE 查询指令 | 以官方编码配方建立参考，但必须验证实际部署/导出，不凭“m3”字符串认等价 |
| Sentence Transformers [E3] | 提供 encode_query/encode_document，支持非对称任务与相应 prompt/router | Query/Document 角色进入契约；两侧不必文本处理相同，但须为匹配的一对 |
| Azure AI Search [E4] | 索引 embedding skill 与查询 vectorizer 分离，要求使用匹配的同模型配置/部署 | 分离读写职责、保持模型契约一致；不宣称 Azure 已自动认证任意本地/云实现互换 |
| Pinecone [E5] | integrated embedding 配置可区分 read_parameters/write_parameters | 借鉴角色参数分离，不把服务参数设置当成异构模型兼容证明 |
| Hugging Face TEI [E6][E7] | 提供模型服务、动态 token 批处理；可指定 model revision、dtype、pooling 等 | 自托管本地/线上同一服务镜像与模型修订，有利于减少差异；服务版本仍须验证 |
| Qdrant [E8] | 向量字段/命名空间配置维度与 metric | 存储格式校验不等于语义空间校验；不同空间可独立建字段后融合，不能用字段配置替代认证 |
| MTEB / Sentence Transformers evaluator [E9][E10] | 多类嵌入评测与检索评估机制 | 借鉴数据/评测协议；公开榜单不能替代“本地 Query × 当前私有语料向量”的交叉测试 |

### 2.1 可用库与引入决策

- 已有 Microsoft.Extensions.AI 10.10.0 可使用 IEmbeddingGenerator 抽象，配合项目角色/溯源包装 [E11]；它不自动判断两个服务是否在同一空间。
- 已有 ONNX Runtime、LLamaSharp、Tokenizer 与 BackendAdapter；复用推理与计数，不重复造模型运行时。
- FlagEmbedding/Sentence Transformers 可作为离线参考与验证 worker，TEI 可作为自托管一致服务候选；不要求为此把生产服务改成 Python。
- 首版 .NET 误差、归一化与点积可用平台数值能力实现，不强制新增依赖。若需要矩阵批量指标，可评估 Math.NET Numerics [E12]；本轮未在集中包声明中发现直接引用。
- MTEB 仅作为离线评测候选，现系统未发现专门的跨实现兼容认证基础设施。Polly/HTTP resilience 可复用前述总规范候选，不负责语义兼容。
- 不存在从名称、维度自动证明任意模型无损互换的通用库；不能将自动生成证书等同于自动证明。

建议优先路线：同一固定模型修订、相同角色配方、自托管 TEI/标准导出与本地执行之间验证；商业线上别名无法固定修订时降低保证级别并持续监测，不默认为可信替代。

## 3. 当前项目核对与实施风险

| 现有源码 | 可复用基础 | 本次发现的差距 |
| :--- | :--- | :--- |
| [IEmbeddingService](../Infrastructure/ReferenceRAG.Infrastructure/Features/ModelManagement/Interfaces/IEmbeddingService.cs) | 已有 EmbeddingMode.Query/Document/Symmetric | 角色当前被描述为前缀，尚无角色编码契约、端点路由和生产者标识 |
| [LazyModelServices](../Infrastructure/ReferenceRAG.Infrastructure/Features/ModelManagement/Services/LazyModelServices.cs) | 按模式取 QueryPrefix/DocumentPrefix，已有生命周期 | 单一运行状态不能替代独立 Query/Document 路由；变更前核对实际调用层避免双加前缀 |
| [EmbeddingService](../Infrastructure/ReferenceRAG.Infrastructure/Features/ModelManagement/Services/EmbeddingService.cs) | ONNX pooling 配置、L2 归一化、按模式前缀 | 加载配置不同会改变空间，不能根据文件名默认适配 M3 |
| [ModelBackendStrategyFactory](../Infrastructure/ReferenceRAG.Infrastructure/Features/ModelManagement/Strategies/ModelBackendStrategyFactory.cs) | 已创建 ONNX/GGUF/API 适配器 | 当前 GGUF 构造传 LLamaPoolingType.Mean；与官方 M3 默认 CLS 有差异，需实测，不能直接判定当前所有数据正确或错误 |
| [LlamaEmbeddingAdapter](../Infrastructure/ReferenceRAG.Infrastructure/Features/ModelManagement/Adapters/LlamaEmbeddingAdapter.cs) | 可配置 pooling、归一化、原生 token 计数 | _batchTokenCapacity 上限 512，单条输入超限被截断；宣称 context 8192 不代表编码完整 8192，长文互换尤其危险 |
| [OpenAiEmbeddingAdapter](../Infrastructure/ReferenceRAG.Infrastructure/Features/ModelManagement/Adapters/OpenAiEmbeddingAdapter.cs) | REST 与 prefix 适配 | SupportsAsymmetricEncoding=false；只补 prefix 不能覆盖所有供应商 input_type/task 语义 |
| [OpenAIEmbeddingService](../Infrastructure/ReferenceRAG.Infrastructure/Features/ModelManagement/Services/OpenAIEmbeddingService.cs) | 批处理、维度探针与标准响应 | 带 EmbeddingMode 的重载直接忽略 mode，发送 model/input；探针只检查维度，未认证配方，不能据此保证非对称模型适配 |
| [FileIndexPipeline](../Business/ReferenceRAG.Business/Features/Indexing/Services/FileIndexPipeline.cs) | 写入使用 EmbeddingMode.Document | VectorRecord 主要保存 ModelName，缺少具体端点/编码实现/证书/原输入视图哈希 |
| [HybridSearchService](../Business/ReferenceRAG.Business/Features/Search/Services/HybridSearchService.cs) | 查询使用 EmbeddingMode.Query | 仍依赖同一 _embeddingService 与 ModelName 选表，不能单独切换 QueryEndpoint |
| [ModelManagementExtensions](../Infrastructure/ReferenceRAG.Infrastructure/Features/ModelManagement/ModelManagementExtensions.cs) | DI、模型/计数/生命周期服务注册 | IEmbeddingService 是全局单例，需重写为角色路由，保留底层工厂与适配器 |
| [VectorRecord](../Infrastructure/ReferenceRAG.Infrastructure/Models/VectorRecord.cs) | 分块向量与模型/维度记录 | 目标 schema 增加语义契约与生产者 cohort，不靠“当前模型”覆盖来源 |

这些是源码静态证据，不构成本轮已复现的具体检索故障；本轮不顺手修复。尤其不能将现有 Mean/512 截断的输出直接重新贴标为官方 M3 默认空间。来源证据不足时先抽样复算和验证，必要时重建。

## 4. 身份拆分：语义契约、执行实现与路由

总规范原有 EmbeddingSpec 进一步精确定义，保留这个名称但不再把物理 Provider 与逻辑空间绑定死。

| 对象 | 职责与关键字段 |
| :--- | :--- |
| EmbeddingSpec | 不可变逻辑检索编码契约：RepresentationKind、维度、metric、参考模型/配对修订、QueryEncodingContract、DocumentEncodingContract、标准 normalization/预处理 |
| RoleEncodingContract | 角色、输入语言/模态、tokenizer 语义修订、pooling、投影/输出层、prompt/task/input_type、特殊 token、padding/truncation 与允许输入域 |
| EndpointDefinition/Revision | 本地/线上入口、协议、Provider、凭据引用、执行配置、健康；地址/密钥不进入 EmbeddingSpecHash |
| EncodingImplementationRevision | 实际权重/导出/Tokenizer/引擎/精度/量化、角色变换、后处理及限长；结果生产/查询时的真实算法指纹 |
| CorpusCohort | 候选定义/准入在 Catalog；正式活动向量的 producer 引用数、集合与 CorpusCohortSetRevision 在 kb.db，随发布/删除同事务维护；staging 不计入活动集合 |
| CompatibilityAdmission | QueryEncodingRevision → CorpusCohort 的有向准入、可接受等级/数据域、报告与修订、有效期/撤销状态 |
| EmbeddingRoutePolicy | kbId、Query 主/备用端点、Document 主/备用端点、路由修订、QoS、出站策略、准入等级要求 |
| RuntimeKey | Endpoint/EncodingImplementation/ExecutionProfile/设备/后端；生命周期实例身份，不能直接当语义空间身份 |

同一模型的 ONNX/GGUF 文件哈希可能不同；不能要求文件字节相同才能认同一逻辑契约，也不能因此省略转换与量化认证。导出关系、配置和测试共同作为实现准入证据。训练配对的双编码器可在同一 EmbeddingSpec 指定不同 Query/Document checkpoint；两个随意模型不能由管理员填相同 SpecId 就获得准入。

EmbeddingSpecHash 包含逻辑语义参数，不含 ProviderId、BaseUrl、API Key、超时、批次大小和纯设备参数；实际算子、精度、截断等在实现指纹内记录并接受验证。密钥/地址改变若仍指向同一已固定实现，可只改路由；服务实际实现改变必须重新认证。

### 4.1 兼容不是可随意传递的等价关系

“Q_A 能检索 D_A”不推出“Q_B 能检索 D_A”；“Q_B 与 Q_A 探针接近”也不保证所有长文本和边界查询一致。准入按角色、具体实现修订、语料 cohort、metric 和允许数据域记录，不能用 union-find 将所有“m3”合并。

若知识库现有 C1/C2 两类文档生产者，激活 QueryEndpoint Q 必须同时具有 Q→C1、Q→C2 的有效准入。切换 DocumentEndpoint 产生 C3 时，必须保证所有可被正式路由选用的 Query 主/备用均可检索 C3；否则拒绝写入路由切换或先移除不兼容备用。

初版限制每库活跃 Document cohort 的数量（建议最多 2，参数待实测），防止兼容矩阵和 provenance 无界增长。超限选择归一重编码或独立表示；不能丢失旧 cohort 标识后强行合并。

空库的首对 Q/D 不依赖生产向量或 vacuous 空集合覆盖：G2 先用固定参考配方与隔离获准样本建立 ReferenceAdmission，再激活初始配对路由；同端点也需校验真实配方。第一批正式向量提交时才将其 cohort 加入实际集合并复核全部 Query 主/备用。未配置或验证未通过时只提供文本/结构图。具体 bootstrap 与事务见统一计划第 4 节。

## 5. 兼容验证：不能只比较维度

### 5.1 四级结论

| 结论 | 含义 | 生产行为 |
| :--- | :--- | :--- |
| Unknown | 只有声明或元数据不足 | 不自动接替；只允许隔离验证/影子查询 |
| ContractAligned | 权重/角色配方可追溯，结构校验通过，但无充分召回验证 | 进入验证，不直接承诺免重建 |
| ValidatedCompatible | 固定修订、约束输入域与验收集的数值/交叉检索通过 | 允许策略明确接受的路由切换，显示验证范围；记录数值差异 |
| Rejected / Suspended | 结构、召回、漂移或有效性不满足 | 阻止新路由/写入，保留原向量供其他有效 Query 使用 |

证书是准入记录，不是对任意输入的数学证明。不可固定的远端别名仅能获得“当前观测版本、有限保证”的有时效记录；StrictPinned 策略拒绝这种端点接管 Document 生产，也不作为自动兜底。

### 5.2 分层验证步骤

1. 元数据比对：区分 fine-tune/base/M3-unsupervised、dense/sparse/ColBERT；核对 query/document 配方、Tokenizer、最大输入、输出层、pooling、单位化与距离度量。
2. 输出校验：每批维度、数量/顺序、有限值、非零范数、dtype/后处理均合规。拒绝 NaN、空结果、模拟向量及重复/缺失 index；实际截断要显式回报并符合契约。
3. 对照探针：中英文、短/长、代码、小说、合同否定/金额、表格、Unicode、空白、长度边界；同输入分别比较两端 Query 输出和 Document 输出。
4. 数值统计：L2/最大绝对误差、同输入向量余弦、范数、批次/顺序敏感性和重复执行漂移。只看“自相似度接近 1”不足以证明交叉排序正确。
5. 检索矩阵：固定语料/查询，比较 Q_ref×D_ref、Q_new×D_ref、Q_ref×D_new、Q_new×D_new；混合历史 cohort 时再对混合集测试。Query-only 切换不必生产全量 D_new，但必须测 Q_new×所有实际历史 cohort。
6. 质量指标：Evidence Recall@K、nDCG/MRR、TopK overlap、候选分数/阈值变化、近分 tie 查询及不同长度域；数值近似与标注相关性分别报告。
7. 管理者审查范围后激活，保存输入/输出摘要、真实修订、阈值和报告；私有文档只有在既定出站权限允许时才发送验证端点。

阈值保存在 ValidationProfileRevision；依据首次参考实测、重复运行噪声与业务损失容限确定，发布前固定，不用一条“cos>0.99”覆盖全部模型。批次大小和硬件可能影响浮点误差，验证须包含生产使用的范围。评分阈值附近与几乎同分的结果尤其需要复核。

对于单位化向量，若某查询新旧向量距离至多 ε，则它与单位文档向量的点积变化至多 ε；名次间隔小于约 2ε 时仍可能交换。因此即使小误差，也不能承诺排序绝对不变；探针集上的误差界不能推广到所有输入。

### 5.3 长度、量化与历史向量

若旧文档实际上被本地截到 512 token，新端点对全文 8192 token 编码会改变相同“文本块”的表示；不允许以模型 nominal context 相同判兼容。准入范围要包含实际输入视图及截断行为；统一修复截断通常属于新的文档编码版本，可能需要重编码。

只有 Query 侧更换时，不会补回旧文档向量中已丢失的长文信息。不重建能保留旧数据，但不能声称自动修正旧索引质量。若想维持老表示，必须显式接受它的已知限制；若要完整长文表示，创建相应重建任务。

量化/FP16/FP32/ONNX/GGUF 变化默认视为新的 EncodingImplementationRevision，需验证，不一概判定必须重建，也不自动认定无损。商业“BGE-M3”可能包含私有微调或预处理，元数据未知时不能补写一个猜测的指纹。

历史向量缺少生产者证据时，不执行自动“已兼容”迁移；从保留的原输入视图抽样复算仅提供有限证据，不补造不存在的来源记录。这是新功能准入约束，不建设旧数据兼容包袱。

## 6. Query/Document 双路由与切换流程

~~~mermaid
flowchart TB
    KB["KnowledgeBase: EmbeddingSpec + RoutePolicy"] --> Q["Query 路由: 本地优先"]
    KB --> D["Document 路由: 线上批量"]
    Q --> GateQ["检查 Q 到全部 corpus cohort 的准入"]
    D --> GateD["检查新 cohort 被全部可选 Q 接受"]
    GateQ --> Pool["设备预算 / Provider限流 / 租约"]
    GateD --> Pool
    Pool --> Result["返回真实实现与编码角色"]
    Result --> Read["查询既有向量"]
    Result --> Write["向量写入与 producer provenance"]
~~~

### 6.1 建议配置形态

~~~json
{
  "knowledgeBaseId": "kb_docs",
  "embeddingSpecId": "bge_m3_dense_reference_r1",
  "queryRoute": {
    "primaryEndpointId": "local_m3_onnx_r1",
    "fallbackEndpointIds": ["hosted_m3_pinned_r1"],
    "requiredAdmission": "ValidatedCompatible"
  },
  "documentRoute": {
    "primaryEndpointId": "hosted_m3_pinned_r1",
    "fallbackEndpointIds": [],
    "onUnavailable": "PauseVectorWrites"
  },
  "egressPolicy": "AllowConfiguredEndpoints"
}
~~~

示例端点不是已经验证的真实服务；保存前必须验证所有角色准入、输入域和出站约束。配置不允许单靠上述 ID 绕过验证。

### 6.2 Query 端点切换

1. 固定当前 CorpusCohortSetRevision、RouteRevision、ValidationProfile；执行候选 Query 对全部 cohort 的验证。
2. 检查所有角色能力、限长与隐私策略；复核期间新增 cohort 会使旧报告范围不足，激活需重检。
3. CAS 发布新的 QueryRouteRevision。之后的新请求选新端点；在途编码保留原路由快照，但实际召回前须经过第 6.3 节读栅栏，不能用旧 Query 无条件读取后来新增的 cohort。旧实例零租约后可卸载。
4. 不改 ContentGeneration、VectorGeneration、chunkId 与已有文档向量；不因仅切换 URL 或 Provider 清空索引。
5. 清除/隔离旧 Query 路由缓存；监测时延、质量/漂移，失败可把查询路由指回仍有效端点，无需恢复数据库。

### 6.3 Document 端点切换与混合生产者

1. 创建候选 DocumentEncodingRevision 与 cohort，验证全部启用的 Query 主/备用能检索它，并检查现有块的输入预算。
2. 激活新的 DocumentRouteRevision/ProducerAdmissionEpoch，后台在批次边界切换；ProcessingPlanHash 固定逻辑角色输入/tokenizer 语义/预算，实际端点和实现记录在每次 EmbeddingAttemptEnvelope，不因已准入端点替代改计划。每批固定生产者与输入视图，能力不满足则重新规划，禁止一个响应内混入不明实现。
3. 已有向量不重算；新内容标记新 cohort，旧 cohort 保持真实来源。覆盖文档向量时以 kb.db 短事务同时切换活动指针、cohort 净引用数、必要的集合修订及 ReadRevision；最后一个正式引用删除才移出集合。Catalog 的候选/准入不是活动数据真相，撤销也不能删集合成员来掩盖未认证数据。
4. 旧批次可在准入仍有效、输入/文档/IndexEpoch 未变时按原快照提交，记录旧生产者；撤销/强制禁用后的迟到响应不得发布。
5. 全部 cohort 的准入覆盖是读路由的约束，新写入不得使现有正式查询不可用。单写进程通过控制序列/短发布门控协调激活、撤销与提交，不能只在网络请求前检查一次。

读栅栏固定为“先编码、再校验并取得召回快照”：Query 向量返回后，在短控制门内读取最新 cohort-set/准入撤销版本，检查实际 QueryEncodingRevision 对全部可见 cohort 的准入，并建立与该集合一致的 kb.db 读取事务快照；随后释放控制门，在该有限快照内执行召回。网络/模型推理不在事务或门控内等待。并发新增 producer 要在读快照建立后才发布，或已被该次准入覆盖；不得出现校验 C1 后无快照地读到 C1+C2。

若在途旧 Query 已无法覆盖新集合，丢弃该查询向量，在剩余 deadline 内最多重新规划一次当前路由；仍不能满足则显式降级/失败，不静默过滤新 cohort。跨库分别获得各库一致快照，不承诺跨库同一时刻。撤销使新读快照不可用；已经通过门控并开始的有限召回可完成，强制即时停止需额外取消在途召回，不能声称能撤回已经发送的结果。G0 验证实际 sqlite-vec/读连接的快照行为。

纯路由替换不推进 IndexEpoch/VectorGeneration，不使其他索引任务失效；ProducerAdmissionEpoch/RouteRevision 专门治理端点切换。真正修改 EmbeddingSpec/处理内容才按原规范推进相应索引版本。

### 6.4 失败与回退

| 情况 | 行为 |
| :--- | :--- |
| 本地 Query 不可用、已认证线上 Query 可用且允许外发 | 按显式策略回退，重新检查当前 cohort 覆盖、剩余 deadline 与费用 |
| Query 候选未知或某 cohort 未覆盖 | 不执行不兼容检索；AllowPartial 可按声明降级全文/图结构，Strict 明确失败，不静默遗漏某些向量 |
| 线上 Document 超时/限流 | 排队/有限重试/暂停向量任务；已有检索继续，新增文本可标 VectorPending |
| 备用 Document 通过全部准入 | 可按批次切换并记录 cohort；未认证的本地端点不能因“离线兜底”而写混空间 |
| 返回维度/范数/实现摘要异常、模拟向量 | 拒绝该批，挂起端点准入，避免污染正式库 |
| 输入超长且端点不能按契约编码 | 回到 ProcessingPlan 重新切片或明确失败，不静默缩短文本 |
| local_only | 本地 Query、远端索引也同样受限；“搜索本地”不代表后台文档没有外发 |

## 7. 设计模式、接口、缓存与认证生命周期

Adapter 负责协议与输出归一；Strategy 负责角色端点选择与回退；Policy/Specification 负责语义准入；StateMachine 负责验证/激活/挂起；Lease 负责运行资源。这些职责不合并到一个 CanSwitchModel(string name) 方法。

~~~csharp
// 目标契约示意，值对象和持久化实现待实施。
public interface IEmbeddingRouter
{
    ValueTask<IEmbeddingLease> AcquireAsync(
        EmbeddingSpecId spec, EncodingRole role,
        EmbeddingRouteSnapshot routes, WorkloadContext work, CancellationToken ct);
}
public interface IEmbeddingCompatibilityService
{
    Task<ValidationReport> ValidateAsync(
        CompatibilityRequest request, CancellationToken ct);
    AdmissionDecision Check(
        QueryEncodingRevision query, CorpusCohortSet cohorts,
        AdmissionPolicy policy);
}
public sealed record EmbeddingBatchResult(
    EncodingRole Role, string SpecHash, string EncodingRevision,
    string EndpointRevision, string AdmissionId,
    IReadOnlyList<EncodedItem> Items);
~~~

每个 EncodedItem 包含稳定输入 ID/视图哈希、向量与已知截断信息。请求角色、前缀和服务 input_type/task 由 RoleAdapter 显式映射，不能在多个层重复加指令。公共 OpenAI embeddings 无标准化 Query/Document 参数时，使用绑定的固定角色 endpoint 或提供商扩展；不假装传一个通用 mode 就所有服务生效。

准入生命周期为 Declared → Verifying → Validated → Active → Suspended/Expired/Revoked。EndpointHealth 与 AdmissionStatus 分开；网络恢复不能自动恢复被撤销的语义认证。过期需复验，漂移触发停止新使用；已产生的数据按 cohort 标记隔离状态并决定重新认证、暂停向量检索或重编码，不“删除日志后视为正常”。

认证报告绑定 role revision、cohort set、数据域、metric、ValidationProfileRevision、端点可验证的版本标识；route CAS 与文档提交均复核有效性。撤销和迟到响应竞争由单写控制序列+短提交门协调，禁止在门控内等待网络或推理。未知远端静默漂移仍可能逃过探针，界面显示此剩余风险。

缓存规则：
- Query 向量键含 Query 文本/正规化规则、QueryEncodingRevision、spec、角色与实际端点实现；只按 EmbeddingSpecHash 合并不再足够。
- Document 缓存键含真实输入视图哈希、DocumentEncodingRevision/后处理和 spec；不跨未证明相同的近似实现复用缓存。
- 已验证兼容但存在数值差异的两个端点，仍不共享同一缓存键；实际选定 fallback 后按实际实现查缓存。
- 跨库 Query 编码只在实际 Query 编码修订/预算/出站策略相同且该输出获准查询各库时合并。
- 检索结果缓存必须另含完整规范化请求、当前授权修订、逐库 ReadRevision/generation/cohort-set、准入撤销和路由/排序版本。ReadRevision 随普通更新/删除、各视图刷新及能力失效同事务递增；同 cohort 更新也不能命中旧结果。每次缓存读取先重检授权/控制版本，Query 向量缓存不因纯语料更新而失效。

## 8. 管理交互、性能与验证矩阵

管理页新增“编码契约与兼容端点”：
1. 选择逻辑 EmbeddingSpec，分别展示 Query 与 Document 编码配方。
2. 注册本地/线上 Endpoint，显示实际实现/精度/限长、可验证版本、角色支持与隐私策略。
3. 运行兼容测试，显示数值差异、交叉召回、历史 cohort 覆盖与未验证域；测试可能发送样本，先按既定出站策略筛选。
4. 选择用途为“只替换查询”“只替换后台生产者”或“两者”，显示需补的准入及是否不重建。
5. 激活新路由，展示在途任务保持快照、未来批次生效；数据空间改变时改显示“需要重编码”，不沿用免重建提示。
6. 检索与索引详情记录实际端点、编码角色、准入报告、cohort 与耗时。搜索端本地但后台线上时明确展示数据外发位置。

目标 API：
| 端点 | 行为 |
| :--- | :--- |
| POST /api/embedding-specs | 创建逻辑 Query/Document 契约，不通过命名自动合并 |
| POST /api/embedding-endpoints | 注册具体实现/Provider/角色与声明，探测不自动激活 |
| POST /api/embedding-compatibility/jobs | 返回验证任务，固定样本域、策略、端点修订与 cohort |
| GET /api/embedding-compatibility/reports/{id} | 分角色数值/召回报告、版本、有效性和限制 |
| POST /api/knowledge-bases/{id}/embedding-routes | ExpectedRouteRevision/CohortSetRevision、用途和准入验证后激活 |
| POST /api/embedding-admissions/{id}/revoke | 影响预览与安全撤销，阻止迟到写入/未认证回退 |

性能边界：在线批量 Document 生成可释放本地 GPU 给查询/Chat，但引入网络、配额、成本和隐私约束；本地 Query 减少单次网络往返，也可能因本地 Chat 争用出现尾延迟。分别测 p50/p95/p99、冷启动、batch token、429/超时、资源峰值和费用，不默认“本地一定快、线上一定快”。

首版验证先采用代表性子集和离线精确点积，再使用实际 sqlite-vec 检索和线上回放；大矩阵分块，不能一次将全库向量复制到内存或做无界 N×M 分配。每日/版本变更探针可使用无敏感合成文本，真实质量复验按事件/任务触发，不为所有请求额外调用多个端点。

最小验收：
| 场景 | 必须观察到的行为 |
| :--- | :--- |
| Query 本地替换线上同契约端点 | 旧向量/代次不变，实际路由正确，交叉召回达标 |
| 后台线上、检索本地 | Query/Document 角色与前缀正确，结果有实际编码 provenance |
| 同名不同 pooling / 微调 / 模式 | 不自动获准；同维度不能绕过 |
| 长文 512 与 8192 截断差异 | 报告发现，不掩盖信息丢失 |
| FP16/量化/不同导出 | 显示差异与适用范围，不自动要求或豁免重建 |
| 已有 C1/C2，新增 C3 | 全部 Query 主/备用覆盖检查，不通过则不发布新生产者 |
| Q_A→C1、Q_B→C2 仅各自通过 | 不推断 Q_A→C2 或 Q_B→C1，禁止传递性合并 |
| 路由激活期间有旧批次 | 有效旧准入可提交；已撤销迟到响应被拒绝 |
| 旧 Query 在途时路由移除它，并新增只匹配新 Query 的 cohort | 旧 Query 在读栅栏被拒绝/有界重规划；不能读取未获准的新集合 |
| 服务别名或实现摘要变化 | 重新验证/挂起，不永久信任名称 |
| local_only 与 fallback | 索引、查询、验证样本均不越权外发 |
| 仅改凭据/健康/端点地址 | 不自动重建或重切片；真实实现改变仍需复验 |
| 停用某 Provider | 兼容替代路径仍可服务；无法替代时准确显示角色能力降级 |

图谱中的结构节点不因仅替换编码端点重建；若实体/社区本身有向量字段，也按各自逻辑 EmbeddingSpec 与 producer cohort 管理。更换图抽取模型或内容画像是另一种重处理，不与端点切换捆绑。

## 9. 工作包归属与剩余边界

执行顺序以[统一计划 G0–G9](unified-architecture-contracts-and-implementation-plan.md)为唯一依据，B 编号只保留专题范围：

| 工作包 | 统一门与放行条件 |
| :--- | :--- |
| B0：参考与差异确认 | G0，固定 M3 配方/当前实现差异；隔离 spike 不作正式准入 |
| B1：角色/身份/溯源 | G2，含空库 ReferenceAdmission、同端点双角色与首批正式 cohort |
| B2：免重建 Query 替换 | G4，交叉验证、报告、读栅栏及实际管理界面/API同步交付 |
| B3：线上生产/本地检索 | G4，混合 producer、全部 Q 主/备用覆盖、批次快照/撤销/缓存竞态 |
| B4：管理/漂移/费用/隐私 | 最小能力随 G2/G4 实现，集中真实故障/容量与发布证据在 G9 |

在 G2 的基础闭环完成前不开放多端点免重建；不能把测试按钮/状态恢复/准入撤销全部后置至运维阶段。测试与 Job/Attempt 状态按统一计划执行，未通过门不宣传可用。

真正不同模型间的蒸馏、学习投影、向后兼容嵌入训练或双索引迁移可另立专项。把 A 向量补零、截维、旋转或归一化，不能凭空恢复与 B 的语义对齐；即使学习映射可用，也需域外检验并作为新实现/表示记录，不作为首版“无损转换器”。

本方案不要求保留历史软件格式；保留 producer provenance 与验证版本是当前数据正确性的组成，不是旧架构兼容层。当前项目底层可复用，但尚无完成的双路由和认证体系；本轮只完成调研/文档，不将参考库或静态检查描述成已经实测通过。

## 10. 官方来源

调研日期为 2026-10-31；以下支持模型/工具能力与行业做法，具体端点是否兼容仍须实测。
- [E1 BGE-M3 官方说明](https://github.com/FlagOpen/FlagEmbedding/blob/master/research/BGE_M3/README.md)：dense/sparse/multi-vector、查询指令与使用限制。
- [E2 FlagEmbedding M3 推理实现](https://github.com/FlagOpen/FlagEmbedding/blob/master/FlagEmbedding/inference/embedder/encoder_only/m3.py)：pooling、归一化、精度与角色参数。
- [E3 Sentence Transformers Semantic Search](https://www.sbert.net/examples/sentence_transformer/applications/semantic-search/README.html)：非对称 Query/Document 编码。
- [E4 Azure AI Search Vectorizer](https://learn.microsoft.com/en-us/azure/search/vector-search-how-to-configure-vectorizer)：索引/查询分工及匹配模型要求。
- [E5 Pinecone Create an Index](https://docs.pinecone.io/guides/index-data/create-an-index)：integrated embedding 与读写参数。
- [E6 Hugging Face TEI](https://huggingface.co/docs/text-embeddings-inference/en/index)：模型服务与批处理。
- [E7 TEI CLI Arguments](https://huggingface.co/docs/text-embeddings-inference/en/cli_arguments)：模型修订与执行参数。
- [E8 Qdrant Collections](https://qdrant.tech/documentation/manage-data/collections/)：维度、度量与命名向量。
- [E9 MTEB](https://github.com/embeddings-benchmark/mteb)：嵌入评测框架。
- [E10 Sentence Transformers Evaluators](https://www.sbert.net/docs/package_reference/sentence_transformer/evaluation.html)：检索与数值评估参考。
- [E11 Microsoft.Extensions.AI](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai)：已有 .NET 嵌入抽象。
- [E12 Math.NET Numerics](https://github.com/mathnet/mathnet-numerics)：可选数值工具，非兼容认证器。
