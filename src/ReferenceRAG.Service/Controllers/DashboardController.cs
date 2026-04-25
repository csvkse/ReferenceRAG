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

        var files = await _vectorStore.GetAllFilesAsync();
        var fileList = files.ToList();

        // 仅统计属于已配置源的文件
        var sourceNames = config.Sources.Select(s => s.Name).ToHashSet();
        var validFiles = fileList.Where(f => sourceNames.Contains(f.Source)).ToList();

        var totalChunks = validFiles.Sum(f => f.ChunkCount);

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
                ChunkCount = sourceFiles.Sum(f => f.ChunkCount)
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

        var chunkList = (await _vectorStore.GetChunksByFileAsync(firstFile.Id)).ToList();
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
