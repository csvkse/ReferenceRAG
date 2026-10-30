using System.IO;
using System.IO.Compression;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using ReferenceRAG.ApiHost.Services;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Infrastructure.Features.FileSystem;

namespace ReferenceRAG.ApiHost.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class FileSystemController : ControllerBase
{
    private const long MaxTextBytes = 2 * 1024 * 1024; // 2MB
    private readonly ILogger<FileSystemController> _logger;
    private readonly IFileReaderService _fileReaderService;
    private readonly IFileSearchEngine? _fileSearchEngine;

    public FileSystemController(
        ILogger<FileSystemController> logger,
        IFileReaderService? fileReaderService = null,
        IFileSearchEngine? fileSearchEngine = null)
    {
        _logger = logger;
        _fileReaderService = fileReaderService ?? new FileReaderService();
        _fileSearchEngine = fileSearchEngine;
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

    [HttpGet("read-text")]
    [HttpGet("content")]
    public async Task<IActionResult> ReadText([FromQuery] string path, [FromQuery] string? sourcePath = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            return BadRequest(new { error = "路径不能为空" });

        string normalized;
        try { normalized = FileBrowserPath.Normalize(path); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }

        if (!string.IsNullOrWhiteSpace(sourcePath))
        {
            string normSource;
            try { normSource = FileBrowserPath.Normalize(sourcePath); }
            catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }

            if (!FileBrowserPath.IsWithin(normalized, normSource))
                return StatusCode(StatusCodes.Status403Forbidden, new { error = "无权限访问超出指定知识源范围的路径" });
        }

        if (!System.IO.File.Exists(normalized))
            return NotFound(new { error = $"文件不存在: {normalized}" });

        var info = new FileInfo(normalized);
        if (info.Length > MaxTextBytes)
            return BadRequest(new { error = $"文件大小超过 2MB 限制（当前 {info.Length / 1024 / 1024}MB），无法以在线文本查看，请直接下载", isTooLarge = true, isBinary = false });

        try
        {
            using (var fs = new FileStream(normalized, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var buffer = new byte[Math.Min((int)info.Length, 8192)];
                var read = await fs.ReadAsync(buffer);
                if (buffer.Take(read).Contains((byte)0))
                    return BadRequest(new { error = "检测到二进制文件，无法以文本模式预览或编辑", isTooLarge = false, isBinary = true });
            }

            var content = await System.IO.File.ReadAllTextAsync(normalized, Encoding.UTF8);
            return Ok(new ReadTextResponse(
                Path: normalized,
                Name: info.Name,
                Size: info.Length,
                Content: content,
                Modified: info.LastWriteTimeUtc
            ));
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = $"无权限读取该文件: {normalized}" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "读取文件失败: {Path}", normalized);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = $"读取文件失败: {ex.Message}" });
        }
    }

    [HttpGet("read-range")]
    [HttpGet("lines")]
    public async Task<IActionResult> ReadLineRange(
        [FromQuery] string path,
        [FromQuery] int startLine = 1,
        [FromQuery] int endLine = 200,
        [FromQuery] string? sourcePath = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            return BadRequest(new { error = "路径不能为空" });

        string normalized;
        try { normalized = FileBrowserPath.Normalize(path); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }

        if (!string.IsNullOrWhiteSpace(sourcePath))
        {
            string normSource;
            try { normSource = FileBrowserPath.Normalize(sourcePath); }
            catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }

            if (!FileBrowserPath.IsWithin(normalized, normSource))
                return StatusCode(StatusCodes.Status403Forbidden, new { error = "无权限访问超出指定知识源范围的路径" });
        }

        if (!System.IO.File.Exists(normalized))
            return NotFound(new { error = $"文件不存在: {normalized}" });

        try
        {
            var result = await _fileReaderService.ReadLineRangeAsync(normalized, startLine, endLine, ct: HttpContext.RequestAborted);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "流式按行读取失败: {Path}", normalized);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = $"读取失败: {ex.Message}" });
        }
    }

    [HttpGet("metadata")]
    [HttpGet("outline")]
    public async Task<IActionResult> GetMetadata([FromQuery] string path, [FromQuery] string? sourcePath = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            return BadRequest(new { error = "路径不能为空" });

        string normalized;
        try { normalized = FileBrowserPath.Normalize(path); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }

        if (!string.IsNullOrWhiteSpace(sourcePath))
        {
            string normSource;
            try { normSource = FileBrowserPath.Normalize(sourcePath); }
            catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }

            if (!FileBrowserPath.IsWithin(normalized, normSource))
                return StatusCode(StatusCodes.Status403Forbidden, new { error = "无权限访问超出指定知识源范围的路径" });
        }

        if (!System.IO.File.Exists(normalized))
            return NotFound(new { error = $"文件不存在: {normalized}" });

        try
        {
            var result = await _fileReaderService.GetMetadataAsync(normalized, ct: HttpContext.RequestAborted);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取文件大纲元数据失败: {Path}", normalized);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = $"获取失败: {ex.Message}" });
        }
    }

    [HttpPost("search")]
    public async Task<IActionResult> SearchFiles([FromBody] FileSearchQuery query)
    {
        if (query == null || string.IsNullOrWhiteSpace(query.Pattern))
            return BadRequest(new { error = "搜索关键词或模式不能为空" });

        if (_fileSearchEngine == null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "文件搜索引擎未就绪" });

        // 数据源 ABAC 隔离下推
        var currentKey = HttpContext.Items["CurrentApiKey"] as ApiKeyEntity;
        var effectiveSources = ApiKeyPermissionMatrix.GetEffectiveAllowedSources(currentKey);
        if (effectiveSources != null)
        {
            if (query.Sources == null || query.Sources.Count == 0)
            {
                query.Sources = effectiveSources.ToList();
            }
            else
            {
                query.Sources = query.Sources
                    .Where(s => effectiveSources.Contains(s, StringComparer.OrdinalIgnoreCase))
                    .ToList();
                if (query.Sources.Count == 0)
                {
                    return Ok(new FileSearchResult(
                        Success: true,
                        QueryPattern: query.Pattern,
                        IsRegex: query.IsRegex,
                        TotalMatches: 0,
                        MatchedFilesCount: 0,
                        TotalFilesScanned: 0,
                        ElapsedMilliseconds: 0,
                        Truncated: false,
                        Files: new List<FileSearchMatchGroup>()
                    ));
                }
            }
        }

        try
        {
            var result = await _fileSearchEngine.SearchAsync(query, HttpContext.RequestAborted);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "文件搜索异常: {Pattern}", query.Pattern);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = $"搜索失败: {ex.Message}" });
        }
    }

    [HttpGet("find-files")]
    public async Task<IActionResult> FindFiles(
        [FromQuery] string? sourceName = null,
        [FromQuery] string? globPattern = "*",
        [FromQuery] int maxResults = 100)
    {
        if (_fileSearchEngine == null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "文件搜索引擎未就绪" });

        var currentKey = HttpContext.Items["CurrentApiKey"] as ApiKeyEntity;
        var effectiveSources = ApiKeyPermissionMatrix.GetEffectiveAllowedSources(currentKey);
        if (effectiveSources != null)
        {
            if (!string.IsNullOrWhiteSpace(sourceName) && !effectiveSources.Contains(sourceName, StringComparer.OrdinalIgnoreCase))
            {
                return StatusCode(StatusCodes.Status403Forbidden, new { error = $"无权限访问数据源: {sourceName}" });
            }
        }

        try
        {
            var results = await _fileSearchEngine.FindFilesAsync(sourceName, globPattern ?? "*", maxResults, HttpContext.RequestAborted);
            return Ok(new { success = true, count = results.Count, files = results });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "通配查找文件失败: {Pattern}", globPattern);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = $"查找失败: {ex.Message}" });
        }
    }

    [HttpPost("write-text")]
    [HttpPost("content")]
    public async Task<IActionResult> WriteText([FromBody] WriteTextRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.Path))
            return BadRequest(new { error = "路径不能为空" });

        string normalized;
        try { normalized = FileBrowserPath.Normalize(request.Path); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }

        if (!string.IsNullOrWhiteSpace(request.SourcePath))
        {
            string normSource;
            try { normSource = FileBrowserPath.Normalize(request.SourcePath); }
            catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }

            if (!FileBrowserPath.IsWithin(normalized, normSource))
                return StatusCode(StatusCodes.Status403Forbidden, new { error = "无权限在指定知识源范围之外写入文件" });
        }

        var contentBytes = Encoding.UTF8.GetByteCount(request.Content ?? string.Empty);
        if (contentBytes > MaxTextBytes)
            return BadRequest(new { error = $"内容大小超过 2MB 限制（当前 {contentBytes / 1024 / 1024}MB）" });

        var parent = Path.GetDirectoryName(normalized);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
            return BadRequest(new { error = "目标上级目录不存在，请先创建目录" });

        try
        {
            await System.IO.File.WriteAllTextAsync(normalized, request.Content ?? string.Empty, new UTF8Encoding(false));
            _logger.LogInformation("文本文件保存成功: {Path}", normalized);
            return Ok(new WriteTextResponse(true, normalized, "文件保存成功"));
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = $"无权限写入该文件: {normalized}" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "写入文件失败: {Path}", normalized);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = $"写入文件失败: {ex.Message}" });
        }
    }

    [HttpPost("action")]
    public IActionResult ExecuteAction([FromBody] FileActionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.Action) || string.IsNullOrWhiteSpace(request?.SourcePath))
            return BadRequest(new { error = "动作与源路径不能为空" });

        string from;
        try { from = FileBrowserPath.Normalize(request.SourcePath); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }

        if (FileBrowserPath.IsRoot(from))
            return BadRequest(new { error = "禁止对系统根目录进行破坏性操作" });

        if (!string.IsNullOrWhiteSpace(request.SandBoxRoot))
        {
            string normRoot;
            try { normRoot = FileBrowserPath.Normalize(request.SandBoxRoot); }
            catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }

            if (!FileBrowserPath.IsWithin(from, normRoot))
                return StatusCode(StatusCodes.Status403Forbidden, new { error = "源路径越出授权的知识源沙箱范围" });

            if (!string.IsNullOrWhiteSpace(request.TargetPath))
            {
                string normTarget;
                try { normTarget = FileBrowserPath.Normalize(request.TargetPath); }
                catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }

                if (!FileBrowserPath.IsWithin(normTarget, normRoot))
                    return StatusCode(StatusCodes.Status403Forbidden, new { error = "目标路径越出授权的知识源沙箱范围" });
            }
        }

        try
        {
            switch (request.Action.ToLowerInvariant())
            {
                case "delete":
                    if (System.IO.File.Exists(from))
                    {
                        System.IO.File.Delete(from);
                        _logger.LogInformation("文件删除成功: {Path}", from);
                        return Ok(new FileActionResponse(true, "delete", from, null, "文件删除成功"));
                    }
                    if (Directory.Exists(from))
                    {
                        if (!request.Recursive && Directory.EnumerateFileSystemEntries(from).Any())
                            return BadRequest(new { error = "目标目录非空，请确认后执行递归删除", isNonEmptyDir = true });

                        Directory.Delete(from, request.Recursive);
                        _logger.LogInformation("目录删除成功: {Path}", from);
                        return Ok(new FileActionResponse(true, "delete", from, null, "目录删除成功"));
                    }
                    return NotFound(new { error = $"路径不存在: {from}" });

                case "rename":
                case "move":
                    if (string.IsNullOrWhiteSpace(request.TargetPath))
                        return BadRequest(new { error = "移动或重命名操作必须指定目标路径 TargetPath" });

                    var to = FileBrowserPath.Normalize(request.TargetPath);
                    if (FileBrowserPath.IsRoot(to))
                        return BadRequest(new { error = "目标路径不能为根目录" });

                    var targetParent = Path.GetDirectoryName(to);
                    if (!string.IsNullOrEmpty(targetParent) && !Directory.Exists(targetParent))
                        Directory.CreateDirectory(targetParent);

                    if (System.IO.File.Exists(from))
                    {
                        if (System.IO.File.Exists(to))
                            return Conflict(new { error = "目标位置已存在同名文件" });
                        System.IO.File.Move(from, to);
                        _logger.LogInformation("文件重命名/移动成功: {From} -> {To}", from, to);
                        return Ok(new FileActionResponse(true, request.Action, from, to, "文件移动/重命名成功"));
                    }
                    if (Directory.Exists(from))
                    {
                        if (Directory.Exists(to))
                            return Conflict(new { error = "目标位置已存在同名目录" });
                        Directory.Move(from, to);
                        _logger.LogInformation("目录重命名/移动成功: {From} -> {To}", from, to);
                        return Ok(new FileActionResponse(true, request.Action, from, to, "目录移动/重命名成功"));
                    }
                    return NotFound(new { error = $"源路径不存在: {from}" });

                case "copy":
                    if (string.IsNullOrWhiteSpace(request.TargetPath))
                        return BadRequest(new { error = "复制操作必须指定目标路径 TargetPath" });

                    var dest = FileBrowserPath.Normalize(request.TargetPath);
                    var destParent = Path.GetDirectoryName(dest);
                    if (!string.IsNullOrEmpty(destParent) && !Directory.Exists(destParent))
                        Directory.CreateDirectory(destParent);

                    if (System.IO.File.Exists(from))
                    {
                        if (System.IO.File.Exists(dest))
                            return Conflict(new { error = "目标位置已存在同名文件" });
                        System.IO.File.Copy(from, dest);
                        _logger.LogInformation("文件复制成功: {From} -> {Dest}", from, dest);
                        return Ok(new FileActionResponse(true, "copy", from, dest, "文件复制成功"));
                    }
                    if (Directory.Exists(from))
                    {
                        if (Directory.Exists(dest))
                            return Conflict(new { error = "目标位置已存在同名目录" });
                        CopyDirectoryRecursively(from, dest);
                        _logger.LogInformation("目录复制成功: {From} -> {Dest}", from, dest);
                        return Ok(new FileActionResponse(true, "copy", from, dest, "目录复制成功"));
                    }
                    return NotFound(new { error = $"源路径不存在: {from}" });

                default:
                    return BadRequest(new { error = $"不支持的操作动作: {request.Action}，仅支持 delete/rename/move/copy" });
            }
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "没有权限执行该文件操作" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "执行文件操作失败: {Action} on {From}", request.Action, from);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = $"执行文件操作失败: {ex.Message}" });
        }
    }

    [HttpPost("upload")]
    public async Task<IActionResult> Upload([FromQuery] string path, IFormFile? file, [FromQuery] string? sourcePath = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            return BadRequest(new { error = "上传目标目录不能为空" });

        string normalized;
        try { normalized = FileBrowserPath.Normalize(path); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }

        if (!string.IsNullOrWhiteSpace(sourcePath))
        {
            string normSource;
            try { normSource = FileBrowserPath.Normalize(sourcePath); }
            catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }

            if (!FileBrowserPath.IsWithin(normalized, normSource))
                return StatusCode(StatusCodes.Status403Forbidden, new { error = "上传目标越出授权的知识源沙箱范围" });
        }

        if (!Directory.Exists(normalized))
            return BadRequest(new { error = $"目标目录不存在: {normalized}" });

        if (file == null || file.Length == 0)
            return BadRequest(new { error = "未选择有效文件或文件内容为空" });

        var fileName = Path.GetFileName(file.FileName);
        if (string.IsNullOrWhiteSpace(fileName))
            return BadRequest(new { error = "文件名无效" });

        var target = UniquePath(normalized, fileName);
        try
        {
            await using (var stream = System.IO.File.Create(target))
            {
                await file.CopyToAsync(stream);
            }
            _logger.LogInformation("文件上传成功: {Target}", target);
            return Ok(new UploadFileResponse(true, target, Path.GetFileName(target), file.Length, "上传成功"));
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = $"无权限上传到该目录: {normalized}" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "文件上传失败: {Target}", target);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = $"文件上传失败: {ex.Message}" });
        }
    }

    [HttpGet("download")]
    [HttpGet("download-zip")]
    public IActionResult Download([FromQuery] string path, [FromQuery] string? sourcePath = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            return BadRequest(new { error = "路径不能为空" });

        string normalized;
        try { normalized = FileBrowserPath.Normalize(path); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }

        if (FileBrowserPath.IsRoot(normalized))
            return BadRequest(new { error = "不能下载系统根目录" });

        if (!string.IsNullOrWhiteSpace(sourcePath))
        {
            string normSource;
            try { normSource = FileBrowserPath.Normalize(sourcePath); }
            catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }

            if (!FileBrowserPath.IsWithin(normalized, normSource))
                return StatusCode(StatusCodes.Status403Forbidden, new { error = "下载目标越出授权的知识源沙箱范围" });
        }

        if (System.IO.File.Exists(normalized))
        {
            var fileName = Path.GetFileName(normalized);
            return PhysicalFile(normalized, "application/octet-stream", fileName);
        }

        if (Directory.Exists(normalized))
        {
            var folderName = FileBrowserPath.Name(normalized);
            if (string.IsNullOrWhiteSpace(folderName) || folderName == "磁盘") folderName = "source_folder";
            var downloadName = $"{folderName}.zip";

            var tempZipPath = Path.Combine(Path.GetTempPath(), $"rrag_dl_{Guid.NewGuid():N}.zip");
            try
            {
                BuildDirectoryZip(normalized, tempZipPath);
            }
            catch (Exception ex)
            {
                try { if (System.IO.File.Exists(tempZipPath)) System.IO.File.Delete(tempZipPath); } catch { }
                return BadRequest(new { error = $"打包文件夹失败: {ex.Message}" });
            }

            var stream = new FileStream(tempZipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.DeleteOnClose);
            return File(stream, "application/zip", downloadName);
        }

        return NotFound(new { error = $"路径不存在: {normalized}" });
    }

    private static void CopyDirectoryRecursively(string sourceDir, string destinationDir)
    {
        var dir = new DirectoryInfo(sourceDir);
        if (!dir.Exists)
            throw new DirectoryNotFoundException($"源目录未找到: {dir.FullName}");

        Directory.CreateDirectory(destinationDir);

        foreach (var file in dir.GetFiles())
        {
            var targetFilePath = Path.Combine(destinationDir, file.Name);
            file.CopyTo(targetFilePath, false);
        }

        foreach (var subDir in dir.GetDirectories())
        {
            var nextTargetSubDir = Path.Combine(destinationDir, subDir.Name);
            CopyDirectoryRecursively(subDir.FullName, nextTargetSubDir);
        }
    }

    private static void BuildDirectoryZip(string rootPath, string zipFilePath)
    {
        const int maxFiles = 20000;
        const long maxTotalBytes = 2L * 1024 * 1024 * 1024; // 2GB

        int fileCount = 0;
        long totalBytes = 0;

        using var archive = ZipFile.Open(zipFilePath, ZipArchiveMode.Create, Encoding.UTF8);
        var rootDir = new DirectoryInfo(rootPath);

        void AddDirectory(DirectoryInfo currentDir, string relativePath)
        {
            if ((currentDir.Attributes & FileAttributes.ReparsePoint) != 0) return;

            foreach (var subDir in currentDir.EnumerateDirectories())
            {
                if ((subDir.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                var nextRel = string.IsNullOrEmpty(relativePath) ? subDir.Name : $"{relativePath}/{subDir.Name}";
                archive.CreateEntry($"{nextRel}/");
                AddDirectory(subDir, nextRel);
            }

            foreach (var file in currentDir.EnumerateFiles())
            {
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (++fileCount > maxFiles)
                    throw new InvalidOperationException($"文件夹内文件数量过多（超过 {maxFiles} 个），建议使用命令行打包");

                totalBytes += file.Length;
                if (totalBytes > maxTotalBytes)
                    throw new InvalidOperationException($"文件夹总大小超过安全上限（{maxTotalBytes / 1024 / 1024 / 1024}GB）");

                var entryName = string.IsNullOrEmpty(relativePath) ? file.Name : $"{relativePath}/{file.Name}";
                var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
                entry.LastWriteTime = file.LastWriteTime;

                using var fs = file.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var entryStream = entry.Open();
                fs.CopyTo(entryStream);
            }
        }

        AddDirectory(rootDir, "");
    }

    private static string UniquePath(string dir, string fileName)
    {
        var target = FileBrowserPath.Join(dir, fileName);
        if (!System.IO.File.Exists(target) && !Directory.Exists(target))
        {
            return target;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (int i = 1; i < 10000; i++)
        {
            var candidate = FileBrowserPath.Join(dir, $"{stem} ({i}){ext}");
            if (!System.IO.File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }
        return FileBrowserPath.Join(dir, $"{stem}_{Guid.NewGuid():N}{ext}");
    }
}

public sealed record ReadTextResponse(
    string Path,
    string Name,
    long Size,
    string Content,
    DateTime Modified
);

public sealed record WriteTextRequest(
    string Path,
    string Content,
    string? SourcePath = null
);

public sealed record WriteTextResponse(
    bool Success,
    string Path,
    string Message
);

public sealed record FileActionRequest(
    string Action,
    string SourcePath,
    string? TargetPath = null,
    bool Recursive = false,
    string? SandBoxRoot = null
);

public sealed record FileActionResponse(
    bool Success,
    string Action,
    string SourcePath,
    string? TargetPath,
    string Message
);

public sealed record UploadFileResponse(
    bool Success,
    string Path,
    string Name,
    long Size,
    string Message
);

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
