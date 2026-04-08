using Microsoft.AspNetCore.Mvc;
using ObsidianRAG.Core.Interfaces;
using ObsidianRAG.Core.Models;
using ObsidianRAG.Core.Services;

namespace ObsidianRAG.Service.Controllers;

/// <summary>
/// Dashboard API 控制器
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class DashboardController : ControllerBase
{
    private readonly IVectorStore _vectorStore;
    private readonly ConfigManager _configManager;
    private readonly ILogger<DashboardController> _logger;

    public DashboardController(
        IVectorStore vectorStore,
        ConfigManager configManager,
        ILogger<DashboardController> logger)
    {
        _vectorStore = vectorStore;
        _configManager = configManager;
        _logger = logger;
    }

    /// <summary>
    /// 获取仪表盘统计信息
    /// </summary>
    [HttpGet("stats")]
    public async Task<ActionResult<DashboardStats>> GetStats()
    {
        var files = await _vectorStore.GetAllFilesAsync();
        var fileList = files.ToList();

        var config = _configManager.Load();

        return Ok(new DashboardStats
        {
            TotalFiles = fileList.Count,
            TotalChunks = fileList.Sum(f => f.ChunkCount),
            SourceCount = config.Sources.Count,
            AvgQueryTime = 25 // TODO: 从实际查询统计获取
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
