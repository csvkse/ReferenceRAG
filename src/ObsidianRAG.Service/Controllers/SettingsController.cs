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
        
        // 填充重排模型的完整路径
        if (!string.IsNullOrEmpty(config.Rerank?.CurrentModel))
        {
            var rerankModelPath = Path.Combine(
                config.DataPath ?? "data",
                "models",
                config.Rerank.CurrentModel,
                "model.onnx");
            
            // 如果 ModelPath 为空或不存在，使用计算出的路径
            if (string.IsNullOrEmpty(config.Rerank.ModelPath) || !System.IO.File.Exists(config.Rerank.ModelPath))
            {
                config.Rerank.ModelPath = rerankModelPath;
            }
        }
        
        // 填充 Embedding 模型的完整路径
        if (!string.IsNullOrEmpty(config.Embedding?.ModelPath))
        {
            var embeddingModelDir = Path.GetDirectoryName(config.Embedding.ModelPath);
            config.Embedding.ModelsPath = embeddingModelDir;
        }
        
        return Ok(config);
    }

    /// <summary>
    /// 获取 CUDA/GPU 可用性状态
    /// </summary>
    [HttpGet("cuda-availability")]
    public ActionResult<CudaAvailability> GetCudaAvailability()
    {
        var cudaAvailable = CheckCudaAvailability();
        return Ok(new CudaAvailability
        {
            IsAvailable = cudaAvailable,
            Message = cudaAvailable ? "CUDA 可用" : "未检测到 CUDA/GPU，需要安装 NVIDIA 驱动和 CUDA 运行时"
        });
    }

    private static bool CheckCudaAvailability()
    {
        try
        {
            // 尝试创建 CUDA 执行选项来检测 CUDA 是否可用
            var sessionOptions = new Microsoft.ML.OnnxRuntime.SessionOptions();
            sessionOptions.AppendExecutionProvider_CUDA(0);
            sessionOptions.Dispose();
            return true;
        }
        catch
        {
            return false;
        }
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

/// <summary>
/// CUDA/GPU 可用性状态
/// </summary>
public class CudaAvailability
{
    /// <summary>
    /// CUDA 是否可用
    /// </summary>
    public bool IsAvailable { get; set; }

    /// <summary>
    /// 状态消息
    /// </summary>
    public string Message { get; set; } = "";
}
