using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Service.Services;

namespace ReferenceRAG.Service.Controllers;

/// <summary>
/// OpenAI 兼容的 Chat Completions 接口
/// 供第三方软件（Obsidian 插件、Cherry Studio、NextChat、Chatbox、Cursor、LangChain 等）直接调用
/// 输出严格遵循 OpenAI 标准 camelCase 规范
/// </summary>
[ApiController]
public class OpenAIChatController : ControllerBase
{
    private readonly MafChatService _chatService;
    private readonly ITokenizer _tokenizer;
    private readonly ILogger<OpenAIChatController> _logger;

    private static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public OpenAIChatController(
        MafChatService chatService,
        ITokenizer tokenizer,
        ILogger<OpenAIChatController> logger)
    {
        _chatService = chatService;
        _tokenizer = tokenizer;
        _logger = logger;
    }

    /// <summary>
    /// POST /v1/chat/completions
    /// 执行 OpenAI 兼容的聊天补全（支持流式 SSE 与单次 JSON 响应，自适应 RAG 增强）
    /// </summary>
    [HttpPost("v1/chat/completions")]
    public async Task<ActionResult> CreateChatCompletion([FromBody] JsonElement request, CancellationToken ct)
    {
        // 1. 验证必需字段 messages
        if (!request.TryGetProperty("messages", out var messagesEl) || messagesEl.ValueKind != JsonValueKind.Array)
        {
            return OpenAIError("messages is required and must be an array of message objects", "invalid_request_error", 400);
        }

        var (messages, parseError) = ParseMessages(messagesEl);
        if (parseError != null)
        {
            return OpenAIError(parseError, "invalid_request_error", 400);
        }

        if (messages.Count == 0)
        {
            return OpenAIError("messages array cannot be empty", "invalid_request_error", 400);
        }

        // 2. 基础安全边界校验
        const int MaxMessageCount = 200;
        const int MaxTotalChars = 256000;
        if (messages.Count > MaxMessageCount)
        {
            return OpenAIError($"messages array cannot exceed {MaxMessageCount} items", "invalid_request_error", 400);
        }

        long totalChars = 0;
        foreach (var m in messages)
        {
            totalChars += m.Text?.Length ?? 0;
        }
        if (totalChars > MaxTotalChars)
        {
            return OpenAIError($"Total messages content exceeds maximum length of {MaxTotalChars} characters", "invalid_request_error", 400);
        }

        // 3. 提取请求参数
        string model = request.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == JsonValueKind.String
            ? modelEl.GetString()?.Trim() ?? "reference-rag"
            : "reference-rag";

        bool stream = request.TryGetProperty("stream", out var streamEl) && streamEl.ValueKind == JsonValueKind.True;

        // 4. 检查服务配置就绪状态
        if (!_chatService.IsConfigured)
        {
            _logger.LogWarning("[OpenAICompat] Chat client is not configured; returning service_unavailable");
            return OpenAIError("Chat API Key is not configured in ReferenceRAG settings. Please configure it in settings before calling.", "service_unavailable", 503);
        }

        // 5. 判断是否启用 RAG 知识库检索增强 (Mode B)
        bool isRagMode = model.Equals("reference-rag", StringComparison.OrdinalIgnoreCase) ||
                         model.Contains("rag", StringComparison.OrdinalIgnoreCase);

        if (isRagMode)
        {
            var latestUserQuery = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(latestUserQuery))
            {
                var ragContext = await _chatService.RetrieveRagContextAsync(latestUserQuery, topK: 5, ct);
                if (!string.IsNullOrWhiteSpace(ragContext))
                {
                    // 在消息头部注入 RAG 系统参考提示词
                    messages.Insert(0, new ChatMessage(ChatRole.System, ragContext));
                    _logger.LogInformation("[OpenAICompat] RAG context successfully injected for query: \"{Query}\"",
                        latestUserQuery.Length > 40 ? latestUserQuery[..40] + "..." : latestUserQuery);
                }
            }
        }

        // 6. 执行流式或非流式生成
        var sw = Stopwatch.StartNew();
        string completionId = "chatcmpl-" + Guid.NewGuid().ToString("N")[..16];
        long createdTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (stream)
        {
            return await HandleStreamingCompletionAsync(completionId, createdTimestamp, model, messages, sw, ct);
        }
        else
        {
            return await HandleNonStreamingCompletionAsync(completionId, createdTimestamp, model, messages, sw, ct);
        }
    }

    private async Task<ActionResult> HandleStreamingCompletionAsync(
        string completionId,
        long created,
        string model,
        List<ChatMessage> messages,
        Stopwatch sw,
        CancellationToken ct)
    {
        Response.ContentType = "text/event-stream";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        var responseText = new StringBuilder();

        try
        {
            // 发送首个 Chunk 声明 role
            var initialChunk = new
            {
                id = completionId,
                @object = "chat.completion.chunk",
                created,
                model,
                choices = new[]
                {
                    new
                    {
                        index = 0,
                        delta = new { role = "assistant", content = "" },
                        finish_reason = (string?)null
                    }
                }
            };
            await Response.WriteAsync($"data: {JsonSerializer.Serialize(initialChunk, CamelCase)}\n\n", ct);
            await Response.Body.FlushAsync(ct);

            // 增量流式生成并输出
            await foreach (var delta in _chatService.StreamCompletionAsync(messages, ct: ct))
            {
                if (string.IsNullOrEmpty(delta)) continue;
                responseText.Append(delta);

                var chunk = new
                {
                    id = completionId,
                    @object = "chat.completion.chunk",
                    created,
                    model,
                    choices = new[]
                    {
                        new
                        {
                            index = 0,
                            delta = new { content = delta },
                            finish_reason = (string?)null
                        }
                    }
                };
                await Response.WriteAsync($"data: {JsonSerializer.Serialize(chunk, CamelCase)}\n\n", ct);
                await Response.Body.FlushAsync(ct);
            }

            // 发送结束 stop 块，包含 token usage 统计
            int promptTokens = CountPromptTokens(messages);
            int completionTokens = _tokenizer.CountTokens(responseText.ToString());

            var finalChunk = new
            {
                id = completionId,
                @object = "chat.completion.chunk",
                created,
                model,
                choices = new[]
                {
                    new
                    {
                        index = 0,
                        delta = new { },
                        finish_reason = "stop"
                    }
                },
                usage = new
                {
                    prompt_tokens = promptTokens,
                    completion_tokens = completionTokens,
                    total_tokens = promptTokens + completionTokens
                }
            };
            await Response.WriteAsync($"data: {JsonSerializer.Serialize(finalChunk, CamelCase)}\n\n", ct);

            // 终结符号 [DONE]
            await Response.WriteAsync("data: [DONE]\n\n", ct);
            await Response.Body.FlushAsync(ct);

            sw.Stop();
            _logger.LogInformation("[OpenAICompat] Stream Chat completed: {PromptTokens} prompt + {CompTokens} completion tokens, {Ms}ms",
                promptTokens, completionTokens, sw.ElapsedMilliseconds);

            return new EmptyResult();
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[OpenAICompat] Client disconnected from streaming chat");
            return new EmptyResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[OpenAICompat] Streaming chat error");
            var errJson = JsonSerializer.Serialize(new
            {
                error = new
                {
                    message = ex.Message,
                    type = "server_error"
                }
            }, CamelCase);
            await Response.WriteAsync($"data: {errJson}\n\n", CancellationToken.None);
            await Response.WriteAsync("data: [DONE]\n\n", CancellationToken.None);
            await Response.Body.FlushAsync(CancellationToken.None);
            return new EmptyResult();
        }
    }

    private async Task<ActionResult> HandleNonStreamingCompletionAsync(
        string completionId,
        long created,
        string model,
        List<ChatMessage> messages,
        Stopwatch sw,
        CancellationToken ct)
    {
        try
        {
            string reply = await _chatService.CompleteAsync(messages, ct: ct);
            sw.Stop();

            int promptTokens = CountPromptTokens(messages);
            int completionTokens = _tokenizer.CountTokens(reply);

            var result = new
            {
                id = completionId,
                @object = "chat.completion",
                created,
                model,
                choices = new[]
                {
                    new
                    {
                        index = 0,
                        message = new
                        {
                            role = "assistant",
                            content = reply
                        },
                        finish_reason = "stop"
                    }
                },
                usage = new
                {
                    prompt_tokens = promptTokens,
                    completion_tokens = completionTokens,
                    total_tokens = promptTokens + completionTokens
                }
            };

            _logger.LogInformation("[OpenAICompat] Non-stream Chat completed: {PromptTokens} prompt + {CompTokens} completion tokens, {Ms}ms",
                promptTokens, completionTokens, sw.ElapsedMilliseconds);

            return CamelCaseJson(result);
        }
        catch (OperationCanceledException)
        {
            return OpenAIError("Request was cancelled by client", "cancelled", 499);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[OpenAICompat] Chat completion error");
            return OpenAIError(ex.Message, "server_error", 500);
        }
    }

    private (List<ChatMessage> messages, string? error) ParseMessages(JsonElement messagesElement)
    {
        var result = new List<ChatMessage>();
        foreach (var item in messagesElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                return (result, "Each item in messages must be a JSON object");

            if (!item.TryGetProperty("role", out var roleEl) || roleEl.ValueKind != JsonValueKind.String)
                return (result, "Each message must contain a valid string 'role'");

            string roleStr = roleEl.GetString()?.ToLowerInvariant() ?? "user";
            var role = roleStr switch
            {
                "system" => ChatRole.System,
                "assistant" => ChatRole.Assistant,
                "tool" => ChatRole.Tool,
                _ => ChatRole.User
            };

            string contentText = "";
            if (item.TryGetProperty("content", out var contentEl))
            {
                if (contentEl.ValueKind == JsonValueKind.String)
                {
                    contentText = contentEl.GetString() ?? "";
                }
                else if (contentEl.ValueKind == JsonValueKind.Array)
                {
                    // 支持多部分内容格式 [{type: "text", text: "..."}]
                    var sb = new StringBuilder();
                    foreach (var part in contentEl.EnumerateArray())
                    {
                        if (part.ValueKind == JsonValueKind.Object &&
                            part.TryGetProperty("text", out var textEl) &&
                            textEl.ValueKind == JsonValueKind.String)
                        {
                            sb.Append(textEl.GetString());
                        }
                    }
                    contentText = sb.ToString();
                }
            }

            result.Add(new ChatMessage(role, contentText));
        }

        return (result, null);
    }

    private int CountPromptTokens(IEnumerable<ChatMessage> messages)
    {
        int total = 0;
        foreach (var msg in messages)
        {
            total += 3; // OpenAI 协议每条消息的固有边界开销
            if (!string.IsNullOrEmpty(msg.Text))
            {
                total += _tokenizer.CountTokens(msg.Text);
            }
        }
        return total + 3; // 前后引导 token 开销
    }

    private ContentResult OpenAIError(string message, string type = "invalid_request_error", int statusCode = 400)
    {
        Response.StatusCode = statusCode;
        Response.ContentType = "application/json";
        var errObj = new
        {
            error = new
            {
                message,
                type,
                param = (string?)null,
                code = (string?)null
            }
        };
        return Content(JsonSerializer.Serialize(errObj, CamelCase), "application/json");
    }

    private ContentResult CamelCaseJson(object value, int statusCode = 200)
    {
        Response.StatusCode = statusCode;
        Response.ContentType = "application/json";
        return Content(JsonSerializer.Serialize(value, CamelCase), "application/json");
    }
}
