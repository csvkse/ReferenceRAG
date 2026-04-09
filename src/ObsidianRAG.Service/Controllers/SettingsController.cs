using Microsoft.AspNetCore.Mvc;
using ObsidianRAG.Core.Interfaces;
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
    private readonly IModelManager _modelManager;
    private readonly ILogger<SettingsController> _logger;

    public SettingsController(
        ConfigManager configManager,
        IModelManager modelManager,
        ILogger<SettingsController> logger)
    {
        _configManager = configManager;
        _modelManager = modelManager;
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

    /// <summary>
    /// 更新模型保存路径（可选迁移已有模型）
    /// </summary>
    [HttpPatch("models-path")]
    public async Task<ActionResult> UpdateModelsPath([FromBody] UpdateModelsPathRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ModelsPath))
        {
            return BadRequest(new { error = "模型路径不能为空" });
        }

        _logger.LogInformation("更新模型路径: {Path}, 迁移: {Migrate}", request.ModelsPath, request.MigrateExisting);

        var (success, error, migrated) = await _modelManager.SetModelsPathAsync(
            request.ModelsPath, request.MigrateExisting);

        if (!success)
        {
            return BadRequest(new { error });
        }

        return Ok(new
        {
            message = $"模型路径已更新: {request.ModelsPath}",
            migratedCount = migrated.Count,
            migrated
        });
    }
}

/// <summary>
/// 更新模型路径请求
/// </summary>
public class UpdateModelsPathRequest
{
    /// <summary>
    /// 新的模型保存路径
    /// </summary>
    public string ModelsPath { get; set; } = "";

    /// <summary>
    /// 是否迁移已有模型到新路径
    /// </summary>
    public bool MigrateExisting { get; set; } = false;
}
