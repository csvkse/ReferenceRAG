using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Rougamo;
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
        var resp = await _http.PostAsync($"{_baseUrl}/rerank", content, ct);
        resp.EnsureSuccessStatusCode();
        sw.Stop();

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var results = new List<RerankDocument>();
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
}
