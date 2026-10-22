using System.ClientModel;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI;
using ReferenceRAG.Business.Features.Chat.Services;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;

namespace ReferenceRAG.Service.Services;

public record SseEvent(string Type, string? Delta = null, string? Message = null);

public class MafChatService : IDisposable, IChatModelCapabilityProvider
{
    private volatile IChatClient? _client;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ConfigManager? _configManager;
    private readonly IModelManager? _modelManager;
    private volatile bool _isLocalGguf;
    private volatile string _activeBackend = "OpenAiApi";
    private volatile string _systemPrompt;
    private volatile string _model = "gpt-4o-mini";
    private readonly AITool[] _tools;
    private readonly ConcurrentDictionary<string, ChatSession> _sessions = new();
    private readonly ILogger<MafChatService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly int _maxSessions;
    private readonly int _maxMessagesPerSession;
    private readonly TimeSpan _sessionIdleTimeout;
    private readonly HttpMessageHandler? _httpMessageHandler;
    private readonly IMultimodalMessageAdapter _multimodalAdapter;
    private readonly CompositeVisionCapabilityMatcher _compositeVisionMatcher = new();
    private DateTime _lastChatActivityUtc = DateTime.UtcNow;
    private int _chatModelIdleTimeoutSeconds = 300;
    private readonly System.Threading.Timer? _chatIdleEvictionTimer;
    private readonly AsyncLocal<List<string>?> _currentRequestExecutedTools = new();
    private volatile int _activeContextSize = 8192;
    private readonly IContextUsageStrategy _contextUsageStrategy = new FastTokenEstimationContextUsageStrategy();

    public int ActiveContextSize => _activeContextSize;

    public ContextUsageInfo GetSessionContextUsage(string sessionId)
    {
        List<ChatMessage>? msgs = null;
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            lock (session.Messages)
            {
                msgs = [.. session.Messages];
            }
        }

        return _contextUsageStrategy.Calculate(
            msgs,
            _systemPrompt,
            _tools,
            _activeContextSize);
    }

    public (string SessionId, ContextUsageInfo Usage) CreateSessionWithUsage()
    {
        var id = CreateSession();
        return (id, GetSessionContextUsage(id));
    }

    private void RecordExecutedTool(string toolName)
    {
        var list = _currentRequestExecutedTools.Value;
        if (list != null && !list.Contains(toolName))
        {
            list.Add(toolName);
        }
    }

    public IChatClient? Client => _client;
    public bool IsConfigured => _client != null;
    public bool IsLocalGguf => _isLocalGguf && _activeBackend == "LocalGguf";
    public bool IsLocalChat => _isLocalGguf || _activeBackend == "LocalOnnxGenAi" || _activeBackend == "LocalOnnx";
    public string ActiveBackend => _activeBackend;
    public string CurrentModel => _model;
    public string SystemPrompt => _systemPrompt;
    public AITool[] Tools => _tools;

    /// <summary>
    /// 获取当前对话后端的模型能力元数据（视觉多模态、工具调用、上下文容量声明）
    /// </summary>
    public ChatModelCapabilities GetCapabilities()
    {
        var client = _client;
        if (client != null)
        {
            var llama = client.GetService<LocalLlamaChatClient>();
            if (llama != null) return llama.GetCapabilities();

            var onnx = client.GetService<LocalOnnxChatClient>();
            if (onnx != null) return onnx.GetCapabilities();

            if (client is IChatModelCapabilityProvider provider)
            {
                return provider.GetCapabilities();
            }
        }

        var match = _compositeVisionMatcher.Match(_activeBackend, _model, null);

        return new ChatModelCapabilities(
            SupportsVision: match.SupportsVision,
            SupportsToolCalling: true,
            ModelName: _model,
            BackendType: _activeBackend,
            MaxContextTokens: _activeContextSize,
            Description: _activeBackend switch
            {
                "LocalGguf" => "本地 llama.cpp GGUF 引擎",
                "LocalOnnxGenAi" or "LocalOnnx" => "本地 ONNX Runtime GenAI 引擎",
                _ => "外部 API 大模型服务"
            },
            VisionUnsupportedReason: match.SupportsVision ? null : match.GuidanceMessage,
            VisionStatus: match.Status.ToString(),
            MatchedProjectorPath: match.MatchedProjectorPath,
            GuidanceMessage: match.GuidanceMessage);
    }

    /// <summary>当前本地聊天模型实际采用的空闲释放时间。</summary>
    public int ModelIdleTimeoutSeconds => _chatModelIdleTimeoutSeconds;

    /// <summary>
    /// 指示本地对话模型是否已实际加载至显存/内存（程序启动时默认不加载，首次对话时按需延迟加载）
    /// </summary>
    public bool IsModelLoaded
    {
        get
        {
            if (_client == null) return false;
            if (!_isLocalGguf) return true; // 在线 API 模式不占用本地显存，视作就绪

            var llama = _client.GetService<LocalLlamaChatClient>();
            if (llama != null) return llama.IsModelLoaded;

            var onnx = _client.GetService<LocalOnnxChatClient>();
            if (onnx != null) return onnx.IsModelLoaded;

            return false;
        }
    }

    /// <summary>
    /// 显式卸载本地对话模型并释放显存/内存（与嵌入模型和重排模型一致）
    /// </summary>
    public void UnloadModel()
    {
        if (_client == null || !_isLocalGguf) return;
        var llama = _client.GetService<LocalLlamaChatClient>();
        llama?.Unload();
        var onnx = _client.GetService<LocalOnnxChatClient>();
        onnx?.Unload();
    }

    public MafChatService(
        IConfiguration config,
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        ILogger<MafChatService> logger,
        TimeProvider? timeProvider = null,
        HttpMessageHandler? httpMessageHandler = null,
        ConfigManager? configManager = null,
        IModelManager? modelManager = null,
        IMultimodalMessageAdapter? multimodalAdapter = null)
    {
        _scopeFactory = scopeFactory;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _httpMessageHandler = httpMessageHandler;
        _configManager = configManager;
        _modelManager = modelManager;
        _multimodalAdapter = multimodalAdapter ?? new MultimodalMessageAdapter(logger);

        var section = config.GetSection("Chat");
        var endpoint = section["Endpoint"] ?? "https://api.openai.com/v1";
        var apiKey = section["ApiKey"];
        var model = section["Model"] ?? "gpt-4o-mini";
        _model = model;
        _systemPrompt = section["SystemPrompt"] ?? "你是 ReferenceRAG 智能助手。";
        _maxSessions = Math.Max(1, section.GetValue("MaxSessions", 100));
        _maxMessagesPerSession = Math.Max(3, section.GetValue("MaxMessagesPerSession", 50));
        _sessionIdleTimeout = TimeSpan.FromMinutes(Math.Max(1, section.GetValue("SessionIdleMinutes", 60)));

        _tools =
        [
            AIFunctionFactory.Create(SearchKnowledgeAsync, "SearchKnowledge", "检索本地知识库，返回与提问最相关的笔记切片、相似度与文件路径。"),
            AIFunctionFactory.Create(DrillDownChunkAsync, "DrillDownChunk", "展开某个特定分块的完整上下文，可向前向后查看邻近段落。"),
            AIFunctionFactory.Create(BM25SearchAsync, "BM25Search", "精确关键词检索本地知识库文档。"),
            AIFunctionFactory.Create(SearchNoteByTitleAsync, "SearchNoteByTitle", "按笔记标题检索本地笔记。"),
            AIFunctionFactory.Create(GetFileStructureAsync, "GetFileStructure", "获取知识库文件目录结构树。"),
            AIFunctionFactory.Create(ReadFileContentAsync, "ReadFileContent", "读取指定文件的全部或部分内容。"),
            AIFunctionFactory.Create(GetIndexStatusAsync, "GetIndexStatus", "获取知识库当前索引状态：向量分块总数、BM25 文档总数。")
        ];

        if (_configManager != null)
        {
            ReloadConfiguration();
        }
        else
        {
            var activeBackend = section["ActiveBackend"] ?? "OpenAiApi";
            if (string.Equals(activeBackend, "LocalGguf", StringComparison.OrdinalIgnoreCase))
            {
                _activeBackend = "LocalGguf";
                _isLocalGguf = true;
                var modelPath = section["ModelPath"] ?? string.Empty;
                var planner = new ReferenceRAG.Core.Features.HardwareAcceleration.ChatPerformancePlanner();
                var plan = planner.PlanProfile(new ReferenceRAG.Core.Features.HardwareAcceleration.ChatPerformancePlanningRequest
                {
                    ModelPath = modelPath,
                    ModelName = _model,
                    PerformanceMode = section["PerformanceMode"] ?? "Adaptive",
                    AdaptiveStrategy = section["AdaptiveStrategy"] ?? "Balanced",
                    UserContextSize = section.GetValue<int?>("ContextSize"),
                    UserGpuLayers = section.GetValue<int?>("GpuLayers"),
                    UserBatchSize = section.GetValue<int?>("BatchSize"),
                    UserUBatchSize = section.GetValue<int?>("UBatchSize")
                });

                var visionMatch = _compositeVisionMatcher.Match("LocalGguf", _model, modelPath);
                var rawClient = new LocalLlamaChatClient(
                    modelPath: modelPath,
                    modelName: _model,
                    gpuLayers: plan.GpuLayers,
                    contextSize: plan.ContextSize,
                    batchSize: plan.BatchSize,
                    uBatchSize: plan.UBatchSize,
                    mmprojPath: visionMatch.MatchedProjectorPath,
                    logger: _logger);
                _client = rawClient.AsBuilder().UseFunctionInvocation().Build();
            }
            else if (string.Equals(activeBackend, "LocalOnnxGenAi", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(activeBackend, "LocalOnnx", StringComparison.OrdinalIgnoreCase))
            {
                _activeBackend = "LocalOnnxGenAi";
                _isLocalGguf = true;
                var modelPath = section["ModelPath"] ?? string.Empty;
                var ogaDir = OnnxGenAiPathResolver.ResolveModelDirectory(modelPath);
                if (!string.IsNullOrEmpty(ogaDir))
                {
                    modelPath = ogaDir;
                }
                var rawClient = new LocalOnnxChatClient(
                    modelPath: modelPath,
                    modelName: _model,
                    logger: _logger);
                _client = rawClient.AsBuilder().UseFunctionInvocation().Build();
            }
            else
            {
                _activeBackend = "OpenAiApi";
                _isLocalGguf = false;
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    _logger.LogWarning("聊天 API Key 未配置；聊天接口将在调用时返回配置错误");
                }
                else
                {
                    _client = BuildChatClient(endpoint, apiKey, model);
                }
            }
        }

        if (_modelManager != null)
        {
            _modelManager.ChatModelSwitched += OnChatModelSwitched;
        }

        _chatIdleEvictionTimer = new System.Threading.Timer(CheckChatModelIdleEviction, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    private void OnChatModelSwitched(object? sender, ModelSwitchedEventArgs e)
    {
        _logger.LogInformation("检测到本地对话模型切换事件: {ModelName} -> {ModelPath}", e.ModelName, e.ModelPath);
        ReloadConfiguration();
    }

    private IChatClient? BuildChatClient(string endpoint, string? apiKey, string model)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return null;

        var sanitizingHandler = _httpMessageHandler != null
            ? new OpenAiToolCallSanitizingHandler(_httpMessageHandler)
            : new OpenAiToolCallSanitizingHandler();
        var httpClient = new HttpClient(sanitizingHandler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = new Uri(endpoint),
            Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(httpClient)
        };

        var rawClient = new OpenAIClient(new ApiKeyCredential(apiKey), clientOptions)
            .GetChatClient(model)
            .AsIChatClient();

        return rawClient
            .AsBuilder()
            .UseFunctionInvocation()
            .Build();
    }

    /// <summary>
    /// 根据系统配置动态重载 AI 聊天客户端（支持 LocalGguf 本地模型与 OpenAiApi 远程端点双向切换）
    /// </summary>
    public void ReloadConfiguration()
    {
        if (_configManager == null) return;

        var config = _configManager.Load();
        var chatConfig = config.Chat ?? new ChatModelConfig();
        var activeBackend = chatConfig.ActiveBackend ?? "OpenAiApi";
        _chatModelIdleTimeoutSeconds = chatConfig.IdleTimeoutSeconds > 0 ? chatConfig.IdleTimeoutSeconds : 300;

        _systemPrompt = string.IsNullOrWhiteSpace(chatConfig.SystemPrompt)
            ? "你是 ReferenceRAG 智能助手。"
            : chatConfig.SystemPrompt.Trim();

        if (string.Equals(activeBackend, "LocalGguf", StringComparison.OrdinalIgnoreCase))
        {
            _activeBackend = "LocalGguf";
            _isLocalGguf = true;
            var localGguf = chatConfig.LocalGguf ?? new LocalGgufChatProfile();
            var modelName = string.IsNullOrWhiteSpace(localGguf.ModelName) ? "Qwen2.5-7B-Instruct-GGUF" : localGguf.ModelName;
            _model = modelName;

            var modelPath = localGguf.ModelPath;
            if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
            {
                if (_modelManager != null)
                {
                    var registered = _modelManager.GetChatModels().FirstOrDefault(m => string.Equals(m.Name, modelName, StringComparison.OrdinalIgnoreCase));
                    if (registered != null)
                    {
                        var resolved = ModelManager.ResolveModelFilePath(registered.LocalPath, localGguf.Variant);
                        if (!string.IsNullOrWhiteSpace(resolved))
                        {
                            modelPath = resolved;
                        }
                    }
                }
            }

            var planner = new ReferenceRAG.Core.Features.HardwareAcceleration.ChatPerformancePlanner();
            var plan = planner.PlanProfile(new ReferenceRAG.Core.Features.HardwareAcceleration.ChatPerformancePlanningRequest
            {
                ModelPath = modelPath,
                ModelName = modelName,
                UseGpu = localGguf.Acceleration?.Backend != AccelerationBackend.Cpu,
                PerformanceMode = localGguf.PerformanceMode,
                AdaptiveStrategy = localGguf.AdaptiveStrategy,
                UserContextSize = localGguf.ContextSize > 0 ? localGguf.ContextSize : null,
                UserGpuLayers = localGguf.GpuLayers >= 0 ? localGguf.GpuLayers : null,
                UserBatchSize = localGguf.BatchSize > 0 ? localGguf.BatchSize : null,
                UserUBatchSize = localGguf.UBatchSize > 0 ? localGguf.UBatchSize : null
            });

            int gpuLayers = plan.GpuLayers;
            int contextSize = plan.ContextSize;
            int batchSize = plan.BatchSize;
            int uBatchSize = plan.UBatchSize;

            _logger.LogInformation(
                "正在初始化本地 GGUF 聊天客户端: ModelName={ModelName}, Path={Path}, GpuLayers={Layers}, ContextSize={Ctx}, BatchSize={Batch}, UBatchSize={UBatch} ({Rationale})",
                modelName, modelPath, gpuLayers, contextSize, batchSize, uBatchSize, plan.Rationale);
            _activeContextSize = contextSize;

            try
            {
                var visionMatch = _compositeVisionMatcher.Match("LocalGguf", modelName, modelPath);
                var rawClient = new LocalLlamaChatClient(
                    modelPath: modelPath ?? string.Empty,
                    modelName: modelName,
                    gpuLayers: gpuLayers,
                    contextSize: contextSize,
                    batchSize: batchSize,
                    uBatchSize: uBatchSize,
                    mmprojPath: visionMatch.MatchedProjectorPath,
                    logger: _logger);

                var newClient = rawClient
                    .AsBuilder()
                    .UseFunctionInvocation()
                    .Build();

                var oldClient = Interlocked.Exchange(ref _client, newClient);
                (oldClient as IDisposable)?.Dispose();

                _logger.LogInformation("本地 GGUF 聊天客户端装配完成并已激活: {ModelName}", modelName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "初始化本地 GGUF 聊天客户端失败: {ModelName}", modelName);
                var oldClient = Interlocked.Exchange(ref _client, null);
                (oldClient as IDisposable)?.Dispose();
            }
        }
        else if (string.Equals(activeBackend, "LocalOnnxGenAi", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(activeBackend, "LocalOnnx", StringComparison.OrdinalIgnoreCase))
        {
            _activeBackend = "LocalOnnxGenAi";
            _isLocalGguf = true;
            var localOnnx = chatConfig.LocalOnnxGenAi ?? new LocalOnnxGenAiChatProfile();
            var modelName = string.IsNullOrWhiteSpace(localOnnx.ModelName) ? "Phi-3.5-mini-instruct-onnx" : localOnnx.ModelName;
            _model = modelName;

            var modelPath = localOnnx.ModelPath;
            if (string.IsNullOrWhiteSpace(modelPath) || (!File.Exists(modelPath) && !Directory.Exists(modelPath)))
            {
                if (_modelManager != null)
                {
                    var registered = _modelManager.GetChatModels().FirstOrDefault(m => string.Equals(m.Name, modelName, StringComparison.OrdinalIgnoreCase));
                    if (registered != null && !string.IsNullOrWhiteSpace(registered.LocalPath))
                    {
                        var resolved = ModelManager.ResolveModelFilePath(registered.LocalPath, registered.ActiveVariantFileName);
                        modelPath = !string.IsNullOrWhiteSpace(resolved) ? resolved : registered.LocalPath;
                    }
                }
            }

            var ogaDir = OnnxGenAiPathResolver.ResolveModelDirectory(modelPath);
            if (!string.IsNullOrEmpty(ogaDir))
            {
                modelPath = ogaDir;
            }

            _logger.LogInformation(
                "正在初始化本地 ONNX 聊天客户端: ModelName={ModelName}, Path={Path}, Backend={Backend}",
                modelName, modelPath, localOnnx.Acceleration?.Backend);
            _activeContextSize = 4096;

            try
            {
                var rawClient = new LocalOnnxChatClient(
                    modelPath: modelPath ?? string.Empty,
                    modelName: modelName,
                    acceleration: localOnnx.Acceleration,
                    logger: _logger);

                var newClient = rawClient
                    .AsBuilder()
                    .UseFunctionInvocation()
                    .Build();

                var oldClient = Interlocked.Exchange(ref _client, newClient);
                (oldClient as IDisposable)?.Dispose();

                _logger.LogInformation("本地 ONNX 聊天客户端装配完成并已激活: {ModelName}", modelName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "初始化本地 ONNX 聊天客户端失败: {ModelName}", modelName);
                var oldClient = Interlocked.Exchange(ref _client, null);
                (oldClient as IDisposable)?.Dispose();
            }
        }
        else
        {
            _activeBackend = "OpenAiApi";
            _isLocalGguf = false;
            var openAiProfile = chatConfig.OpenAiApi ?? new OpenAiApiChatProfile();
            var ep = string.IsNullOrWhiteSpace(openAiProfile.Endpoint) ? "https://api.openai.com/v1" : openAiProfile.Endpoint.Trim();
            var mdl = string.IsNullOrWhiteSpace(openAiProfile.Model) ? "gpt-4o-mini" : openAiProfile.Model.Trim();
            var key = openAiProfile.ApiKey?.Trim();
            _model = mdl;
        _activeContextSize = mdl.Contains("128k", StringComparison.OrdinalIgnoreCase) || mdl.Contains("mini", StringComparison.OrdinalIgnoreCase) || mdl.Contains("gpt-4", StringComparison.OrdinalIgnoreCase) ? 128000 : 8192;
            _activeContextSize = mdl.Contains("128k", StringComparison.OrdinalIgnoreCase) || mdl.Contains("mini", StringComparison.OrdinalIgnoreCase) || mdl.Contains("gpt-4", StringComparison.OrdinalIgnoreCase) ? 128000 : 8192;

            if (string.IsNullOrWhiteSpace(key))
            {
                var oldClient = Interlocked.Exchange(ref _client, null);
                (oldClient as IDisposable)?.Dispose();
                _logger.LogWarning("AI 聊天 API Key 未配置，聊天接口将提示配置");
            }
            else
            {
                try
                {
                    var newClient = BuildChatClient(ep, key, mdl);
                    var oldClient = Interlocked.Exchange(ref _client, newClient);
                    (oldClient as IDisposable)?.Dispose();
                    _logger.LogInformation("OpenAI 兼容聊天客户端热更新成功，立即生效");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "热更新 OpenAI 聊天客户端失败");
                    var oldClient = Interlocked.Exchange(ref _client, null);
                    (oldClient as IDisposable)?.Dispose();
                }
            }
        }
    }

    /// <summary>
    /// 热更新 AI 聊天配置（无需重启即时生效）
    /// </summary>
    public void UpdateConfiguration(string? endpoint, string? apiKey, string? model, string? systemPrompt)
    {
        _isLocalGguf = false;
        var ep = string.IsNullOrWhiteSpace(endpoint) ? "https://api.openai.com/v1" : endpoint.Trim();
        var mdl = string.IsNullOrWhiteSpace(model) ? "gpt-4o-mini" : model.Trim();
        var key = apiKey?.Trim();

        _logger.LogInformation("正在热更新 AI 聊天配置: Endpoint={Endpoint}, Model={Model}, HasKey={HasKey}",
            ep, mdl, !string.IsNullOrWhiteSpace(key));

        _model = mdl;
        _activeContextSize = mdl.Contains("128k", StringComparison.OrdinalIgnoreCase) || mdl.Contains("mini", StringComparison.OrdinalIgnoreCase) || mdl.Contains("gpt-4", StringComparison.OrdinalIgnoreCase) ? 128000 : 8192;
        _systemPrompt = string.IsNullOrWhiteSpace(systemPrompt) ? "你是 ReferenceRAG 智能助手。" : systemPrompt.Trim();

        if (string.IsNullOrWhiteSpace(key))
        {
            var old = Interlocked.Exchange(ref _client, null);
            (old as IDisposable)?.Dispose();
            _logger.LogWarning("AI 聊天 API Key 已清空，聊天接口将提示配置");
            return;
        }

        try
        {
            var newClient = BuildChatClient(ep, key, mdl);
            var old = Interlocked.Exchange(ref _client, newClient);
            (old as IDisposable)?.Dispose();
            _logger.LogInformation("AI 聊天客户端热更新成功，立即生效");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "热更新 AI 聊天客户端失败");
            throw;
        }
    }

    private void CheckChatModelIdleEviction(object? state)
    {
        if (!_isLocalGguf || _client == null) return;
        if (_chatModelIdleTimeoutSeconds <= 0) return;

        if (IsModelLoaded && (DateTime.UtcNow - _lastChatActivityUtc).TotalSeconds >= _chatModelIdleTimeoutSeconds)
        {
            _logger.LogInformation(
                "[MafChatService] 本地对话模型已空闲超过 {Timeout} 秒，触发自动显存卸载释放...",
                _chatModelIdleTimeoutSeconds);
            UnloadModel();
        }
    }

    public void Dispose()
    {
        _chatIdleEvictionTimer?.Dispose();
        if (_modelManager != null)
        {
            _modelManager.ChatModelSwitched -= OnChatModelSwitched;
        }
        var old = Interlocked.Exchange(ref _client, null);
        (old as IDisposable)?.Dispose();
    }

    // ── 工具定义 ──────────────────────────────────────────────────────

    [Description("语义搜索知识库，返回相关笔记片段及来源元数据（文件路径、行号、refId）。适用于通用知识查询，建议扩展查询词以提高召回率。")]
    private async Task<string> SearchKnowledgeAsync(
        [Description("查询词，建议补充同义词、中英双语、相关术语以提高召回率")] string? query = null,
        [Description("返回数量，默认 10")] int topK = 10,
        [Description("检索查询词（别名）")] string? keyword = null)
    {
        var effectiveQuery = !string.IsNullOrWhiteSpace(query) ? query : keyword ?? string.Empty;
        RecordExecutedTool("SearchKnowledge");
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var searchService = scope.ServiceProvider.GetRequiredService<ISearchService>();
            var request = new AIQueryRequest
            {
                Query = effectiveQuery,
                TopK = topK,
                Mode = QueryMode.HybridRerank,
                ContextWindow = 1
            };
            var response = await searchService.SearchAsync(request);

            if (response.Chunks.Count == 0)
                return $"知识库中未检索到与“{effectiveQuery}”相关的文档或片段。建议尝试更精确或不同的业务关键词。";

            const int maxChunkChars = 800;
            const int maxTotalChars = 5000;

            var sb = new StringBuilder();
            sb.AppendLine($"## 语义检索结果（共 {response.Chunks.Count} 条，耗时 {response.Stats.DurationMs}ms）\n");

            for (int i = 0; i < response.Chunks.Count; i++)
            {
                var c = response.Chunks[i];
                var title = c.Title ?? Path.GetFileNameWithoutExtension(c.FilePath);
                var heading = string.IsNullOrEmpty(c.HeadingPath) ? "" : $" > {c.HeadingPath}";
                var chunkHeader = $"[{i + 1}] 《{title}》{heading}\n    文件: {c.FilePath}（行 {c.StartLine}-{c.EndLine}，refId: {c.RefId}，score: {c.Score:F3}）\n    ---";

                var content = c.Content ?? string.Empty;
                if (content.Length > maxChunkChars)
                {
                    content = content[..maxChunkChars] + $"\n    ... [片段内容较长已截断，如需完整正文可调用 DrillDownChunk(refId: \"{c.RefId}\")]";
                }

                var chunkBlock = $"{chunkHeader}\n{content}\n\n";
                if (sb.Length + chunkBlock.Length > maxTotalChars)
                {
                    sb.AppendLine($"... [为适配模型上下文限制，已省略剩余 {response.Chunks.Count - i} 条较低相关度检索结果]");
                    break;
                }

                sb.Append(chunkBlock);
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SearchKnowledge 失败: {Query}", effectiveQuery);
            return $"搜索失败：{ex.Message}";
        }
    }

    [Description("按 refId 展开某个检索结果的完整上下文，适用于 SearchKnowledge 返回内容被截断时。")]
    private async Task<string> DrillDownChunkAsync(
        [Description("来自 SearchKnowledge 结果的 refId")] string refId,
        [Description("原始查询词")] string query,
        [Description("上下文扩展窗口大小，默认 2")] int expandContext = 2)
    {
        RecordExecutedTool("DrillDownChunk");
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var searchService = scope.ServiceProvider.GetRequiredService<ISearchService>();
            var response = await searchService.DrillDownAsync(new DrillDownRequest
            {
                Query = query,
                RefIds = [refId],
                ExpandContext = expandContext
            });
            return string.IsNullOrWhiteSpace(response.FullContext)
                ? "未能展开该片段内容。"
                : $"## 展开内容（refId: {refId}）\n\n{response.FullContext}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DrillDown 失败: {RefId}", refId);
            return $"展开失败：{ex.Message}";
        }
    }

    [Description("BM25 关键词精确搜索，适用于错误码、API 名称、变量名、精确术语等场景。")]
    private async Task<string> BM25SearchAsync(
        [Description("精确关键词")] string? keyword = null,
        [Description("返回数量，默认 10")] int topK = 10,
        [Description("检索关键词（别名）")] string? query = null)
    {
        var effectiveKeyword = !string.IsNullOrWhiteSpace(keyword) ? keyword : query ?? string.Empty;
        RecordExecutedTool("BM25Search");
        try
        {
            if (string.IsNullOrWhiteSpace(effectiveKeyword)) return "搜索关键词不能为空。";
            using var scope = _scopeFactory.CreateScope();
            var bm25Store = scope.ServiceProvider.GetRequiredService<IBM25Store>();
            var vectorStore = scope.ServiceProvider.GetRequiredService<IVectorStore>();
            var configManager = scope.ServiceProvider.GetRequiredService<ConfigManager>();

            var config = configManager.Load();
            var enabledSources = config.Sources
                .Where(s => s.Enabled)
                .Select(s => s.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (config.Sources.Count > 0 && enabledSources.Count == 0)
                return $"未找到关键词「{effectiveKeyword}」的匹配内容（所有数据源均已停用）。";

            var results = await bm25Store.SearchAsync(effectiveKeyword, topK * 2);

            var filteredResults = new List<BM25SearchResult>();
            if (config.Sources.Count > 0)
            {
                foreach (var item in results)
                {
                    var chunk = await vectorStore.GetChunkAsync(item.ChunkId);
                    if (chunk != null)
                    {
                        var file = await vectorStore.GetFileAsync(chunk.FileId);
                        if (file != null && !string.IsNullOrEmpty(file.Source) && enabledSources.Contains(file.Source))
                        {
                            filteredResults.Add(item);
                            if (filteredResults.Count >= topK) break;
                        }
                    }
                }
            }
            else
            {
                filteredResults = results.Take(topK).ToList();
            }

            if (filteredResults.Count == 0)
                return $"未找到关键词「{keyword}」的匹配内容。";

            const int maxChunkChars = 800;
            const int maxTotalChars = 5000;
            var sb = new StringBuilder();
            sb.AppendLine($"## BM25 精确检索（词：{keyword}）\n");
            int idx = 1;
            for (int i = 0; i < filteredResults.Count; i++)
            {
                var item = filteredResults[i];
                var header = $"[{idx++}] score: {item.Score:F2}\n";
                var content = item.Content ?? string.Empty;
                if (content.Length > maxChunkChars)
                {
                    content = content[..maxChunkChars] + "\n... [内容过长已截断]";
                }
                var itemBlock = $"{header}{content}\n\n";
                if (sb.Length + itemBlock.Length > maxTotalChars)
                {
                    sb.AppendLine($"... [已省略剩余 {filteredResults.Count - i} 条检索结果以适配上下文限制]");
                    break;
                }
                sb.Append(itemBlock);
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BM25Search 失败: {Keyword}", effectiveKeyword);
            return $"BM25 搜索失败：{ex.Message}";
        }
    }

    [Description("按笔记标题关键词查找知识图谱节点，返回节点 ID 和标题，可用于后续 GetFileStructure 或 ReadFileContent。")]
    private async Task<string> SearchNoteByTitleAsync(
        [Description("笔记标题关键词")] string titleQuery,
        [Description("返回数量，默认 10")] int limit = 10)
    {
        RecordExecutedTool("SearchNoteByTitle");
        try
        {
            if (string.IsNullOrWhiteSpace(titleQuery)) return "笔记标题关键词不能为空。";
            using var scope = _scopeFactory.CreateScope();
            var graphStore = scope.ServiceProvider.GetRequiredService<IGraphStore>();
            var vectorStore = scope.ServiceProvider.GetRequiredService<IVectorStore>();
            var configManager = scope.ServiceProvider.GetRequiredService<ConfigManager>();

            var config = configManager.Load();
            var enabledSources = config.Sources
                .Where(s => s.Enabled)
                .Select(s => s.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (config.Sources.Count > 0 && enabledSources.Count == 0)
                return $"未找到标题含「{titleQuery}」的笔记节点（所有数据源均已停用）。";

            limit = Math.Clamp(limit, 1, 50);
            var nodes = await graphStore.SearchNodesAsync(titleQuery, limit * 2);

            var filteredNodes = new List<GraphNode>();
            if (config.Sources.Count > 0)
            {
                var paths = nodes.Select(n =>
                {
                    var hashIdx = n.Id.IndexOf('#');
                    return hashIdx >= 0 ? n.Id[..hashIdx] : n.Id;
                }).Distinct().ToList();

                var files = (await vectorStore.GetFilesByPathsAsync(paths))
                    .Where(f => !string.IsNullOrEmpty(f.Source) && enabledSources.Contains(f.Source))
                    .ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);

                foreach (var node in nodes)
                {
                    var hashIdx = node.Id.IndexOf('#');
                    var p = hashIdx >= 0 ? node.Id[..hashIdx] : node.Id;
                    if (files.ContainsKey(p))
                    {
                        filteredNodes.Add(node);
                        if (filteredNodes.Count >= limit) break;
                    }
                }
            }
            else
            {
                filteredNodes = nodes.Take(limit).ToList();
            }

            if (filteredNodes.Count == 0)
                return $"未找到标题含「{titleQuery}」的笔记节点。";

            var sb = new StringBuilder();
            sb.AppendLine($"## 图谱节点搜索（标题：{titleQuery}）");
            sb.AppendLine();
            foreach (var node in filteredNodes)
            {
                sb.AppendLine($"- 《{node.Title}》  类型: {node.Type}");
                sb.AppendLine($"  ID: {node.Id}");
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SearchNoteByTitle 失败: {Query}", titleQuery);
            return $"图谱搜索失败：{ex.Message}";
        }
    }

    [Description("获取一批文件的章节目录结构（标题 + 行号范围），不含正文，用于规划后续精准读取。文件路径用逗号分隔，单次最多 20 个。")]
    private async Task<string> GetFileStructureAsync(
        [Description("文件绝对路径列表，多个路径用英文逗号分隔")] string filePaths)
    {
        RecordExecutedTool("GetFileStructure");
        try
        {
            var paths = filePaths.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (paths.Length == 0) return "文件路径列表不能为空。";
            if (paths.Length > 20) return "单次最多查询 20 个文件。";

            using var scope = _scopeFactory.CreateScope();
            var vectorStore = scope.ServiceProvider.GetRequiredService<IVectorStore>();
            var configManager = scope.ServiceProvider.GetRequiredService<ConfigManager>();

            var config = configManager.Load();
            var enabledSources = config.Sources
                .Where(s => s.Enabled)
                .Select(s => s.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var sb = new StringBuilder();
            sb.AppendLine("## 文件章节结构");
            sb.AppendLine();

            foreach (var rawPath in paths)
            {
                if (string.IsNullOrWhiteSpace(rawPath) || rawPath.Contains(".."))
                {
                    sb.AppendLine($"- {rawPath}: 路径无效");
                    continue;
                }

                var normalizedPath = Path.GetFullPath(rawPath);
                var file = await vectorStore.GetFileByPathAsync(normalizedPath)
                           ?? await vectorStore.GetFileByPathAsync(rawPath);

                if (file == null)
                {
                    sb.AppendLine($"- {rawPath}: 文件未被索引");
                    continue;
                }

                if (config.Sources.Count > 0 && (!enabledSources.Contains(file.Source ?? "")))
                {
                    sb.AppendLine($"- {rawPath}: 文件所属数据源已停用");
                    continue;
                }

                var chunks = (await vectorStore.GetChunksByFileAsync(file.Id))
                    .OrderBy(c => c.ChunkOrder)
                    .ToList();

                sb.AppendLine($"### {(string.IsNullOrEmpty(file.Title) ? file.Path : file.Title)}");
                sb.AppendLine($"路径: {file.Path}");
                foreach (var chunk in chunks)
                {
                    sb.AppendLine($"  - {chunk.HeadingPath}（行 {chunk.StartLine}-{chunk.EndLine}）");
                }
                sb.AppendLine();
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetFileStructure 失败");
            return $"获取文件结构失败：{ex.Message}";
        }
    }

    [Description("按行范围精准读取文件内容。startLine=0 且 endLine=0 时读取全文。建议先用 GetFileStructure 确认目标章节行号再调用。")]
    private async Task<string> ReadFileContentAsync(
        [Description("文件绝对路径")] string filePath,
        [Description("起始行，0 表示从头")] int startLine = 0,
        [Description("结束行，0 表示到末尾")] int endLine = 0)
    {
        RecordExecutedTool("ReadFileContent");
        try
        {
            if (string.IsNullOrWhiteSpace(filePath) || filePath.Contains(".."))
                return "文件路径无效。";

            if (startLine < 0 || endLine < 0)
                return "startLine / endLine 不能为负数。";

            if (startLine > 0 && endLine > 0 && startLine > endLine)
                return $"startLine ({startLine}) 不能大于 endLine ({endLine})。";

            using var scope = _scopeFactory.CreateScope();
            var vectorStore = scope.ServiceProvider.GetRequiredService<IVectorStore>();
            var configManager = scope.ServiceProvider.GetRequiredService<ConfigManager>();

            var config = configManager.Load();
            var enabledSources = config.Sources
                .Where(s => s.Enabled)
                .Select(s => s.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var normalizedPath = Path.GetFullPath(filePath);
            var file = await vectorStore.GetFileByPathAsync(normalizedPath)
                       ?? await vectorStore.GetFileByPathAsync(filePath);

            if (file == null)
                return "文件未被索引。";

            if (config.Sources.Count > 0 && (!enabledSources.Contains(file.Source ?? "")))
                return "该文件所属数据源已停用，无法读取内容。";

            var allChunks = await vectorStore.GetChunksByFileAsync(file.Id);
            var filtered = allChunks.OrderBy(c => c.ChunkOrder);
            var hasRange = startLine > 0 || endLine > 0;
            if (hasRange)
            {
                var start = startLine > 0 ? startLine : 1;
                var end = endLine > 0 ? endLine : int.MaxValue;
                filtered = filtered.Where(c => c.StartLine <= end && c.EndLine >= start)
                                   .OrderBy(c => c.ChunkOrder);
            }

            const int maxFileChars = 6000;
            var sb = new StringBuilder();
            foreach (var chunk in filtered)
            {
                var text = chunk.Content ?? string.Empty;
                if (sb.Length + text.Length > maxFileChars)
                {
                    var remain = Math.Max(0, maxFileChars - sb.Length);
                    if (remain > 100)
                    {
                        sb.AppendLine(text[..remain]);
                    }
                    sb.AppendLine("\n... [为保护模型上下文窗口，已截断后续内容。建议传入具体的 startLine 与 endLine 分段精准读取]");
                    break;
                }
                sb.AppendLine(text);
            }

            return sb.Length == 0 ? "未读取到内容。" : sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ReadFileContent 失败: {Path}", filePath);
            return $"读取文件失败：{ex.Message}";
        }
    }

    [Description("获取知识库当前索引状态：向量分块总数、BM25 文档总数。")]
    private async Task<string> GetIndexStatusAsync()
    {
        RecordExecutedTool("GetIndexStatus");
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var vectorStore = scope.ServiceProvider.GetRequiredService<IVectorStore>();
            var bm25Store = scope.ServiceProvider.GetRequiredService<IBM25Store>();
            var vectorStats = await vectorStore.GetVectorStatsAsync();
            var bm25Stats = await bm25Store.GetStatsAsync();
            return $"向量索引：{vectorStats.Sum(v => v.VectorCount)} 个分块；BM25 索引：{bm25Stats.TotalDocuments} 个文档";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetIndexStatus 失败");
            return $"获取状态失败：{ex.Message}";
        }
    }

    // ── 会话管理 ──────────────────────────────────────────────────────

    public string CreateSession()
    {
        RemoveExpiredSessions();
        _lastChatActivityUtc = DateTime.UtcNow;
        while (_sessions.Count >= _maxSessions)
        {
            var oldest = _sessions.MinBy(item => item.Value.LastAccessUtc);
            if (oldest.Key is null || !_sessions.TryRemove(oldest.Key, out _)) break;
        }

        var sessionId = Guid.NewGuid().ToString("N");
        _sessions[sessionId] = new ChatSession([new ChatMessage(ChatRole.System, _systemPrompt)], _timeProvider.GetUtcNow());
        return sessionId;
    }

    public bool DeleteSession(string sessionId) => _sessions.TryRemove(sessionId, out _);

    public IReadOnlyList<string> GetToolDescriptions()
    {
        return _tools.Select(t =>
        {
            var func = t as AIFunction;
            return func != null ? $"{func.Name}（{func.Description?.Split('。')[0] ?? ""}）" : "";
        }).Where(s => !string.IsNullOrEmpty(s)).ToList();
    }

    public IAsyncEnumerable<SseEvent> StreamAsync(
        string sessionId,
        string userMessage,
        CancellationToken ct = default)
        => StreamAsync(sessionId, userMessage, null, ct);

    public async IAsyncEnumerable<SseEvent> StreamAsync(
        string sessionId,
        string userMessage,
        IReadOnlyList<string>? images,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var client = _client;
        if (client is null)
        {
            var msg = _isLocalGguf
                ? "本地聊天模型未就绪或未找到模型文件，请在设置中检查模型配置"
                : "聊天 API Key 未配置，请在项目设置中配置 Chat ApiKey 后使用";
            yield return new SseEvent("error", Message: msg);
            yield return new SseEvent("done");
            yield break;
        }

        // 校验输入图像列表安全合规性与当前模型多模态能力
        if (images != null && images.Count > 0)
        {
            var caps = GetCapabilities();
            if (!caps.SupportsVision)
            {
                var warnBadge = $"> ⚠️ **【多模态提示】** 当前加载的模型【{_model}】为纯语言模型（LLM），未挂载多模态视觉投影器（mmproj），无法直接识别图像。本次回答已安全降级，将优先基于您的提问文字与知识库内容为您解答。\n\n";
                yield return new SseEvent("text", Delta: warnBadge);
                // 传 null 避免在提示词中拼接误导小模型的图像标记，防止小模型产生“无法访问外部图像”的幻觉解释
                images = null;
            }
            else
            {
                var (valid, validationErr) = _multimodalAdapter.ValidateImages(images);
                if (!valid)
                {
                    yield return new SseEvent("error", Message: validationErr ?? "图片校验失败");
                    yield return new SseEvent("done");
                    yield break;
                }
            }
        }

        RemoveExpiredSessions();
        _currentRequestExecutedTools.Value = new List<string>();
        _lastChatActivityUtc = DateTime.UtcNow;
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            yield return new SseEvent("error", Message: "会话不存在，请创建新会话");
            yield return new SseEvent("done");
            yield break;
        }

        // 状态机全生命周期初始化（借鉴 MAF 工作流与状态机模式 State Machine Pattern）
        var turnContext = new ChatTurnContext
        {
            SessionId = sessionId,
            UserMessage = userMessage
        };
        var fsm = new ToolCallingExecutionStateMachine(turnContext);

        session.LastAccessUtc = _timeProvider.GetUtcNow();
        List<ChatMessage> history;
        lock (session.Messages)
        {
            session.Messages.Add(_multimodalAdapter.AdaptUserMessage(userMessage, images));
            TrimHistory(session.Messages);
            history = [.. session.Messages];
        }

        // 1. 前置意图路由阶段 (Routing State)
        fsm.TransitionTo(ChatTurnState.Routing, "评估前置意图路由先验分流");
        var decision = await CompositeIntentRouter.Default.RouteAsync(userMessage, _tools, ct);
        turnContext.RoutingDecision = decision;

        // 2. 策略模式：意图自适应工具动态下发 (Strategy Pattern: Tool Selection)
        var toolStrategy = new IntentAdaptiveToolSelectionStrategy();
        var selectedTools = toolStrategy.SelectTools(userMessage, _tools, decision, GetCapabilities());

        var options = new ChatOptions
        {
            Tools = [.. selectedTools],
            MaxOutputTokens = 4096
        };
        var responseText = new StringBuilder();
        var streamedBadge = false;

        // 3. 确定性直接工具分发（若前置意图路由直接决断，如搜索前缀、GetIndexStatus 等）
        if (decision is { Handled: true, FunctionName: not null } &&
            _tools.OfType<AIFunction>().FirstOrDefault(f => f.Name.Equals(decision.FunctionName, StringComparison.OrdinalIgnoreCase)) is { } targetFunc)
        {
            fsm.TransitionTo(ChatTurnState.DirectToolExecuting, $"前置确定性命中工具 {decision.FunctionName}");
            fsm.TransitionTo(ChatTurnState.ToolExecuting, $"服务端直接执行分发工具 {decision.FunctionName}");
            turnContext.ExecutedTools.Add(decision.FunctionName);
            RecordExecutedTool(decision.FunctionName);

            var directBadge = $"> 🔧 **已执行工具**: `{decision.FunctionName}`\n\n";
            responseText.Append(directBadge);
            yield return new SseEvent("text", Delta: directBadge);
            streamedBadge = true;

            string toolResult;
            try
            {
                var args = decision.Arguments ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                var invokeResult = await targetFunc.InvokeAsync(new AIFunctionArguments(args), ct);
                toolResult = invokeResult?.ToString() ?? "无返回结果";
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "直接分发工具 {ToolName} 执行异常", decision.FunctionName);
                toolResult = $"工具执行异常: {ex.Message}";
            }

            // 构造工具结果上下文，转入 Phase 2 总结生成（强负向防说教，杜绝递归调用）
            fsm.TransitionTo(ChatTurnState.Phase2Synthesizing, "工具执行完毕转入 Phase 2 总结生成");
            var phase2History = new List<ChatMessage>(history)
            {
                new(ChatRole.Assistant, directBadge.TrimEnd()),
                new(ChatRole.Tool, toolResult)
            };

            var phase2Options = new ChatOptions
            {
                Tools = [], // 总结阶段不再挂载工具，专注生成纯粹总结
                MaxOutputTokens = 4096
            };

            await foreach (var update in client.GetStreamingResponseAsync(phase2History, phase2Options, ct))
            {
                if (!string.IsNullOrEmpty(update.Text))
                {
                    responseText.Append(update.Text);
                    yield return new SseEvent("text", Delta: update.Text);
                }
            }
        }
        else
        {
            // 正常大模型自主推理与工具决策 (Phase 1 Inferencing)
            fsm.TransitionTo(ChatTurnState.Phase1Inferencing, "大模型自主推理与工具决策");

            await foreach (var update in client.GetStreamingResponseAsync(history, options, ct))
            {
                if (!streamedBadge && _currentRequestExecutedTools.Value is { Count: > 0 } execTools)
                {
                    fsm.TransitionTo(ChatTurnState.ToolExecuting, "模型产生工具调用并执行");
                    fsm.TransitionTo(ChatTurnState.Phase2Synthesizing, "工具执行完毕转入总结");

                    foreach (var t in execTools)
                    {
                        if (!turnContext.ExecutedTools.Contains(t))
                            turnContext.ExecutedTools.Add(t);
                    }

                    var badge = $"> 🔧 **已执行工具**: {string.Join(", ", execTools.Select(t => $"`{t}`"))}\n\n";
                    responseText.Append(badge);
                    yield return new SseEvent("text", Delta: badge);
                    streamedBadge = true;
                }

                if (!string.IsNullOrEmpty(update.Text))
                {
                    responseText.Append(update.Text);
                    yield return new SseEvent("text", Delta: update.Text);
                }
            }
        }

        // 4. 输出清洗与规范化 (Output Sanitizing)
        var rawText = responseText.ToString();
        turnContext.RawOutput = rawText;

        if (fsm.CurrentState == ChatTurnState.Phase1Inferencing || fsm.CurrentState == ChatTurnState.Phase2Synthesizing)
        {
            fsm.TransitionTo(ChatTurnState.OutputSanitizing, "执行响应清洗与标签自愈");
        }

        var sanitized = ChatToolCallingHelper.SanitizeAssistantResponse(rawText);

        if (_currentRequestExecutedTools.Value is { Count: > 0 } finishedTools)
        {
            var badge = $"> 🔧 **已执行工具**: {string.Join(", ", finishedTools.Select(t => $"`{t}`"))}\n\n";
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                sanitized = badge + "工具执行完毕。";
            }
            else if (!sanitized.StartsWith("> 🔧 **已执行工具**", StringComparison.Ordinal))
            {
                sanitized = badge + sanitized;
            }
        }

        // 5. 自愈保底判定 (Self-Healing Fallback)
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = fsm.TriggerSelfHealing("检测到输出为空或模型截断");
        }
        else if (fsm.CurrentState == ChatTurnState.OutputSanitizing)
        {
            fsm.TransitionTo(ChatTurnState.Completed, "本轮对话成功结束");
        }

        turnContext.SanitizedOutput = sanitized;

        if (!string.IsNullOrEmpty(sanitized))
        {
            if (!string.Equals(sanitized, rawText, StringComparison.Ordinal))
            {
                yield return new SseEvent("replace", Delta: sanitized);
            }

            lock (session.Messages)
            {
                session.Messages.Add(new ChatMessage(ChatRole.Assistant, sanitized));
                TrimHistory(session.Messages);
                session.LastAccessUtc = _timeProvider.GetUtcNow();
            }
        }

        // 6. 附带全生命周期遥测指标的上下文用量通知 (Telemetry in context event)
        var finalUsage = GetSessionContextUsage(sessionId);
        var telemetryPayload = new
        {
            finalUsage.UsedTokens,
            finalUsage.MaxContextTokens,
            finalUsage.Percent,
            StateDurations = turnContext.StateDurationMs,
            StateTransitions = turnContext.StateTransitions.Select(s => s.ToString()).ToList(),
            ExecutedTools = turnContext.ExecutedTools
        };
        yield return new SseEvent("context", Delta: JsonSerializer.Serialize(telemetryPayload));

        yield return new SseEvent("done");
    }

    /// <summary>
    /// 检索本地知识库并格式化为 RAG 知识注入提示词（用于 OpenAI 兼容接口 reference-rag 模式）
    /// </summary>
    public async Task<string> RetrieveRagContextAsync(string query, int topK = 5, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(query)) return string.Empty;

            using var scope = _scopeFactory.CreateScope();
            var searchService = scope.ServiceProvider.GetRequiredService<ISearchService>();
            var request = new AIQueryRequest
            {
                Query = query,
                TopK = topK,
                Mode = QueryMode.HybridRerank,
                ContextWindow = 1
            };
            var response = await searchService.SearchAsync(request);

            if (response.Chunks.Count == 0)
                return string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine("## 本地知识库参考片段 (Local Knowledge Base Context)");
            sb.AppendLine("以下内容检索自用户的本地知识库。请优先结合这些参考资料准确回答问题，并在引用的观点后标注对应出处，例如 [1] 或《笔记标题》：\n");

            for (int i = 0; i < response.Chunks.Count; i++)
            {
                var c = response.Chunks[i];
                var title = c.Title ?? Path.GetFileNameWithoutExtension(c.FilePath);
                var heading = string.IsNullOrEmpty(c.HeadingPath) ? "" : $" > {c.HeadingPath}";
                sb.AppendLine($"[{i + 1}] 《{title}》{heading} (来源: {c.FilePath})");
                sb.AppendLine("```markdown");
                sb.AppendLine(c.Content);
                sb.AppendLine("```");
                sb.AppendLine();
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RetrieveRagContextAsync 失败: {Query}", query);
            return string.Empty;
        }
    }

    /// <summary>
    /// 无状态多轮流式生成（供 OpenAI 兼容 POST /v1/chat/completions 调用）
    /// </summary>
    public async IAsyncEnumerable<string> StreamCompletionAsync(
        IList<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var client = _client;
        if (client is null)
        {
            var msg = _isLocalGguf
                ? "本地聊天模型未就绪或未找到模型文件，请在设置中检查模型配置"
                : "Chat API Key 未配置，请在设置中配置 Chat ApiKey 后使用";
            throw new InvalidOperationException(msg);
        }

        _currentRequestExecutedTools.Value = new List<string>();
        options ??= new ChatOptions { Tools = [.. _tools] };
        _lastChatActivityUtc = DateTime.UtcNow;
        var streamedBadge = false;
        await foreach (var update in client.GetStreamingResponseAsync(messages, options, ct))
        {
            if (!streamedBadge && _currentRequestExecutedTools.Value is { Count: > 0 } execTools)
            {
                yield return $"> 🔧 **已执行工具**: {string.Join(", ", execTools.Select(t => $"`{t}`"))}\n\n";
                streamedBadge = true;
            }

            if (!string.IsNullOrEmpty(update.Text))
            {
                yield return update.Text;
            }
        }
    }

    /// <summary>
    /// 无状态多轮单次响应生成（供 OpenAI 兼容 POST /v1/chat/completions 调用）
    /// </summary>
    public async Task<string> CompleteAsync(
        IList<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken ct = default)
    {
        var client = _client;
        if (client is null)
        {
            var msg = _isLocalGguf
                ? "本地聊天模型未就绪或未找到模型文件，请在设置中检查模型配置"
                : "Chat API Key 未配置，请在设置中配置 Chat ApiKey 后使用";
            throw new InvalidOperationException(msg);
        }

        _currentRequestExecutedTools.Value = new List<string>();
        options ??= new ChatOptions { Tools = [.. _tools] };
        _lastChatActivityUtc = DateTime.UtcNow;
        var response = await client.GetResponseAsync(messages, options, ct);
        var rawText = response?.Text ?? string.Empty;
        var sanitized = ChatToolCallingHelper.SanitizeAssistantResponse(rawText);
        if (_currentRequestExecutedTools.Value is { Count: > 0 } finishedTools)
        {
            var badge = $"> 🔧 **已执行工具**: {string.Join(", ", finishedTools.Select(t => $"`{t}`"))}\n\n";
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                sanitized = badge + "工具执行完毕。";
            }
            else if (!sanitized.StartsWith("> 🔧 **已执行工具**", StringComparison.Ordinal))
            {
                sanitized = badge + sanitized;
            }
        }
        else if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = "抱歉，当前模型未能生成有效回答，请重试或调整提问。";
        }
        return sanitized;
    }

    private void RemoveExpiredSessions()
    {
        var cutoff = _timeProvider.GetUtcNow() - _sessionIdleTimeout;
        foreach (var item in _sessions)
            if (item.Value.LastAccessUtc < cutoff)
                _sessions.TryRemove(item.Key, out _);
    }

    internal void TrimHistory(List<ChatMessage> messages)
    {
        var removable = messages.Count - _maxMessagesPerSession;
        if (removable <= 0) return;

        messages.RemoveRange(1, Math.Min(removable, messages.Count - 1));

        // 协议保护：截断后首条非系统消息不能是 Tool 响应，
        // 必须向前丢弃直到遇到首个 User 消息，保证 OpenAI / MAF 会话协议合法性
        while (messages.Count > 1 && messages[1].Role != ChatRole.User)
        {
            messages.RemoveAt(1);
        }
    }

    private sealed class ChatSession(List<ChatMessage> messages, DateTimeOffset lastAccessUtc)
    {
        public List<ChatMessage> Messages { get; } = messages;
        public DateTimeOffset LastAccessUtc { get; set; } = lastAccessUtc;
    }
}
