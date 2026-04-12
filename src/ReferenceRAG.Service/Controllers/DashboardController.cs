using Microsoft.AspNetCore.Mvc;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;

namespace ReferenceRAG.Service.Controllers;

/// <summary>
/// Dashboard API 控制器
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class DashboardController : ControllerBase
{
    private readonly IVectorStore _vectorStore;
    private readonly ConfigManager _configManager;
    private readonly QueryStatsService _statsService;
    private readonly ILogger<DashboardController> _logger;

    public DashboardController(
        IVectorStore vectorStore,
        ConfigManager configManager,
        QueryStatsService statsService,
        ILogger<DashboardController> logger)
    {
        _vectorStore = vectorStore;
        _configManager = configManager;
        _statsService = statsService;
        _logger = logger;
    }

    /// <summary>
    /// 获取仪表盘统计信息
    /// </summary>
    [HttpGet("stats")]
    public async Task<ActionResult<DashboardStats>> GetStats()
    {
        var config = _configManager.Load();

        // 回填旧数据中 source 为空的孤儿记录
        try
        {
            var sourceNameToPath = config.Sources.ToDictionary(s => s.Name, s => s.Path);
            var backfilled = await _vectorStore.BackfillSourceAsync(sourceNameToPath);
            if (backfilled > 0)
            {
                _logger.LogInformation("Backfilled source for {Count} orphaned file records", backfilled);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to backfill source for orphaned records");
        }

        var files = await _vectorStore.GetAllFilesAsync();
        var fileList = files.ToList();

        // 仅统计属于已配置源的文件，排除不属于任何源的孤儿记录
        var sourceNames = config.Sources.Select(s => s.Name).ToHashSet();
        var validFiles = fileList.Where(f => sourceNames.Contains(f.Source)).ToList();

        // 直接从 chunks 表统计，避免依赖 FileRecord.ChunkCount（旧数据可能为0）
        var totalChunks = 0;
        foreach (var file in validFiles)
        {
            if (file.ChunkCount > 0)
            {
                totalChunks += file.ChunkCount;
            }
            else
            {
                // 旧记录 ChunkCount 为 0，回退到查询 chunks 表
                var chunks = await _vectorStore.GetChunksByFileAsync(file.Id);
                totalChunks += chunks.Count();
            }
        }

        var avgQueryTime = await _statsService.GetAverageQueryTimeAsync();

        return Ok(new DashboardStats
        {
            TotalFiles = validFiles.Count,
            TotalChunks = totalChunks,
            SourceCount = config.Sources.Count,
            AvgQueryTime = avgQueryTime
        });
    }

    /// <summary>
    /// 获取源文件夹列表
    /// </summary>
    [HttpGet("sources")]
    public async Task<ActionResult<List<SourceInfo>>> GetSources()
    {
        var config = _configManager.Load();
        var files = await _vectorStore.GetAllFilesAsync();
        var fileList = files.ToList();

        // 获取所有 chunks 并按 fileId 分组计算实际数量
        var chunkCounts = new Dictionary<string, int>();
        foreach (var file in fileList)
        {
            var chunks = await _vectorStore.GetChunksByFileAsync(file.Id);
            chunkCounts[file.Id] = chunks.Count();
        }

        var sources = config.Sources.Select(s =>
        {
            var sourceFiles = fileList.Where(f => f.Source == s.Name).ToList();
            return new SourceInfo
            {
                Name = s.Name,
                Path = s.Path,
                Type = s.Type.ToString(),
                Enabled = s.Enabled,
                FileCount = sourceFiles.Count,
                ChunkCount = sourceFiles.Sum(f => chunkCounts.GetValueOrDefault(f.Id, 0))
            };
        }).ToList();

        return Ok(sources);
    }

    /// <summary>
    /// 调试：检查向量存储状态
    /// </summary>
    [HttpGet("debug/vectors")]
    public async Task<ActionResult> DebugVectors()
    {
        var files = await _vectorStore.GetAllFilesAsync();
        var fileList = files.ToList();

        var firstFile = fileList.FirstOrDefault();
        if (firstFile == null)
        {
            return Ok(new { error = "No files found" });
        }

        var chunks = await _vectorStore.GetChunksByFileAsync(firstFile.Id);
        var chunkList = chunks.ToList();

        var firstChunk = chunkList.FirstOrDefault();
        if (firstChunk == null)
        {
            return Ok(new { error = "No chunks found", fileCount = fileList.Count });
        }

        var vector = await _vectorStore.GetVectorByChunkIdAsync(firstChunk.Id);

        return Ok(new
        {
            fileCount = fileList.Count,
            chunkCount = chunkList.Count,
            firstChunk = new
            {
                id = firstChunk.Id,
                fileId = firstChunk.FileId,
                content = firstChunk.Content?.Substring(0, Math.Min(50, firstChunk.Content?.Length ?? 0))
            },
            vectorInfo = vector == null ? null : new
            {
                id = vector.Id,
                chunkId = vector.ChunkId,
                dimension = vector.Dimension,
                vectorLength = vector.Vector?.Length ?? 0,
                firstFewValues = vector.Vector?.Take(5).ToArray()
            }
        });
    }
}

/// <summary>
/// 仪表盘统计
/// </summary>
public class DashboardStats
{
    public int TotalFiles { get; set; }
    public int TotalChunks { get; set; }
    public int SourceCount { get; set; }
    public double AvgQueryTime { get; set; }
}

/// <summary>
/// 源文件夹信息
/// </summary>
public class SourceInfo
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Type { get; set; } = "";
    public bool Enabled { get; set; }
    public int FileCount { get; set; }
    public int ChunkCount { get; set; }
}
