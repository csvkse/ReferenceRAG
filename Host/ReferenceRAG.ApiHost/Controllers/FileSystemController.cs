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
    public IActionResult Browse([FromQuery] string? path = null, [FromQuery] bool folderOnly = true)
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
                    Entries: drives
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

        try
        {
            var dirInfo = new DirectoryInfo(normalized);

            // 列出子目录
            foreach (var dir in dirInfo.EnumerateDirectories())
            {
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

            // 如果不仅限文件夹，列出文件
            if (!folderOnly)
            {
                foreach (var file in dirInfo.EnumerateFiles())
                {
                    try
                    {
                        entries.Add(new FileSystemEntry(
                            Name: file.Name,
                            Path: file.FullName.Replace('\\', '/'),
                            IsDirectory: false,
                            Size: file.Length,
                            Modified: file.LastWriteTimeUtc
                        ));
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                    catch (Exception)
                    {
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
            Entries: sortedEntries
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
    List<FileSystemEntry> Entries
);

public sealed record CreateDirectoryRequest(
    string Path
);

public sealed record CreateDirectoryResponse(
    bool Success,
    string Path,
    string Message
);
