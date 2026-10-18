using Microsoft.AspNetCore.Mvc;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Core.Features.HardwareAcceleration.Detection;
using ReferenceRAG.Core.Features.HardwareAcceleration.Injection;
using ReferenceRAG.Core.Features.HardwareAcceleration.Scanning;

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
    private readonly ILogger<SettingsController> _logger;

    public SettingsController(
        ConfigManager configManager,
        IModelManager modelManager,
        ILogger<SettingsController> logger,
        IEmbeddingService? embeddingService = null,
        IRerankService? rerankService = null)
    {
        _configManager = configManager;
        _modelManager = modelManager;
        _logger = logger;
        _embeddingService = embeddingService;
        _rerankService = rerankService;
    }

    /// <summary>
    /// 获取配置
    /// </summary>
    [HttpGet]
    public ActionResult<ObsidianRagConfig> Get()
    {
        var config = _configManager.Load();

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
    /// 获取 CUDA/GPU 可用性状态
    /// </summary>
    [HttpGet("cuda-availability")]
    public ActionResult<CudaAvailability> GetCudaAvailability()
    {
        var config = _configManager.Load();
        var result = CheckCudaAvailability(config?.Embedding?.CudaLibraryPath, config?.Embedding?.CudaDeviceId ?? 0);
        return Ok(result);
    }

    private static CudaAvailability CheckCudaAvailability(string? configuredPath = null, int deviceId = 0)
    {
        var detector = new HardwareDetector();
        var hwContext = detector.GetContext();
        var primaryGpu = hwContext.PrimaryGpu;

        var scanner = new CudaEnvironmentScanner(new Win32EnvironmentInjector());
        var scanResult = scanner.ScanAndInject(configuredPath);

        try
        {
            using var sessionOptions = new Microsoft.ML.OnnxRuntime.SessionOptions();
            sessionOptions.AppendExecutionProvider_CUDA(deviceId);
            return new CudaAvailability
            {
                IsAvailable = true,
                GpuName = primaryGpu?.Name ?? "NVIDIA GPU",
                Strategy = "NativeCuda",
                DetectedPath = scanResult.MainDllDirectory,
                HasPortableCuda = hwContext.HasPortableCuda,
                Message = $"已检测到 {primaryGpu?.Name ?? "NVIDIA GPU"}，已成功激活 CUDA 12 硬件加速"
            };
        }
        catch (Exception ex)
        {
            var msg = hwContext.HasNvidiaGpu
                ? $"检测到 {primaryGpu?.Name ?? "NVIDIA 显卡"}，但无法加载 CUDA 12 运行库 ({ex.Message})"
                : "未检测到支持的 NVIDIA GPU，硬件加速不可用";

            return new CudaAvailability
            {
                IsAvailable = false,
                GpuName = primaryGpu?.Name,
                Strategy = "None",
                HasPortableCuda = hwContext.HasPortableCuda,
                Message = msg
            };
        }
    }

    /// <summary>
    /// 保存配置
    /// </summary>
    [HttpPost]
    public ActionResult Save([FromBody] ObsidianRagConfig config)
    {
        var current = _configManager.Load();
        var embeddingIdentityChanged = !string.Equals(current.Embedding.Mode, config.Embedding.Mode, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(current.Embedding.ModelName, config.Embedding.ModelName, StringComparison.Ordinal)
            || current.Embedding.ApiDimension != config.Embedding.ApiDimension;
        var embeddingRuntimeChanged = embeddingIdentityChanged
            || !string.Equals(current.Embedding.ApiBaseUrl, config.Embedding.ApiBaseUrl, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(current.Embedding.ApiKey, config.Embedding.ApiKey, StringComparison.Ordinal)
            || !string.Equals(current.Embedding.ModelPath, config.Embedding.ModelPath, StringComparison.OrdinalIgnoreCase);
        var modelRuntimeChanged = embeddingRuntimeChanged
            || !string.Equals(current.Rerank.Mode, config.Rerank.Mode, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(current.Rerank.ModelName, config.Rerank.ModelName, StringComparison.Ordinal)
            || !string.Equals(current.Rerank.ApiBaseUrl, config.Rerank.ApiBaseUrl, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(current.Rerank.ApiKey, config.Rerank.ApiKey, StringComparison.Ordinal)
            || !string.Equals(current.Rerank.ModelPath, config.Rerank.ModelPath, StringComparison.OrdinalIgnoreCase);

        // 关键保护：数据源由 Sources 模块独立管理，设置保存不能覆盖可能已并发修改的 Sources 列表
        config.Sources = current.Sources;

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

        return Ok(new
        {
            message = modelRuntimeChanged ? "配置已保存，重启后应用模型服务" : "配置已保存",
            requiresRestart = modelRuntimeChanged,
            indexRequiresRebuild = embeddingIdentityChanged
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
        _logger.LogInformation("已卸载嵌入模型和重排模型，新模型将在下次使用时自动加载");

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
/// CUDA/GPU 可用性状态
/// </summary>
public class CudaAvailability
{
    /// <summary>
    /// CUDA 是否可用
    /// </summary>
    public bool IsAvailable { get; set; }

    /// <summary>
    /// 状态消息
    /// </summary>
    public string Message { get; set; } = "";

    /// <summary>
    /// 检测到的显卡设备名称
    /// </summary>
    public string? GpuName { get; set; }

    /// <summary>
    /// 加速策略名称
    /// </summary>
    public string? Strategy { get; set; }

    /// <summary>
    /// 探测到的 CUDA DLL 所在主路径
    /// </summary>
    public string? DetectedPath { get; set; }

    /// <summary>
    /// 是否存在本地便携式运行库
    /// </summary>
    public bool HasPortableCuda { get; set; }
}
