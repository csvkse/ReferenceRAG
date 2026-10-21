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

/// <summary>
/// ONNX GenAI 执行引擎生命周期状态枚举（状态机模式）
/// </summary>
public enum OnnxEngineState
{
    Uninitialized,
    Initializing,
    Ready,
    Generating,
    Degraded,
    Disposed
}

/// <summary>
/// ONNX GenAI 后端执行引擎抽象适配器（适配器模式）
/// </summary>
public interface IOnnxGenAiEngineAdapter : IDisposable
{
    /// <summary>
    /// 是否由底层真实原生硬件加速（DirectML / CUDA）驱动
    /// </summary>
    bool IsNativeAccelerated { get; }

    /// <summary>
    /// 后端引擎名称描述（如 "DirectML 原生硬件加速" 或 "智能上下文感知兜底"）
    /// </summary>
    string BackendDescription { get; }

    /// <summary>
    /// 当前引擎生命周期状态
    /// </summary>
    OnnxEngineState State { get; }

    /// <summary>
    /// 初始化加载模型权重与分词器
    /// </summary>
    Task<bool> InitializeAsync(string modelPath, HardwareAccelerationConfig? config, CancellationToken ct);

    /// <summary>
    /// 执行自回归流式 Token 推理解码
    /// </summary>
    IAsyncEnumerable<string> GenerateTokensAsync(string prompt, int maxTokens, CancellationToken ct);

    /// <summary>
    /// 执行自回归流式 Token 推理解码（提供结构化消息与工具上下文以支持适配层自愈降级）
    /// </summary>
    IAsyncEnumerable<string> GenerateTokensAsync(
        string prompt,
        IList<ChatMessage>? messages,
        IList<AITool>? tools,
        int maxTokens,
        CancellationToken ct) => GenerateTokensAsync(prompt, maxTokens, ct);

    /// <summary>
    /// 显式卸载模型权重并释放 GPU / 内存资源
    /// </summary>
    void Unload();
}
