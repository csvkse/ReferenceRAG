using System;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Business.Features.Search.Adapters;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Infrastructure.Features.Cognitive;

namespace ReferenceRAG.Business.Features.Search.Cognitive;

/// <summary>
/// 认知证据评估器工厂接口
/// </summary>
public interface ICognitiveEvidenceEvaluatorFactory
{
    /// <summary>
    /// 根据配置解析生效的认知评估适配器
    /// </summary>
    ICognitiveEvidenceEvaluator? GetEvaluator(CognitiveLexicalConfig config);
}

/// <summary>
/// 认知证据评估器工厂实现
/// </summary>
public class CognitiveEvidenceEvaluatorFactory : ICognitiveEvidenceEvaluatorFactory
{
    private readonly Func<IRerankExecutionAdapter?>? _rerankAdapterFactory;
    private readonly Func<ISystemOneClient?>? _jevClientFactory;
    private readonly ILoggerFactory? _loggerFactory;

    public CognitiveEvidenceEvaluatorFactory(
        Func<IRerankExecutionAdapter?>? rerankAdapterFactory,
        Func<ISystemOneClient?>? jevClientFactory = null,
        ILoggerFactory? loggerFactory = null)
    {
        _rerankAdapterFactory = rerankAdapterFactory;
        _jevClientFactory = jevClientFactory;
        _loggerFactory = loggerFactory;
    }

    public CognitiveEvidenceEvaluatorFactory(
        IRerankExecutionAdapter? rerankAdapter = null,
        ISystemOneClient? jevClient = null,
        ILoggerFactory? loggerFactory = null)
        : this(() => rerankAdapter, () => jevClient, loggerFactory)
    {
    }

    public ICognitiveEvidenceEvaluator? GetEvaluator(CognitiveLexicalConfig config)
    {
        if (config == null || config.Backend == CognitiveLexicalBackend.Disabled)
        {
            return null;
        }

        var threshold = config.AcceptanceThreshold > 0 ? config.AcceptanceThreshold : 0.50f;

        switch (config.Backend)
        {
            case CognitiveLexicalBackend.CrossEncoder:
                var rerankAdapter = _rerankAdapterFactory?.Invoke();
                if (rerankAdapter != null && rerankAdapter.IsAvailable)
                {
                    return new CrossEncoderEvidenceAdapter(
                        rerankAdapter,
                        threshold,
                        _loggerFactory?.CreateLogger<CrossEncoderEvidenceAdapter>());
                }
                if (config.EnableFallback)
                {
                    return new StatisticalHeuristicEvidenceAdapter(threshold);
                }
                return null;

            case CognitiveLexicalBackend.Jev:
                var jevClient = _jevClientFactory?.Invoke();
                if (jevClient != null && jevClient.IsConfigured)
                {
                    return new JevSystemOneEvidenceAdapter(
                        jevClient,
                        threshold,
                        _loggerFactory?.CreateLogger<JevSystemOneEvidenceAdapter>());
                }
                if (config.EnableFallback)
                {
                    return new StatisticalHeuristicEvidenceAdapter(threshold);
                }
                return null;

            case CognitiveLexicalBackend.Statistical:
                return new StatisticalHeuristicEvidenceAdapter(threshold);

            default:
                return null;
        }
    }
}
