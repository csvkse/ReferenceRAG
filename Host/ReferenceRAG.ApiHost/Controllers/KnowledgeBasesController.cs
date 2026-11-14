using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;
using ReferenceRAG.Business.Features.KnowledgeBase.Services;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
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
    private readonly ISourceLinkEventPublisher? _eventPublisher;
    private readonly IReconciliationState? _reconciliationState;
    private readonly ConfigManager? _configManager;
    private readonly IFileMonitorService? _fileMonitor;
    private readonly IKnowledgeBaseStorageManager? _storageManager;

    public KnowledgeBasesController(
        IKnowledgeBaseService kbService,
        ILogger<KnowledgeBasesController> logger,
        ISourceLinkEventPublisher? eventPublisher = null,
        IReconciliationState? reconciliationState = null,
        ConfigManager? configManager = null,
        IFileMonitorService? fileMonitor = null,
        IKnowledgeBaseStorageManager? storageManager = null)
    {
        _kbService = kbService ?? throw new ArgumentNullException(nameof(kbService));
        _logger = logger;
        _eventPublisher = eventPublisher;
        _reconciliationState = reconciliationState;
        _configManager = configManager;
        _fileMonitor = fileMonitor;
        _storageManager = storageManager;
    }

    private void Publish(SourceLinkEventKind kind, string path, string? kbId = null,
        string? connectorId = null, string? detail = null)
    {
        _eventPublisher?.Publish(new SourceLinkEvent
        {
            Kind = kind,
            Path = path,
            KnowledgeBaseId = kbId,
            ConnectorId = connectorId,
            Detail = detail
        });
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
            Publish(SourceLinkEventKind.SourceLinked, connector.TargetPath, id, connector.Id, "add-connector");
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
        var connector = await _kbService.GetConnectorAsync(connectorId, ct);
        var success = await _kbService.DeleteConnectorAsync(id, connectorId, cascade, ct);
        if (!success) return NotFound(new { message = $"连接器不存在: {connectorId}" });
        Publish(SourceLinkEventKind.ConnectorDetached, connector?.TargetPath ?? string.Empty,
            id, connectorId, $"delete-connector:cascade={cascade}");
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

    // ==================== 词法倒排索引状态与手动补齐 ====================

    /// <summary>
    /// 查询词法倒排索引状态（O(1) 裁决，不触发任何构建）。
    /// 返回 Absent/Stale 表示当前读路径已降级为关键词扫描，后台 Worker 正在补齐。
    /// </summary>
    [HttpGet("{id}/lexical-index")]
    public async Task<ActionResult<LexicalIndexStatusInfo>> GetLexicalIndexStatus(string id, CancellationToken ct)
    {
        if (_storageManager == null)
            return StatusCode(503, new { message = "知识库存储未就绪" });
        try
        {
            await using var lease = await _storageManager.AcquireLeaseAsync(id, StorageAccess.ReadOnly, ct);
            return Ok(await lease.Store.GetLexicalIndexStatusAsync(ct));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
    }

    /// <summary>
    /// 手动触发词法索引补齐（运维/验收用）。分批构建 + 检查点续传，可重复调用（幂等）。
    /// <paramref name="rebuild"/>=true 时先清空词法表再全量重建，用于清理历史重复行或强制重分词。
    /// </summary>
    [HttpPost("{id}/lexical-index/backfill")]
    public async Task<ActionResult<LexicalIndexBackfillResult>> BackfillLexicalIndex(
        string id,
        [FromQuery] int batchSize = 500,
        [FromQuery] bool rebuild = false,
        CancellationToken ct = default)
    {
        if (_storageManager == null)
            return StatusCode(503, new { message = "知识库存储未就绪" });
        try
        {
            await using var lease = await _storageManager.AcquireLeaseAsync(id, StorageAccess.ReadWrite, ct);
            var result = await lease.Store.BackfillLexicalIndexAsync(
                Math.Clamp(batchSize, 50, 5000), null, ct, force: rebuild);
            return Ok(result);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "知识库 {Id} 词法索引补齐失败", id);
            return StatusCode(500, new { message = ex.Message });
        }
    }

    /// <summary>
    /// 清理词法索引中的重复行/孤儿行（运维用）。FTS5 无唯一约束，历史并发补齐会留下重复行，
    /// 重复行会让倒排表与 df 虚高、bm25 打分失真、查询耗时成倍放大。
    /// </summary>
    [HttpPost("{id}/lexical-index/repair")]
    public async Task<ActionResult<object>> RepairLexicalIndex(string id, CancellationToken ct)
    {
        if (_storageManager == null)
            return StatusCode(503, new { message = "知识库存储未就绪" });
        try
        {
            await using var lease = await _storageManager.AcquireLeaseAsync(id, StorageAccess.ReadWrite, ct);
            var removed = await lease.Store.RepairLexicalIndexAsync(ct);
            // 去重之后顺手合并 FTS5 段：段碎片化会让"词不存在"的查询从 4ms 涨到 230ms
            // （每个 segment 都要各查一次 term 索引）。运维一次调用把索引修干净。
            var optimized = await lease.Store.OptimizeLexicalIndexAsync(ct);
            return Ok(new { success = true, removedRows = removed, optimized });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "知识库 {Id} 词法索引去重失败", id);
            return StatusCode(500, new { message = ex.Message });
        }
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

    /// <summary>
    /// 蓝绿影子重构索引（作业化）：校验 + 单飞占位后立即 202 返回 Queued 作业，
    /// 实际编码在独立后台任务执行，与 HTTP 请求生命周期解耦（根治 60s 超时 + RequestAborted 取消）。
    /// </summary>
    [HttpPost("{id}/reindex")]
    public async Task<ActionResult<JobDefinition>> TriggerModelReindex(
        string id,
        [FromBody] TriggerReindexRequest? request,
        CancellationToken ct)
    {
        try
        {
            var job = await _kbService.TriggerModelReindexJobAsync(id, request?.TargetEmbeddingSpecId, ct);
            return Accepted($"/api/jobs/{job.Id}", job);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (KnowledgeBaseJobConflictException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "知识库 {Id} 蓝绿影子重构索引入队失败", id);
            return StatusCode(500, new { message = ex.Message });
        }
    }

    /// <summary>重构作业面板状态：活跃作业 + 向量代次/影子/旧代表信息（前端进度抽屉轮询）。</summary>
    [HttpGet("{id}/reindex/status")]
    public async Task<ActionResult<ReindexJobStatus>> GetReindexStatus(string id, CancellationToken ct)
    {
        try
        {
            var status = await _kbService.GetReindexStatusAsync(id, ct);
            return Ok(status);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
    }

    /// <summary>回滚到上一代向量索引（保留的 chunk_vectors_old 存在时可用）。</summary>
    [HttpPost("{id}/reindex/rollback")]
    public async Task<ActionResult<KnowledgeBaseDefinition>> RollbackModelReindex(string id, CancellationToken ct)
    {
        try
        {
            var kb = await _kbService.RollbackModelReindexAsync(id, ct);
            return Ok(kb);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (KnowledgeBaseJobConflictException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "知识库 {Id} 向量代次回滚失败", id);
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

    // ==================== P2：挂载漂移对账（remaining-plan §1.2 / §1.3） ====================

    /// <summary>手动触发一次对账（首版只读，只产出报告，不修改任何数据）。</summary>
    [HttpPost("reconcile")]
    public async Task<ActionResult<SourceLinkReconcileReport>> Reconcile(CancellationToken ct)
    {
        var report = await _kbService.ReconcileAsync(ct);
        _reconciliationState?.Set(report);
        return Ok(report);
    }

    /// <summary>读取定时作业最近一次对账结果；尚未运行时返回 null。</summary>
    [HttpGet("reconcile/latest")]
    public ActionResult<SourceLinkReconcileReport?> GetLatestReconcile()
        => Ok(_reconciliationState?.Latest);

    /// <summary>
    /// 确认重绑：把漂移连接器指向用户选定的新目录，保留 connectorId 与已索引语料。
    /// 仅更新 target_path / path_key；目标被占用时返回 409，不自动 detach/attach。
    /// </summary>
    [HttpPost("reconcile/rebind")]
    public async Task<ActionResult> RebindConnector(
        [FromBody] RebindConnectorRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request?.KnowledgeBaseId) ||
            string.IsNullOrWhiteSpace(request?.ConnectorId) ||
            string.IsNullOrWhiteSpace(request?.TargetPath))
        {
            return BadRequest(new { message = "knowledgeBaseId / connectorId / targetPath 均不能为空" });
        }
        try
        {
            // 重绑前先取旧路径：源（源管理投影）需随之迁移，否则旧路径残留 Unlinked、
            // 新路径又多出一条「仅知识库」行。
            var before = await _kbService.GetConnectorAsync(request.ConnectorId, ct);
            var oldPath = before?.TargetPath;

            var updated = await _kbService.RebindConnectorAsync(
                request.KnowledgeBaseId, request.ConnectorId, request.TargetPath, ct);
            Publish(SourceLinkEventKind.SourceLinked, updated.TargetPath,
                request.KnowledgeBaseId, updated.Id, "rebind");

            var migratedSource = string.IsNullOrWhiteSpace(oldPath) || oldPath == updated.TargetPath
                ? null
                : _configManager?.RenameSourcePath(oldPath, updated.TargetPath);
            if (migratedSource != null)
            {
                // 文件监控按源名注册，路径变化后需重新挂载监控
                _fileMonitor?.RemoveSource(migratedSource.Name);
                _fileMonitor?.AddSource(migratedSource.Path, migratedSource.Name, migratedSource.FilePatterns);
                _logger.LogInformation("[KnowledgeBases] 重绑已同步迁移源投影: {Old} -> {New}",
                    oldPath, updated.TargetPath);
            }

            return Ok(new
            {
                status = "rebound",
                connectorId = updated.Id,
                targetPath = updated.TargetPath,
                sourceMigrated = migratedSource != null,
                sourceName = migratedSource?.Name
            });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message, code = "ConnectorPathConflict" });
        }
    }

    /// <summary>
    /// 一键清理孤儿连接器。默认 purgeData=false 只删 catalog 记录（保守），
    /// 显式传 true 才调用 PurgeConnectorDataAsync 级联清空该连接器的文档/切片/索引。
    /// </summary>
    [HttpPost("reconcile/cleanup")]
    public async Task<ActionResult> CleanupOrphanConnector(
        [FromBody] CleanupOrphanRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request?.KnowledgeBaseId) ||
            string.IsNullOrWhiteSpace(request?.ConnectorId))
        {
            return BadRequest(new { message = "knowledgeBaseId / connectorId 均不能为空" });
        }
        var connector = await _kbService.GetConnectorAsync(request.ConnectorId, ct);
        var success = await _kbService.DeleteConnectorAsync(
            request.KnowledgeBaseId, request.ConnectorId, request.PurgeData, ct);
        if (!success) return NotFound(new { message = $"连接器不存在: {request.ConnectorId}" });

        Publish(SourceLinkEventKind.ConnectorDetached, connector?.TargetPath ?? string.Empty,
            request.KnowledgeBaseId, request.ConnectorId, $"orphan-cleanup:purgeData={request.PurgeData}");

        // 孤儿连接器清理后，若该路径已无连接器引用且物理目录仍不存在，
        // 说明它是一条彻底失效的源投影，一并移除，避免列表里留下「未挂载」僵尸行。
        var targetPath = connector?.TargetPath;
        var sourceRemoved = false;
        if (!string.IsNullOrWhiteSpace(targetPath) && !Directory.Exists(targetPath) && _configManager != null)
        {
            var stillReferenced = false;
            foreach (var kb in await _kbService.ListKnowledgeBasesAsync(ct))
            {
                var connectors = await _kbService.GetConnectorsAsync(kb.Id, ct);
                if (connectors.Any(c => string.Equals(c.TargetPath, targetPath, StringComparison.OrdinalIgnoreCase)))
                {
                    stillReferenced = true;
                    break;
                }
            }
            if (!stillReferenced)
            {
                var pendingName = _configManager.Load().Sources
                    .FirstOrDefault(s => string.Equals(s.Path, targetPath, StringComparison.OrdinalIgnoreCase))?.Name;
                sourceRemoved = _configManager.RemoveSource(targetPath);
                if (sourceRemoved && !string.IsNullOrWhiteSpace(pendingName))
                {
                    _fileMonitor?.RemoveSource(pendingName);
                    Publish(SourceLinkEventKind.SourceRemoved, targetPath, request.KnowledgeBaseId,
                        request.ConnectorId, "orphan-cleanup:remove-projection");
                }
            }
        }

        return Ok(new
        {
            status = "cleaned",
            connectorId = request.ConnectorId,
            purgeData = request.PurgeData,
            sourceRemoved
        });
    }

    /// <summary>最近关联事件（可观测性：SourceLinked / ConnectorDetached / SourceRemoved）。</summary>
    [HttpGet("link-events")]
    public ActionResult<IReadOnlyList<SourceLinkEvent>> GetLinkEvents([FromQuery] int take = 50)
        => Ok(_eventPublisher?.Recent(take) ?? (IReadOnlyList<SourceLinkEvent>)Array.Empty<SourceLinkEvent>());
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

/// <summary>确认重绑请求（路径迁移治理）。</summary>
public sealed record RebindConnectorRequest(
    string? KnowledgeBaseId,
    string? ConnectorId,
    string? TargetPath);

/// <summary>孤儿连接器清理请求；PurgeData 默认 false，避免误删语料。</summary>
public sealed record CleanupOrphanRequest(
    string? KnowledgeBaseId,
    string? ConnectorId,
    bool PurgeData = false);
