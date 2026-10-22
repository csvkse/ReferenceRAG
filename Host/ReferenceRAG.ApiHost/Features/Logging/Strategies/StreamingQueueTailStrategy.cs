using System.Text;

namespace ReferenceRAG.ApiHost.Features.Logging.Strategies;

/// <summary>
/// 环形队列顺序尾部读取策略 (保底策略)
/// 适用于非定位流或微型日志文件。
/// </summary>
public sealed class StreamingQueueTailStrategy : ILogReaderStrategy
{
    private const int MaxPayloadBytes = 512 * 1024;

    public bool CanHandle(FileInfo fileInfo, int lines) => fileInfo.Exists && fileInfo.Length < 32 * 1024;

    public async Task<(string Content, bool IsTruncated)> ReadTailAsync(
        string filePath,
        int lines,
        CancellationToken cancellationToken = default)
    {
        lines = Math.Clamp(lines, 1, 5000);

        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            useAsync: true);

        using var reader = new StreamReader(stream, Encoding.UTF8);
        var queue = new Queue<string>(lines);

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (queue.Count == lines)
            {
                queue.Dequeue();
            }
            queue.Enqueue(line);
        }

        var text = string.Join(Environment.NewLine, queue);
        var isTruncated = false;
        if (text.Length > MaxPayloadBytes)
        {
            text = text[^MaxPayloadBytes..];
            text = "[...内容已截断（超过512KB），请点击右上角按钮下载完整日志文件...]\n" + text;
            isTruncated = true;
        }

        return (text, isTruncated);
    }
}
