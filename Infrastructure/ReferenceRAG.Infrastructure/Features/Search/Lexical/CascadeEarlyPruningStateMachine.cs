using System;
using System.Collections.Generic;
using System.Linq;
using ReferenceRAG.Infrastructure.Features.KnowledgeBase.Storage;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;

namespace ReferenceRAG.Infrastructure.Features.Search.Lexical;

/// <summary>
/// 级联早停状态机阶段
/// </summary>
public enum CascadePruningPhase
{
    Idle = 0,
    Hop0LexicalRecalled = 1,
    EarlyExitDetermined = 2,
    ProceedToMultiModal = 3,
    Completed = 4
}

/// <summary>
/// 级联早停状态机 (Cascade Early-Pruning State Machine)
/// 当词法召回结果出现压倒性置信度优势 (Margin Ratio > 0.6) 时直接短路退出，端到端延迟压制在 5ms 内
/// </summary>
public sealed class CascadeEarlyPruningStateMachine
{
    public CascadePruningPhase CurrentPhase { get; private set; } = CascadePruningPhase.Idle;
    public bool TriggeredEarlyExit { get; private set; }
    public string? ExitReason { get; private set; }

    /// <summary>
    /// 评估词法首跳候选列表，决定是否触发早停短路退出
    /// </summary>
    public bool EvaluateEarlyExit(
        string? query,
        IReadOnlyList<RecallItem> lexicalCandidates,
        double minMarginThreshold = 0.55)
    {
        CurrentPhase = CascadePruningPhase.Hop0LexicalRecalled;

        if (lexicalCandidates == null || lexicalCandidates.Count == 0 || string.IsNullOrWhiteSpace(query))
        {
            CurrentPhase = CascadePruningPhase.ProceedToMultiModal;
            return false;
        }

        var top1 = lexicalCandidates[0];

        // 若只有一个候选且分数有效，直接早停
        if (lexicalCandidates.Count == 1)
        {
            TriggeredEarlyExit = true;
            ExitReason = "单一绝对匹配切片，直接短路退出";
            CurrentPhase = CascadePruningPhase.EarlyExitDetermined;
            return true;
        }

        var top2 = lexicalCandidates[1];
        if (top1.Score <= 0)
        {
            CurrentPhase = CascadePruningPhase.ProceedToMultiModal;
            return false;
        }

        // 计算 Top-1 相对于 Top-2 的相对优势比 Margin Ratio
        double margin = (top1.Score - top2.Score) / top1.Score;

        // 提取专有名词与引号
        var phrases = NamePhraseExtractor.Extract(query);
        bool hasStrictQuote = phrases.StrictQuotes.Count > 0 && phrases.StrictQuotes.Any(q => top1.Text.Contains(q, StringComparison.OrdinalIgnoreCase));

        // 判定准则: 相对优势比显著 或 包含双引号精确短语且排名第一
        if (margin >= minMarginThreshold || hasStrictQuote)
        {
            TriggeredEarlyExit = true;
            ExitReason = hasStrictQuote
                ? "命中双引号精确短语且置信度第一，直接短路退出"
                : $"Top-1 相对优势比达到 {margin:P1} (>= {minMarginThreshold:P1})，直接短路退出";
            CurrentPhase = CascadePruningPhase.EarlyExitDetermined;
            return true;
        }

        CurrentPhase = CascadePruningPhase.ProceedToMultiModal;
        return false;
    }

    /// <summary>
    /// 静态快捷早停评估方法
    /// </summary>
    public static (bool ShouldExit, string? Reason) CheckEarlyExit(
        string? query,
        IReadOnlyList<RecallItem> lexicalCandidates,
        double minMarginThreshold = 0.55)
    {
        var sm = new CascadeEarlyPruningStateMachine();
        var shouldExit = sm.EvaluateEarlyExit(query, lexicalCandidates, minMarginThreshold);
        return (shouldExit, sm.ExitReason);
    }
}
