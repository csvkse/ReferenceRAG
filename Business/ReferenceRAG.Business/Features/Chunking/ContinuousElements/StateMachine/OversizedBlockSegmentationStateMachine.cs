using ReferenceRAG.Business.Features.Chunking.ContinuousElements.Strategies;
using ReferenceRAG.Core.Helpers;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Business.Features.Chunking.ContinuousElements.StateMachine;

/// <summary>
/// 连续结构块切分生命周期状态
/// </summary>
public enum ContinuousBlockSegmentationState
{
    Detected,
    BudgetInspected,
    ContextExtracted,
    Segmenting,
    HardLimitValidating,
    Finalizing,
    Completed,
    Failed
}

/// <summary>
/// 超限连续结构块切分执行状态机
/// 全程管控连续结构块从识别、预算评估、上下文抽取、策略切分到硬上限防御的生命周期
/// </summary>
public class OversizedBlockSegmentationStateMachine
{
    public ContinuousBlockSegmentationState CurrentState { get; private set; } = ContinuousBlockSegmentationState.Detected;
    public string? LastError { get; private set; }
    public int ProducedChunkCount { get; private set; }

    /// <summary>
    /// 驱动状态机执行完整的连续元素切分流程
    /// </summary>
    public List<ChunkRecord> Execute(
        IContinuousChunkingStrategy strategy,
        object element,
        string[] allLines,
        ChunkingOptions options,
        ITokenizer? tokenizer,
        string fileId,
        string? headingPath,
        int headingLevel,
        ref int chunkIndexCounter)
    {
        try
        {
            CurrentState = ContinuousBlockSegmentationState.Detected;

            // 1. 预算检查
            CurrentState = ContinuousBlockSegmentationState.BudgetInspected;

            // 2. 上下文抽取与策略执行
            CurrentState = ContinuousBlockSegmentationState.ContextExtracted;
            CurrentState = ContinuousBlockSegmentationState.Segmenting;

            var chunks = strategy.Chunk(
                element,
                allLines,
                options,
                tokenizer,
                fileId,
                headingPath,
                headingLevel,
                ref chunkIndexCounter);

            // 3. 物理硬上限防御与校验
            CurrentState = ContinuousBlockSegmentationState.HardLimitValidating;
            foreach (var chunk in chunks)
            {
                var count = tokenizer?.CountTokens(chunk.Content) ?? TokenEstimator.EstimateTokens(chunk.Content);
                chunk.TokenCount = count;
            }

            // 4. 收尾落盘与完成
            CurrentState = ContinuousBlockSegmentationState.Finalizing;
            ProducedChunkCount = chunks.Count;
            CurrentState = ContinuousBlockSegmentationState.Completed;

            return chunks;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            CurrentState = ContinuousBlockSegmentationState.Failed;
            throw;
        }
    }
}
