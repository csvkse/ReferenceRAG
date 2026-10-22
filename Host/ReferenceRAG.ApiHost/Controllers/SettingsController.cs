using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Core.Features.HardwareAcceleration.Detection;
using ReferenceRAG.Core.Features.HardwareAcceleration.Injection;
using ReferenceRAG.Core.Features.HardwareAcceleration.Scanning;
using ReferenceRAG.Core.Features.HardwareAcceleration.Models;
using ReferenceRAG.Core.Features.HardwareAcceleration.Probing;
using ReferenceRAG.Core.Features.HardwareAcceleration;
using ReferenceRAG.Service.Services;

namespace ReferenceRAG.Service.Controllers;

/// <summary>
/// 设置 API
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class SettingsController : ControllerBase
{
    private readonly ConfigManager _configManager;
    private readonly IModelManager _modelManager;
    private readonly IEmbeddingService? _embeddingService;
    private readonly IRerankService? _rerankService;
    private readonly MafChatService? _chatService;
    private readonly ILogger<SettingsController> _logger;
    private readonly IHardwareAccelerationProbeService _probeService;

    public SettingsController(
        ConfigManager configManager,
        IModelManager modelManager,
        ILogger<SettingsController> logger,
        IEmbeddingService? embeddingService = null,
        IRerankService? rerankService = null,
        MafChatService? chatService = null,
        IHardwareAccelerationProbeService? probeService = null)
    {
        _configManager = configManager;
        _modelManager = modelManager;
        _logger = logger;
        _embeddingService = embeddingService;
        _rerankService = rerankService;
        _chatService = chatService;
        _probeService = probeService ?? new HardwareAccelerationProbeService();
    }

    /// <summary>
    /// 获取配置
    /// </summary>
    [HttpGet]
    public ActionResult<ObsidianRagConfig> Get()
    {
        var rawConfig = _configManager.Load();
        // 深拷贝配置对象，避免脱敏处理污染 ConfigManager 内存中的真实运行配置
        var serialized = JsonSerializer.Serialize(rawConfig);
        var config = JsonSerializer.Deserialize<ObsidianRagConfig>(serialized) ?? rawConfig;

        // 统一脱敏标记：设置 ApiKeyConfigured，供前端安全感知；对敏感凭据统一执行掩码脱敏
        if (config.Chat?.OpenAiApi != null)
        {
            config.Chat.OpenAiApi.ApiKeyConfigured = !string.IsNullOrWhiteSpace(config.Chat.OpenAiApi.ApiKey);
            config.Chat.OpenAiApi.ApiKey = MaskKey(config.Chat.OpenAiApi.ApiKey);
        }

        if (config.Service != null)
        {
            config.Service.ApiKey = MaskKey(config.Service.ApiKey);
            config.Service.DiagKey = MaskKey(config.Service.DiagKey);
        }

        if (config.Embedding != null && !string.IsNullOrWhiteSpace(config.Embedding.ApiKey))
        {
            config.Embedding.ApiKey = MaskKey(config.Embedding.ApiKey);
        }

        if (config.Rerank != null && !string.IsNullOrWhiteSpace(config.Rerank.ApiKey))
        {
            config.Rerank.ApiKey = MaskKey(config.Rerank.ApiKey);
        }

        // 获取模型根目录（优先使用顶层配置）
        var modelsRoot = !string.IsNullOrEmpty(config.ModelsRootPath)
            ? config.ModelsRootPath
            : Path.Combine(config.DataPath ?? "data", "models");

        // 填充重排模型的完整路径
        if (!string.IsNullOrEmpty(config.Rerank?.CurrentModel))
        {
            var rerankModelPath = Path.Combine(
                modelsRoot,
                config.Rerank.CurrentModel,
                "model.onnx");

            // 如果 ModelPath 为空或不存在，使用计算出的路径
            if (string.IsNullOrEmpty(config.Rerank.ModelPath) || !System.IO.File.Exists(config.Rerank.ModelPath))
            {
                config.Rerank.ModelPath = rerankModelPath;
            }
        }

        // 确保 ModelsRootPath 有值
        if (string.IsNullOrEmpty(config.ModelsRootPath))
        {
            config.ModelsRootPath = modelsRoot;
        }

        return Ok(config);
    }

    /// <summary>
    /// 获取全平台硬件加速状态（探测 GPU 列表、推荐方案与支持的后端，支持沙箱隔离与熔断降级）
    /// </summary>
    [AllowAnonymous]
    [HttpGet("hardware-acceleration/status")]
    public ActionResult<HardwareAccelerationStatusResult> GetHardwareAccelerationStatus([FromQuery] bool refresh = false)
    {
        try
        {
            var config = _configManager.Load();
            var customPath = config?.Embedding?.Acceleration?.CustomLibraryPath ?? config?.Embedding?.CudaLibraryPath;
            var deviceId = config?.Embedding?.Acceleration?.DeviceId ?? config?.Embedding?.CudaDeviceId ?? 0;
            var result = _probeService.GetStatus(customPath, deviceId, forceRefresh: refresh);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取硬件加速状态异常");
            return Ok(new HardwareAccelerationStatusResult
            {
                ActiveBackend = "Cpu",
                ActiveBackendReason = $"硬件探测异常: {ex.Message}，已安全降级为 CPU",
                RecommendedBackend = "Auto",
                Message = $"硬件探测异常: {ex.Message}，已安全降级为 CPU",
                AvailableBackends = new List<AvailableBackendInfo>
                {
                    new() { Backend = "Auto", Name = "自动感知协商 (智能推荐)", Supported = true, Reason = "已降级为 CPU 运算" },
                    new() { Backend = "Cpu", Name = "纯 CPU 运算", Supported = true, Reason = "多核 CPU 确定性推理 (无显卡依赖与低能耗)" }
                }
            });
        }
    }

    /// <summary>
    /// 获取 CUDA/GPU 可用性状态（向后兼容接口）
    /// </summary>
    [AllowAnonymous]
    [HttpGet("cuda-availability")]
    public ActionResult<CudaAvailability> GetCudaAvailability()
    {
        try
        {
            var config = _configManager.Load();
            var customPath = config?.Embedding?.Acceleration?.CustomLibraryPath ?? config?.Embedding?.CudaLibraryPath;
            var deviceId = config?.Embedding?.Acceleration?.DeviceId ?? config?.Embedding?.CudaDeviceId ?? 0;
            var result = _probeService.GetCudaAvailability(customPath, deviceId);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取 CUDA 可用性状态异常");
            return Ok(new CudaAvailability
            {
                IsAvailable = false,
                Strategy = "None",
                Message = $"硬件探测异常: {ex.Message}，已安全降级为 CPU"
            });
        }
    }

    private static HardwareAccelerationStatusResult CheckHardwareAccelerationStatus(string? configuredPath = null, int deviceId = 0)
    {
        return HardwareAccelerationProbeWorker.ExecuteProbe(configuredPath, deviceId);
    }

    private static CudaAvailability CheckCudaAvailability(string? configuredPath = null, int deviceId = 0)
    {
        var status = HardwareAccelerationProbeWorker.ExecuteProbe(configuredPath, deviceId);
        var cudaBackend = status.AvailableBackends.Find(b => string.Equals(b.Backend, "Cuda", StringComparison.OrdinalIgnoreCase));
        var isAvailable = cudaBackend?.Supported == true;
        return new CudaAvailability
        {
            IsAvailable = isAvailable,
            GpuName = status.PrimaryGpu?.Name,
            Strategy = isAvailable ? "NativeCuda" : "None",
            DetectedPath = configuredPath,
            HasPortableCuda = status.HasNvidiaGpu,
            Message = cudaBackend?.Reason ?? status.Message
        };
    }

    /// <summary>
    /// 保存配置
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<SettingsSaveResult>> Save([FromBody] ObsidianRagConfig config)
    {
        var current = _configManager.Load();
        var embeddingIdentityChanged = !string.Equals(current.Embedding.Mode, config.Embedding.Mode, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(current.Embedding.ModelName, config.Embedding.ModelName, StringComparison.Ordinal)
            || current.Embedding.ApiDimension != config.Embedding.ApiDimension;
        var embeddingRuntimeChanged = embeddingIdentityChanged
            || !string.Equals(current.Embedding.ApiBaseUrl, config.Embedding.ApiBaseUrl, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(current.Embedding.ApiKey, config.Embedding.ApiKey, StringComparison.Ordinal)
            || !string.Equals(current.Embedding.ModelPath, config.Embedding.ModelPath, StringComparison.OrdinalIgnoreCase)
            || current.Embedding.UseCuda != config.Embedding.UseCuda
            || current.Embedding.Acceleration.Backend != config.Embedding.Acceleration.Backend
            || current.Embedding.Acceleration.DeviceId != config.Embedding.Acceleration.DeviceId
            || !string.Equals(current.Embedding.Acceleration.CustomLibraryPath, config.Embedding.Acceleration.CustomLibraryPath, StringComparison.OrdinalIgnoreCase)
            || current.Embedding.BatchSize != config.Embedding.BatchSize
            || current.Embedding.MaxSequenceLength != config.Embedding.MaxSequenceLength;
        var rerankRuntimeChanged = !string.Equals(current.Rerank.Mode, config.Rerank.Mode, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(current.Rerank.ModelName, config.Rerank.ModelName, StringComparison.Ordinal)
            || !string.Equals(current.Rerank.ApiBaseUrl, config.Rerank.ApiBaseUrl, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(current.Rerank.ApiKey, config.Rerank.ApiKey, StringComparison.Ordinal)
            || !string.Equals(current.Rerank.ModelPath, config.Rerank.ModelPath, StringComparison.OrdinalIgnoreCase)
            || current.Rerank.UseCuda != config.Rerank.UseCuda
            || current.Rerank.Acceleration.Backend != config.Rerank.Acceleration.Backend
            || current.Rerank.Acceleration.DeviceId != config.Rerank.Acceleration.DeviceId
            || !string.Equals(current.Rerank.Acceleration.CustomLibraryPath, config.Rerank.Acceleration.CustomLibraryPath, StringComparison.OrdinalIgnoreCase)
            || current.Rerank.Enabled != config.Rerank.Enabled;

        var networkBindingChanged = current.Service.Port != config.Service.Port
            || current.Service.AllowNetworkAccess != config.Service.AllowNetworkAccess
            || current.Service.EnableHttpService != config.Service.EnableHttpService;

        // 关键保护：数据源由 Sources 模块独立管理，设置保存不能覆盖可能已并发修改的 Sources 列表
        config.Sources = current.Sources;

        // 关键保护：前端脱敏留空或包含掩码标记（****）时，若旧配置已有有效 API Key，则继承旧 Key 防止意外被覆盖为清空
        if (config.Chat?.OpenAiApi != null)
        {
            if (string.Equals(config.Chat.OpenAiApi.ApiKey, "__CLEAR__", StringComparison.OrdinalIgnoreCase))
            {
                config.Chat.OpenAiApi.ApiKey = string.Empty;
            }
            else if ((string.IsNullOrWhiteSpace(config.Chat.OpenAiApi.ApiKey) || config.Chat.OpenAiApi.ApiKey.Contains("****")) && !string.IsNullOrWhiteSpace(current.Chat?.OpenAiApi?.ApiKey))
            {
                config.Chat.OpenAiApi.ApiKey = current.Chat.OpenAiApi.ApiKey;
            }
        }

        if (config.Service != null && current.Service != null)
        {
            if (config.Service.ApiKey?.Contains("****") == true)
            {
                config.Service.ApiKey = current.Service.ApiKey;
            }

            if (config.Service.DiagKey?.Contains("****") == true)
            {
                config.Service.DiagKey = current.Service.DiagKey;
            }
        }

        if (config.Embedding != null && current.Embedding != null && config.Embedding.ApiKey?.Contains("****") == true)
        {
            config.Embedding.ApiKey = current.Embedding.ApiKey;
        }

        if (config.Rerank != null && current.Rerank != null && config.Rerank.ApiKey?.Contains("****") == true)
        {
            config.Rerank.ApiKey = current.Rerank.ApiKey;
        }

        _configManager.Save(config);

        // 热更新模型驻留与滑动过期策略（无需重启服务即时生效）
        if (_embeddingService is IModelRetentionPolicyTarget embeddingRetention)
        {
            embeddingRetention.UpdateRetentionPolicy(config);
        }
        if (_rerankService is IModelRetentionPolicyTarget rerankRetention)
        {
            rerankRetention.UpdateRetentionPolicy(config);
        }

        // 热重载嵌入模型配置（状态机两阶段热重载）
        if (embeddingRuntimeChanged && _embeddingService is IModelConfigurableTarget embeddingConfigurable)
        {
            await embeddingConfigurable.ApplyConfigurationAsync(config.Embedding);
        }

        // 热重载重排模型配置（状态机两阶段热重载）
        if (rerankRuntimeChanged && _rerankService is IRerankConfigurableTarget rerankConfigurable)
        {
            await rerankConfigurable.ApplyConfigurationAsync(config.Rerank);
        }

        // 热重载聊天模型配置（支持 LocalGguf 与 OpenAiApi 双向热切）
        _chatService?.ReloadConfiguration();

        var message = networkBindingChanged
            ? "配置已保存，网络端口/外网设置需重启服务后生效"
            : (embeddingIdentityChanged
                ? "配置已即时热生效。嵌入模型已切换，检索前建议重建向量索引"
                : "配置已保存并即时生效");

        return Ok(new SettingsSaveResult
        {
            Message = message,
            RequiresRestart = networkBindingChanged,
            IndexRequiresRebuild = embeddingIdentityChanged
        });
    }

    /// <summary>
    /// 更新模型保存路径（可选迁移已有模型）
    /// </summary>
    [HttpPatch("models-path")]
    public async Task<ActionResult> UpdateModelsPath([FromBody] UpdateModelsPathRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ModelsPath))
        {
            return BadRequest(new { error = "模型路径不能为空" });
        }

        _logger.LogInformation("更新模型路径: {Path}, 迁移: {Migrate}", request.ModelsPath, request.MigrateExisting);

        var result = await _modelManager.SetModelsPathAsync(
            request.ModelsPath, request.MigrateExisting);

        if (!result.Success)
        {
            return BadRequest(new { error = result.Error });
        }

        // 卸载当前加载的模型，下次使用时将自动加载新路径的模型
        _embeddingService?.UnloadModel();
        _rerankService?.UnloadModel();
        _chatService?.UnloadModel();
        _logger.LogInformation("已卸载嵌入模型、重排模型和对话模型，新模型将在下次使用时自动加载");

        return Ok(new
        {
            message = $"模型路径已更新: {request.ModelsPath}",
            migratedCount = result.Migrated.Count,
            migrated = result.Migrated,
            embeddingModels = result.EmbeddingModels,
            rerankModels = result.RerankModels
        });
    }

    /// <summary>
    /// 对话模型自适应性能规划测算（供设置看板与实时画像预览）
    /// </summary>
    [HttpPost("chat-performance/preview")]
    public ActionResult<ChatPerformanceProfile> PreviewChatPerformance([FromBody] ChatPerformancePlanningRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ModelPath) && !string.IsNullOrWhiteSpace(request.ModelName))
        {
            var chatModel = _modelManager.GetChatModels().FirstOrDefault(m => string.Equals(m.Name, request.ModelName, StringComparison.OrdinalIgnoreCase));
            if (chatModel != null && !string.IsNullOrWhiteSpace(chatModel.LocalPath))
            {
                var resolved = ModelManager.ResolveModelFilePath(chatModel.LocalPath, chatModel.ActiveVariantFileName);
                request = request with { ModelPath = !string.IsNullOrWhiteSpace(resolved) ? resolved : chatModel.LocalPath };
            }
        }

        var planner = new ChatPerformancePlanner();
        var profile = planner.PlanProfile(request);
        return Ok(profile);
    }

    /// <summary>
    /// 验证 Hugging Face Access Token 有效性与身份
    /// </summary>
    [HttpPost("verify-hf-token")]
    public async Task<ActionResult> VerifyHfToken([FromBody] VerifyHfTokenRequest request, CancellationToken cancellationToken = default)
    {
        var token = request.Token?.Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            return BadRequest(new { valid = false, message = "Token 不能为空" });
        }

        var endpoint = !string.IsNullOrWhiteSpace(request.Endpoint)
            ? request.Endpoint.Trim().TrimEnd('/')
            : "https://hf-mirror.com";

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ReferenceRAG/2.0 (TokenValidator)");
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            // 优先尝试提供的端点，若失败尝试官方端点
            var urls = new List<string> { $"{endpoint}/api/whoami-v2" };
            if (!urls.Contains("https://huggingface.co/api/whoami-v2", StringComparer.OrdinalIgnoreCase))
            {
                urls.Add("https://huggingface.co/api/whoami-v2");
            }

            HttpResponseMessage? response = null;
            string? responseBody = null;

            foreach (var url in urls)
            {
                try
                {
                    response = await client.GetAsync(url, cancellationToken);
                    responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                    if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.Unauthorized || response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                    {
                        break;
                    }
                }
                catch
                {
                    // 尝试下一个端点
                }
            }

            if (response == null)
            {
                return Ok(new { valid = false, message = "网络请求失败，无法连接至 Hugging Face 或镜像源端点" });
            }

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                return Ok(new { valid = false, message = "Token 认证失败 (HTTP 401/403)：Token 无效、已过期或缺少读取权限" });
            }

            if (!response.IsSuccessStatusCode)
            {
                return Ok(new { valid = false, message = $"端点返回异常状态码: {(int)response.StatusCode} {response.ReasonPhrase}" });
            }

            // 解析 whoami 返回
            using var doc = System.Text.Json.JsonDocument.Parse(responseBody ?? "{}");
            var root = doc.RootElement;
            var username = root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : "Authenticated User";
            var role = "read";
            if (root.TryGetProperty("auth", out var authProp) &&
                authProp.TryGetProperty("accessToken", out var atProp) &&
                atProp.TryGetProperty("role", out var roleProp))
            {
                role = roleProp.GetString() ?? "read";
            }

            return Ok(new
            {
                valid = true,
                username,
                role,
                message = $"Token 验证成功！授权用户: {username} (权限: {role})"
            });
        }
        catch (OperationCanceledException)
        {
            return Ok(new { valid = false, message = "验证请求超时，请检查网络连接" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "验证 HF Token 异常");
            return Ok(new { valid = false, message = $"验证异常: {ex.Message}" });
        }
    }

    private static string MaskKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return "";
        if (key.Length <= 4) return "****";
        return $"{key[..2]}****{key[^2..]}";
    }
}

/// <summary>
/// 验证 Hugging Face Token 请求
/// </summary>
public class VerifyHfTokenRequest
{
    public string? Token { get; set; }
    public string? Endpoint { get; set; }
}

/// <summary>
/// 更新模型路径请求
/// </summary>
public class UpdateModelsPathRequest
{
    /// <summary>
    /// 新的模型保存路径
    /// </summary>
    public string ModelsPath { get; set; } = "";

    /// <summary>
    /// 是否迁移已有模型到新路径
    /// </summary>
    public bool MigrateExisting { get; set; } = false;
}

/// <summary>
/// 设置保存响应模型
/// </summary>
public class SettingsSaveResult
{
    /// <summary>
    /// 提示消息
    /// </summary>
    public string Message { get; set; } = "";

    /// <summary>
    /// 是否需要重启服务（仅服务端口或外网绑定变更时为 true）
    /// </summary>
    public bool RequiresRestart { get; set; }

    /// <summary>
    /// 嵌入模型或维度变更时为 true，建议重建向量索引
    /// </summary>
    public bool IndexRequiresRebuild { get; set; }
}

