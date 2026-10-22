using System.IO;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using ReferenceRAG.ApiHost.Services;

namespace ReferenceRAG.ApiHost.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class FileSystemController : ControllerBase
{
    private readonly ILogger<FileSystemController> _logger;

    public FileSystemController(ILogger<FileSystemController> logger)
    {
        _logger = logger;
    }

    [HttpGet("browse")]
    [HttpGet]
    public IActionResult Browse([FromQuery] string? path = null, [FromQuery] bool folderOnly = false)
    {
        string normalized;
        try
        {
            normalized = FileBrowserPath.Normalize(path);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }

        // Windows 虚拟根：列出所有驱动器
        if (OperatingSystem.IsWindows() && normalized == "/")
        {
            try
            {
                var drives = DriveInfo.GetDrives()
                    .Where(d =>
                    {
                        try { return d.IsReady && Directory.Exists(d.Name); }
                        catch { return false; }
                    })
                    .Select(d =>
                    {
                        var drivePath = d.Name.Replace('\\', '/');
                        var label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "" : $" ({d.VolumeLabel})";
                        return new FileSystemEntry(
                            Name: $"{drivePath.TrimEnd('/')}{label}",
                            Path: drivePath,
                            IsDirectory: true,
                            Size: 0,
                            Modified: DateTime.MinValue
                        );
                    })
                    .ToList();

                return Ok(new FileSystemBrowseResponse(
                    Path: "/",
                    Parent: null,
                    Name: "磁盘",
                    IsRoot: true,
                    IsVirtualRoot: true,
                    Entries: drives,
                    TotalDirectories: drives.Count,
                    TotalFiles: 0,
                    TotalFilesSizeBytes: 0
                ));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取磁盘列表失败");
                return StatusCode(StatusCodes.Status500InternalServerError, new { error = $"获取磁盘列表失败: {ex.Message}" });
            }
        }

        if (!Directory.Exists(normalized))
        {
            return NotFound(new { error = $"目录不存在: {normalized}" });
        }

        var entries = new List<FileSystemEntry>();
        int dirCount = 0;
        int fileCount = 0;
        long totalFilesSizeBytes = 0;

        try
        {
            var dirInfo = new DirectoryInfo(normalized);
            var enumOptions = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
                MatchCasing = MatchCasing.CaseInsensitive
            };

            // 列出子目录
            foreach (var dir in dirInfo.EnumerateDirectories("*", enumOptions))
            {
                dirCount++;
                try
                {
                    entries.Add(new FileSystemEntry(
                        Name: dir.Name,
                        Path: dir.FullName.Replace('\\', '/'),
                        IsDirectory: true,
                        Size: 0,
                        Modified: dir.LastWriteTimeUtc
                    ));
                }
                catch (UnauthorizedAccessException)
                {
                    // 单个子目录无权限访问时忽略，不阻断父目录的展示
                }
                catch (Exception)
                {
                    // 忽略异常的单个子目录
                }
            }

            // 列出文件或统计文件数
            foreach (var file in dirInfo.EnumerateFiles("*", enumOptions))
            {
                fileCount++;
                long fileSize = 0;
                try
                {
                    fileSize = file.Length;
                    totalFilesSizeBytes += fileSize;
                }
                catch { }

                if (!folderOnly)
                {
                    try
                    {
                        entries.Add(new FileSystemEntry(
                            Name: file.Name,
                            Path: file.FullName.Replace('\\', '/'),
                            IsDirectory: false,
                            Size: fileSize,
                            Modified: file.LastWriteTimeUtc
                        ));
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "跳过无法读取的文件条目: {Name}", file.Name);
                    }
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = $"无权限读取该目录: {normalized}" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "读取目录失败: {Path}", normalized);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = $"读取目录失败: {ex.Message}" });
        }

        var sortedEntries = entries
            .OrderByDescending(e => e.IsDirectory)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Ok(new FileSystemBrowseResponse(
            Path: normalized,
            Parent: FileBrowserPath.Parent(normalized),
            Name: FileBrowserPath.Name(normalized),
            IsRoot: FileBrowserPath.IsRoot(normalized),
            IsVirtualRoot: false,
            Entries: sortedEntries,
            TotalDirectories: dirCount,
            TotalFiles: fileCount,
            TotalFilesSizeBytes: totalFilesSizeBytes
        ));
    }

    [HttpPost("mkdir")]
    public IActionResult CreateDirectory([FromBody] CreateDirectoryRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.Path))
        {
            return BadRequest(new { error = "路径不能为空" });
        }

        string normalized;
        try
        {
            normalized = FileBrowserPath.Normalize(request.Path);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }

        if (Directory.Exists(normalized))
        {
            return Conflict(new { error = "目标目录已存在" });
        }

        try
        {
            Directory.CreateDirectory(normalized);
            _logger.LogInformation("成功创建目录: {Path}", normalized);
            return Ok(new CreateDirectoryResponse(true, normalized, "目录创建成功"));
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = $"无权限在目标位置创建目录: {normalized}" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "创建目录失败: {Path}", normalized);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = $"创建目录失败: {ex.Message}" });
        }
    }
}

public sealed record FileSystemEntry(
    string Name,
    string Path,
    bool IsDirectory,
    long Size,
    DateTime Modified
);

public sealed record FileSystemBrowseResponse(
    string Path,
    string? Parent,
    string Name,
    bool IsRoot,
    bool IsVirtualRoot,
    List<FileSystemEntry> Entries,
    int TotalDirectories = 0,
    int TotalFiles = 0,
    long TotalFilesSizeBytes = 0
);

public sealed record CreateDirectoryRequest(
    string Path
);

public sealed record CreateDirectoryResponse(
    bool Success,
    string Path,
    string Message
);
