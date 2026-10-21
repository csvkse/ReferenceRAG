# 模型视觉投影器（mmproj）仓库存在性探测与独立补齐下载架构设计

## 1. 用户需求与核心痛点背景

### 1.1 问题提出
用户针对已下载的 GGUF 大模型（例如 `Qwen3.5 4B Instruct`，本地已存在约 2.52 GB 的 `Q4_K_M.gguf` 文本量化权重）提出两个核心疑问与需求：
1. **能单独下载视觉投影器（mmproj）吗？**
   本地已经拥有庞大的主模型权重文件，是否可以只下载数百 MB（约 600~700 MB）的伴生视觉投影器（`mmproj-F16.gguf`），而无需重新下载 3.2 GB 的完整双文件包？
2. **能判断仓库是否存在投影器吗？**
   在下载前或下载后，系统能否自动探测远端模型仓库（Hugging Face / ModelScope）是否存在配套的视觉投影器，向用户明确呈现当前模型是否具备多模态能力、远端有哪些精度的投影器可选？

### 1.2 现状盲区分析
- **盲区一：下载粒度过粗**。此前系统将 `Q4_K_M` 变体与伴生 `mmproj` 打包成联合下载选项（展示为 `Q4_K_M (含视觉模型)`，合并体积约 3.4 GB）。若用户此前单独下载过文本模型，前端变体抽屉中没有暴露单独下载 `mmproj` 的选项。
- **盲区二：多模态视觉就绪状态不可见**。在已下载模型列表中，即使本地目录缺少 `mmproj`，界面也仅显示 `✓ Q4_K_M (2.52 GB)`，用户无法获知该模型是否支持图像识别、本地是否缺少视觉组件。
- **盲区三：命名启发式识别盲区**。传统正则表达式匹配 `(?:-vl\b|_vl\b|vision|llava)`，而阿里巴巴开源的 `Qwen/Qwen3.5-4B-Instruct-GGUF` 等新一代原生端侧多模态模型未带有 `-vl` 后缀，纯靠正则会导致探测漏网。

---

## 2. 头脑风暴与可行性深度剖析

### 2.1 仓库投影器存在性探测（Feasibility & Performance）
- **可行性**：100% 可行。
- **协议开销与延迟**：
  - Hugging Face / ModelScope 均提供轻量级 Tree API（例如 `GET https://hf-mirror.com/api/models/{repo}/tree/main`）；
  - 单次调用仅传输数十 KB JSON 数据，网络耗时通常在 80~200 ms；
  - 判定规则严密确定：直接过滤扩展名为 `.gguf` 且文件名命中 `mmproj` 的条目（如 `mmproj-F16.gguf`, `mmproj-model-f16.gguf`），无需拉取模型二进制。
- **性能优化**：
  - **内存缓存（LRU/TTL = 1小时）**：同一个模型仓库在 1 小时内探测过 Manifest 后缓存结果，避免反复访问外网；
  - **注册表先验标记（Zero-Latency）**：在内置模型注册表（`_modelRegistry`）中显式标注 `IsMultimodal = true`，应用启动时即可零耗时渲染基本视觉属性。

### 2.2 已经下载模型的视觉投影器独立下载（Bandwidth & Efficiency）
- **带宽节约对比**：
  - 传统重下：2.52 GB (LLM) + 0.67 GB (mmproj) = **3.19 GB**；
  - 独立补齐：仅需下载 **0.67 GB**，节约 **79%** 网络带宽与磁盘 I/O。
- **本地存储拓扑**：
  - 投影器文件直接存放于主模型目录下：`models/Chat/Qwen3.5-4B-Instruct-GGUF/mmproj-F16.gguf`；
  - `LocalLlamaChatClient` 启动时通过目录自愈探针检测同目录下的 `*mmproj*.gguf`，即可无缝激活视觉多模态能力。

---

## 3. 设计模式优化落地

### 3.1 适配器模式（Adapter Pattern）
通过 `IModelSourceAdapter`（如 `HuggingFaceSourceAdapter`）统一封装不同远端源（Hugging Face、ModelScope、本地离线缓存源）的元数据请求：
- 适配输出标准 `ModelManifest`，内置 `GetMmprojFiles()` 提取器；
- 适配输出 `ProjectorCapabilityInfo`，解耦具体源的 API 数据结构差异。

### 3.2 策略模式（Strategy Pattern）
- **伴生匹配策略（`GgufMultimodalCompanionStrategy`）**：
  - 规则一（家族亲和度）：提取 `qwen3.5`, `qwen2.5`, `llava`, `gemma-4` 等家族标识，优先匹配同一架构的投影器；
  - 规则二（精度优先）：按 `F16 -> FP16 -> BF16 -> Q8_0 -> Q4_K_M` 顺序选择，防止视觉特征由于过度量化而发生显著漂移与伪影；
  - 规则三（体积兜底）：同等条件下选取体积最大的投影器。
- **下载模式策略（Download Mode Strategy）**：
  - 模式 A（`CombinedCompanion`）：初次下载，主模型与投影器联合打包下载；
  - 模式 B（`StandaloneProjector`）：已存在主模型时，仅下载投影器并增量装配。

### 3.3 状态机模式（State Machine Pattern）
模型视觉就绪度流转状态机（`ModelProjectorStateMachine`）：

```
                  ┌──────────────────────────────────────────────┐
                  │                NotApplicable                 │ (纯文本模型，如 Bge-M3)
                  └──────────────────────┬───────────────────────┘
                                         │ 识别为多模态架构
                                         ▼
                  ┌──────────────────────────────────────────────┐
                  │                   Missing                    │ (本地仅有主文本模型，缺少投影器)
                  └──────────────────────┬───────────────────────┘
                                         │ 用户点击「补全视觉组件」
                                         ▼
                  ┌──────────────────────────────────────────────┐
                  │                 Downloading                  │ (正在下载 mmproj 投影器)
                  └──────────────────────┬───────────────────────┘
                                         │ 传输校验完毕
                                         ▼
                  ┌──────────────────────────────────────────────┐
                  │                    Ready                     │ (主模型 + 投影器均就绪，激活多模态)
                  └──────────────────────────────────────────────┘
```

---

## 4. 业界主流方案对比

| 解决方案 | 远端探测机制 | 独立下载投影器能力 | 本地就绪状态与交互 |
| :--- | :--- | :--- | :--- |
| **Ollama** | 远端 Manifest JSON 逐层哈希检查 | 分层拉取（底层支持，但 CLI 屏蔽了细节，无法单独指定） | 统一 Tag 呈现，无独立投影器操作入口 |
| **LM Studio** | 正则匹配 HuggingFace 仓库文件树中的 `mmproj-*.gguf` | **完全支持**。变体抽屉中展示独立的 "Vision Projector" 选项 | 卡片显示 "Vision Ready" 或 "Missing Projector" 徽章，提供补齐入口 |
| **llama.cpp (原生)** | 无自动探测，依赖用户命令行参数 `--mmproj` | 用户手动从 HF 下载单文件放入文件夹 | 启动时尝试加载，若缺少则降级为纯文本并警告 |
| **Jan.ai** | Cortex 引擎探测仓库清单 | 支持单独下载 cortex mmproj 插件 | 提示安装多模态视觉插件 |
| **ReferenceRAG (本项目)** | **毫秒级 Tree API 探针 + 注册表先验 + 社区免转源回退** | **一键独立补齐 (`POST /projector/download`) + 变体列表独立暴露** | **表格直观显示 `[📷 视觉就绪: mmproj-F16]` / `[⚠️ 缺少视觉投影器] ⚡ 补全`** |

---

## 5. API 契约与架构设计

### 5.1 数据模型扩展

#### `ModelInfo`（模型核心信息）新增多模态状态字段：
```csharp
public bool IsMultimodal { get; set; }
public bool HasMmprojLocally { get; set; }
public string? LocalMmprojFileName { get; set; }
public long LocalMmprojSizeBytes { get; set; }
public bool RemoteProjectorAvailable { get; set; }
public string? RemoteProjectorFileName { get; set; }
public long RemoteProjectorSizeBytes { get; set; }
public string ProjectorStatus { get; set; } = "not_applicable"; // "ready" | "missing" | "downloading" | "not_applicable"
```

#### `ModelDownloadOptions` 新增投影器选项集合：
```csharp
public bool HasMmproj => MmprojOptions.Count > 0;
public List<OnnxFileOption> MmprojOptions { get; set; } = new();
public OnnxFileOption? RecommendedMmproj { get; set; }
```

### 5.2 接口端点契约

1. **投影器状态与远端探测**：
   - `GET /api/models/chat/{modelName}/projector/status`
   - 返回本地是否就绪、远端仓库是否存在投影器、推荐的投影器文件名及大小、所有可用投影器列表。
2. **单独下载视觉投影器**：
   - `POST /api/models/chat/{modelName}/projector/download`
   - 请求体（可选）：`{ "projectorPath": "mmproj-F16.gguf", "repository": "Qwen/Qwen3.5-4B-Instruct-GGUF" }`
   - 动作：仅拉取指定的投影器文件写入该模型目录，驱动状态机由 `missing` 转为 `ready`。

---

## 6. 前端 UI 交互设计

1. **已下载模型变体栏展示**：
   - 若 `row.isMultimodal && row.hasMmprojLocally`：
     - 徽章：`[📷 视觉就绪 (mmproj-F16.gguf)]`（绿色标签）；
   - 若 `row.isMultimodal && !row.hasMmprojLocally`：
     - 徽章：`[⚠️ 缺少视觉投影器]`（橙色警告）；
     - 按钮：`[⚡ 补全视觉组件]`（点击直接触发投影器单独下载）；
2. **变体下载对话框（+ 下载新变体）**：
   - 在已有的变体列表下方，增加 **"📷 视觉投影器组件 (mmproj)"** 独立选择卡片；
   - 用户可以单独勾选 `mmproj-F16.gguf (672 MB)` 进行下载，无需重新拉取主模型。
