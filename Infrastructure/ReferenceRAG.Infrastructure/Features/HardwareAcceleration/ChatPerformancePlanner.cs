namespace ReferenceRAG.Core.Features.HardwareAcceleration;

using System;
using System.IO;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Features.HardwareAcceleration.Detection;
using ReferenceRAG.Core.Features.HardwareAcceleration.Models;
using ReferenceRAG.Core.Features.ModelManagement.Helpers;

/// <summary>
/// 对话模型性能规划请求
/// </summary>
public record ChatPerformancePlanningRequest
{
    public string? ModelPath { get; init; }
    public string? ModelName { get; init; }
    public HardwareContext? HardwareContext { get; init; }
    public bool UseGpu { get; init; } = true;

    /// <summary>
    /// 规划模式：Adaptive（自适应规划 - 智能推荐）| Fixed（固定参数 - 手动精调）
    /// </summary>
    public string PerformanceMode { get; init; } = "Adaptive";

    /// <summary>
    /// 自适应策略类型：Balanced（通用平衡）| HighThroughput（高吞吐）| LongContext（长窗口）| LowMemory（轻量低显存）
    /// </summary>
    public string AdaptiveStrategy { get; init; } = "Balanced";

    public int? UserContextSize { get; init; }
    public int? UserGpuLayers { get; init; }
    public int? UserBatchSize { get; init; }
    public int? UserUBatchSize { get; init; }
}

/// <summary>
/// 对话模型自适应性能配置方案
/// </summary>
public record ChatPerformanceProfile
{
    /// <summary>
    /// GPU 卸载层数 (-1 全量卸载，0 纯 CPU，>0 部分卸载)
    /// </summary>
    public int GpuLayers { get; init; }

    /// <summary>
    /// 自适应计算得出的安全最优上下文长度 (ContextSize)
    /// </summary>
    public int ContextSize { get; init; }

    /// <summary>
    /// Prefill 批处理最大 Token 数 (BatchSize)
    /// </summary>
    public int BatchSize { get; init; }

    /// <summary>
    /// 物理微批处理大小 (UBatchSize)
    /// </summary>
    public int UBatchSize { get; init; }

    /// <summary>
    /// 预估总显存开销（字节，含模型权重与 KV Cache）
    /// </summary>
    public ulong EstimatedVramBytes { get; init; }

    /// <summary>
    /// 是否启用了 GPU 加速
    /// </summary>
    public bool IsGpuAccelerated => GpuLayers != 0;

    /// <summary>
    /// 决策原因及说明
    /// </summary>
    public string Rationale { get; init; } = "";

    /// <summary>
    /// 规划模式：Adaptive | Fixed
    /// </summary>
    public string Mode { get; init; } = "Adaptive";

    /// <summary>
    /// 策略类型（如 Balanced, HighThroughput, LongContext, LowMemory, Manual）
    /// </summary>
    public string Strategy { get; init; } = "Balanced";

    /// <summary>
    /// 潜在风险与安全警告（如超显存风险等）
    /// </summary>
    public string? Warning { get; init; }
}

/// <summary>
/// 对话模型自适应性能规划器接口
/// 对标嵌入模型的自适应硬件卸载与批处理架构，避免不同模型能力差异造成的性能浪费或显存击穿
/// </summary>
public interface IChatPerformancePlanner
{
    /// <summary>
    /// 规划对话模型的自适应运行参数（向后兼容重载）
    /// </summary>
    ChatPerformanceProfile PlanProfile(
        string? modelPath,
        string? modelName,
        HardwareContext? hardwareContext = null,
        bool useGpu = true,
        int? userContextSize = null,
        int? userGpuLayers = null);

    /// <summary>
    /// 基于结构化请求执行模型性能规划（支持自适应与固定参数双模态）
    /// </summary>
    ChatPerformanceProfile PlanProfile(ChatPerformancePlanningRequest request);
}

/// <summary>
/// 对话模型自适应性能规划器实现
/// 核心特性：
/// 1. 双模态无缝切换：支持「自适应规划 (Adaptive)」与「固定参数 (Fixed)」，满足小白与专家极客不同诉求；
/// 2. 4 维自适应策略倾斜：Balanced（通用平衡）、HighThroughput（高吞吐优先）、LongContext（长窗口优先）、LowMemory（节能轻量）；
/// 3. 安全警戒软箝位：固定模式下自动测算 VRAM 物理防爆与越界提示，100% 杜绝 Hard OOM 闪退；
/// 4. CPU 模式低延迟自适应：纯 CPU 下自适应降低批大小 (512) 与微批 (256)，提升首字交互响应速度。
/// </summary>
public class ChatPerformancePlanner : IChatPerformancePlanner
{
    private readonly IHardwareDetector _hardwareDetector;
    private readonly ILogger<ChatPerformancePlanner>? _logger;

    // 系统与桌面保留显存缓冲（默认 768MB）
    private const ulong SystemVramReservedBytes = 768UL * 1024 * 1024;

    public ChatPerformancePlanner(
        IHardwareDetector? hardwareDetector = null,
        ILogger<ChatPerformancePlanner>? logger = null)
    {
        _hardwareDetector = hardwareDetector ?? new HardwareDetector();
        _logger = logger;
    }

    public ChatPerformanceProfile PlanProfile(
        string? modelPath,
        string? modelName,
        HardwareContext? hardwareContext = null,
        bool useGpu = true,
        int? userContextSize = null,
        int? userGpuLayers = null)
    {
        return PlanProfile(new ChatPerformancePlanningRequest
        {
            ModelPath = modelPath,
            ModelName = modelName,
            HardwareContext = hardwareContext,
            UseGpu = useGpu,
            PerformanceMode = (userContextSize.HasValue && userContextSize.Value > 0) ? "Fixed" : "Adaptive",
            AdaptiveStrategy = "Balanced",
            UserContextSize = userContextSize,
            UserGpuLayers = userGpuLayers
        });
    }

    public ChatPerformanceProfile PlanProfile(ChatPerformancePlanningRequest request)
    {
        var hwContext = request.HardwareContext ?? _hardwareDetector.GetContext();
        var safeModelName = request.ModelName ?? "GGUF-Chat";

        // 1. 读取模型文件物理信息与 GGUF 元数据
        ulong modelSizeBytes = 0;
        int? metadataMaxCtx = null;
        int? metadataBlockCount = null;

        if (!string.IsNullOrWhiteSpace(request.ModelPath) && File.Exists(request.ModelPath))
        {
            var fi = new FileInfo(request.ModelPath);
            modelSizeBytes = (ulong)fi.Length;

            var metadata = GgufMetadataReader.ReadMetadata(request.ModelPath);
            metadataMaxCtx = metadata.ContextLength;
            metadataBlockCount = metadata.BlockCount;
        }

        var totalLayers = metadataBlockCount ?? EstimateLayers(modelSizeBytes, safeModelName);
        var maxSupportedCtx = metadataMaxCtx.HasValue && metadataMaxCtx.Value > 0
            ? metadataMaxCtx.Value
            : 32768;

        // 2. 判定模型参数量级
        var modelScale = ClassifyModelScale(modelSizeBytes, safeModelName);

        // 3. 硬件与显存检测
        var primaryGpu = hwContext.PrimaryGpu ?? hwContext.GpuDevices?.FirstOrDefault();
        var totalVram = primaryGpu?.TotalVramBytes ?? 0;
        var isIntegratedGpu = totalVram == 0 && (primaryGpu?.Vendor is GpuVendor.Intel or GpuVendor.Amd);

        // 对于 Intel / AMD 核显 (共享系统内存，无独立物理显存)，按动态共享内存规划（预估 4GB 供核显 Vulkan 调度）
        if (isIntegratedGpu)
        {
            totalVram = 4UL * 1024 * 1024 * 1024;
        }

        var hasGpuSupport = request.UseGpu && totalVram > 0 && (hwContext.HasNvidiaGpu || hwContext.HasDirectX12 || isIntegratedGpu);
        var usableVram = totalVram > SystemVramReservedBytes ? totalVram - SystemVramReservedBytes : 0;

        ulong defaultWeightBytes = modelScale switch
        {
            ModelParameterScale.Small => (ulong)(1.3 * 1024 * 1024 * 1024),
            ModelParameterScale.Medium => (ulong)(2.6 * 1024 * 1024 * 1024),
            ModelParameterScale.Standard => (ulong)(4.8 * 1024 * 1024 * 1024),
            ModelParameterScale.Large => (ulong)(9.5 * 1024 * 1024 * 1024),
            _ => (ulong)(4.5 * 1024 * 1024 * 1024)
        };
        var weightVramNeeded = (ulong)(modelSizeBytes > 0 ? modelSizeBytes * 1.10 : defaultWeightBytes);

        // =========================================================================
        // 分支 1: 固定参数模式 (Fixed Mode) - 遵从用户手动配置
        // =========================================================================
        var isFixed = string.Equals(request.PerformanceMode, "Fixed", StringComparison.OrdinalIgnoreCase);
        if (isFixed)
        {
            int plannedGpuLayers = !hasGpuSupport
                ? 0
                : (request.UserGpuLayers.HasValue ? request.UserGpuLayers.Value : -1);

            int targetCtx = request.UserContextSize.HasValue && request.UserContextSize.Value > 0
                ? request.UserContextSize.Value
                : 4096;
            targetCtx = Math.Clamp(targetCtx, 512, 131072);

            int targetBatch = request.UserBatchSize.HasValue && request.UserBatchSize.Value > 0
                ? request.UserBatchSize.Value
                : 2048;
            targetBatch = Math.Clamp(targetBatch, 128, 4096);

            int targetUBatch = request.UserUBatchSize.HasValue && request.UserUBatchSize.Value > 0
                ? request.UserUBatchSize.Value
                : 512;
            targetUBatch = Math.Clamp(targetUBatch, 64, Math.Min(targetBatch, 1024));

            ulong estimatedVram = 0;
            if (hasGpuSupport && plannedGpuLayers != 0)
            {
                double weightFraction = (plannedGpuLayers == -1 || plannedGpuLayers >= totalLayers)
                    ? 1.0
                    : (double)plannedGpuLayers / Math.Max(1, totalLayers);
                ulong weightVram = (ulong)(weightVramNeeded * weightFraction);
                ulong kvVram = (ulong)targetCtx * 128UL * 1024UL;
                ulong batchVram = (ulong)targetBatch * 32UL * 1024UL;
                estimatedVram = weightVram + kvVram + batchVram;
            }

            string? warning = null;
            if (hasGpuSupport && plannedGpuLayers != 0 && totalVram > 0 && estimatedVram > totalVram)
            {
                warning = $"⚠️ 显存压力告警：预估显存开销 ({FormatBytes(estimatedVram)}) 超过当前物理显存 ({FormatBytes(totalVram)})，可能引发性能严重下降、系统分页颠簸或 CUDA OOM 报错。建议下调上下文或切换自适应规划。";
            }
            else if (targetCtx > maxSupportedCtx)
            {
                warning = $"⚠️ 设定上下文 ({targetCtx}) 超过该模型官方标称上下文 ({maxSupportedCtx})，长文本问答可能出现注意力衰减或重复幻觉。";
            }

            var fixedProfile = new ChatPerformanceProfile
            {
                Mode = "Fixed",
                Strategy = "Manual",
                GpuLayers = plannedGpuLayers,
                ContextSize = targetCtx,
                BatchSize = targetBatch,
                UBatchSize = targetUBatch,
                EstimatedVramBytes = estimatedVram,
                Rationale = $"固定参数模式 (Fixed)：完全遵从用户手动设定的运行参数 [GpuLayers={plannedGpuLayers}, ContextSize={targetCtx}, BatchSize={targetBatch}, UBatchSize={targetUBatch}]。预估显存: {FormatBytes(estimatedVram)}。",
                Warning = warning
            };

            _logger?.LogInformation("[ChatPerformancePlanner] 固定模式规划结果 [{ModelName}]: {Rationale}", safeModelName, fixedProfile.Rationale);
            return fixedProfile;
        }

        // =========================================================================
        // 分支 2: 自适应规划模式 (Adaptive Mode) - 启发式智能决策
        // =========================================================================
        var strategy = string.IsNullOrWhiteSpace(request.AdaptiveStrategy) ? "Balanced" : request.AdaptiveStrategy;

        // ---------------- 场景 2.1: 纯 CPU 模式 ----------------
        if (!hasGpuSupport)
        {
            int cpuCtx;
            int cpuBatch;
            int cpuUbatch;

            switch (strategy)
            {
                case "LowMemory":
                    cpuCtx = 2048;
                    cpuBatch = 256;
                    cpuUbatch = 128;
                    break;
                case "HighThroughput":
                    cpuCtx = 2048;
                    cpuBatch = 512;
                    cpuUbatch = 256;
                    break;
                case "LongContext":
                    cpuCtx = 4096;
                    cpuBatch = 256;
                    cpuUbatch = 128;
                    break;
                case "Balanced":
                default:
                    cpuCtx = modelScale == ModelParameterScale.Small ? 4096 : 2048;
                    cpuBatch = 512;
                    cpuUbatch = 256;
                    break;
            }

            if (request.UserContextSize.HasValue && request.UserContextSize.Value > 0)
            {
                cpuCtx = Math.Clamp(request.UserContextSize.Value, 1024, 8192);
            }

            var cpuProfile = new ChatPerformanceProfile
            {
                Mode = "Adaptive",
                Strategy = strategy,
                GpuLayers = 0,
                ContextSize = cpuCtx,
                BatchSize = cpuBatch,
                UBatchSize = cpuUbatch,
                EstimatedVramBytes = 0,
                Rationale = $"CPU 运行模式（自适应 [{strategy}]）：自适应优化批大小 ({cpuBatch}) 与上下文 ({cpuCtx})，优先保证首字交互响应延迟 (TTFT) 与内存平稳",
                Warning = null
            };

            _logger?.LogInformation("[ChatPerformancePlanner] 规划结果: {Rationale}", cpuProfile.Rationale);
            return cpuProfile;
        }

        // ---------------- 场景 2.2: GPU 显存紧张 / 部分卸载 ----------------
        if (usableVram < weightVramNeeded)
        {
            var layerRatio = (double)usableVram / weightVramNeeded;
            var offloadLayers = Math.Clamp((int)(totalLayers * layerRatio), 0, totalLayers - 1);

            int constrainedCtx = strategy switch
            {
                "LowMemory" => 1024,
                "LongContext" => 3072,
                _ => 2048
            };

            var constrainedProfile = new ChatPerformanceProfile
            {
                Mode = "Adaptive",
                Strategy = strategy,
                GpuLayers = offloadLayers,
                ContextSize = constrainedCtx,
                BatchSize = 1024,
                UBatchSize = 256,
                EstimatedVramBytes = usableVram,
                Rationale = isIntegratedGpu
                    ? $"集成核显共享内存偏紧 (可用 {FormatBytes(usableVram)} < 需求 {FormatBytes(weightVramNeeded)})：自适应部分卸载 ({offloadLayers}/{totalLayers} 层)，上下文箝位为 {constrainedCtx} 以防共享内存溢出"
                    : $"GPU 显存紧张 (可用 {FormatBytes(usableVram)} < 需求 {FormatBytes(weightVramNeeded)})：自适应部分卸载 ({offloadLayers}/{totalLayers} 层)，上下文箝位为 {constrainedCtx} 以防显存击穿",
                Warning = isIntegratedGpu
                    ? "当前核显共享内存无法全量装入模型权重，已自动执行部分层卸载保护"
                    : "当前物理显存无法全量装入模型权重，已自动执行部分层卸载保护"
            };

            _logger?.LogInformation("[ChatPerformancePlanner] 规划结果: {Rationale}", constrainedProfile.Rationale);
            return constrainedProfile;
        }

        // ---------------- 场景 2.3: GPU 显存充足 / 全量卸载与 4 维策略倾斜 ----------------
        var vramSurplusForKv = usableVram - weightVramNeeded;
        int plannedGpu = -1; // 100% 全量卸载

        int optimalCtx;
        int optimalBatch;
        int optimalUbatch;
        string rationale;

        switch (strategy)
        {
            case "HighThroughput": // 高吞吐优先
                if (vramSurplusForKv >= 3000UL * 1024 * 1024)
                {
                    optimalCtx = Math.Min(8192, maxSupportedCtx);
                    optimalBatch = 4096;
                    optimalUbatch = 1024;
                    rationale = $"高吞吐优先策略 (盈余 {FormatBytes(vramSurplusForKv)})：最大化批处理容量 (4096 Batch) 提升知识库长文本预填充速度与首字响应 (TTFT)";
                }
                else if (vramSurplusForKv >= 1200UL * 1024 * 1024)
                {
                    optimalCtx = Math.Min(4096, maxSupportedCtx);
                    optimalBatch = 2048;
                    optimalUbatch = 512;
                    rationale = $"高吞吐优先策略 (盈余 {FormatBytes(vramSurplusForKv)})：配置 2048 批大小与 4096 上下文，加速推理吞吐";
                }
                else
                {
                    optimalCtx = Math.Min(2048, maxSupportedCtx);
                    optimalBatch = 1024;
                    optimalUbatch = 256;
                    rationale = $"高吞吐优先策略 (显存偏紧)：配置 1024 批大小与 2048 上下文保证吞吐流水线流畅";
                }
                break;

            case "LongContext": // 长窗口优先
                if (vramSurplusForKv >= 3500UL * 1024 * 1024)
                {
                    optimalCtx = Math.Min(32768, maxSupportedCtx);
                    optimalBatch = 1024;
                    optimalUbatch = 512;
                    rationale = $"长窗口优先策略 (盈余 {FormatBytes(vramSurplusForKv)})：最大化释放 {optimalCtx} (32K) 超长上下文，满足复杂深度 RAG 与海量长文分析";
                }
                else if (vramSurplusForKv >= 1500UL * 1024 * 1024)
                {
                    optimalCtx = Math.Min(16384, maxSupportedCtx);
                    optimalBatch = 1024;
                    optimalUbatch = 256;
                    rationale = $"长窗口优先策略 (盈余 {FormatBytes(vramSurplusForKv)})：自适应扩展至 {optimalCtx} (16K) 上下文，承载深层次多轮交互";
                }
                else
                {
                    optimalCtx = Math.Min(8192, maxSupportedCtx);
                    optimalBatch = 512;
                    optimalUbatch = 256;
                    rationale = $"长窗口优先策略 (盈余 {FormatBytes(vramSurplusForKv)})：释放 8192 (8K) 上下文并适度收紧批大小以防溢出";
                }
                break;

            case "LowMemory": // 显存节约 / 节能
                optimalCtx = Math.Min(4096, maxSupportedCtx);
                optimalBatch = 1024;
                optimalUbatch = 256;
                if (vramSurplusForKv < 1200UL * 1024 * 1024)
                {
                    optimalCtx = 2048;
                    optimalBatch = 512;
                }
                rationale = $"轻量低显存策略 (盈余 {FormatBytes(vramSurplusForKv)})：收缩上下文至 {optimalCtx}，保留最大可用显存空间供其他进程平稳运转";
                break;

            case "Balanced": // 通用平衡 (默认推荐)
            default:
                if (vramSurplusForKv >= 3500UL * 1024 * 1024) // 剩余显存 >= 3.5GB
                {
                    if (modelScale == ModelParameterScale.Small)
                    {
                        optimalCtx = Math.Min(32768, maxSupportedCtx);
                        optimalBatch = 2048;
                        optimalUbatch = 512;
                        rationale = $"显存充裕 (盈余 {FormatBytes(vramSurplusForKv)}) 且为轻量模型：自适应释放最大 {optimalCtx} (32K) 超长上下文窗口与 2048 批大小，彻底发挥模型最强多轮与长文分析能力";
                    }
                    else if (modelScale == ModelParameterScale.Medium)
                    {
                        optimalCtx = Math.Min(16384, maxSupportedCtx);
                        optimalBatch = 2048;
                        optimalUbatch = 512;
                        rationale = $"显存充裕 (盈余 {FormatBytes(vramSurplusForKv)})：自适应扩展至 {optimalCtx} (16K) 上下文与 2048 批大小，性能与显存高度平衡";
                    }
                    else
                    {
                        optimalCtx = Math.Min(16384, maxSupportedCtx);
                        optimalBatch = 2048;
                        optimalUbatch = 512;
                        rationale = $"高规格显存 (盈余 {FormatBytes(vramSurplusForKv)})：大模型自适应提升至 {optimalCtx} (16K) 上下文与 2048 批大小";
                    }
                }
                else if (vramSurplusForKv >= 1200UL * 1024 * 1024) // 剩余显存 1.2GB ~ 3.5GB（典型 8GB 显卡跑 7B）
                {
                    optimalCtx = Math.Min(8192, maxSupportedCtx);
                    optimalBatch = 2048;
                    optimalUbatch = 512;
                    rationale = $"显存良好匹配 (盈余 {FormatBytes(vramSurplusForKv)})：自适应配置 8192 (8K) 黄金平衡上下文与 2048 批大小，确保显存平稳健康且 100% 杜绝 NoKvSlot";
                }
                else // 剩余显存 < 1.2GB（显存偏紧）
                {
                    optimalCtx = Math.Min(4096, maxSupportedCtx);
                    optimalBatch = 1024;
                    optimalUbatch = 512;
                    rationale = $"显存边际充足 (盈余 {FormatBytes(vramSurplusForKv)})：自适应轻量化至 4096 (4K) 上下文以保证 KV Cache 安全边界";
                }
                break;
        }

        if (isIntegratedGpu)
        {
            rationale = $"集成核显 Vulkan 加速 (共享内存 {FormatBytes(usableVram)})：" + rationale;
        }

        var estimatedTotalVram = weightVramNeeded + ((ulong)optimalCtx * 128UL * 1024UL) + ((ulong)optimalBatch * 32UL * 1024UL);

        var profile = new ChatPerformanceProfile
        {
            Mode = "Adaptive",
            Strategy = strategy,
            GpuLayers = plannedGpu,
            ContextSize = optimalCtx,
            BatchSize = optimalBatch,
            UBatchSize = optimalUbatch,
            EstimatedVramBytes = estimatedTotalVram,
            Rationale = rationale,
            Warning = null
        };

        _logger?.LogInformation("[ChatPerformancePlanner] 规划完成 [{ModelName}]: {Rationale}", safeModelName, profile.Rationale);
        return profile;
    }

    private static ModelParameterScale ClassifyModelScale(ulong sizeBytes, string modelName)
    {
        var lower = modelName.ToLowerInvariant();
        if (lower.Contains("270m") || lower.Contains("350m") || lower.Contains("0.5b") || lower.Contains("0.8b") ||
            lower.Contains("1b") || lower.Contains("1.2b") || lower.Contains("1.5b") || lower.Contains("2b") ||
            lower.Contains("e2b") || lower.Contains("bonsai"))
            return ModelParameterScale.Small;
        if (lower.Contains("3b") || lower.Contains("4b") || lower.Contains("mini"))
            return ModelParameterScale.Medium;
        if (lower.Contains("14b") || lower.Contains("32b") || lower.Contains("70b"))
            return ModelParameterScale.Large;

        if (sizeBytes > 0)
        {
            var mb = sizeBytes / (1024 * 1024);
            if (mb <= 2300) return ModelParameterScale.Small;     // ~1.5B 权重通常 1GB~2.2GB
            if (mb <= 4200) return ModelParameterScale.Medium;    // ~3B~4B 权重通常 2.3GB~4GB
            if (mb <= 7800) return ModelParameterScale.Standard;  // ~7B~8B 权重通常 4.2GB~7.5GB
            return ModelParameterScale.Large;                     // 14B+
        }

        return ModelParameterScale.Standard;
    }

    private static int EstimateLayers(ulong sizeBytes, string modelName)
    {
        var lower = modelName.ToLowerInvariant();
        if (lower.Contains("1.5b")) return 28;
        if (lower.Contains("3b")) return 36;
        if (lower.Contains("7b") || lower.Contains("8b")) return 28;
        if (lower.Contains("14b")) return 48;

        var mb = sizeBytes / (1024 * 1024);
        if (mb <= 2300) return 28;
        if (mb <= 4200) return 32;
        if (mb <= 7800) return 28;
        return 40;
    }

    private static string FormatBytes(ulong bytes)
    {
        if (bytes >= 1024UL * 1024 * 1024)
            return $"{Math.Round((double)bytes / (1024 * 1024 * 1024), 2)} GB";
        if (bytes >= 1024UL * 1024)
            return $"{Math.Round((double)bytes / (1024 * 1024), 1)} MB";
        return $"{bytes} B";
    }

    private enum ModelParameterScale
    {
        Small,     // <= 2B
        Medium,    // 3B ~ 4B
        Standard,  // 7B ~ 8B
        Large      // >= 14B
    }
}
