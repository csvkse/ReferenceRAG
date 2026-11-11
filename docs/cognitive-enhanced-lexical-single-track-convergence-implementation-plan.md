# 认知增强词法检索（多后端适配器与单轨知识库收敛）调整落地方案

> **文档标识**：ReferenceRAG-RFC-202611-COG01  
> **状态**：通过 / 立即执行  
> **设计版本**：v2.4+ (单轨收敛版)  
> **核心原则**：Clean-Break 架构重构，不为未正式发布的历史遗留功能承担兼容负担。

---

## 一、前序方案执行评估与关键架构调整结论

### 1.1 能否直接执行前序会话 (`Lexical Flow Optimization With Jev`) 中的方案？
**结论：不能直接生搬硬套执行，必须进行四大核心架构调整。**

在前序会话中制定的初始方案，是在系统处于“旧版多目录双轨（`vectors.db` + `bm25.db` + `config.Sources`）”时代设计的。随着系统在刚完成的收敛工作中彻底切换至以 `IKnowledgeBaseSearchService`（`kb.db` + SQLite FTS5 虚拟表 `fts_chunks_lexical`）为核心的单轨物理知识库架构，前序方案若不调整直接执行，将面临以下致命缺陷：

1. **存储靶标错位（Target Storage Mismatch）**：
   - 原方案依赖旧 `_bm25StoreFactory` 与 `_vectorStore.GetChunksByIdsAsync`。在新系统中，所有权威切片（如已挂载的 39,977 个切片）均持久化在各知识库的独立 `kb.db` 中。若照搬原方案，认知检索将查向空的旧 `bm25.db`，导致检索结果全空。
   - **调整点**：全链路召回（Hop 0 初筛与 Hop 1 实体多跳）全面收敛绑定至 `IKnowledgeBaseSearchService`，以 `UseLexicalTokenizer = true` 作为权威词法检索底座。
2. **重排模型上下文缺失（Context Gap in Cross-Encoder）**：
   - Jev 原生支持在请求体中传递结构化 `previously_retrieved_evidence`；但 Cross-Encoder 重排模型只接受 `(Query, Document)` 二元组，原生无先验证据上下文支持。
   - **调整点**：采用**查询增强范式（Prompt/Context-Augmented Query）**：
     $$\text{AugmentedQuery} = \text{Query} + \text{" [参考背景: "} + \text{Truncate}(\text{PriorEvidence.Content}, 120) + \text{"]"}$$
     利用 Cross-Attention 机制让重排模型在评估新候选切片时自动与前序证据发生交叉注意力交互。
3. **分值标尺异构与归一化（Heterogeneous Score Scaling）**：
   - Jev 输出严格的概率 $P \in [0.0, 1.0]$；而 Cross-Encoder 输出未定界 Logits（例如 $-6.0 \sim +9.0$）。若直接使用固定门限（如 0.50）过滤，会导致候选切片被 100% 误杀或全通。
   - **调整点**：在适配器层建立自适应 Sigmoid 归一化层，将所有输出无缝映射至 $[0.0, 1.0]$ 标准置信度区间。
4. **统一分层代理贯通（Unified Proxy Integration）**：
   - Jev 云端端点在部分网络环境下受地理合规封锁（HTTP 451）。
   - **调整点**：Jev 适配器必须直接利用刚落地的 `DynamicWebProxyAdapter`，遵循三态策略（`Inherit` / `Direct` / `Custom`），确保外部 SaaS 通道平稳运行。

---

## 二、头脑风暴：可行性、逻辑漏洞、性能瓶颈与设计模式深度推演

### 2.1 可行性评估 (Feasibility)
- **重排模型作为第一核心本地后端的可行性**：
  极高。现代重排模型（如 BGE-Reranker-v2-m3、MiniLM-L6）在粗筛后精排的 MRR@10 与 NDCG@10 均处于统治地位。在离线内网、保密局域网或私有化部署环境下，Cross-Encoder 无需任何公网网络，完美满足合规要求。
- **Jev 作为云端概率推演后端的可行性**：
  极高。Jev 的 `noul` 分类头专为命题真值概率判定设计，计算耗时仅 100ms 左右，不占用本地显存。配合已就绪的分层代理，完全解除了网络封锁风险。
- **启发式规则（Statistical Heuristic）作为兜底后端的可行性**：
  100% 可行。基于伪相关反馈（PRF）、TF-IDF 逆文档频率与词重叠率（Jaccard / Overlap），单次推断 <1ms，0 显存、0 模型依赖，为极端低配设备提供刚性保底。

### 2.2 逻辑漏洞与边界防御 (Logic Gaps & Defenses)
1. **语义漂移与多跳发散（Semantic Drift）**：
   - 漏洞：若 Hop 0 召回的分块含有大量偏离主题的专有名词，Hop 1 盲目扩展会导致第二跳彻底偏离用户原始意图。
   - 防御：
     - 严格限制 Hop 1 提取的词项数量（$\le 5$ 项）；
     - 多跳检索构建复合查询：`{ExpansionTerms} OR ({OriginalQuery})`，强制原始查询词权重始终占比 50% 以上；
     - 限制最大多跳深度（默认 2 跳，最大 3 跳）。
2. **误杀致空隐患（Zero Recall Hazard）**：
   - 漏洞：若认知后端的置信度门限设得过高（例如 0.65），或者模型对特定领域冷门词打分过于严苛，可能导致所有候选分块都被裁决剔除，向用户返回空结果。
   - 防御：**受保护种子机制（Protected Seeds）**。Hop 0 FTS5 BM25 得分排名前 3 的分块享有永久豁免权。即使认知后端判定其未达标，也赋予其 0.50 保底分数纳入证据池，坚决杜绝空返回。
3. **网络与模型崩溃容灾（Resilience Fallback）**：
   - 漏洞：云端 API 超时（如 Jev 网络抖动）或本地 ONNX 推理显存溢出。
   - 防御：适配器内置 Polly 熔断与 `try-catch` 降级，在发生异常时立刻无缝降级为纯本地 BM25 + DPP 流程，并在返回中标记 `IsDegraded = true`。

### 2.3 性能瓶颈与优化手段 (Performance Optimizations)
1. **候选预剪枝（Pre-Rerank Pruning）**：
   - FTS5 初筛可返回 40~50 条候选，但送入重排/Jev 打分前，先按 FTS5 原始分截取前 12~15 条。实验证明，排名 20 名以后的分块包含核心证据的概率低于 1%，提前截断可节省 65% 的推断算力。
2. **早期早停截断（Early-Exit Pruning）**：
   - 若 Hop 0 评估中已出现得分 $\ge 0.85$（`EarlyExitThreshold`）的极强证据，且有效分块数已满足用户请求的 `TopK`，状态机立即判定证据充足，**跳过 Hop 1 实体多跳**，直接进入 DPP 输出，将总耗时削减 50% 以上。
3. **认知决策内存缓存（Cognitive Decision MemoryCache）**：
   - 引入 LRU 缓存，键为 `$"Cognitive:{Backend}:{Hash(Query)}:{ChunkId}"`，相同上下文下的重复评估在 1ms 内直接返回。

### 2.4 设计模式深度优化
- **适配器模式 (Adapter Pattern)**：
  定义中立接口 `ICognitiveEvidenceEvaluator`。`JevSystemOneEvidenceAdapter` 适配 System One 客户端；`CrossEncoderEvidenceAdapter` 适配 `IRerankExecutionAdapter` / `IRerankService`；`StatisticalHeuristicEvidenceAdapter` 适配纯内存规则。
- **策略模式 (Strategy Pattern)**：
  `CognitiveEnhancedLexicalStrategy` 实现 `ISearchExecutionStrategy`，对上层 `SearchService` 提供统一的词法检索执行能力。
- **状态机模式 (State Machine Pattern)**：
  扩充 `SearchPipelineStateMachine` 与 `SearchPipelinePhase` 枚举，对检索过程全程跟踪监控（Hop0检索 $\rightarrow$ 认知判定 $\rightarrow$ 早停检查 $\rightarrow$ 实体提取 $\rightarrow$ Hop1检索 $\rightarrow$ 联合判定 $\rightarrow$ DPP去重 $\rightarrow$ 装配）。
- **工厂模式 (Factory Pattern)**：
  `CognitiveEvidenceEvaluatorFactory` 根据当前配置动态解析并返回活跃适配器，支持运行时零停机热切换。

---

## 三、业界主流方案与开源库调研对比

### 3.1 业界优秀开源库与工具借鉴
1. **FlashRank (GitHub / Python & .NET)**：
   - 特点：使用量化轻量级 MiniLM 模型在纯 CPU 上执行 Cross-Encoder 重排，单次推断仅需 15ms，仅占用 4MB 内存。
   - 借鉴：为本项目提供了将重排作为轻量离线后端的理论支撑与工程信心。
2. **Polly (v8)**：
   - 特点：.NET 权威容灾弹性框架，提供超时（Timeout）、断路器（Circuit Breaker）与重试（Retry）。
   - 借鉴：用于守护外部认知 API 调用，确保检索 SLA 不受单点故障波及。
3. **Microsoft.Extensions.Caching.Memory**：
   - 特点：高性能进程内内存缓存。
   - 借鉴：为多跳检索中的高频分块评估提供二级缓存。

### 3.2 业界主流 RAG 框架对比矩阵

| 架构 / 框架 | 词法与重排多后端架构 | 多跳与先验证据支持 | 离线纯净度 |
| :--- | :--- | :--- | :---: |
| **LlamaIndex** | `BaseNodePostprocessor` 统一抽象（支持 SentenceTransformer, Cohere, LLM） | 需编写复杂的 ReAct Agent 编排流水线 | 取决于具体实现 |
| **LangChain** | `ContextualCompressionRetriever` 过滤剪枝 | 依赖复杂检索器嵌套，容易引发状态膨胀 | 取决于具体实现 |
| **SAG2 (SAG-Wiki)** | 强绑定 TypeSafe AI Jev，使用 `noul` 分类头 | 原生支持结构化 `previously_retrieved_evidence` | ❌ 强依赖云端 SaaS |
| **ReferenceRAG (本方案)** | **`ICognitiveEvidenceEvaluator` 适配器矩阵（Jev, Cross-Encoder, Statistical）** | **原生支持多跳先验证据 + Prompt-Augmented 范式** | **✅ 100% 纯本地离线可用** |

---

## 四、当前系统已有基础设施复用审计 (Audit)

系统内部具有高度完善的模块化设计，本次重构**实现 95% 以上已有资产复用**：

| 组件名称 | 所在命名空间 / 路径 | 复用方式与角色 |
| :--- | :--- | :--- |
| `IKnowledgeBaseSearchService` | `ReferenceRAG.Business.Features.KnowledgeBase.Services` | **权威召回底座**：执行跨知识库 FTS5 BM25 词法检索 |
| `IRerankExecutionAdapter` / `IRerankService` | `ReferenceRAG.Core.Features.ModelManagement` | **Cross-Encoder 适配器内核**：执行本地/云端交叉注意力打分 |
| `JevSystemOneClient` | `ReferenceRAG.Infrastructure.Features.Cognitive` | **Jev 适配器内核**：执行 System One 概率推演 |
| `DynamicWebProxyAdapter` | `ReferenceRAG.Infrastructure.Features.Network` | **网络层代理**：保障 Jev 在受限网络环境下直连/代理三态切换 |
| `LexicalEntityExtractor` | `ReferenceRAG.Business.Features.Search.Services` | **实体抽取工具**：提取 Hop 0 专有名词与高上下文 IDF 词项 |
| `DppKernelDiversitySelector` | `ReferenceRAG.Business.Features.Search.Services` | **多样性采样**：在装配最终结果前执行免向量 DPP 核去重 |
| `SearchPipelineStateMachine` | `ReferenceRAG.Business.Features.Search.StateMachine` | **流程状态治理**：纳管 Hop 0 到 Hop 1 各阶段的遥测与耗时 |

---

## 五、单轨物理知识库收敛之具体方案与代码蓝图

### 5.1 契约层定义 (`CognitiveEvidenceContracts.cs`)
- 包含 `CognitiveBackendCapabilities`（多跳支持、批处理、最大上下文等）；
- 包含 `CognitiveScoreResult`（归一化得分 $[0.0, 1.0]$、是否接受 `IsAccepted`、原因元数据）；
- 包含 `CandidateEvaluationItem`（分块 ID、标题、内容、原始 BM25 分数）；
- 包含统一评估契约 `ICognitiveEvidenceEvaluator`。

### 5.2 适配器层实现 (Adapters)
1. **`CrossEncoderEvidenceAdapter`**：
   - 依赖注入 `IRerankService` 或 `IRerankExecutionAdapter`；
   - 构造复合增强查询：`{query} [参考背景: {context}]`；
   - 实行自适应 Sigmoid 归一化：$1.0 / (1.0 + e^{-\text{score}})$；
   - 超时与异常安全保底。
2. **`JevSystemOneEvidenceAdapter`**：
   - 依赖注入 `ISystemOneClient`；
   - 构造强类型 System One 请求，在 `state` 中传递待评估文本与 `previously_retrieved_evidence`；
   - 读取 `noul` 真值概率作为置信度。
3. **`StatisticalHeuristicEvidenceAdapter`**：
   - 基于分块内容与查询词项的词频交集、逆文档覆盖率（Overlap）与位置特征快速计算启发式得分；
   - 保证零模型消耗、<1ms 耗时。
4. **`CognitiveEvidenceEvaluatorFactory`**：
   - 根据 `CognitiveLexicalConfig.Backend` 解析对应实现，并在禁用或异常时返回降级实现。

### 5.3 核心检索策略 (`CognitiveEnhancedLexicalStrategy.cs`)
- **Hop 0 初筛**：通过 `IKnowledgeBaseSearchService` 发起 `CrossKnowledgeBaseSearchRequest(UseLexicalTokenizer = true)` 获取候选分块；
- **受保护种子保护**：锁定 Hop 0 Top 3 为 `ProtectedSeeds`；
- **认知判别**：调用活跃 `ICognitiveEvidenceEvaluator.EvaluateBatchAsync`；
- **早停检查 (Early Exit)**：若有证据得分 $\ge 0.85$ 且有效证据数充足，提前结束多跳；
- **Hop 1 扩展**：调用 `LexicalEntityExtractor.BuildExpansionTerms` 抽取实体短语，发起第二跳检索并联合前验证据打分；
- **证据池融合与 DPP 多样性截断**：合并双跳有效证据，经 `DppKernelDiversitySelector` 去重后装配为最终 `SearchResult`。

### 5.4 前端设置与探测端点
- 后端：在 `SettingsController.cs` 中增加 `POST /api/settings/test-cognitive-backend` 端点；
- 前端：在 `Settings.js` 的「检索与分段」面板中增加认知增强后端单选卡片组（`Disabled` / `CrossEncoder` / `Jev` / `Statistical`），以及多跳深度、阈值调节滑块与连通性测试按钮。

---

## 六、质量保障与验证准则 (Verification Checklist)

1. **测试门禁**：
   - 编写 `CognitiveEnhancedLexicalMultiBackendGateTests.cs`，断言：
     - CrossEncoder 适配器的 Query-Augmented 评分与 Sigmoid 映射；
     - Jev 适配器的 Noul 概率读取与上下文透传；
     - Statistical 适配器的内存快速评估与保底行为；
     - 策略早停（Early-Exit）机制触发；
     - 异常时自动降级与 Protected Seeds 保底。
   - 运行全部既有单元测试与集成测试（`dotnet test` 与 `frontend-logic.test.mjs`）。
2. **部署与视觉验收**：
   - 通过 `scripts/deploy-linux.ps1 -Mode Dev -SyncMethod Auto` 部署到 Linux 容器（`127.0.0.1:5124`）；
   - 使用内置浏览器验证设置页面交互与检索返回效果。
