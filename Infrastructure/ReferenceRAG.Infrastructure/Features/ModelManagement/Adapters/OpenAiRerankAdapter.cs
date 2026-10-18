using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Core.Services.Rerank;

namespace ReferenceRAG.Core.Features.ModelManagement.Adapters;

/// <summary>
/// 基于远程 OpenAI 兼容 REST API 的重排适配器
/// </summary>
public sealed class OpenAiRerankAdapter : IRerankBackendAdapter
{
    private readonly RerankConfig _config;
    private OpenAIRerankService? _innerService;
    private bool _disposed;

    public string BackendType => "openai";
    public string ModelPath => _config.ApiBaseUrl ?? "";
    public string ModelName => _config.ModelName;
    public bool IsLoaded => _innerService?.IsLoaded ?? false;

    public OpenAiRerankAdapter(RerankConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _innerService = new OpenAIRerankService(_config);
        return Task.CompletedTask;
    }

    public async Task<double> RerankAsync(string query, string document, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_innerService == null) throw new InvalidOperationException("OpenAI 重排引擎尚未初始化");

        var batchResult = await _innerService.RerankBatchAsync(query, new[] { document }, cancellationToken);
        return batchResult.Documents.FirstOrDefault()?.RelevanceScore ?? 0.0;
    }

    public Task<RerankResult> RerankBatchAsync(string query, IEnumerable<string> documents, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_innerService == null) throw new InvalidOperationException("OpenAI 重排引擎尚未初始化");

        return _innerService.RerankBatchAsync(query, documents, cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _innerService?.Dispose();
        _innerService = null;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
