using System;
using System.Threading;
using System.Threading.Tasks;

namespace ReferenceRAG.Core.Features.DocumentExtraction.Domain;

/// <summary>
/// OCR 识别结果记录
/// </summary>
public sealed record OcrRecognitionResult(
    string Text,
    float Confidence = 1.0f,
    string? ModelName = null);

/// <summary>
/// OCR 识别细粒度进度报告
/// </summary>
public sealed record OcrProgressInfo(
    int CurrentPage,
    int TotalPages,
    string StatusMessage,
    int ExtractedChars = 0)
{
    public double ProgressPercentage => TotalPages > 0 ? Math.Round((double)CurrentPage / TotalPages * 100.0, 1) : 0;
}

/// <summary>
/// 端侧与云端 OCR 推理引擎契约（可拔插适配器模式）
/// 抹平 RapidOCR / PaddleOCR.NET、Windows 原生 OCR 或外部多模态 VLM 推理
/// </summary>
public interface IOcrInferenceEngine
{
    /// <summary>
    /// 当前 OCR 引擎模型与运行环境是否就绪
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// OCR 引擎标识名称
    /// </summary>
    string EngineName { get; }

    /// <summary>
    /// 对图像字节执行文字识别
    /// </summary>
    Task<OcrRecognitionResult> RecognizeImageAsync(ReadOnlyMemory<byte> imageBytes, CancellationToken ct = default);
}
