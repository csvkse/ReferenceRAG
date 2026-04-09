using Microsoft.AspNetCore.Mvc;
using ObsidianRAG.Core.Interfaces;
using ObsidianRAG.Core.Models;
using ObsidianRAG.Core.Services;
using ObsidianRAG.Service.Services;

namespace ObsidianRAG.Service.Controllers;

/// <summary>
/// 源文件夹管理 API
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class SourcesController : ControllerBase
{
    private readonly ConfigManager _configManager;
    private readonly IVectorStore _vectorStore;
    private readonly IndexService _indexService;
    private readonly ILogger<SourcesController> _logger;

    public SourcesController(
        ConfigManager configManager,
        IVectorStore vectorStore,
        IndexService indexService,
        ILogger<SourcesController> logger)
    {
        _configManager = configManager;
        _vectorStore = vectorStore;
        _indexService = indexService;
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
            source.Name = request.Name;
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
    /// 启动索引
    /// </summary>
    [HttpPost("{name}/index")]
    public async Task<ActionResult<IndexJob>> StartIndex(string name, [FromBody] IndexOptions? options = null)
    {
        var config = _configManager.Load();
        var source = config.Sources.FirstOrDefault(s => s.Name == name);

        if (source == null)
        {
            return NotFound(new { error = $"源 '{name}' 不存在" });
        }

        var job = await _indexService.StartIndexAsync(new IndexRequest
        {
            Sources = new List<string> { name },
            Force = options?.Force ?? false
        });

        return AcceptedAtAction(nameof(IndexController.GetStatus), "Index", new { indexId = job.Id }, job);
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
/// 索引选项
/// </summary>
public class IndexOptions
{
    public bool Force { get; set; }
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
