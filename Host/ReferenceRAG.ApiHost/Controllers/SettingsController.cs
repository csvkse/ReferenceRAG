using ReferenceRAG.Core.Features.ModelManagement.Services;
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
using ReferenceRAG.Business.Features.Search.Cognitive;

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
    private readonly ICognitiveEvidenceEvaluatorFactory? _cognitiveFactory;

    public SettingsController(
        ConfigManager configManager,
        IModelManager modelManager,
        ILogger<SettingsController> logger,
        IEmbeddingService? embeddingService = null,
        IRerankService? rerankService = null,
        MafChatService? chatService = null,
        IHardwareAccelerationProbeService? probeService = null,
        ICognitiveEvidenceEvaluatorFactory? cognitiveFactory = null)
    {
        _configManager = configManager;
        _modelManager = modelManager;
        _logger = logger;
        _embeddingService = embeddingService;
        _rerankService = rerankService;
        _chatService = chatService;
        _probeService = probeService ?? new HardwareAccelerationProbeService();
        _cognitiveFactory = cognitiveFactory;
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

        if (config.SystemOne != null)
        {
            config.SystemOne.ApiKeyConfigured = !string.IsNullOrWhiteSpace(rawConfig.SystemOne?.ApiKey);
            config.SystemOne.ApiKey = MaskKey(config.SystemOne.ApiKey);
        }

        if (config.Chat?.SystemOne != null)
        {
            config.Chat.SystemOne.ApiKeyConfigured = !string.IsNullOrWhiteSpace(rawConfig.Chat?.SystemOne?.ApiKey);
            config.Chat.SystemOne.ApiKey = MaskKey(config.Chat.SystemOne.ApiKey);
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
        SynchronizeModelUrnBindings(config, current);
        if (current.Indexing.EmbeddingEnabled != config.Indexing.EmbeddingEnabled)
            return Conflict(new { error = "向量索引启用状态不能在运行时切换；请停止服务后修改配置并重启。" });
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

        if (config.SystemOne != null && current.SystemOne != null)
        {
            if (string.Equals(config.SystemOne.ApiKey, "__CLEAR__", StringComparison.OrdinalIgnoreCase))
            {
                config.SystemOne.ApiKey = string.Empty;
            }
            else if ((string.IsNullOrWhiteSpace(config.SystemOne.ApiKey) || config.SystemOne.ApiKey.Contains("****")) && !string.IsNullOrWhiteSpace(current.SystemOne.ApiKey))
            {
                config.SystemOne.ApiKey = current.SystemOne.ApiKey;
            }
        }

        if (config.Chat?.SystemOne != null && current.Chat?.SystemOne != null)
        {
            if (string.Equals(config.Chat.SystemOne.ApiKey, "__CLEAR__", StringComparison.OrdinalIgnoreCase))
            {
                config.Chat.SystemOne.ApiKey = string.Empty;
            }
            else if ((string.IsNullOrWhiteSpace(config.Chat.SystemOne.ApiKey) || config.Chat.SystemOne.ApiKey.Contains("****")) && !string.IsNullOrWhiteSpace(current.Chat.SystemOne.ApiKey))
            {
                config.Chat.SystemOne.ApiKey = current.Chat.SystemOne.ApiKey;
            }
        }

        if (config.SystemOne != null)
        {
            config.Chat ??= new ChatModelConfig();
            config.Chat.SystemOne = config.SystemOne;
        }
        else if (config.Chat?.SystemOne != null)
        {
            config.SystemOne = config.Chat.SystemOne;
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

    private void SynchronizeModelUrnBindings(ObsidianRagConfig config, ObsidianRagConfig current)
    {
        var resolver = new ModelAssetResolver(_configManager, _modelManager);

        // 1. Chat Model URN 同步
        if (config.Chat != null && !string.IsNullOrEmpty(config.Chat.ActiveModelUrn))
        {
            var resolved = resolver.Resolve(config.Chat.ActiveModelUrn, "chat");
            if (resolved != null)
            {
                if (resolved.IsLocal)
                {
                    config.Chat.ActiveBackend = "LocalGguf";
                    config.Chat.LocalGguf ??= new LocalGgufChatProfile();
                    if (string.IsNullOrWhiteSpace(config.Chat.LocalGguf.ModelName) || config.Chat.ActiveModelUrn != "local:qwen2.5-7b-instruct-q4_k_m")
                    {
                        config.Chat.LocalGguf.ModelName = resolved.Urn.ModelName;
                    }
                    if (!string.IsNullOrEmpty(resolved.LocalModelPath))
                        config.Chat.LocalGguf.ModelPath = resolved.LocalModelPath;
                    if (!string.IsNullOrEmpty(resolved.LocalVariant))
                        config.Chat.LocalGguf.Variant = resolved.LocalVariant;
                }
                else
                {
                    config.Chat.ActiveBackend = "OpenAiApi";
                    config.Chat.OpenAiApi ??= new OpenAiApiChatProfile();
                    config.Chat.OpenAiApi.Endpoint = resolved.BaseUrl ?? "https://api.openai.com/v1";
                    config.Chat.OpenAiApi.ApiKey = resolved.ApiKey ?? string.Empty;
                    config.Chat.OpenAiApi.Model = resolved.RemoteModelId ?? "gpt-4o-mini";
                }
            }
        }

        // 2. Embedding Model URN 同步
        if (config.Embedding != null && !string.IsNullOrEmpty(config.Embedding.ActiveModelUrn))
        {
            var resolved = resolver.Resolve(config.Embedding.ActiveModelUrn, "embedding");
            if (resolved != null)
            {
                if (resolved.IsLocal)
                {
                    config.Embedding.ActiveBackend = "LocalOnnx";
                    config.Embedding.Mode = "onnx";
                    config.Embedding.LocalOnnx ??= new LocalOnnxEmbeddingProfile();
                    if (string.IsNullOrWhiteSpace(config.Embedding.LocalOnnx.ModelName) || config.Embedding.ActiveModelUrn != "local:bge-small-zh-v1.5")
                    {
                        config.Embedding.LocalOnnx.ModelName = resolved.Urn.ModelName;
                    }
                    if (!string.IsNullOrEmpty(resolved.LocalModelPath))
                        config.Embedding.LocalOnnx.ModelPath = resolved.LocalModelPath;
                }
                else
                {
                    config.Embedding.ActiveBackend = "OpenAiApi";
                    config.Embedding.Mode = "openai";
                    config.Embedding.OpenAiApi ??= new OpenAiApiEmbeddingProfile();
                    config.Embedding.OpenAiApi.BaseUrl = resolved.BaseUrl;
                    config.Embedding.OpenAiApi.ApiKey = resolved.ApiKey;
                    config.Embedding.OpenAiApi.ModelName = resolved.RemoteModelId ?? "text-embedding-3-small";
                    config.Embedding.ApiBaseUrl = resolved.BaseUrl;
                    config.Embedding.ApiKey = resolved.ApiKey;
                    config.Embedding.ApiDimension = resolved.Dimension > 0 ? resolved.Dimension : 1024;
                }
            }
        }

        // 3. Rerank Model URN 同步
        if (config.Rerank != null && !string.IsNullOrEmpty(config.Rerank.ActiveModelUrn))
        {
            var resolved = resolver.Resolve(config.Rerank.ActiveModelUrn, "rerank");
            if (resolved != null)
            {
                if (resolved.IsLocal)
                {
                    config.Rerank.ActiveBackend = "LocalOnnx";
                    config.Rerank.Mode = "onnx";
                    config.Rerank.LocalOnnx ??= new LocalOnnxRerankProfile();
                    if (string.IsNullOrWhiteSpace(config.Rerank.LocalOnnx.ModelName) || config.Rerank.ActiveModelUrn != "local:bge-reranker-base")
                    {
                        config.Rerank.LocalOnnx.ModelName = resolved.Urn.ModelName;
                        config.Rerank.LocalOnnx.CurrentModel = resolved.Urn.ModelName;
                    }
                    if (!string.IsNullOrEmpty(resolved.LocalModelPath))
                        config.Rerank.LocalOnnx.ModelPath = resolved.LocalModelPath;
                }
                else
                {
                    config.Rerank.ActiveBackend = "OpenAiApi";
                    config.Rerank.Mode = "openai";
                    config.Rerank.OpenAiApi ??= new OpenAiApiRerankProfile();
                    config.Rerank.OpenAiApi.BaseUrl = resolved.BaseUrl;
                    config.Rerank.OpenAiApi.ApiKey = resolved.ApiKey;
                    config.Rerank.OpenAiApi.ModelName = resolved.RemoteModelId ?? "bge-reranker-large";
                    config.Rerank.ApiBaseUrl = resolved.BaseUrl;
                    config.Rerank.ApiKey = resolved.ApiKey;
                }
            }
        }
    }

    /// <summary>
    /// 测试 TypeSafe AI System One 极速决策引擎连通性与意图识别
    /// </summary>
    [HttpPost("test-systemone")]
    public async Task<ActionResult> TestSystemOne([FromBody] TestSystemOneRequest request, CancellationToken ct)
    {
        var current = _configManager.Load();
        var baseUrl = !string.IsNullOrWhiteSpace(request.BaseUrl) ? request.BaseUrl.Trim() : (current.SystemOne?.BaseUrl ?? "https://api.typesafe.ai/v1/systemone");
        var model = !string.IsNullOrWhiteSpace(request.Model) ? request.Model.Trim() : (current.SystemOne?.Model ?? "jev-latest");
        var apiKey = request.ApiKey?.Trim();
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Contains("****"))
        {
            apiKey = current.SystemOne?.ApiKey ?? current.Chat?.SystemOne?.ApiKey;
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return BadRequest(new { success = false, message = "未提供或未配置有效 API Key" });
        }

        var query = !string.IsNullOrWhiteSpace(request.Query) ? request.Query.Trim() : "帮我查一下知识库里的技术架构设计规范";
        var threshold = request.Threshold ?? current.SystemOne?.RetrievalConfidenceThreshold ?? 0.65f;
        var proxyPolicy = request.ProxyPolicy ?? current.SystemOne?.ProxyPolicy ?? ProxyPolicy.Inherit;
        var customProxyUrl = !string.IsNullOrWhiteSpace(request.CustomProxyUrl) ? request.CustomProxyUrl : current.SystemOne?.CustomProxyUrl;
        var proxy = ReferenceRAG.Infrastructure.Features.Network.DynamicWebProxyAdapter.ResolveProxy(current, proxyPolicy, customProxyUrl);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var client = new ReferenceRAG.Infrastructure.Features.Cognitive.JevSystemOneClient(
                apiKey: apiKey,
                baseUrl: baseUrl,
                model: model,
                timeoutSeconds: 8,
                proxy: proxy);

            var engine = new ReferenceRAG.Infrastructure.Features.Cognitive.JevDecisionEngine(
                client: client,
                enabled: true,
                retrievalThreshold: threshold);

            var classification = await engine.ClassifyIntentAsync(query, ct);
            sw.Stop();

            if (classification.Rationale != null && classification.Rationale.StartsWith("决策异常兜底:"))
            {
                return Ok(new
                {
                    success = false,
                    latencyMs = sw.ElapsedMilliseconds,
                    error = classification.Rationale
                });
            }

            return Ok(new
            {
                success = true,
                latencyMs = sw.ElapsedMilliseconds,
                intent = classification.PrimaryIntent,
                requiresRetrieval = classification.RequiresRetrieval,
                confidence = classification.Confidence,
                retrievalProbability = classification.RetrievalProbability,
                rationale = classification.Rationale,
                model = client.ModelName
            });
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning(ex, "[SettingsController] System One 连通性测试失败: {Message}", ex.Message);
            return Ok(new
            {
                success = false,
                latencyMs = sw.ElapsedMilliseconds,
                error = ex.Message
            });
        }
    }

    /// <summary>
    /// 测试代理服务器连通性
    /// </summary>
    [HttpPost("test-proxy")]
    public async Task<ActionResult> TestProxy([FromBody] TestProxyRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ProxyUrl))
        {
            return BadRequest(new { success = false, message = "未提供代理服务器地址" });
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var proxyConfig = new ProxyConfig
            {
                Enabled = true,
                ProxyUrl = request.ProxyUrl.Trim(),
                Username = request.Username,
                Password = request.Password
            };
            var proxy = new ReferenceRAG.Infrastructure.Features.Network.DynamicWebProxyAdapter(() => proxyConfig);
            using var handler = new SocketsHttpHandler
            {
                Proxy = proxy,
                UseProxy = true,
                ConnectTimeout = TimeSpan.FromSeconds(5)
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };

            var testTarget = !string.IsNullOrWhiteSpace(request.TestUrl) ? request.TestUrl.Trim() : "https://api.typesafe.ai/health";
            using var resp = await client.GetAsync(testTarget, HttpCompletionOption.ResponseHeadersRead, ct);
            sw.Stop();
            return Ok(new
            {
                success = true,
                statusCode = (int)resp.StatusCode,
                latencyMs = sw.ElapsedMilliseconds,
                message = $"代理连通成功，目标响应: {(int)resp.StatusCode} {resp.ReasonPhrase}"
            });
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Ok(new
            {
                success = false,
                latencyMs = sw.ElapsedMilliseconds,
                error = ex.Message
            });
        }
    }

    /// <summary>
    /// 测试认知增强词法检索后端连通性与评估打分
    /// </summary>
    [HttpPost("test-cognitive-backend")]
    public async Task<ActionResult> TestCognitiveBackend(
        [FromBody] TestCognitiveBackendRequest request,
        CancellationToken ct)
    {
        if (request.Backend == CognitiveLexicalBackend.Disabled)
        {
            return Ok(new
            {
                success = true,
                backend = "Disabled",
                message = "认知增强已禁用，当前使用纯本地 FTS5 BM25 + DPP 检索",
                isAvailable = true,
                durationMs = 0
            });
        }

        if (_cognitiveFactory == null)
        {
            return Ok(new
            {
                success = false,
                backend = request.Backend.ToString(),
                message = "认知证据评估器工厂未注册",
                isAvailable = false
            });
        }

        var config = new CognitiveLexicalConfig
        {
            Backend = request.Backend,
            AcceptanceThreshold = request.AcceptanceThreshold,
            EnableFallback = false
        };

        var evaluator = _cognitiveFactory.GetEvaluator(config);
        if (evaluator == null || !evaluator.IsAvailable)
        {
            return Ok(new
            {
                success = false,
                backend = request.Backend.ToString(),
                message = $"后端 {request.Backend} 当前未就绪或未配置有效模型/密钥",
                isAvailable = false
            });
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var testQuery = "金庸武侠小说代表作品与主要人物";
            var testCandidates = new List<CandidateEvaluationItem>
            {
                new("chunk-probe-1", "射雕英雄传", "", "金庸创作了十四部经典的武侠小说，包括《射雕英雄传》《天龙八部》等。", 12.5),
                new("chunk-probe-2", "今日天气预报", "", "今天全国多地晴转多云，气温适宜，外出游玩请注意防晒。", 1.2)
            };

            var evals = await evaluator.EvaluateBatchAsync(testQuery, testCandidates, Array.Empty<CandidateEvaluationItem>(), ct);
            sw.Stop();

            var sampleResults = testCandidates.Select(c =>
            {
                evals.TryGetValue(c.ChunkId, out var r);
                return new
                {
                    chunkId = c.ChunkId,
                    title = c.Title,
                    score = r?.NormalizedScore ?? 0.0,
                    isAccepted = r?.IsAccepted ?? false,
                    reason = r?.Reason ?? "无评估结果"
                };
            }).ToList();

            return Ok(new
            {
                success = true,
                backend = evaluator.BackendName,
                message = $"后端 {evaluator.BackendName} 运行正常 (耗时 {sw.ElapsedMilliseconds}ms)",
                isAvailable = true,
                durationMs = sw.ElapsedMilliseconds,
                sampleResults
            });
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning(ex, "[SettingsController] 认知后端 {Backend} 测试异常: {Message}", request.Backend, ex.Message);
            return Ok(new
            {
                success = false,
                backend = evaluator.BackendName,
                message = $"认知后端测试失败: {ex.Message}",
                isAvailable = false,
                durationMs = sw.ElapsedMilliseconds
            });
        }
    }
}

/// <summary>
/// 测试认知增强后端请求参数
/// </summary>
public class TestCognitiveBackendRequest
{
    public CognitiveLexicalBackend Backend { get; set; } = CognitiveLexicalBackend.Disabled;
    public float AcceptanceThreshold { get; set; } = 0.50f;
}

/// <summary>
/// 测试 TypeSafe AI System One 请求参数
/// </summary>
public class TestSystemOneRequest
{
    public string? BaseUrl { get; set; }
    public string? ApiKey { get; set; }
    public string? Model { get; set; }
    public string? Query { get; set; }
    public float? Threshold { get; set; }
    public ProxyPolicy? ProxyPolicy { get; set; }
    public string? CustomProxyUrl { get; set; }
}

/// <summary>
/// 测试代理请求参数
/// </summary>
public class TestProxyRequest
{
    public string? ProxyUrl { get; set; }
    public string? TestUrl { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
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
