using System.Runtime.CompilerServices;
using System.Text;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Features.HardwareAcceleration;

namespace ReferenceRAG.Business.Features.Chat.Services;

/// <summary>
/// 基于 LLamaSharp 的本地 GGUF 大语言模型客户端（实现 Microsoft.Extensions.AI.IChatClient）。
/// 支持 GPU (CUDA) 显存卸载与自动 CPU 降级、流式生成及 ChatML 提示词格式。
/// </summary>
public sealed class LocalLlamaChatClient : IChatClient
{
    private readonly string _modelPath;
    private readonly string _modelName;
    private readonly int _gpuLayers;
    private readonly int _contextSize;
    private readonly int _batchSize;
    private readonly int _uBatchSize;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    private LLamaWeights? _weights;
    private StatelessExecutor? _executor;
    private bool _initialized;
    private bool _isSimulationMode;
    private bool _disposed;

    public string ModelPath => _modelPath;
    public string ModelName => _modelName;
    public int GpuLayers => _gpuLayers;
    public int ContextSize => _contextSize;
    public int BatchSize => _batchSize;
    public int UBatchSize => _uBatchSize;
    public bool IsSimulationMode => _isSimulationMode;
    public bool IsInitialized => _initialized;
    public bool IsModelLoaded => _weights != null && _initialized;

    public LocalLlamaChatClient(
        string modelPath,
        string modelName,
        int gpuLayers = -1,
        int contextSize = 4096,
        int batchSize = 2048,
        int uBatchSize = 512,
        ILogger? logger = null)
    {
        _modelPath = modelPath;
        _modelName = string.IsNullOrWhiteSpace(modelName) ? "Local-GGUF" : modelName;
        _gpuLayers = gpuLayers;
        _contextSize = contextSize > 32768 ? 32768 : (contextSize > 0 ? contextSize : 4096);
        _batchSize = batchSize > 0 ? batchSize : 2048;
        _uBatchSize = uBatchSize > 0 ? uBatchSize : 512;
        _logger = logger;
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized) return;

        await _semaphore.WaitAsync(ct);
        try
        {
            if (_initialized) return;

            if (string.IsNullOrWhiteSpace(_modelPath) || !File.Exists(_modelPath))
            {
                _logger?.LogWarning("[LocalLlamaChat] GGUF 模型文件不存在: '{Path}'，启用模拟降级模式", _modelPath);
                _isSimulationMode = true;
                _initialized = true;
                return;
            }

            if (_gpuLayers != 0)
            {
                LlamaNativeInitializer.EnsureInitialized(preferCuda: true);
            }

            var effectiveCtx = (uint)Math.Clamp(_contextSize, 1024, 32768);
            var batchTokenCapacity = Math.Min(effectiveCtx, (uint)_batchSize);
            var uBatchCapacity = Math.Min(batchTokenCapacity, (uint)_uBatchSize);

            _logger?.LogInformation(
                "[LocalLlamaChat] 正在加载本地 GGUF 模型: {ModelName} ({Path}) [GpuLayers={GpuLayers}, ContextSize={Ctx}, BatchSize={Batch}, UBatchSize={UBatch}]",
                _modelName, _modelPath, _gpuLayers, effectiveCtx, batchTokenCapacity, uBatchCapacity);

            try
            {
                var parameters = new ModelParams(_modelPath)
                {
                    GpuLayerCount = _gpuLayers,
                    ContextSize = effectiveCtx,
                    BatchSize = batchTokenCapacity,
                    UBatchSize = uBatchCapacity
                };

                _weights = await Task.Run(() => LLamaWeights.LoadFromFile(parameters), ct);
                _executor = new StatelessExecutor(_weights, parameters)
                {
                    ApplyTemplate = false
                };
                _initialized = true;
                _logger?.LogInformation("[LocalLlamaChat] GGUF 权重加载成功 (GPU 加速已就绪): {ModelName}", _modelName);
            }
            catch (Exception ex) when (_gpuLayers != 0)
            {
                _logger?.LogWarning(ex, "[LocalLlamaChat] GPU 加载失败，正在降级回退至 CPU 纯算力模式: {ModelPath}", _modelPath);
                var cpuParams = new ModelParams(_modelPath)
                {
                    GpuLayerCount = 0,
                    ContextSize = effectiveCtx,
                    BatchSize = batchTokenCapacity,
                    UBatchSize = uBatchCapacity
                };

                _weights = await Task.Run(() => LLamaWeights.LoadFromFile(cpuParams), ct);
                _executor = new StatelessExecutor(_weights, cpuParams)
                {
                    ApplyTemplate = false
                };
                _initialized = true;
                _logger?.LogInformation("[LocalLlamaChat] CPU 降级加载成功: {ModelName}", _modelName);
            }
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

        if (_isSimulationMode)
        {
            var userText = messageList.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "你好";

            // 若包含工具且用户提出搜索提问，模拟工具调用
            if (options?.Tools != null && options.Tools.Count > 0 &&
                (userText.Contains("知识库") || userText.Contains("搜索") || userText.Contains("查找") || userText.Contains("笔记")))
            {
                var callId = $"call_{Guid.NewGuid():N}";
                yield return new ChatResponseUpdate
                {
                    Role = ChatRole.Assistant,
                    Contents = [new FunctionCallContent(callId, "SearchKnowledgeAsync", new Dictionary<string, object?> { ["query"] = userText, ["topK"] = 5 })],
                    ModelId = _modelName
                };
                yield break;
            }

            var simTokens = new[]
            {
                $"[本地模型 {_modelName}] ",
                "已收到提问：",
                $"\"{userText}\"。",
                "\n\n（当前处于测试模拟模式，未检测到真实 GGUF 权重文件，模型服务就绪）"
            };

            foreach (var token in simTokens)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ChatResponseUpdate
                {
                    Role = ChatRole.Assistant,
                    Contents = [new TextContent(token)],
                    ModelId = _modelName
                };
                await Task.Yield();
            }
            yield break;
        }

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

        var maxOutputTokens = Math.Clamp(options?.MaxOutputTokens ?? 1024, 128, 2048);
        var prompt = ChatToolCallingHelper.BuildChatMlPrompt(messageList, options?.Tools, _contextSize, maxOutputTokens);
        var pipeline = new DefaultSamplingPipeline
        {
            Temperature = options?.Temperature ?? 0.7f,
            TopP = options?.TopP ?? 0.9f
        };
        var inferenceParams = new InferenceParams
        {
            MaxTokens = maxOutputTokens,
            AntiPrompts = ["<|im_end|>", "<|endoftext|>", "<|im_start|>", "\n<|im_start|>", "<|end_of_text|>", "</s>"],
            SamplingPipeline = pipeline,
            OverflowStrategy = ContextOverflowStrategy.TruncateAndReprefill,
            TokensKeep = 512,
            ContextTruncationPercentage = 0.25f
        };

        var maxPromptTokens = _contextSize - inferenceParams.MaxTokens - 32;
        if (maxPromptTokens > 256)
        {
            prompt = TruncatePromptToTokenBudget(prompt, maxPromptTokens);
        }

        var buffer = new StringBuilder();
        bool inToolCall = false;

        await _semaphore.WaitAsync(cancellationToken);
        string? overflowErrorMessage = null;
        try
        {
            ClearKvCache();

            IAsyncEnumerable<string>? tokenStream = null;
            try
            {
                tokenStream = _executor!.InferAsync(prompt, inferenceParams, cancellationToken);
            }
            catch (LLama.Exceptions.LLamaDecodeError ex) when (ex.Message.Contains("NoKvSlot", StringComparison.OrdinalIgnoreCase))
            {
                _logger?.LogError(ex, "[LocalLlamaChat] InferAsync 初始化阶段 KV 槽位溢出 (NoKvSlot)");
                ClearKvCache();
                overflowErrorMessage = "\n\n> ⚠️ **【上下文容量提示】** 本轮对话及知识库检索正文的长度达到了当前本地模型上下文的上限（NoKvSlot）。已自动重置槽位，建议精简输入或开启新会话。";
            }

            if (tokenStream != null && overflowErrorMessage == null)
            {
                var enumerator = tokenStream.GetAsyncEnumerator(cancellationToken);
                while (true)
                {
                    string token;
                    try
                    {
                        if (!await enumerator.MoveNextAsync())
                        {
                            break;
                        }
                        token = enumerator.Current;
                    }
                    catch (LLama.Exceptions.LLamaDecodeError ex) when (ex.Message.Contains("NoKvSlot", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger?.LogError(ex, "[LocalLlamaChat] 生成循环中捕获 NoKvSlot 异常");
                        ClearKvCache();
                        overflowErrorMessage = "\n\n> ⚠️ **【上下文容量提示】** 本轮对话及知识库检索正文的长度达到了当前本地模型上下文的上限（NoKvSlot）。已自动重置槽位，建议精简输入或开启新会话。";
                        break;
                    }

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
                    yield return new ChatResponseUpdate
                    {
                        Role = ChatRole.Assistant,
                        Contents = [new TextContent(remaining)],
                        ModelId = _modelName
                    };
                }
            }
        }
        finally
        {
            ClearKvCache();
            _semaphore.Release();
        }

        if (!string.IsNullOrEmpty(overflowErrorMessage))
        {
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents = [new TextContent(overflowErrorMessage)],
                ModelId = _modelName
            };
            yield break;
        }
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var sb = new StringBuilder();

        await foreach (var update in GetStreamingResponseAsync(chatMessages, options, cancellationToken))
        {
            if (!string.IsNullOrEmpty(update.Text))
            {
                sb.Append(update.Text);
            }
        }

        return new ChatResponse(new[] { new ChatMessage(ChatRole.Assistant, sb.ToString()) })
        {
            ModelId = _modelName
        };
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceKey is null && (serviceType.IsInstanceOfType(this) || serviceType == typeof(LocalLlamaChatClient)))
        {
            return this;
        }
        return null;
    }

    /// <summary>
    /// 显式卸载模型权重并释放底层显存/内存（与嵌入和重排模型一致，按需或空闲自动释放）
    /// </summary>
    public void Unload()
    {
        _semaphore.Wait();
        try
        {
            if (!_initialized && _weights == null) return;
            _logger?.LogInformation("[LocalLlamaChat] 正在卸载 GGUF 权重以释放显存/内存: {ModelName}", _modelName);
            ClearKvCache();
            _weights?.Dispose();
            _weights = null;
            _executor = null;
            _initialized = false;
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            _logger?.LogInformation("[LocalLlamaChat] GGUF 权重卸载完成，显存已释放: {ModelName}", _modelName);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[LocalLlamaChat] 卸载模型权重时发生异常");
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private int CountTokens(string text)
    {
        if (string.IsNullOrEmpty(text) || _weights?.NativeHandle == null) return 0;
        try
        {
            return _weights.NativeHandle.Tokenize(text, false, true, Encoding.UTF8).Length;
        }
        catch
        {
            return (int)(text.Length / 1.5);
        }
    }

    private string TruncatePromptToTokenBudget(string prompt, int maxPromptTokens)
    {
        if (_weights?.NativeHandle == null) return prompt;
        try
        {
            var tokenCount = CountTokens(prompt);
            if (tokenCount <= maxPromptTokens) return prompt;

            _logger?.LogWarning(
                "[LocalLlamaChat] 输入 Token 数量 ({Tokens}) 超过模型上下文安全上限 ({MaxTokens})，正在执行 Token 级安全裁剪...",
                tokenCount, maxPromptTokens);

            int systemEnd = prompt.IndexOf("<|im_end|>\n", StringComparison.Ordinal);
            string systemPart = string.Empty;
            string remainingPart = prompt;

            if (systemEnd > 0)
            {
                systemPart = prompt.Substring(0, systemEnd + 11);
                remainingPart = prompt.Substring(systemEnd + 11);
            }

            var systemTokens = CountTokens(systemPart);
            int budgetForRemaining = Math.Max(64, maxPromptTokens - systemTokens - 24);

            var remainingTokens = CountTokens(remainingPart);
            if (remainingTokens > budgetForRemaining)
            {
                double keepRatio = (double)budgetForRemaining / remainingTokens;
                int targetCharCount = Math.Max(64, (int)(remainingPart.Length * keepRatio * 0.9));
                string truncatedTail = remainingPart.Substring(remainingPart.Length - targetCharCount);
                int tagIdx = truncatedTail.IndexOf("<|im_start|>", StringComparison.Ordinal);
                if (tagIdx >= 0 && tagIdx < truncatedTail.Length - 32)
                {
                    truncatedTail = truncatedTail.Substring(tagIdx);
                }
                remainingPart = "<|im_start|>system\n[提示：输入内容及检索资料已达模型上下文预算上限，较早内容已自动折叠]\n<|im_end|>\n" + truncatedTail;
            }

            return systemPart + remainingPart;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[LocalLlamaChat] Tokenize/Truncate warning");
            return prompt;
        }
    }

    private void ClearKvCache()
    {
        try
        {
            var ctx = _executor?.Context;
            if (ctx != null)
            {
                var handle = ctx.NativeHandle;
                if (handle != null && !handle.IsClosed && !handle.IsInvalid)
                {
                    handle.MemoryClear();
                }
            }
        }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[LocalLlamaChat] MemoryClear warning");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _semaphore.Dispose();
        _weights?.Dispose();
        _weights = null;
        _executor = null;
    }
}
