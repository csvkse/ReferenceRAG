using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Rougamo;
using ReferenceRAG.Core.Helpers;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Tracing;
using ReferenceRAG.Infrastructure.Features.ModelManagement.Helpers;

namespace ReferenceRAG.Core.Services;

/// <summary>
/// OpenAI 兼容嵌入 API 实现（SiliconFlow / Ollama / vLLM / Xinference / LM Studio / TEI 等）
/// 调用标准 POST /v1/embeddings 端点，内置重试、自动探针与 IPv4 优先处理
/// </summary>
public sealed class OpenAIEmbeddingService : IEmbeddingService, IDisposable, IRougamo<SearchTraceAttribute>
{
    private static readonly ILogger _logger = StaticLogger.GetLogger("OpenAIEmbeddingService");
    private readonly HttpClient _http;
    private readonly int _batchSize;
    private readonly int _configuredDimension;
    private readonly SemaphoreSlim _probeLock = new(1, 1);
    private readonly bool _ownsClient;
    private volatile int _dimension;
    private bool _probed;
    private bool _disposed;

    public string ModelName { get; private set; }
    public int Dimension => _dimension > 0 ? _dimension : _configuredDimension;
    public bool IsSimulationMode => string.IsNullOrWhiteSpace(BaseUrl);
    public bool SupportsAsymmetricEncoding => false;

    internal string BaseUrl { get; }

    public OpenAIEmbeddingService(EmbeddingConfig cfg, HttpMessageHandler? customHandler = null)
    {
        ModelName = cfg.ModelName;
        BaseUrl = (cfg.ApiBaseUrl ?? "").TrimEnd('/');
        _batchSize = Math.Max(1, cfg.BatchSize);
        _configuredDimension = cfg.ApiDimension ?? 0;

        if (customHandler != null)
        {
            _http = new HttpClient(customHandler) { Timeout = TimeSpan.FromSeconds(120) };
            _ownsClient = true;
        }
        else
        {
            var handler = CloudHttpHandlerFactory.CreateHandler(connectTimeoutSeconds: 10);
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(120) };
            _ownsClient = true;
        }

        if (!string.IsNullOrWhiteSpace(cfg.ApiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiKey);

        // 后台预热：建立 TCP/TLS 连接 + 探测维度，消除首次搜索冷启动延迟
        if (!IsSimulationMode)
            _ = Task.Run(() => EnsureProbeAsync(CancellationToken.None));
    }

    public async Task<float[]> EncodeAsync(string text, CancellationToken ct = default)
        => (await EncodeBatchCoreAsync([text], ct))[0];

    public async Task<float[]> EncodeAsync(string text, EmbeddingMode mode, CancellationToken ct = default)
        => await EncodeAsync(text, ct);

    public async Task<float[][]> EncodeBatchAsync(IEnumerable<string> texts, CancellationToken ct = default)
    {
        await EnsureProbeAsync(ct);
        var list = texts.ToList();
        if (list.Count == 0) return [];

        var result = new float[list.Count][];
        for (int i = 0; i < list.Count; i += _batchSize)
        {
            var batch = list.GetRange(i, Math.Min(_batchSize, list.Count - i));
            var vecs = await EncodeBatchCoreAsync(batch, ct);
            for (int j = 0; j < vecs.Length; j++)
                result[i + j] = vecs[j];
        }
        return result;
    }

    public async Task<float[][]> EncodeBatchAsync(IEnumerable<string> texts, EmbeddingMode mode, CancellationToken ct = default)
        => await EncodeBatchAsync(texts, ct);

    private async Task<float[][]> EncodeBatchCoreAsync(List<string> texts, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new { model = ModelName, input = texts });
        var url = $"{BaseUrl}/embeddings";
        const int maxRetries = 3;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                using var content = new StringContent(body, Encoding.UTF8, "application/json");

                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug("[OpenAIEmbedding] POST {Url} model={Model} batch={Batch}", url, ModelName, texts.Count);

                var resp = await _http.PostAsync(url, content, ct);
                var payload = await resp.Content.ReadAsByteArrayAsync(ct);

                if (!resp.IsSuccessStatusCode)
                {
                    var statusCode = (int)resp.StatusCode;
                    var errorBody = Decode(payload);

                    if ((statusCode == 429 || statusCode >= 500) && attempt < maxRetries)
                    {
                        var delayMs = (int)(Math.Pow(2, attempt) * 250);
                        _logger.LogWarning("[OpenAIEmbedding] HTTP {Status}，将在 {Delay}ms 后执行重试... POST {Url}",
                            statusCode, delayMs, url);
                        await Task.Delay(delayMs, ct);
                        continue;
                    }

                    _logger.LogError("[OpenAIEmbedding] HTTP {Status} {Reason} | POST {Url} | 响应: {Body}",
                        statusCode, resp.ReasonPhrase, url, Truncate(errorBody, 2000));
                    throw new HttpRequestException(
                        $"嵌入服务返回 {statusCode} ({resp.ReasonPhrase})；POST {url}；响应: {Truncate(errorBody, 500)}");
                }

                var results = new float[texts.Count][];
                using var doc = JsonDocument.Parse(payload);
                var data = doc.RootElement.GetProperty("data");

                foreach (var item in data.EnumerateArray())
                {
                    var idx = item.GetProperty("index").GetInt32();
                    var vec = item.GetProperty("embedding").EnumerateArray()
                                 .Select(e => e.GetSingle()).ToArray();
                    results[idx] = vec;
                    if (_dimension == 0) _dimension = vec.Length;
                }

                return results;
            }
            catch (Exception ex) when (attempt < maxRetries && ex is not OperationCanceledException)
            {
                var delayMs = (int)(Math.Pow(2, attempt) * 250);
                _logger.LogWarning(ex, "[OpenAIEmbedding] 请求异常，将在 {Delay}ms 后重试: {Message}", delayMs, ex.Message);
                await Task.Delay(delayMs, ct);
            }
        }

        throw new HttpRequestException($"嵌入请求在 {maxRetries} 次重试后均失败。");
    }

    private static string Decode(byte[] payload) =>
        payload.Length == 0 ? "(空)" : Encoding.UTF8.GetString(payload);

    private static string Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? "(空)"
        : value.Length <= max ? value : value[..max] + $"…(共 {value.Length} 字符)";

    private async Task EnsureProbeAsync(CancellationToken ct)
    {
        if (_probed || IsSimulationMode) return;
        await _probeLock.WaitAsync(ct);
        try
        {
            if (_probed) return;
            var probe = await EncodeBatchCoreAsync(["probe"], ct);
            _dimension = probe[0].Length;
            _probed = true;
            _logger.LogInformation("[OpenAIEmbedding] 探针成功: 维度={Dimension} 端点={Url}",
                _dimension, $"{BaseUrl}/embeddings");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[OpenAIEmbedding] 探针失败，将回退使用配置的 ApiDimension={Dimension}；端点={Url}",
                _configuredDimension, $"{BaseUrl}/embeddings");
        }
        finally { _probeLock.Release(); }
    }

    public float[] Normalize(float[] v)
    {
        var norm = MathF.Sqrt(v.Sum(x => x * x));
        return norm == 0 ? v : v.Select(x => x / norm).ToArray();
    }

    public float Similarity(float[] a, float[] b) => a.Zip(b, (x, y) => x * y).Sum();

    public Task<bool> ReloadModelAsync(string modelPath, string modelName, int? maxSequenceLength = null)
    {
        ModelName = modelName;
        _probed = false;
        return Task.FromResult(true);
    }

    public void UnloadModel() { }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_ownsClient)
        {
            _http.Dispose();
        }
    }
}
