using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Business.Features.Chat.Services;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Infrastructure.Features.Storage.Bootstrap;
using ReferenceRAG.Service.Services;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReferenceRAG.Service.Controllers;

public record ChatStreamRequest(string SessionId, string Message, IReadOnlyList<string>? Images = null);
public record CreateSessionResponse(string SessionId, int UsedTokens = 0, int MaxTokens = 8192, double Percent = 0.0);

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
    private readonly ILogger<MafChatController> _logger;
    private readonly ConfigManager _configManager;

    private static readonly JsonSerializerOptions _sseOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    [ActivatorUtilitiesConstructor]
    public MafChatController(
        MafChatService chatService,
        ILogger<MafChatController> logger,
        ConfigManager? configManager = null,
        IHostEnvironment? environment = null)
    {
        _chatService = chatService;
        _logger = logger;
        if (configManager != null)
        {
            _configManager = configManager;
        }
        else if (environment != null && !string.IsNullOrWhiteSpace(environment.ContentRootPath))
        {
            var configPath = ConfigPathResolver.ResolveConfigPath(environment.ContentRootPath);
            _configManager = new ConfigManager(customConfigPath: configPath);
        }
        else
        {
            _configManager = new ConfigManager();
        }
    }

    public MafChatController(
        MafChatService chatService,
        IConfiguration configuration,
        ILogger<MafChatController> logger,
        IHostEnvironment? environment = null)
        : this(chatService, logger, null, environment)
    {
    }

    [HttpPost("sessions")]
    public ActionResult<CreateSessionResponse> CreateSession()
    {
        var (sessionId, usage) = _chatService.CreateSessionWithUsage();
        return Ok(new CreateSessionResponse(sessionId, usage.UsedTokens, usage.MaxContextTokens, usage.Percent));
    }

    [HttpGet("sessions/{sessionId}/context")]
    public ActionResult<ContextUsageInfo> GetSessionContext(string sessionId)
    {
        return Ok(_chatService.GetSessionContextUsage(sessionId));
    }

    [HttpDelete("sessions/{sessionId}")]
    public ActionResult DeleteSession(string sessionId)
    {
        _chatService.DeleteSession(sessionId);
        return NoContent();
    }

    [HttpGet("capabilities")]
    public ActionResult<ChatModelCapabilities> GetCapabilities()
    {
        return Ok(_chatService.GetCapabilities());
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
            await foreach (var evt in _chatService.StreamAsync(request.SessionId, request.Message, request.Images, ct))
            {
                var json = JsonSerializer.Serialize(evt, _sseOptions);
                await Response.WriteAsync($"data: {json}\n\n", ct);
                await Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chat streaming failed: {Message}", ex.Message);
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
        var ragConfig = _configManager.Load();
        var openAi = ragConfig.Chat?.OpenAiApi ?? new OpenAiApiChatProfile();
        return Ok(new ChatConfig
        {
            Endpoint = openAi.Endpoint,
            ApiKey = string.Empty,
            ApiKeyConfigured = !string.IsNullOrWhiteSpace(openAi.ApiKey),
            Model = openAi.Model,
            SystemPrompt = ragConfig.Chat?.SystemPrompt ?? string.Empty
        });
    }

    [HttpPost("config")]
    public ActionResult SaveConfig([FromBody] ChatConfig config)
    {
        try
        {
            var ragConfig = _configManager.Load();
            ragConfig.Chat ??= new ChatModelConfig();
            ragConfig.Chat.OpenAiApi ??= new OpenAiApiChatProfile();

            if (!string.IsNullOrWhiteSpace(config.Endpoint))
                ragConfig.Chat.OpenAiApi.Endpoint = config.Endpoint.Trim();

            if (string.Equals(config.ApiKey, "__CLEAR__", StringComparison.OrdinalIgnoreCase))
                ragConfig.Chat.OpenAiApi.ApiKey = string.Empty;
            else if (!string.IsNullOrWhiteSpace(config.ApiKey))
                ragConfig.Chat.OpenAiApi.ApiKey = config.ApiKey.Trim();

            if (!string.IsNullOrWhiteSpace(config.Model))
                ragConfig.Chat.OpenAiApi.Model = config.Model.Trim();

            if (!string.IsNullOrWhiteSpace(config.SystemPrompt))
                ragConfig.Chat.SystemPrompt = config.SystemPrompt.Trim();

            _configManager.Save(ragConfig);
            _chatService.ReloadConfiguration();

            _logger.LogInformation("Chat 配置已通过 ConfigManager 保存并即时热更新");
            return Ok(new { message = "配置已保存并即时生效" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存 Chat 配置失败");
            return StatusCode(500, new { error = ex.Message });
        }
    }
}
