using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using ReferenceRAG.Core.Interfaces;

namespace ReferenceRAG.Service.Controllers;

/// <summary>
/// OpenAI 兼容的 Rerank 接口
/// 参考 Cohere/Jina 标准 API 格式
/// 输出严格遵循 camelCase 格式（绕过全局 PascalCase 策略）
/// </summary>
[ApiController]
public class OpenAIRerankController : ControllerBase
{
    private readonly IRerankService _rerankService;
    private readonly ITokenizer _tokenizer;
    private readonly ILogger<OpenAIRerankController> _logger;

    // 严格 camelCase 序列化
    private static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public OpenAIRerankController(
        IRerankService rerankService,
        ITokenizer tokenizer,
        ILogger<OpenAIRerankController> logger)
    {
        _rerankService = rerankService;
        _tokenizer = tokenizer;
        _logger = logger;
    }

    /// <summary>
    /// POST /v1/rerank
    /// 对文档进行重排评分
    /// </summary>
    [HttpPost("v1/rerank")]
    public async Task<ActionResult> Rerank([FromBody] JsonElement request)
    {
        // 验证必需字段
        if (!request.TryGetProperty("query", out var queryElement) || queryElement.ValueKind != JsonValueKind.String)
            return CamelCaseJson(new { error = "query is required and must be a string" }, 400);

        if (!request.TryGetProperty("documents", out var docsElement) || docsElement.ValueKind != JsonValueKind.Array)
            return CamelCaseJson(new { error = "documents is required and must be an array" }, 400);

        var sw = Stopwatch.StartNew();

        // query/model 的取值放在 try 外，遇非 UTF-8 字节时 GetString() 会抛
        // InvalidOperationException 并逃逸成空白 500，必须显式转成 400。
        if (!TryGetString(queryElement, out var query) || query == null)
            return CamelCaseJson(new { error = "query must be valid UTF-8 encoded text" }, 400);

        string model = "";
        if (request.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == JsonValueKind.String)
        {
            if (!TryGetString(modelEl, out var modelName) || modelName == null)
                return CamelCaseJson(new { error = "model must be valid UTF-8 encoded text" }, 400);
            model = modelName;
        }

        int? topN = request.TryGetProperty("top_n", out var topNEl) ? topNEl.GetInt32() : null;
        bool returnDocuments = !request.TryGetProperty("return_documents", out var returnDocsEl) || returnDocsEl.GetBoolean();

        // 解析文档数组
        var (parsedDocuments, parseError) = ParseDocuments(docsElement);
        if (parseError != null)
            return CamelCaseJson(new { error = parseError }, 400);

        var documents = parsedDocuments ?? Array.Empty<string>();
        if (documents.Length == 0)
            return CamelCaseJson(new { error = "documents array cannot be empty" }, 400);

        // 安全限制
        const int MaxDocumentCount = 256;
        const int MaxTextLength = 32000;
        if (documents.Length > MaxDocumentCount)
            return CamelCaseJson(new { error = $"documents array cannot exceed {MaxDocumentCount} items" }, 400);

        for (int i = 0; i < documents.Length; i++)
        {
            if (documents[i].Length > MaxTextLength)
                return CamelCaseJson(new { error = $"documents[{i}] exceeds maximum length of {MaxTextLength} characters" }, 400);
        }

        try
        {
            // 调用重排服务
            var rerankResult = await _rerankService.RerankBatchAsync(query, documents);

            // 构建结果列表
            var results = new List<object>();
            var totalTokens = _tokenizer.CountTokens(query);
            int resultCount = topN ?? documents.Length;

            foreach (var doc in rerankResult.Documents.Take(resultCount))
            {
                totalTokens += _tokenizer.CountTokens(documents[doc.Index]);

                results.Add(new
                {
                    index = doc.Index,
                    relevance_score = doc.RelevanceScore,
                    document = returnDocuments ? doc.Document : null
                });
            }

            sw.Stop();
            _logger.LogInformation(
                "[OpenAICompat] Rerank: {Count} docs, {Tokens} tokens, {Ms}ms, model={Model}",
                documents.Length, totalTokens, sw.ElapsedMilliseconds, _rerankService.ModelName);

            return CamelCaseJson(new
            {
                @object = "list",
                model = string.IsNullOrEmpty(model) ? _rerankService.ModelName : model,
                results,
                usage = new
                {
                    prompt_tokens = totalTokens,
                    total_tokens = totalTokens
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[OpenAICompat] Rerank failed");
            return CamelCaseJson(new { error = "Rerank processing failed" }, 500);
        }
    }

    /// <summary>
    /// POST /v1/rerank/single
    /// 对单个查询-文档对进行重排评分（简化接口）
    /// </summary>
    [HttpPost("v1/rerank/single")]
    public async Task<ActionResult> RerankSingle([FromBody] JsonElement request)
    {
        if (!request.TryGetProperty("query", out var queryElement) || queryElement.ValueKind != JsonValueKind.String)
            return CamelCaseJson(new { error = "query is required" }, 400);

        if (!request.TryGetProperty("document", out var docElement) || docElement.ValueKind != JsonValueKind.String)
            return CamelCaseJson(new { error = "document is required" }, 400);

        // 同样放在 try 外取值，非 UTF-8 时显式返回 400 而非空白 500。
        if (!TryGetString(queryElement, out var query) || query == null)
            return CamelCaseJson(new { error = "query must be valid UTF-8 encoded text" }, 400);

        if (!TryGetString(docElement, out var document) || document == null)
            return CamelCaseJson(new { error = "document must be valid UTF-8 encoded text" }, 400);

        string model = "";
        if (request.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == JsonValueKind.String)
        {
            if (!TryGetString(modelEl, out var modelName) || modelName == null)
                return CamelCaseJson(new { error = "model must be valid UTF-8 encoded text" }, 400);
            model = modelName;
        }

        var sw = Stopwatch.StartNew();

        try
        {
            var score = await _rerankService.RerankAsync(query, document);
            var tokens = _tokenizer.CountTokens(query) + _tokenizer.CountTokens(document);

            sw.Stop();
            _logger.LogInformation(
                "[OpenAICompat] RerankSingle: {Tokens} tokens, {Ms}ms, score={Score:F4}",
                tokens, sw.ElapsedMilliseconds, score);

            return CamelCaseJson(new
            {
                @object = "rerank_result",
                model = string.IsNullOrEmpty(model) ? _rerankService.ModelName : model,
                relevance_score = score,
                usage = new
                {
                    prompt_tokens = tokens,
                    total_tokens = tokens
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[OpenAICompat] RerankSingle failed");
            return CamelCaseJson(new { error = "Rerank processing failed" }, 500);
        }
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

    /// <summary>
    /// 安全读取 JSON 字符串值。
    /// JsonElement.GetString() 在值含非 UTF-8 字节时抛 InvalidOperationException，
    /// 会逃逸控制器变成空白 500；这里统一转成失败信号，由调用方返回 400。
    /// </summary>
    private static bool TryGetString(JsonElement element, out string? value)
    {
        try
        {
            value = element.GetString();
            return true;
        }
        catch (InvalidOperationException)
        {
            value = null;
            return false;
        }
    }

    /// <summary>
    /// 解析 documents 数组，支持纯字符串与 { "text": "..." } 对象两种 Cohere/Jina 形式。
    /// 返回 error 非空表示数组内含非 UTF-8 或不受支持的元素。
    /// </summary>
    private static (string[]? Documents, string? Error) ParseDocuments(JsonElement array)
    {
        var list = new List<string>(array.GetArrayLength());
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                if (!TryGetString(item, out var text) || text == null)
                    return (null, "documents contains an item that is not valid UTF-8 encoded text");
                list.Add(text);
            }
            else if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("text", out var textEl))
            {
                if (textEl.ValueKind != JsonValueKind.String)
                    return (null, "documents[].text must be a string");
                if (!TryGetString(textEl, out var objText) || objText == null)
                    return (null, "documents contains an item that is not valid UTF-8 encoded text");
                list.Add(objText);
            }
            else
            {
                return (null, $"documents[{list.Count}] must be a string or an object with a \"text\" property");
            }
        }
        return (list.ToArray(), null);
    }
}
