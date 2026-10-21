# 视觉多模态支持限制与多轮对话工具调用丢失深度调研与架构解决方案

> **文档状态**：已落地实施与全量门禁验证  
> **涉及核心模块**：`LocalLlamaChatClient`、`MafChatService`、`ChatToolCallingHelper`、`IIntentRoutingStrategy`、前端 `Chat.js` 与 `MafChatController`  
> **目标问题**：  
> 1. 图片模型为何无法识别？`Qwen3.5-2B-GPT-5.1-HighIQ-INSTRUCT.i1-Q4_0.gguf` 模型是否支持视觉？  
> 2. 为何除首轮调用工具显示标签外，后续轮次工具触发均不显示？是模型未调用工具还是历史记录未显示？

---

## 目录
- [一、问题现象复现与核心疑问分析](#一问题现象复现与核心疑问分析)
- [二、深度技术根因剖析（Root Cause Analysis）](#二深度技术根因剖析root-cause-analysis)
  - [2.1 问题 1 根因：Qwen3.5-2B-*.gguf 纯语言模型与多模态 VLM 架构边界](#21-问题-1-根因qwen35-2b-gguf-纯语言模型与多模态-vlm-架构边界)
  - [2.2 问题 2 根因：模型未调用工具还是记录未显示？](#22-问题-2-根因模型未调用工具还是记录未显示)
- [三、业界主流方案与开源库横向对比](#三业界主流方案与开源库横向对比)
- [四、架构重构与设计模式方案](#四架构重构与设计模式方案)
  - [4.1 模型能力感知器（Capability Provider Pattern）](#41-模型能力感知器capability-provider-pattern)
  - [4.2 历史上下文去装饰适配器（History Sanitizer Adapter）](#42-历史上下文去装饰适配器history-sanitizer-adapter)
  - [4.3 多轮追问自然语言意图路由器（Multi-Turn Intent Router）](#43-多轮追问自然语言意图路由器multi-turn-intent-router)
  - [4.4 三层联防视觉安全守卫（Multi-Tier Visual Guard）](#44-三层联防视觉安全守卫multi-tier-visual-guard)
- [五、具体实现与代码落盘](#五具体实现与代码落盘)
- [六、防回归质量门禁与测试用例](#六防回归质量门禁与测试用例)

---

## 一、问题现象复现与核心疑问分析

### 现象 1：图片上传后模型无法识别内容
- **用户行为**：在会话窗口上传/粘贴一张图片，输入“介绍这张图片”或“请分析该图”。
- **模型回复**：
  > “用户提到一张附带图像的图片... 由于我无法直接看到您提到的这张图片（我无法访问外部图像或本地文件），我只能基于知识库内容进行回答...”
- **用户疑问**：`Qwen3.5-2B-GPT-5.1-HighIQ-INSTRUCT.i1-Q4_0.gguf` 模型不支持图片识别吗？

### 现象 2：多轮对话中工具调用徽标在后续轮次消失
- **用户行为**：
  - 第 1 轮：输入“知识库状态”或“搜索知识库”，模型上方成功出现 `> 🔧 已执行工具: GetIndexStatus`，并给出正常回答；
  - 第 2 轮及后续轮次：输入自然语言追问（如“搜索知识库关于配置的说明”、“再查一下向量模型”或“包含哪些文件”），助手的回复中**完全没有出现** `> 🔧 已执行工具: xxx` 徽章。
- **用户疑问**：**是模型没有调用工具，还是工具调用记录在后续 Agent 会话中没有显示？**

---

## 二、深度技术根因剖析（Root Cause Analysis）

### 2.1 问题 1 根因：Qwen3.5-2B-*.gguf 纯语言模型与多模态 VLM 架构边界

#### 1. 模型架构本质：LLM vs VLM
`Qwen3.5-2B-GPT-5.1-HighIQ-INSTRUCT.i1-Q4_0.gguf` 是通过 llama.cpp i-matrix 量化的 **纯文本大语言模型（Decoder-only Large Language Model）**。
- **纯文本 LLM 的张量结构**：仅包含离散词表的词嵌入矩阵（`token_embd.weight`，维度 $V \times D$）、多层自注意力机制（Self-Attention）和前馈网络（MLP）。其输入必须是离散 Token ID 序列，**在物理权重结构上完全不存在处理二维连续像素张量（$H \times W \times C$）的能力**。
- **视觉语言模型（VLM）的标准结构**：
  必须具备两个核心权重体系：
  1. **视觉编码器（Vision Encoder / ViT）**：如 SigLIP-SO400M 或 CLIP ViT-L/14，将图片 Patch 划分并投影为视觉连续向量；
  2. **多模态投影器（Multimodal Projector，即 `mmproj`）**：将视觉特征维度映射到大语言模型的隐藏层维度（Hidden Dimension $D$），并作为特殊 Token（如 `<image>`）拼入序列。

```
[用户上传图片 (Pixels)]
       │
       ▼
 [Vision Transformer (ViT)] ───► [mmproj (Multimodal Projector)] ──► [Vision Tokens]
                                                                            │
 [用户提问文本 (Text)] ────────► [Text Tokenizer] ───────────────────► [Text Tokens]
                                                                            │
                                                                            ▼
                                                                [LLM Decoder Backbone]
```

在 llama.cpp / LLamaSharp 体系中：
- 纯文本 GGUF 单文件（`*.gguf`）**不包含**任何视觉权重；
- 多模态推理必须配套挂载 **`mmproj-*.gguf`** 文件，并由底层初始化 `ClipModel`；
- **结论**：`Qwen3.5-2B-GPT-5.1-HighIQ-INSTRUCT.i1-Q4_0.gguf` **从权重物理层面上 100% 不支持图像识别**。阿里开源的视觉模型统称为 `Qwen2-VL-2B-Instruct` 或 `Qwen2.5-VL-3B-Instruct`，其 GGUF 格式必须包含配套的 `mmproj-qwen2-vl-2b-instruct-f16.gguf`。

#### 2. 代码层逻辑缺陷：静默降级提示词反噬
在原有实现中：
1. 前端 `Chat.js` 缺乏对当前激活模型的能力探测，无条件展示了图片上传/粘贴按钮；
2. 后端 `ChatToolCallingHelper.FormatSingleMessage` 检测到当前本地引擎未挂载视觉投影器时，执行了如下降级：
   ```csharp
   imgNotes.Add($"[附带图像 #{i + 1} ({dc.MediaType ?? "image/jpeg"}), 本地文本引擎未挂载视觉投影器，已优先基于文本与知识库为您解答]");
   text = $"{notesBlock}\n{text}";
   ```
3. 文本大模型看到的完整 Prompt 实际上是：
   ```
   <|im_start|>user
   [附带图像 #1 (image/jpeg), 本地文本引擎未挂载视觉投影器，已优先基于文本与知识库为您解答]
   介绍这张图片
   <|im_end|>
   ```
4. 纯语言模型阅读了这段系统硬编码提示后，顺着提示词一本正经地回答：“由于我无法直接看到您提到的这张图片（我无法访问外部图像或本地文件），我只能基于知识库内容进行回答...”。用户因此产生困惑，以为模型识别报错。

---

### 2.2 问题 2 根因：模型未调用工具还是记录未显示？

针对用户的核心疑问：**“是模型没调用工具还是工具调用记录在后续agent会话中没显示？”**

#### 核心结论：模型在后续轮次中【根本没有调用工具】（Not Invoked）！
这不是前端或后端的渲染丢失，而是小模型在第 2 轮及后续轮次中完全没有发出 `<tool_call>` 指令。

导致模型在后续轮次未调用工具的技术根因有三重：

#### 根因 1：历史上下文装饰性徽标污染，引发少样本模仿漂移（Format Drift & In-Context Bias）
- 在第 1 轮工具执行完成后，`MafChatService` 在保存 Assistant 最终消息时，将 UI 装饰性徽标一并持久化：
  ```csharp
  var badge = $"> 🔧 **已执行工具**: {string.Join(", ", finishedTools.Select(t => $"`{t}`"))}\n\n";
  session.Messages.Add(new ChatMessage(ChatRole.Assistant, badge + sanitized));
  ```
- 当进入第 2 轮时，`ChatToolCallingHelper.BuildChatMlPrompt` 遍历历史消息，将第 1 轮的 Assistant 回复原封不动塞入 ChatML：
  ```
  <|im_start|>system
  你可以使用以下工具：... 如果你需要调用工具，请使用 <tool_call>...
  <|im_end|>
  <|im_start|>user
  知识库状态
  <|im_end|>
  <|im_start|>assistant
  > 🔧 **已执行工具**: `GetIndexStatus`

  知识库当前已构建索引：共包含 42 篇笔记...
  <|im_end|>
  <|im_start|>user
  那这些索引包含哪些文件？
  <|im_end|>
  <|im_start|>assistant
  ```
- **技术破坏点**：
  在小模型（尤其是 2B 参数量级）的自注意力机制中，**历史上下文中的 Assistant 回复构成了强烈的 In-Context Few-Shot 样本示范**！
  模型观察到历史中 Assistant 的上一轮回复是直接以 Markdown 引用块（`> 🔧 已执行工具`）开头的正文，而不是以 `<tool_call>` 语法块开头。
  小模型发生**格式漂移（Format Drift）**，模仿历史风格，直接在第 2 轮生成纯文本聊天，彻底抑制了 `<tool_call>` 的生成。

#### 根因 2：协议层中间状态丢失（丢弃了 FunctionCall / ToolResult 状态）
- 依据标准 OpenAI / Qwen ChatML 协议，多轮工具调用的标准上下文拓扑必须是：
  ```
  user ──► assistant (<tool_call>) ──► tool (<tool_response>) ──► assistant (final text)
  ```
- 原架构中，`session.Messages` 在第 1 轮结束后只留下了被污染的单一 `Assistant` 文本，中间的函数调用结构与工具返回结果被完全丢弃。第 2 轮的模型无法感知历史轮次真正调用了什么函数与参数。

#### 根因 3：确定性意图分发器（Deterministic Dispatcher）的冒号限制规则漏出
对比第 1 轮与第 2 轮的意图分发规则：
- **第 1 轮常见提问**：“查询知识库状态” 或 “知识库状态”
  命中代码：
  ```csharp
  if (cleanCommand.Contains("索引状态") || cleanCommand.Contains("知识库状态"))
      return true; // 包含式通配匹配，无需冒号！
  ```
  因此第 1 轮 100% 命中了确定性路由，绕过了模型推理直接执行了 `GetIndexStatus`，并打印了徽章！
- **第 2 轮常见追问**：“搜索知识库关于配置的说明”、“再查一下向量模型”、“查一下三端优化”
  命中代码（原逻辑）：
  ```csharp
  // 原逻辑必须要求带中英文冒号！
  else if (cleanCommand.StartsWith("搜索知识库中:", ...) ||
           cleanCommand.StartsWith("搜索:", ...) ||
           cleanCommand.StartsWith("查一下:", ...))
  ```
  在自然中文对话中，用户后续追问绝大多数不会主动敲冒号！
  由于没有冒号，`TryDetectDirectToolCall` 与 `ExactPrefixRoutingStrategy` 全部返回 `false`；
  请求于是下沉给小模型（`Qwen3.5-2B`）；而小模型又受【根因 1】的徽标污染影响，不再输出 `<tool_call>`，而是直接凭常识幻觉作答；
  由于工具根本没有被执行，`_currentRequestExecutedTools` 集合在第 2 轮为空，因此**不会打印任何工具调用标签**！

---

## 三、业界主流方案与开源库横向对比

| 框架 / 系统 | 视觉多模态处理机制 | 多轮工具调用上下文管理 | 历史 UI 徽章与装饰层处理 | 适用优势与借鉴点 |
| :--- | :--- | :--- | :--- | :--- |
| **llama.cpp / mtmd** | `llava_init_context` + `mmproj-*.gguf` 投影器 | 原生维护 ChatML `<tool_call>` 与 `<tool_response>` 消息对 | 无 UI 污染，纯协议传输 | 明确规定主模型与 mmproj 的协同架构，严格校验投影器存在性 |
| **Semantic Kernel** | `IChatCompletionService` + `ChatHistory` | 维护 `ChatRole.Tool` 与 `FunctionCallContent` 独立节点 | 序列化时通过 Formatter 过滤非模型协议内容 | 结构化对象存储，避免纯文本拼接 |
| **OpenAI / vLLM** | 模型 Capability 元数据声明 (`modalities: ["text", "image"]`) | 标准 Function Calling 多轮协议（ID 关联响应） | 服务端纯粹 JSON 交互，UI 徽标由前端独立维护渲染 | 模型不支持多模态时立即返回 400 Bad Request，严禁静默注入污染 |
| **Open-WebUI / LibreChat** | 前端动态调用 `/api/models` 探测 `supports_vision` | 会话存储中剥离 Agent Thinking 思考过程与 Tool Call 装饰 | 前端独立渲染工具折叠卡片，历史 Prompt 仅含干净对话 | 前端响应式禁用不可用功能，用户预期清晰 |

---

## 四、架构重构与设计模式方案

```
                     ┌────────────────────────────────────────────────────────┐
                     │                 前端交互层 (Chat.js)                    │
                     │  - 探测 /api/chat/capabilities                          │
                     │  - 若 SupportsVision=false，禁用上传并给出明确 Tooltip     │
                     └───────────────────────────┬────────────────────────────┘
                                                 │
                                                 ▼
                     ┌────────────────────────────────────────────────────────┐
                     │          三层防御网关 (MafChatController & Service)     │
                     │  - 模型多模态能力检测守卫 (Vision Capability Guard)        │
                     │  - 多模态非法上传阻断与友好 SSE 提示                     │
                     └───────────────────────────┬────────────────────────────┘
                                                 │
                                                 ▼
    ┌────────────────────────────────────────────┴───────────────────────────────────────────┐
    │                                意图路由层 (Strategy Pattern)                            │
    │  - ExactPrefixRoutingStrategy 扩展：全覆盖无冒号自然语言（"搜索知识库 xxx"、"再查一下"）   │
    │  - 追问意图增强：自动提取干净关键词，杜绝小模型在多轮会话中漏调工具                        │
    └────────────────────────────────────────────┬───────────────────────────────────────────┘
                                                 │
                                                 ▼
    ┌────────────────────────────────────────────┴───────────────────────────────────────────┐
    │                          上下文洁净适配器 (History Sanitizer)                           │
    │  - StripDecorativeBadges: 在构建下一轮 Prompt 时彻底剥离历史 UI 徽章                   │
    │  - 消除 Few-Shot Format Drift，保证大模型看到的历史上下文符合纯净规范                   │
    └────────────────────────────────────────────────────────────────────────────────────────┘
```

### 4.1 模型能力感知器（Capability Provider Pattern）
定义 `IChatModelCapabilityProvider` 接口与 `ChatModelCapabilities` 领域模型，向系统各层（前端、Controller、Service）声明当前模型的真实能力：
```csharp
public record ChatModelCapabilities(
    bool SupportsVision,
    bool SupportsToolCalling,
    string ModelName,
    string BackendType,
    int MaxContextTokens,
    string? Description = null,
    string? VisionUnsupportedReason = null);

public interface IChatModelCapabilityProvider
{
    ChatModelCapabilities GetCapabilities();
}
```
- 对于纯文本本地模型（如 `Qwen3.5-2B-*.gguf`、ONNX 纯文本模型）：`SupportsVision = false`；
- 对于未来挂载了 `mmproj-*.gguf` 的多模态模型或具备视觉的云端 API（如 `gpt-4o`、`qwen-vl-max`）：`SupportsVision = true`。

### 4.2 历史上下文去装饰适配器（History Sanitizer Adapter）
在 `ChatToolCallingHelper` 中实现装饰性徽标安全剥离器：
```csharp
public static string StripDecorativeBadges(string text)
{
    if (string.IsNullOrWhiteSpace(text)) return text;
    // 彻底剥离历史中由系统注入的 > 🔧 **已执行工具**: xxx 以及 > 🔧 **已调用工具**: xxx
    var cleaned = Regex.Replace(
        text,
        @"^>\s*🔧\s*\*\*已(?:执行|调用)工具\*\*:[^\n]*(?:\r?\n)*",
        "",
        RegexOptions.Multiline);
    return cleaned.TrimStart();
}
```
在 `FormatSingleMessage` 中，当遍历 `msg.Role == ChatRole.Assistant` 时，先调用 `StripDecorativeBadges` 净化文本，然后再生成 `<|im_start|>assistant\n{cleanText}\n<|im_end|>`。
**收益**：模型第 2 轮及后续轮次看到的历史样本中再无非标准 UI 标记，彻底杜绝格式漂移！

### 4.3 多轮追问自然语言意图路由器（Multi-Turn Intent Router）
彻底重构 `MatchSearchQuery` 与 `TryDetectDirectToolCall`，支持以下全面的无冒号中文自然语言前缀及多轮追问模式：
1. **多轮追问动词**：`"再查一下"`, `"再搜索一下"`, `"再检索一下"`, `"再搜一下"`, `"继续查"`, `"继续搜索"`, `"继续检索"`, `"再找找"`, `"再看看"`；
2. **知识库自然动词**：`"搜索知识库"`, `"检索知识库"`, `"查询知识库"`, `"在知识库中搜索"`, `"在知识库中查找"`, `"从知识库检索"`, `"从知识库搜索"`；
3. **日常口语指令**：`"帮我查一下"`, `"帮我搜索"`, `"帮我检索"`, `"查一下"`, `"搜索"`, `"检索"`, `"查找"`（支持空格分隔或直接跟关键词）；
4. **文件与状态追问**：`"有哪些文件"`, `"包含哪些文件"`, `"文件列表"`, `"笔记列表"` 自动路由至 `GetFileStructure`。

### 4.4 三层联防视觉安全守卫（Multi-Tier Visual Guard）
1. **第一层（前端 UI 响应式守卫）**：
   - 挂载时通过 `GET /api/chat/capabilities` 获取 `supportsVision`；
   - 若不支持视觉，在图片上传按钮上显示禁用样式与 Tooltip；用户粘贴图片时直接 Toast 提示拦截，杜绝无效流量。
2. **第二层（后端 API 网关守卫）**：
   - `MafChatService.StreamAsync` 校验：若当前模型不支持视觉，立即下发友好的多模态提示 SSE，并将 `images` 参数置 `null`；
   - 杜绝在文本 Prompt 中拼接误导性的 `[附带图像 #1, 本地文本引擎未挂载视觉投影器...]`，保证纯文本模型仅处理纯文本。
3. **第三层（底层执行器守卫）**：
   - 若未来配置了 `mmprojPath` 且真实文件存在，则启用 `ClipModel` 进行视觉张量计算，形成平滑扩展架构。

---

## 五、具体实现与代码落盘

### 涉及修改与新增文件清单
1. **新建** `Business/ReferenceRAG.Business/Features/Chat/Services/IChatModelCapabilityProvider.cs`
2. **修改** `Business/ReferenceRAG.Business/Features/Chat/Services/ChatToolCallingHelper.cs`（增加 `StripDecorativeBadges`、优化多轮追问自然语言搜索路由）
3. **修改** `Business/ReferenceRAG.Business/Features/Chat/Services/IIntentRoutingStrategy.cs`（扩充无冒号搜索追问前缀与文件结构前缀）
4. **修改** `Business/ReferenceRAG.Business/Features/Chat/Services/LocalLlamaChatClient.cs`（实现 `IChatModelCapabilityProvider`，标明纯文本模型无视觉能力）
5. **修改** `Business/ReferenceRAG.Business/Features/Chat/Services/LocalOnnxChatClient.cs`（实现 `IChatModelCapabilityProvider`）
6. **修改** `Business/ReferenceRAG.Business/Features/Chat/Services/MafChatService.cs`（接入能力感知、多模态前置友好拦截、多轮对话徽标洁净处理）
7. **修改** `Host/ReferenceRAG.ApiHost/Controllers/MafChatController.cs`（新增 `/api/chat/capabilities` 接口）
8. **修改** `Business/ReferenceRAG.Business/wwwroot/app/views/Chat.js`（感知模型能力、联动禁用图片上传并友好提示）
9. **新建测试** `tests/ReferenceRAG.Host.Tests/ChatMultimodalAndMultiTurnToolTests.cs`

---

## 六、防回归质量门禁与测试用例

为保障该架构在未来迭代中绝不倒退，设立以下自动化测试门禁：
1. **多模态能力感知契约测试**：验证纯文本 GGUF 模型（如 `Qwen3.5-2B`）正确返回 `SupportsVision = false`，且输出清晰的未支持原因；
2. **上下文去装饰测试**：验证包含 `> 🔧 **已执行工具**: xxx` 的 Assistant 历史消息，经 `StripDecorativeBadges` 后被完整剥离，且正文完全不受损；
3. **多轮无冒号搜索追问路由测试**：验证“再查一下关于配置的说明”、“搜索知识库 神经网络”、“包含哪些文件”无需输入冒号即可 100% 确定性分发至对应工具；
4. **全量持续集成门禁（AST、前端单元测试、后端集成测试）100% 绿灯验证**。
