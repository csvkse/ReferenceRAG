namespace ReferenceRAG.Business.Features.Chunking.Strategies;

/// <summary>
/// 自适应分段策略工厂
/// </summary>
public class AdaptiveStrategyFactory
{
    private readonly Dictionary<string, IAdaptiveChunkingStrategy> _strategies;
    private readonly IAdaptiveChunkingStrategy _defaultStrategy;

    public AdaptiveStrategyFactory(IEnumerable<IAdaptiveChunkingStrategy>? strategies = null)
    {
        _strategies = new Dictionary<string, IAdaptiveChunkingStrategy>(StringComparer.OrdinalIgnoreCase);

        if (strategies != null)
        {
            foreach (var s in strategies)
            {
                _strategies[s.StrategyType] = s;
            }
        }

        // 确保内置 4 大核心策略就绪
        if (!_strategies.ContainsKey("Balanced"))
            _strategies["Balanced"] = new BalancedAdaptiveStrategy();
        if (!_strategies.ContainsKey("PrecisionDense"))
            _strategies["PrecisionDense"] = new PrecisionDenseAdaptiveStrategy();
        if (!_strategies.ContainsKey("DeepNarrative"))
            _strategies["DeepNarrative"] = new DeepNarrativeAdaptiveStrategy();
        if (!_strategies.ContainsKey("CodeFirst"))
            _strategies["CodeFirst"] = new CodeStructureFirstAdaptiveStrategy();

        _defaultStrategy = _strategies["Balanced"];
    }

    public IAdaptiveChunkingStrategy GetStrategy(string? strategyType)
    {
        if (string.IsNullOrWhiteSpace(strategyType))
            return _defaultStrategy;

        return _strategies.TryGetValue(strategyType, out var strategy)
            ? strategy
            : _defaultStrategy;
    }
}
