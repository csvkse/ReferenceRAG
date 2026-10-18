using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services.Rerank;

namespace ReferenceRAG.Core.Features.ModelManagement.Adapters;

/// <summary>
/// 基于 ONNX Runtime 的重排模型适配器
/// </summary>
public sealed class OnnxRerankAdapter : IRerankBackendAdapter
{
    private readonly RerankOptions _options;
    private readonly IGpuMemoryManager? _memoryManager;
    private OnnxRerankService? _innerService;
    private bool _disposed;

    public string BackendType => "onnx";
    public string ModelPath => _options.ModelPath;
    public string ModelName => _options.ModelName;
    public bool IsLoaded => _innerService?.IsLoaded ?? false;

    public OnnxRerankAdapter(RerankOptions options, IGpuMemoryManager? memoryManager = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _memoryManager = memoryManager;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _innerService = new OnnxRerankService(_options, _memoryManager);
        return Task.CompletedTask;
    }

    public Task<double> RerankAsync(string query, string document, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_innerService == null) throw new InvalidOperationException("ONNX 重排引擎尚未初始化");

        return _innerService.RerankAsync(query, document, cancellationToken);
    }

    public Task<RerankResult> RerankBatchAsync(string query, IEnumerable<string> documents, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_innerService == null) throw new InvalidOperationException("ONNX 重排引擎尚未初始化");

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
