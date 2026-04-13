using Microsoft.AspNetCore.Mvc;
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
    /// 嵌入模型维度
    /// </summary>
    public int EmbeddingDimension { get; set; }

    /// <summary>
    /// 当前重排模型名称
    /// </summary>
    public string? RerankModel { get; set; }

    /// <summary>
    /// 重排是否启用
    /// </summary>
    public bool RerankEnabled { get; set; }

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

/// <summary>
/// AI 专用查询接口
/// </summary>
[ApiController]
[Route("api/ai")]
public class AIQueryController : ControllerBase
{
    private readonly ISearchService _searchService;
    private readonly QueryStatsService _statsService;
    private readonly IModelManager _modelManager;
    private readonly IVectorStore _vectorStore;
    private readonly IBM25Store _bm25Store;
    private readonly IRerankService _rerankService;
    private readonly ILogger<AIQueryController> _logger;

    public AIQueryController(
        ISearchService searchService,
        QueryStatsService statsService,
        IModelManager modelManager,
        IVectorStore vectorStore,
        IBM25Store bm25Store,
        IRerankService rerankService,
        ILogger<AIQueryController> logger)
    {
        _searchService = searchService;
        _statsService = statsService;
        _modelManager = modelManager;
        _vectorStore = vectorStore;
        _bm25Store = bm25Store;
        _rerankService = rerankService;
        _logger = logger;
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
    /// </summary>
    private AIQueryRequest AdjustRequestByMode(AIQueryRequest request)
    {
        var adjusted = new AIQueryRequest
        {
            Query = request.Query,
            Mode = request.Mode,
            Sources = request.Sources,
            Filters = request.Filters,
            Options = request.Options,
            K1 = request.K1,
            B = request.B
        };

        return request.Mode switch
        {
            QueryMode.Quick => new AIQueryRequest
            {
                Query = request.Query,
                Mode = request.Mode,
                TopK = 3,
                ContextWindow = 0,
                MaxTokens = 1000,
                Sources = request.Sources,
                Filters = request.Filters,
                Options = request.Options,
                K1 = request.K1,
                B = request.B
            },
            QueryMode.Standard => new AIQueryRequest
            {
                Query = request.Query,
                Mode = request.Mode,
                TopK = 10,
                ContextWindow = 1,
                MaxTokens = 3000,
                Sources = request.Sources,
                Filters = request.Filters,
                Options = request.Options,
                K1 = request.K1,
                B = request.B
            },
            QueryMode.Hybrid => new AIQueryRequest
            {
                Query = request.Query,
                Mode = request.Mode,
                TopK = 15,
                ContextWindow = 1,
                MaxTokens = 4000,
                Sources = request.Sources,
                Filters = request.Filters,
                Options = request.Options,
                K1 = request.K1,
                B = request.B
            },
            QueryMode.Deep => new AIQueryRequest
            {
                Query = request.Query,
                Mode = request.Mode,
                TopK = 20,
                ContextWindow = 2,
                MaxTokens = 6000,
                Sources = request.Sources,
                Filters = request.Filters,
                Options = request.Options,
                K1 = request.K1,
                B = request.B
            },
            QueryMode.HybridRerank => new AIQueryRequest
            {
                Query = request.Query,
                Mode = request.Mode,
                TopK = 10,  // 最终返回数量
                ContextWindow = 1,
                MaxTokens = 4000,
                Sources = request.Sources,
                Filters = request.Filters,
                Options = request.Options,
                K1 = request.K1,
                B = request.B,
                EnableRerank = true  // 强制启用重排
            },
            _ => adjusted
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
            // 获取嵌入模型信息
            var embeddingModel = _modelManager.GetCurrentModel();

            // 获取重排模型信息
            var rerankModel = _modelManager.GetCurrentRerankModel();
            var rerankEnabled = _rerankService.IsLoaded;

            // 获取 BM25 索引统计
            var bm25Stats = await _bm25Store.GetStatsAsync();

            // 获取向量索引统计
            var vectorStats = await _vectorStore.GetVectorStatsAsync();

            var response = new SearchStatusResponse
            {
                EmbeddingModel = embeddingModel?.DisplayName ?? embeddingModel?.Name,
                EmbeddingDimension = embeddingModel?.Dimension ?? 0,
                RerankModel = rerankModel?.DisplayName ?? rerankModel?.Name,
                RerankEnabled = rerankEnabled,
                Bm25IndexedDocuments = bm25Stats.TotalDocuments,
                Bm25HasIndex = bm25Stats.TotalDocuments > 0,
                VectorIndexedChunks = vectorStats.Sum(v => v.VectorCount),
                VectorHasIndex = vectorStats.Sum(v => v.VectorCount) > 0,
                TotalFiles = 0  // 需要单独获取
            };

            // 获取文件总数
            var files = await _vectorStore.GetAllFilesAsync();
            response.TotalFiles = files.Count();

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取搜索状态失败");
            return StatusCode(500, new { error = "获取搜索状态失败" });
        }
    }
}
