using Microsoft.AspNetCore.Mvc;
using ReferenceRAG.Business.Features.KnowledgeBase.Services;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;

namespace ReferenceRAG.Service.Controllers;

/// <summary>
/// 知识库与连接器管理 REST API 控制器
/// </summary>
[ApiController]
[Route("api/knowledge-bases")]
public class KnowledgeBasesController : ControllerBase
{
    private readonly IKnowledgeBaseService _kbService;
    private readonly ILogger<KnowledgeBasesController> _logger;

    public KnowledgeBasesController(
        IKnowledgeBaseService kbService,
        ILogger<KnowledgeBasesController> logger)
    {
        _kbService = kbService ?? throw new ArgumentNullException(nameof(kbService));
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<KnowledgeBaseDefinition>>> ListKnowledgeBases(CancellationToken ct)
    {
        var list = await _kbService.ListKnowledgeBasesAsync(ct);
        return Ok(list);
    }

    [HttpGet("embedding-specs")]
    public async Task<ActionResult<IReadOnlyList<EmbeddingSpec>>> ListEmbeddingSpecs(CancellationToken ct)
    {
        var list = await _kbService.ListEmbeddingSpecsAsync(ct);
        return Ok(list);
    }

    [HttpPost]
    public async Task<ActionResult<KnowledgeBaseDefinition>> CreateKnowledgeBase(
        [FromBody] CreateKnowledgeBaseRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new { message = "知识库名称不能为空" });
        }

        try
        {
            EmbeddingSpec? customSpec = null;
            if (request.EmbeddingSpec != null && !string.IsNullOrWhiteSpace(request.EmbeddingSpec.ModelName))
            {
                var specId = string.IsNullOrWhiteSpace(request.EmbeddingSpec.Id)
                    ? $"spec_{Guid.NewGuid():N}"[..16]
                    : request.EmbeddingSpec.Id;
                customSpec = new EmbeddingSpec(
                    Id: specId,
                    ModelName: request.EmbeddingSpec.ModelName.Trim(),
                    Dimension: request.EmbeddingSpec.Dimension > 0 ? request.EmbeddingSpec.Dimension : 1024,
                    Metric: string.IsNullOrWhiteSpace(request.EmbeddingSpec.Metric) ? "cosine" : request.EmbeddingSpec.Metric.Trim(),
                    QueryPrefix: request.EmbeddingSpec.QueryPrefix,
                    DocumentPrefix: request.EmbeddingSpec.DocumentPrefix,
                    MaxTokens: request.EmbeddingSpec.MaxTokens > 0 ? request.EmbeddingSpec.MaxTokens : 512,
                    EnableDualChannel: request.EmbeddingSpec.EnableDualChannel,
                    IngestionBackend: request.EmbeddingSpec.IngestionBackend,
                    IngestionBaseUrl: request.EmbeddingSpec.IngestionBaseUrl,
                    IngestionApiKey: request.EmbeddingSpec.IngestionApiKey,
                    SearchBackend: request.EmbeddingSpec.SearchBackend
                );
            }

            var kb = await _kbService.CreateKnowledgeBaseAsync(
                request.Name,
                request.Description,
                request.EmbeddingSpecId ?? customSpec?.Id ?? "default-spec",
                customSpec,
                ct);
            return CreatedAtAction(nameof(GetKnowledgeBase), new { id = kb.Id }, kb);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "创建知识库失败");
            return StatusCode(500, new { message = ex.Message });
        }
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<KnowledgeBaseDefinition>> GetKnowledgeBase(string id, CancellationToken ct)
    {
        var kb = await _kbService.GetKnowledgeBaseAsync(id, ct);
        if (kb == null) return NotFound(new { message = $"知识库不存在: {id}" });
        return Ok(kb);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteKnowledgeBase(string id, CancellationToken ct)
    {
        var success = await _kbService.DeleteKnowledgeBaseAsync(id, ct);
        if (!success) return NotFound(new { message = $"知识库不存在或已被删除: {id}" });
        return NoContent();
    }

    [HttpGet("{id}/connectors")]
    public async Task<ActionResult<IReadOnlyList<ConnectorDefinition>>> GetConnectors(string id, CancellationToken ct)
    {
        var connectors = await _kbService.GetConnectorsAsync(id, ct);
        return Ok(connectors);
    }

    [HttpPost("{id}/connectors")]
    public async Task<ActionResult<ConnectorDefinition>> AddConnector(
        string id,
        [FromBody] AddConnectorRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.TargetPath))
        {
            return BadRequest(new { message = "连接器目标路径 TargetPath 不能为空" });
        }

        try
        {
            var connector = await _kbService.AddConnectorAsync(
                id,
                request.Kind ?? "Folder",
                request.TargetPath,
                request.FilePattern,
                ct);
            return Ok(connector);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { message = ex.Message });
        }
    }

    [HttpDelete("{id}/connectors/{connectorId}")]
    public async Task<IActionResult> DeleteConnector(string id, string connectorId, CancellationToken ct)
    {
        var success = await _kbService.DeleteConnectorAsync(connectorId, ct);
        if (!success) return NotFound(new { message = $"连接器不存在: {connectorId}" });
        return NoContent();
    }

    [HttpPost("{id}/jobs")]
    public async Task<ActionResult<JobDefinition>> TriggerSyncJob(
        string id,
        [FromBody] TriggerJobRequest request,
        CancellationToken ct)
    {
        try
        {
            var job = await _kbService.TriggerSyncJobAsync(id, request.ConnectorId, ct);
            return Accepted($"/api/jobs/{job.Id}", job);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
}

/// <summary>
/// 异步作业管理 API 控制器
/// </summary>
[ApiController]
[Route("api/jobs")]
public class JobsController : ControllerBase
{
    private readonly IKnowledgeBaseService _kbService;

    public JobsController(IKnowledgeBaseService kbService)
    {
        _kbService = kbService;
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<JobDefinition>> GetJob(string id, CancellationToken ct)
    {
        var job = await _kbService.GetJobAsync(id, ct);
        if (job == null) return NotFound(new { message = $"任务不存在: {id}" });
        return Ok(job);
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<JobDefinition>>> ListJobs(
        [FromQuery] string? knowledgeBaseId,
        CancellationToken ct)
    {
        var list = await _kbService.ListJobsAsync(knowledgeBaseId, ct);
        return Ok(list);
    }

    [HttpPost("{id}/cancel")]
    public async Task<IActionResult> CancelJob(string id, CancellationToken ct)
    {
        var success = await _kbService.CancelJobAsync(id, ct);
        if (!success) return NotFound(new { message = $"任务不可取消或不存在: {id}" });
        return Ok(new { message = "已发送取消请求", jobId = id });
    }
}

/// <summary>
/// 跨知识库受控检索控制器
/// </summary>
[ApiController]
[Route("api/knowledge-base-search")]
public class KnowledgeBaseSearchController : ControllerBase
{
    private readonly IKnowledgeBaseSearchService _searchService;

    public KnowledgeBaseSearchController(IKnowledgeBaseSearchService searchService)
    {
        _searchService = searchService;
    }

    [HttpPost]
    public async Task<ActionResult<CrossKnowledgeBaseSearchResponse>> Search(
        [FromBody] CrossKnowledgeBaseSearchRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
        {
            return BadRequest(new { message = "搜索查询 Query 不能为空" });
        }

        try
        {
            var response = await _searchService.SearchAsync(request, ct);
            return Ok(response);
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(503, new { message = ex.Message });
        }
    }
}

public sealed record EmbeddingSpecDto(
    string? Id,
    string ModelName,
    int Dimension,
    string Metric = "cosine",
    string? QueryPrefix = null,
    string? DocumentPrefix = null,
    int MaxTokens = 512,
    bool EnableDualChannel = false,
    string? IngestionBackend = null,
    string? IngestionBaseUrl = null,
    string? IngestionApiKey = null,
    string? SearchBackend = null);

public sealed record CreateKnowledgeBaseRequest(
    string Name,
    string? Description = null,
    string? EmbeddingSpecId = null,
    EmbeddingSpecDto? EmbeddingSpec = null);
public sealed record AddConnectorRequest(string? Kind, string TargetPath, string? FilePattern = null);
public sealed record TriggerJobRequest(string ConnectorId);
