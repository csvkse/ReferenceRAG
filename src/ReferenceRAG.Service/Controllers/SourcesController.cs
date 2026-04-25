using Microsoft.AspNetCore.Mvc;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Service.Services;

namespace ReferenceRAG.Service.Controllers;

/// <summary>
/// 源文件夹管理 API
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class SourcesController : ControllerBase
{
    private readonly ConfigManager _configManager;
    private readonly IVectorStore _vectorStore;
    private readonly ILogger<SourcesController> _logger;

    public SourcesController(
        ConfigManager configManager,
        IVectorStore vectorStore,
        ILogger<SourcesController> logger)
    {
        _configManager = configManager;
        _vectorStore = vectorStore;
        _logger = logger;
    }

    /// <summary>
    /// 获取所有源
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<SourceDetail>>> GetAll()
    {
        var config = _configManager.Load();
        var files = await _vectorStore.GetAllFilesAsync();
        var fileList = files.ToList();

        var sources = config.Sources.Select(s =>
        {
            var sourceFiles = fileList.Where(f => f.Source == s.Name).ToList();
            return new SourceDetail
            {
                Name = s.Name,
                Path = s.Path,
                Type = s.Type.ToString(),
                Enabled = s.Enabled,
                Recursive = s.Recursive,
                FilePatterns = s.FilePatterns,
                FileCount = sourceFiles.Count,
                ChunkCount = sourceFiles.Sum(f => f.ChunkCount),
                LastIndexed = sourceFiles.Max(f => f.ModifiedAt)
            };
        }).ToList();

        return Ok(sources);
    }

    /// <summary>
    /// 获取所有路径（源和文件夹）
    /// </summary>
    /// <remarks>
    /// 返回所有已配置的源及其包含的文件夹路径列表
    /// </remarks>
    [HttpGet("~/api/paths")]
    public async Task<ActionResult<PathsResponse>> GetPaths()
    {
        var config = _configManager.Load();
        var files = await _vectorStore.GetAllFilesAsync();
        var fileList = files.ToList();

        var response = new PathsResponse
        {
            Sources = config.Sources.Select(s =>
            {
                var sourceFiles = fileList.Where(f => f.Source == s.Name).ToList();

                // 从文件路径中提取唯一的文件夹路径
                var folders = sourceFiles
                    .Select(f =>
                    {
                        // 获取文件的父文件夹路径
                        var parentFolder = Path.GetDirectoryName(f.Path);
                        return parentFolder;
                    })
                    .Where(p => !string.IsNullOrEmpty(p))
                    .Distinct()
                    .OrderBy(p => p)
                    .ToList();

                return new SourcePathInfo
                {
                    Name = s.Name,
                    RootPath = s.Path,
                    Folders = folders
                };
            }).ToList()
        };

        return Ok(response);
    }

    /// <summary>
    /// 获取单个源详情
    /// </summary>
    [HttpGet("{name}")]
    public async Task<ActionResult<SourceDetail>> Get(string name)
    {
        var config = _configManager.Load();
        var source = config.Sources.FirstOrDefault(s => s.Name == name);

        if (source == null)
        {
            return NotFound(new { error = $"源 '{name}' 不存在" });
        }

        var files = await _vectorStore.GetAllFilesAsync();
        var sourceFiles = files.Where(f => f.Source == name).ToList();

        var detail = new SourceDetail
        {
            Name = source.Name,
            Path = source.Path,
            Type = source.Type.ToString(),
            Enabled = source.Enabled,
            Recursive = source.Recursive,
            FilePatterns = source.FilePatterns,
            FileCount = sourceFiles.Count,
            ChunkCount = sourceFiles.Sum(f => f.ChunkCount),
            LastIndexed = sourceFiles.Max(f => f.ModifiedAt)
        };

        return Ok(detail);
    }

    /// <summary>
    /// 获取源文件列表
    /// </summary>
    [HttpGet("{name}/files")]
    public async Task<ActionResult<List<FileDetail>>> GetFiles(string name, [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        var config = _configManager.Load();
        var source = config.Sources.FirstOrDefault(s => s.Name == name);

        if (source == null)
        {
            return NotFound(new { error = $"源 '{name}' 不存在" });
        }

        var files = await _vectorStore.GetAllFilesAsync();
        var sourceFiles = files
            .Where(f => f.Source == name)
            .OrderByDescending(f => f.ModifiedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(f => new FileDetail
            {
                Path = f.Path,
                ChunkCount = f.ChunkCount,
                LastModified = f.ModifiedAt ?? DateTime.MinValue,
                Hash = f.ContentHash.Length >= 8 ? f.ContentHash[..8] : f.ContentHash
            })
            .ToList();

        return Ok(sourceFiles);
    }

    /// <summary>
    /// 添加源
    /// </summary>
    [HttpPost]
    public ActionResult<SourceFolder> Add([FromBody] AddSourceRequest request)
    {
        if (string.IsNullOrEmpty(request.Path))
        {
            return BadRequest(new { error = "路径不能为空" });
        }

        // Path traversal prevention
        if (request.Path.Contains(".."))
        {
            return BadRequest(new { error = "路径不能包含 '..' 目录遍历字符" });
        }

        if (!Path.IsPathRooted(request.Path))
        {
            return BadRequest(new { error = "路径必须是绝对路径" });
        }

        var fullPath = Path.GetFullPath(request.Path);
        if (!Directory.Exists(fullPath))
        {
            return BadRequest(new { error = $"目录不存在: {fullPath}" });
        }

        var config = _configManager.Load();

        // 检查是否已存在（使用不区分大小写的路径比较）
        var existingSource = config.Sources.FirstOrDefault(s =>
            s.Path.Equals(fullPath, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrEmpty(request.Name) && s.Name.Equals(request.Name, StringComparison.OrdinalIgnoreCase)));

        if (existingSource != null)
        {
            return Conflict(new { error = $"源已存在: {existingSource.Name} ({existingSource.Path})" });
        }

        var source = new SourceFolder
        {
            Path = fullPath,
            Name = request.Name ?? Path.GetFileName(request.Path) ?? "新源",
            Type = request.Type ?? SourceType.Markdown,
            Enabled = true,
            Recursive = request.Recursive ?? true,
            FilePatterns = request.FilePatterns?.ToList() ?? new List<string> { "*.md" },
            ExcludeDirs = new List<string> { ".git", "node_modules" }
        };

        // Obsidian 类型自动添加排除目录
        if (source.Type == SourceType.Obsidian)
        {
            source.ExcludeDirs.AddRange(new[] { ".obsidian", ".trash" });
        }

        try
        {
            _configManager.AddSource(source);
            _logger.LogInformation("已添加源: {Name} ({Path})", source.Name, source.Path);
            return CreatedAtAction(nameof(Get), new { name = source.Name }, source);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "添加源失败: {Path}", request.Path);
            _logger.LogError(ex, "添加源失败: {Path}", request.Path);
            return StatusCode(500, new { error = "添加源失败，请查看服务日志" });
        }
    }

    /// <summary>
    /// 更新源
    /// </summary>
    [HttpPut("{name}")]
    public ActionResult Update(string name, [FromBody] UpdateSourceRequest request)
    {
        var config = _configManager.Load();
        var source = config.Sources.FirstOrDefault(s => s.Name == name);

        if (source == null)
        {
            return NotFound(new { error = $"源 '{name}' 不存在" });
        }

        if (request.Name != null && request.Name != name)
        {
            return BadRequest(new { error = "不支持重命名源，请删除后重新添加" });
        }

        if (request.Enabled.HasValue)
        {
            source.Enabled = request.Enabled.Value;
        }

        if (request.Recursive.HasValue)
        {
            source.Recursive = request.Recursive.Value;
        }

        if (request.FilePatterns != null)
        {
            source.FilePatterns = request.FilePatterns.ToList();
        }

        _configManager.Save(config);

        return Ok(source);
    }

    /// <summary>
    /// 删除源
    /// </summary>
    [HttpDelete("{name}")]
    public async Task<ActionResult> Delete(string name, [FromQuery] bool deleteData = false)
    {
        var config = _configManager.Load();
        var source = config.Sources.FirstOrDefault(s => s.Name == name);

        if (source == null)
        {
            return NotFound(new { error = $"源 '{name}' 不存在" });
        }

        _configManager.RemoveSource(name);

        if (deleteData)
        {
            // 删除相关的向量数据
            await _vectorStore.DeleteBySourceAsync(name);
        }

        return NoContent();
    }

    /// <summary>
    /// 启用/禁用源
    /// </summary>
    [HttpPatch("{name}/toggle")]
    public ActionResult Toggle(string name, [FromBody] ToggleRequest request)
    {
        _configManager.ToggleSource(name, request.Enabled);
        return Ok();
    }

    /// <summary>
    /// 扫描源文件（不索引）
    /// </summary>
    [HttpGet("{name}/scan")]
    public ActionResult<ScanResult> Scan(string name)
    {
        var config = _configManager.Load();
        var source = config.Sources.FirstOrDefault(s => s.Name == name);

        if (source == null)
        {
            return NotFound(new { error = $"源 '{name}' 不存在" });
        }

        // 转换路径格式（WSL 环境下将 Windows 路径转为 /mnt/x/ 格式）
        var normalizedPath = PathUtility.NormalizePath(source.Path);
        if (!Directory.Exists(normalizedPath))
        {
            return BadRequest(new { error = $"源目录不存在或无法访问: {source.Path}" });
        }

        var files = Directory.GetFiles(normalizedPath, "*.*",
            source.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .Where(f => source.FilePatterns.Any(p => MatchesPattern(f, p)))
            .Where(f => !source.ExcludeDirs.Any(d => f.Contains(d)))
            .ToList();

        var result = new ScanResult
        {
            TotalFiles = files.Count,
            TotalSize = files.Sum(f => new System.IO.FileInfo(f).Length),
            Extensions = files.GroupBy(f => Path.GetExtension(f).ToLower())
                .ToDictionary(g => g.Key, g => g.Count()),
            Files = files.Take(100).Select(f => new FileItem
            {
                Path = Path.GetRelativePath(normalizedPath, f),
                Size = new System.IO.FileInfo(f).Length,
                Modified = System.IO.File.GetLastWriteTime(f)
            }).ToList()
        };

        return Ok(result);
    }


    /// <summary>
    /// 按行范围批量获取文件分段内容
    /// </summary>
    [HttpPost("file/lines")]
    public async Task<ActionResult<FileLinesResponse>> GetFileLinesBatch([FromBody] FileLinesRequest request)
    {
        if (request.Items == null || request.Items.Count == 0)
            return BadRequest(new { error = "items 不能为空" });

        var config = _configManager.Load();
        var sourcePaths = config.Sources
            .Select(s => Path.GetFullPath(s.Path))
            .ToList();

        var results = new List<FileLinesResult>();

        const int maxItems = 20;
        if (request.Items.Count > maxItems)
            return BadRequest(new { error = $"单次最多 {maxItems} 条，当前 {request.Items.Count} 条" });

        foreach (var item in request.Items)
        {
            if (string.IsNullOrWhiteSpace(item.Path) || item.Path.Contains(".."))
            {
                results.Add(new FileLinesResult { Path = item.Path ?? "", Error = "路径无效" });
                continue;
            }

            if (item.StartLine < 0 || item.EndLine < 0)
            {
                results.Add(new FileLinesResult { Path = item.Path, Error = "startLine / endLine 不能为负数" });
                continue;
            }

            if (item.StartLine > 0 && item.EndLine > 0 && item.StartLine > item.EndLine)
            {
                results.Add(new FileLinesResult { Path = item.Path, Error = $"startLine ({item.StartLine}) 不能大于 endLine ({item.EndLine})" });
                continue;
            }

            var normalizedPath = Path.GetFullPath(item.Path);

            if (!sourcePaths.Any(sp => normalizedPath.StartsWith(sp, StringComparison.OrdinalIgnoreCase)))
            {
                results.Add(new FileLinesResult { Path = item.Path, Error = "路径不在任何已注册源的范围内" });
                continue;
            }

            var file = await _vectorStore.GetFileByPathAsync(normalizedPath)
                       ?? await _vectorStore.GetFileByPathAsync(item.Path);

            if (file == null)
            {
                results.Add(new FileLinesResult { Path = item.Path, Error = "文件未被索引" });
                continue;
            }

            var allChunks = await _vectorStore.GetChunksByFileAsync(file.Id);

            // 行范围过滤：startLine=0 且 endLine=0 时返回全部
            var filtered = allChunks.OrderBy(c => c.ChunkOrder);
            var hasRange = item.StartLine > 0 || item.EndLine > 0;
            if (hasRange)
            {
                var start = item.StartLine > 0 ? item.StartLine : 1;
                var end = item.EndLine > 0 ? item.EndLine : int.MaxValue;
                filtered = filtered.Where(c => c.StartLine <= end && c.EndLine >= start)
                                   .OrderBy(c => c.ChunkOrder);
            }

            results.Add(new FileLinesResult
            {
                Path = file.Path,
                Title = file.Title,
                Source = file.Source,
                RequestedRange = hasRange
                    ? new LineRange { StartLine = item.StartLine, EndLine = item.EndLine }
                    : null,
                Chunks = filtered.Select(c => new FileChunkItem
                {
                    Index = c.ChunkIndex,
                    HeadingPath = c.HeadingPath,
                    StartLine = c.StartLine,
                    EndLine = c.EndLine,
                    Content = c.Content
                }).ToList()
            });
        }

        return Ok(new FileLinesResponse { Results = results });
    }

    /// <summary>
    /// 批量获取文件结构信息（章节目录 + 行号，不含正文内容）
    /// </summary>
    [HttpPost("files/info")]
    public async Task<ActionResult<FilesInfoResponse>> GetFilesInfo([FromBody] FilesInfoRequest request)
    {
        if (request.Paths == null || request.Paths.Count == 0)
            return BadRequest(new { error = "paths 不能为空" });

        const int maxPaths = 20;
        if (request.Paths.Count > maxPaths)
            return BadRequest(new { error = $"单次最多 {maxPaths} 条，当前 {request.Paths.Count} 条" });

        var config = _configManager.Load();
        var sourcePaths = config.Sources
            .Select(s => Path.GetFullPath(s.Path))
            .ToList();

        var results = new List<FileInfoResult>();

        foreach (var rawPath in request.Paths)
        {
            if (string.IsNullOrWhiteSpace(rawPath) || rawPath.Contains(".."))
            {
                results.Add(new FileInfoResult { Path = rawPath ?? "", Error = "路径无效" });
                continue;
            }

            var normalizedPath = Path.GetFullPath(rawPath);

            if (!sourcePaths.Any(sp => normalizedPath.StartsWith(sp, StringComparison.OrdinalIgnoreCase)))
            {
                results.Add(new FileInfoResult { Path = rawPath, Error = "路径不在任何已注册源的范围内" });
                continue;
            }

            var file = await _vectorStore.GetFileByPathAsync(normalizedPath)
                       ?? await _vectorStore.GetFileByPathAsync(rawPath);

            if (file == null)
            {
                results.Add(new FileInfoResult { Path = rawPath, Error = "文件未被索引" });
                continue;
            }

            var chunks = (await _vectorStore.GetChunksByFileAsync(file.Id))
                .OrderBy(c => c.ChunkOrder)
                .ToList();

            results.Add(new FileInfoResult
            {
                Path = file.Path,
                Title = file.Title,
                Source = file.Source,
                TotalChunks = chunks.Count,
                TotalLines = chunks.Count > 0 ? chunks.Max(c => c.EndLine) : 0,
                Sections = chunks.Select(c => new FileSectionItem
                {
                    Index = c.ChunkIndex,
                    HeadingPath = c.HeadingPath,
                    StartLine = c.StartLine,
                    EndLine = c.EndLine
                }).ToList()
            });
        }

        return Ok(new FilesInfoResponse { Results = results });
    }

    private static bool MatchesPattern(string filePath, string pattern)
    {
        if (pattern.StartsWith("*."))
        {
            return filePath.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase);
        }
        return filePath.Contains(pattern, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// 源详情
/// </summary>
public class SourceDetail
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Type { get; set; } = "";
    public bool Enabled { get; set; }
    public bool Recursive { get; set; }
    public List<string> FilePatterns { get; set; } = new();
    public int FileCount { get; set; }
    public int ChunkCount { get; set; }
    public DateTime? LastIndexed { get; set; }
}

/// <summary>
/// 文件详情
/// </summary>
public class FileDetail
{
    public string Path { get; set; } = "";
    public int ChunkCount { get; set; }
    public DateTime LastModified { get; set; }
    public string Hash { get; set; } = "";
}

/// <summary>
/// 添加源请求
/// </summary>
public class AddSourceRequest
{
    public string? Path { get; set; }
    public string? Name { get; set; }
    public SourceType? Type { get; set; }
    public bool? Recursive { get; set; }
    public string[]? FilePatterns { get; set; }
}

/// <summary>
/// 更新源请求
/// </summary>
public class UpdateSourceRequest
{
    public string? Name { get; set; }
    public bool? Enabled { get; set; }
    public bool? Recursive { get; set; }
    public string[]? FilePatterns { get; set; }
}

/// <summary>
/// 切换请求
/// </summary>
public class ToggleRequest
{
    public bool Enabled { get; set; }
}

/// <summary>
/// 扫描结果
/// </summary>
public class ScanResult
{
    public int TotalFiles { get; set; }
    public long TotalSize { get; set; }
    public Dictionary<string, int> Extensions { get; set; } = new();
    public List<FileItem> Files { get; set; } = new();
}

/// <summary>
/// 文件项
/// </summary>
public class FileItem
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public DateTime Modified { get; set; }
}

/// <summary>
/// 路径查询响应
/// </summary>
public class PathsResponse
{
    /// <summary>
    /// 源列表
    /// </summary>
    public List<SourcePathInfo> Sources { get; set; } = new();
}

/// <summary>
/// 源路径信息
/// </summary>
public class SourcePathInfo
{
    /// <summary>
    /// 源名称
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// 源根路径
    /// </summary>
    public string RootPath { get; set; } = "";

    /// <summary>
    /// 该源下的文件夹路径列表
    /// </summary>
    public List<string> Folders { get; set; } = new();
}


/// <summary>
/// 文件分段项
/// </summary>
public class FileChunkItem
{
    public int Index { get; set; }
    public string? HeadingPath { get; set; }
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public string Content { get; set; } = "";
}

/// <summary>
/// 批量行范围查询请求
/// </summary>
public class FileLinesRequest
{
    public List<FileLinesItem> Items { get; set; } = new();
}

public class FileLinesItem
{
    public string Path { get; set; } = "";
    /// <summary>0 表示不限起始行</summary>
    public int StartLine { get; set; } = 0;
    /// <summary>0 表示不限结束行</summary>
    public int EndLine { get; set; } = 0;
}

/// <summary>
/// 批量行范围查询响应
/// </summary>
public class FileLinesResponse
{
    public List<FileLinesResult> Results { get; set; } = new();
}

public class FileLinesResult
{
    public string Path { get; set; } = "";
    public string? Title { get; set; }
    public string? Source { get; set; }
    public LineRange? RequestedRange { get; set; }
    public List<FileChunkItem> Chunks { get; set; } = new();
    public string? Error { get; set; }
}

public class LineRange
{
    public int StartLine { get; set; }
    public int EndLine { get; set; }
}

/// <summary>
/// 批量文件信息查询请求
/// </summary>
public class FilesInfoRequest
{
    public List<string> Paths { get; set; } = new();
}

/// <summary>
/// 批量文件信息查询响应
/// </summary>
public class FilesInfoResponse
{
    public List<FileInfoResult> Results { get; set; } = new();
}

public class FileInfoResult
{
    public string Path { get; set; } = "";
    public string? Title { get; set; }
    public string? Source { get; set; }
    public int TotalChunks { get; set; }
    public int TotalLines { get; set; }
    public List<FileSectionItem> Sections { get; set; } = new();
    public string? Error { get; set; }
}

/// <summary>
/// 文件章节项（不含正文内容）
/// </summary>
public class FileSectionItem
{
    public int Index { get; set; }
    public string? HeadingPath { get; set; }
    public int StartLine { get; set; }
    public int EndLine { get; set; }
}

