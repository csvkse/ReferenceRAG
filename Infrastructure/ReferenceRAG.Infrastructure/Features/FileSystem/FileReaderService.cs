using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Infrastructure.Features.FileSystem;

public sealed partial class FileReaderService : IFileReaderService
{
    private const int MaxLineChars = 65536; // 单行字符上限 64KB，防止极端压缩文件引发内存风暴
    private const int MaxAllowedReadLines = 2000; // 单次读取最大安全行数
    private readonly IVectorStore? _vectorStore;
    private readonly ILogger<FileReaderService>? _logger;

    public FileReaderService(IVectorStore? vectorStore = null, ILogger<FileReaderService>? logger = null)
    {
        _vectorStore = vectorStore;
        _logger = logger;
    }

    public async Task<FileLineRangeResult> ReadLineRangeAsync(
        string normalizedPath,
        int startLine = 1,
        int? endLine = null,
        long byteOffset = 0,
        CancellationToken ct = default)
    {
        if (!File.Exists(normalizedPath))
        {
            throw new FileNotFoundException($"文件不存在: {normalizedPath}", normalizedPath);
        }

        var fileInfo = new FileInfo(normalizedPath);
        if (await IsBinaryFileAsync(normalizedPath, ct))
        {
            throw new InvalidOperationException("检测到二进制文件，无法执行按行流式读取");
        }

        var start = Math.Max(1, startLine);
        var targetLineCount = endLine.HasValue && endLine.Value >= start
            ? Math.Min(endLine.Value - start + 1, MaxAllowedReadLines)
            : 200;
        var targetEnd = start + targetLineCount - 1;

        var lines = new List<string>(Math.Min(targetLineCount, 500));
        var currentLine = 1;
        var hasMore = false;
        long nextByteOffset = 0;
        var reachedEof = false;

        await using var fs = new FileStream(normalizedPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (byteOffset > 0 && byteOffset < fs.Length)
        {
            fs.Seek(byteOffset, SeekOrigin.Begin);
            currentLine = start; // 信任调用方传入的精确 byteOffset 对应的行号
        }

        using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 8192);

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line == null)
            {
                reachedEof = true;
                break;
            }

            if (line.Length > MaxLineChars)
            {
                line = line[..MaxLineChars] + " ... [截断]";
            }

            if (currentLine >= start && currentLine <= targetEnd)
            {
                lines.Add(line);
            }
            else if (currentLine > targetEnd)
            {
                hasMore = true;
                nextByteOffset = fs.Position;
                break;
            }

            currentLine++;
        }

        var totalLines = reachedEof ? (currentLine - 1) : -1;
        var content = string.Join("\n", lines);

        return new FileLineRangeResult(
            Path: normalizedPath,
            Name: fileInfo.Name,
            StartLine: start,
            EndLine: start + lines.Count - 1,
            TotalLines: totalLines,
            ReturnedLines: lines.Count,
            HasMore: hasMore,
            NextStartLine: hasMore ? (targetEnd + 1) : 0,
            NextByteOffset: nextByteOffset,
            Lines: lines,
            Content: content
        );
    }

    public async Task<FileMetadataResult> GetMetadataAsync(string normalizedPath, CancellationToken ct = default)
    {
        if (!File.Exists(normalizedPath))
        {
            throw new FileNotFoundException($"文件不存在: {normalizedPath}", normalizedPath);
        }

        var fileInfo = new FileInfo(normalizedPath);
        var ext = fileInfo.Extension.ToLowerInvariant();
        var isMarkdown = ext is ".md" or ".markdown" or ".mdown" or ".mkd";
        var isBinary = await IsBinaryFileAsync(normalizedPath, ct);

        if (isBinary)
        {
            return new FileMetadataResult(
                Path: normalizedPath,
                Name: fileInfo.Name,
                Extension: ext,
                SizeBytes: fileInfo.Length,
                TotalLines: 0,
                LineEnding: "None",
                ModifiedUtc: fileInfo.LastWriteTimeUtc,
                IsBinary: true,
                IsMarkdown: false,
                Frontmatter: new Dictionary<string, string>(),
                Headings: new List<HeadingOutlineItem>()
            );
        }

        var headings = new List<HeadingOutlineItem>();
        var frontmatter = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var totalLines = 0;
        var crlfCount = 0;
        var lfOnlyCount = 0;

        // 状态机流转变量
        var inStart = true;
        var inFrontmatter = false;
        var inCodeBlock = false;

        await using var fs = new FileStream(normalizedPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 8192);

        while (!ct.IsCancellationRequested)
        {
            var rawLine = await reader.ReadLineAsync(ct);
            if (rawLine == null) break;
            totalLines++;

            var line = rawLine.TrimEnd();

            // 1. 首行探测 Frontmatter 开始
            if (inStart)
            {
                inStart = false;
                if (isMarkdown && line.Trim() == "---")
                {
                    inFrontmatter = true;
                    continue;
                }
            }

            // 2. Frontmatter 解析状态
            if (inFrontmatter)
            {
                if (line.Trim() == "---")
                {
                    inFrontmatter = false; // Frontmatter 结束
                    continue;
                }

                var colonIdx = line.IndexOf(':');
                if (colonIdx > 0)
                {
                    var k = line[..colonIdx].Trim();
                    var v = line[(colonIdx + 1)..].Trim().Trim('"', '\'');
                    if (!string.IsNullOrEmpty(k) && !frontmatter.ContainsKey(k))
                    {
                        frontmatter[k] = v;
                    }
                }
                continue;
            }

            // 3. 代码块状态机切换 (遇到 ``` 或 ~~~ 开启/关闭)
            if (isMarkdown && (line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("~~~", StringComparison.Ordinal)))
            {
                inCodeBlock = !inCodeBlock;
                continue;
            }

            // 4. 正文标题识别 (仅在非代码块中生效)
            if (isMarkdown && !inCodeBlock && line.StartsWith('#'))
            {
                var level = 0;
                while (level < line.Length && line[level] == '#')
                {
                    level++;
                }

                if (level >= 1 && level <= 6 && level < line.Length && char.IsWhiteSpace(line[level]))
                {
                    var title = line[level..].Trim();
                    if (!string.IsNullOrWhiteSpace(title))
                    {
                        headings.Add(new HeadingOutlineItem(level, title, totalLines));
                    }
                }
            }
        }

        // 换行符类型研判
        var lineEnding = (crlfCount > 0 && lfOnlyCount > 0) ? "Mixed" : (crlfCount > 0 ? "CRLF" : "LF");

        // 联动 RAG 向量知识库分块统计
        int? ragChunkCount = null;
        DateTime? ragLastIndexed = null;
        if (_vectorStore != null)
        {
            try
            {
                var fileRecord = await _vectorStore.GetFileByPathAsync(normalizedPath, ct);
                if (fileRecord != null)
                {
                    ragChunkCount = fileRecord.ChunkCount;
                    ragLastIndexed = fileRecord.IndexedAt;
                }
            }
            catch { /* 忽略向量库查询瞬态异常 */ }
        }

        return new FileMetadataResult(
            Path: normalizedPath,
            Name: fileInfo.Name,
            Extension: ext,
            SizeBytes: fileInfo.Length,
            TotalLines: totalLines,
            LineEnding: lineEnding,
            ModifiedUtc: fileInfo.LastWriteTimeUtc,
            IsBinary: false,
            IsMarkdown: isMarkdown,
            Frontmatter: frontmatter,
            Headings: headings,
            RagChunkCount: ragChunkCount,
            RagLastIndexed: ragLastIndexed
        );
    }

    private static async Task<bool> IsBinaryFileAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new byte[Math.Min((int)fs.Length, 8192)];
            var read = await fs.ReadAsync(buffer.AsMemory(), ct);
            return buffer.Take(read).Contains((byte)0);
        }
        catch
        {
            return false;
        }
    }
}
