using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Rougamo;
using ReferenceRAG.Core.Helpers;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Tracing;

namespace ReferenceRAG.Core.Services.Rerank;

/// <summary>
/// OpenAI 兼容重排 API 实现（Jina / Cohere / infinity-emb / TEI / NVIDIA NIM 等）
/// 调用标准 POST /v1/rerank 端点
/// </summary>
public sealed class OpenAIRerankService : IRerankService, IDisposable, IRougamo<SearchTraceAttribute>
{
    private static readonly ILogger _logger = StaticLogger.GetLogger("OpenAIRerankService");
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public string ModelName { get; private set; }
    public bool IsLoaded => !string.IsNullOrWhiteSpace(_baseUrl);

    public OpenAIRerankService(RerankConfig cfg)
    {
        ModelName = cfg.ModelName;
        _baseUrl = (cfg.ApiBaseUrl ?? "").TrimEnd('/');

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        if (!string.IsNullOrWhiteSpace(cfg.ApiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiKey);
    }

    public async Task<RerankResult> RerankBatchAsync(
        string query, IEnumerable<string> documents, CancellationToken ct = default)
    {
        var docs = documents.ToList();
        var sw = Stopwatch.StartNew();

        var body = JsonSerializer.Serialize(new
        {
            model = ModelName,
            query,
            documents = docs,
            top_n = docs.Count       // 返回全部，由调用方截取 TopN
        });

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var url = $"{_baseUrl}/rerank";
        var resp = await _http.PostAsync(url, content, ct);
        var payload = await resp.Content.ReadAsByteArrayAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogError("[OpenAIRerank] HTTP {Status} {Reason} | POST {Url} | 响应: {Body}",
                (int)resp.StatusCode, resp.ReasonPhrase, url, Truncate(Decode(payload), 2000));
            throw new HttpRequestException(
                $"重排服务返回 {(int)resp.StatusCode} ({resp.ReasonPhrase})；POST {url}；响应: {Truncate(Decode(payload), 500)}");
        }
        sw.Stop();

        var results = new List<RerankDocument>();
        try
        {
            using var doc = JsonDocument.Parse(payload);
            foreach (var item in doc.RootElement.GetProperty("results").EnumerateArray())
            {
                var idx = item.GetProperty("index").GetInt32();
                results.Add(new RerankDocument
                {
                    Index = idx,
                    Document = docs[idx],
                    RelevanceScore = item.GetProperty("relevance_score").GetDouble()
                });
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            _logger.LogError(ex, "[OpenAIRerank] 响应解析失败 | POST {Url} | 响应: {Body}",
                url, Truncate(Decode(payload), 2000));
            throw new InvalidOperationException(
                $"无法解析重排响应（{ex.Message}）；POST {url}；" +
                $"请确认返回 OpenAI/Jina 格式的 {{\"results\":[{{\"index\":0,\"relevance_score\":0.5}}]}}；" +
                $"响应片段: {Truncate(Decode(payload), 300)}", ex);
        }

        return new RerankResult
        {
            Query = query,
            Documents = results.OrderByDescending(r => r.RelevanceScore).ToList(),
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    public async Task<double> RerankAsync(string query, string document, CancellationToken ct = default)
    {
        var result = await RerankBatchAsync(query, [document], ct);
        return result.Documents.FirstOrDefault()?.RelevanceScore ?? 0;
    }

    public Task<bool> ReloadModelAsync(string modelPath, string modelName)
    {
        ModelName = modelName;
        return Task.FromResult(true);
    }

    public void UnloadModel() { }

    public void Dispose() => _http.Dispose();

    private static string Decode(byte[] payload) =>
        payload.Length == 0 ? "(空)" : Encoding.UTF8.GetString(payload);

    private static string Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? "(空)"
        : value.Length <= max ? value : value[..max] + $"…(共 {value.Length} 字符)";
}
