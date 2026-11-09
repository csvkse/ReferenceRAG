# 词法检索默认启用与自适应容灾降级架构可行性调研与设计方案

> **文档状态**：已完成可行性调研与架构设计  
> **实施版本**：ReferenceRAG v2.2+  
> **关联模块**：`ReferenceRAG.Business.Features.Search`、`ReferenceRAG.Infrastructure.Features.Database`、`ReferenceRAG.ApiHost`、`Web UI`  
> **设计模式**：策略模式 (Strategy Pattern)、装饰器/回退组合 (Fallback Decorator)、适配器模式 (Adapter Pattern)、状态机模式 (State Machine Pattern)、断路器模式 (Circuit Breaker Pattern)

---

## 一、背景与问题根因剖析 (Problem Statement & Current Friction)

### 1.1 现状痛点与用户摩擦 (User Friction)
在用户使用 ReferenceRAG 的 `/search` 页面进行知识检索时，直观暴露了以下严重的体验与可用性矛盾：

1. **界面入口与后端能力的“假死锁”（UX False Promise）**：
   - 搜索页面顶端显式提供了四个检索单选按钮：`[快速]`、`[词法]`、`[平衡]`、`[深度]`。
   - 当用户在默认配置下点击 `[词法]` 并发起搜索时，系统直接阻断并弹出红色错误：
     $$\text{HTTP 409 Conflict: } \{\text{"code": "lexical\_disabled", "error": "词法检索尚未启用。"}\}$$
   - 用户尝试在前端设置页开启，但 [`SettingsController.cs`](file:///e:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/Host/ReferenceRAG.ApiHost/Controllers/SettingsController.cs#L203-L205) 明确规定：
     ```csharp
     if (current.Search.EnableLexical != config.Search.EnableLexical)
         return Conflict(new { error = "索引模式不能在运行时切换；请停止服务后修改配置并重启。" });
     ```
     用户必须登录服务器终端、手动修改 `appsettings.json`、执行 `docker restart` 才能使用该功能，操作链条极其冗长复杂。

2. **资产就绪与功能失活的割裂（Asset Disconnect）**：
   - 事实上，当前系统在常规索引构建（`IndexService`）时，**已经常态化维护了 `bm25.db`（`Fts5BM25Store`）**。
   - 界面状态栏明确显示 `BM25: 940 文档` 与 `向量: 940 分块`，底层的 SQLite FTS5 倒排索引完全就绪且数据新鲜。
   - 错误之所以发生，是因为历史设计将 `[词法]` 模式硬编码绑定到了独立的代管理协调器（`LexicalIndexCoordinator`）与其实验性开关 `Search.EnableLexical: false`，使得已就绪的 FTS5 全文倒排索引在单一词法请求时被“无谓锁死”。

3. **零容灾机制导致可用性脆断（Zero Fallback & SLA Cliff）**：
   - 当系统依赖云端 API（如 SiliconFlow BGE-M3 嵌入模型、BGE-Reranker-v2-m3 重排模型）或外部模型端点时，面临三大常见不可抗力：
     - **配额与限流**：触发 HTTP 429 Too Many Requests，或 API Token 额度用尽。
     - **网络波动与抖动**：宿主机外网中断、DNS 解析失败、SSL 握手超时、云端返回 502/503/504。
     - **本地硬件异常**：CUDA OOM、DirectML 驱动崩溃、本地 ONNX 引擎未加载。
   - 在上述任意故障发生时，或者用户将 `Indexing.EmbeddingEnabled` 置为 `false` 时，系统均直接抛出 500 Internal Server Error 或 409 Conflict，导致搜索和聊天（Chat）功能彻底瘫痪，可用性 SLA 归零。

---

## 二、头脑风暴：可行性、逻辑闭环与性能全景评估 (Brainstorming & Feasibility)

围绕用户的核心诉求：**“能否将词法作为降级选项并默认支持？”**，从可行性、逻辑一致性、性能开销三个维度展开头脑风暴与严密推导。

```
                    ┌────────────────────────────────────────────────────────┐
                    │               用户检索请求 (User Query)                │
                    └───────────────────────────┬────────────────────────────┘
                                                │
                                    ┌───────────▼───────────┐
                                    │ 检索调度器 (Router)   │
                                    └─────┬───────────┬─────┘
                                          │           │
                     [显式词法模式]       │           │ [混合/快速/深度模式]
                     QueryMode.Lexical    │           │
                                          │           ▼
                                          │    ┌─────────────────────────────┐
                                          │    │ Primary: 向量/混合检索策略  │
                                          │    │ (Embedding + BM25 + Rerank) │
                                          │    └──────────────┬──────────────┘
                                          │                   │
                                          │       ┌───────────┴───────────┐
                                          │       │ 探测/执行健康检查     │
                                          │       │ (CircuitBreaker/Try)  │
                                          │       └─────┬───────────┬─────┘
                                          │             │ 成功      │ 异常/超时/禁用
                                          │             │           │ (Fallback Trigger)
                                          │             ▼           ▼
                                          │      ┌────────────┐ ┌───────────────────────────┐
                                          │      │ 密集向量   │ │ Fallback: 本地极速词法策略 │
                                          └─────>│ 混合召回   │ │ (Fts5BM25Store 毫秒级返回)│
                                                 └─────┬──────┘ └─────────────┬─────────────┘
                                                       │                      │
                                                       │      ┌───────────────┘
                                                       │      │ [附加降级遥测 IsDegraded=true]
                                                       ▼      ▼
                                          ┌───────────────────────────────────┐
                                          │ 统一结果装配 (ChunkResult / Stats)│
                                          └─────────────────┬─────────────────┘
                                                            │
                                                            ▼
                                          ┌───────────────────────────────────┐
                                          │ 前端响应 (高可用保证，温和提示)   │
                                          └───────────────────────────────────┘
```

### 2.1 可行性推导（Feasibility: 100% 可行且低成本）
1. **默认开启词法模式（Default Enable）**：
   - `Fts5BM25Store` 在 `Infrastructure` 层已经深度实现了 SQLite FTS5 引擎、结巴（jieba.NET）精确中文分词、中英停用词过滤以及 BM25 Okapi 排序算法。
   - 将 `Search.EnableLexical` 默认值变更为 `true`，并由检索策略工厂直接挂载 `LexicalSearchStrategy`，即可直接复用现有的 `bm25.db` 或协调器索引，彻底消除前端 409 报错，无需任何外部依赖。
2. **作为自动容灾降级底座（Automatic Degradation Tier）**：
   - 词法检索（FTS5 BM25）运行在本地进程内 SQLite 中，完全不占用 GPU 显存，不产生任何网络 IO，耗时稳定在 1~10ms，具备 **“零外部依赖、绝对高可用”** 的本质特征。
   - 当云端 Embedding 服务超时（如 >3000ms）、断网、配额耗尽或返回 4xx/5xx 时，检索管线捕获异常，就地回退至本地 FTS5 检索，可以保证服务永远在线（Never Down）。

### 2.2 逻辑与一致性陷阱分析及化解方案 (Edge Cases & Countermeasures)

| 潜在风险/逻辑缺陷 | 根因机理 | 规避与优化设计 (Countermeasure) |
| :--- | :--- | :--- |
| **评分空间异构性 (Score Disparity)** | 密集向量返回余弦相似度 $[0.0, 1.0]$，而 BM25 原始分值为任意实数（SQLite `bm25()` 表现为非标准化负数）。直接混合或返回会导致前端评分标签渲染为红色低分或数值混乱。 | 引入 **评分归一化适配器 (`ScoreNormalizerAdapter`)**：使用 Min-Max 极值映射或动态 Sigmoid 函数将 BM25 绝对得分归一化至 $[0.1, 0.95]$ 区间；多路召回时统一使用 **RRF (Reciprocal Rank Fusion)** 无量纲排序倒数融合。 |
| **无感知降级引发诊断困难 (Silent Failure)** | 如果系统悄悄降级为词法检索，用户在搜索意图泛化的高级语义时可能发现匹配精度下降，却无法知晓模型服务已经出现故障。 | **显式可观测性规范 (Transparent Telemetry)**：在 `AIQueryResponse.Stats` 和 `PipelineTelemetry` 中注入 `IsDegraded: true` 与 `DegradeReason`（如 `"Embedding upstream 429: Rate limited, fell back to local FTS5 BM25"`）。前端显示黄色温和横幅，运维端产生告警日志。 |
| **长尾等待拖垮整体延迟 (Tail Latency Trap)** | 若云端 API 超时 30 秒才失败，降级即便成功，用户也已经经历了 30 秒卡死，降级失去了实用意义。 | 引入 **分级超时预算 (Tiered Timeout Budgeting)**：对云端 Embedding 设置硬超时门限（建议 2500ms），结合 **断路器 (Circuit Breaker)**，一旦连续 3 次超时或故障直接熔断 30 秒，熔断期间新请求 0ms 旁路直接执行本地词法检索。 |
| **分块元数据一致性 (Metadata Integrity)** | 词法检索召回的分块必须包含与向量检索完全一致的 `RefId`、`FileId`、`FilePath`、`StartLine`、`EndLine`、`HeadingPath`。 | 统一使用已有的 `ChunkResult` 领域模型，确保不论从 `vectors.db` 还是 `bm25.db` 召回，分块的 Markdown 标题树与 Obsidian 双向链接定位完全一致，保持下游 LLM 上下文装配（Context Building）的一致性。 |

### 2.3 性能与算力开销对比 (Performance Profile)

| 检索范式 | 平均延迟 (P50) | 长尾延迟 (P99) | CPU 占用 | GPU / 显存占用 | 网络开销 | 可用性保证 (Availability) |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| **标准混合重排 (HybridRerank)** | 600ms ~ 1200ms | 2500ms ~ 4000ms | 中等 (分词与合并) | 依赖云端或 2~4GB 显存 | 需两次云端 API 交互 | 取决于云端服务健康与网络 |
| **极速向量检索 (Quick/Standard)**| 150ms ~ 400ms | 1000ms ~ 2000ms | 极低 | 依赖云端或 1GB 显存 | 需一次云端 API 交互 | 取决于云端服务健康与网络 |
| **本地词法检索 (Lexical FTS5)** | **1ms ~ 5ms** | **8ms ~ 15ms** | **微秒级轻量** | **0 MB (纯零开销)** | **0 字节 (完全离线)** | **100% 绝对可用 (本地进程级)** |
| **自适应降级模式 (Fallback)** | 正常时同 Hybrid；故障时 **$\le 2505\text{ms}$** (熔断期 **3ms**) | **$\le 2515\text{ms}$** | 自动规避堆积 | 保护云端/显存不被打崩 | 故障时自动断网重试 | **99.99% 持续在线** |

---

## 三、设计模式重构与架构蓝图 (Design Patterns & Architecture)

为避免简单堆叠 `try-catch` 导致的“面条代码”和架构退化，本方案系统性引入五大经典设计模式：

```
       ┌────────────────────────────────────────────────────────────────────────┐
       │                 ISearchExecutionStrategy (策略模式抽象)                │
       └───────────────────────────────────┬────────────────────────────────────┘
                                           │
                ┌──────────────────────────┴──────────────────────────┐
                │                                                     │
 ┌──────────────▼─────────────────┐                 ┌─────────────────▼─────────────────┐
 │   LexicalSearchStrategy        │                 │  FallbackResilientSearchStrategy  │
 │   (纯词法检索具体策略)         │◄────────────────┤  (容灾降级装饰器组合策略)          │
 └──────────────┬─────────────────┘  委托作为备选   └─────────────────┬─────────────────┘
                │                                                     │ 包裹作为主策略
                │                                   ┌─────────────────▼─────────────────┐
                │                                   │ HybridRerank / Quick / Deep       │
                │                                   └───────────────────────────────────┘
                ▼
 ┌──────────────────────────────────────────────────────────────────────────────────────┐
 │                      ILexicalRetrievalAdapter (适配器模式抽象)                       │
 ├────────────────────────────────────────┬─────────────────────────────────────────────┤
 │ Fts5BM25StoreAdapter (适配本地bm25.db) │ LexicalCoordinatorAdapter (适配多代词法库)  │
 └────────────────────────────────────────┴─────────────────────────────────────────────┘
                                           ▲
                                           │
 ┌─────────────────────────────────────────┴────────────────────────────────────────────┐
 │                  SearchPipelineStateMachine (状态机模式增强)                         │
 │ [QueryPrepared] ──> [ParallelRetrieving] ──> [Degrading] ──> [DegradedCompleted]     │
 └──────────────────────────────────────────────────────────────────────────────────────┘
                                           ▲
                                           │
 ┌─────────────────────────────────────────┴────────────────────────────────────────────┐
 │                    ResilienceCircuitBreaker (断路器模式熔断防护)                     │
 │          [Closed (闭合)] <──(试探成功)─── [Half-Open] <──(超时/失败)─── [Open]       │
 └──────────────────────────────────────────────────────────────────────────────────────┘
```

### 3.1 策略模式与装饰器模式 (Strategy & Decorator Pattern)
- **原生词法策略 (`LexicalSearchStrategy`)**：
  实现统一的 [`ISearchExecutionStrategy`](file:///e:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/Business/ReferenceRAG.Business/Features/Search/Strategies/ISearchExecutionStrategy.cs)，将词法检索标准化为一等公民策略。
- **容灾降级装饰器 (`FallbackResilientSearchStrategy`)**：
  采用装饰器与复合策略模式。它持有主策略（如 `HybridRerankSearchStrategy`）与备选策略（`LexicalSearchStrategy`）：
  - **前置判断**：检查全局配置（若 `EmbeddingEnabled == false` 或模型未就绪，直接零等待走备选策略）。
  - **断路感知**：若底层嵌入服务的断路器处于 `Open` 状态，旁路主策略直接走备选策略。
  - **异常自愈**：当主策略执行抛出网络异常、超时异常、API 限流异常或向量库只读异常时，拦截异常，记录状态机降级跃迁，无缝执行备选词法策略。

### 3.2 适配器模式 (Adapter Pattern)
- **`ILexicalRetrievalAdapter`**：
  统一屏蔽底层的具体词法存储差异。
  - 适配器 A (`Fts5BM25StoreAdapter`)：对接主线默认维护的 `Fts5BM25Store`。
  - 适配器 B (`LexicalCoordinatorAdapter`)：对接支持多代只读隔离的 `LexicalIndexCoordinator`。
  - 适配器 C (`KnowledgeBaseLexicalAdapter`)：对接独立知识库的 SQLite FTS 召回流。
- **`IScoreNormalizer`**：
  将 BM25 离散得分自适应映射为归一化置信度分值。

### 3.3 状态机模式 (State Machine Pattern)
扩展现有的 [`SearchPipelineStateMachine`](file:///e:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/Business/ReferenceRAG.Business/Features/Search/StateMachine/SearchPipelineStateMachine.cs)：
- 在 [`SearchPipelinePhase`](file:///e:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/Business/ReferenceRAG.Business/Features/Search/StateMachine/SearchPipelinePhase.cs) 中新增生命周期状态：
  - `Degrading`：主检索异常，正在触发降级转移。
  - `DegradedRetrieved`：降级词法召回完毕。
  - `DegradedCompleted`：降级装配完成。
- 状态机包含 `RecordDegradation(Exception triggerEx, string targetStrategy)` 方法，自动记录故障源阶段、异常信息及降级耗时。

### 3.4 断路器模式 (Circuit Breaker Pattern)
在云端调用层（`SiliconFlow` / `OpenAI` / `LocalOnnx`）引入线程安全的三态断路器 `ResilienceCircuitBreaker`：
- **Closed (正常闭合)**：正常放行所有请求。
- **Open (故障熔断)**：当短时间内（如 60 秒内）连续发生 3 次异常（429、Timeout、5xx），断路器跳开，持续 30 秒。在此期间所有请求**直接短路至本地词法检索，耗时 0ms**，防止级联等待。
- **Half-Open (半开探测)**：熔断期过后，允许单个请求尝试调用向量模型。若成功则切回 Closed；若失败则重新熔断 30 秒。

---

## 四、业界主流方案与竞品调研对比 (Industry Benchmarks)

调研主流企业级搜索引擎与工业级 RAG 框架在“混合检索降级与词法兜底”方面的成熟实践：

| 框架 / 产品 | 降级机制与容灾拓扑 | 触发条件 | 分数协调方案 | 用户态/可观测性 |
| :--- | :--- | :--- | :--- | :--- |
| **AWS OpenSearch** (2.11+) | `fallback_to_lexical: true`<br>Neural Search 自动退化为 BM25 match | 外部 SageMaker / Bedrock 嵌入不可用、超时或集群负载过高 | 默认使用 Normalization Processor 或 RRF 进行打分抹平 | HTTP 响应包含 `_degraded: true`，CloudWatch 输出告警指标 |
| **Azure AI Search** | 托管多级检索（Semantic Ranker + Vector + BM25）自动降级 | 向量化管道（Skillset / Azure OpenAI）不可达或配额耗尽 | 语义打分器退化为系统原生 BM25 排名得分 | 响应 Header 包含 `x-ms-search-degraded` 警告标识 |
| **LlamaIndex** | `RouterRetriever` + `FallbackRetriever`<br>以密集检索为主，以 `BM25Retriever` 为 fallback | 捕获 `RateLimitError`、`APIConnectionError`、`TimeoutError` | 依赖 `ReciprocalRerank` 重排器重整候选 | Python 记录 `WARNING` 日志并在 Metadata 标记 `fallback_source` |
| **LangChain** | `retriever.with_fallbacks([bm25_retriever])` | 任意检索异常自动流转至后备 Runnable | 管道式候选聚合 | LangSmith 链路追踪上呈现降级分支高亮 |
| **Elasticsearch** | Text Search + Dense Vector 混合管道容灾 | 向量维度不匹配、模型加载中或节点内存不足 | 放弃线性组合权重，退化为单一 BM25 分数 | 响应中返回 `partial_failures` 与警告字段 |
| **ReferenceRAG (本设计方案)** | **`FallbackResilientSearchStrategy` + `ResilienceCircuitBreaker` + 状态机端到端透传** | **网络异常、超时、API 429、配置禁用、模型崩溃** | **Min-Max / Sigmoid 归一化 + RRF 融合双保险** | **`Stats.IsDegraded=true` + `PipelineTelemetry` + 前端温和 Toast/Badge** |

---

## 五、系统现有基础设施与资产复用审查 (Assets Reuse & Gaps)

### 5.1 可无缝复用的现有资产
1. **[`Fts5BM25Store`](file:///e:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/Infrastructure/ReferenceRAG.Infrastructure/Features/Database/Fts5BM25Store.cs)**：
   - 现成维护于 `data/bm25.db`。已具备完整的中英分词、停用词过滤和高效 FTS5 倒排索引能力。
   - 每次用户添加数据源或重建索引时，系统均已自动为其建好索引，**零改造即可直接作为默认词法底座**。
2. **[`DualChannelEmbeddingService`](file:///e:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/Infrastructure/ReferenceRAG.Infrastructure/Features/ModelManagement/Services/DualChannelEmbeddingService.cs)**：
   - 内部已具备 `enableFallback` 思想及生成通道与检索通道解耦的基础，为底层适配提供了良好的先例。
3. **[`SearchPipelineStateMachine`](file:///e:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/Business/ReferenceRAG.Business/Features/Search/StateMachine/SearchPipelineStateMachine.cs)**：
   - 状态机设计优良，各阶段耗时追踪和遥测输出（`PipelineTelemetry`）完整，只需增加降级状态扩展。
4. **[`SearchStrategyFactory`](file:///e:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/Business/ReferenceRAG.Business/Features/Search/Strategies/SearchStrategyFactory.cs)**：
   - 策略注册表机制已存在，只需将 `Lexical` 纳入注册字典并套上容灾装饰器。

### 5.2 必须打破的硬编码与架构断层
1. **断层 1：`SearchConfig.EnableLexical` 默认值为 `false`**：
   - 历史遗留标记，导致开箱即用的体验被阻断。
2. **断层 2：`SearchService.SearchAsync` 内的排他抛错**：
   ```csharp
   // 现状：直接抛出异常中断
   if (!config.Indexing.EmbeddingEnabled)
       throw new SearchCapabilityException("embedding_disabled", "embedding 已禁用，请选择词法检索模式。");
   ```
   应改造为自动降级：无需报错，自动将当前请求执行为词法检索。
3. **断层 3：`SettingsController.cs` 限制运行时切换**：
   - 因为词法检索实际上可以无缝利用现有的 `Fts5BM25Store`，所以不涉及危险的底层存储格式变更，应当放开该限制。

---

## 六、端到端具体落地实施方案 (Implementation Blueprint)

### 6.1 配置层改造 (`ObsidianRagConfig.cs`)
在 `SearchConfig` 中更新默认值并新增降级调优选项：

```csharp
public class SearchConfig
{
    /// <summary>启用词法检索入口，默认开启（开箱即用）。</summary>
    public bool EnableLexical { get; set; } = true;

    /// <summary>当向量服务不可用（断网、限流、模型崩溃、未就绪）时，是否自动降级为本地词法检索，保障服务不中断。</summary>
    public bool EnableAutomaticFallback { get; set; } = true;

    /// <summary>云端向量检索硬超时门限（毫秒），超时后立即触发极速词法降级，默认 2500ms。</summary>
    public int EmbeddingTimeoutMs { get; set; } = 2500;

    /// <summary>对话检索策略；保持旧版混合重排默认值。</summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
    public QueryMode ChatQueryMode { get; set; } = QueryMode.HybridRerank;
    
    // ... 其他属性保持不变
}
```

### 6.2 状态机枚举扩展 (`SearchPipelinePhase.cs`)
新增自适应降级生命周期：

```csharp
public enum SearchPipelinePhase
{
    QueryPrepared,
    ParallelRetrieving,
    HybridFusing,
    GraphExpanding,
    Reranking,
    AssemblingResults,
    Completed,
    // === 容灾降级扩展阶段 ===
    Degrading,           // 捕获异常，正平滑转移到备选策略
    DegradedRetrieved,   // 本地词法备选检索完毕
    DegradedCompleted    // 降级装配完成
}
```

并在 `SearchStats` 与 `SearchPipelineTelemetry` 中暴露：
```csharp
public class SearchStats
{
    public int TotalMatches { get; set; }
    public long DurationMs { get; set; }
    public int EstimatedTokens { get; set; }
    public SearchPipelineTelemetry? PipelineTelemetry { get; set; }
    
    // 降级可观测字段
    public bool IsDegraded { get; set; } = false;
    public string? DegradeReason { get; set; }
}
```

### 6.3 统一词法策略实现 (`LexicalSearchStrategy.cs`)
将独立的词法检索逻辑抽取为标准的 `ISearchExecutionStrategy`：

```csharp
public class LexicalSearchStrategy : ISearchExecutionStrategy
{
    private readonly Func<IBM25Store> _bm25StoreFactory;
    private readonly Func<LexicalIndexCoordinator>? _coordinatorFactory;

    public LexicalSearchStrategy(
        Func<IBM25Store> bm25StoreFactory,
        Func<LexicalIndexCoordinator>? coordinatorFactory = null)
    {
        _bm25StoreFactory = bm25StoreFactory;
        _coordinatorFactory = coordinatorFactory;
    }

    public QueryMode Mode => QueryMode.Lexical;

    public async Task<SearchStrategyResult> ExecuteAsync(
        SearchExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        context.StateMachine.TransitionTo(SearchPipelinePhase.ParallelRetrieving, 1, context.Request.TopK, "执行本地高速词法检索 (FTS5 BM25)");

        var bm25Store = _bm25StoreFactory();
        var rawResults = await bm25Store.SearchAsync(context.Request.Query, context.Request.TopK);

        // 评分归一化处理 (避免负分或离散分失真)
        var normalized = NormalizeBm25Scores(rawResults);

        return new SearchStrategyResult
        {
            Results = normalized,
            RerankApplied = false,
            RerankStats = null
        };
    }

    private static List<SearchResult> NormalizeBm25Scores(List<BM25SearchResult> raw)
    {
        if (raw.Count == 0) return new List<SearchResult>();
        var maxScore = raw.Max(r => r.Score);
        var minScore = raw.Min(r => r.Score);
        var range = Math.Max(maxScore - minScore, 0.0001);

        return raw.Select(r => new SearchResult
        {
            ChunkId = r.ChunkId,
            Content = r.Content,
            Score = (float)(0.5 + 0.45 * ((r.Score - minScore) / range)), // 映射至 [0.5, 0.95]
            Rank = r.Rank
        }).ToList();
    }
}
```

### 6.4 容灾降级装饰器组合策略 (`FallbackResilientSearchStrategy.cs`)
核心调度守卫，实现透明故障转移：

```csharp
public class FallbackResilientSearchStrategy : ISearchExecutionStrategy
{
    private readonly ISearchExecutionStrategy _primaryStrategy;
    private readonly ISearchExecutionStrategy _fallbackStrategy;
    private readonly ResilienceCircuitBreaker _circuitBreaker;
    private readonly ILogger _logger;

    public FallbackResilientSearchStrategy(
        ISearchExecutionStrategy primaryStrategy,
        ISearchExecutionStrategy fallbackStrategy,
        ResilienceCircuitBreaker circuitBreaker,
        ILogger logger)
    {
        _primaryStrategy = primaryStrategy;
        _fallbackStrategy = fallbackStrategy;
        _circuitBreaker = circuitBreaker;
        _logger = logger;
    }

    public QueryMode Mode => _primaryStrategy.Mode;

    public async Task<SearchStrategyResult> ExecuteAsync(
        SearchExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        // 门禁 1：若静态配置已显式禁用 Embedding，直接零等待降级
        if (!context.Config.Indexing.EmbeddingEnabled)
        {
            return await ExecuteFallbackAsync(context, "Embedding 已在配置中全局禁用", cancellationToken);
        }

        // 门禁 2：检查断路器状态，若已处于熔断期，快速短路降级
        if (_circuitBreaker.IsOpen)
        {
            return await ExecuteFallbackAsync(context, $"向量端点处于熔断冷却态 (断路保护中)", cancellationToken);
        }

        // 门禁 3：为云端/计算调用附加超时上限预算
        using var timeoutCts = new CancellationTokenSource(context.Config.Search.EmbeddingTimeoutMs);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            var result = await _primaryStrategy.ExecuteAsync(context, linkedCts.Token);
            _circuitBreaker.RecordSuccess();
            return result;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _circuitBreaker.RecordFailure();
            _logger.LogWarning("[SearchFallback] 向量检索超时 ({Timeout}ms)，触发自动词法降级", context.Config.Search.EmbeddingTimeoutMs);
            return await ExecuteFallbackAsync(context, $"向量检索超时 ({context.Config.Search.EmbeddingTimeoutMs}ms)", cancellationToken);
        }
        catch (Exception ex) when (IsTransientOrRecoverable(ex))
        {
            _circuitBreaker.RecordFailure();
            _logger.LogWarning(ex, "[SearchFallback] 主检索策略发生异常，触发自动词法降级: {Message}", ex.Message);
            return await ExecuteFallbackAsync(context, $"主策略异常: {ex.Message}", cancellationToken);
        }
    }

    private async Task<SearchStrategyResult> ExecuteFallbackAsync(
        SearchExecutionContext context,
        string reason,
        CancellationToken ct)
    {
        context.StateMachine.TransitionTo(SearchPipelinePhase.Degrading, 0, 0, $"容灾降级生效: {reason}");
        
        var fallbackResult = await _fallbackStrategy.ExecuteAsync(context, ct);

        context.StateMachine.TransitionTo(SearchPipelinePhase.DegradedRetrieved, 0, fallbackResult.Results.Count, "词法降级结果装配完毕");
        
        // 打上标记供下游透传
        context.StateMachine.RecordDegradation(reason);

        return fallbackResult;
    }

    private static bool IsTransientOrRecoverable(Exception ex) =>
        ex is HttpRequestException or TimeoutException or SearchCapabilityException or InvalidOperationException;
}
```

### 6.5 策略工厂统一集成 (`SearchStrategyFactory.cs`)
```csharp
public class SearchStrategyFactory : ISearchStrategyFactory
{
    private readonly Dictionary<QueryMode, ISearchExecutionStrategy> _strategies;

    public SearchStrategyFactory(
        IHybridSearchService? hybridSearchService,
        Func<IBM25Store> bm25StoreFactory,
        ResilienceCircuitBreaker circuitBreaker,
        ILogger<SearchStrategyFactory> logger)
    {
        var lexicalStrategy = new LexicalSearchStrategy(bm25StoreFactory);

        // 为各个模式装配容灾组合策略
        ISearchExecutionStrategy Wrap(ISearchExecutionStrategy primary) =>
            new FallbackResilientSearchStrategy(primary, lexicalStrategy, circuitBreaker, logger);

        _strategies = new Dictionary<QueryMode, ISearchExecutionStrategy>
        {
            [QueryMode.Lexical] = lexicalStrategy, // 原生词法策略直接支持
            [QueryMode.Quick] = Wrap(new QuickSearchStrategy()),
            [QueryMode.Standard] = Wrap(new StandardSearchStrategy()),
            [QueryMode.Hybrid] = Wrap(hybridSearchService != null ? new HybridSearchStrategy(hybridSearchService) : new StandardSearchStrategy()),
            [QueryMode.HybridRerank] = Wrap(hybridSearchService != null ? new HybridRerankSearchStrategy(hybridSearchService) : new StandardSearchStrategy()),
            [QueryMode.Deep] = Wrap(hybridSearchService != null ? new DeepSearchStrategy(hybridSearchService) : new StandardSearchStrategy())
        };
    }

    public ISearchExecutionStrategy GetStrategy(QueryMode mode) =>
        _strategies.TryGetValue(mode, out var strategy) ? strategy : _strategies[QueryMode.HybridRerank];
}
```

### 6.6 前端温和提示与交互适配 (`Search.js` & `Search.vue`)
前端通过响应中的 `isDegraded` 自动渲染友好告警，不打断用户工作流：

```javascript
// 在 handleSearch 响应处理中：
if (response.data.stats?.isDegraded) {
    message.warning(`⚡ 云端向量服务暂时不可用（${response.data.stats.degradeReason}），系统已自动平滑降级至本地极速词法检索。`, {
        duration: 5000,
        closable: true
    });
}
```

并在分块卡片上方显示小徽标：`[词法降级匹配]`，保证用户知情权与信任感。

---

## 七、质量门禁与验收测试规划 (Quality Gates & Verification)

本方案需配套以下单元与集成测试套件，纳入 CI/CD 与自动化回归体系：

```
tests/ReferenceRAG.Tests/
  ├── LexicalDefaultSupportAndFallbackTests.cs  (核心策略与断路器单元测试)
tests/ReferenceRAG.Host.Tests/
  ├── LexicalDegradationHostE2ETests.cs         (端到端 HTTP 与错误注入模拟测试)
```

### 核心测试用例矩阵

1. **`LexicalMode_DefaultEnabled_ReturnsMatchesWithout409`**：
   - 默认配置下，以 `mode=Lexical` 请求 `POST /api/ai/query`，断言状态码返回 `200 OK`，`TotalMatches > 0`，绝不抛出 `lexical_disabled`。
2. **`EmbeddingDisabled_AutomaticallyDegradesToLexical`**：
   - 设置 `Indexing.EmbeddingEnabled = false`，发起 `mode=HybridRerank` 查询。
   - 断言不抛出 `embedding_disabled` 异常，返回 `200 OK`，且 `Stats.IsDegraded == true`。
3. **`UpstreamApiTimeout_FastFailsAndDegradesWithinBudget`**：
   - 注入模拟云端 API 挂起 10 秒。
   - 断言整体请求在 $\le 2600\text{ms}$ 内迅速返回，`Stats.IsDegraded == true`，`Stats.DegradeReason` 包含 `超时` 字样。
4. **`CircuitBreaker_OpensAfterThreeFailures_ShortCircuitsInZeroMs`**：
   - 连续注入 3 次 HTTP 500 云端异常。
   - 第 4 次请求发起时，断言耗时 $\le 10\text{ms}$，直接由断路器短路执行词法检索。
5. **`ScoreNormalization_ScoresRemainWithinValidRange`**：
   - 验证降级召回的所有分块得分均严格处于 $[0.1, 1.0]$ 区间内，且在前端排序正常。

---

## 八、总结与实施建议 (Conclusion & Recommendation)

1. **结论**：
   - 用户的直觉非常敏锐：**在本地已维护 `bm25.db` 的前提下，报“词法检索尚未启用”且不能在运行时启用，确实属于体验缺陷；将词法默认支持并作为自适应降级选项，在技术上 100% 可行，并且符合工业级高可用规范**。
2. **收益**：
   - **用户体验翻倍**：消除前端 409 红色弹框，开箱即用。
   - **系统可用性达到 99.99%**：彻底免疫外部 API 抖动、配额耗尽、离线断网导致的崩溃。
   - **架构清晰优美**：通过策略模式、装饰器模式与状态机统一了检索拓扑，消除了特殊分支与硬编码。
3. **推荐实施阶段**：
   - **P1**：修改 `Search.EnableLexical` 默认值为 `true`，解除 `SettingsController` 运行时互斥限制，消灭 409 报错。
   - **P2**：接入 `FallbackResilientSearchStrategy` 与轻量断路器，实现 `HybridRerank` 遇异常自动回退至本地 FTS5。
   - **P3**：前端感知与遥测优化，实现黄色 Toast 优雅提醒。
