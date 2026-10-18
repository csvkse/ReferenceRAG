using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Business.Features.Chunking.ContinuousElements.Strategies;

/// <summary>
/// 连续结构块切分策略工厂
/// </summary>
public class ContinuousChunkingStrategyFactory
{
    private readonly Dictionary<ChunkType, IContinuousChunkingStrategy> _strategies;

    public ContinuousChunkingStrategyFactory(IEnumerable<IContinuousChunkingStrategy>? strategies = null)
    {
        if (strategies != null && strategies.Any())
        {
            _strategies = strategies.ToDictionary(s => s.SupportedType, s => s);
        }
        else
        {
            _strategies = new Dictionary<ChunkType, IContinuousChunkingStrategy>
            {
                [ChunkType.Table] = new TableContinuousChunkingStrategy(),
                [ChunkType.Code] = new CodeBlockContinuousChunkingStrategy(),
                [ChunkType.List] = new ListTreeContinuousChunkingStrategy()
            };
        }
    }

    /// <summary>
    /// 获取指定分块类型对应的连续切分策略
    /// </summary>
    public IContinuousChunkingStrategy? GetStrategy(ChunkType type)
    {
        return _strategies.TryGetValue(type, out var strategy) ? strategy : null;
    }
}
