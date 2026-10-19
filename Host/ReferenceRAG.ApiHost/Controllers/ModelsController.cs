using Microsoft.AspNetCore.Mvc;
using ReferenceRAG.Core.Features.ModelManagement.Telemetry;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Infrastructure.Features.ModelManagement.Download.Models;

namespace ReferenceRAG.Service.Controllers;

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
    private readonly IModelTelemetryService? _telemetryService;
    private readonly IVectorStore _vectorStore;
    private readonly ILogger<ModelsController> _logger;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DownloadProgress> _downloadProgress = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource> _downloadCts = new();
    private static readonly SemaphoreSlim _modelSwitchLock = new(1, 1);

    public ModelsController(
        ConfigManager configManager,
        IModelManager modelManager,
        IEmbeddingService embeddingService,
        IVectorStore vectorStore,
        ILogger<ModelsController> logger,
        IRerankService? rerankService = null,
        IModelTelemetryService? telemetryService = null)
    {
        _configManager = configManager;
        _modelManager = modelManager;
        _embeddingService = embeddingService;
        _vectorStore = vectorStore;
        _logger = logger;
        _rerankService = rerankService;
        _telemetryService = telemetryService;
    }

    /// <summary>
    /// 扫描模型目录
    /// </summary>
    [HttpPost("scan")]
    public async Task<ActionResult> ScanModels()
    {
        try
        {
            _modelManager.RefreshLocalModels();

            // 返回扫描后的模型列表
            var embeddingModels = await _modelManager.GetAvailableModelsAsync("embedding");
            var rerankModels = await _modelManager.GetAvailableModelsAsync("reranker");
            var chatModels = await _modelManager.GetAvailableModelsAsync("chat");

            return Ok(new
            {
                message = "模型扫描完成",
                embeddingModels,
                rerankModels,
                chatModels
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "扫描模型失败");
            return StatusCode(500, new { error = $"扫描模型失败: {ex.Message}" });
        }
    }

    /// <summary>
    /// 获取嵌入式模型与重排模型的实时协同运行状态（状态机状态、GPU硬件加速、租约与多版本变体）
    /// </summary>
    [HttpGet("runtime-status")]
    public ActionResult<ModelsRuntimeStatusResponse> GetRuntimeStatus()
    {
        var cfg = _configManager.Load();
        var currentEmbModel = _modelManager.GetCurrentModel();
        var currentRerankModel = _modelManager.GetCurrentRerankModel();

        SingleModelStatusResponse? embStatus = null;
        SingleModelStatusResponse? rerankStatus = null;

        if (_telemetryService != null)
        {
            var embTelemetry = _telemetryService.GetCurrentEmbeddingTelemetry();
            embStatus = new SingleModelStatusResponse
            {
                ModelName = !string.IsNullOrEmpty(embTelemetry.Configuration.ModelName) ? embTelemetry.Configuration.ModelName : (currentEmbModel?.Name ?? string.Empty),
                DisplayName = currentEmbModel?.DisplayName ?? embTelemetry.Configuration.DisplayName,
                ActiveVariant = currentEmbModel?.ActiveVariantFileName ?? (currentEmbModel?.LocalPath != null ? Path.GetFileName(currentEmbModel.LocalPath) : null),
                ActiveVariantPath = currentEmbModel?.LocalPath,
                State = (int)embTelemetry.StateMachine.State,
                StateName = embTelemetry.StateMachine.StateName,
                StateDescription = embTelemetry.StateMachine.StateDescription,
                ActiveLeases = embTelemetry.StateMachine.ActiveLeases,
                IsReady = embTelemetry.StateMachine.IsReady,
                IsGpuMode = embTelemetry.Hardware.IsGpuMode,
                ExecutionDevice = embTelemetry.Hardware.ExecutionDevice,
                TotalVramGb = embTelemetry.Hardware.TotalVramGb,
                OffloadDescription = embTelemetry.Hardware.OffloadDescription,
                Quantization = embTelemetry.Configuration.Quantization,
                ModelFormat = currentEmbModel?.InstalledVariants.FirstOrDefault(v => v.IsActive)?.Format
                    ?? (currentEmbModel?.LocalPath?.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) == true ? "gguf" : "onnx"),
                Dimension = embTelemetry.Configuration.Dimension,
                MaxSequenceLength = embTelemetry.Configuration.ContextSize,
                RetentionStrategy = cfg.ModelRetentionStrategy.ToString(),
                IdleTimeoutSeconds = cfg.ModelIdleTimeoutSeconds,
                UnloadOnlyWhenMinimizedToTray = cfg.UnloadOnlyWhenMinimizedToTray,
                LastError = embTelemetry.StateMachine.LastError,
                InstalledVariants = currentEmbModel?.InstalledVariants ?? new()
            };

            var rerankTelemetry = _telemetryService.GetCurrentRerankTelemetry();
            rerankStatus = new SingleModelStatusResponse
            {
                ModelName = !string.IsNullOrEmpty(rerankTelemetry.Configuration.ModelName) ? rerankTelemetry.Configuration.ModelName : (currentRerankModel?.Name ?? string.Empty),
                DisplayName = currentRerankModel?.DisplayName ?? rerankTelemetry.Configuration.DisplayName,
                ActiveVariant = currentRerankModel?.ActiveVariantFileName ?? (currentRerankModel?.LocalPath != null ? Path.GetFileName(currentRerankModel.LocalPath) : null),
                ActiveVariantPath = currentRerankModel?.LocalPath,
                State = (int)rerankTelemetry.StateMachine.State,
                StateName = rerankTelemetry.StateMachine.StateName,
                StateDescription = rerankTelemetry.StateMachine.StateDescription,
                ActiveLeases = rerankTelemetry.StateMachine.ActiveLeases,
                IsReady = rerankTelemetry.StateMachine.IsReady,
                IsGpuMode = rerankTelemetry.Hardware.IsGpuMode,
                ExecutionDevice = rerankTelemetry.Hardware.ExecutionDevice,
                TotalVramGb = rerankTelemetry.Hardware.TotalVramGb,
                OffloadDescription = rerankTelemetry.Hardware.OffloadDescription,
                Quantization = rerankTelemetry.Configuration.Quantization,
                ModelFormat = currentRerankModel?.InstalledVariants.FirstOrDefault(v => v.IsActive)?.Format
                    ?? (currentRerankModel?.LocalPath?.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) == true ? "gguf" : "onnx"),
                Dimension = rerankTelemetry.Configuration.Dimension,
                MaxSequenceLength = rerankTelemetry.Configuration.ContextSize,
                RetentionStrategy = cfg.ModelRetentionStrategy.ToString(),
                IdleTimeoutSeconds = cfg.ModelIdleTimeoutSeconds,
                UnloadOnlyWhenMinimizedToTray = cfg.UnloadOnlyWhenMinimizedToTray,
                LastError = rerankTelemetry.StateMachine.LastError,
                InstalledVariants = currentRerankModel?.InstalledVariants ?? new()
            };
        }

        var currentChatModel = _modelManager.GetCurrentChatModel();
        SingleModelStatusResponse? chatStatus = null;
        if (currentChatModel != null)
        {
            var activeVariant = currentChatModel.InstalledVariants.FirstOrDefault(v => v.IsActive) ?? currentChatModel.InstalledVariants.FirstOrDefault();
            chatStatus = new SingleModelStatusResponse
            {
                ModelName = currentChatModel.Name,
                DisplayName = currentChatModel.DisplayName,
                ActiveVariant = currentChatModel.ActiveVariantFileName ?? (currentChatModel.LocalPath != null ? Path.GetFileName(currentChatModel.LocalPath) : null),
                ActiveVariantPath = currentChatModel.LocalPath,
                State = 2,
                StateName = "Ready",
                StateDescription = "就绪",
                IsReady = true,
                IsGpuMode = cfg.Chat?.LocalGguf?.GpuLayers > 0,
                ExecutionDevice = (cfg.Chat?.LocalGguf?.GpuLayers > 0) ? "GPU" : "CPU",
                Quantization = activeVariant?.Quantization,
                ModelFormat = activeVariant?.Format ?? "gguf",
                Dimension = currentChatModel.Dimension,
                MaxSequenceLength = currentChatModel.MaxSequenceLength,
                InstalledVariants = currentChatModel.InstalledVariants
            };
        }

        return Ok(new ModelsRuntimeStatusResponse
        {
            Embedding = embStatus,
            Rerank = rerankStatus,
            Chat = chatStatus
        });
    }

    /// <summary>
    /// 切换同一模型的特定变体版本（如 Q4_K_M, Q8_0, ONNX 等）
    /// </summary>
    [HttpPost("switch-variant")]
    public async Task<ActionResult> SwitchVariant([FromBody] SwitchVariantRequest request)
    {
        if (string.IsNullOrEmpty(request.ModelName) || string.IsNullOrEmpty(request.VariantFileName))
        {
            return BadRequest(new { error = "模型名称和变体文件名不能为空" });
        }

        if (!await _modelSwitchLock.WaitAsync(TimeSpan.FromSeconds(30)))
        {
            return StatusCode(429, new { error = "模型切换正在进行中，请稍后重试" });
        }

        try
        {
            var success = await _modelManager.SwitchModelVariantAsync(request.ModelName, request.VariantFileName, request.IsRerank);
            if (!success)
            {
                return BadRequest(new { error = $"切换到变体 {request.VariantFileName} 失败" });
            }

            var cfg = _configManager.Load();
            if (request.ModelType == "chat" || (!request.IsRerank && _modelManager.GetChatModels().Any(m => m.Name == request.ModelName)))
            {
                // Chat 模型变体切换（配置和激活变体已由 _modelManager 更新，事件已触发）
            }
            else if (request.IsRerank)
            {
                if (_rerankService != null)
                {
                    var targetPath = cfg.Rerank.ModelPath ?? string.Empty;
                    await _rerankService.ReloadModelAsync(targetPath, request.ModelName);
                }
            }
            else
            {
                var targetPath = cfg.Embedding.ModelPath ?? string.Empty;
                await _embeddingService.ReloadModelAsync(targetPath, request.ModelName);
            }

            return Ok(new { message = $"已成功切换到版本: {request.VariantFileName}" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "切换变体失败: {ModelName} -> {Variant}", request.ModelName, request.VariantFileName);
            return StatusCode(500, new { error = $"切换变体失败: {ex.Message}" });
        }
        finally
        {
            _modelSwitchLock.Release();
        }
    }

    /// <summary>
    /// 删除特定模型变体文件
    /// </summary>
    [HttpDelete("variant")]
    public async Task<ActionResult> DeleteVariant(
        [FromQuery] string modelName,
        [FromQuery] string variantFileName,
        [FromQuery] bool isRerank = false)
    {
        if (string.IsNullOrEmpty(modelName) || string.IsNullOrEmpty(variantFileName))
        {
            return BadRequest(new { error = "模型名称和变体文件名不能为空" });
        }

        try
        {
            var success = await _modelManager.DeleteModelVariantAsync(modelName, variantFileName, isRerank);
            if (!success)
            {
                return BadRequest(new { error = $"删除变体 {variantFileName} 失败" });
            }

            return Ok(new { message = $"已成功删除变体文件: {variantFileName}" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除模型变体失败: {ModelName}/{Variant}", modelName, variantFileName);
            return StatusCode(500, new { error = $"删除模型变体失败: {ex.Message}" });
        }
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
            var oldModel = models.FirstOrDefault(m => m.Name == oldModelName);

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

            // 先验证并切换运行时模型，避免无效模型破坏旧配置/向量。
            var modelPath = ModelManager.ResolveModelFilePath(model.LocalPath);
            var reloadSuccess = await _embeddingService.ReloadModelAsync(modelPath, request.ModelName, model.MaxSequenceLength);

            if (!reloadSuccess)
            {
                _logger.LogError("Embedding 服务重新加载模型失败: {ModelName}", request.ModelName);
                return StatusCode(500, new { error = "模型重新加载失败" });
            }

            var success = await _modelManager.SwitchModelAsync(request.ModelName);
            if (!success)
            {
                _logger.LogError("模型配置切换失败: {ModelName}", request.ModelName);
                if (oldModel?.LocalPath is not null)
                {
                    var oldModelPath = ModelManager.ResolveModelFilePath(oldModel.LocalPath);
                    var rolledBack = await _embeddingService.ReloadModelAsync(
                        oldModelPath, oldModelName, oldModel.MaxSequenceLength);
                    _logger.Log(rolledBack ? LogLevel.Information : LogLevel.Critical,
                        "运行时模型回滚 {Result}: {ModelName}", rolledBack ? "成功" : "失败", oldModelName);
                }
                return StatusCode(500, new { error = "模型配置切换失败" });
            }

            if (oldDimension != model.Dimension && request.DeleteOldVectors)
            {
                _logger.LogInformation("检测到维度变化 ({Old} -> {New})，删除旧模型向量",
                    oldDimension, model.Dimension);
                var deletedCount = await _vectorStore.DeleteVectorsByModelAsync(oldModelName);
                _logger.LogInformation("已删除 {Count} 条旧模型向量", deletedCount);
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
    public async Task<ActionResult<ModelDownloadOptions>> GetDownloadOptions(string modelName, CancellationToken cancellationToken = default)
    {
        var options = await _modelManager.GetDownloadOptionsAsync(modelName, cancellationToken);
        return Ok(options);
    }

    /// <summary>
    /// 搜索开源社区模型变种（如 GGUF 量化版本、ONNX 变种）
    /// </summary>
    [HttpGet("variants/search")]
    public async Task<ActionResult<List<ModelVariantSearchResult>>> SearchVariants(
        [FromQuery] string query,
        [FromQuery] string? format = null,
        [FromQuery] int limit = 15,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return BadRequest(new { error = "搜索关键词不能为空" });
        }

        var results = await _modelManager.SearchVariantsAsync(query, format, limit, cancellationToken);
        return Ok(results);
    }

    /// <summary>
    /// 获取特定变种仓库的详情与量化文件清单
    /// </summary>
    [HttpGet("variants/detail")]
    public async Task<ActionResult<ModelVariantDetail>> GetVariantDetail(
        [FromQuery] string repository,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repository))
        {
            return BadRequest(new { error = "仓库标识 (repository) 不能为空" });
        }

        var detail = await _modelManager.GetVariantDetailAsync(repository, cancellationToken);
        if (detail == null)
        {
            return NotFound(new { error = $"未能获取仓库 '{repository}' 的详情或该仓库不存在" });
        }

        return Ok(detail);
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
            // 如果用户指定了具体变体，检查该具体变体是否已存在
            // 如果未指定具体变体，或者具体变体已存在，才跳过下载；否则允许增量下载新变体！
            bool variantAlreadyExists = false;
            if (!string.IsNullOrEmpty(request?.OnnxFilePath))
            {
                var fileName = Path.GetFileName(request.OnnxFilePath);
                variantAlreadyExists = model.InstalledVariants.Any(v =>
                    string.Equals(v.FileName, fileName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(v.RelativePath, request.OnnxFilePath, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                variantAlreadyExists = true;
            }

            if (variantAlreadyExists)
            {
                return Ok(new { message = "模型或所选变体已下载", isAlreadyDownloaded = true, model });
            }
        }

        // 检查是否已在下载中
        if (_downloadProgress.TryGetValue(modelName, out var existing) && 
            existing.Status == "downloading")
        {
            return Ok(new { message = "模型正在下载中", progress = existing });
        }

        var cts = new CancellationTokenSource();
        _downloadCts[modelName] = cts;

        // 初始化进度
        var progress = new DownloadProgress
        {
            ModelName = modelName,
            Status = "downloading",
            Stage = DownloadStage.ResolvingManifest,
            StageMessage = "正在初始化下载任务...",
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
                
                var (success, error) = await _modelManager.DownloadModelAsync(
                    modelName,
                    new Progress<DownloadProgress>(p =>
                    {
                        _downloadProgress[modelName] = p;
                    }),
                    request?.Format,
                    request?.OnnxFilePath,
                    cts.Token,
                    request?.Repository);

                if (_downloadProgress.TryGetValue(modelName, out var pg2))
                {
                    if (success)
                    {
                        pg2.Status = "completed";
                        pg2.Stage = DownloadStage.Completed;
                        pg2.StageMessage = "模型下载与配置就绪";
                        pg2.Progress = 100;
                        pg2.EndTime = DateTime.UtcNow;
                        _logger.LogInformation("[ModelManager] 模型下载完成: {ModelName}", modelName);
                    }
                    else
                    {
                        pg2.Status = "failed";
                        pg2.Stage = DownloadStage.Failed;
                        pg2.ErrorMessage = error ?? "未知错误";
                        pg2.StageMessage = $"下载失败: {error}";
                        pg2.ErrorCode = ParseErrorCode(error ?? "");
                        pg2.EndTime = DateTime.UtcNow;
                        _logger.LogError("[ModelManager] 模型下载失败: {ModelName} - {Error}", modelName, error);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("[ModelManager] 用户取消了模型下载: {ModelName}", modelName);
                if (_downloadProgress.TryGetValue(modelName, out var pgCancel))
                {
                    pgCancel.Status = "cancelled";
                    pgCancel.Stage = DownloadStage.Cancelled;
                    pgCancel.StageMessage = "下载已由用户取消";
                    pgCancel.EndTime = DateTime.UtcNow;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ModelManager] 模型下载异常: {ModelName}", modelName);
                if (_downloadProgress.TryGetValue(modelName, out var pg3))
                {
                    pg3.Status = "failed";
                    pg3.Stage = DownloadStage.Failed;
                    pg3.ErrorMessage = ex.Message;
                    pg3.StageMessage = $"下载异常: {ex.Message}";
                    pg3.ErrorCode = DownloadErrorCode.Unknown;
                    pg3.EndTime = DateTime.UtcNow;
                }
            }
            finally
            {
                _downloadCts.TryRemove(modelName, out _);
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
    /// 获取所有活跃下载任务（含 rerank_ / convert_ 前缀 key，用于前端页面恢复）
    /// </summary>
    [HttpGet("downloads/active")]
    public ActionResult<List<ActiveDownloadItem>> GetActiveDownloads()
    {
        var items = _downloadProgress
            .Where(kvp => kvp.Value.Status == "downloading")
            .Select(kvp => new ActiveDownloadItem { Key = kvp.Key, Progress = kvp.Value })
            .ToList();
        return Ok(items);
    }

    /// <summary>
    /// 取消下载
    /// </summary>
    [HttpDelete("download/{modelName}")]
    public ActionResult CancelDownload(string modelName)
    {
        var key = modelName;
        if (!_downloadProgress.ContainsKey(key) && _downloadProgress.ContainsKey($"rerank_{modelName}"))
        {
            key = $"rerank_{modelName}";
        }

        bool wasRunning = false;
        if (_downloadCts.TryRemove(key, out var cts))
        {
            try
            {
                cts.Cancel();
                cts.Dispose();
                wasRunning = true;
            }
            catch { }
        }

        if (_downloadProgress.TryGetValue(key, out var progress))
        {
            progress.Status = "cancelled";
            progress.Stage = DownloadStage.Cancelled;
            progress.StageMessage = "下载已由用户取消";
            progress.EndTime = DateTime.UtcNow;
            return Ok(new { message = "已取消下载", wasRunning, cancelled = true });
        }
        
        // 幂等确认：若后台当前已无该任务（或从未启动），确认状态为已停止并返回成功，绝不以 404 阻碍前端重置
        return Ok(new { message = "当前没有正在进行的下载任务，已确认重置", wasRunning = false, cancelled = true });
    }

    /// <summary>
    /// 取消重排模型下载
    /// </summary>
    [HttpDelete("rerank/download/{modelName}")]
    public ActionResult CancelRerankDownload(string modelName)
    {
        return CancelDownload($"rerank_{modelName}");
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
            _downloadProgress.TryRemove(modelName, out _);
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
            "gte-multilingual-base" => "GTE Multilingual Base",
            "xVITA-Embed-300M-zhTW" => "xVITA Embed 300M zh-TW",
            "Qwen3-Embedding-0.6B" => "Qwen3 Embedding 0.6B",
            "Qwen3-Embedding-4B" => "Qwen3 Embedding 4B",
            "jina-embeddings-v3" => "Jina Embeddings v3",
            "Qwen3-Reranker-0.6B" => "Qwen3 Reranker 0.6B",
            "Qwen3-Reranker-4B" => "Qwen3 Reranker 4B",
            "jina-reranker-v2-base-multilingual" => "Jina Reranker v2 Base Multilingual",
            "mxbai-rerank-base-v1" => "mxbai Rerank Base v1",
            "mxbai-rerank-xsmall-v1" => "mxbai Rerank xSmall v1",
            "gte-multilingual-reranker-base" => "GTE Multilingual Reranker Base",
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

        if (lowerMessage.Contains("401") || lowerMessage.Contains("403") || lowerMessage.Contains("授权") || lowerMessage.Contains("hf_token") || lowerMessage.Contains("unauthorized"))
            return DownloadErrorCode.AuthorizationRequired;

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

    // 重新加载 Rerank 服务的模型（优先用 LocalPath，兼容 GGUF、子目录与扁平结构）
    if (_rerankService != null)
    {
        var localDir = model.LocalPath;
        var modelFilePath = ModelManager.ResolveModelFilePath(localDir);
        if (string.IsNullOrEmpty(localDir) || (!System.IO.File.Exists(modelFilePath) && !System.IO.Directory.Exists(modelFilePath)))
        {
            var config = _configManager.Load();
            var modelsPath = config.ModelsRootPath;
            var candidatePath = Path.Combine(modelsPath, "Reranker", request.ModelName);
            modelFilePath = ModelManager.ResolveModelFilePath(candidatePath);
        }

        var reloadSuccess = await _rerankService.ReloadModelAsync(modelFilePath, request.ModelName);
        if (!reloadSuccess)
            _logger.LogWarning("Rerank 服务重新加载模型失败: {ModelName}", request.ModelName);
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
        // 如果用户指定了具体变体，检查该具体变体是否已存在
        // 如果未指定具体变体，或者具体变体已存在，才跳过下载；否则允许增量下载新变体！
        bool variantAlreadyExists = false;
        if (!string.IsNullOrEmpty(request?.OnnxFilePath))
        {
            var fileName = Path.GetFileName(request.OnnxFilePath);
            variantAlreadyExists = model.InstalledVariants.Any(v =>
                string.Equals(v.FileName, fileName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(v.RelativePath, request.OnnxFilePath, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            variantAlreadyExists = true;
        }

        if (variantAlreadyExists)
        {
            return Ok(new { message = "重排模型或所选变体已下载", isAlreadyDownloaded = true, model });
        }
    }

    // 检查是否已在下载中
    var progressKey = $"rerank_{modelName}";
    if (_downloadProgress.TryGetValue(progressKey, out var existing) && 
        existing.Status == "downloading")
    {
        return Ok(new { message = "重排模型正在下载中", progress = existing });
    }

    var cts = new CancellationTokenSource();
    _downloadCts[progressKey] = cts;

    // 初始化进度
    var progress = new DownloadProgress
    {
        ModelName = modelName,
        Status = "downloading",
        Stage = DownloadStage.ResolvingManifest,
        StageMessage = "正在初始化重排模型下载任务...",
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
            
            var (success, error) = await _modelManager.DownloadModelAsync(
                modelName,
                new Progress<DownloadProgress>(p =>
                {
                    _downloadProgress[progressKey] = p;
                }),
                request?.Format,
                request?.OnnxFilePath,
                cts.Token,
                request?.Repository);

            if (_downloadProgress.TryGetValue(progressKey, out var pg2))
            {
                if (success)
                {
                    pg2.Status = "completed";
                    pg2.Stage = DownloadStage.Completed;
                    pg2.StageMessage = "重排模型下载与配置就绪";
                    pg2.Progress = 100;
                    pg2.EndTime = DateTime.UtcNow;
                    _logger.LogInformation("[RerankModel] 重排模型下载完成: {ModelName}", modelName);
                }
                else
                {
                    pg2.Status = "failed";
                    pg2.Stage = DownloadStage.Failed;
                    pg2.ErrorMessage = error ?? "未知错误";
                    pg2.StageMessage = $"下载失败: {error}";
                    pg2.ErrorCode = ParseErrorCode(error ?? "");
                    pg2.EndTime = DateTime.UtcNow;
                    _logger.LogError("[RerankModel] 重排模型下载失败: {ModelName} - {Error}", modelName, error);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("[RerankModel] 用户取消了重排模型下载: {ModelName}", modelName);
            if (_downloadProgress.TryGetValue(progressKey, out var pgCancel))
            {
                pgCancel.Status = "cancelled";
                pgCancel.Stage = DownloadStage.Cancelled;
                pgCancel.StageMessage = "下载已由用户取消";
                pgCancel.EndTime = DateTime.UtcNow;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[RerankModel] 重排模型下载异常: {ModelName}", modelName);
            if (_downloadProgress.TryGetValue(progressKey, out var pg3))
            {
                pg3.Status = "failed";
                pg3.Stage = DownloadStage.Failed;
                pg3.ErrorMessage = ex.Message;
                pg3.StageMessage = $"下载异常: {ex.Message}";
                pg3.ErrorCode = DownloadErrorCode.Unknown;
                pg3.EndTime = DateTime.UtcNow;
            }
        }
        finally
        {
            _downloadCts.TryRemove(progressKey, out _);
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
        _downloadProgress.TryRemove(progressKey, out _);
        return Ok(new { message = "重排模型已删除" });
    }
    
    return NotFound(new { error = "重排模型不存在或删除失败" });
}

/// <summary>
/// 获取重排模型下载选项
/// </summary>
[HttpGet("rerank/download-options/{modelName}")]
public async Task<ActionResult<ModelDownloadOptions>> GetRerankDownloadOptions(string modelName, CancellationToken cancellationToken = default)
{
    var options = await _modelManager.GetDownloadOptionsAsync(modelName, cancellationToken);
    return Ok(options);
}

    // ==================== 聊天模型管理 API ====================

    /// <summary>
    /// 获取所有聊天模型列表
    /// </summary>
    [HttpGet("chat")]
    public async Task<ActionResult<List<ModelInfo>>> GetChatModels()
    {
        var models = await _modelManager.GetAvailableModelsAsync("chat");
        return Ok(models);
    }

    /// <summary>
    /// 获取已下载的聊天模型列表
    /// </summary>
    [HttpGet("chat/downloaded")]
    public ActionResult<List<ModelInfo>> GetDownloadedChatModels()
    {
        var models = _modelManager.GetDownloadedChatModels();
        return Ok(models);
    }

    /// <summary>
    /// 获取当前聊天模型
    /// </summary>
    [HttpGet("chat/current")]
    public ActionResult<ModelInfo?> GetCurrentChatModel()
    {
        var model = _modelManager.GetCurrentChatModel();
        return Ok(model);
    }

    /// <summary>
    /// 切换聊天模型
    /// </summary>
    [HttpPost("chat/switch")]
    public async Task<ActionResult> SwitchChatModel([FromBody] SwitchModelRequest request)
    {
        if (string.IsNullOrEmpty(request.ModelName))
        {
            return BadRequest(new { error = "模型名称不能为空" });
        }

        var models = _modelManager.GetChatModels();
        var model = models.FirstOrDefault(m => m.Name == request.ModelName);

        if (model == null)
        {
            return NotFound(new { error = $"聊天模型不存在: {request.ModelName}" });
        }

        if (!model.IsDownloaded)
        {
            return BadRequest(new { error = $"聊天模型尚未下载: {request.ModelName}", needDownload = true });
        }

        _logger.LogInformation("开始切换聊天模型: {ModelName}", request.ModelName);

        var success = await _modelManager.SwitchChatModelAsync(request.ModelName);

        if (!success)
        {
            _logger.LogError("聊天模型切换失败: {ModelName}", request.ModelName);
            return StatusCode(500, new { error = "聊天模型切换失败" });
        }

        _logger.LogInformation("聊天模型切换成功: {ModelName}", request.ModelName);
        return Ok(new
        {
            message = $"已切换到聊天模型: {model.DisplayName}",
            model
        });
    }

    /// <summary>
    /// 下载聊天模型
    /// </summary>
    [HttpPost("chat/download/{modelName}")]
    public async Task<ActionResult> DownloadChatModel(string modelName, [FromBody] DownloadModelRequest? request = null)
    {
        return await DownloadModel(modelName, request);
    }

    /// <summary>
    /// 获取聊天模型下载进度
    /// </summary>
    [HttpGet("chat/download/{modelName}/progress")]
    public ActionResult<DownloadProgress> GetChatDownloadProgress(string modelName)
    {
        return GetDownloadProgress(modelName);
    }

    /// <summary>
    /// 取消聊天模型下载
    /// </summary>
    [HttpDelete("chat/download/{modelName}")]
    public ActionResult CancelChatDownload(string modelName)
    {
        return CancelDownload(modelName);
    }

    /// <summary>
    /// 获取聊天模型下载选项
    /// </summary>
    [HttpGet("chat/download-options/{modelName}")]
    public async Task<ActionResult<ModelDownloadOptions>> GetChatDownloadOptions(string modelName, CancellationToken cancellationToken = default)
    {
        var options = await _modelManager.GetDownloadOptionsAsync(modelName, cancellationToken);
        return Ok(options);
    }

    /// <summary>
    /// 删除聊天模型
    /// </summary>
    [HttpDelete("chat/{modelName}")]
    public async Task<ActionResult> DeleteChatModel(string modelName)
    {
        var config = _configManager.Load();
        if (config.Chat?.Model == modelName)
        {
            return BadRequest(new { error = "无法删除当前正在使用的聊天模型" });
        }

        var success = await _modelManager.DeleteModelAsync(modelName);
        if (success)
        {
            _downloadProgress.TryRemove(modelName, out _);
            return Ok(new { message = "聊天模型已删除" });
        }

        return NotFound(new { error = "聊天模型不存在或删除失败" });
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
    /// 指定要下载的文件路径（如: model.onnx, onnx/model_qint8_avx512.onnx, bge-m3-Q4_K_M.gguf）
    /// 如果不指定，将自动选择最佳选项
    /// </summary>
    public string? OnnxFilePath { get; set; }

    /// <summary>
    /// 指定格式类型: "gguf" 或 "onnx"，可选
    /// </summary>
    public string? Format { get; set; }

    /// <summary>
    /// 指定来源模型仓库（主仓库或社区候选源），可选
    /// </summary>
    public string? Repository { get; set; }
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

public class ActiveDownloadItem
{
    /// <summary>
    /// 进度字典 key（含 rerank_ / convert_ 前缀），前端用于重建状态 Map
    /// </summary>
    public string Key { get; set; } = string.Empty;
    public DownloadProgress Progress { get; set; } = new();
}

/// <summary>
/// 模型运行时多模型协同状态快照
/// </summary>
public class ModelsRuntimeStatusResponse
{
    public SingleModelStatusResponse? Embedding { get; set; }
    public SingleModelStatusResponse? Rerank { get; set; }
    public SingleModelStatusResponse? Chat { get; set; }
}

/// <summary>
/// 单个模型的详细实时状态快照
/// </summary>
public class SingleModelStatusResponse
{
    public string ModelName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? ActiveVariant { get; set; }
    public string? ActiveVariantPath { get; set; }
    public int State { get; set; }
    public string StateName { get; set; } = "Unloaded";
    public string StateDescription { get; set; } = "未加载";
    public int ActiveLeases { get; set; }
    public bool IsReady { get; set; }
    public bool IsGpuMode { get; set; }
    public string ExecutionDevice { get; set; } = "CPU";
    public double? TotalVramGb { get; set; }
    public string? OffloadDescription { get; set; }
    public string? Quantization { get; set; }
    public string? ModelFormat { get; set; }
    public int Dimension { get; set; }
    public int MaxSequenceLength { get; set; }
    public string? RetentionStrategy { get; set; }
    public int IdleTimeoutSeconds { get; set; }
    public bool UnloadOnlyWhenMinimizedToTray { get; set; }
    public string? LastError { get; set; }
    public List<ModelVariantInfo> InstalledVariants { get; set; } = new();
}

/// <summary>
/// 切换同模型特定变体请求
/// </summary>
public class SwitchVariantRequest
{
    public string ModelName { get; set; } = string.Empty;
    public string VariantFileName { get; set; } = string.Empty;
    public bool IsRerank { get; set; }
    public string? ModelType { get; set; }
}

