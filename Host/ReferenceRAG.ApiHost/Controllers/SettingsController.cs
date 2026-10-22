using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Core.Features.HardwareAcceleration.Detection;
using ReferenceRAG.Core.Features.HardwareAcceleration.Injection;
using ReferenceRAG.Core.Features.HardwareAcceleration.Scanning;
using ReferenceRAG.Core.Features.HardwareAcceleration.Models;
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

    public SettingsController(
        ConfigManager configManager,
        IModelManager modelManager,
        ILogger<SettingsController> logger,
        IEmbeddingService? embeddingService = null,
        IRerankService? rerankService = null,
        MafChatService? chatService = null)
    {
        _configManager = configManager;
        _modelManager = modelManager;
        _logger = logger;
        _embeddingService = embeddingService;
        _rerankService = rerankService;
        _chatService = chatService;
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
    /// 获取全平台硬件加速状态（探测 GPU 列表、推荐方案与支持的后端）
    /// </summary>
    [HttpGet("hardware-acceleration/status")]
    public ActionResult<HardwareAccelerationStatusResult> GetHardwareAccelerationStatus()
    {
        try
        {
            var config = _configManager.Load();
            var customPath = config?.Embedding?.Acceleration?.CustomLibraryPath ?? config?.Embedding?.CudaLibraryPath;
            var deviceId = config?.Embedding?.Acceleration?.DeviceId ?? config?.Embedding?.CudaDeviceId ?? 0;
            var result = CheckHardwareAccelerationStatus(customPath, deviceId);
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
    [HttpGet("cuda-availability")]
    public ActionResult<CudaAvailability> GetCudaAvailability()
    {
        try
        {
            var config = _configManager.Load();
            var customPath = config?.Embedding?.Acceleration?.CustomLibraryPath ?? config?.Embedding?.CudaLibraryPath;
            var deviceId = config?.Embedding?.Acceleration?.DeviceId ?? config?.Embedding?.CudaDeviceId ?? 0;
            var result = CheckCudaAvailability(customPath, deviceId);
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
        HardwareContext hwContext;
        try
        {
            var detector = new HardwareDetector();
            hwContext = detector.GetContext();
        }
        catch
        {
            hwContext = new HardwareContext();
        }
        var primaryGpu = hwContext.PrimaryGpu;

        CudaEnvironmentScanResult scanResult;
        try
        {
            var scanner = new CudaEnvironmentScanner(new Win32EnvironmentInjector());
            scanResult = scanner.ScanAndInject(configuredPath);
        }
        catch
        {
            scanResult = new CudaEnvironmentScanResult { Found = false };
        }

        bool hasCuda = false;
        bool hasDirectMl = false;
        try
        {
            hasCuda = OnnxExecutionProviderHelper.HasCuda;
            hasDirectMl = OnnxExecutionProviderHelper.HasDirectMl;
        }
        catch { }

        var availableBackends = new List<AvailableBackendInfo>
        {
            new()
            {
                Backend = "Auto",
                Name = "自动感知协商 (智能推荐)",
                Supported = true,
                Reason = "自适应探查最佳可用算力，发生故障时自动平滑降级"
            }
        };

        // CUDA
        bool cudaSupported = false;
        string cudaReason;
        if (!hwContext.HasNvidiaGpu)
        {
            cudaReason = "未检测到支持的 NVIDIA GPU";
        }
        else if (!scanResult.Found && !hwContext.HasPortableCuda)
        {
            cudaReason = $"检测到 {primaryGpu?.Name}，但系统未找到匹配的 CUDA 12 动态库 (cublasLt64_12.dll)";
        }
        else if (!hasCuda)
        {
            cudaReason = "当前构建未包含 CUDA 原生执行模块 (可选用 NVIDIA 专属满血版)";
        }
        else
        {
            try
            {
                using var testOptions = new Microsoft.ML.OnnxRuntime.SessionOptions();
                if (OnnxExecutionProviderHelper.TryAppendCuda(testOptions, deviceId))
                {
                    cudaSupported = true;
                    cudaReason = $"已就绪 (检测到 {primaryGpu?.Name ?? "NVIDIA GPU"}，已挂载 CUDA 12)";
                }
                else
                {
                    cudaReason = "CUDA 提供者加载失败，请检查驱动版本 (>=528.33)";
                }
            }
            catch (Exception ex)
            {
                cudaReason = $"CUDA 初始化异常: {ex.Message}";
            }
        }

        availableBackends.Add(new AvailableBackendInfo
        {
            Backend = "Cuda",
            Name = "NVIDIA CUDA (满血版)",
            Supported = cudaSupported,
            Reason = cudaReason
        });

        // DirectML
        bool dmlSupported = false;
        string dmlReason;
        if (!hwContext.HasDirectX12)
        {
            dmlReason = "仅支持 Windows 10/11 DirectX 12 环境";
        }
        else if (primaryGpu == null)
        {
            dmlReason = "未检测到支持的图形加速卡";
        }
        else
        {
            try
            {
                using var testOptions = new Microsoft.ML.OnnxRuntime.SessionOptions();
                if (OnnxExecutionProviderHelper.TryAppendDirectMl(testOptions, deviceId))
                {
                    dmlSupported = true;
                    dmlReason = $"已就绪 (基于 DirectX 12 跨平台通用算力: {primaryGpu.Name})";
                }
                else if (hasDirectMl)
                {
                    dmlSupported = true;
                    dmlReason = "已就绪 (DirectML 原生模块已集成)";
                }
                else
                {
                    dmlReason = "当前构建未包含 DirectML 执行提供者 (可选用通用版构建)";
                }
            }
            catch (Exception ex)
            {
                dmlReason = $"DirectML 初始化异常: {ex.Message}";
            }
        }

        availableBackends.Add(new AvailableBackendInfo
        {
            Backend = "DirectML",
            Name = "通用 DirectML (AMD/Intel/通用版)",
            Supported = dmlSupported,
            Reason = dmlReason
        });

        // OpenVINO (Intel Arc 独显 / 酷睿 Iris Xe & UHD 核显)
        bool openVinoSupported = false;
        string openVinoReason;
        if (OnnxExecutionProviderHelper.HasOpenVino)
        {
            try
            {
                using var testOptions = new Microsoft.ML.OnnxRuntime.SessionOptions();
                if (OnnxExecutionProviderHelper.TryAppendOpenVino(testOptions, "GPU", out var vinoErr))
                {
                    openVinoSupported = true;
                    openVinoReason = "已就绪 (Intel GPU / 酷睿核显 OpenVINO 原生硬件加速)";
                }
                else
                {
                    openVinoReason = $"OpenVINO 硬件加速暂未就绪 ({vinoErr})";
                }
            }
            catch (Exception ex)
            {
                var innermost = ex;
                while (innermost.InnerException != null) innermost = innermost.InnerException;
                openVinoReason = $"OpenVINO 初始化异常: {innermost.Message}";
            }
        }
        else
        {
            openVinoReason = "当前构建未包含 OpenVINO 模块 (可选用 Intel 专属版镜像)";
        }

        availableBackends.Add(new AvailableBackendInfo
        {
            Backend = "OpenVino",
            Name = "Intel OpenVINO (Arc独显 / 酷睿核显)",
            Supported = openVinoSupported,
            Reason = openVinoReason
        });

        // CPU
        availableBackends.Add(new AvailableBackendInfo
        {
            Backend = "Cpu",
            Name = "纯 CPU 运算",
            Supported = true,
            Reason = "多核 CPU 确定性推理 (无显卡依赖与低能耗)"
        });

        string recommended = "Auto";
        string active = cudaSupported ? "Cuda"
            : (openVinoSupported ? "OpenVino"
            : (dmlSupported ? "DirectML" : "Cpu"));

        string activeReason = active switch
        {
            "Cuda" => cudaReason,
            "OpenVino" => openVinoReason,
            "DirectML" => dmlReason,
            _ => primaryGpu?.Vendor switch
            {
                GpuVendor.Intel => !string.IsNullOrEmpty(openVinoReason) ? openVinoReason : "多核 CPU 确定性推理",
                GpuVendor.Amd => "Linux 环境 AMD 核显/APU 采用多核 CPU 向量计算 (对话大模型仍支持 Vulkan 核显加速)",
                GpuVendor.Nvidia => !string.IsNullOrEmpty(cudaReason) ? cudaReason : "多核 CPU 确定性推理",
                _ => "多核 CPU 确定性推理"
            }
        };

        string mainMessage;
        if (primaryGpu != null)
        {
            var vramText = primaryGpu.TotalVramGb > 0 ? $" (显存: {primaryGpu.TotalVramGb} GB)" : " (集成核显 / 共享系统内存)";
            if (active != "Cpu")
            {
                mainMessage = $"检测到图形加速硬件: {primaryGpu.Name}{vramText}，已就绪硬件加速: {active}";
            }
            else
            {
                mainMessage = $"检测到图形加速硬件: {primaryGpu.Name}{vramText}，硬件加速提供程序暂未就绪 ({activeReason})，已安全回退至 CPU";
            }
        }
        else if (openVinoSupported)
        {
            mainMessage = "检测到 Intel 硬件加速算力 (已就绪 OpenVINO GPU/核显加速)，推荐加速方案: OpenVino";
        }
        else
        {
            mainMessage = "系统未检测到物理加速显卡，已就绪 CPU 高精度运算";
        }

        return new HardwareAccelerationStatusResult
        {
            DetectedGpus = hwContext.GpuDevices.ToList(),
            PrimaryGpu = primaryGpu,
            RecommendedBackend = recommended,
            ActiveBackend = active,
            ActiveBackendReason = activeReason,
            HasNvidiaGpu = hwContext.HasNvidiaGpu,
            HasDirectX12 = hwContext.HasDirectX12,
            AvailableBackends = availableBackends,
            Message = mainMessage
        };
    }

    private static CudaAvailability CheckCudaAvailability(string? configuredPath = null, int deviceId = 0)
    {
        var status = CheckHardwareAccelerationStatus(configuredPath, deviceId);
        var cudaBackend = status.AvailableBackends.FirstOrDefault(b => b.Backend == "Cuda");
        var isAvailable = cudaBackend?.Supported ?? false;
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

/// <summary>
/// 全平台硬件加速状态模型
/// </summary>
public class HardwareAccelerationStatusResult
{
    /// <summary>
    /// 探测到的所有显卡设备列表
    /// </summary>
    public List<GpuDeviceInfo> DetectedGpus { get; set; } = new();

    /// <summary>
    /// 当前主显卡设备信息
    /// </summary>
    public GpuDeviceInfo? PrimaryGpu { get; set; }

    /// <summary>
    /// 推荐使用的后端标识（Auto, Cuda, DirectML, Cpu）
    /// </summary>
    public string RecommendedBackend { get; set; } = "Auto";

    /// <summary>
    /// 当前生效激活的后端标识
    /// </summary>
    public string ActiveBackend { get; set; } = "Cpu";

    /// <summary>
    /// 当前后端就绪或回退状态诊断原因
    /// </summary>
    public string? ActiveBackendReason { get; set; }

    /// <summary>
    /// 是否存在 NVIDIA 显卡
    /// </summary>
    public bool HasNvidiaGpu { get; set; }

    /// <summary>
    /// 是否支持 DirectX 12 (Windows 10/11)
    /// </summary>
    public bool HasDirectX12 { get; set; }

    /// <summary>
    /// 可用硬件加速后端清单
    /// </summary>
    public List<AvailableBackendInfo> AvailableBackends { get; set; } = new();

    /// <summary>
    /// 综合诊断消息
    /// </summary>
    public string Message { get; set; } = "";
}

/// <summary>
/// 硬件后端详情
/// </summary>
public class AvailableBackendInfo
{
    /// <summary>
    /// 后端标识: Auto, Cuda, DirectML, Vulkan, Rocm, OpenVino, Cpu
    /// </summary>
    public string Backend { get; set; } = "";

    /// <summary>
    /// 显示名称
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// 当前系统与构建环境是否支持
    /// </summary>
    public bool Supported { get; set; }

    /// <summary>
    /// 状态说明或未就绪原因
    /// </summary>
    public string? Reason { get; set; }
}

