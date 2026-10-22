namespace ReferenceRAG.Business.Features.Chat.Services;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

/// <summary>
/// 对话与工具调用全生命周期执行状态（有限状态机 FSM）
/// </summary>
public enum ChatTurnState
{
    /// <summary>
    /// 空闲状态
    /// </summary>
    Idle,

    /// <summary>
    /// 前置意图路由阶段（复合策略判定）
    /// </summary>
    Routing,

    /// <summary>
    /// 确定性直接工具分发（跳过大模型 Phase 1 决策）
    /// </summary>
    DirectToolExecuting,

    /// <summary>
    /// 模型 Phase 1 工具决策与推理（挂载 GBNF / Schema 约束）
    /// </summary>
    Phase1Inferencing,

    /// <summary>
    /// 工具执行中（调用底层存储/搜索）
    /// </summary>
    ToolExecuting,

    /// <summary>
    /// 模型 Phase 2 总结生成（强负向约束，防元对话说教）
    /// </summary>
    Phase2Synthesizing,

    /// <summary>
    /// 响应清洗与消毒阶段（剥离 XML 标记与裸 JSON 规整）
    /// </summary>
    OutputSanitizing,

    /// <summary>
    /// 自愈与降级保底阶段（空响应或崩溃自愈，防止白屏）
    /// </summary>
    SelfHealingFallback,

    /// <summary>
    /// 执行完成
    /// </summary>
    Completed,

    /// <summary>
    /// 失败中断
    /// </summary>
    Failed
}

/// <summary>
/// 会话单轮执行上下文
/// </summary>
public class ChatTurnContext
{
    public string SessionId { get; init; } = string.Empty;
    public string UserMessage { get; init; } = string.Empty;
    public List<string> ExecutedTools { get; } = [];
    public string? RawOutput { get; set; }
    public string? SanitizedOutput { get; set; }
    public string? FallbackMessage { get; set; }
    public RoutingDecision? RoutingDecision { get; set; }
    public Dictionary<ChatTurnState, long> StateDurationMs { get; } = [];
    public List<ChatTurnState> StateTransitions { get; } = [];
}

/// <summary>
/// 工具调用执行状态机（状态机模式 State Machine Pattern）
/// 严格管控三端执行生命周期流转，确保状态不可逆转违背、异常路径安全自愈、性能遥测精准留痕。
/// </summary>
public class ToolCallingExecutionStateMachine
{
    private ChatTurnState _currentState = ChatTurnState.Idle;
    private readonly ChatTurnContext _context;
    private readonly Stopwatch _stopwatch = new();
    private ChatTurnState _lastState = ChatTurnState.Idle;

    public ChatTurnState CurrentState => _currentState;
    public ChatTurnContext Context => _context;

    public ToolCallingExecutionStateMachine(ChatTurnContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _context.StateTransitions.Add(ChatTurnState.Idle);
    }

    /// <summary>
    /// 执行状态转移并校验前置守卫
    /// </summary>
    public void TransitionTo(ChatTurnState nextState, string? reason = null)
    {
        ValidateTransition(_currentState, nextState);

        if (_stopwatch.IsRunning)
        {
            _stopwatch.Stop();
            _context.StateDurationMs[_currentState] = _stopwatch.ElapsedMilliseconds;
            _stopwatch.Reset();
        }

        _lastState = _currentState;
        _currentState = nextState;
        _context.StateTransitions.Add(nextState);

        if (nextState != ChatTurnState.Completed && nextState != ChatTurnState.Failed)
        {
            _stopwatch.Start();
        }
    }

    /// <summary>
    /// 触发自愈保底状态转移
    /// </summary>
    public string TriggerSelfHealing(string? reason = null)
    {
        TransitionTo(ChatTurnState.SelfHealingFallback, reason);

        string healedText;
        if (_context.ExecutedTools.Count > 0)
        {
            var badge = $"> 🔧 **已执行工具**: {string.Join(", ", _context.ExecutedTools.Select(t => $"`{t}`"))}\n\n";
            healedText = badge + "工具执行完毕，知识库已完成检索。";
        }
        else
        {
            healedText = _context.FallbackMessage ?? "抱歉，当前模型未能生成有效回答，请重试或调整提问。";
        }

        _context.SanitizedOutput = healedText;
        TransitionTo(ChatTurnState.Completed, "自愈补全完成");
        return healedText;
    }

    private static void ValidateTransition(ChatTurnState current, ChatTurnState next)
    {
        // 允许向最终终止态跳转
        if (next == ChatTurnState.Failed || next == ChatTurnState.SelfHealingFallback)
            return;

        bool valid = (current, next) switch
        {
            (ChatTurnState.Idle, ChatTurnState.Routing) => true,
            (ChatTurnState.Routing, ChatTurnState.DirectToolExecuting) => true,
            (ChatTurnState.Routing, ChatTurnState.Phase1Inferencing) => true,
            (ChatTurnState.Routing, ChatTurnState.Phase2Synthesizing) => true,
            (ChatTurnState.Routing, ChatTurnState.OutputSanitizing) => true,
            (ChatTurnState.DirectToolExecuting, ChatTurnState.ToolExecuting) => true,
            (ChatTurnState.Phase1Inferencing, ChatTurnState.ToolExecuting) => true,
            (ChatTurnState.Phase1Inferencing, ChatTurnState.OutputSanitizing) => true,
            (ChatTurnState.ToolExecuting, ChatTurnState.Phase2Synthesizing) => true,
            (ChatTurnState.ToolExecuting, ChatTurnState.OutputSanitizing) => true,
            (ChatTurnState.Phase2Synthesizing, ChatTurnState.ToolExecuting) => true,
            (ChatTurnState.Phase2Synthesizing, ChatTurnState.OutputSanitizing) => true,
            (ChatTurnState.OutputSanitizing, ChatTurnState.Completed) => true,
            (ChatTurnState.SelfHealingFallback, ChatTurnState.Completed) => true,
            _ => false
        };

        if (!valid)
        {
            throw new InvalidOperationException($"非法状态转移: 无法从 [{current}] 跳转至 [{next}]");
        }
    }
}
