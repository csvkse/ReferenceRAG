using Microsoft.AspNetCore.Mvc;
using ReferenceRAG.Core.Features.ModelManagement.Telemetry;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;

namespace ReferenceRAG.Service.Controllers;

/// <summary>
/// 搜索状态信息
/// </summary>
public class SearchStatusResponse
{
    /// <summary>
    /// 当前嵌入模型名称
    /// </summary>
    public string? EmbeddingModel { get; set; }

    /// <summary>
    /// 当前生效嵌入模型变体（如 Q4_K_M, FP16）
    /// </summary>
    public string? EmbeddingVariant { get; set; }

    /// <summary>
    /// 嵌入模型文件格式（gguf / onnx）
    /// </summary>
    public string EmbeddingFormat { get; set; } = string.Empty;

    /// <summary>
    /// 嵌入模型硬件执行设备（如 CUDA 0 (GPU), DirectML, CPU）
    /// </summary>
    public string EmbeddingDevice { get; set; } = string.Empty;

    /// <summary>
    /// 是否为 GPU 硬件加速
    /// </summary>
    public bool EmbeddingIsGpu { get; set; }

    /// <summary>
    /// 嵌入模型生命周期状态（Ready, Idle, Busy 等）
    /// </summary>
    public string EmbeddingState { get; set; } = string.Empty;

    /// <summary>
    /// 嵌入模型维度
    /// </summary>
    public int EmbeddingDimension { get; set; }

    public string EmbeddingProvider { get; set; } = string.Empty;

    /// <summary>
    /// 当前重排模型名称
    /// </summary>
    public string? RerankModel { get; set; }

    /// <summary>
    /// 当前生效重排模型变体（如 Q4_K_M, Q8_0）
    /// </summary>
    public string? RerankVariant { get; set; }

    /// <summary>
    /// 重排模型文件格式（gguf / onnx）
    /// </summary>
    public string RerankFormat { get; set; } = string.Empty;

    /// <summary>
    /// 重排模型硬件执行设备
    /// </summary>
    public string RerankDevice { get; set; } = string.Empty;

    /// <summary>
    /// 重排是否为 GPU 硬件加速
    /// </summary>
    public bool RerankIsGpu { get; set; }

    /// <summary>
    /// 重排模型生命周期状态
    /// </summary>
    public string RerankState { get; set; } = string.Empty;

    /// <summary>
    /// 重排是否启用
    /// </summary>
    public bool RerankEnabled { get; set; }

    /// <summary>
    /// 重排模型是否已经加载到内存；与配置启用状态分开，避免懒加载模型被误报为未启用
    /// </summary>
    public bool RerankLoaded { get; set; }

    public string RerankProvider { get; set; } = string.Empty;

    public bool IndexRequiresRebuild { get; set; }

    /// <summary>
    /// BM25 索引文档数
    /// </summary>
    public int Bm25IndexedDocuments { get; set; }

    /// <summary>
    /// BM25 索引是否存在
    /// </summary>
    public bool Bm25HasIndex { get; set; }

    /// <summary>
    /// 向量索引文档数
    /// </summary>
    public int VectorIndexedChunks { get; set; }

    /// <summary>
    /// 向量索引是否存在
    /// </summary>
    public bool VectorHasIndex { get; set; }

    /// <summary>
    /// 总文件数
    /// </summary>
    public int TotalFiles { get; set; }
}

public sealed record RerankStatusSnapshot(bool Enabled, bool Loaded, string Model)
{
    public static RerankStatusSnapshot Create(bool configuredEnabled, IRerankService service) =>
        new(configuredEnabled, service.IsLoaded, service.ModelName);
}

/// <summary>
/// AI 专用查询接口
/// </summary>
[ApiController]
[Route("api/ai")]
public class AIQueryController : ControllerBase
{
    private readonly ISearchService _searchService;
    private readonly IQueryStatsService _statsService;
    private readonly IEmbeddingService _embeddingService;
    private readonly IVectorStore _vectorStore;
    private readonly IBM25Store _bm25Store;
    private readonly IRerankService _rerankService;
    private readonly ConfigManager _configManager;
    private readonly ILogger<AIQueryController> _logger;
    private readonly IModelManager? _modelManager;
    private readonly IModelTelemetryService? _telemetryService;

    public AIQueryController(
        ISearchService searchService,
        IQueryStatsService statsService,
        IEmbeddingService embeddingService,
        IVectorStore vectorStore,
        IBM25Store bm25Store,
        IRerankService rerankService,
        ConfigManager configManager,
        ILogger<AIQueryController> logger,
        IModelManager? modelManager = null,
        IModelTelemetryService? telemetryService = null)
    {
        _searchService = searchService;
        _statsService = statsService;
        _embeddingService = embeddingService;
        _vectorStore = vectorStore;
        _bm25Store = bm25Store;
        _rerankService = rerankService;
        _configManager = configManager;
        _logger = logger;
        _modelManager = modelManager;
        _telemetryService = telemetryService;
    }

    /// <summary>
    /// 智能查询 - AI 第一次调用的推荐接口
    /// </summary>
    [HttpPost("query")]
    public async Task<ActionResult<AIQueryResponse>> Query([FromBody] AIQueryRequest request)
    {
        try
        {
            // 根据模式自动调整参数
            var adjustedRequest = AdjustRequestByMode(request);

            var response = await _searchService.SearchAsync(adjustedRequest);

            // 记录查询统计
            await _statsService.RecordQueryAsync(
                query: request.Query,
                durationMs: response.Stats?.DurationMs ?? 0,
                resultCount: response.Stats?.TotalMatches ?? 0,
                sources: request.Sources,
                mode: request.Mode.ToString()
            );

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "查询失败: {Query}", request.Query);
            return StatusCode(500, new { error = "查询处理失败，请稍后重试" });
        }
    }

    /// <summary>
    /// 深入查询 - 当 AI 需要更多细节时
    /// </summary>
    [HttpPost("drill-down")]
    public async Task<ActionResult<DrillDownResponse>> DrillDown([FromBody] DrillDownRequest request)
    {
        try
        {
            var response = await _searchService.DrillDownAsync(request);
            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "深入查询失败");
            return StatusCode(500, new { error = "深入查询处理失败，请稍后重试" });
        }
    }

    /// <summary>
    /// 根据模式调整请求参数
    /// 优先使用前端传入的值，未传入时使用模式默认值
    /// </summary>
    private AIQueryRequest AdjustRequestByMode(AIQueryRequest request)
    {
        // 获取模式默认参数
        var (defaultTopK, defaultContextWindow, defaultMaxTokens) = request.Mode switch
        {
            QueryMode.Quick => (3, 0, 1000),
            QueryMode.Standard => (10, 1, 3000),
            QueryMode.Hybrid => (15, 1, 4000),
            QueryMode.Deep => (20, 2, 6000),
            QueryMode.HybridRerank => (10, 1, 4000),
            _ => (10, 1, 3000)
        };

        return new AIQueryRequest
        {
            Query = request.Query,
            Mode = request.Mode,
            // 优先使用前端传入的值，否则使用模式默认值
            TopK = request.TopK > 0 ? request.TopK : defaultTopK,
            ContextWindow = request.ContextWindow >= 0 ? request.ContextWindow : defaultContextWindow,
            MaxTokens = request.MaxTokens > 0 ? request.MaxTokens : defaultMaxTokens,
            Sources = request.Sources,
            Filters = request.Filters,
            Options = request.Options,
            K1 = request.K1,
            B = request.B,
            // HybridRerank 与 Deep 模式强制启用重排
            EnableRerank = (request.Mode == QueryMode.HybridRerank || request.Mode == QueryMode.Deep) ? true : request.EnableRerank,
            RerankTopN = request.RerankTopN,
            Slim = request.Slim
        };
    }

    /// <summary>
    /// 获取搜索状态信息 - 用于前端显示当前模型和索引状态
    /// </summary>
    [HttpGet("status")]
    public async Task<ActionResult<SearchStatusResponse>> GetSearchStatus()
    {
        try
        {
            var config = _configManager.Load();
            var rerankStatus = RerankStatusSnapshot.Create(config.Rerank.Enabled, _rerankService);

            // 获取 BM25 索引统计
            var bm25Stats = await _bm25Store.GetStatsAsync();

            // 获取向量索引统计
            var vectorStats = await _vectorStore.GetVectorStatsAsync();

            var currentEmb = _modelManager?.GetCurrentModel();
            var currentRerank = _modelManager?.GetCurrentRerankModel();
            var embTel = _telemetryService?.GetCurrentEmbeddingTelemetry();
            var rerankTel = _telemetryService?.GetCurrentRerankTelemetry();

            var embVariant = currentEmb?.ActiveVariantFileName ?? embTel?.Configuration.Quantization;
            var embFormat = currentEmb?.InstalledVariants.FirstOrDefault(v => v.IsActive)?.Format
                ?? embTel?.Configuration.ModelFormat
                ?? (currentEmb?.LocalPath?.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) == true ? "gguf" : "onnx");
            var embDevice = embTel?.Hardware.ExecutionDevice ?? "CPU";
            var embIsGpu = embTel?.Hardware.IsGpuMode ?? false;
            var embState = embTel?.StateMachine.StateName ?? "就绪";

            var rerankVariant = currentRerank?.ActiveVariantFileName ?? rerankTel?.Configuration.Quantization;
            var rerankFormat = currentRerank?.InstalledVariants.FirstOrDefault(v => v.IsActive)?.Format
                ?? rerankTel?.Configuration.ModelFormat
                ?? (currentRerank?.LocalPath?.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) == true ? "gguf" : "onnx");
            var rerankDevice = rerankTel?.Hardware.ExecutionDevice ?? "CPU";
            var rerankIsGpu = rerankTel?.Hardware.IsGpuMode ?? false;
            var rerankState = rerankTel?.StateMachine.StateName ?? (rerankStatus.Loaded ? "已加载" : "未加载");

            var response = new SearchStatusResponse
            {
                EmbeddingModel = !string.IsNullOrEmpty(embTel?.Configuration.ModelName) ? embTel.Configuration.ModelName : _embeddingService.ModelName,
                EmbeddingVariant = embVariant,
                EmbeddingFormat = embFormat,
                EmbeddingDevice = embDevice,
                EmbeddingIsGpu = embIsGpu,
                EmbeddingState = embState,
                EmbeddingDimension = _embeddingService.Dimension,
                EmbeddingProvider = GetProviderName(_embeddingService, embFormat),
                RerankModel = !string.IsNullOrEmpty(rerankTel?.Configuration.ModelName) ? rerankTel.Configuration.ModelName : rerankStatus.Model,
                RerankVariant = rerankVariant,
                RerankFormat = rerankFormat,
                RerankDevice = rerankDevice,
                RerankIsGpu = rerankIsGpu,
                RerankState = rerankState,
                RerankProvider = GetProviderName(_rerankService, rerankFormat),
                RerankEnabled = rerankStatus.Enabled,
                RerankLoaded = rerankStatus.Loaded,
                Bm25IndexedDocuments = bm25Stats.TotalDocuments,
                Bm25HasIndex = bm25Stats.TotalDocuments > 0,
                VectorIndexedChunks = vectorStats.Sum(v => v.VectorCount),
                VectorHasIndex = vectorStats.Sum(v => v.VectorCount) > 0,
                TotalFiles = 0  // 需要单独获取
            };

            response.IndexRequiresRebuild = vectorStats.Count > 0 && !vectorStats.Any(stat =>
                string.Equals(stat.ModelName, response.EmbeddingModel, StringComparison.OrdinalIgnoreCase) &&
                (response.EmbeddingDimension <= 0 || stat.Dimension == response.EmbeddingDimension));

            response.TotalFiles = await _vectorStore.GetFileCountAsync();

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取搜索状态失败");
            return StatusCode(500, new { error = "获取搜索状态失败" });
        }
    }

    private static string GetProviderName(object? service, string format = "")
    {
        if (service == null) return "none";
        if (service.GetType().Name.StartsWith("OpenAI", StringComparison.Ordinal)) return "openai";
        if (string.Equals(format, "gguf", StringComparison.OrdinalIgnoreCase) ||
            service.GetType().Name.Contains("Llama", StringComparison.OrdinalIgnoreCase))
            return "gguf";
        return "onnx";
    }
}
