namespace ReferenceRAG.Business.Features.Chat.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntimeGenAI;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;

/// <summary>
/// 基于 Microsoft.ML.OnnxRuntimeGenAI 的原生 DirectML 硬件加速推理解码适配器。
/// 真正将 ONNX 语言模型权重载入 GPU/显存，并执行 C++ 级自回归流式 Token 推理。
/// </summary>
public sealed class NativeOgaDirectMlAdapter : IOnnxGenAiEngineAdapter
{
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private string _modelPath = string.Empty;
    private volatile OnnxEngineState _state = OnnxEngineState.Uninitialized;
    private bool _disposed;

    private Model? _model;
    private Tokenizer? _tokenizer;

    public bool IsNativeAccelerated => true;
    public string BackendDescription => "Microsoft ONNX Runtime GenAI (DirectML 原生硬件加速)";
    public OnnxEngineState State => _state;

    public NativeOgaDirectMlAdapter(ILogger? logger = null)
    {
        _logger = logger;
    }

    public async Task<bool> InitializeAsync(string modelPath, HardwareAccelerationConfig? config, CancellationToken ct)
    {
        if (_state == OnnxEngineState.Ready) return true;

        if (!OperatingSystem.IsWindows())
        {
            _logger?.LogInformation("[NativeOgaAdapter] DirectML 硬件加速仅在 Windows 平台支持，当前平台将平滑降级");
            _state = OnnxEngineState.Degraded;
            return false;
        }

        await _semaphore.WaitAsync(ct);
        try
        {
            if (_state == OnnxEngineState.Ready) return true;

            _state = OnnxEngineState.Initializing;
            var targetDir = OnnxGenAiPathResolver.ResolveModelDirectory(modelPath) ?? modelPath;
            _modelPath = targetDir;

            if (string.IsNullOrWhiteSpace(targetDir) || !Directory.Exists(targetDir))
            {
                _logger?.LogWarning("[NativeOgaAdapter] 模型目录不存在: '{Path}'", targetDir);
                _state = OnnxEngineState.Degraded;
                return false;
            }

            var hasGenAiConfig = File.Exists(Path.Combine(targetDir, "genai_config.json"));
            var hasModel = File.Exists(Path.Combine(targetDir, "model.onnx")) || Directory.GetFiles(targetDir, "*.onnx").Length > 0;

            if (!hasGenAiConfig || !hasModel)
            {
                _logger?.LogWarning("[NativeOgaAdapter] 目录缺少完整的 OGA 配置规范 (genai_config.json 或 *.onnx): '{Path}'", targetDir);
                _state = OnnxEngineState.Degraded;
                return false;
            }

            return await Task.Run(() =>
            {
                try
                {
                    _logger?.LogInformation("[NativeOgaAdapter] 正在装载 ONNX 原生模型与分词器: '{Path}'", targetDir);
                    _model = new Model(targetDir);
                    _tokenizer = new Tokenizer(_model);
                    _state = OnnxEngineState.Ready;
                    _logger?.LogInformation("[NativeOgaAdapter] 原生 ONNX 模型与分词器装载成功，DirectML 硬件加速就绪");
                    return true;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "[NativeOgaAdapter] 初始化原生 OGA 模型失败，将平滑降级: {Message}", ex.Message);
                    _state = OnnxEngineState.Degraded;
                    CleanupNativeResources();
                    return false;
                }
            }, ct);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async IAsyncEnumerable<string> GenerateTokensAsync(
        string prompt,
        int maxTokens,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_state != OnnxEngineState.Ready)
        {
            _logger?.LogWarning("[NativeOgaAdapter] 引擎未处于就绪状态 (State={State})，无法执行原生推理解码", _state);
            yield break;
        }

        var model = _model;
        var tokenizer = _tokenizer;
        if (model == null || tokenizer == null)
        {
            yield break;
        }

        await _semaphore.WaitAsync(ct);
        _state = OnnxEngineState.Generating;
        try
        {
            using var sequences = tokenizer.Encode(prompt);
            int promptTokenCount = 0;
            try
            {
                promptTokenCount = sequences[0].Length;
            }
            catch
            {
                // In case indexer differs across OGA versions
            }

            using var generatorParams = new GeneratorParams(model);
            int clampedMaxTokens = Math.Clamp(maxTokens, 64, 4096);
            int totalMaxLength = Math.Min(promptTokenCount + clampedMaxTokens, 131072);
            generatorParams.SetSearchOption("max_length", totalMaxLength);

            using var tokenizerStream = tokenizer.CreateStream();
            using var generator = new Generator(model, generatorParams);
            generator.AppendTokenSequences(sequences);

            while (!generator.IsDone())
            {
                ct.ThrowIfCancellationRequested();

                generator.GenerateNextToken();
                var currentSeq = generator.GetSequence(0);
                if (currentSeq.Length > 0)
                {
                    int lastToken = currentSeq[^1];
                    string decodedPiece = tokenizerStream.Decode(lastToken);
                    if (!string.IsNullOrEmpty(decodedPiece))
                    {
                        yield return decodedPiece;
                    }
                }

                await Task.Yield();
            }
        }
        finally
        {
            _state = OnnxEngineState.Ready;
            _semaphore.Release();
        }
    }

    public void Unload()
    {
        _semaphore.Wait();
        try
        {
            CleanupNativeResources();
            _state = OnnxEngineState.Uninitialized;
            _logger?.LogInformation("[NativeOgaAdapter] ONNX 原生模型已卸载并释放显存");
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private void CleanupNativeResources()
    {
        try
        {
            _tokenizer?.Dispose();
            _tokenizer = null;
            _model?.Dispose();
            _model = null;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[NativeOgaAdapter] 释放原生资源时发生异常");
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
