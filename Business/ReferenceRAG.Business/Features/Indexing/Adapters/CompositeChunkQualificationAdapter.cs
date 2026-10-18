using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Business.Features.Indexing.Adapters;

/// <summary>
/// 组合准入适配器：按优先级组合并执行责任链评估
/// </summary>
public class CompositeChunkQualificationAdapter : IChunkQualificationAdapter
{
    private readonly List<IChunkQualificationAdapter> _adapters;

    public string AdapterName => "CompositeChunkQualificationAdapter";
    public int Priority => 0;

    public CompositeChunkQualificationAdapter()
        : this(null)
    {
    }

    public CompositeChunkQualificationAdapter(IEnumerable<IChunkQualificationAdapter>? adapters)
    {
        if (adapters != null)
        {
            _adapters = adapters.OrderBy(a => a.Priority).ToList();
        }
        else
        {
            // 默认责任链配置
            _adapters = new List<IChunkQualificationAdapter>
            {
                new SeparatorNoiseAdapter(),
                new InformationDensityAdapter()
            }.OrderBy(a => a.Priority).ToList();
        }
    }

    public ChunkQualificationResult Evaluate(ChunkRecord chunk, ChunkEvaluationContext context)
    {
        foreach (var adapter in _adapters)
        {
            var result = adapter.Evaluate(chunk, context);
            if (!result.IsQualified)
            {
                return result; // 短路返回首个不合格判定
            }
        }

        return ChunkQualificationResult.Qualified();
    }
}
