using Microsoft.AspNetCore.Mvc;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;

namespace ReferenceRAG.Service.Controllers;

/// <summary>
/// 设置 API
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class SettingsController : ControllerBase
{
    private readonly ConfigManager _configManager;
    private readonly IModelManager _modelManager;
    private readonly IEmbeddingService _embeddingService;
    private readonly IRerankService _rerankService;
    private readonly ILogger<SettingsController> _logger;

    public SettingsController(
        ConfigManager configManager,
        IModelManager modelManager,
        IEmbeddingService embeddingService,
        IRerankService rerankService,
        ILogger<SettingsController> logger)
    {
        _configManager = configManager;
        _modelManager = modelManager;
        _embeddingService = embeddingService;
        _rerankService = rerankService;
        _logger = logger;
    }

    /// <summary>
    /// 获取配置
    /// </summary>
    [HttpGet]
    public ActionResult<ObsidianRagConfig> Get()
    {
        var config = _configManager.Load();

        // 获取模型根目录（优先使用顶层配置）
        var modelsRoot = !string.IsNullOrEmpty(config.ModelsRootPath)
            ? config.ModelsRootPath
            : Path.Combine(config.DataPath ?? "data", "models");

        // 填充重排模型的完整路径
        if (!string.IsNullOrEmpty(config.Rerank?.CurrentModel))
        {
            var rerankModelPath = Path.Combine(
                modelsRoot,
                config.Rerank.CurrentModel,
                "model.onnx");

            // 如果 ModelPath 为空或不存在，使用计算出的路径
            if (string.IsNullOrEmpty(config.Rerank.ModelPath) || !System.IO.File.Exists(config.Rerank.ModelPath))
            {
                config.Rerank.ModelPath = rerankModelPath;
            }
        }

        // 确保 ModelsRootPath 有值
        if (string.IsNullOrEmpty(config.ModelsRootPath))
        {
            config.ModelsRootPath = modelsRoot;
        }

        // 同步 AllowNetworkAccess 与旧 Host 字段（向后兼容旧配置）
        if (config.Service.Host == "0.0.0.0" && !config.Service.AllowNetworkAccess)
        {
            config.Service.AllowNetworkAccess = true;
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
        // 保持 Host 字段与 AllowNetworkAccess 同步（双向兼容）
        config.Service.Host = config.Service.AllowNetworkAccess ? "0.0.0.0" : "localhost";

        _configManager.Save(config);

        // 修改监听地址需要重启才能生效
        var requiresRestart = true;
        return Ok(new { message = "配置已保存", requiresRestart });
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

        var result = await _modelManager.SetModelsPathAsync(
            request.ModelsPath, request.MigrateExisting);

        if (!result.Success)
        {
            return BadRequest(new { error = result.Error });
        }

        //// 卸载当前加载的模型，下次使用时将自动加载新路径的模型
        //_embeddingService.UnloadModel();
        //_rerankService.UnloadModel();
        _logger.LogInformation("已卸载嵌入模型和重排模型，新模型将在下次使用时自动加载");

        return Ok(new
        {
            message = $"模型路径已更新: {request.ModelsPath}",
            migratedCount = result.Migrated.Count,
            migrated = result.Migrated,
            embeddingModels = result.EmbeddingModels,
            rerankModels = result.RerankModels
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
