using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Rougamo;
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
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var resp = await _http.PostAsync($"{BaseUrl}/embeddings", content, ct);
        resp.EnsureSuccessStatusCode();

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var data = doc.RootElement.GetProperty("data");
        var results = new float[texts.Count][];

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
        }
        catch { /* 探针失败，继续使用 ApiDimension */ }
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
