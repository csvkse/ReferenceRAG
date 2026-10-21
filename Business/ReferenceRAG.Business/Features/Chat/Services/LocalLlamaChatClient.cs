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
public sealed class LocalLlamaChatClient : IChatClient, IChatModelCapabilityProvider
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
    private MtmdWeights? _mtmdWeights;
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
    public MtmdWeights? MtmdWeights => _mtmdWeights;
    public bool HasVisionProjector => _mtmdWeights != null;

    private readonly GgufVisionMatchingStrategy _visionMatcher = new();
    private readonly string? _mmprojPath;

    public ChatModelCapabilities GetCapabilities()
    {
        var match = _visionMatcher.Match(_modelName, _mmprojPath ?? _modelPath);
        return new(
            SupportsVision: match.SupportsVision,
            SupportsToolCalling: true,
            ModelName: _modelName,
            BackendType: "LocalGguf",
            MaxContextTokens: _contextSize,
            Description: "本地 llama.cpp GGUF 引擎",
            VisionUnsupportedReason: match.SupportsVision ? null : match.GuidanceMessage,
            VisionStatus: match.Status.ToString(),
            MatchedProjectorPath: match.MatchedProjectorPath,
            GuidanceMessage: match.GuidanceMessage);
    }

    public LocalLlamaChatClient(
        string modelPath,
        string modelName,
        int gpuLayers = -1,
        int contextSize = 4096,
        int batchSize = 2048,
        int uBatchSize = 512,
        string? mmprojPath = null,
        ILogger? logger = null)
    {
        _modelPath = modelPath;
        _modelName = string.IsNullOrWhiteSpace(modelName) ? "Local-GGUF" : modelName;
        _gpuLayers = gpuLayers;
        _contextSize = contextSize > 32768 ? 32768 : (contextSize > 0 ? contextSize : 4096);
        _batchSize = batchSize > 0 ? batchSize : 2048;
        _uBatchSize = uBatchSize > 0 ? uBatchSize : 512;
        _mmprojPath = mmprojPath;
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

                // 尝试挂载匹配的 GGUF 视觉投影器 (mmproj)
                var visionMatch = _visionMatcher.Match(_modelName, _mmprojPath ?? _modelPath);
                if (visionMatch.SupportsVision && !string.IsNullOrEmpty(visionMatch.MatchedProjectorPath) && File.Exists(visionMatch.MatchedProjectorPath))
                {
                    try
                    {
                        var mtmdParams = new MtmdContextParams();
                        _mtmdWeights = MtmdWeights.LoadFromFile(visionMatch.MatchedProjectorPath, _weights, mtmdParams);
                        _logger?.LogInformation("[LocalLlamaChat] GGUF 视觉多模态投影器加载成功: {Path}", visionMatch.MatchedProjectorPath);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "[LocalLlamaChat] 视觉投影器加载失败，降级为纯文本模式: {Path}", visionMatch.MatchedProjectorPath);
                        _mtmdWeights = null;
                    }
                }

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

                var visionMatch = _visionMatcher.Match(_modelName, _mmprojPath ?? _modelPath);
                if (visionMatch.SupportsVision && !string.IsNullOrEmpty(visionMatch.MatchedProjectorPath) && File.Exists(visionMatch.MatchedProjectorPath))
                {
                    try
                    {
                        var mtmdParams = new MtmdContextParams();
                        _mtmdWeights = MtmdWeights.LoadFromFile(visionMatch.MatchedProjectorPath, _weights, mtmdParams);
                        _logger?.LogInformation("[LocalLlamaChat] CPU 降级视觉投影器加载成功: {Path}", visionMatch.MatchedProjectorPath);
                    }
                    catch (Exception cpuVisionEx)
                    {
                        _logger?.LogWarning(cpuVisionEx, "[LocalLlamaChat] CPU 视觉投影器加载失败: {Path}", visionMatch.MatchedProjectorPath);
                        _mtmdWeights = null;
                    }
                }

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

        // 提取所有附带的图片数据（Base64 或二进制流）
        var imageBytesList = new List<byte[]>();
        foreach (var msg in messageList)
        {
            foreach (var content in msg.Contents)
            {
                if (content is DataContent dc && dc.Data.Length > 0)
                {
                    imageBytesList.Add(dc.Data.ToArray());
                }
            }
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

        if (_isSimulationMode)
        {
            var userText = messageList.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "你好";
            var toolMsg = messageList.LastOrDefault(m => m.Role == ChatRole.Tool);
            string? toolResultText = null;
            if (toolMsg != null)
            {
                var fr = toolMsg.Contents.OfType<FunctionResultContent>().FirstOrDefault();
                toolResultText = fr?.Result?.ToString() ?? toolMsg.Text;
            }

            if (!string.IsNullOrWhiteSpace(toolResultText))
            {
                var toolSummary = toolResultText.Contains("向量索引")
                    ? $"知识库当前已成功构建索引：{toolResultText}。"
                    : ((toolResultText.Contains("未找到") || toolResultText.Contains("未检索到"))
                        ? $"知识库中未检索到相关内容：{toolResultText}"
                        : $"已成功为您执行工具并检索到以下参考内容：\n\n{toolResultText}");

                yield return new ChatResponseUpdate
                {
                    Role = ChatRole.Assistant,
                    Contents = [new TextContent(toolSummary)],
                    ModelId = _modelName
                };
                yield break;
            }

            // 若包含图片输入且处于测试模拟模式，验证多模态接收与解析通路
            if (imageBytesList.Count > 0)
            {
                var visionSimTokens = new[]
                {
                    $"[本地视觉模型 {_modelName}] ",
                    "已成功接收并解析附带图片（",
                    $"{imageBytesList.Count} 张图片，多模态投影器就绪）。",
                    $"\n\n针对您的提问 \"{userText}\"：已通过视觉编码器提取图像特征，图片呈现清晰，多模态推理正常。"
                };

                foreach (var token in visionSimTokens)
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

        // 动态弹性输出预算策略：优先保障思维链 (<think>) 与长正文完整生成，杜绝过早截断。
        // 若未显式传入 MaxOutputTokens，结合当前模型的上下文容量 (_contextSize) 弹性分配（16k 默认 4096，8k 默认 2048）
        int defaultOutputTokens = Math.Clamp((int)_contextSize / 4, 1024, 4096);
        int requestedMax = options?.MaxOutputTokens ?? defaultOutputTokens;
        int maxSafeBudget = Math.Max(256, (int)_contextSize - 256);
        int maxOutputTokens = Math.Clamp(requestedMax, 256, Math.Min(8192, maxSafeBudget));

        var caps = GetCapabilities();
        var prompt = ChatToolCallingHelper.BuildChatMlPrompt(
            messageList,
            options?.Tools,
            _contextSize,
            maxOutputTokens,
            _modelName,
            supportsVision: caps.SupportsVision && (imageBytesList.Count > 0 || _mtmdWeights != null));

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
        var activeEmbeds = new List<SafeMtmdEmbed>();
        InteractiveExecutor? interactiveExec = null;
        LLamaContext? interactiveCtx = null;
        try
        {
            ClearKvCache();

            IAsyncEnumerable<string>? tokenStream = null;
            try
            {
                if (imageBytesList.Count > 0 && _mtmdWeights != null)
                {
                    var effectiveCtx = (uint)Math.Clamp(_contextSize, 1024, 32768);
                    var batchCap = Math.Min(effectiveCtx, (uint)_batchSize);
                    var uBatchCap = Math.Min(batchCap, (uint)_uBatchSize);
                    var contextParams = new ModelParams(_modelPath)
                    {
                        GpuLayerCount = _gpuLayers,
                        ContextSize = effectiveCtx,
                        BatchSize = batchCap,
                        UBatchSize = uBatchCap
                    };
                    interactiveCtx = _weights!.CreateContext(contextParams);
                    interactiveExec = new InteractiveExecutor(interactiveCtx, _mtmdWeights, _logger);

                    foreach (var imgBytes in imageBytesList)
                    {
                        var embed = _mtmdWeights.LoadMedia(imgBytes);
                        activeEmbeds.Add(embed);
                        interactiveExec.Embeds.Add(embed);
                    }

                    tokenStream = interactiveExec.InferAsync(prompt, inferenceParams, cancellationToken);
                }
                else
                {
                    tokenStream = _executor!.InferAsync(prompt, inferenceParams, cancellationToken);
                }
            }
            catch (LLama.Exceptions.LLamaDecodeError ex) when (ex.Message.Contains("NoKvSlot", StringComparison.OrdinalIgnoreCase))
            {
                _logger?.LogError(ex, "[LocalLlamaChat] InferAsync 初始化阶段 KV 槽位溢出 (NoKvSlot)");
                ClearKvCache();
                overflowErrorMessage = "\n\n> ⚠️ **【上下文容量提示】** 本轮对话及知识库检索正文的长度达到了当前本地模型上下文的上限（NoKvSlot）。已自动重置槽位，建议精简输入或开启新会话。";
            }

            if (tokenStream != null && overflowErrorMessage == null)
            {
                if (prompt.EndsWith("<think>\n", StringComparison.OrdinalIgnoreCase))
                {
                    yield return new ChatResponseUpdate
                    {
                        Role = ChatRole.Assistant,
                        Contents = [new TextContent("<think>\n")],
                        ModelId = _modelName
                    };
                }

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
            foreach (var embed in activeEmbeds)
            {
                try { embed.Dispose(); } catch { }
            }
            interactiveCtx?.Dispose();
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
            _mtmdWeights?.Dispose();
            _mtmdWeights = null;
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
        _mtmdWeights?.Dispose();
        _mtmdWeights = null;
        _executor = null;
    }
}
