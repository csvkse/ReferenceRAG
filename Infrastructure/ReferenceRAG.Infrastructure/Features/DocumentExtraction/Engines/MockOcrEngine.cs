using System;
using System.Threading;
using System.Threading.Tasks;
using ReferenceRAG.Core.Features.DocumentExtraction.Domain;

namespace ReferenceRAG.Core.Features.DocumentExtraction.Engines;

/// <summary>
/// 模拟/测试专用 OCR 识别引擎（适配器模式实现）
/// 用于单元测试、集成验证与无模型环境下的行为模拟
/// </summary>
public sealed class MockOcrEngine : IOcrInferenceEngine
{
    private readonly Func<ReadOnlyMemory<byte>, string>? _generator;
    private readonly bool _isAvailable;

    public MockOcrEngine(bool isAvailable = true, Func<ReadOnlyMemory<byte>, string>? generator = null)
    {
        _isAvailable = isAvailable;
        _generator = generator;
    }

    public bool IsAvailable => _isAvailable;

    public string EngineName => "MockOcrEngine";

    public Task<OcrRecognitionResult> RecognizeImageAsync(ReadOnlyMemory<byte> imageBytes, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!_isAvailable)
        {
            return Task.FromResult(new OcrRecognitionResult(string.Empty, 0f, EngineName));
        }

        string text = _generator != null ? _generator(ReadOnlyMemory<byte>.Empty) : "模拟识别出的样本文本内容。";
        return Task.FromResult(new OcrRecognitionResult(text, 0.98f, EngineName));
    }
}
