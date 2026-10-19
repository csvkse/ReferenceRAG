using Microsoft.AspNetCore.Mvc;
using ReferenceRAG.Service.Services;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ReferenceRAG.Service.Controllers;

public record ChatStreamRequest(string SessionId, string Message);
public record CreateSessionResponse(string SessionId);

public class ChatConfig
{
    public string Endpoint { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string SystemPrompt { get; set; } = string.Empty;
    public bool ApiKeyConfigured { get; set; }
}

[ApiController]
[Route("api/chat")]
public class MafChatController : ControllerBase
{
    private readonly MafChatService _chatService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MafChatController> _logger;

    private static readonly JsonSerializerOptions _sseOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private static readonly JsonSerializerOptions _writeOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null
    };

    public MafChatController(MafChatService chatService, IConfiguration configuration, ILogger<MafChatController> logger)
    {
        _chatService = chatService;
        _configuration = configuration;
        _logger = logger;
    }

    [HttpPost("sessions")]
    public ActionResult<CreateSessionResponse> CreateSession()
    {
        var sessionId = _chatService.CreateSession();
        return Ok(new CreateSessionResponse(sessionId));
    }

    [HttpDelete("sessions/{sessionId}")]
    public ActionResult DeleteSession(string sessionId)
    {
        _chatService.DeleteSession(sessionId);
        return NoContent();
    }

    [HttpGet("tools")]
    public ActionResult<IReadOnlyList<string>> GetTools()
    {
        return Ok(_chatService.GetToolDescriptions());
    }

    [HttpPost("stream")]
    public async Task StreamChat([FromBody] ChatStreamRequest request, CancellationToken ct)
    {
        Response.ContentType = "text/event-stream";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        try
        {
            await foreach (var evt in _chatService.StreamAsync(request.SessionId, request.Message, ct))
            {
                var json = JsonSerializer.Serialize(evt, _sseOptions);
                await Response.WriteAsync($"data: {json}\n\n", ct);
                await Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            var errJson = JsonSerializer.Serialize(new { type = "error", message = ex.Message }, _sseOptions);
            await Response.WriteAsync($"data: {errJson}\n\n");
            var doneJson = JsonSerializer.Serialize(new { type = "done" }, _sseOptions);
            await Response.WriteAsync($"data: {doneJson}\n\n");
            await Response.Body.FlushAsync();
        }
    }

    [HttpGet("config")]
    public ActionResult<ChatConfig> GetConfig()
    {
        var section = _configuration.GetSection("Chat");
        var appSettingsPath = Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json");
        string endpoint = section["Endpoint"] ?? string.Empty;
        string model = section["Model"] ?? string.Empty;
        string systemPrompt = section["SystemPrompt"] ?? string.Empty;
        bool hasApiKey = !string.IsNullOrWhiteSpace(section["ApiKey"]);

        // 若 appsettings.json 存在，补充核对文件中的最新状态（避免 IConfiguration reload 延迟）
        if (System.IO.File.Exists(appSettingsPath))
        {
            try
            {
                var json = System.IO.File.ReadAllText(appSettingsPath);
                var root = JsonNode.Parse(json)?.AsObject();
                if (root?["Chat"] is JsonObject chatObj)
                {
                    if (chatObj["Endpoint"] != null) endpoint = chatObj["Endpoint"]?.GetValue<string>() ?? endpoint;
                    if (chatObj["Model"] != null) model = chatObj["Model"]?.GetValue<string>() ?? model;
                    if (chatObj["SystemPrompt"] != null) systemPrompt = chatObj["SystemPrompt"]?.GetValue<string>() ?? systemPrompt;
                    if (chatObj["ApiKey"] != null)
                    {
                        var keyInFile = chatObj["ApiKey"]?.GetValue<string>();
                        hasApiKey = !string.IsNullOrWhiteSpace(keyInFile);
                    }
                }
            }
            catch
            {
                // fallback to IConfiguration
            }
        }

        return Ok(new ChatConfig
        {
            Endpoint = endpoint,
            ApiKey = string.Empty,
            ApiKeyConfigured = hasApiKey,
            Model = model,
            SystemPrompt = systemPrompt
        });
    }

    [HttpPost("config")]
    public ActionResult SaveConfig([FromBody] ChatConfig config)
    {
        try
        {
            var appSettingsPath = Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json");
            JsonObject root;
            if (System.IO.File.Exists(appSettingsPath))
            {
                var json = System.IO.File.ReadAllText(appSettingsPath);
                root = JsonNode.Parse(json)?.AsObject() ?? new JsonObject();
            }
            else
            {
                root = new JsonObject();
            }

            var existingApiKey = root["Chat"]?["ApiKey"]?.GetValue<string>()
                ?? _configuration.GetSection("Chat")["ApiKey"]
                ?? string.Empty;

            string effectiveApiKey;
            if (string.Equals(config.ApiKey, "__CLEAR__", StringComparison.OrdinalIgnoreCase))
            {
                effectiveApiKey = string.Empty;
            }
            else if (string.IsNullOrWhiteSpace(config.ApiKey) && !string.IsNullOrWhiteSpace(existingApiKey))
            {
                // 前端脱敏留空：自动继承已有有效 Key，防止意外抹除
                effectiveApiKey = existingApiKey;
            }
            else
            {
                effectiveApiKey = config.ApiKey?.Trim() ?? string.Empty;
            }

            var chatObj = root["Chat"]?.AsObject() ?? new JsonObject();
            chatObj["Endpoint"] = config.Endpoint;
            chatObj["ApiKey"] = effectiveApiKey;
            chatObj["Model"] = config.Model;
            chatObj["SystemPrompt"] = config.SystemPrompt;
            root["Chat"] = chatObj;

            System.IO.File.WriteAllText(appSettingsPath, root.ToJsonString(_writeOptions));

            // 即时热更新 MafChatService 客户端
            _chatService.UpdateConfiguration(config.Endpoint, effectiveApiKey, config.Model, config.SystemPrompt);

            _logger.LogInformation("Chat 配置已保存并即时生效");
            return Ok(new { message = "配置已保存并即时生效" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存 Chat 配置失败");
            return StatusCode(500, new { error = ex.Message });
        }
    }
}
