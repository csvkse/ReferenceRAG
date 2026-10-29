# 聊天模型回退 CPU 模式深度调研与 Vulkan 核显加速架构演进方案

## 1. 现场问题现象与背景调研

在性能测试看板（`http://10.126.1.60:5123/performance`）及模型运行状态接口（`GET /api/Models/runtime-status`）中：
* **嵌入模型（bge-m3）**：正常启用了 GPU 加速，全量 24/24 层卸载至 `AMD Vulkan GPU (AMD Radeon GPU / APU)`。
* **重排模型（bce-reranker-base_v1）**：正常启用了 GPU 加速，全量 12/12 层卸载至 `AMD Vulkan GPU (AMD Radeon GPU / APU)`。
* **聊天模型（Qwen3.5-0.8B-Instruct-GGUF）**：却意外回退到了 **纯 CPU 运行模式**（`GpuLayers = 0`, `IsGpuMode = false`, `ExecutionDevice = "CPU"`），导致单次生成流式响应时延暴增（TTFT 延迟达数十秒，StreamChat 耗时超过 24,000ms）。

宿主机物理环境画像：
* **操作系统**：Ubuntu 24.04.5 LTS（Docker 容器运行环境）
* **CPU**：16 核心 AMD Ryzen 处理器
* **GPU 设备**：AMD Radeon APU / 集成核显（Linux 字符设备节点 `/dev/dri/renderD128`，厂商 `GpuVendor.Amd`）
* **显存特性**：UMA（统一内存架构），无 PCIe 独立板载 VRAM，`TotalVramBytes` 探针采集结果为 `0 / null`。

---

## 2. 根因深度剖析（Root Cause Analysis）

通过反向追踪模型切换、加载生命周期与硬件探测调用链路，发现了系统内两套“显卡感知与显存评估算法”的代码分叉与断层：

### 2.1 嵌入与重排模型为何能成功走核显 Vulkan 加速？
嵌入和重排模型使用的是 `GpuOffloadCalculator`（位于 `ReferenceRAG.Infrastructure.Features.HardwareAcceleration`）：
```csharp
// GpuOffloadCalculator.cs
var hasGpu = hwContext.HasNvidiaGpu || hwContext.HasDirectX12 || (hwContext.PrimaryGpu != null && hwContext.PrimaryGpu.Vendor != GpuVendor.Unknown);

var totalVram = hwContext.PrimaryGpu?.TotalVramBytes ?? 0;
var isIntegratedGpu = totalVram == 0 && hwContext.PrimaryGpu?.Vendor is GpuVendor.Intel or GpuVendor.Amd;

// 对于 Intel / AMD 核显 (共享系统内存，无独立物理显存)，按动态共享内存规划（预估 4GB 供核显 Vulkan 调度）
if (isIntegratedGpu)
{
    totalVram = 4UL * 1024 * 1024 * 1024;
}
```
`GpuOffloadCalculator` 充分考虑了 Linux AMD/Intel 核显的特殊性：虽然 `TotalVramBytes == 0`，但识别出为 `Amd/Intel` 厂商，因此自动分配了 4GB 共享系统内存作为虚拟显存池，并返回 `GpuLayers = -1`。

### 2.2 聊天模型为何被误判并回退到纯 CPU？
在切换聊天模型（`ModelManager.SwitchChatModelAsync`）和初始化客户端（`MafChatService.cs`）时，使用的是后来独立编写的 `ChatPerformancePlanner`：
```csharp
// ChatPerformancePlanner.cs (修复前缺陷代码 - 第 191 行)
var primaryGpu = hwContext.PrimaryGpu;
var totalVram = primaryGpu?.TotalVramBytes ?? 0;

// 缺陷处：强校验 totalVram > 0，且只认 Nvidia GPU 或 Windows DirectX12！
var hasGpuSupport = request.UseGpu && totalVram > 0 && (hwContext.HasNvidiaGpu || hwContext.HasDirectX12);
```
此判断逻辑存在严重三连误判：
1. **强制要求 `totalVram > 0`**：Linux 环境下的 AMD APU 核显由系统内存统一动态划拨，探测器汇报的 `TotalVramBytes` 为 `0 / null`，直接被过滤；
2. **只校验 `HasNvidiaGpu` 与 `HasDirectX12`**：DirectX 12 仅存在于 Windows 10/11，在 Linux Ubuntu 容器中为 `false`；且当前显卡是 AMD 而非 NVIDIA，`HasNvidiaGpu` 亦为 `false`；
3. **完全无视 Vulkan 算力与核显**：未复用 `isIntegratedGpu` 规则，导致在 Linux AMD APU 上 `hasGpuSupport` 恒为 `false`。

判定 `hasGpuSupport == false` 后，`ChatPerformancePlanner` 直接进入纯 CPU 分支：
* 分配 `GpuLayers = 0`，`ContextSize = 4096`，`BatchSize = 512`；
* 保存至 `config.json` 中的 `Chat.LocalGguf.GpuLayers = 0`；
* `LocalLlamaChatClient` 判断 `_gpuLayers == 0` 时，直接绕过 `LlamaNativeInitializer`（不加载 Vulkan 动态库），直接将 `GpuLayerCount = 0` 传入 llama.cpp，最终被迫以 CPU 软解算力运行。

---

## 3. 业界主流方案与调研借鉴

在边缘端、轻量云服务器和工作站中，AMD Ryzen APU（如 Radeon 780M/680M/Vega）与 Intel 酷睿核显（Iris Xe/UHD）极为普及。针对此类无独立 VRAM 的统一内存（UMA）设备，业界主流推理引擎的设计准则如下：

### 3.1 Ollama (`gpu/gpu.go` & `llm/server.go`)
* **多级别驱动扫描**：不以“是否有物理显存”作为 GPU 是否可用的前提，而是按优先级扫描动态驱动：
  1. NVIDIA CUDA / NVML
  2. AMD ROCm / HIP（针对专业独显）
  3. Vulkan（针对 AMD APU 核显、Intel 核显、跨平台通用 GPU）
  4. Apple Metal
  5. 纯 CPU / AVX2
* **UMA 内存预算计算**：若检测到设备为 UMA（统一内存），从操作系统可用物理内存（Available Physical RAM）中安全划拨配额（例如 `max(4GB, 50% * free_ram)`）作为显存预算，用于分配 `-ngl`（GPU Offload 层数）。

### 3.2 llama.cpp 原生后端 (`ggml-vulkan.cpp`)
* 在 Vulkan 模式下，设备类型枚举包含 `VK_PHYSICAL_DEVICE_TYPE_INTEGRATED_GPU` 与 `VK_PHYSICAL_DEVICE_TYPE_DISCRETE_GPU`；
* 无论是核显还是独显，只要 Vulkan 驱动就绪（如 Linux 下的 `radv` / `amdgpu` 或 `/dev/dri/renderD*`），均能创建 Compute Pipeline 执行全量张量运算。
* 在小参数量对话模型（如 Qwen2.5/3.5 0.8B~1.5B）上，AMD 780M/680M 核显的 Vulkan 速度通常是多核 CPU 软解的 5~12 倍。

### 3.3 ONNX Runtime (`ExecutionProvider` 策略)
* Windows 优先选用 DirectML，Linux 优先选用 CUDA / ROCm / OpenVINO / Vulkan；
* 当无法探测到 PCIe 物理显存时，回退到共享内存估算模式，而非直接放弃硬件加速。

---

## 4. 当前系统已有基础设施与复用分析

经代码库调研，ReferenceRAG 系统本身**早已具备完整的 Vulkan 核显运行库与基础设施**，仅因规划器中的条件遗漏导致 Chat 模型未能接通：

| 现有基础设施组件 | 所在模块 | 能力与状态 |
| :--- | :--- | :--- |
| `GpuOffloadCalculator` | `ReferenceRAG.Core.Features.HardwareAcceleration` | **已实现且经过线上验证**：自动识别 AMD/Intel 核显，自动分配 4GB 虚拟显存配额，支撑了 Embedding 和 Rerank 的全量卸载 |
| `LlamaNativeInitializer` | `ReferenceRAG.Core.Features.HardwareAcceleration` | **已具备跨平台 Vulkan 动态库注入**：Linux 环境下已正确搜索 `/app/runtimes/linux-x64/native/vulkan/libllama.so` 并成功加载 |
| `IHardwareDetector` | `ReferenceRAG.Core.Features.HardwareAcceleration.Detection` | **准确识别硬件**：已精确探测出 `AMD Radeon GPU / APU`（`GpuVendor.Amd`，`renderD128`） |
| `LlamaTelemetryStrategy` | `ReferenceRAG.Infrastructure.Features.ModelManagement.Telemetry` | **具备完备的设备名称渲染逻辑**：能将 AMD 核显格式化为 `AMD Vulkan GPU (AMD Radeon GPU / APU)` |

**结论**：无需引入任何外部第三方包，直接将 `GpuOffloadCalculator` 的核显与 Vulkan 感知规范对齐并复用到 `ChatPerformancePlanner` 与 `ModelsController`，即可彻底修复问题。

---

## 5. 落地解决方案与工程实现

### 5.1 修复 `ChatPerformancePlanner.cs`
将统一内存 APU/核显识别逻辑注入 `ChatPerformancePlanner`，并在自适应与固定参数双模态中全面支持 Vulkan 核显：

```csharp
// 1. 硬件与显存检测：支持独立显存与核显虚拟显存
var primaryGpu = hwContext.PrimaryGpu ?? hwContext.GpuDevices?.FirstOrDefault();
var totalVram = primaryGpu?.TotalVramBytes ?? 0;
var isIntegratedGpu = totalVram == 0 && (primaryGpu?.Vendor is GpuVendor.Intel or GpuVendor.Amd);

// 对于 Intel / AMD 核显 (共享系统内存，无独立物理显存)，按动态共享内存规划（预估 4GB 供核显 Vulkan 调度）
if (isIntegratedGpu)
{
    totalVram = 4UL * 1024 * 1024 * 1024;
}

// 门禁放行条件：只要具备 Nvidia GPU、DirectX12 或是已就绪的 AMD/Intel 核显，即视为具备 GPU 加速能力
var hasGpuSupport = request.UseGpu && totalVram > 0 && (hwContext.HasNvidiaGpu || hwContext.HasDirectX12 || isIntegratedGpu);
var usableVram = totalVram > SystemVramReservedBytes ? totalVram - SystemVramReservedBytes : 0;
```

同时，在生成的自适应画像（Profile）中明确标注核显 Vulkan 加速说明：
```csharp
if (isIntegratedGpu)
{
    rationale = $"集成核显 Vulkan 加速 (共享内存 {FormatBytes(usableVram)})：" + rationale;
}
```

### 5.2 增强 `ModelsController.cs` 实时运行时遥测
修复 `GetRuntimeStatus` 中的 `chatStatus` 构造，使其与 `embStatus` / `rerankStatus` 保持高度统一，准确回显实际硬件后端：
```csharp
var isGpu = cfg.Chat?.LocalGguf?.GpuLayers != 0;
var hwContext = _hardwareDetector.GetContext();
var primaryGpu = hwContext.PrimaryGpu ?? hwContext.GpuDevices?.FirstOrDefault();

string execDevice = "CPU";
if (isGpu)
{
    if (hwContext.HasNvidiaGpu)
        execDevice = $"NVIDIA CUDA 12 ({primaryGpu?.Name ?? "GPU"})";
    else if (primaryGpu?.Vendor == GpuVendor.Intel)
        execDevice = $"Intel Vulkan GPU ({primaryGpu.Name})";
    else if (primaryGpu?.Vendor == GpuVendor.Amd)
        execDevice = $"AMD Vulkan GPU ({primaryGpu.Name})";
    else
        execDevice = $"Vulkan GPU ({primaryGpu?.Name ?? "GPU"})";
}

var gpuLayers = cfg.Chat?.LocalGguf?.GpuLayers ?? 0;
string? offloadDesc = isGpu
    ? (gpuLayers == -1 ? "全量卸载到 GPU" : $"GPU 加速 (卸载 {gpuLayers} 层)")
    : "纯 CPU 计算 (0 层 GPU Offload)";
```

---

## 6. 同类门禁与防回归自动化测试

为防止未来重构时再次出现“独显有显存而核显无显存导致回退 CPU”的问题，在 `tests/ReferenceRAG.Host.Tests/ChatPerformanceModeAndSettingsQualityGateTests.cs` 中新增了 4 项针对 Linux 无独立 VRAM 核显的专属质量门禁测试：

1. **`ChatPerformancePlanner_AmdApuIntegratedGpu_OnLinux_ShouldEnableVulkanAndFullOffload`**
   * 模拟 Linux AMD APU（`TotalVramBytes = 0`, `HasNvidiaGpu = false`, `HasDirectX12 = false`）；
   * 断言：`profile.IsGpuAccelerated == true`，`profile.GpuLayers == -1`，`profile.Rationale` 包含 `"集成核显 Vulkan 加速"`。
2. **`ChatPerformancePlanner_IntelIntegratedGpu_OnLinux_ShouldEnableVulkanAndFullOffload`**
   * 模拟 Linux Intel 核显（`TotalVramBytes = 0`, `HasNvidiaGpu = false`, `HasDirectX12 = false`）；
   * 断言：自适应规划同样进入 Vulkan 全量加速通道。
3. **`ChatPerformancePlanner_FixedMode_AmdIntegratedGpu_RespectsManualGpuLayers`**
   * 验证固定参数模式（Fixed Mode）在核显环境下，不会将用户指定的 `GpuLayers = -1` 强制覆盖降级为 `0`。
4. **`ModelsController_GetRuntimeStatus_OnAmdIntegratedGpu_ReportsAmdVulkanGpu`**
   * 验证 Controller 运行状态端点在 AMD 核显环境下，`ExecutionDevice` 输出为 `AMD Vulkan GPU (AMD Radeon GPU / APU)`，`OffloadDescription` 为 `全量卸载到 GPU`。

运行结果：全套 21 项门禁测试 **全部通过（21/21 Passed, 0 Failed）**。
