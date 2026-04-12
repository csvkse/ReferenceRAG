using Microsoft.AspNetCore.Mvc;
using ObsidianRAG.Core.Interfaces;
using ObsidianRAG.Core.Models;
using ObsidianRAG.Core.Services;

namespace ObsidianRAG.Service.Controllers;

/// <summary>
/// 模型管理 API
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class ModelsController : ControllerBase
{
    private readonly ConfigManager _configManager;
    private readonly IModelManager _modelManager;
    private readonly IEmbeddingService _embeddingService;
    private readonly IRerankService? _rerankService;
    private readonly IVectorStore _vectorStore;
    private readonly ILogger<ModelsController> _logger;
    private static readonly Dictionary<string, DownloadProgress> _downloadProgress = new();
    private static readonly SemaphoreSlim _modelSwitchLock = new(1, 1);

    public ModelsController(
        ConfigManager configManager,
        IModelManager modelManager,
        IEmbeddingService embeddingService,
        IVectorStore vectorStore,
        ILogger<ModelsController> logger,
        IRerankService? rerankService = null)
    {
        _configManager = configManager;
        _modelManager = modelManager;
        _embeddingService = embeddingService;
        _vectorStore = vectorStore;
        _logger = logger;
        _rerankService = rerankService;
    }

    /// <summary>
    /// 获取当前模型信息
    /// </summary>
    [HttpGet("current")]
    public async Task<ActionResult<ModelInfo?>> GetCurrentModel()
    {

        var model = _modelManager.GetCurrentModel();
        return Ok(model);
    }

    /// <summary>
    /// 获取所有可用模型
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<ModelInfo>>> GetAvailableModels()
    {
        var models = await _modelManager.GetAvailableModelsAsync("embedding");
        return Ok(models);
    }

    /// <summary>
    /// 获取已下载的模型
    /// </summary>
    [HttpGet("downloaded")]
    public ActionResult<List<ModelInfo>> GetDownloadedModels()
    {
        var models = _modelManager.GetDownloadedModels();
        return Ok(models);
    }

    /// <summary>
    /// 切换模型
    /// </summary>
    [HttpPost("switch")]
    public async Task<ActionResult> SwitchModel([FromBody] SwitchModelRequest request)
    {
        if (string.IsNullOrEmpty(request.ModelName))
        {
            return BadRequest(new { error = "模型名称不能为空" });
        }

        // 并发锁，防止同时切换模型
        if (!await _modelSwitchLock.WaitAsync(TimeSpan.FromSeconds(30)))
        {
            return StatusCode(429, new { error = "模型切换正在进行中，请稍后重试" });
        }

        try
        {
            var oldDimension = _embeddingService.Dimension;
            var oldModelName = _embeddingService.ModelName;

            var models = await _modelManager.GetAvailableModelsAsync();
            var model = models.FirstOrDefault(m => m.Name == request.ModelName);

            if (model == null)
            {
                return NotFound(new { error = $"模型不存在: {request.ModelName}" });
            }

            if (!model.IsDownloaded)
            {
                return BadRequest(new { error = $"模型尚未下载: {request.ModelName}", needDownload = true });
            }

            _logger.LogInformation("开始切换模型: {OldModel} -> {NewModel} (DeleteOldVectors: {Delete})",
                oldModelName, request.ModelName, request.DeleteOldVectors);

            // 维度变化时处理旧向量
            if (oldDimension != model.Dimension && request.DeleteOldVectors)
            {
                _logger.LogInformation("检测到维度变化 ({Old} -> {New})，删除旧模型向量",
                    oldDimension, model.Dimension);
                var deletedCount = await _vectorStore.DeleteVectorsByModelAsync(oldModelName);
                _logger.LogInformation("已删除 {Count} 条旧模型向量", deletedCount);
            }

            // 更新配置文件
            var success = await _modelManager.SwitchModelAsync(request.ModelName);

            if (!success)
            {
                _logger.LogError("模型切换失败: {ModelName}", request.ModelName);
                return StatusCode(500, new { error = "模型切换失败" });
            }

            // 重新加载 Embedding 服务的模型
            var onnxPath = Path.Combine(model.LocalPath ?? "", "model.onnx");
            var reloadSuccess = await _embeddingService.ReloadModelAsync(onnxPath, request.ModelName);

            if (!reloadSuccess)
            {
                _logger.LogError("Embedding 服务重新加载模型失败: {ModelName}", request.ModelName);
                return StatusCode(500, new { error = "模型重新加载失败" });
            }

            _logger.LogInformation("模型切换成功: {ModelName} (Dimension: {OldDimension} -> {NewDimension})",
                request.ModelName, oldDimension, _embeddingService.Dimension);
            return Ok(new
            {
                message = $"已切换到模型: {model.DisplayName}",
                model,
                dimension = _embeddingService.Dimension,
                dimensionChanged = oldDimension != model.Dimension,
                oldDimension
            });
        }
        finally
        {
            _modelSwitchLock.Release();
        }
    }

    /// <summary>
    /// 获取模型的下载选项
    /// </summary>
    [HttpGet("download-options/{modelName}")]
    public async Task<ActionResult<ModelDownloadOptions>> GetDownloadOptions(string modelName)
    {
        var options = await _modelManager.GetDownloadOptionsAsync(modelName);
        return Ok(options);
    }

    /// <summary>
    /// 开始下载模型
    /// </summary>
    [HttpPost("download/{modelName}")]
    public async Task<ActionResult> DownloadModel(string modelName, [FromBody] DownloadModelRequest? request = null)
    {
        var models = await _modelManager.GetAvailableModelsAsync();
        var model = models.FirstOrDefault(m => m.Name == modelName);
        
        if (model == null)
        {
            return NotFound(new { error = $"模型不存在: {modelName}" });
        }

        if (model.IsDownloaded)
        {
            return Ok(new { message = "模型已下载", model });
        }

        // 检查是否已在下载中
        if (_downloadProgress.TryGetValue(modelName, out var existing) && 
            existing.Status == "downloading")
        {
            return Ok(new { message = "模型正在下载中", progress = existing });
        }

        // 初始化进度
        var progress = new DownloadProgress
        {
            ModelName = modelName,
            Status = "downloading",
            Progress = 0,
            StartTime = DateTime.UtcNow,
            TotalBytes = model.ModelSizeBytes
        };
        _downloadProgress[modelName] = progress;

        // 异步启动下载
        _ = Task.Run(async () =>
        {
            try
            {
                _logger.LogInformation("[ModelManager] 开始下载模型: {ModelName}", modelName);
                
                var (success, error) = await _modelManager.DownloadModelAsync(modelName, new Progress<float>(p =>
                {
                    if (_downloadProgress.TryGetValue(modelName, out var pg))
                    {
                        pg.Progress = p;
                        pg.BytesReceived = (long)(p * model.ModelSizeBytes / 100);

                        var elapsed = (DateTime.UtcNow - pg.StartTime).TotalSeconds;
                        if (elapsed > 0 && pg.BytesReceived > 0)
                        {
                            pg.SpeedBytesPerSecond = pg.BytesReceived / elapsed;
                            if (pg.SpeedBytesPerSecond > 0)
                            {
                                var remaining = model.ModelSizeBytes - pg.BytesReceived;
                                pg.EstimatedSecondsRemaining = (int)(remaining / pg.SpeedBytesPerSecond);
                            }
                        }
                    }
                }), null, request?.OnnxFilePath);

                if (_downloadProgress.TryGetValue(modelName, out var pg2))
                {
                    if (success)
                    {
                        pg2.Status = "completed";
                        pg2.Progress = 100;
                        pg2.EndTime = DateTime.UtcNow;
                        _logger.LogInformation("[ModelManager] 模型下载完成: {ModelName}", modelName);
                    }
                    else
                    {
                        pg2.Status = "failed";
                        pg2.ErrorMessage = error ?? "未知错误";
                        pg2.ErrorCode = ParseErrorCode(error ?? "");
                        pg2.EndTime = DateTime.UtcNow;
                        _logger.LogError("[ModelManager] 模型下载失败: {ModelName} - {Error}", modelName, error);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ModelManager] 模型下载异常: {ModelName}", modelName);
                if (_downloadProgress.TryGetValue(modelName, out var pg3))
                {
                    pg3.Status = "failed";
                    pg3.ErrorMessage = ex.Message;
                    pg3.ErrorCode = DownloadErrorCode.Unknown;
                    pg3.EndTime = DateTime.UtcNow;
                }
            }
        });

        return Accepted(new { message = "开始下载模型", progress });
    }

    /// <summary>
    /// 获取下载进度
    /// </summary>
    [HttpGet("download/{modelName}/progress")]
    public ActionResult<DownloadProgress> GetDownloadProgress(string modelName)
    {
        if (!_downloadProgress.TryGetValue(modelName, out var progress))
        {
            return Ok(new DownloadProgress
            {
                ModelName = modelName,
                Status = "idle"
            });
        }

        return Ok(progress);
    }

    /// <summary>
    /// 取消下载
    /// </summary>
    [HttpDelete("download/{modelName}")]
    public ActionResult CancelDownload(string modelName)
    {
        if (_downloadProgress.TryGetValue(modelName, out var progress))
        {
            if (progress.Status == "downloading")
            {
                progress.Status = "cancelled";
                progress.EndTime = DateTime.UtcNow;
                return Ok(new { message = "已取消下载" });
            }
        }
        
        return NotFound(new { error = "没有正在进行的下载任务" });
    }

    /// <summary>
    /// 删除模型
    /// </summary>
    [HttpDelete("{modelName}")]
    public async Task<ActionResult> DeleteModel(string modelName)
    {
        var config = _configManager.Load();
        if (config.Embedding.ModelName == modelName)
        {
            return BadRequest(new { error = "无法删除当前正在使用的模型" });
        }

        var success = await _modelManager.DeleteModelAsync(modelName);
        
        if (success)
        {
            _downloadProgress.Remove(modelName);
            return Ok(new { message = "模型已删除" });
        }
        
        return NotFound(new { error = "模型不存在或删除失败" });
    }

    /// <summary>
    /// 获取推荐模型
    /// </summary>
    [HttpGet("recommended")]
    public async Task<ActionResult<List<ModelInfo>>> GetRecommendedModels(
        [FromQuery] string? language = null,
        [FromQuery] bool preferGpu = true)
    {
        var models = await _modelManager.GetRecommendedModelsAsync(language, preferGpu);
        return Ok(models);
    }

    /// <summary>
    /// 转换模型格式
    /// </summary>
    [HttpPost("{modelName}/convert")]
    public async Task<ActionResult> ConvertModelFormat(
        string modelName,
        [FromBody] ConvertFormatRequest request)
    {
        if (string.IsNullOrEmpty(request.TargetFormat) ||
            (request.TargetFormat != "embedded" && request.TargetFormat != "external"))
        {
            return BadRequest(new { error = "目标格式必须是 'embedded' 或 'external'" });
        }

        var models = await _modelManager.GetAvailableModelsAsync();
        var model = models.FirstOrDefault(m => m.Name == modelName);
        
        if (model == null)
        {
            return NotFound(new { error = $"模型不存在: {modelName}" });
        }

        if (!model.IsDownloaded)
        {
            return BadRequest(new { error = "模型未下载，无法转换" });
        }

        // 检查是否已在转换中
        var progressKey = $"convert_{modelName}";
        if (_downloadProgress.TryGetValue(progressKey, out var existing) && 
            existing.Status == "downloading")
        {
            return Ok(new { message = "模型正在转换中", progress = existing });
        }

        // 初始化进度
        var progress = new DownloadProgress
        {
            ModelName = modelName,
            Status = "downloading",
            Progress = 0,
            StartTime = DateTime.UtcNow
        };
        _downloadProgress[progressKey] = progress;

        // 异步执行转换
        _ = Task.Run(async () =>
        {
            try
            {
                _logger.LogInformation("[ModelManager] 开始转换模型格式: {ModelName} -> {Format}", 
                    modelName, request.TargetFormat);
                
                var (success, error) = await _modelManager.ConvertFormatAsync(
                    modelName, 
                    request.TargetFormat,
                    new Progress<float>(p =>
                    {
                        if (_downloadProgress.TryGetValue(progressKey, out var pg))
                        {
                            pg.Progress = p;
                        }
                    }));

                if (_downloadProgress.TryGetValue(progressKey, out var pg2))
                {
                    if (success)
                    {
                        pg2.Status = "completed";
                        pg2.Progress = 100;
                        pg2.EndTime = DateTime.UtcNow;
                        _logger.LogInformation("[ModelManager] 模型格式转换完成: {ModelName}", modelName);
                    }
                    else
                    {
                        pg2.Status = "failed";
                        pg2.ErrorMessage = error ?? "未知错误";
                        pg2.EndTime = DateTime.UtcNow;
                        _logger.LogError("[ModelManager] 模型格式转换失败: {ModelName} - {Error}", modelName, error);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ModelManager] 模型格式转换异常: {ModelName}", modelName);
                if (_downloadProgress.TryGetValue(progressKey, out var pg3))
                {
                    pg3.Status = "failed";
                    pg3.ErrorMessage = ex.Message;
                    pg3.EndTime = DateTime.UtcNow;
                }
            }
        });

        return Accepted(new { message = "开始转换模型格式", progress });
    }

    /// <summary>
    /// 获取转换进度
    /// </summary>
    [HttpGet("{modelName}/convert/progress")]
    public ActionResult<DownloadProgress> GetConvertProgress(string modelName)
    {
        var progressKey = $"convert_{modelName}";
        if (!_downloadProgress.TryGetValue(progressKey, out var progress))
        {
            return Ok(new DownloadProgress
            {
                ModelName = modelName,
                Status = "idle"
            });
        }

        return Ok(progress);
    }

    /// <summary>
    /// 添加自定义 HuggingFace 模型
    /// </summary>
    [HttpPost("custom")]
    public async Task<ActionResult> AddCustomModel([FromBody] AddCustomModelRequest request)
    {
        if (string.IsNullOrEmpty(request.HuggingFaceId))
        {
            return BadRequest(new { error = "HuggingFace 模型 ID 不能为空" });
        }

        var (success, error, model) = await _modelManager.AddCustomModelAsync(
            request.HuggingFaceId, 
            request.DisplayName);

        if (!success)
        {
            return BadRequest(new { error });
        }

        return Ok(new { message = $"已添加自定义模型: {model?.Name}", model });
    }

    private static string GetDisplayName(string modelName)
    {
        return modelName switch
        {
            "bge-small-zh-v1.5" => "BGE Small Chinese v1.5",
            "bge-base-zh-v1.5" => "BGE Base Chinese v1.5",
            "bge-large-zh-v1.5" => "BGE Large Chinese v1.5",
            "bge-m3" => "BGE M3",
            "bge-base-en-v1.5" => "BGE Base English v1.5",
            "bge-small-en-v1.5" => "BGE Small English v1.5",
            _ => modelName
        };
    }

    /// <summary>
    /// 从错误消息中解析错误码
    /// </summary>
    private static DownloadErrorCode ParseErrorCode(string errorMessage)
    {
        if (string.IsNullOrEmpty(errorMessage))
            return DownloadErrorCode.Unknown;

        var lowerMessage = errorMessage.ToLowerInvariant();

        if (lowerMessage.Contains("model.onnx") && lowerMessage.Contains("不完整"))
            return DownloadErrorCode.FileIncomplete;

        if (lowerMessage.Contains("转换失败") || lowerMessage.Contains("转换异常"))
        {
            if (lowerMessage.Contains("python") || lowerMessage.Contains("optimum"))
                return DownloadErrorCode.DependenciesMissing;
            if (lowerMessage.Contains("过大") || lowerMessage.Contains("限制"))
                return DownloadErrorCode.ModelTooLargeForEmbedded;
            return DownloadErrorCode.ConversionFailed;
        }

        if (lowerMessage.Contains("python") && lowerMessage.Contains("不可用"))
            return DownloadErrorCode.PythonNotFound;

        if (lowerMessage.Contains("网络") || lowerMessage.Contains("timeout") || lowerMessage.Contains("connection"))
            return DownloadErrorCode.NetworkError;

        if (lowerMessage.Contains("存储") || lowerMessage.Contains("磁盘") || lowerMessage.Contains("空间"))
            return DownloadErrorCode.StorageError;

        return DownloadErrorCode.Unknown;
    }

    // ==================== 重排模型管理 API ====================

    /// <summary>
    /// 获取所有重排模型列表
    /// </summary>
    [HttpGet("rerank")]
    public async Task<ActionResult<List<ModelInfo>>> GetRerankModels()
    {
        var models = await _modelManager.GetAvailableModelsAsync("reranker");
        return Ok(models);
    }

/// <summary>
/// 获取已下载的重排模型列表
/// </summary>
[HttpGet("rerank/downloaded")]
public ActionResult<List<ModelInfo>> GetDownloadedRerankModels()
{
    var models = _modelManager.GetDownloadedRerankModels();
    return Ok(models);
}

/// <summary>
/// 获取当前重排模型
/// </summary>
[HttpGet("rerank/current")]
public ActionResult<ModelInfo?> GetCurrentRerankModel()
{
    var model = _modelManager.GetCurrentRerankModel();
    return Ok(model);
}

/// <summary>
/// 切换重排模型
/// </summary>
[HttpPost("rerank/switch")]
public async Task<ActionResult> SwitchRerankModel([FromBody] SwitchModelRequest request)
{
    if (string.IsNullOrEmpty(request.ModelName))
    {
        return BadRequest(new { error = "模型名称不能为空" });
    }

    var models = _modelManager.GetRerankModels();
    var model = models.FirstOrDefault(m => m.Name == request.ModelName);

    if (model == null)
    {
        return NotFound(new { error = $"重排模型不存在: {request.ModelName}" });
    }

    if (!model.IsDownloaded)
    {
        return BadRequest(new { error = $"重排模型尚未下载: {request.ModelName}", needDownload = true });
    }

    _logger.LogInformation("开始切换重排模型: {ModelName}", request.ModelName);

    var success = await _modelManager.SwitchRerankModelAsync(request.ModelName);

    if (!success)
    {
        _logger.LogError("重排模型切换失败: {ModelName}", request.ModelName);
        return StatusCode(500, new { error = "重排模型切换失败" });
    }

    // 重新加载 Rerank 服务的模型
    if (_rerankService != null)
    {
        var config = _configManager.Load();
        var modelsPath = config.Embedding.ModelsPath;
        var onnxPath = Path.Combine(modelsPath, request.ModelName, "model.onnx");
        if (!System.IO.File.Exists(onnxPath))
        {
            onnxPath = Path.Combine(modelsPath, request.ModelName, "onnx", "model.onnx");
        }
        
        var reloadSuccess = await _rerankService.ReloadModelAsync(onnxPath, request.ModelName);
        if (!reloadSuccess)
        {
            _logger.LogWarning("Rerank 服务重新加载模型失败: {ModelName}", request.ModelName);
        }
    }

    _logger.LogInformation("重排模型切换成功: {ModelName}", request.ModelName);
    return Ok(new
    {
        message = $"已切换到重排模型: {model.DisplayName}",
        model
    });
}

/// <summary>
/// 下载重排模型
/// </summary>
[HttpPost("rerank/download/{modelName}")]
public async Task<ActionResult> DownloadRerankModel(string modelName, [FromBody] DownloadModelRequest? request = null)
{
    var models = _modelManager.GetRerankModels();
    var model = models.FirstOrDefault(m => m.Name == modelName);
    
    if (model == null)
    {
        return NotFound(new { error = $"重排模型不存在: {modelName}" });
    }

    if (model.IsDownloaded)
    {
        return Ok(new { message = "重排模型已下载", model });
    }

    // 检查是否已在下载中
    var progressKey = $"rerank_{modelName}";
    if (_downloadProgress.TryGetValue(progressKey, out var existing) && 
        existing.Status == "downloading")
    {
        return Ok(new { message = "重排模型正在下载中", progress = existing });
    }

    // 初始化进度
    var progress = new DownloadProgress
    {
        ModelName = modelName,
        Status = "downloading",
        Progress = 0,
        StartTime = DateTime.UtcNow,
        TotalBytes = model.ModelSizeBytes
    };
    _downloadProgress[progressKey] = progress;

    // 异步启动下载
    _ = Task.Run(async () =>
    {
        try
        {
            _logger.LogInformation("[RerankModel] 开始下载重排模型: {ModelName}", modelName);
            
            var (success, error) = await _modelManager.DownloadModelAsync(modelName, new Progress<float>(p =>
            {
                if (_downloadProgress.TryGetValue(progressKey, out var pg))
                {
                    pg.Progress = p;
                    pg.BytesReceived = (long)(p * model.ModelSizeBytes / 100);
                }
            }), null, request?.OnnxFilePath);

            if (_downloadProgress.TryGetValue(progressKey, out var pg2))
            {
                if (success)
                {
                    pg2.Status = "completed";
                    pg2.Progress = 100;
                    pg2.EndTime = DateTime.UtcNow;
                    _logger.LogInformation("[RerankModel] 重排模型下载完成: {ModelName}", modelName);
                }
                else
                {
                    pg2.Status = "failed";
                    pg2.ErrorMessage = error ?? "未知错误";
                    pg2.EndTime = DateTime.UtcNow;
                    _logger.LogError("[RerankModel] 重排模型下载失败: {ModelName} - {Error}", modelName, error);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[RerankModel] 重排模型下载异常: {ModelName}", modelName);
            if (_downloadProgress.TryGetValue(progressKey, out var pg3))
            {
                pg3.Status = "failed";
                pg3.ErrorMessage = ex.Message;
                pg3.EndTime = DateTime.UtcNow;
            }
        }
    });

    return Accepted(new { message = "开始下载重排模型", progress });
}

/// <summary>
/// 获取重排模型下载进度
/// </summary>
[HttpGet("rerank/download/{modelName}/progress")]
public ActionResult<DownloadProgress> GetRerankDownloadProgress(string modelName)
{
    var progressKey = $"rerank_{modelName}";
    if (!_downloadProgress.TryGetValue(progressKey, out var progress))
    {
        return Ok(new DownloadProgress
        {
            ModelName = modelName,
            Status = "idle"
        });
    }

    return Ok(progress);
}

/// <summary>
/// 删除重排模型
/// </summary>
[HttpDelete("rerank/{modelName}")]
public async Task<ActionResult> DeleteRerankModel(string modelName)
{
    var config = _configManager.Load();
    if (config.Rerank?.CurrentModel == modelName)
    {
        return BadRequest(new { error = "无法删除当前正在使用的重排模型" });
    }

    var success = await _modelManager.DeleteModelAsync(modelName);
    
    if (success)
    {
        var progressKey = $"rerank_{modelName}";
        _downloadProgress.Remove(progressKey);
        return Ok(new { message = "重排模型已删除" });
    }
    
    return NotFound(new { error = "重排模型不存在或删除失败" });
}

/// <summary>
/// 获取重排模型下载选项
/// </summary>
[HttpGet("rerank/download-options/{modelName}")]
public async Task<ActionResult<ModelDownloadOptions>> GetRerankDownloadOptions(string modelName)
{
    var options = await _modelManager.GetDownloadOptionsAsync(modelName);
    return Ok(options);
}
}

/// <summary>
/// 切换模型请求
/// </summary>
public class SwitchModelRequest
{
    public string ModelName { get; set; } = string.Empty;

    /// <summary>
    /// 是否删除旧模型向量（维度变化时有用）
    /// </summary>
    public bool DeleteOldVectors { get; set; } = false;
}

/// <summary>
/// 下载模型请求
/// </summary>
public class DownloadModelRequest
{
    /// <summary>
    /// 指定要下载的 ONNX 文件路径（如: model.onnx, onnx/model_qint8_avx512.onnx）
    /// 如果不指定，将自动选择最佳选项
    /// </summary>
    public string? OnnxFilePath { get; set; }
}

/// <summary>
/// 转换格式请求
/// </summary>
public class ConvertFormatRequest
{
    /// <summary>
    /// 目标格式: "embedded" 或 "external"
    /// </summary>
    public string TargetFormat { get; set; } = "embedded";
}

/// <summary>
/// 添加自定义模型请求
/// </summary>
public class AddCustomModelRequest
{
    /// <summary>
    /// HuggingFace 模型 ID (例如: BAAI/bge-base-zh-v1.5)
    /// </summary>
    public string HuggingFaceId { get; set; } = string.Empty;
    
    /// <summary>
    /// 显示名称（可选）
    /// </summary>
    public string? DisplayName { get; set; }
}
