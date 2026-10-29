namespace ReferenceRAG.Business.Features.Chat.Services;

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// 流式重复生成熔断器 (Repetition Circuit Breaker Guardrail)
/// 针对大语言模型自回归生成中的病态重复循环（Pathological Repetition Degeneration）提供实时滑动窗口监控。
/// 包含两项核心检测算法：
/// 1. 紧密周期性文本块检测 (Consecutive Periodic Chunk): 检测形如 P + P + P 的连续死循环短语；
/// 2. 窗口内长句高频重现检测 (Windowed Long-Sentence Duplication): 检测结构化场景/列表（如场景一、场景二、场景三）中重复出现的长句台词与段落。
/// </summary>
public class RepetitionCircuitBreaker
{
    private readonly int _maxHistoryLength;
    private readonly int _minChunkLength;
    private readonly int _minSentenceLength;
    private readonly int _minRepetitions;

    private readonly StringBuilder _buffer = new();
    private bool _isBroken = false;
    private string? _breakReason = null;

    /// <summary>
    /// 初始化流式重复熔断器
    /// </summary>
    /// <param name="maxHistoryLength">滑动窗口最大字符保留数（默认 2048 字符）</param>
    /// <param name="minChunkLength">连续周期性重复块的最小字符数（默认 16 字符，避免误杀短词如“哈哈哈哈”）</param>
    /// <param name="minSentenceLength">独立长句重复检测的最小字符数（默认 18 字符）</param>
    /// <param name="minRepetitions">触发熔断的重复出现频次阈值（默认 3 次）</param>
    public RepetitionCircuitBreaker(
        int maxHistoryLength = 2048,
        int minChunkLength = 16,
        int minSentenceLength = 18,
        int minRepetitions = 3)
    {
        _maxHistoryLength = Math.Max(256, maxHistoryLength);
        _minChunkLength = Math.Max(10, minChunkLength);
        _minSentenceLength = Math.Max(10, minSentenceLength);
        _minRepetitions = Math.Max(2, minRepetitions);
    }

    /// <summary>当前是否已熔断</summary>
    public bool IsBroken => _isBroken;

    /// <summary>触发熔断的原因说明</summary>
    public string? BreakReason => _breakReason;

    /// <summary>已接收并检验通过的安全历史文本总长度</summary>
    public int ProcessedLength => _buffer.Length;

    /// <summary>
    /// 流式接收增量 Token 并执行自回归循环检测。
    /// </summary>
    /// <param name="token">新到达的文本 Token</param>
    /// <param name="safeChunk">允许发射给前端的安全文本切片（若已熔断且存在多余重复，可能为 null）</param>
    /// <returns>若生成正常返回 true；若检测到病态退化死循环触发熔断，返回 false</returns>
    public bool ProcessToken(string? token, out string? safeChunk)
    {
        if (_isBroken)
        {
            safeChunk = null;
            return false;
        }

        if (string.IsNullOrEmpty(token))
        {
            safeChunk = token;
            return true;
        }

        _buffer.Append(token);

        // 控制滑动窗口大小，防止超长会话中内存无限增长
        if (_buffer.Length > _maxHistoryLength * 2)
        {
            _buffer.Remove(0, _buffer.Length - _maxHistoryLength);
        }

        var currentText = _buffer.ToString();

        // 1. 紧密周期性文本块检测 (Consecutive Periodic Chunk Detection: P + P + P)
        if (CheckConsecutivePeriodicLoop(currentText, out var periodicChunk))
        {
            _isBroken = true;
            _breakReason = $"检测到长度为 {periodicChunk.Length} 的连续周期性文本块重复出现 {_minRepetitions} 次: \"{TruncateForLog(periodicChunk)}\"";
            safeChunk = null;
            return false;
        }

        // 2. 滑动窗口内长句/长段落高频重复检测 (Windowed Long-Sentence Duplication)
        if (CheckWindowSentenceDuplication(currentText, out var repeatedSentence, out var count))
        {
            _isBroken = true;
            _breakReason = $"检测到长度为 {repeatedSentence.Length} 的核心语句在窗口内重复出现 {count} 次: \"{TruncateForLog(repeatedSentence)}\"";
            safeChunk = null;
            return false;
        }

        safeChunk = token;
        return true;
    }

    /// <summary>
    /// 连续周期循环检测：在文本尾部查找是否存在长度 L 且连续紧挨着出现 >= _minRepetitions 次的子串
    /// </summary>
    private bool CheckConsecutivePeriodicLoop(string text, out string matchedChunk)
    {
        matchedChunk = string.Empty;
        int textLen = text.Length;
        int maxL = Math.Min(300, textLen / _minRepetitions);

        for (int l = maxL; l >= _minChunkLength; l--)
        {
            string candidate = text.Substring(textLen - l, l);

            // 若候选块仅由空白或标点组成，忽略
            if (string.IsNullOrWhiteSpace(candidate)) continue;

            bool allMatch = true;
            for (int r = 1; r < _minRepetitions; r++)
            {
                int startIndex = textLen - ((r + 1) * l);
                if (startIndex < 0)
                {
                    allMatch = false;
                    break;
                }

                if (!text.Substring(startIndex, l).Equals(candidate, StringComparison.Ordinal))
                {
                    allMatch = false;
                    break;
                }
            }

            if (allMatch)
            {
                matchedChunk = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 窗口长句/长段落重复检测：分析最近输入的文本片段中，是否已累计出现过 >= _minRepetitions 次的长句子
    /// </summary>
    private bool CheckWindowSentenceDuplication(string text, out string matchedSentence, out int matchCount)
    {
        matchedSentence = string.Empty;
        matchCount = 0;

        // 仅在文本达到一定规模时进行句子级扫描
        if (text.Length < _minSentenceLength * _minRepetitions) return false;

        // 提取候选长句：按换行、句号、分号等断句符号拆分
        var lines = text.Split(new[] { '\n', '\r', '。', '；', '！', '？' }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return false;

        // 取最近生成的几行（例如倒数 1~3 行）作为待检目标
        int checkBackCount = Math.Min(3, lines.Length);
        for (int i = lines.Length - 1; i >= lines.Length - checkBackCount; i--)
        {
            var rawLine = lines[i].Trim();
            // 去除常见序号、前缀符号与章节/场景前缀，提取核心语料
            var normalizedLine = Regex.Replace(rawLine, @"^(?:\d+[\.\、\:\)]|\-|\*|\#+|(?:第[一二三四五六七八九十0-9]+[章节步条点项]|场景[一二三四五六七八九十0-9]+|模块[一二三四五六七八九十0-9]+)[：:\s])\s*", "").Trim();

            if (normalizedLine.Length < _minSentenceLength) continue;

            // 统计该核心语句在全文中出现的次数
            int occurrences = CountOccurrences(text, normalizedLine);
            if (occurrences >= _minRepetitions)
            {
                matchedSentence = normalizedLine;
                matchCount = occurrences;
                return true;
            }
        }

        return false;
    }

    private static int CountOccurrences(string source, string pattern)
    {
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(pattern)) return 0;
        int count = 0;
        int idx = 0;
        while ((idx = source.IndexOf(pattern, idx, StringComparison.Ordinal)) != -1)
        {
            count++;
            idx += pattern.Length;
        }
        return count;
    }

    private static string TruncateForLog(string s, int maxLength = 40)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var clean = s.Replace("\r", " ").Replace("\n", " ");
        return clean.Length <= maxLength ? clean : clean.Substring(0, maxLength) + "...";
    }

    /// <summary>重置熔断器状态</summary>
    public void Reset()
    {
        _buffer.Clear();
        _isBroken = false;
        _breakReason = null;
    }
}
