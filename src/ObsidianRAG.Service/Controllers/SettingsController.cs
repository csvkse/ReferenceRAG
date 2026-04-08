using Microsoft.AspNetCore.Mvc;
using ObsidianRAG.Core.Models;
using ObsidianRAG.Core.Services;

namespace ObsidianRAG.Service.Controllers;

/// <summary>
/// 设置 API
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class SettingsController : ControllerBase
{
    private readonly ConfigManager _configManager;
    private readonly ILogger<SettingsController> _logger;

    public SettingsController(
        ConfigManager configManager,
        ILogger<SettingsController> logger)
    {
        _configManager = configManager;
        _logger = logger;
    }

    /// <summary>
    /// 获取配置
    /// </summary>
    [HttpGet]
    public ActionResult<ObsidianRagConfig> Get()
    {
        var config = _configManager.Load();
        return Ok(config);
    }

    /// <summary>
    /// 保存配置
    /// </summary>
    [HttpPost]
    public ActionResult Save([FromBody] ObsidianRagConfig config)
    {
        _configManager.Save(config);
        return Ok(new { message = "配置已保存" });
    }
}
