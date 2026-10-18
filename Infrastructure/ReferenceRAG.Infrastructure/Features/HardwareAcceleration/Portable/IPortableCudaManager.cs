namespace ReferenceRAG.Core.Features.HardwareAcceleration.Portable;

/// <summary>
/// 便携式 CUDA 运行时管理器接口
/// </summary>
public interface IPortableCudaManager
{
    /// <summary>
    /// 便携式 CUDA 运行库是否已就绪
    /// </summary>
    bool IsInstalled();

    /// <summary>
    /// 获取便携式运行库安装目录
    /// </summary>
    string GetInstallDirectory();

    /// <summary>
    /// 解压安装本地运行库压缩包 (zip)
    /// </summary>
    Task<bool> InstallFromArchiveAsync(string archivePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 从镜像下载并解压便携式运行库
    /// </summary>
    Task<bool> DownloadAndInstallAsync(string? customUrl = null, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
}
