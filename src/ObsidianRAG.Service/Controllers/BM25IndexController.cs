using Microsoft.AspNetCore.Mvc;
using ObsidianRAG.Core.Interfaces;
using ObsidianRAG.Core.Services;

namespace ObsidianRAG.Service.Controllers;

/// <summary>
/// BM25 索引管理 API - 管理倒排索引和搜索
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class BM25IndexController : ControllerBase
{
    private readonly IBM25Store _bm25Store;
    private readonly IVectorStore _vectorStore;
    private readonly ConfigManager _configManager;
    private readonly ILogger<BM25IndexController> _logger;

    public BM25IndexController(
        IBM25Store bm25Store,
        IVectorStore vectorStore,
        ConfigManager configManager,
        ILogger<BM25IndexController> logger)
    {
        _bm25Store = bm25Store;
        _vectorStore = vectorStore;
        _configManager = configManager;
        _logger = logger;
    }

    // ==================== Provider 管理 ====================

    /// <summary>
    /// 获取当前 BM25 Provider
    /// </summary>
    [HttpGet("provider")]
    public ActionResult<BM25ProviderInfo> GetProvider()
    {
        var config = _configManager.Load();
        var currentProvider = config.Search?.BM25Provider ?? "fts5";

        // 判断实际运行的 provider 类型
        var actualProvider = _bm25Store.GetType().Name.Contains("Fts5") ? "fts5" : "legacy";

        return Ok(new BM25ProviderInfo
        {
            ConfiguredProvider = currentProvider,
            ActiveProvider = actualProvider,
            IsMatch = currentProvider.Equals(actualProvider, StringComparison.OrdinalIgnoreCase),
            Description = currentProvider == "fts5"
                ? "FTS5: 使用 SQLite FTS5 内置 BM25（推荐，性能更好）"
                : "Legacy: 使用手动倒排索引实现（备用）"
        });
    }

    /// <summary>
    /// 设置 BM25 Provider（仅修改配置，需重启服务生效）
    /// </summary>
    [HttpPost("provider")]
    public ActionResult SetProvider([FromBody] SetProviderRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Provider))
        {
            return BadRequest(new { error = "Provider 不能为空" });
        }

        var provider = request.Provider.ToLowerInvariant();
        if (provider != "fts5" && provider != "legacy")
        {
            return BadRequest(new { error = "Provider 必须是 'fts5' 或 'legacy'" });
        }

        var config = _configManager.Load();

        if (config.Search == null)
        {
            config.Search = new ObsidianRAG.Core.Models.SearchConfig();
        }

        var oldProvider = config.Search.BM25Provider ?? "fts5";
        config.Search.BM25Provider = provider;

        _configManager.Save(config);

        _logger.LogInformation("BM25 Provider 已修改: {OldProvider} -> {NewProvider}，请重启服务使更改生效", oldProvider, provider);

        return Ok(new
        {
            message = $"Provider 已修改为 '{provider}'，请重启服务使更改生效",
            provider = provider,
            requiresRestart = true
        });
    }

    // ==================== 模型管理 ====================

    /// <summary>
    /// 创建 BM25 模型
    /// </summary>
    [HttpPost("models")]
    public async Task<ActionResult<BM25ModelResponse>> CreateModel([FromBody] CreateBM25ModelRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new { error = "模型名称不能为空" });
        }

        if (_bm25Store.ModelExists(request.Name))
        {
            return Conflict(new { error = $"模型 '{request.Name}' 已存在" });
        }

        _logger.LogInformation("创建 BM25 模型: {ModelName}", request.Name);

        var model = await _bm25Store.CreateModelAsync(request.Name);

        return Created($"/api/bm25index/models/{model.Name}", new BM25ModelResponse
        {
            Name = model.Name,
            AverageDocLength = model.AverageDocLength,
            TotalDocuments = model.TotalDocuments,
            VocabularySize = model.VocabularySize,
            IsEnabled = model.IsEnabled,
            CreatedAt = model.CreatedAt,
            Message = $"模型 '{model.Name}' 创建成功"
        });
    }

    /// <summary>
    /// 获取所有 BM25 模型
    /// </summary>
    [HttpGet("models")]
    public async Task<ActionResult<List<BM25ModelResponse>>> GetAllModels()
    {
        var models = await _bm25Store.GetAllModelsAsync();

        return Ok(models.Select(m => new BM25ModelResponse
        {
            Name = m.Name,
            AverageDocLength = m.AverageDocLength,
            TotalDocuments = m.TotalDocuments,
            VocabularySize = m.VocabularySize,
            IsEnabled = m.IsEnabled,
            CreatedAt = m.CreatedAt
        }).ToList());
    }

    /// <summary>
    /// 获取指定模型信息
    /// </summary>
    [HttpGet("models/{modelName}")]
    public async Task<ActionResult<BM25ModelResponse>> GetModel(string modelName)
    {
        var model = await _bm25Store.GetModelInfoAsync(modelName);
        if (model == null)
        {
            return NotFound(new { error = $"模型 '{modelName}' 不存在" });
        }

        return Ok(new BM25ModelResponse
        {
            Name = model.Name,
            AverageDocLength = model.AverageDocLength,
            TotalDocuments = model.TotalDocuments,
            VocabularySize = model.VocabularySize,
            IsEnabled = model.IsEnabled,
            CreatedAt = model.CreatedAt
        });
    }

    /// <summary>
    /// 删除 BM25 模型
    /// </summary>
    [HttpDelete("models/{modelName}")]
    public async Task<ActionResult> DeleteModel(string modelName)
    {
        if (!_bm25Store.ModelExists(modelName))
        {
            return NotFound(new { error = $"模型 '{modelName}' 不存在" });
        }

        _logger.LogInformation("删除 BM25 模型: {ModelName}", modelName);

        await _bm25Store.DeleteModelAsync(modelName);

        return Ok(new { message = $"模型 '{modelName}' 已删除" });
    }

    /// <summary>
    /// 启用模型
    /// </summary>
    [HttpPost("models/{modelName}/enable")]
    public async Task<ActionResult> EnableModel(string modelName)
    {
        if (!_bm25Store.ModelExists(modelName))
        {
            return NotFound(new { error = $"模型 '{modelName}' 不存在" });
        }

        await _bm25Store.EnableModelAsync(modelName);
        _logger.LogInformation("启用 BM25 模型: {ModelName}", modelName);

        return Ok(new { message = $"模型 '{modelName}' 已启用" });
    }

    /// <summary>
    /// 禁用模型
    /// </summary>
    [HttpPost("models/{modelName}/disable")]
    public async Task<ActionResult> DisableModel(string modelName)
    {
        if (!_bm25Store.ModelExists(modelName))
        {
            return NotFound(new { error = $"模型 '{modelName}' 不存在" });
        }

        await _bm25Store.DisableModelAsync(modelName);
        _logger.LogInformation("禁用 BM25 模型: {ModelName}", modelName);

        return Ok(new { message = $"模型 '{modelName}' 已禁用" });
    }

    // ==================== 索引操作 ====================

    /// <summary>
    /// 索引指定模型的所有文档
    /// </summary>
    [HttpPost("models/{modelName}/index")]
    public async Task<ActionResult<IndexProgressResponse>> IndexModelDocuments(string modelName, [FromBody] IndexModelRequest? request = null)
    {
        if (!_bm25Store.ModelExists(modelName))
        {
            return NotFound(new { error = $"模型 '{modelName}' 不存在" });
        }

        _logger.LogInformation("开始索引模型 {ModelName} 的所有文档", modelName);

        // 获取所有文档
        var files = await _vectorStore.GetAllFilesAsync();
        var chunksList = new List<(string chunkId, string content)>();

        foreach (var file in files)
        {
            var chunks = await _vectorStore.GetChunksByFileAsync(file.Id);
            foreach (var chunk in chunks)
            {
                if (!string.IsNullOrWhiteSpace(chunk.Content))
                {
                    chunksList.Add((chunk.Id, chunk.Content));
                }
            }
        }

        if (chunksList.Count == 0)
        {
            return Ok(new IndexProgressResponse
            {
                ModelName = modelName,
                TotalDocuments = 0,
                ProcessedDocuments = 0,
                ProgressPercent = 100,
                Message = "没有文档需要索引"
            });
        }

        var progress = new Progress<int>(p =>
        {
            // 进度报告
        });

        await _bm25Store.IndexBatchAsync(modelName, chunksList, progress);

        var modelInfo = await _bm25Store.GetModelInfoAsync(modelName);

        return Ok(new IndexProgressResponse
        {
            ModelName = modelName,
            TotalDocuments = chunksList.Count,
            ProcessedDocuments = chunksList.Count,
            ProgressPercent = 100,
            TotalTerms = modelInfo?.VocabularySize ?? 0,
            Message = $"已索引 {chunksList.Count} 个文档"
        });
    }

    /// <summary>
    /// 索引单个文档
    /// </summary>
    [HttpPost("models/{modelName}/documents/{chunkId}")]
    public async Task<ActionResult> IndexDocument(string modelName, string chunkId, [FromBody] IndexDocumentRequest request)
    {
        if (!_bm25Store.ModelExists(modelName))
        {
            return NotFound(new { error = $"模型 '{modelName}' 不存在" });
        }

        await _bm25Store.IndexDocumentAsync(modelName, chunkId, request.Content);

        return Ok(new { message = $"文档 {chunkId} 已索引到模型 {modelName}" });
    }

    /// <summary>
    /// 清空模型的索引
    /// </summary>
    [HttpDelete("models/{modelName}/index")]
    public async Task<ActionResult> ClearModelIndex(string modelName)
    {
        if (!_bm25Store.ModelExists(modelName))
        {
            return NotFound(new { error = $"模型 '{modelName}' 不存在" });
        }

        _logger.LogInformation("清空 BM25 模型 {ModelName} 的索引", modelName);

        await _bm25Store.ClearModelAsync(modelName);

        return Ok(new { message = $"模型 '{modelName}' 的索引已清空" });
    }

    /// <summary>
    /// 重建模型索引
    /// </summary>
    [HttpPost("models/{modelName}/rebuild")]
    public async Task<ActionResult<IndexProgressResponse>> RebuildModelIndex(string modelName)
    {
        if (!_bm25Store.ModelExists(modelName))
        {
            return NotFound(new { error = $"模型 '{modelName}' 不存在" });
        }

        _logger.LogInformation("重建 BM25 模型 {ModelName} 的索引", modelName);

        var progress = new Progress<int>();
        await _bm25Store.RebuildFullIndexAsync(modelName, progress);

        var modelInfo = await _bm25Store.GetModelInfoAsync(modelName);

        return Ok(new IndexProgressResponse
        {
            ModelName = modelName,
            TotalDocuments = modelInfo?.TotalDocuments ?? 0,
            ProcessedDocuments = modelInfo?.TotalDocuments ?? 0,
            ProgressPercent = 100,
            TotalTerms = modelInfo?.VocabularySize ?? 0,
            Message = "索引重建完成"
        });
    }

    /// <summary>
    /// 测试端点：从向量库获取所有文档并索引到 BM25（用于性能测试）
    /// </summary>
    [HttpPost("models/{modelName}/reindex-all")]
    public async Task<ActionResult<IndexProgressResponse>> ReindexAllDocuments(string modelName)
    {
        if (!_bm25Store.ModelExists(modelName))
        {
            return NotFound(new { error = $"模型 '{modelName}' 不存在" });
        }

        _logger.LogInformation("开始全量索引 BM25 模型 {ModelName} 的所有文档", modelName);

        // 获取所有文件
        var files = await _vectorStore.GetAllFilesAsync();
        var fileList = files.ToList();

        _logger.LogInformation("找到 {FileCount} 个文件", fileList.Count);

        var documentsToIndex = new List<(string ChunkId, string Content)>();

        foreach (var file in fileList)
        {
            var chunks = await _vectorStore.GetChunksByFileAsync(file.Id);
            foreach (var chunk in chunks)
            {
                if (!string.IsNullOrEmpty(chunk.Content))
                {
                    documentsToIndex.Add((chunk.Id, chunk.Content));
                }
            }
        }

        _logger.LogInformation("准备索引 {DocCount} 个文档", documentsToIndex.Count);

        if (documentsToIndex.Count == 0)
        {
            return Ok(new IndexProgressResponse
            {
                ModelName = modelName,
                TotalDocuments = 0,
                ProcessedDocuments = 0,
                ProgressPercent = 100,
                Message = "没有文档需要索引"
            });
        }

        var progress = new Progress<int>();
        var startTime = DateTime.UtcNow;

        await _bm25Store.IndexBatchAsync(modelName, documentsToIndex, progress);

        var duration = DateTime.UtcNow - startTime;
        var modelInfo = await _bm25Store.GetModelInfoAsync(modelName);

        _logger.LogInformation("BM25 全量索引完成，{DocCount} 个文档，耗时 {Duration}ms",
            documentsToIndex.Count, duration.TotalMilliseconds);

        return Ok(new IndexProgressResponse
        {
            ModelName = modelName,
            TotalDocuments = documentsToIndex.Count,
            ProcessedDocuments = documentsToIndex.Count,
            ProgressPercent = 100,
            TotalTerms = modelInfo?.VocabularySize ?? 0,
            Message = $"索引完成，{documentsToIndex.Count} 个文档，耗时 {duration.TotalMilliseconds:F0}ms"
        });
    }

    // ==================== 搜索操作 ====================

    /// <summary>
    /// BM25 搜索
    /// </summary>
    [HttpGet("models/{modelName}/search")]
    public async Task<ActionResult<BM25SearchResponse>> Search(
        string modelName,
        [FromQuery] string query,
        [FromQuery] int topK = 10)
    {
        if (!_bm25Store.ModelExists(modelName))
        {
            return NotFound(new { error = $"模型 '{modelName}' 不存在" });
        }

        if (!_bm25Store.IsModelEnabled(modelName))
        {
            return BadRequest(new { error = $"模型 '{modelName}' 已禁用" });
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return BadRequest(new { error = "查询语句不能为空" });
        }

        var startTime = DateTime.UtcNow;
        var results = await _bm25Store.SearchAsync(modelName, query, topK);
        var durationMs = (int)(DateTime.UtcNow - startTime).TotalMilliseconds;

        return Ok(new BM25SearchResponse
        {
            ModelName = modelName,
            Query = query,
            TotalResults = results.Count,
            DurationMs = durationMs,
            Results = results.Select((r, index) => new BM25SearchResultItem
            {
                ChunkId = r.ChunkId,
                Content = r.Content,
                Score = r.Score,
                Rank = index + 1
            }).ToList()
        });
    }

    // ==================== 统计信息 ====================

    /// <summary>
    /// 获取 BM25 统计摘要
    /// </summary>
    [HttpGet("summary")]
    public async Task<ActionResult<BM25Summary>> GetSummary()
    {
        var models = await _bm25Store.GetAllModelsAsync();
        var files = await _vectorStore.GetAllFilesAsync();
        var fileList = files.ToList();

        int totalIndexed = 0;
        int totalVocabulary = 0;
        int enabledCount = 0;

        foreach (var m in models)
        {
            totalIndexed += m.TotalDocuments;
            totalVocabulary += m.VocabularySize;
            if (m.IsEnabled) enabledCount++;
        }

        return Ok(new BM25Summary
        {
            TotalModels = models.Count,
            EnabledModels = enabledCount,
            TotalIndexedDocuments = totalIndexed,
            TotalVocabularySize = totalVocabulary,
            TotalFiles = fileList.Count,
            TotalChunks = fileList.Sum(f => f.ChunkCount),
            Models = models.Select(m => new BM25ModelStat
            {
                Name = m.Name,
                TotalDocuments = m.TotalDocuments,
                VocabularySize = m.VocabularySize,
                IsEnabled = m.IsEnabled
            }).ToList()
        });
    }
}

// ==================== 请求模型 ====================

public class CreateBM25ModelRequest
{
    public string Name { get; set; } = string.Empty;
}

public class IndexModelRequest
{
    public List<string>? Sources { get; set; }
    public bool Force { get; set; } = false;
}

public class IndexDocumentRequest
{
    public string Content { get; set; } = string.Empty;
}

// ==================== 响应模型 ====================

public class BM25ModelResponse
{
    public string Name { get; set; } = string.Empty;
    public double AverageDocLength { get; set; }
    public int TotalDocuments { get; set; }
    public int VocabularySize { get; set; }
    public bool IsEnabled { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? Message { get; set; }
}

public class IndexProgressResponse
{
    public string ModelName { get; set; } = string.Empty;
    public int TotalDocuments { get; set; }
    public int ProcessedDocuments { get; set; }
    public int ProgressPercent { get; set; }
    public int TotalTerms { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class BM25SearchResponse
{
    public string ModelName { get; set; } = string.Empty;
    public string Query { get; set; } = string.Empty;
    public int TotalResults { get; set; }
    public int DurationMs { get; set; }
    public List<BM25SearchResultItem> Results { get; set; } = new();
}

public class BM25SearchResultItem
{
    public string ChunkId { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public double Score { get; set; }
    public int Rank { get; set; }
}

public class BM25Summary
{
    public int TotalModels { get; set; }
    public int EnabledModels { get; set; }
    public int TotalIndexedDocuments { get; set; }
    public int TotalVocabularySize { get; set; }
    public int TotalFiles { get; set; }
    public int TotalChunks { get; set; }
    public List<BM25ModelStat> Models { get; set; } = new();
}

public class BM25ModelStat
{
    public string Name { get; set; } = string.Empty;
    public int TotalDocuments { get; set; }
    public int VocabularySize { get; set; }
    public bool IsEnabled { get; set; }
}

public class BM25ProviderInfo
{
    public string ConfiguredProvider { get; set; } = "fts5";
    public string ActiveProvider { get; set; } = "fts5";
    public bool IsMatch { get; set; }
    public string Description { get; set; } = "";
}

public class SetProviderRequest
{
    public string Provider { get; set; } = "fts5";
}
