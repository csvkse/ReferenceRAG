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
        return Ok(new ChatConfig
        {
            Endpoint = section["Endpoint"] ?? string.Empty,
            ApiKey = section["ApiKey"] ?? string.Empty,
            Model = section["Model"] ?? string.Empty,
            SystemPrompt = section["SystemPrompt"] ?? string.Empty
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

            root["Chat"] = new JsonObject
            {
                ["Endpoint"] = config.Endpoint,
                ["ApiKey"] = config.ApiKey,
                ["Model"] = config.Model,
                ["SystemPrompt"] = config.SystemPrompt
            };

            System.IO.File.WriteAllText(appSettingsPath, root.ToJsonString(_writeOptions));
            _logger.LogInformation("Chat 配置已保存，重启服务后生效");
            return Ok(new { message = "配置已保存，重启服务后生效" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存 Chat 配置失败");
            return StatusCode(500, new { error = ex.Message });
        }
    }
}
