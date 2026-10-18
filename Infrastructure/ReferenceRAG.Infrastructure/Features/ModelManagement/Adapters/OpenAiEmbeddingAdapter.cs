using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;

namespace ReferenceRAG.Core.Features.ModelManagement.Adapters;

/// <summary>
/// 基于远程 OpenAI 兼容 REST API 的嵌入适配器（Ollama / vLLM / TEI 等）
/// </summary>
public sealed class OpenAiEmbeddingAdapter : IEmbeddingBackendAdapter
{
    private readonly EmbeddingConfig _config;
    private OpenAIEmbeddingService? _innerService;
    private bool _disposed;

    public string BackendType => "openai";
    public string ModelPath => _config.ApiBaseUrl ?? "";
    public string ModelName => _config.ModelName;
    public int Dimension => _innerService != null && _innerService.Dimension > 0 ? _innerService.Dimension : (_config.ApiDimension ?? 0);
    public int MaxSequenceLength => _config.MaxSequenceLength > 0 ? _config.MaxSequenceLength : 8192;
    public bool SupportsAsymmetricEncoding => false;
    public bool IsSimulationMode => _innerService?.IsSimulationMode ?? false;

    public OpenAiEmbeddingAdapter(EmbeddingConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _innerService = new OpenAIEmbeddingService(_config);
        return Task.CompletedTask;
    }

    public Task<float[]> EncodeAsync(string text, string? prefix = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_innerService == null) throw new InvalidOperationException("OpenAI 引擎尚未初始化");

        var finalText = string.IsNullOrEmpty(prefix) ? text : $"{prefix}{text}";
        return _innerService.EncodeAsync(finalText, cancellationToken);
    }

    public Task<float[][]> EncodeBatchAsync(IReadOnlyList<string> texts, string? prefix = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_innerService == null) throw new InvalidOperationException("OpenAI 引擎尚未初始化");

        if (!string.IsNullOrEmpty(prefix))
        {
            var prefixed = texts.Select(t => $"{prefix}{t}").ToList();
            return _innerService.EncodeBatchAsync(prefixed, cancellationToken);
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
