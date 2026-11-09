using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using ReferenceRAG.Infrastructure.Features.ModelManagement.Rerank;

namespace ReferenceRAG.Core.Services.Rerank;

/// <summary>
/// OpenAI 兼容重排 API 实现（SiliconFlow / Jina / Cohere / infinity-emb / TEI / NVIDIA NIM 等）
/// 调用标准 POST /v1/rerank 端点，内置批次切分、分值归一化与容灾降级
/// </summary>
public sealed class OpenAIRerankService : IRerankService, IDisposable, IRougamo<SearchTraceAttribute>
{
    private static readonly ILogger _logger = StaticLogger.GetLogger("OpenAIRerankService");
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly IScoreNormalizer _scoreNormalizer;
    private readonly RerankTokenBudgeter _budgeter;
    private readonly bool _ownsClient;
    private bool _disposed;

    public string ModelName { get; private set; }
    public bool IsLoaded => !string.IsNullOrWhiteSpace(_baseUrl);

    public OpenAIRerankService(
        RerankConfig cfg,
        HttpMessageHandler? customHandler = null,
        IScoreNormalizer? scoreNormalizer = null,
        RerankTokenBudgeter? budgeter = null)
    {
        ModelName = cfg.ModelName;
        _baseUrl = (cfg.ApiBaseUrl ?? "").TrimEnd('/');
        _scoreNormalizer = scoreNormalizer ?? ScoreNormalizer.Instance;
        _budgeter = budgeter ?? RerankTokenBudgeter.Default;

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
    }

    public async Task<RerankResult> RerankBatchAsync(
        string query, IEnumerable<string> documents, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var docs = documents.ToList();
        if (docs.Count == 0)
        {
            return new RerankResult { Query = query, Documents = new List<RerankDocument>(), DurationMs = 0 };
        }

        var sw = Stopwatch.StartNew();

        // 1. 预算控制与批次切分（防止超出单次云端网关载荷上限）
        var partitions = _budgeter.Partition(docs);
        var mergedResults = new List<RerankDocument>(docs.Count);

        try
        {
            foreach (var partition in partitions)
            {
                var partitionResults = await ExecutePartitionRerankAsync(query, partition, ct);
                mergedResults.AddRange(partitionResults);
            }

            sw.Stop();

            return new RerankResult
            {
                Query = query,
                Documents = mergedResults.OrderByDescending(r => r.RelevanceScore).ToList(),
                DurationMs = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning(ex, "[OpenAIRerank] 远端重排服务调用失败，触发弹性保底策略（按召回原始顺序降序赋分）: {Message}", ex.Message);

            // 容灾平滑降级：按原有初筛顺序保底返回，避免搜索管线抛出未捕获异常引发前端崩溃
            var fallbackDocs = docs.Select((doc, i) => new RerankDocument
            {
                Index = i,
                Document = doc,
                RelevanceScore = Math.Max(0.01, 1.0 - (double)i / Math.Max(1, docs.Count))
            }).ToList();

            return new RerankResult
            {
                Query = query,
                Documents = fallbackDocs,
                DurationMs = sw.ElapsedMilliseconds
            };
        }
    }

    private async Task<List<RerankDocument>> ExecutePartitionRerankAsync(
        string query, RerankBatchPartition partition, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            model = ModelName,
            query,
            documents = partition.Documents,
            top_n = partition.Documents.Count
        });

        var url = $"{_baseUrl}/rerank";
        const int maxRetries = 3;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                var resp = await _http.PostAsync(url, content, ct);
                var payload = await resp.Content.ReadAsByteArrayAsync(ct);

                if (!resp.IsSuccessStatusCode)
                {
                    var statusCode = (int)resp.StatusCode;
                    var errorBody = Decode(payload);

                    if ((statusCode == 429 || statusCode >= 500) && attempt < maxRetries)
                    {
                        var delayMs = (int)(Math.Pow(2, attempt) * 250);
                        _logger.LogWarning("[OpenAIRerank] HTTP {Status}，将在 {Delay}ms 后执行重试... POST {Url}",
                            statusCode, delayMs, url);
                        await Task.Delay(delayMs, ct);
                        continue;
                    }

                    _logger.LogError("[OpenAIRerank] HTTP {Status} {Reason} | POST {Url} | 响应: {Body}",
                        statusCode, resp.ReasonPhrase, url, Truncate(errorBody, 2000));
                    throw new HttpRequestException(
                        $"重排服务返回 {statusCode} ({resp.ReasonPhrase})；POST {url}；响应: {Truncate(errorBody, 500)}");
                }

                var results = new List<RerankDocument>();
                using var doc = JsonDocument.Parse(payload);
                foreach (var item in doc.RootElement.GetProperty("results").EnumerateArray())
                {
                    var partitionIdx = item.GetProperty("index").GetInt32();
                    var originalIdx = partition.OriginalIndices[partitionIdx];
                    var rawScore = item.GetProperty("relevance_score").GetDouble();
                    var normalizedScore = _scoreNormalizer.Normalize(rawScore, ModelName);

                    results.Add(new RerankDocument
                    {
                        Index = originalIdx,
                        Document = partition.Documents[partitionIdx],
                        RelevanceScore = normalizedScore
                    });
                }

                return results;
            }
            catch (Exception ex) when (attempt < maxRetries && ex is not OperationCanceledException)
            {
                var delayMs = (int)(Math.Pow(2, attempt) * 250);
                _logger.LogWarning(ex, "[OpenAIRerank] 请求异常，将在 {Delay}ms 后执行第 {Next} 次重试...", delayMs, attempt + 1);
                await Task.Delay(delayMs, ct);
            }
        }

        throw new HttpRequestException($"重排请求在 {maxRetries} 次重试后均未成功。");
    }

    public async Task<double> RerankAsync(string query, string document, CancellationToken ct = default)
    {
        var result = await RerankBatchAsync(query, [document], ct);
        return result.Documents.FirstOrDefault()?.RelevanceScore ?? 0.0;
    }

    public Task<bool> ReloadModelAsync(string modelPath, string modelName)
    {
        ModelName = modelName;
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

    private static string Decode(byte[] payload) =>
        payload.Length == 0 ? "(空)" : Encoding.UTF8.GetString(payload);

    private static string Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? "(空)"
        : value.Length <= max ? value : value[..max] + $"…(共 {value.Length} 字符)";
}
