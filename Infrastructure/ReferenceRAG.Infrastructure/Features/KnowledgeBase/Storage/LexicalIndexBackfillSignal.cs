using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace ReferenceRAG.Core.Features.KnowledgeBase.Storage;

/// <summary>
/// 词法索引补齐请求信号（进程内去重队列）。
/// 查询读路径发现词法索引未就绪时，只"投递补齐意图"，绝不在请求线程上建索引
/// （历史事故：首次词法查询在请求线程内全量分词建索引，阻塞 16 秒）。
/// </summary>
public static class LexicalIndexBackfillSignal
{
    private static readonly Channel<string> _channel =
        Channel.CreateBounded<string>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });

    private static readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _gate = new();

    /// <summary>请求补齐指定知识库（幂等：同一库在队列中只保留一条）。</summary>
    public static void Request(string? knowledgeBaseId)
    {
        if (string.IsNullOrWhiteSpace(knowledgeBaseId)) return;
        lock (_gate)
        {
            if (!_pending.Add(knowledgeBaseId!)) return;
        }
        _channel.Writer.TryWrite(knowledgeBaseId!);
    }

    /// <summary>读取一个补齐请求；超时返回 null（供后台 Worker 的空闲轮询使用）。</summary>
    public static async Task<string?> ReadAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            var id = await _channel.Reader.ReadAsync(cts.Token);
            lock (_gate) { _pending.Remove(id); }
            return id;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }
}
