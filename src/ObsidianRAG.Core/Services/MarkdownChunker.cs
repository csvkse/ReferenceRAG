using Markdig;
using Markdig.Syntax;
using ObsidianRAG.Core.Interfaces;
using ObsidianRAG.Core.Models;

namespace ObsidianRAG.Core.Services;

/// <summary>
/// Markdown 分段器 - 支持行号记录
/// </summary>
public class MarkdownChunker : IMarkdownChunker
{
    private readonly ITokenizer? _tokenizer;
    private ChunkingOptions _options;

    public MarkdownChunker(ChunkingOptions? options = null, ITokenizer? tokenizer = null)
    {
        _options = options ?? new ChunkingOptions();
        _tokenizer = tokenizer;
    }

    /// <summary>
    /// 分段 Markdown 内容
    /// </summary>
    public List<ChunkRecord> Chunk(string content, FileRecord file)
    {
        var chunks = Chunk(content, _options);
        // 设置 FileId
        foreach (var chunk in chunks)
        {
            chunk.FileId = file.Id;
        }
        return chunks;
    }

    /// <summary>
    /// 分段 Markdown 内容（接口方法）
    /// </summary>
    public List<ChunkRecord> Chunk(string content, ChunkingOptions? options = null)
    {
        if (options != null) _options = options;
        
        var result = new List<ChunkRecord>();
        var lines = content.Split('\n');
        var chunkIndex = 0;

        // 1. 提取章节
        var sections = ExtractSections(content, lines);

        foreach (var section in sections)
        {
            var sectionTokens = _tokenizer?.CountTokens(section.Content) ?? EstimateTokens(section.Content);

            if (sectionTokens <= _options.MaxTokens)
            {
                // 章节完整，直接输出
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
                // 章节过长，继续切分
                foreach (var chunk in ChunkSection(section, lines, chunkIndex, Guid.NewGuid().ToString()))
                {
                    result.Add(chunk);
                    chunkIndex++;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 提取章节（按标题层级）
    /// </summary>
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

            // 检测标题 (Markdown 格式: # Title)
            var headingMatch = System.Text.RegularExpressions.Regex.Match(line, @"^(#{1,6})\s+(.+)$");

            if (headingMatch.Success)
            {
                // 保存上一个章节
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

                // 更新标题栈
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

        // 最后一个章节
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

        // 如果没有章节，返回整个文档作为一个章节
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

    /// <summary>
    /// 切分过长章节
    /// </summary>
    private List<ChunkRecord> ChunkSection(
        Section section,
        string[] allLines,
        int startChunkIndex,
        string fileId)
    {
        var result = new List<ChunkRecord>();
        var paragraphs = ExtractParagraphs(section, allLines);
        var buffer = new List<Paragraph>();
        var bufferTokens = 0;
        var chunkIndex = startChunkIndex;

        foreach (var para in paragraphs)
        {
            var paraTokens = _tokenizer?.CountTokens(para.Content) ?? EstimateTokens(para.Content);

            // 长段落处理
            if (paraTokens > _options.MaxTokens)
            {
                // 先输出缓冲区
                if (buffer.Count > 0)
                {
                    result.Add(CreateChunkFromParagraphs(buffer, allLines, chunkIndex++, section, fileId));
                    buffer.Clear();
                    bufferTokens = 0;
                }

                // 切分长段落
                foreach (var subChunk in SplitLongParagraph(para, allLines, chunkIndex, section, fileId))
                {
                    result.Add(subChunk);
                    chunkIndex++;
                }
            }
            else if (bufferTokens + paraTokens > _options.MaxTokens)
            {
                // 缓冲区满
                result.Add(CreateChunkFromParagraphs(buffer, allLines, chunkIndex++, section, fileId));
                buffer.Clear();
                buffer.Add(para);
                bufferTokens = paraTokens;
            }
            else
            {
                buffer.Add(para);
                bufferTokens += paraTokens;
            }
        }

        // 剩余内容
        if (buffer.Count > 0)
        {
            result.Add(CreateChunkFromParagraphs(buffer, allLines, chunkIndex, section, fileId));
        }

        return result;
    }

    /// <summary>
    /// 提取段落
    /// </summary>
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
                // 空行分隔段落
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

        // 最后一个段落
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

    /// <summary>
    /// 切分长段落
    /// </summary>
    private List<ChunkRecord> SplitLongParagraph(
        Paragraph para,
        string[] allLines,
        int startChunkIndex,
        Section section,
        string fileId)
    {
        var result = new List<ChunkRecord>();
        // 按句子切分
        var sentences = SplitSentences(para.Content);
        var buffer = new List<string>();
        var bufferTokens = 0;
        int currentLine = para.StartLine;
        var chunkIndex = startChunkIndex;

        foreach (var sentence in sentences)
        {
            var sentenceTokens = _tokenizer?.CountTokens(sentence) ?? EstimateTokens(sentence);

            if (bufferTokens + sentenceTokens > _options.MaxTokens && buffer.Count > 0)
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

    /// <summary>
    /// 创建分段记录
    /// </summary>
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
        // 计算权重
        float weight = ComputeWeight(level, content);

        // 获取结束列
        int endColumn = lines.Length > 0 && endLine > 0 && endLine <= lines.Length
            ? lines[Math.Min(endLine - 1, lines.Length - 1)].Length
            : 0;

        return new ChunkRecord
        {
            Id = Guid.NewGuid().ToString(),
            FileId = fileId,
            ChunkIndex = chunkIndex,
            Content = content,
            TokenCount = _tokenizer?.CountTokens(content) ?? EstimateTokens(content),
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

    /// <summary>
    /// 从段落列表创建分段
    /// </summary>
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

    /// <summary>
    /// 计算分段权重
    /// </summary>
    private float ComputeWeight(int level, string content)
    {
        float weight = 1.0f;

        // 标题级别越高，权重越大
        weight *= level switch
        {
            1 => 1.5f,  // H1
            2 => 1.3f,  // H2
            3 => 1.1f,  // H3
            _ => 1.0f
        };

        // 内容越短，信息密度越高
        if (content.Length < 200) weight *= 1.2f;

        return weight;
    }

    /// <summary>
    /// 按句子切分
    /// </summary>
    private List<string> SplitSentences(string text)
    {
        var sentences = new List<string>();
        var current = new System.Text.StringBuilder();

        for (int i = 0; i < text.Length; i++)
        {
            current.Append(text[i]);

            // 检测句子结束
            if (text[i] == '。' || text[i] == '！' || text[i] == '？' ||
                text[i] == '.' || text[i] == '!' || text[i] == '?')
            {
                // 检查后面是否有引号
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
        {
            sentences.Add(current.ToString());
        }

        return sentences;
    }

    /// <summary>
    /// 估算 token 数量
    /// </summary>
    private int EstimateTokens(string text)
    {
        // 简单估算：中文约 1.5 字符/token，英文约 4 字符/token
        var chineseCount = text.Count(c => c > 0x4E00 && c < 0x9FFF);
        var otherCount = text.Length - chineseCount;
        return (int)(chineseCount / 1.5 + otherCount / 4);
    }

    /// <summary>
    /// 章节信息
    /// </summary>
    private class Section
    {
        public string Content { get; set; } = string.Empty;
        public int StartLine { get; set; }
        public int EndLine { get; set; }
        public string? HeadingPath { get; set; }
        public int Level { get; set; }
    }

    /// <summary>
    /// 段落信息
    /// </summary>
    private class Paragraph
    {
        public string Content { get; set; } = string.Empty;
        public int StartLine { get; set; }
        public int EndLine { get; set; }
    }
}
