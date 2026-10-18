namespace ReferenceRAG.Business.Features.Indexing.StateMachine;

/// <summary>
/// 索引任务生命周期阶段枚举
/// </summary>
public enum IndexJobPhase
{
    /// <summary>排队中</summary>
    Queued,

    /// <summary>扫描源目录与过滤文件</summary>
    Scanning,

    /// <summary>Phase 1: CPU 并行读文件、分块、哈希比对</summary>
    Preparing,

    /// <summary>Phase 2: GPU/CPU 大批次向量推理</summary>
    Embedding,

    /// <summary>Stage 3: 批量安全写入存储</summary>
    Writing,

    /// <summary>Phase 3: BM25 与知识图谱收尾后处理</summary>
    Finalizing,

    /// <summary>任务成功完成</summary>
    Completed,

    /// <summary>任务异常失败</summary>
    Faulted,

    /// <summary>任务已被用户取消</summary>
    Cancelled
}
