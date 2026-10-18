using System.Diagnostics;

namespace ReferenceRAG.Business.Features.Chunking.StateMachine;

/// <summary>
/// 分段切分执行阶段枚举
/// </summary>
public enum ChunkingPhase
{
    Initialized,
    ResolvingModelProfile,
    DecomposingAst,
    ExecutingAdaptiveStrategy,
    PostProcessingAndMerging,
    Completed,
    Failed
}

/// <summary>
/// 分段状态机单步追踪记录
/// </summary>
public class ChunkingStepTrace
{
    public ChunkingPhase Phase { get; set; }
    public string PhaseName { get; set; } = string.Empty;
    public long DurationMs { get; set; }
    public string? Details { get; set; }
}

/// <summary>
/// 分段切分遥测数据
/// </summary>
public class ChunkingTelemetry
{
    public string Mode { get; set; } = "Fixed";
    public string StrategyName { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public int TargetTokens { get; set; }
    public int EffectiveMaxTokens { get; set; }
    public int InputLength { get; set; }
    public int OutputChunkCount { get; set; }
    public long TotalDurationMs { get; set; }
    public List<ChunkingStepTrace> Steps { get; set; } = new();
}

/// <summary>
/// 分段切分执行状态机
/// </summary>
public class ChunkingExecutionStateMachine
{
    private readonly Stopwatch _stopwatch = new();
    private readonly List<ChunkingStepTrace> _steps = new();
    private Stopwatch _stepStopwatch = new();

    public ChunkingPhase CurrentPhase { get; private set; } = ChunkingPhase.Initialized;
    public ChunkingTelemetry Telemetry { get; } = new();

    public ChunkingExecutionStateMachine()
    {
        _stopwatch.Start();
        _stepStopwatch.Start();
    }

    public void TransitionTo(ChunkingPhase nextPhase, string? details = null)
    {
        _stepStopwatch.Stop();
        _steps.Add(new ChunkingStepTrace
        {
            Phase = CurrentPhase,
            PhaseName = CurrentPhase.ToString(),
            DurationMs = _stepStopwatch.ElapsedMilliseconds,
            Details = details
        });

        CurrentPhase = nextPhase;
        _stepStopwatch = Stopwatch.StartNew();

        if (nextPhase == ChunkingPhase.Completed || nextPhase == ChunkingPhase.Failed)
        {
            _stopwatch.Stop();
            Telemetry.TotalDurationMs = _stopwatch.ElapsedMilliseconds;
            Telemetry.Steps = _steps;
        }
    }
}
