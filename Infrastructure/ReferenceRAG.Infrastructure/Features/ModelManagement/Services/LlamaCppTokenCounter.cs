using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Core.Services.Tokenizers;

/// <summary>
/// Uses llama.cpp's POST /tokenize endpoint. This endpoint is a llama.cpp extension,
/// not part of the standard OpenAI API.
/// </summary>
public sealed class LlamaCppTokenCounter : IEmbeddingTokenCounter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly Uri _tokenizeUrl;
    private readonly string? _apiKey;
    private volatile bool _unsupported;

    public LlamaCppTokenCounter(EmbeddingConfig config)
    {
        var baseUrl = (config.ApiBaseUrl ?? "").TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException("OpenAI 模式未配置 apiBaseUrl，无法使用精确 token 计数。");

        var root = baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? baseUrl[..^3]
            : baseUrl;
        _tokenizeUrl = new Uri(root.TrimEnd('/') + "/tokenize");
        _apiKey = config.ApiKey;
    }

    public async Task<int> CountTokensAsync(string text, CancellationToken cancellationToken = default)
    {
        if (_unsupported)
            throw new NotSupportedException("当前嵌入服务不支持 llama.cpp /tokenize。");

        using var content = new StringContent(
            JsonSerializer.Serialize(new TokenizeRequest(text), JsonOptions),
            Encoding.UTF8,
            "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, _tokenizeUrl) { Content = content };
        if (!string.IsNullOrWhiteSpace(_apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        using var response = await _http.SendAsync(request, cancellationToken);
        if (response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.MethodNotAllowed)
        {
            _unsupported = true;
            throw new NotSupportedException("当前嵌入服务不支持 llama.cpp /tokenize。");
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<TokenizeResponse>(stream, JsonOptions, cancellationToken);
        return payload?.Tokens?.Count ?? throw new InvalidOperationException("嵌入服务 tokenize 响应缺少 tokens。");
    }

    private sealed record TokenizeRequest([property: JsonPropertyName("content")] string Content);

    private sealed record TokenizeResponse([property: JsonPropertyName("tokens")] List<int>? Tokens);
}

public sealed class UnsupportedEmbeddingTokenCounter : IEmbeddingTokenCounter
{
    public Task<int> CountTokensAsync(string text, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("当前嵌入服务不支持 llama.cpp /tokenize。");
}
