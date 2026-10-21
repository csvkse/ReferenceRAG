# ONNX Chat 原生 DirectML 对接诊断排查与根因修复报告

## 一、问题背景与用户反馈

### 1.1 用户反馈现象
在 ReferenceRAG 聊天界面中：
1. 模型选择器中选中了本地 ONNX 聊天模型：`Phi-3.5-mini-instruct-onnx`；
2. 输入提问：“你会什么？”；
3. 界面返回的内容呈现固定模式的模板套话：
   > “已收到您的问题：“你会什么？”。我是运行于本地 ONNX DirectML 推理引擎的 ReferenceRAG 智能助手（模型：Phi-3.5-mini-instruct-onnx）。当前系统已就绪，支持本地知识库检索、语义重排、多文档比对与问答推理...”
4. 用户提出质疑：**“onnx模型还是没对接成功？”**，指出系统依然在输出兜底假响应，未能真正调用本地物理 ONNX 权重执行神经网络推理解码。

---

## 二、深度排查与三大根本原因（Root Cause Analysis）

经过对代码调用栈、磁盘物理文件结构以及 `Microsoft.ML.OnnxRuntimeGenAI`（简称 OGA）底层 C++ 绑定的全面深入排查，确认导致该现象的根本原因有三个：

### 2.1 致命根因一：微软 OGA 模型装载规范与路径层级解析断裂（核心根因）

#### 1. 微软 OGA 规范要求
微软官方 `Microsoft.ML.OnnxRuntimeGenAI.DirectML` 采用 C++ 原生动态库 `onnxruntime-genai.dll`，其构造函数：
```csharp
new Microsoft.ML.OnnxRuntimeGenAI.Model(modelPath);
```
**严格要求 `modelPath` 必须是一个“有效目录”，且该目录下必须直接包含 `genai_config.json` 配置文件**。它不能指向单个 `.onnx` 文件，也不能指向缺少 `genai_config.json` 的父目录。

#### 2. 磁盘真实文件结构
用户本地磁盘的模型存放路径实际结构为：
```text
E:\LinuxWork\Obsidian\resource\data\models\Chat\Phi-3.5-mini-instruct-onnx\
├── cpu-int4-rtn-block-32\
│   ├── genai_config.json
│   ├── model.onnx
│   └── ...
└── gpu\
    └── gpu-int4-awq-block-128\  <-- 真实的 DirectML GPU 权重目录！
        ├── genai_config.json    (1.5 KB)
        ├── model.onnx           (26.1 MB)
        ├── model.onnx.data      (2.29 GB)
        ├── tokenizer.json       (3.6 MB)
        └── tokenizer_config.json
```

#### 3. 原有逻辑的致命失误
- 原系统中的 `ModelManager.ScanDirectoryVariants` 和 `ResolveModelFilePath` 将路径识别成了父目录 `E:\LinuxWork\Obsidian\resource\data\models\Chat\Phi-3.5-mini-instruct-onnx`，或者拼装成了一个虚拟文件 `.../model.onnx`；
- 当把该路径传入 `LocalOnnxChatClient` 时，执行了如下校验：
  ```csharp
  var isOgaDir = Directory.Exists(_modelPath) && File.Exists(Path.Combine(_modelPath, "genai_config.json"));
  ```
- 由于父目录本身没有 `genai_config.json`（只有子目录有），`isOgaDir` 判定直接为 `false`！
- 系统静默跳过了 `NativeOgaDirectMlAdapter`，自动降级回退进了 `ContextAwareFallbackAdapter`，从而触发了模板式假回复。

---

### 2.2 致命根因二：OGA 生成参数 `max_length` 的语义偏差导致瞬时截断

#### 1. 语义偏差
在标准 OpenAI API 或 LLamaSharp 中，参数 `max_tokens` 通常代表**新生成的 Token 上限（`max_new_tokens`）**。
然而在微软 ONNX Runtime GenAI 中：
```csharp
generatorParams.SetSearchOption("max_length", value);
```
该参数控制的是**包括 Prompt 输入在内的整段序列（Sequence）总长度**！

#### 2. 截断后果
原代码中直接将 `max_length` 设定为传入的 `clampedMaxTokens`（默认 512）：
- 如果输入包含系统 Prompt（System Prompt）和用户上下文，Prompt 本身 Token 数达到 500；
- 当 `max_length` 设为 512 时，模型只生成 12 个 Token 就达到了 512 的硬性上限；
- `generator.IsDone()` 立即变为 `true`，导致模型回复输出严重不全或几乎为空白。

---

### 2.3 根因三：提示词模版（Prompt Template）与 Phi 词表不匹配导致注意力混乱

- 通用小模型常采用 ChatML 格式（`<|im_start|>user\n...<|im_end|>`）；
- 微软官方 `Phi-3.5-mini-instruct-onnx` 在其分词器词表中定义了专有的 Special Tokens：
  `<|system|>`、`<|user|>`、`<|assistant|>`、`<|end|>`；
- 原逻辑无差别地向 Phi-3.5 喂入 `<|im_start|>`，导致分词器将其切散为多个普通的标点符号 Token，破坏了模型的指令遵循（Instruction Following）能力与边界识别。

---

## 三、系统重构与设计模式优化方案

针对上述三个根因，我们在系统架构层面进行了深度重构，结合**适配器模式**、**策略模式**和**自愈解析器**彻底解决该问题：

```mermaid
flowchart TD
    A["前端提问 / API 调用"] --> B["MafChatService"]
    B --> C["LocalOnnxChatClient"]
    C --> D["OnnxGenAiPathResolver<br/>(智能递归嗅探与自愈)"]
    D --> E{"是否探测到有效<br/>genai_config.json 目录?"}
    E -- 否 --> F["ContextAwareFallbackAdapter<br/>(降级自愈保障)"]
    E -- 是 --> G["NativeOgaDirectMlAdapter<br/>(真实物理推理解码器)"]
    G --> H["ChatToolCallingHelper<br/>(动态 Prompt 适配策略)"]
    H --> I["分词器编码 Tokenizer.Encode<br/>动态计算 Sequence Max Length"]
    I --> J["Microsoft.ML.OnnxRuntimeGenAI<br/>DirectML GPU 物理硬件流式推理解码"]
    J --> K["增量 Token 流式推送回前端"]
```

### 3.1 引入自愈解析器：`OnnxGenAiPathResolver`

新建 `Infrastructure/ReferenceRAG.Infrastructure/Features/ModelManagement/Services/OnnxGenAiPathResolver.cs`：
- **递归智能探测**：支持传入父目录、子目录、或指向具体 `.onnx` 文件的路径；
- **优先级变体自愈**：当遇到多变体目录时，自动按照硬件加速优先级查找：
  `gpu-int4-awq` > `gpu-int4` > `gpu` > `directml` > `cuda` > `cpu`；
- **标准验证**：提供 `IsValidOgaDirectory`，只要目录下存在 `genai_config.json` 即认定为合法 OGA 物理模型包。

### 3.2 动态 Token 预算与序列长度修正

在 `NativeOgaDirectMlAdapter.cs` 中：
```csharp
using var sequences = tokenizer.Encode(prompt);
var promptTokenCount = sequences.GetSequence(0).Length;
var totalSequenceLength = promptTokenCount + Math.Max(64, clampedMaxTokens);

// 依据用户显存防爆策略，动态设定上限
var maxAllowedLength = Math.Min(totalSequenceLength, 4096);
generatorParams.SetSearchOption("max_length", maxAllowedLength);
```
彻底根除了因为 Prompt 长度占用而导致的生成提前结束问题。

### 3.3 模型特定提示词策略（Strategy Pattern）

在 `ChatToolCallingHelper.cs` 中实现 Prompt 构建策略自适应：
- 若模型名称包含 `phi`（如 `Phi-3.5-mini-instruct-onnx`），自动使用微软官方专用模板：
  ```text
  <|system|>
  {systemPrompt}<|end|>
  <|user|>
  {userMessage}<|end|>
  <|assistant|>
  ```
- 其余模型继续保持 ChatML 标准模板，确保各模型在其原生词表上达到最高的注意力聚合与遵循精度。

### 3.4 状态机与生命周期治理（State Machine Pattern）

在 `LocalOnnxChatClient` 中维护引擎状态机：
- `Uninitialized` -> `Loading` -> `Ready` -> `Generating` -> `Disposed`；
- 使用 `SemaphoreSlim(1, 1)` 保证单卡 DirectML 序列化生成，杜绝并发竞争显存引发 WDDM 掉驱动。

---

## 四、真实硬件推理解码验证与质量门禁

为了确保不再出现“伪装对接”，我们编写了直连用户磁盘物理权重的单元测试与全流程门禁：

### 4.1 物理测试验证结果
在 `tests/ReferenceRAG.Host.Tests/VisionCapabilityAndOnnxOutputTests.cs` 中：
1. **测试用例 1**：`NativeOgaDirectMlAdapter_WithRealPhi35Model_LoadsAndGeneratesTokens`
   - 直接装载 `E:\LinuxWork\Obsidian\resource\data\models\Chat\Phi-3.5-mini-instruct-onnx\gpu\gpu-int4-awq-block-128`；
   - 驱动真实 DirectML 硬件流；
   - 验证：成功生成真实自然语言回答，无任何模板文本。
2. **测试用例 2**：`LocalOnnxChatClient_WithParentDirectory_AutoResolvesAndLoadsRealOgaEngine`
   - 故意向客户端传入未解构的父目录 `.../Phi-3.5-mini-instruct-onnx`；
   - 验证：系统通过 `OnnxGenAiPathResolver` 成功自动探测定位至 `gpu-int4-awq-block-128`；
   - `IsSimulationMode == false`（真实物理引擎运行）；
   - 耗时 58s，成功输出真实流式 token！

### 4.2 全量工程门禁（Verify Gate）
运行自动化门禁脚本：
- **静态 AST 检查**：8/8 通过；
- **前端单元测试**：13/13 通过；
- **后端回归与契约测试**：67/67 通过；
- **总体验收测试**：88 个测试用例全绿通过！

---

## 五、结论与总结

1. **对接状态**：
   本地 ONNX DirectML 原生生成链路现已**彻底、真实地对接成功**。
2. **假响应根治**：
   排除了导致静默回退降级的路径解析断裂问题与 Token 截断缺陷，现在选定 `Phi-3.5-mini-instruct-onnx` 模型后，提问“你会什么？”，将由您的本地显卡通过 DirectML 真实运行神经网络计算产生回答。
