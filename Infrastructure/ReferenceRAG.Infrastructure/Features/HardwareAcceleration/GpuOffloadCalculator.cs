using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Features.HardwareAcceleration.Detection;
using ReferenceRAG.Core.Features.HardwareAcceleration.Models;
using ReferenceRAG.Core.Features.ModelManagement.Helpers;

namespace ReferenceRAG.Core.Features.HardwareAcceleration;

/// <summary>
/// GPU Offload 规划方案结果
/// </summary>
public record GpuOffloadPlan
{
    /// <summary>
    /// 分配的 GPU Offload 层数 (-1 表示全量卸载，0 表示纯 CPU，>0 表示部分卸载)
    /// </summary>
    public int GpuLayers { get; init; }

    /// <summary>
    /// 模型预估或自省的总层数
    /// </summary>
    public int TotalLayers { get; init; }

    /// <summary>
    /// 是否为 100% 全量卸载
    /// </summary>
    public bool IsFullOffload => GpuLayers == -1 || (TotalLayers > 0 && GpuLayers >= TotalLayers);

    /// <summary>
    /// 是否为纯 CPU 运行
    /// </summary>
    public bool IsCpuOnly => GpuLayers == 0;

    /// <summary>
    /// 模型文件磁盘大小
    /// </summary>
    public ulong ModelSizeBytes { get; init; }

    /// <summary>
    /// 预估全量卸载所需显存（含上下文缓冲）
    /// </summary>
    public ulong EstimatedVramNeededBytes { get; init; }

    /// <summary>
    /// 当前探测可用的 GPU 显存
    /// </summary>
    public ulong UsableVramBytes { get; init; }

    /// <summary>
    /// 策略仲裁原因说明
    /// </summary>
    public string Rationale { get; init; } = "";
}

/// <summary>
/// GPU Offload 动态层数计算器接口
/// </summary>
public interface IGpuOffloadCalculator
{
    /// <summary>
    /// 根据模型文件特性与 GPU 显存状态动态规划 GPU Offload 层数
    /// </summary>
    GpuOffloadPlan CalculateOffload(
        string modelPath,
        HardwareContext? context = null,
        bool useGpu = true,
        int contextLength = 4096,
        int? explicitLayers = null);
}

/// <summary>
/// GPU Offload 动态层数计算器实现
/// 结合 GGUF 元数据自省、显存容量与上下文开销，自适应最大化 GPU 加速并消除 OOM 崩溃
/// </summary>
public class GpuOffloadCalculator : IGpuOffloadCalculator
{
    private readonly IHardwareDetector _hardwareDetector;
    private readonly ILogger<GpuOffloadCalculator>? _logger;

    // 为 Windows 窗口管理器与系统保留的显存缓冲区（默认 768MB）
    private const ulong SystemVramReservedBytes = 768UL * 1024 * 1024;

    public GpuOffloadCalculator(
        IHardwareDetector hardwareDetector,
        ILogger<GpuOffloadCalculator>? logger = null)
    {
        _hardwareDetector = hardwareDetector ?? throw new ArgumentNullException(nameof(hardwareDetector));
        _logger = logger;
    }

    public GpuOffloadPlan CalculateOffload(
        string modelPath,
        HardwareContext? context = null,
        bool useGpu = true,
        int contextLength = 4096,
        int? explicitLayers = null)
    {
        var hwContext = context ?? _hardwareDetector.GetContext();
        var modelFile = new FileInfo(modelPath);
        var modelSize = modelFile.Exists ? (ulong)modelFile.Length : 0UL;

        // 1. 用户显式关闭 GPU 加速
        if (!useGpu)
        {
            return new GpuOffloadPlan
            {
                GpuLayers = 0,
                TotalLayers = 0,
                ModelSizeBytes = modelSize,
                EstimatedVramNeededBytes = 0,
                UsableVramBytes = 0,
                Rationale = "配置已关闭 GPU 加速，使用确定性 CPU 运行模式"
            };
        }

        // 2. 系统中未检测到支持硬件加速的显卡
        if (!hwContext.HasNvidiaGpu && !hwContext.HasDirectX12)
        {
            return new GpuOffloadPlan
            {
                GpuLayers = 0,
                TotalLayers = 0,
                ModelSizeBytes = modelSize,
                EstimatedVramNeededBytes = 0,
                UsableVramBytes = 0,
                Rationale = "未检测到支持的独立显卡，自动回退纯 CPU 运行模式"
            };
        }

        // 3. 读取 GGUF 元数据或基于模型大小估算总层数
        var metadata = modelFile.Exists ? GgufMetadataReader.ReadMetadata(modelPath) : null;
        var totalLayers = metadata?.BlockCount ?? EstimateLayersByFileSize(modelSize);

        // 4. 用户指定了明确的层数参数
        if (explicitLayers.HasValue && explicitLayers.Value >= 0)
        {
            return new GpuOffloadPlan
            {
                GpuLayers = explicitLayers.Value,
                TotalLayers = totalLayers,
                ModelSizeBytes = modelSize,
                EstimatedVramNeededBytes = 0,
                UsableVramBytes = hwContext.PrimaryGpu?.TotalVramBytes ?? 0,
                Rationale = $"使用用户显式指定的 GPU Offload 层数: {explicitLayers.Value}/{totalLayers}"
            };
        }

        // 5. 动态评估显存与层数
        var totalVram = hwContext.PrimaryGpu?.TotalVramBytes ?? 0;
        var usableVram = totalVram > SystemVramReservedBytes ? totalVram - SystemVramReservedBytes : 0;

        // 估算上下文 KV 缓存显存需求（按每 1024 token 约 50MB 预估）
        var ctxKb = (ulong)Math.Max(contextLength, 512) * 48UL;
        var estimatedKvCache = ctxKb * 1024UL;
        // 模型全载所需显存 = 权重大小 * 1.12 (元数据与运行时开销) + KV Cache
        var fullOffloadNeeded = (ulong)(modelSize * 1.12) + estimatedKvCache;

        // 场景 A: 显存充裕，全量卸载 (n_gpu_layers = -1)
        if (usableVram >= fullOffloadNeeded)
        {
            var plan = new GpuOffloadPlan
            {
                GpuLayers = -1,
                TotalLayers = totalLayers,
                ModelSizeBytes = modelSize,
                EstimatedVramNeededBytes = fullOffloadNeeded,
                UsableVramBytes = usableVram,
                Rationale = $"GPU 显存充裕 (可用 {FormatBytes(usableVram)} >= 需求 {FormatBytes(fullOffloadNeeded)})，模型全部层 ({totalLayers}/{totalLayers}) 卸载至 GPU"
            };

            _logger?.LogInformation("[GpuOffloadCalculator] 动态规划结果: {Rationale}", plan.Rationale);
            return plan;
        }

        // 场景 B: 显存满足部分卸载 (至少可载入 35% 层数)
        if (usableVram >= fullOffloadNeeded * 0.35 && totalLayers > 0)
        {
            var layerRatio = (double)usableVram / fullOffloadNeeded;
            var offloadLayers = Math.Clamp((int)(totalLayers * layerRatio), 1, totalLayers - 1);

            var plan = new GpuOffloadPlan
            {
                GpuLayers = offloadLayers,
                TotalLayers = totalLayers,
                ModelSizeBytes = modelSize,
                EstimatedVramNeededBytes = fullOffloadNeeded,
                UsableVramBytes = usableVram,
                Rationale = $"GPU 显存部分满足 (可用 {FormatBytes(usableVram)} / 需求 {FormatBytes(fullOffloadNeeded)})，自适应配置部分 Offload ({offloadLayers}/{totalLayers} 层)"
            };

            _logger?.LogInformation("[GpuOffloadCalculator] 动态规划结果: {Rationale}", plan.Rationale);
            return plan;
        }

        // 场景 C: 显存严重不足，为避免 CUDA OOM 崩溃，降级为 CPU
        var fallbackPlan = new GpuOffloadPlan
        {
            GpuLayers = 0,
            TotalLayers = totalLayers,
            ModelSizeBytes = modelSize,
            EstimatedVramNeededBytes = fullOffloadNeeded,
            UsableVramBytes = usableVram,
            Rationale = $"可用显存过小 ({FormatBytes(usableVram)})，不足以安全加载模型，自动降级纯 CPU 模式以确保稳定性"
        };

        _logger?.LogWarning("[GpuOffloadCalculator] 显存不足，降级处理: {Rationale}", fallbackPlan.Rationale);
        return fallbackPlan;
    }

    private static int EstimateLayersByFileSize(ulong modelSizeBytes)
    {
        var mb = modelSizeBytes / (1024 * 1024);
        if (mb <= 600) return 12;       // 如 bge-small, mini-bert
        if (mb <= 1600) return 24;      // 如 bge-large, 1B 模型
        if (mb <= 3500) return 28;      // 如 Qwen2.5-3B 等
        if (mb <= 8000) return 32;      // 如 LLaMA-3-8B 等
        return 40;                      // 14B+ 大模型
    }

    private static string FormatBytes(ulong bytes)
    {
        if (bytes >= 1024UL * 1024 * 1024)
            return $"{Math.Round((double)bytes / (1024 * 1024 * 1024), 2)} GB";
        if (bytes >= 1024UL * 1024)
            return $"{Math.Round((double)bytes / (1024 * 1024), 1)} MB";
        return $"{bytes} B";
    }
}
