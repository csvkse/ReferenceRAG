using Microsoft.AspNetCore.Mvc;
using ReferenceRAG.Core.Interfaces;

namespace ReferenceRAG.Service.Controllers;

/// <summary>
/// GPU 显存管理控制器
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class GpuController : ControllerBase
{
    private readonly IGpuMemoryManager _memoryManager;
    private readonly ILogger<GpuController> _logger;

    public GpuController(IGpuMemoryManager memoryManager, ILogger<GpuController> logger)
    {
        _memoryManager = memoryManager;
        _logger = logger;
    }

    /// <summary>
    /// 获取显存状态
    /// </summary>
    [HttpGet("memory")]
    public ActionResult<GpuMemoryInfo> GetMemory(int deviceId = 0)
    {
        var info = _memoryManager.GetMemoryInfo(deviceId);
        return Ok(info);
    }

    /// <summary>
    /// 获取所有 Session 状态
    /// </summary>
    [HttpGet("sessions")]
    public ActionResult<IReadOnlyDictionary<string, SessionState>> GetSessions()
    {
        var states = _memoryManager.GetSessionStates();
        return Ok(states);
    }

    /// <summary>
    /// 手动触发显存释放
    /// </summary>
    [HttpPost("shrink")]
    public async Task<ActionResult> Shrink(string? name = null)
    {
        await _memoryManager.ShrinkAsync(name);
        var message = name != null ? $"已请求释放 {name}" : "已请求释放所有 Session";
        _logger.LogInformation("[GpuController] {Message}", message);
        return Ok(new { message, timestamp = DateTime.UtcNow });
    }
}
