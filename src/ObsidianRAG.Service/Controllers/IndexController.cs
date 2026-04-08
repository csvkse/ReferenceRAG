using Microsoft.AspNetCore.Mvc;
using ObsidianRAG.Service.Services;

namespace ObsidianRAG.Service.Controllers;

/// <summary>
/// 索引管理 API
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class IndexController : ControllerBase
{
    private readonly IndexService _indexService;
    private readonly ILogger<IndexController> _logger;

    public IndexController(IndexService indexService, ILogger<IndexController> logger)
    {
        _indexService = indexService;
        _logger = logger;
    }

    /// <summary>
    /// 启动索引任务
    /// </summary>
    [HttpPost("start")]
    public async Task<ActionResult<IndexJob>> StartIndex([FromBody] IndexRequest request)
    {
        var job = await _indexService.StartIndexAsync(request);
        return AcceptedAtAction(nameof(GetStatus), new { indexId = job.Id }, job);
    }

    /// <summary>
    /// 获取索引状态
    /// </summary>
    [HttpGet("{indexId}/status")]
    public ActionResult<IndexJob> GetStatus(string indexId)
    {
        var job = _indexService.GetStatus(indexId);
        if (job == null)
        {
            return NotFound(new { error = $"索引任务 '{indexId}' 不存在或已过期" });
        }
        return Ok(job);
    }

    /// <summary>
    /// 获取所有活跃索引任务
    /// </summary>
    [HttpGet("active")]
    public ActionResult<List<IndexJob>> GetActiveJobs()
    {
        return Ok(_indexService.ActiveJobs.Values.ToList());
    }

    /// <summary>
    /// 停止索引任务
    /// </summary>
    [HttpPost("{indexId}/stop")]
    public async Task<ActionResult> StopIndex(string indexId)
    {
        var stopped = await _indexService.StopIndexAsync(indexId);
        if (!stopped)
        {
            return NotFound(new { error = $"索引任务 '{indexId}' 不存在或已完成" });
        }
        return Ok(new { message = "索引任务已停止" });
    }
}
