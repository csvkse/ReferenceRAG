using System;
using System.Collections.Generic;
using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Infrastructure.Features.FileSystem;

/// <summary>
/// 匹配行上下文 Hunk 有限状态机：
/// 使用环形缓冲区管理前置行，使用倒计数管理后置行，并自适应合并临近重叠 Hunk 代码块。
/// </summary>
public sealed class ContextHunkStateMachine
{
    private readonly int _contextBefore;
    private readonly int _contextAfter;
    private readonly Queue<SearchMatchLine> _beforeBuffer;
    private readonly List<SearchHunk> _completedHunks = new();

    private List<SearchMatchLine>? _currentHunkLines;
    private int _trailingCountdown = 0;
    private int _lastEmittedLineNumber = -1;

    public ContextHunkStateMachine(int contextLines)
    {
        _contextBefore = Math.Clamp(contextLines, 0, 5);
        _contextAfter = Math.Clamp(contextLines, 0, 5);
        _beforeBuffer = new Queue<SearchMatchLine>(_contextBefore + 1);
    }

    /// <summary>
    /// 输入单行并驱动状态机演进
    /// </summary>
    public void FeedLine(int lineNumber, string text, bool isMatch, int matchIndex = -1, int matchLength = 0)
    {
        var matchLine = new SearchMatchLine(lineNumber, text, isMatch, matchIndex, matchLength);

        if (isMatch)
        {
            if (_currentHunkLines != null)
            {
                // 如果当前已有开启的 Hunk，且当前匹配行与上一输出行连续或处于前置上下文覆盖间隙内，合并进当前 Hunk
                if (lineNumber <= _lastEmittedLineNumber + _contextBefore + 1)
                {
                    // 补充可能遗漏的中间缓冲行（若有）
                    while (_beforeBuffer.Count > 0)
                    {
                        var buffered = _beforeBuffer.Dequeue();
                        if (buffered.LineNumber > _lastEmittedLineNumber && buffered.LineNumber < lineNumber)
                        {
                            _currentHunkLines.Add(buffered);
                            _lastEmittedLineNumber = buffered.LineNumber;
                        }
                    }

                    _currentHunkLines.Add(matchLine);
                    _lastEmittedLineNumber = lineNumber;
                    _trailingCountdown = _contextAfter;
                    _beforeBuffer.Clear();
                    return;
                }

                // 距离过大，封口上一个 Hunk
                FlushCurrentHunk();
            }

            // 开启新 Hunk
            _currentHunkLines = new List<SearchMatchLine>();

            // 排空前置上下文环形缓冲区
            while (_beforeBuffer.Count > 0)
            {
                var buffered = _beforeBuffer.Dequeue();
                _currentHunkLines.Add(buffered);
                _lastEmittedLineNumber = buffered.LineNumber;
            }

            _currentHunkLines.Add(matchLine);
            _lastEmittedLineNumber = lineNumber;
            _trailingCountdown = _contextAfter;
            _beforeBuffer.Clear();
        }
        else
        {
            if (_currentHunkLines != null && _trailingCountdown > 0)
            {
                // 处于后置上下文捕获期
                _currentHunkLines.Add(matchLine);
                _lastEmittedLineNumber = lineNumber;
                _trailingCountdown--;

                if (_trailingCountdown == 0)
                {
                    // 后置已捕获满，暂不立刻封口，保留等待潜在临近合并
                }
            }
            else
            {
                // 若已有已满的 Hunk，且当前行已超出前置合并窗口，封口上一个 Hunk
                if (_currentHunkLines != null && lineNumber > _lastEmittedLineNumber + _contextBefore)
                {
                    FlushCurrentHunk();
                }

                // 推进前置环形队列
                if (_contextBefore > 0)
                {
                    if (_beforeBuffer.Count >= _contextBefore)
                    {
                        _beforeBuffer.Dequeue();
                    }
                    _beforeBuffer.Enqueue(matchLine);
                }
            }
        }
    }

    /// <summary>
    /// 文件读取结束时，排空收尾未闭合的 Hunk
    /// </summary>
    public List<SearchHunk> Finish()
    {
        FlushCurrentHunk();
        return _completedHunks;
    }

    private void FlushCurrentHunk()
    {
        if (_currentHunkLines != null && _currentHunkLines.Count > 0)
        {
            var start = _currentHunkLines[0].LineNumber;
            var end = _currentHunkLines[^1].LineNumber;
            _completedHunks.Add(new SearchHunk(start, end, _currentHunkLines));
            _currentHunkLines = null;
            _trailingCountdown = 0;
            _beforeBuffer.Clear();
        }
    }
}
