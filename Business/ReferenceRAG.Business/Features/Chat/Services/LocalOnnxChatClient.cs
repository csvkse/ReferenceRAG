namespace ReferenceRAG.Business.Features.Chat.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;

/// <summary>
/// 基于 ONNX / ONNX Runtime GenAI 的本地大语言模型聊天客户端（实现 Microsoft.Extensions.AI.IChatClient）。
/// 完整支持多轮会话、流式 Token 生成、DirectML / CUDA 硬件加速及 ChatML 工具调用拦截。
/// 内部采用适配器模式与策略模式解耦原生推理解码引擎与自愈降级兜底逻辑。
/// </summary>
public sealed class LocalOnnxChatClient : IChatClient, IChatModelCapabilityProvider
{
    private readonly string _modelPath;
    private readonly string _modelName;
    private readonly HardwareAccelerationConfig? _acceleration;
    private readonly ILogger? _logger;
    private readonly IOnnxGenAiEngineAdapter? _customAdapter;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    private IOnnxGenAiEngineAdapter? _engineAdapter;
    private bool _initialized;
    private bool _isSimulationMode;
    private bool _disposed;

    public string ModelPath => _modelPath;
    public string ModelName => _modelName;
    public bool IsSimulationMode => _isSimulationMode;
    public bool IsInitialized => _initialized;
    public bool IsModelLoaded => _initialized && !_isSimulationMode;
    public IOnnxGenAiEngineAdapter? EngineAdapter => _engineAdapter;

    private readonly OnnxVisionMatchingStrategy _visionMatcher = new();

    public ChatModelCapabilities GetCapabilities()
    {
        var match = _visionMatcher.Match(_modelName, _modelPath);
        var backendDesc = _engineAdapter?.BackendDescription ?? "本地 ONNX Runtime GenAI 引擎";
        return new(
            SupportsVision: match.SupportsVision,
            SupportsToolCalling: true,
            ModelName: _modelName,
            BackendType: "LocalOnnx",
            MaxContextTokens: 8192,
            Description: backendDesc,
            VisionUnsupportedReason: match.SupportsVision ? null : match.GuidanceMessage,
            VisionStatus: match.Status.ToString(),
            MatchedProjectorPath: match.MatchedProjectorPath,
            GuidanceMessage: match.GuidanceMessage);
    }

    public LocalOnnxChatClient(
        string modelPath,
        string modelName,
        HardwareAccelerationConfig? acceleration = null,
        ILogger? logger = null,
        IOnnxGenAiEngineAdapter? engineAdapter = null)
    {
        _modelPath = modelPath;
        _modelName = string.IsNullOrWhiteSpace(modelName) ? "Local-ONNX" : modelName;
        _acceleration = acceleration;
        _logger = logger;
        _customAdapter = engineAdapter;
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized) return;

        await _semaphore.WaitAsync(ct);
        try
        {
            if (_initialized) return;

            if (_customAdapter != null)
            {
                _engineAdapter = _customAdapter;
                await _engineAdapter.InitializeAsync(_modelPath, _acceleration, ct);
                _isSimulationMode = !_engineAdapter.IsNativeAccelerated;
                _initialized = true;
                return;
            }

            if (string.IsNullOrWhiteSpace(_modelPath) || (!File.Exists(_modelPath) && !Directory.Exists(_modelPath)))
            {
                _logger?.LogWarning("[LocalOnnxChat] ONNX 模型路径不存在: '{Path}'，启用模拟就绪模式", _modelPath);
                var fallback = new ContextAwareFallbackAdapter(_modelName, _logger);
                await fallback.InitializeAsync(_modelPath, _acceleration, ct);
                _engineAdapter = fallback;
                _isSimulationMode = true;
                _initialized = true;
                return;
            }

            var backend = _acceleration?.Backend.ToString() ?? "DirectML";
            _logger?.LogInformation(
                "[LocalOnnxChat] 正在初始化本地 ONNX Chat 引擎: ModelName={ModelName}, Path={Path}, Backend={Backend}",
                _modelName, _modelPath, backend);

            // 自愈并识别 OGA 标准模型目录（支持 model.onnx 物理路径、模型根目录及带有 gpu/cpu 分支子目录的标准分发结构）
            var ogaDir = OnnxGenAiPathResolver.ResolveModelDirectory(_modelPath);

            if (!string.IsNullOrEmpty(ogaDir) && OnnxGenAiPathResolver.IsValidOgaDirectory(ogaDir))
            {
                try
                {
                    _logger?.LogInformation("[LocalOnnxChat] 成功识别并解析 OGA 物理模型目录: '{OgaDir}'", ogaDir);
                    var nativeAdapter = new NativeOgaDirectMlAdapter(_logger);
                    var initOk = await nativeAdapter.InitializeAsync(ogaDir, _acceleration, ct);
                    if (initOk)
                    {
                        _engineAdapter = nativeAdapter;
                        _isSimulationMode = false;
                        _initialized = true;
                        _logger?.LogInformation("[LocalOnnxChat] 原生 DirectML OGA 对话模型环境就绪: {ModelName} ({Dir})", _modelName, ogaDir);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "[LocalOnnxChat] 原生 OGA 引擎加载失败，准备降级至自愈模式: {Message}", ex.Message);
                }
            }

            // 降级自愈适配器
            _logger?.LogWarning("[LocalOnnxChat] 目标目录未满足原生 OGA 运行条件，启用自愈降级适配器: Path={Path}", _modelPath);
            var fallbackAdapter = new ContextAwareFallbackAdapter(_modelName, _logger);
            await fallbackAdapter.InitializeAsync(_modelPath, _acceleration, ct);
            _engineAdapter = fallbackAdapter;
            _isSimulationMode = true;
            _initialized = true;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await EnsureInitializedAsync(cancellationToken);

        var messageList = chatMessages as IList<ChatMessage> ?? chatMessages.ToList();

        // 确定性工具意图分发器（对显式指令如 'GetIndexStatus'、'搜索: xxx' 实施 100% 确定性分发，杜绝小模型幻觉漏调）
        var lastMsg = messageList.LastOrDefault();
        if (lastMsg?.Role == ChatRole.User &&
            !string.IsNullOrWhiteSpace(lastMsg.Text) &&
            options?.Tools != null &&
            options.Tools.Count > 0 &&
            ChatToolCallingHelper.TryDetectDirectToolCall(lastMsg.Text, options.Tools, out var directName, out var directArgs))
        {
            var callId = $"call_{Guid.NewGuid():N}";
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents = [new FunctionCallContent(callId, directName!, directArgs)],
                ModelId = _modelName
            };
            yield break;
        }

        var caps = GetCapabilities();
        var prompt = ChatToolCallingHelper.BuildChatMlPrompt(
            messageList,
            options?.Tools,
            8192,
            options?.MaxOutputTokens ?? 4096,
            _modelName,
            supportsVision: caps.SupportsVision);

        // 流式生成与工具调用拦截
        var buffer = new StringBuilder();
        bool inToolCall = false;

        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            var tokens = _engineAdapter!.GenerateTokensAsync(
                prompt,
                messageList,
                options?.Tools,
                options?.MaxOutputTokens ?? 4096,
                cancellationToken);

            await foreach (var token in tokens)
            {
                if (string.IsNullOrEmpty(token)) continue;

                buffer.Append(token);
                var currentText = buffer.ToString();

                if (!inToolCall)
                {
                    var toolCallIdx = currentText.IndexOf("<tool_call>", StringComparison.OrdinalIgnoreCase);
                    if (toolCallIdx >= 0)
                    {
                        inToolCall = true;
                        var prefix = currentText.Substring(0, toolCallIdx);
                        if (!string.IsNullOrEmpty(prefix))
                        {
                            int openThink = System.Text.RegularExpressions.Regex.Matches(prefix, @"<(?:think|thought)\b[^>]*>", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count;
                            int closeThink = System.Text.RegularExpressions.Regex.Matches(prefix, @"<\/(?:think|thought)\b[^>]*>", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count;
                            if (openThink > closeThink)
                            {
                                prefix = prefix.TrimEnd() + "\n</think>\n\n";
                            }

                            yield return new ChatResponseUpdate
                            {
                                Role = ChatRole.Assistant,
                                Contents = [new TextContent(prefix)],
                                ModelId = _modelName
                            };
                        }
                        buffer.Clear();
                        buffer.Append(currentText.Substring(toolCallIdx));
                    }
                    else
                    {
                        if (currentText.EndsWith("<") || currentText.EndsWith("<t") || currentText.EndsWith("<to") ||
                            currentText.EndsWith("<too") || currentText.EndsWith("<tool") || currentText.EndsWith("<tool_") ||
                            currentText.EndsWith("<tool_c") || currentText.EndsWith("<tool_ca") || currentText.EndsWith("<tool_cal") ||
                            currentText.EndsWith("<tool_call"))
                        {
                            continue;
                        }

                        var textToEmit = buffer.ToString();
                        buffer.Clear();

                        yield return new ChatResponseUpdate
                        {
                            Role = ChatRole.Assistant,
                            Contents = [new TextContent(textToEmit)],
                            ModelId = _modelName
                        };
                    }
                }
                else
                {
                    var closeIdx = currentText.IndexOf("</tool_call>", StringComparison.OrdinalIgnoreCase);
                    if (closeIdx >= 0)
                    {
                        var fullBlock = currentText.Substring(0, closeIdx + 12);
                        var (success, name, args, _) = ChatToolCallingHelper.TryParseToolCall(fullBlock);
                        if (success && !string.IsNullOrEmpty(name))
                        {
                            var callId = $"call_{Guid.NewGuid():N}";
                            yield return new ChatResponseUpdate
                            {
                                Role = ChatRole.Assistant,
                                Contents = [new FunctionCallContent(callId, name, args)],
                                ModelId = _modelName
                            };
                        }

                        inToolCall = false;
                        buffer.Clear();
                        var leftover = currentText.Substring(closeIdx + 12);
                        if (!string.IsNullOrEmpty(leftover))
                        {
                            buffer.Append(leftover);
                        }
                    }
                }
            }

            if (buffer.Length > 0)
            {
                var remaining = buffer.ToString();
                var (success, name, args, _) = ChatToolCallingHelper.TryParseToolCall(remaining);
                if (success && !string.IsNullOrEmpty(name))
                {
                    var callId = $"call_{Guid.NewGuid():N}";
                    yield return new ChatResponseUpdate
                    {
                        Role = ChatRole.Assistant,
                        Contents = [new FunctionCallContent(callId, name, args)],
                        ModelId = _modelName
                    };
                }
                else if (!string.IsNullOrEmpty(remaining))
                {
                    var safeText = ChatToolCallingHelper.SanitizeAssistantResponse(remaining);
                    if (!string.IsNullOrEmpty(safeText))
                    {
                        yield return new ChatResponseUpdate
                        {
                            Role = ChatRole.Assistant,
                            Contents = [new TextContent(safeText)],
                            ModelId = _modelName
                        };
                    }
                }
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var sb = new StringBuilder();
        var nonTextContents = new List<AIContent>();

        await foreach (var update in GetStreamingResponseAsync(chatMessages, options, cancellationToken))
        {
            if (!string.IsNullOrEmpty(update.Text))
            {
                sb.Append(update.Text);
            }

            if (update.Contents != null)
            {
                foreach (var content in update.Contents)
                {
                    if (content is not TextContent)
                    {
                        nonTextContents.Add(content);
                    }
                }
            }
        }

        var messageContents = new List<AIContent>();
        if (sb.Length > 0 || nonTextContents.Count == 0)
        {
            messageContents.Add(new TextContent(sb.ToString()));
        }
        messageContents.AddRange(nonTextContents);

        return new ChatResponse(new[] { new ChatMessage(ChatRole.Assistant, messageContents) })
        {
            ModelId = _modelName
        };
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceKey is null && (serviceType.IsInstanceOfType(this) || serviceType == typeof(LocalOnnxChatClient)))
        {
            return this;
        }
        if (serviceKey is null && _engineAdapter != null && serviceType.IsInstanceOfType(_engineAdapter))
        {
            return _engineAdapter;
        }
        return null;
    }

    /// <summary>
    /// 自适应解析 ONNX GenAI 物理模型目录（转发至全局解析器）
    /// </summary>
    public static string? ResolveOgaModelDirectory(string? modelPath)
        => OnnxGenAiPathResolver.ResolveModelDirectory(modelPath);

    /// <summary>
    /// 显式卸载模型权重并释放资源（与嵌入和重排模型一致）
    /// </summary>
    public void Unload()
    {
        _semaphore.Wait();
        try
        {
            _engineAdapter?.Unload();
            _engineAdapter = null;
            _initialized = false;
            _isSimulationMode = false;
            _logger?.LogInformation("[LocalOnnxChat] ONNX 对话模型已卸载释放");
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Unload();
        _semaphore.Dispose();
    }
}
