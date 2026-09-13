using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Rougamo;
using ReferenceRAG.Core.Helpers;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Tracing;

namespace ReferenceRAG.Core.Services;

/// <summary>
/// OpenAI 兼容嵌入 API 实现（Ollama / vLLM / Xinference / LM Studio / TEI 等）
/// 调用标准 POST /v1/embeddings 端点
/// </summary>
public sealed class OpenAIEmbeddingService : IEmbeddingService, IDisposable, IRougamo<SearchTraceAttribute>
{
    private static readonly ILogger _logger = StaticLogger.GetLogger("OpenAIEmbeddingService");
    private readonly HttpClient _http;
    private readonly int _batchSize;
    private readonly int _configuredDimension;
    private readonly SemaphoreSlim _probeLock = new(1, 1);
    private volatile int _dimension;
    private bool _probed;

    public string ModelName { get; private set; }
    public int Dimension => _dimension > 0 ? _dimension : _configuredDimension;
    public bool IsSimulationMode => string.IsNullOrWhiteSpace(BaseUrl);
    public bool SupportsAsymmetricEncoding => false;

    internal string BaseUrl { get; }

    public OpenAIEmbeddingService(EmbeddingConfig cfg)
    {
        ModelName = cfg.ModelName;
        BaseUrl = (cfg.ApiBaseUrl ?? "").TrimEnd('/');
        _batchSize = Math.Max(1, cfg.BatchSize);
        _configuredDimension = cfg.ApiDimension ?? 0;

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
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
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("[OpenAIEmbedding] POST {Url} model={Model} batch={Batch} body={Body}",
                url, ModelName, texts.Count, Truncate(body, 1000));

        var resp = await _http.PostAsync(url, content, ct);
        var payload = await resp.Content.ReadAsByteArrayAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            // 非 2xx 时响应体通常携带服务端的错误原因（模型名不对、路由不存在等），
            // 必须记下来，否则 EnsureSuccessStatusCode 会把它丢掉。
            _logger.LogError("[OpenAIEmbedding] HTTP {Status} {Reason} | POST {Url} | 响应: {Body}",
                (int)resp.StatusCode, resp.ReasonPhrase, url, Truncate(Decode(payload), 2000));
            throw new HttpRequestException(
                $"嵌入服务返回 {(int)resp.StatusCode} ({resp.ReasonPhrase})；POST {url}；响应: {Truncate(Decode(payload), 500)}");
        }

        var results = new float[texts.Count][];
        try
        {
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
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // 这类失败几乎都源于响应结构与 OpenAI 规范不符（例如把 /embeddings 误当成 /v1/embeddings，
            // 或 apiBaseUrl 已经含 /v1 导致实际请求打到 /v1/v1/embeddings）。
            _logger.LogError(ex,
                "[OpenAIEmbedding] 响应解析失败 | POST {Url} | 根元素类型: {Kind} | 响应: {Body}",
                url, RootKind(payload), Truncate(Decode(payload), 2000));
            throw new InvalidOperationException(
                $"无法解析嵌入响应（{ex.Message}）；POST {url}；根元素类型: {RootKind(payload)}；" +
                $"请确认接口地址返回 OpenAI 格式的 {{\"data\":[{{\"index\":0,\"embedding\":[...]}}]}}；" +
                $"响应片段: {Truncate(Decode(payload), 300)}", ex);
        }
        return results;
    }

    private static string Decode(byte[] payload) =>
        payload.Length == 0 ? "(空)" : Encoding.UTF8.GetString(payload);

    private static string Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? "(空)"
        : value.Length <= max ? value : value[..max] + $"…(共 {value.Length} 字符)";

    /// <summary>记录根元素类型，用于快速判断响应是对象、数组还是标量</summary>
    private static string RootKind(byte[] payload)
    {
        if (payload.Length == 0) return "(空响应体)";
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                ? "Object: " + string.Join(",", doc.RootElement.EnumerateObject().Select(p => p.Name).Take(8))
                : doc.RootElement.ValueKind.ToString();
        }
        catch (JsonException) { return "(非 JSON，可能是 HTML 错误页)"; }
    }

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
            // 探针失败原先被静默吞掉，导致调用方只看到后续连锁报错、无法定位原因。
            _logger.LogWarning(ex,
                "[OpenAIEmbedding] 探针失败，将回退使用配置的 ApiDimension={Dimension}；端点={Url}。" +
                "请检查 apiBaseUrl 是否指向 OpenAI 兼容端点（通常应以 /v1 结尾）。",
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

    public void Dispose() => _http.Dispose();
}
