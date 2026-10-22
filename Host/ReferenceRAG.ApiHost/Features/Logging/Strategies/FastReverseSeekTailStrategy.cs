using System.Text;

namespace ReferenceRAG.ApiHost.Features.Logging.Strategies;

/// <summary>
/// 逆向指针流式尾部读取策略 (高性能 O(tail) 算法)
/// 直接从文件尾部向头部倒序寻找换行符，避免大文件从头扫描产生高额 CPU 和 GC 分配开销。
/// </summary>
public sealed class FastReverseSeekTailStrategy : ILogReaderStrategy
{
    private const int ChunkSize = 4096;
    private const int MaxPayloadBytes = 512 * 1024; // 512 KB 上限，防止前端卡顿

    public bool CanHandle(FileInfo fileInfo, int lines) => fileInfo.Exists;

    public async Task<(string Content, bool IsTruncated)> ReadTailAsync(
        string filePath,
        int lines,
        CancellationToken cancellationToken = default)
    {
        lines = Math.Clamp(lines, 1, 5000);

        // 使用 FileShare.ReadWrite 允许与正在写日志的 Serilog/FileLogger 并发共存
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: ChunkSize,
            useAsync: true);

        var fileLength = stream.Length;
        if (fileLength == 0)
        {
            return (string.Empty, false);
        }

        var buffer = new byte[ChunkSize];
        var remainingBytes = fileLength;
        var newlineCount = 0;
        long startOffset = 0;

        // 从文件尾部向前倒序扫描换行符
        while (remainingBytes > 0 && newlineCount < lines)
        {
            var bytesToRead = (int)Math.Min(ChunkSize, remainingBytes);
            var seekPos = remainingBytes - bytesToRead;
            stream.Seek(seekPos, SeekOrigin.Begin);

            var read = await stream.ReadAsync(buffer.AsMemory(0, bytesToRead), cancellationToken);
            if (read == 0) break;

            // 倒序寻找换行符
            for (var i = read - 1; i >= 0; i--)
            {
                // 若文件最末字符恰好为 '\n'，跳过该末尾换行
                if (seekPos + i == fileLength - 1 && buffer[i] == '\n')
                {
                    continue;
                }

                if (buffer[i] == '\n')
                {
                    newlineCount++;
                    if (newlineCount >= lines)
                    {
                        startOffset = seekPos + i + 1;
                        break;
                    }
                }
            }

            if (newlineCount >= lines)
            {
                break;
            }

            remainingBytes -= bytesToRead;
        }

        // 若倒序扫描至文件头部仍未达到 lines，则从头读取
        if (newlineCount < lines)
        {
            startOffset = 0;
        }

        var bytesToFetch = fileLength - startOffset;
        if (bytesToFetch <= 0)
        {
            return (string.Empty, false);
        }

        var isTruncated = false;
        if (bytesToFetch > MaxPayloadBytes)
        {
            startOffset = fileLength - MaxPayloadBytes;
            bytesToFetch = MaxPayloadBytes;
            isTruncated = true;
        }

        stream.Seek(startOffset, SeekOrigin.Begin);
        var contentBuffer = new byte[bytesToFetch];
        var totalRead = 0;
        while (totalRead < bytesToFetch)
        {
            var r = await stream.ReadAsync(contentBuffer.AsMemory(totalRead, (int)bytesToFetch - totalRead), cancellationToken);
            if (r == 0) break;
            totalRead += r;
        }

        var text = Encoding.UTF8.GetString(contentBuffer, 0, totalRead).TrimStart('\uFEFF');
        if (isTruncated)
        {
            text = "[...内容已截断（超过512KB），请点击右上角按钮下载完整日志文件...]\n" + text;
        }

        return (text, isTruncated);
    }
}
