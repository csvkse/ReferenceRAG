using System.IO.Compression;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Features.HardwareAcceleration.Detection;

namespace ReferenceRAG.Core.Features.HardwareAcceleration.Portable;

/// <summary>
/// 便携式 CUDA 运行时管理器
/// 负责在 ./runtimes/cuda 中部署与维护极简 CUDA 12 核心 DLL
/// </summary>
public class PortableCudaManager : IPortableCudaManager
{
    private readonly ILogger<PortableCudaManager>? _logger;
    private readonly HttpClient _httpClient;

    // 默认备选官方与镜像加速源
    private static readonly string[] DefaultMirrorUrls =
    {
        "https://hf-mirror.com/datasets/reference-rag/runtimes/resolve/main/cuda12-win-x64-minimal.zip",
        "https://github.com/reference-rag/runtimes/releases/download/v1.0.0/cuda12-win-x64-minimal.zip"
    };

    public PortableCudaManager(HttpClient? httpClient = null, ILogger<PortableCudaManager>? logger = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _logger = logger;
    }

    public bool IsInstalled()
    {
        var targetDir = GetInstallDirectory();
        if (!Directory.Exists(targetDir)) return false;

        var cublasLt = Path.Combine(targetDir, "cublasLt64_12.dll");
        var cublas = Path.Combine(targetDir, "cublas64_12.dll");
        return File.Exists(cublasLt) && File.Exists(cublas);
    }

    public string GetInstallDirectory()
    {
        return HardwareDetector.GetPortableCudaDirectory();
    }

    public async Task<bool> InstallFromArchiveAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(archivePath))
        {
            _logger?.LogWarning("[PortableCudaManager] 压缩包不存在: {Path}", archivePath);
            return false;
        }

        var targetDir = GetInstallDirectory();
        Directory.CreateDirectory(targetDir);

        try
        {
            await Task.Run(() =>
            {
                ZipFile.ExtractToDirectory(archivePath, targetDir, overwriteFiles: true);
            }, cancellationToken);

            var installed = IsInstalled();
            _logger?.LogInformation("[PortableCudaManager] 运行库解压安装完成，有效性={Installed}", installed);
            return installed;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[PortableCudaManager] 解压运行库失败: {Path}", archivePath);
            return false;
        }
    }

    public async Task<bool> DownloadAndInstallAsync(string? customUrl = null, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var urls = !string.IsNullOrWhiteSpace(customUrl)
            ? new[] { customUrl }
            : DefaultMirrorUrls;

        var tempZipPath = Path.Combine(Path.GetTempPath(), $"cuda12-minimal-{Guid.NewGuid():N}.zip");

        foreach (var url in urls)
        {
            try
            {
                _logger?.LogInformation("[PortableCudaManager] 尝试从镜像下载 CUDA 微型运行库: {Url}", url);
                using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!response.IsSuccessStatusCode) continue;

                var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                await using (var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken))
                await using (var fileStream = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[81920];
                    long totalRead = 0;
                    int bytesRead;

                    while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                        totalRead += bytesRead;
                        if (totalBytes > 0 && progress != null)
                        {
                            progress.Report((double)totalRead / totalBytes);
                        }
                    }
                }

                var success = await InstallFromArchiveAsync(tempZipPath, cancellationToken);
                if (success) return true;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[PortableCudaManager] 下载失败，尝试下一个镜像源: {Url}", url);
            }
            finally
            {
                if (File.Exists(tempZipPath))
                {
                    try { File.Delete(tempZipPath); } catch { }
                }
            }
        }

        return false;
    }
}
