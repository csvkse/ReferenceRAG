namespace ReferenceRAG.Business.Features.Indexing.StateMachine;

/// <summary>
/// 分块入库生命周期阶段枚举
/// </summary>
public enum ChunkIngestionPhase
{
    /// <summary>
    /// 刚由分块器从文档内容切分生成
    /// </summary>
    Extracted,

    /// <summary>
    /// 正在由适配器链执行准入与信息密度评估
    /// </summary>
    Evaluating,

    /// <summary>
    /// 准入通过，属于有效独立语义分块
    /// </summary>
    Qualified,

    /// <summary>
    /// 准入未通过，已吸收合并至相邻分块 (保留行号跨度)
    /// </summary>
    Absorbed,

    /// <summary>
    /// 准入未通过，纯噪音碎片，已阻断丢弃
    /// </summary>
    Discarded,

    /// <summary>
    /// 文本增强与输入预算校验完毕，准备执行向量推理
    /// </summary>
    EmbeddingReady,

    /// <summary>
    /// GPU/ONNX 向量推理生成完毕
    /// </summary>
    Embedded,

    /// <summary>
    /// 最终持久化至 SQLite 向量库与 BM25 索引
    /// </summary>
    Persisted
}
