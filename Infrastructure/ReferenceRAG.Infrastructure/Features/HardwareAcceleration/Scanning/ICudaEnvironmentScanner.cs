using ReferenceRAG.Core.Features.HardwareAcceleration.Models;

namespace ReferenceRAG.Core.Features.HardwareAcceleration.Scanning;

/// <summary>
/// CUDA 运行库环境扫描器接口
/// </summary>
public interface ICudaEnvironmentScanner
{
    /// <summary>
    /// 扫描系统与已知环境，寻找匹配的 CUDA 12 运行时并注入当前进程
    /// </summary>
    /// <param name="configuredPath">用户配置的候选路径（最高优先级）</param>
    /// <returns>扫描与注入结果</returns>
    CudaEnvironmentScanResult ScanAndInject(string? configuredPath = null);

    /// <summary>
    /// 仅探测候选目录列表（不执行动态注入）
    /// </summary>
    IReadOnlyList<string> ProbeCandidateDirectories(string? configuredPath = null);
}
