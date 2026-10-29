# 大模型重复生成退化防御与流式实时熔断架构设计规范 (LLM Repetition Degeneration Prevention & Stream Circuit Breaker Architecture)

## 1. 现象复盘与问题定义 (Problem Definition & Failure Analysis)

### 1.1 典型病态退化 Trace 现象
在端侧小模型（如 Qwen2.5-3B/7B/14B）执行知识库 RAG 与长文本总结、技术架构分析或多章节整理时，容易出现**病态周期性重复退化（Pathological Repetition Degeneration）**。
例如模型在生成微服务架构演进或章节总结时，陷入死循环重复输出相同结构与台词：

```text
1. 模块一：分布式鉴权服务接入
   - 时间：系统初始化阶段
   - 目标：网关全局拦截与 JWT 验证
   - 责任人：系统架构师、安全工程师
   - 方案说明：主系统与网关服务通过统一拦截器进行令牌双向校验，保证各租户数据隔离与权限合法性。

2. 模块二：分布式鉴权服务接入
   - 团队负责人对开发者说："请务必确保双向握手协议与超时重试策略准确无误，避免网关层在高并发时出现级联雪崩……"
   - 开发者回应："明白，目前已增加连接池与断路器策略，确保上游鉴权出现异常时降级至本地缓存。"
   - 双方对系统设计达成一致，进入下一阶段压测。
   - 整体架构稳定性和安全性得到阶段性验证。

3. 模块三：分布式鉴权服务接入
   - 团队负责人对开发者说："请务必确保双向握手协议与超时重试策略准确无误，避免网关层在高并发时出现级联雪崩……"
   - 开发者回应："明白，目前已增加连接池与断路器策略，确保上游鉴权出现异常时降级至本地缓存。"
   - 双方对系统设计达成一致，进入下一阶段压测。
   - 整体架构稳定性和安全性得到阶段性验证。

4. 模块四：分布式鉴权服务接入
   - 团队负责人对开发者说："请务必确保双向握手协议与超时重试策略准确无误，避免网关层在高并发时出现级联雪崩……"
...
（一直死循环复制生成到 模块十九，直到耗尽 4096 MaxOutputTokens 上限截断）
```

伴生现象还包括多轮思考标签与伪工具调用的回声循环：
```text
<think> 用户要求使用ReadFileContent来查看具体配置，我需要先获取文件路径，然后读取文件内容... </think>
根据搜索结果，我将逐个读取这些配置，查找分布式鉴权的具体实现。让我先读取文件内容。
<think> 这些文件都被标记为"未被索引"，说明知识库还没有对它们进行向量索引。我需要尝试用BM25搜索... </think>
根据搜索结果，我找到了相关配置。让我继续读取相关文件。
<think> 根据搜索结果，具体内容在搜索结果中已经部分呈现。让我尝试读取具体的文件内容。 </think>
```

---

## 2. 根本原因剖析 (Root Cause Analysis)

经过全链路排查，此问题由四个层面的结构性缺失叠加导致：

### 2.1 自回归 Transformer 的正反馈退化 (Autoregressive Positive Feedback Loop)
- 学术背景：ICLR 经典论文 *The Curious Case of Neural Text Degeneration* (Holtzman et al.) 指出，自回归模型在计算条件概率 $P(w_t | w_{<t})$ 时，Softmax 输出倾向于给予历史上高频出现的 token 更高置信度。
- 一旦模型因偶然采样或列表模板连续输出了两次相同句式（如 `模块一` 和 `模块二` 具有相同长段落），自注意力（Self-Attention）矩阵中该段落的 Key/Value 向量就会对后续生成产生压倒性的注意力吸引力。
- 此时模型陷入了“越重复、概率越高、越输出重复”的数学自强化正反馈循环。

### 2.2 推理采样层（Sampling Pipeline）处于“零惩罚裸奔状态”
检查 `LocalLlamaChatClient.cs`（第 355-359 行）：
```csharp
var pipeline = new DefaultSamplingPipeline
{
    Temperature = options?.Temperature ?? 0.7f,
    TopP = options?.TopP ?? 0.9f
};
```
在底层 `LLamaSharp` / `llama.cpp` 中，`DefaultSamplingPipeline` 的默认值经反射诊断为：
- `RepeatPenalty = 1.0f`（1.0 表示惩罚系数为 1，即**完全不惩罚重复**）
- `FrequencyPenalty = 0.0f`（**频次惩罚为 0**）
- `PresencePenalty = 0.0f`（**存在惩罚为 0**）
- `PenaltyCount = 64`（惩罚上下文距离过窄）

并且，上层 `MafChatService.cs` 在向 `LocalLlamaChatClient` 传递 `ChatOptions` 时，根本没有设置或透传任何 Penalty 相关参数。模型处于完全无拘无束的纯裸奔状态，一旦出现重复 token，Logits 丝毫不会被扣减！

### 2.3 流式生成管道缺乏实时熔断保护 (Absence of Stream Circuit Breaker)
在原有架构中，无论是在 `LocalLlamaChatClient` 内部的 `tokenStream` 枚举循环，还是在 `MafChatService.StreamAsync` 的 SSE 事件产生器中，**没有任何滑动窗口周期性检测机制**。
一旦模型进入死循环，系统必须消耗整整 4096 个 Token 的 CPU/GPU 算力和长达数分钟的推理时间，直到触及硬性上限才停机。不仅严重损耗端侧设备资源，也给用户造成严重卡死感。

### 2.4 工具检索未命中时的死循环重试 (Tool Calling Echo & Feedback Loop)
当工具执行返回“文件未索引”或无结果时，系统未能给小模型注入明确的“单轮工具终止引导或优雅退避兜底提示”，导致小模型在 Phase 1 阶段反复尝试自救，生成多个 `<think>` 标签并产生混乱的多轮工具调用假象。

---

## 3. 业界主流方案调研与对比 (Industry Mainstream Benchmarks)

调研主流大模型推理引擎（llama.cpp、vLLM、HuggingFace、Ollama、LangChain/Semantic Kernel）针对该问题的最佳实践：

| 维度 / 框架 | 机制与参数 | 工作层级 | 业界标准取值 | 优势与局限性 |
| :--- | :--- | :--- | :--- | :--- |
| **llama.cpp** | `repeat_penalty`, `frequency_penalty`, `presence_penalty`, `repeat_last_n` | 底层 C++ Logits 采样层 | `repeat_penalty=1.05~1.15`, `freq_penalty=0.1~0.3`, `repeat_last_n=128~256` | **性能最高，零内存拷贝**。在 Softmax 之前直接对历史 token 的 logit 施加衰减。若 penalty > 1.25 会引发拒绝输出正常定冠词或技术重复词，1.10 为业界公认最佳平衡点。 |
| **vLLM / TGI** | `RepetitionPenaltyLogitsProcessor` | GPU Tensor Logits 算子层 | `repetition_penalty: 1.1` | 标准批处理 logits processor，适合服务器高并发。 |
| **HuggingFace Transformers** | `no_repeat_ngram_size` (`NoRepeatNGramLogitsProcessor`) | 确定性前缀树 (Trie) / N-gram 遮罩 | `no_repeat_ngram_size=3~5` | **硬性阻断**。将任何已出现过的连续 N 个 token 再次出现时的概率置为 $-\infty$。局限：对代码、Markdown 列表符号容易误杀。 |
| **Ollama** | `repeat_penalty`, `repeat_last_n` | 运行时采样管道 | `repeat_penalty: 1.1`, `repeat_last_n: 64` | 极简封装，默认开启 1.1 惩罚。 |
| **LangChain / Semantic Kernel** | 流式熔断守卫 (Stream Circuit Breaker / Output Guardrail) | 网络流式 SSE 管道层 | 滑动窗口循环子串检测 (Sliding Window Periodicity Check) | **终极兜底安全防线**。不依赖底层特定模型实现，实时监测已输出字符流，若发现长周期重复（如长段落连续出现 3 次）立即断开流并打上标记，节约 95% 无效 Token 算力。 |

---

## 4. ReferenceRAG 四层纵深防御治理体系 (Multi-Tier Architecture)

为彻底杜绝此问题，我们在 ReferenceRAG 落地**四层纵深防御体系**：

```mermaid
flowchart TD
    Req[用户对话请求] --> Tier1[第 1 层：自适应采样与惩罚配置]
    Tier1 --> LLamaPipeline[LLamaSharp DefaultSamplingPipeline\nRepeatPenalty=1.10, Freq=0.20, Pres=0.10, Count=128]
    LLamaPipeline --> InferStream[模型流式逐 Token 生成]
    InferStream --> Tier2[第 2 层：实时流式重复熔断器 RepetitionCircuitBreaker]
    Tier2 -- 正常内容 -- --> SSE[向客户端推送 SSE 事件]
    Tier2 -- 检测到周期性循环/高频长句重复 -- --> CircuitBreak[触发熔断 Cutoff\n平滑截断冗余并安全终止]
    SSE --> Tier3[第 3 层：多思考标签融合与工具调用重试上限]
    CircuitBreak --> Tier3
    Tier3 --> Tier4[第 4 层：同类门禁与防回归自动化测试套件]
```

### 4.1 第 1 层：采样层惩罚参数挂载 (Sampling Penalty Configuration)
- 在 `ObsidianRagConfig.cs` 的 `ChatModelConfig` 与 `LocalGgufChatProfile` 声明并支持：
  - `RepeatPenalty`：默认 `1.10f`（建议区间 1.05 ~ 1.20）
  - `FrequencyPenalty`：默认 `0.20f`（建议区间 0.0 ~ 0.5）
  - `PresencePenalty`：默认 `0.10f`（建议区间 0.0 ~ 0.3）
  - `PenaltyCount`：默认 `128`（覆盖最近 128 个 Token 的记忆窗口）
- 在 `MafChatService.cs` 初始化及配置热重载时，将配置注入 `ChatOptions`。
- 在 `LocalLlamaChatClient.cs` 中，采样管线严格绑定以上配置，使底层采样 logits 具备即时自抑制能力。

### 4.2 第 2 层：流式重复熔断器 (RepetitionCircuitBreaker)
设计轻量级、零分配的流式检测守卫 `RepetitionCircuitBreaker`：
- **滑动窗口机制**：维护最近 `2048` 字符的滑动文本窗口。
- **算法 1：连续周期性文本块检测 (Consecutive Periodic Chunk Detection)**
  - 在尾部滑动检测是否存在长度 $L \ge 16$ 的文本子块连续重复出现 $\ge 3$ 次（如形如 $P + P + P$ 的循环短语）。
- **算法 2：行级别/句子级哈希特征指纹检测 (Patterned Sentence Repetition Detection)**
  - 提取长度 $\ge 18$ 的长句子，若在窗口内已重复出现 $\ge 3$ 次，且尾部再次出现相同句子，立即触发熔断。
- **熔断动作**：
  - 截断当前缓冲区，保留首次和二次出现的有效内容，消除冗余尾巴。
  - 记录 `[CircuitBreaker] 检测到模型生成病态循环，已执行安全熔断` 警告日志。
  - 退出生成循环，平滑完成流式响应，避免客户端挂起或无限刷屏。

### 4.3 第 3 层：多轮思考标签融合与重试退避 (Thought Sanitizing & Retry Backoff)
- `ChatToolCallingHelper.SanitizeAssistantResponse` 强化：
  - 对流式过程中产生的多段 `<think>...</think>` 进行聚类合并，仅保留结构清晰的有效思考或平滑融合。
  - 单次对话中同类工具连续无果重试上限锁定为 2 次，超出后直接基于已有线索生成结论并提示用户。

### 4.4 第 4 层：同类门禁与防回归测试 (Regression Test Suite)
- 在 `tests/ReferenceRAG.Host.Tests/AgentOptimizationAndMafPatternsTests.cs` 中建立完备的单元测试：
  - **Test Case 1**：模拟病态重复长文本流（例如连续 3 轮重复相同业务段落），断言熔断器在第 3 轮重复时毫秒级切断。
  - **Test Case 2**：正常包含重复词（如排比句、对仗诗词、自然语法助词）的长文本流，断言不发生误杀。
  - **Test Case 3**：`LocalLlamaChatClient` 采样管线参数装配断言（确保 `RepeatPenalty`、`FrequencyPenalty`、`PresencePenalty` 均被正确绑定）。
  - **Test Case 4**：配置序列化/反序列化及热重载断言。

---

## 5. 预期效果与评估指标

| 指标 | 优化前 | 优化后 |
| :--- | :--- | :--- |
| **死循环发生率** | 在长列表/多场景任务中易被诱发（~15%） | 采样惩罚结合流式熔断后逼近 **0%** |
| **死循环损耗 Token 数量** | 固态耗尽 **4096** Tokens | 熔断器在第 3 次重复时阻断，损耗控制在 **< 300** Tokens（降低 92%+ 算力浪费） |
| **端侧 CPU/GPU 挂起时间** | 持续发热生成 1~3 分钟 | **< 3 秒** 迅速熔断并完成响应 |
| **用户体验** | 屏幕被十几个相同场景狂刷污染 | 优雅展示前部有效信息并平滑收尾 |
