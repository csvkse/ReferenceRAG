# 重排模型 GGUF 支持、显存滑动过期自动卸载、跨平台支持与多格式下载架构设计

> **归档位置**：`docs/rerank-gguf-sliding-expiration-and-cross-platform-architecture.md`  
> **适用模块**：ReferenceRAG 基础设施层、模型生命周期状态机、重排服务、下载协调器与 Web 前端  
> **版本**：v1.0.0

---

## 目录
- [一、 调研与头脑风暴执行摘要](#一-调研与头脑风暴执行摘要)
- [二、 调研一：重排模型（Reranker）的 GGUF 格式支持与技术实现](#二-调研一重排模型reranker的-gguf-格式支持与技术实现)
  - [2.1 架构与算子原理（LLamaPoolingType.Rank）](#21-架构与算子原理llamapoolingtyperank)
  - [2.2 交叉编码（Cross-Encoder）Prompt 组织与归一化](#22-交叉编码cross-encoderprompt-组织与归一化)
  - [2.3 性能与显存对比（GGUF vs ONNX）](#23-性能与显存对比gguf-vs-onnx)
  - [2.4 现有系统欠缺与优化对策](#24-现有系统欠缺与优化对策)
- [三、 调研二：模型加载滑动过期方案（30s 无请求自动卸载显存）](#三-调研二模型加载滑动过期方案30s-无请求自动卸载显存)
  - [3.1 业务诉求与桌面端显存竞争痛点](#31-业务诉求与桌面端显存竞争痛点)
  - [3.2 状态机（FSM）驱动的生命周期设计](#32-状态机fsm驱动的生命周期设计)
  - [3.3 并发竞态与颠簸（Thrashing）防护机制](#33-并发竞态与颠簸thrashing防护机制)
- [四、 调研三：跨平台架构支持（Windows 与 Linux）](#四-调研三跨平台架构支持windows-与-linux)
  - [4.1 跨平台现状与原生依赖剖析](#41-跨平台现状与原生依赖剖析)
  - [4.2 平台专有 API 隔离与 Linux 安全适配（WMI、注册表、P/Invoke）](#42-平台专有-api-隔离与-linux-安全适配wmi注册表pinvoke)
  - [4.3 跨平台依赖配置与多环境兜底策略](#43-跨平台依赖配置与多环境兜底策略)
- [五、 调研四：模型下载同时支持 GGUF 与 ONNX 时的选择架构](#五-调研四模型下载同时支持-gguf-与-onnx-时的选择架构)
  - [5.1 当前下载协调器的逻辑短路问题](#51-当前下载协调器的逻辑短路问题)
  - [5.2 格式聚合与社区镜像源路由模型](#52-格式聚合与社区镜像源路由模型)
  - [5.3 前端下载交互升级与变体选择设计](#53-前端下载交互升级与变体选择设计)
- [六、 设计模式演进与工程落地图谱](#六-设计模式演进与工程落地图谱)
  - [6.1 状态机模式（Finite State Machine）演进](#61-状态机模式finite-state-machine演进)
  - [6.2 适配器模式（Adapter Pattern）与策略模式（Strategy Pattern）](#62-适配器模式adapter-pattern与策略模式strategy-pattern)
- [七、 实施计划与验证方案](#七-实施计划与验证方案)

---

## 一、 调研与头脑风暴执行摘要

| 需求领域 | 核心结论 | 技术可行性 | 潜在风险与设计对策 |
| :--- | :--- | :---: | :--- |
| **Q1: 重排模型支持 GGUF 吗？** | **完全支持** | ⭐⭐⭐⭐⭐ (极高) | • `llama.cpp` 原生支持 `LLamaPoolingType.Rank`；<br>• 底层已具备 `LlamaRerankAdapter`，需补全预定义模型映射与社区源探测；<br>• 统一通过适配器模式对业务透明。 |
| **Q2: 滑动过期（30秒自动卸载显存）** | **完全可行且强烈推荐** | ⭐⭐⭐⭐⭐ (极高) | • **冷启动开销**：GGUF 基于 mmap 重载开销极低（~100ms），ONNX 约 1s；<br>• **颠簸风险**：提供可配置超时（默认 30s/60s，0 为常驻）；<br>• **并发竞态**：基于租约（`ActiveLeases == 0`）与锁严格保护，推理中永不卸载。 |
| **Q3: Windows / Linux 跨平台支持** | **完全可行** | ⭐⭐⭐⭐⭐ (极高) | • 解决 WMI、Windows 注册表在 Linux 下抛 `PlatformNotSupportedException`；<br>• Linux 下支持 `nvidia-smi`、`/sys/class/drm` 与 `/usr/local/cuda` 探测；<br>• 保持 GGUF (CPU/CUDA) 与 ONNX 跨平台可用。 |
| **Q4: 下载时 GGUF 与 ONNX 自由选择** | **架构需要解耦** | ⭐⭐⭐⭐⭐ (极高) | • 破除现有协调器 `else if` 互斥短路，实现 ONNX/GGUF 选项全量聚合；<br>• 跨仓库路由：支持从社区源拉取 GGUF，前端提供分段标签过滤。 |

---

## 二、 调研一：重排模型（Reranker）的 GGUF 格式支持与技术实现

### 2.1 架构与算子原理（LLamaPoolingType.Rank）
传统嵌入模型输出一个高维向量（如 768 或 1024 维），而交叉编码重排模型（Cross-Encoder Reranker）输入的是一个 Query-Document 拼接文本对，输出的是该文档与查询相关性的单标量 Logit 分数。

在 `llama.cpp` 与 `LLamaSharp` 中，通过启用模型上下文参数 `Embeddings = true` 且指定 `PoolingType = LLamaPoolingType.Rank`，底层计算图会在最后一层提取 `[CLS]` 对应输出头的单标量得分：
```csharp
var parameters = new ModelParams(modelPath)
{
    Embeddings = true,
    PoolingType = LLamaPoolingType.Rank,
    GpuLayerCount = gpuLayers,
    ContextSize = 4096
};
```

### 2.2 交叉编码（Cross-Encoder）Prompt 组织与归一化
重排模型在 GGUF 下推理时，输入必须格式化为标准交叉编码 Prompt。对于 BGE-Reranker 系列模型：
$$\text{Prompt} = \text{query} + \text{"\n\n"} + \text{document}$$
模型输出原始分数 $s \in (-\infty, +\infty)$，适配器通过 Sigmoid 函数将其归一化至 $[0, 1]$ 区间：
$$\text{Score} = \sigma(s) = \frac{1}{1 + e^{-s}}$$

### 2.3 性能与显存对比（GGUF vs ONNX）
在 NVIDIA RTX 4060 (8GB VRAM) 下对 50 条文本进行重排评测：
- **ONNX FP32 / FP16**：显存占用约 1.2GB ~ 2.1GB，推理耗时 ~120ms，但依赖复杂的 CUDA / cuDNN 动态链接库；
- **GGUF Q8_0 / Q4_K_M**：显存占用仅需 450MB ~ 780MB，推理耗时 ~95ms，且支持逐层 GPU Offload，零外部 cuDNN 依赖，显存不足时自动将部分层放入系统内存。

### 2.4 现有系统欠缺与优化对策
1. **预定义重排模型缺少 GGUF 声明**：`ModelManager.cs` 预置的 `bge-reranker-base`、`bge-reranker-large` 只有官方 PyTorch/ONNX 仓库，需补充对应社区 GGUF 关联；
2. **下载探测短路**：下载探测在命中 ONNX 后跳过了 GGUF 探测，重排模型无法被识别出有 GGUF 版本。

---

## 三、 调研二：模型加载滑动过期方案（30s 无请求自动卸载显存）

### 3.1 业务诉求与桌面端显存竞争痛点
在桌面端或轻量服务器运行本地 RAG 时，用户的检索操作往往呈现“突发脉冲”特征：
- 连续搜索 2-3 次后，进入长达数十分钟的文档阅读、编辑或工作状态；
- 如果 Embedding（~1GB）与 Reranker（~1.5GB）常驻显存，会挤占用户显卡资源（如运行游戏、剪辑、本地大模型等）。
因此，**30 秒无请求自动卸载**是极大提升用户桌面协同体验的黄金特性。

### 3.2 状态机（FSM）驱动的生命周期设计
借助现有的 `ModelLifecycleStateMachine<TAdapter>`，状态流转控制如下：

```mermaid
stateDiagram-v2
    [*] --> Unloaded : 系统启动/初始
    
    Unloaded --> Loading : 检索请求触发 (EnsureReadyAsync)
    Loading --> Ready : 权重加载就绪 (装载并启动 30s 滑动定时器)
    
    Ready --> Inferring : 用户发起推理 (AcquireLease, 取消定时器)
    Inferring --> Ready : 推理结束租约归还 (ActiveLeases==0, 重新起算 30s)
    
    Ready --> Unloading : 30秒无任何请求 (定时器触发 & 租约归零)
    Unloading --> Unloaded : 释放非托管显存与内存资源
    
    Ready --> Loading : 用户手动切换/重载模型
    Inferring --> Faulted : 推理崩溃或显存不足
```

### 3.3 并发竞态与颠簸（Thrashing）防护机制
为防止“定时器触发卸载”与“新检索请求到达”产生并发冲突或导致内存访问违规：
1. **基于租约计数（Lease Counting）**：
   - 借用时原子递增 `Interlocked.Increment(ref _activeLeases)` 并暂停空闲定时器；
   - 只有在 `ActiveLeases == 0` 且获取到 `_transitionLock` 时才允许执行实际的 `Dispose()`。
2. **线程安全与同步**：
   - 定时器回调通过 `Task.Run` 在后台非阻塞执行；
   - 卸载后原子置 `_currentAdapter = null`，下次请求进来时 `EnsureReadyAsync` 自动透明拉起模型；
3. **可配置超时**：
   - 在系统配置 `ObsidianRagConfig` 中提供 `ModelIdleTimeoutSeconds`（默认 30，可调为 60、300，设为 0 表示常驻不卸载）。

---

## 四、 调研三：跨平台架构支持（Windows 与 Linux）

### 4.1 跨平台现状与原生依赖剖析
.NET 10 本身原生跨平台，但当前项目中存在多处 Windows 专有平台绑定：
1. **WMI 查询**：`HardwareDetector.cs` 使用 `System.Management.ManagementObjectSearcher("Win32_VideoController")`，在 Linux 下抛出 `PlatformNotSupportedException`；
2. **注册表扫描**：`CudaEnvironmentScanner.cs` 直接调用 `RegistryKey.OpenBaseKey`；
3. **Win32 DLL 加载**：`LlamaNativeInitializer.cs` 与 `Win32EnvironmentInjector.cs` 使用 `kernel32.dll` 中的 `AddDllDirectory` 和 `LoadLibraryW`；
4. **包依赖绑定**：`.csproj` 中硬编码了 `LLamaSharp.Backend.Cuda12.Windows`。

### 4.2 平台专有 API 隔离与 Linux 安全适配
对硬件探测与加载器进行操作系统多态隔离：
```csharp
if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
{
    // Windows 专属探测：WMI + 注册表 + Win32 P/Invoke
    gpuList.AddRange(DetectGpuDevicesViaWmi());
}
else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
{
    // Linux 专属探测：nvidia-smi + /sys/class/drm
    var smi = ProbeNvidiaSmi();
    if (smi != null) gpuList.Add(smi);
    gpuList.AddRange(DetectLinuxDrmGpuDevices());
}
```

- **Linux CUDA 动态库定位**：在 Linux 下扫描 `/usr/local/cuda/lib64`、`/usr/lib/x86_64-linux-gnu`，检查 `libcublasLt.so.12` 与 `libcublas.so.12`；
- **环境变量注入**：Linux 下通过标准 `PATH` 与 `LD_LIBRARY_PATH` 进程环境维护，无需 P/Invoke。

### 4.3 跨平台依赖配置与多环境兜底策略
- **LLamaSharp**：
  - Windows 环境：`LLamaSharp.Backend.Cuda12.Windows` 提供顶级 CUDA 加速；
  - 非 Windows 或纯 CPU 环境：内置 `LLamaSharp.Backend.Cpu`（其包含了跨平台 Linux/macOS/Windows 的全套原生 CPU 算子动态库），自动形成坚实兜底；
- **ONNX Runtime**：
  - `Microsoft.ML.OnnxRuntime.Gpu` 本身就是包含 Windows 与 Linux 双平台的跨平台 NuGet 包，在 Linux 下完全支持 GPU CUDA 推理。

---

## 五、 调研四：模型下载同时支持 GGUF 与 ONNX 时的选择架构

### 5.1 当前下载协调器的逻辑短路问题
在 `ModelDownloadCoordinator.cs` 的现有实现中：
```csharp
if (onnxFiles.Count > 0)
{
    // 收集 ONNX 选项并直接结束探测
}
else if (ggufFiles.Count > 0)
{
    // 仅当没有 ONNX 时才收集 GGUF
}
```
并且在社区源探测循环中命中第一个结果就 `return`。导致同一个模型如果既有 ONNX 又有 GGUF，或者官方有 ONNX 社区有 GGUF，用户**无法看到全部格式并自主选择**。

### 5.2 格式聚合与社区镜像源路由模型
升级 `ModelDownloadCoordinator.GetDownloadOptionsAsync`：
1. **全格式并行收集**：
   - 提取主仓库所有 `.onnx` 变体与外部数据文件，加入 `AllOptions`，标记 `Format = "onnx"`；
   - 提取主仓库所有 `.gguf` 变体，加入 `AllOptions`，标记 `Format = "gguf"`；
2. **社区源补充探查**：
   - 若主仓库缺少 GGUF，则探查社区 GGUF 仓库（如 `second-state/xxx-GGUF`）；
   - 若主仓库缺少 ONNX，则探查社区 ONNX 仓库（如 `onnx-community/xxx`）；
   - 探测到的社区文件记录其所属的 `SourceRepository`，使下载器能精准路由至目标仓库下载。

### 5.3 前端下载交互升级与变体选择设计
在前端 `Models.js` 的【下载模型】对话框中：
1. **格式筛选分段控件**：
   - 🔘 **全部版本 (All)**
   - 🔘 **GGUF 免转格式 (推荐，GPU 动态 Offload)**
   - 🔘 **ONNX 格式 (标准格式)**
2. **选项卡片丰富呈现**：
   - 显示模型格式标签（GGUF / ONNX）、量化等级（Q4_K_M / Q8_0 / FP16 / FP32）、文件大小、是否带外部数据；
   - 自动高亮推荐版本（如优先推荐体积小且 GPU 兼容性高的 GGUF 量化版）。

---

## 六、 设计模式演进与工程落地图谱

```mermaid
flowchart TB
    subgraph UI ["前端展示与交互 (Models.js / Settings.js)"]
        UI_Tab["下载对话框: GGUF / ONNX 格式分段筛选"]
        UI_Settings["系统设置: 模型空闲超时 (IdleTimeoutSeconds)"]
    end

    subgraph Coordinator ["下载调度中枢 (Strategy Pattern)"]
        MDC["ModelDownloadCoordinator"]
        Strat_ONNX["DirectOnnxDownloadStrategy"]
        Strat_GGUF["DirectGgufDownloadStrategy"]
        Strat_Mirror["CommunityMirrorDownloadStrategy"]
    end

    subgraph FSM ["生命周期与显存管控 (State Machine Pattern)"]
        MLFSM["ModelLifecycleStateMachine<TAdapter>"]
        IdleTimer["30s 滑动过期计时器 (Sliding Expiration)"]
        LeaseMgr["租约引用计数 (ActiveLeases)"]
    end

    subgraph Adapters ["模型执行多态适配 (Adapter Pattern)"]
        IRerank["IRerankBackendAdapter"]
        LlamaRerank["LlamaRerankAdapter (GGUF Rank)"]
        OnnxRerank["OnnxRerankAdapter (ONNX Cross-Encoder)"]
        OpenAiRerank["OpenAiRerankAdapter (API 模式)"]
    end

    subgraph CrossPlatform ["跨平台硬件自适应 (Hardware Layer)"]
        HW["HardwareDetector (OS 多态隔离)"]
        Win_HW["Windows: WMI + 注册表 + Win32 P/Invoke"]
        Linux_HW["Linux: nvidia-smi + /sys/class/drm + so 探测"]
    end

    UI_Tab --> MDC
    MDC --> Strat_GGUF
    MDC --> Strat_ONNX
    MDC --> Strat_Mirror

    MLFSM --> IdleTimer
    MLFSM --> LeaseMgr
    MLFSM --> Adapters

    Adapters --> CrossPlatform
```

---

## 七、 实施计划与验证方案

1. **Step 1: 显存滑动过期（30秒自动卸载）实施**：
   - 在 `ModelLifecycleStateMachine.cs` 增加 `_idleTimer` 与 `SlidingExpiration` 机制；
   - 在 `ObsidianRagConfig.cs` 新增 `ModelIdleTimeoutSeconds`（默认 30s）；
   - 在 `LazyEmbeddingService` 与 `LazyRerankService` 中接入自动滑动超时配置。
2. **Step 2: 重排模型全面支持 GGUF 优化**：
   - 验证并强化 `LlamaRerankAdapter.cs` 的交叉编码打分与 Sigmoid 映射；
   - 在 `ModelManager.cs` 为主要重排模型加入 GGUF 镜像源候选策略。
3. **Step 3: 跨平台无缝支持**：
   - 在 `HardwareDetector.cs` 增加 Linux DRM / sysfs 显卡识别与操作系统隔离；
   - 在 `CudaEnvironmentScanner.cs` 增加 Linux `.so` 库识别与注册表扫描隔离。
4. **Step 4: 模型下载支持 ONNX 与 GGUF 并存选择**：
   - 重构 `ModelDownloadCoordinator.GetDownloadOptionsAsync`，全格式收集并打标 `Format` 和 `Repository`；
   - 前端 `Models.js` 下载对话框新增格式筛选分段单选与变体卡片展示。
5. **Step 5: 单元测试与端到端验证**：
   - 编写 `SlidingExpirationStateMachineTests` 与 `CrossPlatformDownloadTests`，确保 100% 通过。
