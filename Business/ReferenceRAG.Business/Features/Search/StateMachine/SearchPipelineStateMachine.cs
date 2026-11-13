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
    private int _currentInput = 0;
    private string? _currentDetails;
    private int _lastInputCandidates = 0;

    public SearchPipelinePhase CurrentPhase => _currentPhase;
    public string StrategyName { get; set; } = string.Empty;
    public ModelExecutionInfo? EmbeddingModel { get; set; }
    public ModelExecutionInfo? RerankModel { get; set; }
    public bool IsDegraded { get; private set; } = false;
    public string? DegradeReason { get; private set; }

    public void RecordDegradation(string reason)
    {
        IsDegraded = true;
        DegradeReason = reason;
    }

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
            // 本方法负责"结算上一阶段"：
            //   Details / InputCandidates 取「进入该阶段时」记录的值（谁被记录，就归属于谁）；
            //   OutputCandidates 取本次调用传入的 outputCandidates（上一阶段实际产出的候选数）。
            // 历史实现把本次调用的 details 写进上一阶段，导致前端文案整体错位一个阶段。
            _steps.Add(new SearchPipelineStepTelemetry
            {
                Phase = _currentPhase.ToString(),
                PhaseName = GetPhaseDisplayName(_currentPhase),
                DurationMs = Math.Max(1, durationMs),
                InputCandidates = _currentInput,
                OutputCandidates = outputCandidates,
                Details = _currentDetails
            });
        }

        _currentPhase = nextPhase;
        _currentInput = inputCandidates > 0 ? inputCandidates : _currentInput;
        _currentDetails = details;
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
            InputCandidates = _currentInput,
            OutputCandidates = finalCount,
            Details = _currentDetails
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
        SearchPipelinePhase.Degrading => "容灾降级",
        SearchPipelinePhase.DegradedRetrieved => "降级召回",
        SearchPipelinePhase.DegradedCompleted => "降级完成",
        _ => phase.ToString()
    };
}
