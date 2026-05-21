using Markdig;
using Markdig.Syntax;
using ReferenceRAG.Core.Helpers;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Core.Services;

/// <summary>
/// Markdown 分段器 - 支持行号记录
/// </summary>
public class MarkdownChunker : IMarkdownChunker
{
    private const int ShortContentThreshold = 200;
    private readonly ITokenizer? _tokenizer;
    private readonly ChunkingOptions _defaultOptions;

    private static readonly System.Text.RegularExpressions.Regex HeadingPattern =
        new(@"^(#{1,6})\s+(.+)$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public MarkdownChunker(ChunkingOptions? options = null, ITokenizer? tokenizer = null)
    {
        _defaultOptions = options ?? new ChunkingOptions();
        _tokenizer = tokenizer;
    }

    public List<ChunkRecord> Chunk(string content, FileRecord file)
    {
        var chunks = Chunk(content, (ChunkingOptions?)null);
        foreach (var chunk in chunks)
            chunk.FileId = file.Id;
        return chunks;
    }

    public List<ChunkRecord> Chunk(string content, ChunkingOptions? options = null)
    {
        // P2: 不修改实例状态，使用局部有效配置
        var effectiveOptions = options ?? _defaultOptions;

        var result = new List<ChunkRecord>();
        var lines = content.Split('\n');
        var chunkIndex = 0;

        var sections = ExtractSections(content, lines);
        // P0: 合并短节（< MinTokens）到相邻节，避免产生无效 chunk
        sections = MergeShortSections(sections, effectiveOptions);

        foreach (var section in sections)
        {
            var sectionTokens = _tokenizer?.CountTokens(section.Content) ?? TokenEstimator.EstimateTokens(section.Content);

            if (sectionTokens <= effectiveOptions.MaxTokens)
            {
                result.Add(CreateChunk(
                    section.Content,
                    lines,
                    section.StartLine,
                    section.EndLine,
                    chunkIndex++,
                    section.HeadingPath,
                    section.Level,
                    Guid.NewGuid().ToString()
                ));
            }
            else
            {
                foreach (var chunk in ChunkSection(section, lines, chunkIndex, Guid.NewGuid().ToString(), effectiveOptions))
                {
                    result.Add(chunk);
                    chunkIndex++;
                }
            }
        }

        return result;
    }

    // ── 短节合并 (P0) ─────────────────────────────────────────────────────────

    /// <summary>
    /// 将 token 数 &lt; MinTokens 的节合并到相邻节，避免产生纯标题等无效 chunk。
    /// </summary>
    private List<Section> MergeShortSections(List<Section> sections, ChunkingOptions options)
    {
        if (sections.Count <= 1) return sections;

        var result = new List<Section>();
        Section? pending = null;

        foreach (var section in sections)
        {
            var tokens = _tokenizer?.CountTokens(section.Content) ?? TokenEstimator.EstimateTokens(section.Content);

            if (tokens < options.MinTokens)
            {
                // 短节先缓存，合并到下一节
                pending = pending == null ? section : MergeTwoSections(pending, section);
            }
            else
            {
                // 将缓存的短节前置合并到当前节
                var merged = pending != null ? MergeTwoSections(pending, section) : section;
                pending = null;
                result.Add(merged);
            }
        }

        // 尾部剩余短节追加到最后一节；若整个文档都是短节则直接输出
        if (pending != null)
        {
            if (result.Count > 0)
                result[^1] = MergeTwoSections(result[^1], pending);
            else
                result.Add(pending);
        }

        return result;
    }

    private static Section MergeTwoSections(Section first, Section second)
    {
        return new Section
        {
            Content = first.Content + "\n" + second.Content,
            StartLine = first.StartLine,
            EndLine = second.EndLine,
            HeadingPath = first.HeadingPath ?? second.HeadingPath,
            Level = first.Level > 0 ? first.Level : second.Level
        };
    }

    // ── 章节提取 ──────────────────────────────────────────────────────────────

    private List<Section> ExtractSections(string content, string[] lines)
    {
        var sections = new List<Section>();
        var headingStack = new Stack<(int Level, string Text)>();
        var currentContent = new List<string>();
        int? sectionStart = null;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var lineNum = i + 1;

            var headingMatch = HeadingPattern.Match(line);

            if (headingMatch.Success)
            {
                if (sectionStart.HasValue && currentContent.Count > 0)
                {
                    var headingPath = string.Join("/", headingStack.Select(h => h.Text));
                    sections.Add(new Section
                    {
                        Content = string.Join("\n", currentContent),
                        StartLine = sectionStart.Value,
                        EndLine = lineNum - 1,
                        HeadingPath = headingPath,
                        Level = headingStack.Count > 0 ? headingStack.Peek().Level : 0
                    });
                }

                var level = headingMatch.Groups[1].Value.Length;
                var text = headingMatch.Groups[2].Value.Trim();

                while (headingStack.Count > 0 && headingStack.Peek().Level >= level)
                    headingStack.Pop();
                headingStack.Push((level, text));

                sectionStart = lineNum;
                currentContent = new List<string> { line };
            }
            else
            {
                if (sectionStart == null) sectionStart = lineNum;
                currentContent.Add(line);
            }
        }

        if (sectionStart.HasValue && currentContent.Count > 0)
        {
            var headingPath = string.Join("/", headingStack.Select(h => h.Text));
            sections.Add(new Section
            {
                Content = string.Join("\n", currentContent),
                StartLine = sectionStart.Value,
                EndLine = lines.Length,
                HeadingPath = headingPath,
                Level = headingStack.Count > 0 ? headingStack.Peek().Level : 0
            });
        }

        if (sections.Count == 0)
        {
            sections.Add(new Section
            {
                Content = content,
                StartLine = 1,
                EndLine = lines.Length,
                HeadingPath = null,
                Level = 0
            });
        }

        return sections;
    }

    // ── 过长章节切分 ──────────────────────────────────────────────────────────

    private List<ChunkRecord> ChunkSection(
        Section section,
        string[] allLines,
        int startChunkIndex,
        string fileId,
        ChunkingOptions options)
    {
        var result = new List<ChunkRecord>();
        var paragraphs = ExtractParagraphs(section, allLines);
        var buffer = new List<Paragraph>();
        var bufferTokens = 0;
        var chunkIndex = startChunkIndex;

        foreach (var para in paragraphs)
        {
            var paraTokens = _tokenizer?.CountTokens(para.Content) ?? TokenEstimator.EstimateTokens(para.Content);

            if (paraTokens > options.MaxTokens)
            {
                if (buffer.Count > 0)
                {
                    result.Add(CreateChunkFromParagraphs(buffer, allLines, chunkIndex++, section, fileId));
                    buffer.Clear();
                    bufferTokens = 0;
                }

                foreach (var subChunk in SplitLongParagraph(para, allLines, chunkIndex, section, fileId, options))
                {
                    result.Add(subChunk);
                    chunkIndex++;
                }
            }
            else if (bufferTokens + paraTokens > options.MaxTokens)
            {
                result.Add(CreateChunkFromParagraphs(buffer, allLines, chunkIndex++, section, fileId));

                var overlap = TakeTrailingParagraphs(buffer, options.OverlapTokens);
                buffer.Clear();
                buffer.AddRange(overlap);
                bufferTokens = overlap.Sum(p => _tokenizer?.CountTokens(p.Content) ?? TokenEstimator.EstimateTokens(p.Content));

                buffer.Add(para);
                bufferTokens += paraTokens;
            }
            else
            {
                buffer.Add(para);
                bufferTokens += paraTokens;
            }
        }

        if (buffer.Count > 0)
            result.Add(CreateChunkFromParagraphs(buffer, allLines, chunkIndex, section, fileId));

        return result;
    }

    private List<Paragraph> ExtractParagraphs(Section section, string[] allLines)
    {
        var paragraphs = new List<Paragraph>();
        var lines = section.Content.Split('\n');
        var currentPara = new List<string>();
        int? paraStart = null;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var actualLineNum = section.StartLine + i;

            if (string.IsNullOrWhiteSpace(line))
            {
                if (currentPara.Count > 0 && paraStart.HasValue)
                {
                    paragraphs.Add(new Paragraph
                    {
                        Content = string.Join("\n", currentPara),
                        StartLine = paraStart.Value,
                        EndLine = actualLineNum - 1
                    });
                    currentPara.Clear();
                    paraStart = null;
                }
            }
            else
            {
                if (!paraStart.HasValue) paraStart = actualLineNum;
                currentPara.Add(line);
            }
        }

        if (currentPara.Count > 0 && paraStart.HasValue)
        {
            paragraphs.Add(new Paragraph
            {
                Content = string.Join("\n", currentPara),
                StartLine = paraStart.Value,
                EndLine = section.StartLine + lines.Length - 1
            });
        }

        return paragraphs;
    }

    private List<ChunkRecord> SplitLongParagraph(
        Paragraph para,
        string[] allLines,
        int startChunkIndex,
        Section section,
        string fileId,
        ChunkingOptions options)
    {
        var result = new List<ChunkRecord>();
        var sentences = SplitSentences(para.Content);
        var buffer = new List<string>();
        var bufferTokens = 0;
        int currentLine = para.StartLine;
        var chunkIndex = startChunkIndex;

        foreach (var sentence in sentences)
        {
            var sentenceTokens = _tokenizer?.CountTokens(sentence) ?? TokenEstimator.EstimateTokens(sentence);

            if (bufferTokens + sentenceTokens > options.MaxTokens && buffer.Count > 0)
            {
                result.Add(CreateChunk(
                    string.Join("", buffer),
                    allLines,
                    currentLine,
                    currentLine,
                    chunkIndex++,
                    section.HeadingPath,
                    section.Level,
                    fileId,
                    ChunkType.Forced
                ));
                buffer.Clear();
                bufferTokens = 0;
            }

            buffer.Add(sentence);
            bufferTokens += sentenceTokens;
        }

        if (buffer.Count > 0)
        {
            result.Add(CreateChunk(
                string.Join("", buffer),
                allLines,
                currentLine,
                para.EndLine,
                chunkIndex,
                section.HeadingPath,
                section.Level,
                fileId,
                ChunkType.Forced
            ));
        }

        return result;
    }

    // ── Chunk 构建 ────────────────────────────────────────────────────────────

    private ChunkRecord CreateChunk(
        string content,
        string[] lines,
        int startLine,
        int endLine,
        int chunkIndex,
        string? headingPath,
        int level,
        string fileId,
        ChunkType chunkType = ChunkType.Text)
    {
        float weight = ComputeWeight(level, content);

        int endColumn = lines.Length > 0 && endLine > 0 && endLine <= lines.Length
            ? lines[Math.Min(endLine - 1, lines.Length - 1)].Length
            : 0;

        return new ChunkRecord
        {
            Id = Guid.NewGuid().ToString(),
            FileId = fileId,
            ChunkIndex = chunkIndex,
            Content = content,
            TokenCount = _tokenizer?.CountTokens(content) ?? TokenEstimator.EstimateTokens(content),
            StartLine = startLine,
            EndLine = endLine,
            StartColumn = 1,
            EndColumn = endColumn,
            HeadingPath = headingPath,
            Level = level,
            Weight = weight,
            ChunkType = chunkType
        };
    }

    private ChunkRecord CreateChunkFromParagraphs(
        List<Paragraph> paragraphs,
        string[] allLines,
        int chunkIndex,
        Section section,
        string fileId)
    {
        var content = string.Join("\n\n", paragraphs.Select(p => p.Content));
        var startLine = paragraphs.Min(p => p.StartLine);
        var endLine = paragraphs.Max(p => p.EndLine);

        return CreateChunk(
            content,
            allLines,
            startLine,
            endLine,
            chunkIndex,
            section.HeadingPath,
            section.Level,
            fileId
        );
    }

    // ── 权重计算 (P1: 删除错误的短内容加权) ──────────────────────────────────

    private float ComputeWeight(int level, string content)
    {
        float weight = 1.0f;

        weight *= level switch
        {
            1 => 1.5f,
            2 => 1.3f,
            3 => 1.1f,
            _ => 1.0f
        };

        // P1: 原来 content.Length < 200 时 weight *= 1.2f，
        // 这会让纯标题 chunk 排名更高，已移除。

        return weight;
    }

    // ── 辅助方法 ──────────────────────────────────────────────────────────────

    private List<Paragraph> TakeTrailingParagraphs(List<Paragraph> buffer, int maxTokens)
    {
        if (maxTokens <= 0 || buffer.Count == 0) return new List<Paragraph>();

        var result = new List<Paragraph>();
        var tokens = 0;

        for (int i = buffer.Count - 1; i >= 0; i--)
        {
            var t = _tokenizer?.CountTokens(buffer[i].Content) ?? TokenEstimator.EstimateTokens(buffer[i].Content);
            if (tokens + t > maxTokens && result.Count > 0) break;
            result.Insert(0, buffer[i]);
            tokens += t;
        }

        return result;
    }

    private List<string> SplitSentences(string text)
    {
        var sentences = new List<string>();
        var current = new System.Text.StringBuilder();

        for (int i = 0; i < text.Length; i++)
        {
            current.Append(text[i]);

            if (text[i] == '。' || text[i] == '！' || text[i] == '？' ||
                text[i] == '.' || text[i] == '!' || text[i] == '?')
            {
                if (i + 1 < text.Length && (text[i + 1] == '"' || text[i + 1] == '"' || text[i + 1] == '」'))
                {
                    current.Append(text[i + 1]);
                    i++;
                }

                sentences.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
            sentences.Add(current.ToString());

        return sentences;
    }

    // ── 内部类型 ──────────────────────────────────────────────────────────────

    private class Section
    {
        public string Content { get; set; } = string.Empty;
        public int StartLine { get; set; }
        public int EndLine { get; set; }
        public string? HeadingPath { get; set; }
        public int Level { get; set; }
    }

    private class Paragraph
    {
        public string Content { get; set; } = string.Empty;
        public int StartLine { get; set; }
        public int EndLine { get; set; }
    }
}
