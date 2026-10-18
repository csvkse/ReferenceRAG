using ReferenceRAG.Core.Features.ModelManagement.StateMachine;

namespace ReferenceRAG.Core.Features.ModelManagement.Telemetry;

/// <summary>
/// 模型运行时综合遥测数据（状态机状态、GPU硬件加速与性能参数）
/// </summary>
public class ModelRuntimeTelemetry
{
    /// <summary>
    /// 模型生命周期状态机快照
    /// </summary>
    public ModelLifecycleSnapshot StateMachine { get; set; } = new();

    /// <summary>
    /// 硬件加速与 GPU 遥测指标
    /// </summary>
    public ModelHardwareTelemetry Hardware { get; set; } = new();

    /// <summary>
    /// 核心性能与运行配置明细
    /// </summary>
    public ModelConfigTelemetry Configuration { get; set; } = new();

    /// <summary>
    /// 遥测采集时间戳 (UTC)
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// 状态机生命周期快照
/// </summary>
public class ModelLifecycleSnapshot
{
    /// <summary>
    /// 强类型状态机状态
    /// </summary>
    public ModelState State { get; set; } = ModelState.Unloaded;

    /// <summary>
    /// 状态名称
    /// </summary>
    public string StateName => State.ToString();

    /// <summary>
    /// 状态可读描述
    /// </summary>
    public string StateDescription { get; set; } = "未加载";

    /// <summary>
    /// 当前活跃推理租约借用数 (Active Leases)
    /// </summary>
    public int ActiveLeases { get; set; }

    /// <summary>
    /// 是否就绪（Ready 或 Inferring 状态）
    /// </summary>
    public bool IsReady { get; set; }

    /// <summary>
    /// 最近一次故障/异常信息
    /// </summary>
    public string? LastError { get; set; }
}

/// <summary>
/// 硬件加速与 GPU 遥测
/// </summary>
public class ModelHardwareTelemetry
{
    /// <summary>
    /// 是否处于 GPU 加速模式
    /// </summary>
    public bool IsGpuMode { get; set; }

    /// <summary>
    /// 执行设备描述 (如 "NVIDIA GeForce RTX 4060 Laptop GPU (CUDA 12.x)", "CPU (AVX2)")
    /// </summary>
    public string ExecutionDevice { get; set; } = "CPU";

    /// <summary>
    /// 主 GPU 芯片型号
    /// </summary>
    public string? PrimaryGpuName { get; set; }

    /// <summary>
    /// 显卡总独立显存 (GB)
    /// </summary>
    public double? TotalVramGb { get; set; }

    /// <summary>
    /// 显卡驱动版本
    /// </summary>
    public string? DriverVersion { get; set; }

    /// <summary>
    /// 系统是否具备 CUDA 12 运行环境
    /// </summary>
    public bool IsCudaAvailable { get; set; }

    /// <summary>
    /// 分配的 GPU Offload 层数 (-1 为全量卸载，0 为纯 CPU，>0 为部分层)
    /// </summary>
    public int GpuLayers { get; set; }

    /// <summary>
    /// 是否为 100% 全量卸载到 GPU
    /// </summary>
    public bool IsFullOffload { get; set; }

    /// <summary>
    /// GPU 卸载描述 (如 "全量卸载 (28/28 层)", "纯 CPU 模式")
    /// </summary>
    public string OffloadDescription { get; set; } = "";
}

/// <summary>
/// 模型运行与性能配置
/// </summary>
public class ModelConfigTelemetry
{
    /// <summary>
    /// 模型名称
    /// </summary>
    public string ModelName { get; set; } = "";

    /// <summary>
    /// 模型友好展示名
    /// </summary>
    public string DisplayName { get; set; } = "";

    /// <summary>
    /// 模型后端引擎标识 (llama / onnx / openai)
    /// </summary>
    public string BackendType { get; set; } = "";

    /// <summary>
    /// 模型文件格式 (gguf / onnx / api)
    /// </summary>
    public string ModelFormat => BackendType == "llama" ? "gguf" : (string.IsNullOrEmpty(BackendType) ? "onnx" : BackendType);

    /// <summary>
    /// 引擎友好展示名
    /// </summary>
    public string BackendDisplayName { get; set; } = "";

    /// <summary>
    /// 向量维度
    /// </summary>
    public int Dimension { get; set; }

    /// <summary>
    /// 最大上下文限制 (Tokens)
    /// </summary>
    public int ContextSize { get; set; }

    /// <summary>
    /// 批处理大小 (BatchSize)
    /// </summary>
    public int BatchSize { get; set; }

    /// <summary>
    /// 向量池化方式 (Mean / Cls / LastToken 等)
    /// </summary>
    public string PoolingType { get; set; } = "Mean";

    /// <summary>
    /// 模型量化等级 (Q4_K_M / Q8_0 / FP16 / FP32 / INT8 等)
    /// </summary>
    public string Quantization { get; set; } = "None";

    /// <summary>
    /// 模型磁盘文件体积 (Bytes)
    /// </summary>
    public long ModelSizeBytes { get; set; }

    /// <summary>
    /// 模型文件体积友好展示 (如 "1.24 GB")
    /// </summary>
    public string ModelSizeFriendly { get; set; } = "";

    /// <summary>
    /// 是否支持非对称检索 (Query/Doc 前缀)
    /// </summary>
    public bool SupportsAsymmetricEncoding { get; set; }

    /// <summary>
    /// 是否运行在模拟降级模式
    /// </summary>
    public bool IsSimulationMode { get; set; }
}
