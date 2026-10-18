using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;

namespace ReferenceRAG.Core.Features.ModelManagement.Adapters;

/// <summary>
/// 基于 ONNX Runtime 的本地嵌入模型适配器
/// </summary>
public sealed class OnnxEmbeddingAdapter : IEmbeddingBackendAdapter
{
    private readonly EmbeddingOptions _options;
    private readonly IGpuMemoryManager? _memoryManager;
    private EmbeddingService? _innerService;
    private bool _disposed;

    public string BackendType => "onnx";
    public string ModelPath => _options.ModelPath;
    public string ModelName => _options.ModelName;
    public int Dimension => _innerService?.Dimension ?? 0;
    public int MaxSequenceLength => _options.MaxSequenceLength;
    public bool SupportsAsymmetricEncoding => _innerService?.SupportsAsymmetricEncoding ?? (_options.AsymmetricEncoding != null);
    public bool IsSimulationMode => _innerService?.IsSimulationMode ?? false;
    public EmbeddingOptions Options => _options;

    public OnnxEmbeddingAdapter(EmbeddingOptions options, IGpuMemoryManager? memoryManager = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _memoryManager = memoryManager;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _innerService = new EmbeddingService(_options, _memoryManager);
        return Task.CompletedTask;
    }

    public Task<float[]> EncodeAsync(string text, string? prefix = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_innerService == null) throw new InvalidOperationException("ONNX 引擎尚未初始化");

        if (!string.IsNullOrEmpty(prefix))
        {
            return _innerService.EncodeAsync($"{prefix}{text}", cancellationToken);
        }

        return _innerService.EncodeAsync(text, cancellationToken);
    }

    public Task<float[][]> EncodeBatchAsync(IReadOnlyList<string> texts, string? prefix = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_innerService == null) throw new InvalidOperationException("ONNX 引擎尚未初始化");

        if (!string.IsNullOrEmpty(prefix))
        {
            var prefixedTexts = texts.Select(t => $"{prefix}{t}").ToList();
            return _innerService.EncodeBatchAsync(prefixedTexts, cancellationToken);
        }

        return _innerService.EncodeBatchAsync(texts, cancellationToken);
    }

    public int CountTokens(string text)
    {
        return Math.Max(1, text.Length / 4);
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
