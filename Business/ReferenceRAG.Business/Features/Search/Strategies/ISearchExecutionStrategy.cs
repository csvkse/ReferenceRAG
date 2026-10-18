using ReferenceRAG.Business.Features.Search.Adapters;
using ReferenceRAG.Business.Features.Search.StateMachine;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Business.Features.Search.Strategies;

/// <summary>
/// 搜索策略执行上下文，封装策略所需的适配器、状态机与运行时配置
/// </summary>
public class SearchExecutionContext
{
    public AIQueryRequest Request { get; set; } = new();
    public HashSet<string> EnabledSources { get; set; } = new();
    public ObsidianRagConfig Config { get; set; } = new();
    public SearchPipelineStateMachine StateMachine { get; set; } = new();
    public IBM25RetrievalAdapter BM25Adapter { get; set; } = default!;
    public IDenseVectorRetrievalAdapter VectorAdapter { get; set; } = default!;
    public IRerankExecutionAdapter RerankAdapter { get; set; } = default!;
    public float[]? PrecomputedQueryVector { get; set; }
}

/// <summary>
/// 策略执行阶段性产物
/// </summary>
public class SearchStrategyResult
{
    public List<SearchResult> Results { get; set; } = new();
    public RerankStats? RerankStats { get; set; }
    public bool RerankApplied { get; set; }
}

/// <summary>
/// 统一搜索执行策略接口
/// </summary>
public interface ISearchExecutionStrategy
{
    QueryMode Mode { get; }

    Task<SearchStrategyResult> ExecuteAsync(
        SearchExecutionContext context,
        CancellationToken cancellationToken = default);
}
