using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using ObsidianRAG.Core.Interfaces;

namespace ObsidianRAG.Service.Controllers;

/// <summary>
/// OpenAI 兼容的 Embeddings 接口
/// 供第三方软件（Obsidian 插件、其他工具）调用
/// 输出严格遵循 OpenAI camelCase 格式（绕过全局 PascalCase 策略）
/// </summary>
[ApiController]
public class OpenAIEmbeddingsController : ControllerBase
{
    private readonly IEmbeddingService _embeddingService;
    private readonly ITokenizer _tokenizer;
    private readonly ILogger<OpenAIEmbeddingsController> _logger;

    // 严格 camelCase 序列化，与 OpenAI API 格式一致
    private static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public OpenAIEmbeddingsController(
        IEmbeddingService embeddingService,
        ITokenizer tokenizer,
        ILogger<OpenAIEmbeddingsController> logger)
    {
        _embeddingService = embeddingService;
        _tokenizer = tokenizer;
        _logger = logger;
    }

    /// <summary>
    /// POST /v1/embeddings
    /// </summary>
    [HttpPost("v1/embeddings")]
    public async Task<ActionResult> CreateEmbeddings([FromBody] JsonElement request)
    {
        if (!request.TryGetProperty("input", out var inputElement) || inputElement.ValueKind == JsonValueKind.Null)
            return CamelCaseJson(new { error = "input is required" }, 400);

        var sw = Stopwatch.StartNew();

        string model = request.TryGetProperty("model", out var modelEl) ? modelEl.GetString() ?? "" : "";

        string[] texts = inputElement.ValueKind switch
        {
            JsonValueKind.String => new[] { inputElement.GetString()! },
            JsonValueKind.Array => ParseStringArray(inputElement),
            _ => null
        };

        if (texts == null || texts.Length == 0)
            return CamelCaseJson(new { error = "input must be a string or array of strings" }, 400);

        // SEC-02: 输入长度限制
        const int MaxInputCount = 256;
        const int MaxTextLength = 32000;
        if (texts.Length > MaxInputCount)
            return CamelCaseJson(new { error = $"input array cannot exceed {MaxInputCount} items" }, 400);
        for (int i = 0; i < texts.Length; i++)
        {
            if (texts[i].Length > MaxTextLength)
                return CamelCaseJson(new { error = $"input[{i}] exceeds maximum length of {MaxTextLength} characters" }, 400);
        }

        // 解析可选的 encoding_mode 字段（非标准 OpenAI 扩展）
        var mode = EmbeddingMode.Symmetric; // 默认对称编码，保持 OpenAI 兼容
        if (request.TryGetProperty("encoding_mode", out var modeEl))
        {
            var modeStr = modeEl.GetString()?.ToLowerInvariant();
            mode = modeStr switch
            {
                "query" => EmbeddingMode.Query,
                "document" or "passage" => EmbeddingMode.Document,
                _ => EmbeddingMode.Symmetric // 无效值静默回退
            };
        }

        try
        {
            var embeddings = await _embeddingService.EncodeBatchAsync(texts, mode);

            var totalTokens = 0;
            var data = new List<object>(texts.Length);

            for (int i = 0; i < texts.Length; i++)
            {
                var tokenCount = _tokenizer.CountTokens(texts[i]);
                totalTokens += tokenCount;

                // 计算 embedding norm（正常应该 ≈1.0，因为已 L2 归一化）
                float norm = 0;
                if (embeddings[i] != null)
                    norm = (float)Math.Sqrt(embeddings[i].Sum(x => x * x));

                var displayText = texts[i].Length > 30 ? texts[i][..30] + "..." : texts[i];
                _logger.LogDebug("[Embedding] [{Index}] tokens={Tokens}, norm={Norm:F4}, dim={Dim}, text=\"{Text}\"",
                    i, tokenCount, norm, embeddings[i]?.Length ?? 0, displayText);

                data.Add(new
                {
                    @object = "embedding",
                    embedding = embeddings[i],
                    index = i
                });
            }

            sw.Stop();
            _logger.LogInformation(
                "[OpenAICompat] Embeddings: {Count} texts, {Tokens} tokens, {Ms}ms, model={Model}, dim={Dim}",
                texts.Length, totalTokens, sw.ElapsedMilliseconds,
                _embeddingService.ModelName, _embeddingService.Dimension);

            // 调试模式：在响应中附加原始 embedding 前 10 维 + norm
            bool debug = request.TryGetProperty("debug", out var debugEl) && debugEl.GetBoolean();

            if (debug)
            {
                var debugData = data.Select((d, i) => new
                {
                    index = i,
                    text = texts[i],
                    tokens = _tokenizer.CountTokens(texts[i]),
                    norm = embeddings[i] != null
                        ? Math.Sqrt(embeddings[i].Sum(x => x * x))
                        : 0,
                    first10 = embeddings[i]?.Take(10).ToArray()
                }).ToList();

                // 计算所有文本对的余弦相似度
                var similarities = new List<object>();
                for (int i = 0; i < embeddings.Length; i++)
                {
                    for (int j = i + 1; j < embeddings.Length; j++)
                    {
                        if (embeddings[i] == null || embeddings[j] == null) continue;
                        float dot = 0, normA = 0, normB = 0;
                        int len = Math.Min(embeddings[i].Length, embeddings[j].Length);
                        for (int k = 0; k < len; k++)
                        {
                            dot += embeddings[i][k] * embeddings[j][k];
                            normA += embeddings[i][k] * embeddings[i][k];
                            normB += embeddings[j][k] * embeddings[j][k];
                        }
                        float denom = (float)(Math.Sqrt(normA) * Math.Sqrt(normB));
                        float sim = denom > 0 ? dot / denom : 0;
                        similarities.Add(new
                        {
                            index_a = i,
                            index_b = j,
                            text_a = texts[i].Length > 20 ? texts[i][..20] + "..." : texts[i],
                            text_b = texts[j].Length > 20 ? texts[j][..20] + "..." : texts[j],
                            cosine_similarity = sim
                        });
                    }
                }

                return CamelCaseJson(new
                {
                    @object = "list",
                    data,
                    model = string.IsNullOrEmpty(model) ? _embeddingService.ModelName : model,
                    usage = new { prompt_tokens = totalTokens, total_tokens = totalTokens },
                    debug = new { details = debugData, similarities }
                });
            }

            return CamelCaseJson(new
            {
                @object = "list",
                data,
                model = string.IsNullOrEmpty(model) ? _embeddingService.ModelName : model,
                usage = new
                {
                    prompt_tokens = totalTokens,
                    total_tokens = totalTokens
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[OpenAICompat] Embedding failed");
            return CamelCaseJson(new { error = "Embedding processing failed" }, 500);
        }
    }

    /// <summary>
    /// GET /v1/models
    /// </summary>
    [HttpGet("v1/models")]
    public ActionResult GetModels()
    {
        return CamelCaseJson(new
        {
            @object = "list",
            data = new[]
            {
                new
                {
                    id = _embeddingService.ModelName,
                    @object = "model",
                    owned_by = "obsidian-rag",
                    created = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                }
            }
        });
    }

    /// <summary>
    /// 使用 camelCase 序列化返回响应
    /// </summary>
    private ContentResult CamelCaseJson(object value, int statusCode = 200)
    {
        Response.StatusCode = statusCode;
        Response.ContentType = "application/json";
        return Content(JsonSerializer.Serialize(value, CamelCase));
    }

    private static string[] ParseStringArray(JsonElement array)
    {
        var list = new List<string>(array.GetArrayLength());
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
                list.Add(item.GetString()!);
            else
                throw new InvalidOperationException(
                    $"input array element must be a string, got {item.ValueKind}");
        }
        return list.ToArray();
    }
}
