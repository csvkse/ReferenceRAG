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
/// 端侧 OCR 推理引擎契约（可拔插适配器模式）
/// 预留给 RapidOCR / PaddleOCR.NET 或本地 VLM / 多模态推理
/// </summary>
public interface IOcrInferenceEngine
{
    /// <summary>
    /// 当前 OCR 引擎模型与运行环境是否就绪
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// 对图像字节执行文字识别
    /// </summary>
    Task<OcrRecognitionResult> RecognizeImageAsync(ReadOnlyMemory<byte> imageBytes, CancellationToken ct = default);
}
