using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;
using ReferenceRAG.Business.Features.KnowledgeBase.Services;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;

namespace ReferenceRAG.Service.Controllers;

/// <summary>
/// 知识库与连接器管理 REST API 控制器
/// </summary>
[ApiController]
[Route("api/knowledge-bases")]
[RestrictKnowledgeBaseToFullSourceScope]
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
        var context = ControllerContext.HttpContext;
        var allowed = ApiKeyPermissionMatrix.GetEffectiveAllowedSources(context?.Items["CurrentApiKey"] as ApiKeyEntity);
        if (allowed != null)
        {
            var search = context?.RequestServices.GetService<IKnowledgeBaseSearchService>();
            if (search == null) return StatusCode(403, new { message = "无法验证独立知识库的数据源权限。" });
            return Ok(await search.ListAccessibleKnowledgeBasesAsync(allowed, ct));
        }
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
                request.EmbeddingSpecId ?? customSpec?.Id,
                customSpec,
                request.ChunkingSpecId ?? request.ChunkingSpec?.Id,
                request.ChunkingSpec,
                ct);
            return CreatedAtAction(nameof(GetKnowledgeBase), new { id = kb.Id }, kb);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
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

    [HttpGet("{id}/paths")]
    public async Task<ActionResult<PathsResponse>> GetPaths(string id, CancellationToken ct)
    {
        var response = await _kbService.GetPathsAsync(new[] { id }, ct);
        return Ok(response);
    }

    [HttpPost("paths")]
    public async Task<ActionResult<PathsResponse>> GetBatchPaths(
        [FromBody] GetKnowledgeBasePathsRequest request,
        CancellationToken ct)
    {
        var response = await _kbService.GetPathsAsync(request.KnowledgeBaseIds ?? Array.Empty<string>(), ct);
        return Ok(response);
    }

    [HttpGet("{id}/connectors")]
    public async Task<ActionResult<IReadOnlyList<ConnectorDetailDto>>> GetConnectors(string id, CancellationToken ct)
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
    public async Task<IActionResult> DeleteConnector(
        string id,
        string connectorId,
        [FromQuery] bool cascade = true,
        CancellationToken ct = default)
    {
        var success = await _kbService.DeleteConnectorAsync(id, connectorId, cascade, ct);
        if (!success) return NotFound(new { message = $"连接器不存在: {connectorId}" });
        return NoContent();
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<KnowledgeBaseDefinition>> UpdateKnowledgeBase(
        string id,
        [FromBody] UpdateKnowledgeBaseRequest request,
        CancellationToken ct)
    {
        try
        {
            var updated = await _kbService.UpdateKnowledgeBaseAsync(id, request, ct);
            return Ok(updated);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新知识库失败: {Id}", id);
            return StatusCode(500, new { message = ex.Message });
        }
    }

    [HttpPatch("{id}/toggle")]
    public async Task<IActionResult> ToggleKnowledgeBase(
        string id,
        [FromBody] ToggleKnowledgeBaseRequest request,
        CancellationToken ct)
    {
        var success = await _kbService.ToggleKnowledgeBaseAsync(id, request.Enabled, ct);
        if (!success) return NotFound(new { message = $"知识库不存在: {id}" });
        return Ok(new { success = true, id, enabled = request.Enabled });
    }

    [HttpPut("{id}/connectors/{connectorId}")]
    public async Task<ActionResult<ConnectorDefinition>> UpdateConnector(
        string id,
        string connectorId,
        [FromBody] UpdateConnectorRequest request,
        CancellationToken ct)
    {
        try
        {
            var updated = await _kbService.UpdateConnectorAsync(id, connectorId, request, ct);
            return Ok(updated);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新连接器失败: {ConnectorId}", connectorId);
            return StatusCode(500, new { message = ex.Message });
        }
    }

    [HttpPost("embedding-specs")]
    public async Task<ActionResult<EmbeddingSpec>> CreateEmbeddingSpec(
        [FromBody] EmbeddingSpec spec,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(spec.ModelName))
            return BadRequest(new { message = "ModelName 不能为空" });
        var id = string.IsNullOrWhiteSpace(spec.Id) ? $"spec_{Guid.NewGuid():N}"[..16] : spec.Id.Trim();
        var target = spec with { Id = id };
        await _kbService.SaveEmbeddingSpecAsync(target, ct);
        return Ok(target);
    }

    [HttpPut("embedding-specs/{specId}")]
    public async Task<ActionResult<EmbeddingSpec>> UpdateEmbeddingSpec(
        string specId,
        [FromBody] EmbeddingSpec spec,
        CancellationToken ct)
    {
        var target = spec with { Id = specId };
        await _kbService.SaveEmbeddingSpecAsync(target, ct);
        return Ok(target);
    }

    [HttpDelete("embedding-specs/{specId}")]
    public async Task<IActionResult> DeleteEmbeddingSpec(
        string specId,
        CancellationToken ct)
    {
        var success = await _kbService.DeleteEmbeddingSpecAsync(specId, ct);
        if (!success) return NotFound(new { message = $"规格不存在: {specId}" });
        return NoContent();
    }

    // ==================== ChunkingSpec 分段规格管理 ====================
    [HttpGet("chunking-specs")]
    public async Task<ActionResult<IReadOnlyList<ChunkingSpec>>> ListChunkingSpecs(CancellationToken ct)
    {
        var list = await _kbService.ListChunkingSpecsAsync(ct);
        return Ok(list);
    }

    [HttpGet("chunking-specs/{specId}")]
    public async Task<ActionResult<ChunkingSpec>> GetChunkingSpec(string specId, CancellationToken ct)
    {
        var spec = await _kbService.GetChunkingSpecAsync(specId, ct);
        if (spec == null) return NotFound(new { message = $"分段规格不存在: {specId}" });
        return Ok(spec);
    }

    [HttpPost("chunking-specs")]
    public async Task<ActionResult<ChunkingSpec>> CreateChunkingSpec(
        [FromBody] ChunkingSpec spec,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(spec.StrategyType))
            return BadRequest(new { message = "StrategyType 不能为空" });
        var id = string.IsNullOrWhiteSpace(spec.Id) ? $"spec_chunk_{Guid.NewGuid():N}"[..18] : spec.Id.Trim();
        var target = spec with { Id = id };
        await _kbService.SaveChunkingSpecAsync(target, ct);
        return Ok(target);
    }

    [HttpPut("chunking-specs/{specId}")]
    public async Task<ActionResult<ChunkingSpec>> UpdateChunkingSpec(
        string specId,
        [FromBody] ChunkingSpec spec,
        CancellationToken ct)
    {
        var target = spec with { Id = specId };
        await _kbService.SaveChunkingSpecAsync(target, ct);
        return Ok(target);
    }

    [HttpDelete("chunking-specs/{specId}")]
    public async Task<IActionResult> DeleteChunkingSpec(
        string specId,
        CancellationToken ct)
    {
        var success = await _kbService.DeleteChunkingSpecAsync(specId, ct);
        if (!success) return NotFound(new { message = $"分段规格不存在: {specId}" });
        return NoContent();
    }

    // ==================== 配置偏离诊断与蓝绿影子重构索引 ====================
    [HttpGet("{id}/drift")]
    public async Task<ActionResult<ConfigDriftResult>> CheckConfigDrift(string id, CancellationToken ct)
    {
        try
        {
            var result = await _kbService.CheckConfigDriftAsync(id, ct);
            return Ok(result);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
    }

    [HttpPost("{id}/reindex")]
    public async Task<ActionResult<ModelReindexResult>> TriggerModelReindex(
        string id,
        [FromBody] TriggerReindexRequest? request,
        CancellationToken ct)
    {
        try
        {
            var result = await _kbService.TriggerModelReindexAsync(id, request?.TargetEmbeddingSpecId, ct);
            return Ok(result);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "知识库 {Id} 蓝绿影子重构索引失败", id);
            return StatusCode(500, new { message = ex.Message });
        }
    }

    [HttpPost("{id}/rechunk")]
    public async Task<ActionResult<RechunkResult>> TriggerRechunk(
        string id,
        [FromBody] RechunkKnowledgeBaseRequest? request,
        CancellationToken ct)
    {
        try
        {
            var result = await _kbService.TriggerRechunkAsync(id, request?.TargetChunkingSpecId, ct);
            return Ok(result);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "知识库 {Id} 重新分段执行异常", id);
            return StatusCode(500, new { message = $"重新分段执行异常: {ex.Message}" });
        }
    }

    [HttpPost("{id}/unbind-model")]
    public async Task<ActionResult<KnowledgeBaseDefinition>> UnbindModel(
        string id,
        CancellationToken ct)
    {
        try
        {
            var result = await _kbService.UnbindModelAsync(id, ct);
            return Ok(result);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "知识库 {Id} 解绑模型执行异常", id);
            return StatusCode(500, new { message = $"解绑模型执行异常: {ex.Message}" });
        }
    }

    // ==================== 文档增删改查 ====================
    [HttpGet("{id}/documents")]
    public async Task<IActionResult> GetDocuments(
        string id,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? search = null,
        [FromQuery] string? connectorId = null,
        CancellationToken ct = default)
    {
        var (docs, total) = await _kbService.GetDocumentsAsync(id, page, pageSize, search, connectorId, ct);
        return Ok(new { items = docs, total, page, pageSize });
    }

    [HttpGet("{id}/documents/{docId}")]
    public async Task<ActionResult<KnowledgeBaseDocumentRecord>> GetDocument(
        string id,
        string docId,
        CancellationToken ct)
    {
        var doc = await _kbService.GetDocumentByIdAsync(id, docId, ct);
        if (doc == null) return NotFound(new { message = $"文档不存在: {docId}" });
        return Ok(doc);
    }

    [HttpDelete("{id}/documents/{docId}")]
    public async Task<IActionResult> DeleteDocument(
        string id,
        string docId,
        CancellationToken ct)
    {
        var deleted = await _kbService.DeleteDocumentByIdAsync(id, docId, ct);
        if (!deleted) return NotFound(new { message = $"文档不存在: {docId}" });
        return NoContent();
    }

    [HttpPost("{id}/documents")]
    public async Task<ActionResult<KnowledgeBaseDocumentRecord>> CreateDocument(
        string id,
        [FromBody] CreateDocumentRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Content))
        {
            return BadRequest(new { message = "文档内容 Content 不能为空" });
        }

        var doc = await _kbService.CreateDocumentAsync(
            id,
            request.ConnectorId,
            request.Title,
            request.Content,
            request.SourceKey,
            ct);

        return Created($"/api/knowledge-bases/{id}/documents/{doc.Id}", doc);
    }

    // ==================== 切片增删改查 (HITL) ====================
    [HttpGet("{id}/chunks")]
    public async Task<IActionResult> GetChunks(
        string id,
        [FromQuery] string? documentId = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? search = null,
        [FromQuery] bool? enabledOnly = null,
        CancellationToken ct = default)
    {
        var (chunks, total) = await _kbService.GetChunksAsync(id, documentId, page, pageSize, search, enabledOnly, ct);
        return Ok(new { items = chunks, total, page, pageSize });
    }

    [HttpGet("{id}/chunks/{chunkId}")]
    public async Task<ActionResult<KnowledgeBaseChunkRecord>> GetChunk(
        string id,
        string chunkId,
        CancellationToken ct)
    {
        var chunk = await _kbService.GetChunkByIdAsync(id, chunkId, ct);
        if (chunk == null) return NotFound(new { message = $"切片不存在: {chunkId}" });
        return Ok(chunk);
    }

    [HttpPost("{id}/chunks")]
    public async Task<ActionResult<KnowledgeBaseChunkRecord>> CreateChunk(
        string id,
        [FromBody] CreateChunkRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request?.Text))
            return BadRequest(new { message = "切片文本不能为空" });
        var created = await _kbService.CreateChunkAsync(id, request, ct);
        return CreatedAtAction(nameof(GetChunk), new { id, chunkId = created.Id }, created);
    }

    [HttpPut("{id}/chunks/{chunkId}")]
    public async Task<ActionResult<KnowledgeBaseChunkRecord>> UpdateChunk(
        string id,
        string chunkId,
        [FromBody] UpdateChunkRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request?.Text))
            return BadRequest(new { message = "切片文本不能为空" });
        var updated = await _kbService.UpdateChunkAsync(id, chunkId, request, ct);
        if (updated == null) return NotFound(new { message = $"切片不存在: {chunkId}" });
        return Ok(updated);
    }

    [HttpPatch("{id}/chunks/{chunkId}/toggle")]
    public async Task<IActionResult> ToggleChunk(
        string id,
        string chunkId,
        [FromBody] ToggleChunkRequest request,
        CancellationToken ct)
    {
        var success = await _kbService.ToggleChunkAsync(id, chunkId, request.Enabled, ct);
        if (!success) return NotFound(new { message = $"切片不存在: {chunkId}" });
        return Ok(new { success = true, chunkId, enabled = request.Enabled });
    }

    [HttpDelete("{id}/chunks/{chunkId}")]
    public async Task<IActionResult> DeleteChunk(
        string id,
        string chunkId,
        CancellationToken ct)
    {
        var success = await _kbService.DeleteChunkAsync(id, chunkId, ct);
        if (!success) return NotFound(new { message = $"切片不存在: {chunkId}" });
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

/// <summary>Source names are not grants for independent knowledge-base connectors.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class RestrictKnowledgeBaseToFullSourceScopeAttribute : Attribute, IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        if (context.ActionDescriptor is ControllerActionDescriptor action &&
            action.ControllerTypeInfo.AsType() == typeof(KnowledgeBasesController) &&
            action.MethodInfo.Name == nameof(KnowledgeBasesController.ListKnowledgeBases)) return;
        var key = context.HttpContext.Items["CurrentApiKey"] as ApiKeyEntity;
        if (ApiKeyPermissionMatrix.GetEffectiveAllowedSources(key) != null)
            context.Result = new ObjectResult(new { message = "当前密钥的数据源权限尚未映射到独立知识库连接器。" }) { StatusCode = 403 };
    }
}

/// <summary>
/// 异步作业管理 API 控制器
/// </summary>
[ApiController]
[Route("api/jobs")]
[RestrictKnowledgeBaseToFullSourceScope]
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

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteJob(string id, CancellationToken ct)
    {
        var success = await _kbService.DeleteJobAsync(id, ct);
        if (!success) return NotFound(new { message = $"任务不存在: {id}" });
        return NoContent();
    }

    [HttpPost("prune")]
    public async Task<ActionResult> PruneJobs(
        [FromQuery] string? knowledgeBaseId,
        [FromQuery] int? daysOlderThan,
        CancellationToken ct)
    {
        var span = daysOlderThan.HasValue ? TimeSpan.FromDays(daysOlderThan.Value) : (TimeSpan?)null;
        var pruned = await _kbService.PruneJobsAsync(knowledgeBaseId, span, ct);
        return Ok(new { success = true, prunedCount = pruned });
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
            var key = ControllerContext.HttpContext?.Items["CurrentApiKey"] as ApiKeyEntity;
            var allowed = ApiKeyPermissionMatrix.GetEffectiveAllowedSources(key);
            request = request with { AuthorizedSourceNames = allowed };
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
    EmbeddingSpecDto? EmbeddingSpec = null,
    string? ChunkingSpecId = null,
    ChunkingSpec? ChunkingSpec = null);
public sealed record TriggerReindexRequest(string? TargetEmbeddingSpecId = null);
public sealed record AddConnectorRequest(string? Kind, string TargetPath, string? FilePattern = null);
public sealed record TriggerJobRequest(string ConnectorId);

public sealed record ToggleKnowledgeBaseRequest(bool Enabled);
public sealed record ToggleChunkRequest(bool Enabled);
public sealed record CreateDocumentRequest(string? ConnectorId, string? Title, string Content, string? SourceKey);
public sealed record GetKnowledgeBasePathsRequest(IReadOnlyList<string>? KnowledgeBaseIds);
