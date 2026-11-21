using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Features.DocumentExtraction.Domain;

namespace ReferenceRAG.Core.Features.DocumentExtraction.Engines;

/// <summary>
/// 组合/责任链 OCR 适配器（组合模式与适配器模式）
/// 允许多个引擎（本地 ONNX、云端多模态、Mock）级联，自动选举首个可用引擎
/// </summary>
public sealed class CompositeOcrEngine : IOcrInferenceEngine
{
    private readonly IReadOnlyList<IOcrInferenceEngine> _engines;
    private readonly ILogger<CompositeOcrEngine>? _logger;

    public CompositeOcrEngine(
        IEnumerable<IOcrInferenceEngine>? engines = null,
        ILogger<CompositeOcrEngine>? logger = null)
    {
        _logger = logger;
        _engines = engines?.ToList() ?? new List<IOcrInferenceEngine>();
    }

    public bool IsAvailable => _engines.Any(e => e.IsAvailable);

    public string EngineName
    {
        get
        {
            var active = _engines.FirstOrDefault(e => e.IsAvailable);
            return active != null ? $"Composite({active.EngineName})" : "Composite(None)";
        }
    }

    public async Task<OcrRecognitionResult> RecognizeImageAsync(ReadOnlyMemory<byte> imageBytes, CancellationToken ct = default)
    {
        var active = _engines.FirstOrDefault(e => e.IsAvailable);
        if (active == null)
        {
            _logger?.LogWarning("[CompositeOcrEngine] 无任何可用的 OCR 识别引擎");
            return new OcrRecognitionResult(string.Empty, 0f, EngineName);
        }

        return await active.RecognizeImageAsync(imageBytes, ct);
    }
}
