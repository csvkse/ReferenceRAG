# ReferenceRAG：SAG2 前端交互与可插拔证据评估设计

日期：2026-11-08。状态：调研及设计建议，未实现、未做端到端或性能验证。配套阅读：[SAG2 无向量检索可行性](sag2-embedding-free-retrieval-feasibility.md)。以下界面文案、API 和配置结构均为提案，不表示项目已经提供。

## 1. 回答四个问题

1. 前端需要区分**索引构建方式、查询策略、证据评估后端和回答模型**。用户在搜索页选择模式，在设置页配置证据评估后端，在知识源页查看词法和向量索引状态；展示实际运行与降级，不让 Jev 成为必填项。
2. 需要词法独立索引、证据驱动的多跳检索、统一证据评估契约、Jev 协议适配、重排适配、预算与安全策略、范围过滤、可追溯性和评测。Jev 是可选配置；如果选本地重排或纯词法，不需 Jev 账号。
3. 可以用重排模型替代**每轮候选排序**，也可尝试把已有证据组装到模型输入，但普通 reranker 往往输出查询与候选的相关性，不保证理解证据必要性与多跳上下文。结果、阈值和性能必须实测；不能承诺与 Jev 等价。
4. 可以。建议业务概念为 **候选证据评估器（Evidence Evaluator）**，Jev、现有重排器和未来实现是不同后端。这个概念负责“对候选按指定意图给出带语义的排序/评分”，而不是声称每种实现都能计算证据必要性的概率。

## 2. Jev 在流程中的角色

SAG2 按问题用 BM25 召回候选；对每个候选判断是否包含回答问题所需事实，利用已有证据辅助判断；根据选中的候选提取扩展词，继续检索并重复评估，最后返回原文。Jev 做的是这些**查询阶段的结构化判断**，不负责生成最终答案，也不生成文档 embedding。最后问答仍由单独的生成模型完成。

在本地参考 `SAG-Wiki-main/core/src/models.rs` 中，`jev_batches` 把原始问题和先前证据放进 `state`，为每个候选创建 `noul` 问题，发送 model/state/questions；候选包含 title、passage 及可选 heading，并通过候选 ID 映射结果；实现还有 token 预算、批处理和超大候选处理。`retrieval.rs` 负责轮次、去重、评分、扩展和取消。应参考其行为并结合官方协议做契约测试，不能把常规聊天 `chat/completions` 或 `/rerank` 假定为 Jev 端点。

例子：问题是“某项目作者出生于哪座城市？”候选 A 说明“项目作者是 Alice”，候选 B 说明“Alice 出生于 X”。两者都是潜在必要证据，即使 B 未提及项目名。普通重排器可能把 B 排低；若给它附加 A 作为上下文，它可能改善，也可能因输入格式、长度截断和模型训练目标导致噪声。这需要比较数据，而不是接口适配后假定同等效果。

### Jev 与重排对比

| 项目 | Jev 型证据判断 | 普通 Cross-Encoder / rerank |
|---|---|---|
| 典型输入 | 问题、先前证据状态、候选及明确判断标准 | 问题、候选文档；有些模型/服务支持指令或上下文拼装 |
| 通常输出 | 带 `noul` 类型的判断/分数；具体值按协议解析 | 各候选相关性分数或排名 |
| 已有证据 | 在协议和提示中显式提供 | 接口通常没有独立参数；拼接是近似适配，不保证使用 |
| 阈值 | 与服务版本和判断语义相关 | 与模型、接口、输入模板、量化相关，不可共享 Jev 阈值 |
| 设备 | 取决于服务部署；远程会发送问题及证据 | 本项目支持本地 ONNX/GGUF，也有远程重排适配 |

官方 SAG-Benchmark 提供重排替代实验：MuSiQue 1000 题中，作者报告 Jev 对照 Recall@10 为 86.61%，Jina 双扩展为 72.13%，Qwen 0.6B 双扩展为 74.18%。这仅说明**作者的测试条件下可替换但结果不同**；这些不是本项目的中文笔记表现，且不同消融的提示和流程并不完全一致。来源：[官方复现](https://github.com/Zleap-AI/SAG-Benchmark/blob/main/sag2/README.md)。

## 3. 当前代码可复用内容

| 位置 | 已确认能力 | 设计影响 |
|---|---|---|
| `Business/ReferenceRAG.Business/wwwroot/app/views/Search.js` | 现有搜索页 | 在现有模式控件基础上扩展；先核对其实际请求与展示绑定，避免另起页面 |
| `Settings.js` | 现有设置页 | 证据评估配置作为独立分组，复用设置读写与校验交互；密钥保护需另核实 |
| `Models.js` | 当前区分 embedding、rerank、chat，支持模型与远程模式管理 | 本地证据评估复用现有重排模型选择；Jev 作为远程能力配置，不伪装成本地可下载模型 |
| `Sources.js` | 知识源管理 | 增加词法与向量状态、补向量入口；按当前源/知识库实际数据结构决定位置 |
| `Chat.js` | 现有知识库绑定与聊天模型状态 | 独立展示检索策略和回答模型；不能把回答模型切换当作切换证据评估器 |
| `IRerankService` | `RerankBatchAsync(query, documents, ct)`，返回 index/score | 无独立 previous evidence 参数；`RerankerEvidenceEvaluator` 可调用，但结果是 relevance，不要修改原接口的语义 |
| `IRerankBackendAdapter` 与 ONNX、GGUF、CausalLogit、OpenAi 适配器 | 已有本地及远程重排路径 | 通过现有服务复用生命周期、资源调度、模型下载和 GPU/CPU 回退。远程的所谓 OpenAI 兼容要按服务实际重排协议核验 |
| `ObsidianRagConfig.cs` 中 `RerankConfig` | 本地 ONNX/GGUF 和 API profile、模型及 API 配置 | 证据评估配置引用现有重排 profile，不复制模型配置；Jev 连接另存自己的 protocol/auth/model |
| `SearchPipelineStateMachine`、索引状态机、JobCenter | 现有流水线追踪与任务能力 | 扩展阶段事件/状态，不引入新工作流引擎；需先确认循环、取消和序列化兼容性 |

以上为源码静态检查；特别是前端具体渲染、密钥返回方式、多知识库路由、远程重排的功能覆盖及服务端热更新均未做运行验证。

## 4. 前端交互方案

### 4.1 搜索页：选择一次查询的方式

采用用户能理解的名称，并在高级信息中显示算法；不要直接堆满 Lexical、Evidence、Hybrid 等术语。

| 模式 | 用户说明 | 依赖 | 不可用时 |
|---|---|---|---|
| 关键词快搜 | 离线查找原文，适合命令、错误码 | 词法索引；不需模型 | 索引未就绪：展示建立/等待任务，不静默加载 embedding |
| 关联深搜 | 从候选中继续寻找关联证据，适合跨文档问题 | 词法索引 + 已配置的评估器 | 未配置/不可用：说明原因，提供设置入口和“改用快搜” |
| 混合搜索 | 综合关键词和语义 | embedding、可用向量索引；重排按配置 | 向量未就绪：展示覆盖率和补齐入口；是否允许降级由用户/调用方明确选择 |

保留现有 Quick/Standard/Hybrid/Deep API 语义及配置映射；新模式以新稳定值加入，避免把现有 Quick 悄悄改为 BM25。单次选择优先级：请求显式选择 > 当前页面选择 > 用户默认 > 现有兼容默认；URL/会话持久化遵循现有前端行为。兼容期内旧客户端与 API 请求不改变结果路径。

搜索输入附近显示：当前评估后端（如“本地重排”或“Jev 远程”）、隐私属性（本机/远程）、索引就绪状态。常规用户不必看到 token、轮次数；高级选项显示最大扩展轮次、候选预算、允许降级及查询范围。不得把基于词法启发式的检索称为 Jev 或原版 SAG2。

进行中显示“检索候选 → 评估证据 → 查找关联证据（第 1/1 轮）→ 整理结果”，可取消；后端阶段不支持流式进度时显示整体加载，不伪造进度。结果显示来源、原文/行号、实际模式、评估后端、扩展轮数与是否降级；高级诊断可展开耗时、候选数、策略版本，避免把不同模型分数显示为统一百分比可信度。

### 4.2 设置页：证据评估器

新增“检索 → 证据评估”，不是强制新增 Jev 顶级菜单：

- 后端：`本地/现有重排器`、`Jev 兼容服务`；可选 `无评估器（只快搜）`。本地重排直接引用已选现有 profile，给“前往模型设置”入口。
- Jev 兼容服务：服务 URL 或完整端点、模型名、认证方式（无认证/Bearer）、密钥输入与清除、超时、最大并发、批次/输入预算。URL 补全规则按后端协议适配，不在前端拼接；明确 base URL 和完整路径的冲突规则。
- 单独的“测试连接”：在保存前允许使用临时未保存表单（密钥只发服务端且不可回显），或测试已保存配置；使用固定无敏感文本验证协议结构，不把“HTTP 200”当成能力验证。返回 DNS/连接、认证、协议字段、模型不可用、超时等脱敏结果；测试成功不等于质量达标。
- 保存后显示已配置/未验证/可用/不可用、实际本地或远程；设置变更要说明正在执行的查询继续使用请求开始时的配置快照。密钥为 write-only，读接口返回 `hasCredential`，留空保留，明确的清除动作才删除；实际安全存储复用现有机制前需验证，不能假设已加密。日志、错误、浏览器存储均不含密钥。
- 高级：评分模板版本、最大轮数/候选/并发/超时、失败降级；Jev 和重排各自配置质量阈值，默认先以排名及预算工作，避免未经校准的统一阈值。

允许自定义本地 Jev **协议兼容服务**，不暗示官方 Jev 权重可自托管；兼容服务的模型实现、质量和许可另行确认。远程保存及请求前说明将发送问题、候选片段、标题及可能的先前证据；仅对获授权的数据源发送。目标 URL 在服务端校验协议、重定向、内网访问策略、鉴权转发及 DNS 重绑定，兼顾用户明确配置本地服务的需要，不能简单禁止全部内网；多用户部署还需权限控制。

### 4.3 知识源与对话

源页面显示“词法索引：就绪/索引中/失败；向量：未启用/待补齐/部分就绪/就绪/失败”，按文档数量和版本展示实际覆盖率；新源可选“仅词法”或“词法 + 向量”。从仅词法切换后启动后台补向量，不要求重建词法；已有向量不自动删除，明确提供单独清理。注意目前项目既有来源又有物理知识库，索引能力归属以实际存储模型为准，不能在 UI 只改源而遗忘知识库。

Chat 页：显示当前对话检索策略与回答模型分离；检索阶段结束显示实际模式和降级，引用仍打开原文。生成模型的远程数据传输与 Jev 传输应分别提示。多知识库情况下每库分别验证索引能力与范围，再汇总；部分库不可用时显式展示不完整范围。MCP 和 REST 使用相同服务端路由与策略规则，不依赖页面选择。

## 5. 业务接口和设计模式

### 5.1 推荐最小契约（伪 C#，非现有代码）

```csharp
public interface IEvidenceEvaluator
{
    EvidenceEvaluatorCapabilities Capabilities { get; }
    Task<EvaluationResult> EvaluateAsync(
        EvaluationRequest request, CancellationToken cancellationToken);
}

public sealed record EvaluationRequest(
    string Query,
    IReadOnlyList<EvidenceCandidate> Candidates,
    IReadOnlyList<EvidenceReference> PriorEvidence,
    EvaluationIntent Intent,
    EvaluationBudget Budget);

public sealed record EvidenceEvaluation(
    string CandidateId, int Rank, double? Score,
    ScoreSemantics Semantics, string? ScoreVersion);

public sealed record EvaluationResult(
    IReadOnlyList<EvidenceEvaluation> Items,
    string BackendId, string ModelId, bool ContextUsed,
    IReadOnlyList<string> UnscoredIds);
```

约束：`CandidateId` 唯一且和输入严格映射；检查重复、未知、缺失 ID 与非有限分数。`Semantics` 至少区分 `EvidenceNecessity`、`Relevance`、`RankOnly`；各自携带模型、模板、版本，不跨后端比较或直接融合。`ContextUsed` 指适配器**实际提交**先前证据，不代表模型必然理解；能力声明也不是质量保证。服务提供分数或置信度时按其协议标记语义，不自动将其解释为校准概率。

评估器不负责源权限、扩词或发起查询：调用前统一 scope 与数据外发门禁，评估器只获得已授权候选；输出不得加入新的未经授权文档。排序和扩词由检索编排器控制。请求不可接受时按预先指定策略 fail closed 或返回结构化降级信息；部分失败不得把未评分结果当成零分淘汰，保留其原排名或退出该轮并报告。

### 5.2 四种模式如何组合

| 检索执行策略 | 评估器 | 结果语义 | 允许程度 |
|---|---|---|---|
| 关键词快搜 | 无 | 词法相关性 | 默认可用 |
| 关联深搜 | Jev | 基于必要事实标准的判断 | 实验；需协议与数据传出配置 |
| 关联深搜 | 当前重排器 | 候选相关性，或经输入模板进行上下文近似 | 实验；不等同 Jev |
| 关联深搜 | 无 | 无模型启发式扩展 | 如有实现必须另命名和测试，不能冒充 evidence 模式 |
| 混合搜索 | 现有 rerank / 可选评估器 | 依能力协商，按模型分别显示 | 后续评测引入，避免重复重排 |

**策略模式**：现有 `ISearchExecutionStrategy` 负责选择词法、关联、混合流程；检索算法与模型实现独立。**适配器模式**：`JevEvidenceEvaluator` 调专门协议；`RerankEvidenceEvaluator` 调已有 `IRerankService`。不要让 Jev 实现具有 `ReloadModel/UnloadModel` 的本地模型接口。**工厂/DI**：在启动注册已知实现，依据配置构造/选择，处理能力不匹配，不从用户输入反射加载任意类型。**状态机**：复用现有状态机及进度事件，`Retrieving → Evaluating → Expanding → Retrieving` 由 hop 和预算控制；取消、超时、降级和完成独立记录。无需新状态机框架或一套可任意编排的 DSL。

重排适配器有两种模板策略：① `query_only` 直接使用原问题；② `context_augmented` 将问题与有限量已授权前序证据按模型支持的模板拼接，标记版本。默认先从 query_only 做基线，context_augmented 仅在真实模型支持及评测后启用；现有 `IRerankService` 无指令参数，具体怎样拼接、截断和适配 ONNX/GGUF 要逐后端验证，不允许将不同模型的前缀规则当作通用协议。批量调用注意输出 index 对应、截断上限和保持 prior evidence 原文引用位置。

## 6. 配置与接口提议

以下示例为逻辑模型，不保证与现有 `ObsidianRagConfig` 序列化名称一致；实现前应检查迁移、枚举序列化和热更新：

```json
{
  "retrieval": {
    "defaultMode": "existing-compatible-default",
    "indexCapabilities": { "lexical": true, "vectorGeneration": false },
    "evidence": {
      "evaluator": "existing-reranker",
      "rerankerProfileRef": "current",
      "maxHops": 1,
      "initialCandidates": 40,
      "totalCandidates": 60,
      "timeoutMs": 5000,
      "failurePolicy": "return-lexical-with-warning"
    },
    "jev": {
      "endpoint": "https://example.invalid/v1/systemone",
      "model": "provider-model-name",
      "authMode": "bearer",
      "credentialRef": "stored-secret-reference"
    }
  }
}
```

上面的预算只是实验初值；`vectorGeneration: false` 表示不生成向量，不表示删掉现存向量，真实默认必须保留现有安装行为。`credentialRef` 是建议的数据结构，是否有安全密钥存储要验证；不要在响应、示例、日志中暴露真实密钥。`Jev` 的 endpoint/model/auth 只在选中其后端时必填；服务不要求认证时可为 none。聊天回答模型配置独立。

建议现有搜索请求以向后兼容的可选字段增加 `retrievalMode`、`evaluatorProfileId`、`allowDegraded`；服务端响应增加 `requestedMode`、`effectiveMode`、`evaluator`、`scoreSemantics`、`completedHops`、`degradedReason`、`coverage`、`traceId`。字段具体位置依据现有 REST/MCP 契约和版本策略制定；旧客户端不得因新增 enum 值失败，默认不更改旧值。配置探测、更新、测试可扩展现有设置 API；幂等、鉴权、CSRF、错误码及 IPC 对齐现有宿主，不另造平行认证。

错误约定：未启用/未配置 → 可操作的配置错误；不支持证据上下文 → 明示 relevance 近似；网络/401/429/5xx/超时 → 可按允许策略降级但标记；取消 → cancelled，不当作模型故障；无匹配 → 正常空结果。安全策略禁止未经许可跨域外发时不能通过降级/重试绕开。

## 7. 性能与运行边界

- 无向量只免去 embedding，不意味着零模型或零网络。本地重排可能比向量查询更吃 CPU/GPU；Jev 并发、重试、网络和按量费用也会影响尾延迟。对 N100 与 5825U 分别测冷/热启动、p50/p95/p99、内存峰值、请求数、网络字节和能耗。
- 批次受模型最大上下文、标题与证据长度共同约束，不按候选数盲目分批；为先前证据保留预算，并按文档结构限长，保留原文坐标。超长候选被截断或跳过必须在诊断中报告，不能伪装已完整评分。
- 同一次查询绑定索引版本和后端配置快照。多轮累积去重、限制总候选/轮数/并发/时间；当索引变化导致候选版本失效时，重读并验证或按一致性策略结束，不混合来源版本。
- 本地模型复用现有模型 lease/空闲卸载和资源调度，但要测试多次评分竞争推理资源；避免在未请求证据深搜时提前加载模型。
- 现有 FTS5 的 `bm25(table,k1,b)` 实际是列权重，先校正理解并用真实数据测试；扩词污染、中文分词、文件路径、停用词、范围过滤先于远程调用都是质量和隔离重点。

## 8. 第三方参考及选型

| 项目 | 借鉴点 | 选择建议 |
|---|---|---|
| [SAG-Wiki](https://github.com/Zleap-AI/SAG-Wiki) | Jev batch、范围过滤、多跳扩展、取消、源码测试 | 优先读 Rust 实现、构造跨语言契约测试；MIT 源码移植需保留许可与版权 |
| [SAG-Benchmark](https://github.com/Zleap-AI/SAG-Benchmark/blob/main/sag2/README.md) | Jev 与不同本地 reranker 的消融、复现参数 | 引用前核对其代码及模型各自许可；不把英文基准当中文项目指标 |
| [Haystack Rankers](https://docs.haystack.deepset.ai/docs/ranker) | 独立 retriever / ranker、不同 ranker 组合 | 借鉴组件边界；Python 框架不是 .NET 原生依赖，暂无必要引入 |
| [Cohere Rerank](https://docs.cohere.com/v2/docs/rerank) | query 与文本相关度重排 API | 可作为将来远程 rerank 适配参考；各厂商请求格式和分数语义不一 |
| TypeSafe 官方 Python/JS SDK | Jev 协议和认证可借鉴 | 截至本次检索未确认官方 .NET SDK；优先基于 .NET `HttpClient` 和 `System.Text.Json` 实现小适配器并契约测试，不引入 Node/Python sidecar。验证 SDK 状态与协议版本后再决定 |

## 9. 验证与分期

1. **最小闭环**：无模型快搜，确认建索引、查询、来源过滤、原文引用无 embedding；不需要 Jev 配置。
2. **评估抽象**：在单元测试中用 fake evaluator 覆盖顺序错乱、部分结果、异常、取消和预算；接现有本地重排器，确保语义为 relevance。对照 0 轮与 1 轮，验证扩词能带来增量而非仅增加耗时。
3. **Jev 适配**：按真实服务协议录制脱敏 fixture 或 mock 验证请求、答案 ID、noul 字段、认证、分批、超时及取消；验证远程候选均在允许的来源范围且配置变更不影响在途调用。再做小规模真实端点测试，明确外发和费用。
4. **前端与契约**：旧配置迁移、旧客户端请求、无模型新用户、词法 → 混合补向量、无凭据、服务不可用、取消和降级提示，覆盖 Web 和桌面 IPC；测试范围权限与多知识库。
5. **效果对照**：固定语料、分块及预算，比较 BM25、BM25+rerank 0/1/2 跳、BM25+Jev 0/1/2 跳、现有混合检索；分别对中文、错误码、同义词、跨文档、多跳、无答案评估 Recall@K/AE@K/MRR、来源准确率及延迟。对各模型单独调参并保留冻结测试集。

只有用户可看到请求与实际模式、无授权数据没有进入远程请求、旧模式保持兼容、质量与资源消耗达到实测目标时，才将关联深搜从实验模式提升为推荐模式。
