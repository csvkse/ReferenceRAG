using Microsoft.AspNetCore.Mvc;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;

namespace ReferenceRAG.Service.Controllers;

/// <summary>
/// AI 专用查询接口
/// </summary>
[ApiController]
[Route("api/ai")]
public class AIQueryController : ControllerBase
{
    private readonly ISearchService _searchService;
    private readonly QueryStatsService _statsService;
    private readonly ILogger<AIQueryController> _logger;

    public AIQueryController(
        ISearchService searchService,
        QueryStatsService statsService,
        ILogger<AIQueryController> logger)
    {
        _searchService = searchService;
        _statsService = statsService;
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
}
