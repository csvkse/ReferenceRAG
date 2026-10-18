using System.Diagnostics;
using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Business.Features.Search.StateMachine;

/// <summary>
/// 搜索流水线执行状态机，负责阶段状态流转、耗时追踪与遥测快照生成
/// </summary>
public class SearchPipelineStateMachine
{
    private readonly Stopwatch _phaseStopwatch = new();
    private readonly List<SearchPipelineStepTelemetry> _steps = new();
    private SearchPipelinePhase _currentPhase = SearchPipelinePhase.Created;
    private int _lastInputCandidates = 0;

    public SearchPipelinePhase CurrentPhase => _currentPhase;
    public string StrategyName { get; set; } = string.Empty;
    public ModelExecutionInfo? EmbeddingModel { get; set; }
    public ModelExecutionInfo? RerankModel { get; set; }

    public SearchPipelineStateMachine(string strategyName = "")
    {
        StrategyName = strategyName;
        _phaseStopwatch.Start();
    }

    /// <summary>
    /// 触发状态转移到下一个阶段
    /// </summary>
    public void TransitionTo(
        SearchPipelinePhase nextPhase,
        int inputCandidates = 0,
        int outputCandidates = 0,
        string? details = null)
    {
        _phaseStopwatch.Stop();
        var durationMs = _phaseStopwatch.ElapsedMilliseconds;

        if (_currentPhase != SearchPipelinePhase.Created)
        {
            _steps.Add(new SearchPipelineStepTelemetry
            {
                Phase = _currentPhase.ToString(),
                PhaseName = GetPhaseDisplayName(_currentPhase),
                DurationMs = Math.Max(1, durationMs),
                InputCandidates = inputCandidates > 0 ? inputCandidates : _lastInputCandidates,
                OutputCandidates = outputCandidates,
                Details = details
            });
        }

        _currentPhase = nextPhase;
        _lastInputCandidates = outputCandidates > 0 ? outputCandidates : inputCandidates;
        _phaseStopwatch.Restart();
    }

    /// <summary>
    /// 完成流水线执行
    /// </summary>
    public SearchPipelineTelemetry Complete(int finalCount)
    {
        _phaseStopwatch.Stop();
        var durationMs = _phaseStopwatch.ElapsedMilliseconds;

        _steps.Add(new SearchPipelineStepTelemetry
        {
            Phase = _currentPhase.ToString(),
            PhaseName = GetPhaseDisplayName(_currentPhase),
            DurationMs = Math.Max(1, durationMs),
            InputCandidates = _lastInputCandidates,
            OutputCandidates = finalCount,
            Details = "结果清洗就绪"
        });

        _currentPhase = SearchPipelinePhase.Completed;

        return new SearchPipelineTelemetry
        {
            StrategyName = StrategyName,
            EmbeddingModel = EmbeddingModel,
            RerankModel = RerankModel,
            Steps = _steps
        };
    }

    /// <summary>
    /// 标记执行失败
    /// </summary>
    public SearchPipelineTelemetry Fault(string errorMessage)
    {
        _phaseStopwatch.Stop();
        _steps.Add(new SearchPipelineStepTelemetry
        {
            Phase = SearchPipelinePhase.Faulted.ToString(),
            PhaseName = "执行异常",
            DurationMs = _phaseStopwatch.ElapsedMilliseconds,
            InputCandidates = _lastInputCandidates,
            OutputCandidates = 0,
            Details = errorMessage
        });
        _currentPhase = SearchPipelinePhase.Faulted;

        return new SearchPipelineTelemetry
        {
            StrategyName = StrategyName,
            EmbeddingModel = EmbeddingModel,
            RerankModel = RerankModel,
            Steps = _steps
        };
    }

    public static string GetPhaseDisplayName(SearchPipelinePhase phase) => phase switch
    {
        SearchPipelinePhase.Created => "创建请求",
        SearchPipelinePhase.QueryPrepared => "查询准备",
        SearchPipelinePhase.ParallelRetrieving => "并行召回",
        SearchPipelinePhase.HybridFusing => "混合融合",
        SearchPipelinePhase.GraphExpanding => "图谱扩展",
        SearchPipelinePhase.Reranking => "模型重排",
        SearchPipelinePhase.AssemblingResults => "结果装配",
        SearchPipelinePhase.Completed => "已完成",
        SearchPipelinePhase.Faulted => "异常终止",
        _ => phase.ToString()
    };
}
