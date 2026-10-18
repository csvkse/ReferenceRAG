# 性能测试模型运行状态、GPU遥测与性能配置优化方案

## 1. 概述与需求背景

在本地大模型与 RAG（检索增强生成）系统中，**性能测试与基准评测（Performance Benchmark）**是评估向量化吞吐量、分块效率、显存消耗和系统延迟的关键手段。然而，在现有系统中，用户进入“性能测试”页面时面临以下痛点：

1. **模型运行状态黑盒**：
   - 无法直观获知当前活跃模型处于生命周期的哪个阶段（未加载 `Unloaded`、加载初始化中 `Loading`、就绪 `Ready`、高并发推理中 `Inferring`、正在热重载排空 `Unloading` 还是崩溃故障 `Faulted`）。
   - 在高并发压测或长时间测试时，不知道是否有在途借用租约（Active Leases）积压。
2. **GPU 加速与硬件拓扑不透明**：
   - 用户无法直接确认当前模型是运行在 **NVIDIA CUDA**、**DirectML** 还是退回到了 **CPU**。
   - 针对 GGUF 格式（LLamaSharp/llama.cpp 后端），无法查看模型实际 Offload（卸载）到 GPU 的层数（如 `28/28 层` 或仅部分卸载）、显存预估与硬件检测状态。
3. **关键性能与算力配置不可视**：
   - 缺乏对当前生效的关键性能参数的展示：批处理大小（BatchSize）、最大上下文序列限制（ContextSize / MaxSequenceLength）、向量维度（Dimension）、池化策略（Mean / CLS）、模型量化等级（Q4_K_M、FP16、INT8 等）以及模型后端引擎（LLamaSharp / ONNX Runtime）。

为了彻底解决上述痛点，本项目引入**适配器策略模式（Adapter + Strategy Pattern）**与**强类型状态机模式（State Machine Pattern）**，对性能测试模块与模型底层遥测链路进行系统性重构与增强。

---

## 2. 方案可行性调研与系统现状评估

### 2.1 现有基础设施能力盘点

通过对现有代码库的深度探查，系统已经具备了强大的模块化基础设施，具备极高的演进可行性：

| 模块 / 接口 | 当前所在位置 | 具备的能力与现有功能 |
| :--- | :--- | :--- |
| **`ModelLifecycleStateMachine<T>`** | `Infrastructure/.../StateMachine/` | 强类型状态机，管理 `ModelState`，具备两阶段提交（Two-Phase Commit）、租约借用（`AcquireLease`）、引用计数（`ActiveLeases`）与安全卸载能力。 |
| **`IHardwareDetector` / `HardwareContext`** | `Infrastructure/.../HardwareAcceleration/` | 自动探测物理显卡厂商（Nvidia/AMD/Intel）、显卡型号、驱动版本、独立显存（TotalVramBytes）、CUDA 12 动态库可用性与 DirectX 12 支持。 |
| **`IGpuOffloadCalculator`** | `Infrastructure/.../HardwareAcceleration/` | 智能计算 GGUF 模型的 GPU 卸载方案（`GpuOffloadPlan`），能够预估显存、模型层数并仲裁全量/部分/CPU 卸载。 |
| **`IEmbeddingBackendAdapter`** | `Infrastructure/.../Adapters/` | 统一抽象了 `LlamaEmbeddingAdapter`（GGUF）、`OnnxEmbeddingAdapter`（ONNX）、`OpenAiEmbeddingAdapter` 等底层引擎。 |
| **`IModelManager`** | `Infrastructure/.../Services/` | 管理本地已下载模型、当前选用模型、量化信息、模型文件大小与格式检测。 |
| **`PerformanceController`** | `Host/.../Controllers/` | 现有的性能基准测试接口，具备分块测试、批处理测试与耗时统计功能。 |
| **`Performance.js`** | `Business/.../wwwroot/app/views/` | 现有的前端性能测试界面（Vue 3 SFC），包含基准测试、语义相似度、API 对比等多标签页。 |

### 2.2 可行性结论

基于上述已有架构：
- **可行性极高**：不需要破坏现有模型推断主干逻辑，只需在 `LazyEmbeddingService`（及底层适配器）上扩展**遥测提供者接口（`IModelRuntimeTelemetryProvider`）**，利用现成的状态机和硬件上下文即可实现毫秒级零开销遥测。
- **架构完全吻合**：适配器模式负责消弭不同推理引擎内部结构的异构性，策略模式负责对不同引擎派发专门的遥测采集算法，状态机模式实时同步推断租约与生命周期演变。

---

## 3. 架构设计与设计模式融合

### 3.1 总体架构蓝图

```
┌────────────────────────────────────────────────────────────────────────┐
│                        前端展现层 (Performance.js)                      │
│  - 模型状态机徽章 (Ready / Inferring / Loading / Faulted)                │
│  - GPU 运行模式指示 (CUDA 12 / DirectML / CPU / 显存 / Offload 层数)      │
│  - 核心性能配置卡片 (BatchSize / ContextSize / Pooling / Quantization)    │
└────────────────────────────────────┬───────────────────────────────────┘
                                     │ HTTP GET api/performance/model-status
                                     │ (或作为 benchmark 响应元数据返回)
┌────────────────────────────────────▼───────────────────────────────────┐
│                    控制器层 (PerformanceController.cs)                  │
└────────────────────────────────────┬───────────────────────────────────┘
                                     │
┌────────────────────────────────────▼───────────────────────────────────┐
│               遥测协调器 (ModelTelemetryCoordinator)                   │
├────────────────────────────────┬───────────────────────────────────────┤
│    硬件探测器 (IHardwareDetector)│     模型管理器 (IModelManager)         │
└────────────────┬───────────────┴───────────────────────────────┬───────┘
                 │                                               │
┌────────────────▼───────────────────────────────────────────────▼───────┐
│               适配器策略模式 (Adapter + Strategy Pattern)               │
│                                                                        │
│                      IModelTelemetryStrategy                           │
│                      ┌─────────────────┐                               │
│                      │ ExtractTelemetry│                               │
│                      └────────┬────────┘                               │
│            ┌──────────────────┼──────────────────┐                     │
│            ▼                  ▼                  ▼                     │
│  LlamaTelemetryStrategy  OnnxTelemetryStrategy  OpenAiTelemetryStrategy│
│  (GGUF / GPU Layers)     (CUDA / DirectML / CPU) (Remote Endpoint)     │
└───────────────────────────────┬────────────────────────────────────────┘
                                │ 采集自当前底层适配器
┌───────────────────────────────▼────────────────────────────────────────┐
│               状态机模式 (State Machine Pattern)                        │
│                                                                        │
│                    ModelLifecycleStateMachine<T>                       │
│  - ModelState: Unloaded ➔ Loading ➔ Ready ⇄ Inferring ➔ Unloading    │
│  - ActiveLeases: 实时在途租约借用数                                      │
│  - LastError: 异常隔离上下文                                            │
└────────────────────────────────────────────────────────────────────────┘
```

---

## 4. 详细模式设计与契约定义

### 4.1 状态机模式（State Machine Pattern）

系统利用强类型状态机跟踪模型的完整生命周期，并在接口中暴露强类型状态：

```csharp
public enum ModelState
{
    Unloaded,   // 未加载（无非托管显存占用）
    Loading,    // 正在加载权重、分配 GPU 显存并初始化推理上下文
    Ready,      // 就绪（已加载完成，随时可接受推理）
    Inferring,  // 推理中（当前存在活跃的租约 Borrow/Lease）
    Unloading,  // 正在安全排空并卸载显存
    Faulted,    // 故障状态（如 CUDA 初始化失败、OOM 或文件损坏）
    Disposed    // 已销毁
}
```

#### 遥测提供者接口 `IModelRuntimeTelemetryProvider`
由 `LazyEmbeddingService` 和 `LazyRerankService` 实现：
```csharp
public interface IModelRuntimeTelemetryProvider
{
    /// <summary>
    /// 获取当前模型的运行时状态机状态与租约情况
    /// </summary>
    ModelLifecycleSnapshot GetLifecycleSnapshot();

    /// <summary>
    /// 获取当前底层的适配器实例（供策略提取器分析）
    /// </summary>
    object? CurrentAdapter { get; }

    /// <summary>
    /// 获取当前的配置选项快照
    /// </summary>
    EmbeddingOptions CurrentOptions { get; }
}
```

### 4.2 适配器策略模式（Adapter + Strategy Pattern）

不同模型引擎底层的配置和硬件加速表现形式差异巨大：
- **LLamaSharp (GGUF)**：具有 `GpuLayers`（GPU Offload 层数）、`LLamaPoolingType`、`ContextSize`、`GpuOffloadPlan` 等；
- **ONNX Runtime**：具有 `ExecutionProvider`（CUDA / CPU）、`OrtLoggingLevel`、`MemoryPattern` 等；
- **OpenAI 兼容协议**：属于外部 REST API，没有本地显卡加速，但有远程连接状态。

通过**策略模式**，我们将不同后端的遥测解析逻辑剥离并解耦：

```csharp
public interface IModelTelemetryStrategy
{
    /// <summary>
    /// 当前策略是否支持该适配器
    /// </summary>
    bool CanHandle(object adapter);

    /// <summary>
    /// 执行遥测提取
    /// </summary>
    ModelEngineTelemetry Extract(
        object adapter,
        EmbeddingOptions options,
        HardwareContext hardwareContext,
        ModelInfo? modelMetadata);
}
```

具体策略实现：
1. **`LlamaTelemetryStrategy`**：
   - 提取 `GpuLayers`。
   - 判断当前是否为 GPU 加速模式（`GpuLayers > 0` 且检测到系统可用 GPU）。
   - 解析 GGUF 文件的量化等级（如从文件名或元数据识别 `Q4_K_M`、`Q8_0` 等）。
   - 提取池化类型（`Mean`、`Cls` 等）与上下文窗口（如 `8192`）。
2. **`OnnxTelemetryStrategy`**：
   - 检测 ONNX Runtime 当前使用的执行提供程序（`CUDAExecutionProvider` 还是 `CPUExecutionProvider`）。
   - 提取 ONNX 存储格式（`Embedded` 单文件还是 `External` 散列外挂权重）。
   - 提取最大序列长度（MaxSequenceLength）与向量维度（Dimension）。
3. **`OpenAiTelemetryStrategy`**：
   - 识别为远程云端 API，设备标注为 `Cloud API / Remote Endpoint`，`IsGpuMode = false`。

---

## 5. 遥测数据传输对象（DTO）设计

统一输出给前端的 `ModelRuntimeTelemetry` 数据模型设计如下：

```json
{
  "stateMachine": {
    "state": "Ready",
    "stateDescription": "就绪（可随时响应推理）",
    "activeLeases": 0,
    "isReady": true,
    "lastError": null
  },
  "hardware": {
    "isGpuMode": true,
    "executionDevice": "NVIDIA GeForce RTX 4060 Laptop GPU (CUDA 12.x)",
    "primaryGpuName": "NVIDIA GeForce RTX 4060 Laptop GPU",
    "totalVramGb": 8.0,
    "driverVersion": "560.94",
    "isCudaAvailable": true,
    "gpuLayers": 28,
    "isFullOffload": true,
    "offloadDescription": "全量卸载到 GPU (28/28 层)"
  },
  "configuration": {
    "modelName": "bge-m3-q4_k_m.gguf",
    "displayName": "BGE M3 (GGUF Q4_K_M)",
    "backendType": "llama",
    "backendDisplayName": "LLamaSharp (llama.cpp 原生加速)",
    "dimension": 1024,
    "contextSize": 8192,
    "batchSize": 32,
    "poolingType": "Mean",
    "quantization": "Q4_K_M",
    "modelSizeBytes": 1284505600,
    "modelSizeFriendly": "1.20 GB",
    "supportsAsymmetricEncoding": true,
    "isSimulationMode": false
  },
  "timestamp": "2026-10-18T16:20:00Z"
}
```

---

## 6. 前端 UI 与交互设计

在前端 `Performance.js` 中新增 **“当前推理引擎与硬件加速配置”** 仪表盘，布局如下：

1. **状态机指示区（State Machine Badge）**：
   - 状态徽章：
     - `Ready`：绿色徽章 `✓ 就绪`
     - `Inferring`：蓝色动态闪烁徽章 `⚡ 推理中 (N 租约)`
     - `Loading`：橙色徽章 `⏳ 加载中`
     - `Faulted`：红色徽章 `✕ 故障`（鼠标悬停查看异常）
     - `Unloaded`：灰色徽章 `○ 未加载`
2. **算力加速模式区（Hardware Acceleration Badge & VRAM）**：
   - 若 `isGpuMode = true`：
     - 绿色高亮 `GPU 硬件加速` 标签。
     - 显示 GPU 设备全称（如 `NVIDIA GeForce RTX 4060 Laptop GPU`）与驱动版本。
     - 显示 Offload 卸载状态：例如 `全量 GPU 卸载 (28/28 层)`。
     - 显示总显存容量（例如 `8.0 GB`）。
   - 若 `isGpuMode = false`：
     - 显示灰/黄色 `CPU 推理模式`，若检测到系统有 GPU，给出硬件优化建议。
3. **核心性能参数矩阵（Performance Configurations Grid）**：
   - 引擎类别：`LLamaSharp / ONNX Runtime`
   - 批处理大小（BatchSize）
   - 上下文窗口（ContextSize）
   - 向量维度（Dimension）
   - 模型量化等级（Quantization）
   - 池化机制（PoolingType）
   - 文件体积（Model Size）
4. **实时刷新与性能测试联动**：
   - 提供独立“刷新状态”按钮；
   - 页面加载时自动获取；
   - 运行“性能基准测试”时自动刷新状态机状态，并在测试结果返回中永久绑定本次测试执行时的硬件与状态快照。

---

## 7. 实施计划与测试验证策略

1. **第一阶段：后端基础设施重构与策略模式构建**
   - 创建 `ModelRuntimeTelemetry` 数据结构；
   - 定义 `IModelTelemetryStrategy` 并实现 `LlamaTelemetryStrategy`、`OnnxTelemetryStrategy`、`OpenAiTelemetryStrategy`；
   - 创建 `ModelTelemetryCoordinator` 统一编排采集。
2. **第二阶段：服务与控制器改造**
   - 在 `LazyEmbeddingService` 中实现状态机快照与提供者；
   - 在 `PerformanceController.cs` 中增加 `GET api/performance/model-status` 端点；
   - 在 `BenchmarkResult` 中附带 `ModelTelemetry` 快照。
3. **第三阶段：前端对接与视图渲染**
   - 在 `app/api/index.js` 添加 API；
   - 在 `Performance.js` 编写状态卡片与徽章逻辑，完成响应式数据流。
4. **第四阶段：质量保证与自动化测试**
   - 编写专门的单元测试（针对策略解析、硬件遥测整合、控制器端点与模拟数据）；
   - 执行全量回归测试，确保 369 项测试 100% 通过（零回归）。
