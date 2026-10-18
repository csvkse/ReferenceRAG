namespace ReferenceRAG.Business.Features.Search.StateMachine;

/// <summary>
/// 搜索流水线执行状态机阶段枚举
/// </summary>
public enum SearchPipelinePhase
{
    /// <summary>搜索请求已创建</summary>
    Created = 0,

    /// <summary>查询分析、同义词扩展与源过滤准备</summary>
    QueryPrepared = 1,

    /// <summary>BM25 与密集向量并行召回</summary>
    ParallelRetrieving = 2,

    /// <summary>双路召回结果分数融合（加权平均或RRF）</summary>
    HybridFusing = 3,

    /// <summary>知识图谱实体与邻居节点扩展</summary>
    GraphExpanding = 4,

    /// <summary>Cross-Encoder 重排模型精排与分数阈值过滤</summary>
    Reranking = 5,

    /// <summary>噪声分块清洗、上下文构建与响应组装</summary>
    AssemblingResults = 6,

    /// <summary>搜索流水线成功完成</summary>
    Completed = 7,

    /// <summary>流水线执行异常终止</summary>
    Faulted = 8
}
