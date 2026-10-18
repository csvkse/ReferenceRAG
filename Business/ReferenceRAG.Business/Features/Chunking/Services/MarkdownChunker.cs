using Markdig;
using Markdig.Syntax;
using ReferenceRAG.Business.Features.Chunking.Adapters;
using ReferenceRAG.Business.Features.Chunking.ContinuousElements.Adapters;
using ReferenceRAG.Business.Features.Chunking.ContinuousElements.Models;
using ReferenceRAG.Business.Features.Chunking.ContinuousElements.StateMachine;
using ReferenceRAG.Business.Features.Chunking.ContinuousElements.Strategies;
using ReferenceRAG.Business.Features.Chunking.Strategies;
using ReferenceRAG.Core.Helpers;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Core.Services;

/// <summary>
/// Markdown 分段器 - 支持行号记录、自适应切分与连续结构元素保护
/// </summary>
public class MarkdownChunker : IMarkdownChunker
{
    private const int ShortContentThreshold = 200;
    private readonly ITokenizer? _tokenizer;
    private readonly ChunkingOptions _defaultOptions;
    private readonly IModelProfileAdapter _profileAdapter;
    private readonly AdaptiveStrategyFactory _strategyFactory;
    private readonly ContinuousChunkingStrategyFactory _continuousStrategyFactory;
    private readonly TableStructureAdapter _tableAdapter = new();

    private static readonly System.Text.RegularExpressions.Regex HeadingPattern =
        new(@"^(#{1,6})\s+(.+)$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex SeparatorPattern =
        new(@"^(\s*[=\-_*#~·\u2500-\u257F]{2,}\s*(\(?\d+[\-~至到/,\s]\d+\)?|\d+)?\s*[=\-_*#~·\u2500-\u257F]{0,}\s*)$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    public MarkdownChunker(
        ChunkingOptions? options = null,
        ITokenizer? tokenizer = null,
        IModelProfileAdapter? profileAdapter = null,
        AdaptiveStrategyFactory? strategyFactory = null,
        ContinuousChunkingStrategyFactory? continuousStrategyFactory = null)
    {
        _defaultOptions = options ?? new ChunkingOptions();
        _tokenizer = tokenizer;
        _profileAdapter = profileAdapter ?? new DefaultModelProfileAdapter();
        _strategyFactory = strategyFactory ?? new AdaptiveStrategyFactory();
        _continuousStrategyFactory = continuousStrategyFactory ?? new ContinuousChunkingStrategyFactory();
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
        // P0: 前置清洗巨型 Base64 多媒体内联字符串
        content = Base64CleanerPipeline.Clean(content);

        // P2: 不修改实例状态，使用局部有效配置
        var effectiveOptions = options ?? _defaultOptions;
        var isAdaptive = string.Equals(effectiveOptions.Mode, "Adaptive", StringComparison.OrdinalIgnoreCase);

        if (isAdaptive)
        {
            var profile = effectiveOptions.ModelProfile ?? _profileAdapter.GetProfile(null);
            var strategy = _strategyFactory.GetStrategy(effectiveOptions.AdaptiveStrategyType);
            var adaptiveParams = strategy.ResolveParameters(effectiveOptions, profile);

            effectiveOptions = new ChunkingOptions
            {
                MaxTokens = adaptiveParams.MaxTokens,
                MinTokens = adaptiveParams.MinTokens,
                OverlapTokens = adaptiveParams.OverlapTokens,
                PreserveHeadings = adaptiveParams.PreserveHeadings,
                PreserveCodeBlocks = adaptiveParams.PreserveCodeBlocks,
                PreserveTables = adaptiveParams.PreserveTables,
                Mode = effectiveOptions.Mode,
                AdaptiveLevel = effectiveOptions.AdaptiveLevel,
                AdaptiveStrategyType = effectiveOptions.AdaptiveStrategyType,
                TargetWindowUtilizationPercent = effectiveOptions.TargetWindowUtilizationPercent,
                AdaptiveOverlapPercent = effectiveOptions.AdaptiveOverlapPercent,
                ModelProfile = profile
            };
        }

        var result = new List<ChunkRecord>();
        var lines = content.Split('\n');
        var chunkIndex = 0;

        var sections = ExtractSections(content, lines, effectiveOptions.PreserveHeadings);
        // P0: 合并短节（< MinTokens）到相邻节，避免产生无效 chunk
        sections = MergeShortSections(sections, effectiveOptions);

        if (isAdaptive && !string.Equals(effectiveOptions.AdaptiveLevel, "Level1", StringComparison.OrdinalIgnoreCase))
        {
            // Level 2 / Level 3: 进一步对连续短小兄弟节进行智能聚合，避免产生大量碎片向量
            sections = AggregateSiblingSections(sections, effectiveOptions);
        }

        foreach (var section in sections)
        {
            var sectionTokens = _tokenizer?.CountTokens(section.Content) ?? TokenEstimator.EstimateTokens(section.Content);
            var sectionLines = section.Content.Split('\n');
            bool hasTable = effectiveOptions.PreserveTables && section.Content.Contains('|') && ContainsTable(sectionLines);

            if (sectionTokens <= effectiveOptions.MaxTokens && !hasTable)
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

        result = PostProcessChunks(result, lines, effectiveOptions);
        return result;
    }

    private bool ContainsTable(string[] sectionLines)
    {
        for (int i = 0; i < sectionLines.Length; i++)
        {
            if (_tableAdapter.CanAdapt(sectionLines, i, out _))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 自适应模式下智能聚合连续短小兄弟小节，兼顾信息完整度与空间精简
    /// </summary>
    private List<Section> AggregateSiblingSections(List<Section> sections, ChunkingOptions options)
    {
        if (sections.Count <= 1) return sections;

        var result = new List<Section>();
        var current = sections[0];

        for (int i = 1; i < sections.Count; i++)
        {
            var next = sections[i];
            var currentTokens = _tokenizer?.CountTokens(current.Content) ?? TokenEstimator.EstimateTokens(current.Content);
            var nextTokens = _tokenizer?.CountTokens(next.Content) ?? TokenEstimator.EstimateTokens(next.Content);

            // 兄弟小节合并条件：合并后 token 不超过 85% 预算
            bool canMerge = (currentTokens + nextTokens <= (int)(options.MaxTokens * 0.85f))
                            && (current.Level == next.Level || (current.Level > 0 && next.Level > 0));

            if (canMerge)
            {
                current = MergeTwoSections(current, next);
            }
            else
            {
                result.Add(current);
                current = next;
            }
        }

        result.Add(current);
        return result;
    }

    /// <summary>
    /// 判断文本是否为无独立语义的分隔符、装饰线或微小噪声碎片（如 === (43-48) ===, ---, ***, === 等）
    /// </summary>
    public static bool IsNoiseOrSeparator(string text) => NoiseChunkDetector.IsNoiseOrSeparator(text);

    /// <summary>
    /// 后置处理分块：消除孤立的单行分隔符或噪声分块，将其自动向前或向后合并到相邻内容分块
    /// </summary>
    private List<ChunkRecord> PostProcessChunks(List<ChunkRecord> chunks, string[] lines, ChunkingOptions options)
    {
        if (chunks.Count <= 1)
        {
            return chunks;
        }

        var processed = new List<ChunkRecord>();
        for (int i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            if (IsNoiseOrSeparator(chunk.Content))
            {
                if (processed.Count > 0)
                {
                    // 向前合并到上一个分块
                    var prev = processed[^1];
                    prev.Content += "\n\n" + chunk.Content;
                    prev.EndLine = Math.Max(prev.EndLine, chunk.EndLine);
                    prev.EndColumn = chunk.EndColumn > 0 ? chunk.EndColumn : prev.EndColumn;
                    prev.TokenCount = _tokenizer?.CountTokens(prev.Content) ?? TokenEstimator.EstimateTokens(prev.Content);
                }
                else if (i + 1 < chunks.Count)
                {
                    // 作为首个块且为纯分隔符，向后合并到下一个分块
                    var next = chunks[i + 1];
                    next.Content = chunk.Content + "\n\n" + next.Content;
                    next.StartLine = Math.Min(chunk.StartLine, next.StartLine);
                    next.TokenCount = _tokenizer?.CountTokens(next.Content) ?? TokenEstimator.EstimateTokens(next.Content);
                }
                else
                {
                    // 既无前也无后，保留
                    processed.Add(chunk);
                }
            }
            else
            {
                processed.Add(chunk);
            }
        }

        // 重新编号 ChunkIndex
        for (int i = 0; i < processed.Count; i++)
        {
            processed[i].ChunkIndex = i;
        }

        return processed;
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

    private List<Section> ExtractSections(string content, string[] lines, bool preserveHeadings)
    {
        var sections = new List<Section>();
        var headingStack = new Stack<(int Level, string Text)>();
        var currentContent = new List<string>();
        int? sectionStart = null;
        var inFence = false;   // fenced code 状态：围栏内的 "#" 不是标题

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var lineNum = i + 1;

            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                // 维护围栏状态后再判断标题
                inFence = !inFence;
                currentContent.Add(line);
                if (sectionStart == null) sectionStart = lineNum;
                continue;
            }

            var headingMatch = !inFence ? HeadingPattern.Match(line) : null;

            if (headingMatch?.Success == true)
            {
                if (sectionStart.HasValue && currentContent.Count > 0)
                {
                    var headingPath = string.Join("/", headingStack.Select(h => h.Text));
                    var sectionContent = string.Join("\n", currentContent);
                    if (preserveHeadings || !string.IsNullOrWhiteSpace(sectionContent))
                    {
                        sections.Add(new Section
                        {
                            Content = sectionContent,
                            StartLine = sectionStart.Value,
                            EndLine = lineNum - 1,
                            HeadingPath = headingPath,
                            Level = headingStack.Count > 0 ? headingStack.Peek().Level : 0
                        });
                    }
                }

                var level = headingMatch.Groups[1].Value.Length;
                var text = headingMatch.Groups[2].Value.Trim();

                while (headingStack.Count > 0 && headingStack.Peek().Level >= level)
                    headingStack.Pop();
                headingStack.Push((level, text));

                if (preserveHeadings)
                {
                    sectionStart = lineNum;
                    currentContent = new List<string> { line };
                }
                else
                {
                    sectionStart = lineNum + 1;
                    currentContent = new List<string>();
                }
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
            var sectionContent = string.Join("\n", currentContent);
            if (preserveHeadings || !string.IsNullOrWhiteSpace(sectionContent))
            {
                sections.Add(new Section
                {
                    Content = sectionContent,
                    StartLine = sectionStart.Value,
                    EndLine = lines.Length,
                    HeadingPath = headingPath,
                    Level = headingStack.Count > 0 ? headingStack.Peek().Level : 0
                });
            }
        }

        // PreserveHeadings=false 时，纯标题或空文档不产生任何节
        if (sections.Count == 0 && preserveHeadings)
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
        var paragraphs = ExtractParagraphs(section, allLines, options.PreserveCodeBlocks, options.PreserveTables);
        var buffer = new List<Paragraph>();
        var bufferTokens = 0;
        var chunkIndex = startChunkIndex;

        foreach (var para in paragraphs)
        {
            var paraTokens = _tokenizer?.CountTokens(para.Content) ?? TokenEstimator.EstimateTokens(para.Content);

            // 表格专属处理：支持表头传承与行级对齐
            if (para.IsTable && options.PreserveTables && para.TableElement != null)
            {
                if (buffer.Count > 0)
                {
                    result.Add(CreateChunkFromParagraphs(buffer, allLines, chunkIndex++, section, fileId));
                    buffer.Clear();
                    bufferTokens = 0;
                }

                if (paraTokens > options.MaxTokens)
                {
                    var tableStrategy = _continuousStrategyFactory.GetStrategy(ChunkType.Table);
                    var tableStateMachine = new OversizedBlockSegmentationStateMachine();
                    var tableChunks = tableStateMachine.Execute(
                        tableStrategy!,
                        para.TableElement,
                        allLines,
                        options,
                        _tokenizer,
                        fileId,
                        section.HeadingPath,
                        section.Level,
                        ref chunkIndex);

                    result.AddRange(tableChunks);
                }
                else
                {
                    result.Add(CreateChunk(
                        para.Content,
                        allLines,
                        para.StartLine,
                        para.EndLine,
                        chunkIndex++,
                        section.HeadingPath,
                        section.Level,
                        fileId,
                        ChunkType.Table));
                }
                continue;
            }

            if (paraTokens > options.MaxTokens)
            {
                if (buffer.Count > 0)
                {
                    result.Add(CreateChunkFromParagraphs(buffer, allLines, chunkIndex++, section, fileId));
                    buffer.Clear();
                    bufferTokens = 0;
                }

                if (para.IsCode && options.PreserveCodeBlocks)
                {
                    // 代码块完整性优先：整块单独成 chunk；超限时按行拆分避免嵌入截断
                    if (paraTokens <= options.MaxTokens)
                    {
                        result.Add(CreateChunkFromParagraphs([para], allLines, chunkIndex++, section, fileId));
                    }
                    else
                    {
                        foreach (var subChunk in SplitOversizedCodeBlock(para, allLines, chunkIndex, section, fileId, options))
                        {
                            result.Add(subChunk);
                            chunkIndex++;
                        }
                    }
                }
                else
                {
                    foreach (var subChunk in SplitLongParagraph(para, allLines, chunkIndex, section, fileId, options))
                    {
                        result.Add(subChunk);
                        chunkIndex++;
                    }
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

                // overlap + 新段落合并后可能超过 MaxTokens，从最旧的 overlap 段落开始缩减。
                // 判定用候选整块估算，避免逐段累加与整块估算的整数除舍入差异
                while (overlap.Count > 0 && (
                        _tokenizer?.CountTokens(CountBufferContent(buffer)) ?? TokenEstimator.EstimateTokens(CountBufferContent(buffer)))
                    > options.MaxTokens)
                {
                    buffer.RemoveAt(0);
                    overlap.RemoveAt(0);
                }
                bufferTokens = buffer.Sum(p => _tokenizer?.CountTokens(p.Content) ?? TokenEstimator.EstimateTokens(p.Content));
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

    private List<Paragraph> ExtractParagraphs(Section section, string[] allLines, bool preserveCodeBlocks, bool preserveTables)
    {
        var paragraphs = new List<Paragraph>();
        var lines = section.Content.Split('\n');
        var currentPara = new List<string>();
        int? paraStart = null;
        var inFence = false;

        void FlushParagraph(bool isCode, int endLine)
        {
            if (currentPara.Count == 0 || !paraStart.HasValue) return;
            paragraphs.Add(new Paragraph
            {
                Content = string.Join("\n", currentPara),
                StartLine = paraStart.Value,
                EndLine = endLine,
                IsCode = isCode,
                IsTable = false
            });
            currentPara.Clear();
            paraStart = null;
        }

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var actualLineNum = section.StartLine + i;

            if (preserveCodeBlocks && line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                if (!inFence)
                {
                    FlushParagraph(isCode: false, actualLineNum - 1);
                    inFence = true;
                    paraStart = actualLineNum;
                    currentPara.Add(line);
                }
                else
                {
                    currentPara.Add(line);
                    FlushParagraph(isCode: true, actualLineNum);
                    inFence = false;
                }
                continue;
            }

            if (inFence)
            {
                // 围栏内的空行不拆分段落，保持代码块完整
                currentPara.Add(line);
                continue;
            }

            // 表格检测（非围栏状态下且启用 PreserveTables）
            if (preserveTables && _tableAdapter.CanAdapt(lines, i, out var consumedTableLines))
            {
                // 检查当前段落是否有且仅有前置导语说明行（如“*表 1-1：系统权限矩阵*”）
                string? precedingContext = null;
                if (currentPara.Count == 1 && currentPara[0].Trim().Length < 120)
                {
                    precedingContext = currentPara[0].Trim();
                    currentPara.Clear();
                    paraStart = null;
                }
                else
                {
                    FlushParagraph(isCode: false, actualLineNum - 1);
                }

                var tableElement = _tableAdapter.Adapt(lines, i, consumedTableLines, actualLineNum, precedingContext);
                paragraphs.Add(new Paragraph
                {
                    Content = tableElement.RawContent,
                    StartLine = actualLineNum,
                    EndLine = actualLineNum + consumedTableLines - 1,
                    IsCode = false,
                    IsTable = true,
                    TableElement = tableElement
                });

                i += consumedTableLines - 1;
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph(isCode: false, actualLineNum - 1);
            }
            else
            {
                if (!paraStart.HasValue) paraStart = actualLineNum;
                currentPara.Add(line);
            }
        }

        FlushParagraph(isCode: inFence, section.StartLine + lines.Length - 1);

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

            if (sentenceTokens > options.MaxTokens)
            {
                // 单句超过 MaxTokens：先落盘缓冲区，再按预算强制拆分该句
                if (buffer.Count > 0)
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

                foreach (var part in SplitOversizedText(sentence, options.MaxTokens))
                {
                    result.Add(CreateChunk(
                        part,
                        allLines,
                        para.StartLine,
                        para.EndLine,
                        chunkIndex++,
                        section.HeadingPath,
                        section.Level,
                        fileId,
                        ChunkType.Forced
                    ));
                }
                continue;
            }

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

    /// <summary>
    /// 按 token 预算强制拆分超长文本（单句/单行级别，无法再按语义边界切分时使用）。
    /// 基于估算器自适应推进，保证每个片段估算 token 数 ≤ 预算。
    /// </summary>
    private List<string> SplitOversizedText(string text, int maxTokens)
    {
        var parts = new List<string>();
        if (string.IsNullOrEmpty(text)) return parts;

        var start = 0;
        while (start < text.Length)
        {
            // 初始步长按最保守口径（每 token 1 字符）估算，再自适应调整
            var length = Math.Min(maxTokens + 1, text.Length - start);
            var candidate = text.Substring(start, length);
            var tokens = _tokenizer?.CountTokens(candidate) ?? TokenEstimator.EstimateTokens(candidate);

            while (tokens > Math.Max(1, maxTokens) && length > 1)
            {
                var adjusted = Math.Max(1, (int)((double)length * maxTokens / tokens));
                length = Math.Min(length - 1, adjusted);
                candidate = text.Substring(start, length);
                tokens = _tokenizer?.CountTokens(candidate) ?? TokenEstimator.EstimateTokens(candidate);
            }

            if (length <= 0) length = 1;

            // UTF-16 代理项守护：如果截断终点落在高代理项，确保不劈裂 Emoji 或四字节字符
            if (start + length < text.Length && char.IsHighSurrogate(text[start + length - 1]))
            {
                if (length > 1) length--;
                else length = 2;
            }

            parts.Add(text.Substring(start, length));
            start += length;
        }

        return parts;
    }

    /// <summary>
    /// 超长代码块：按行分组拆分，保证每个子块估算 token 数 ≤ 预算。
    /// 围栏行保留在各自所属的子块中（首块含开围栏、末块含闭围栏）。
    /// </summary>
    private List<ChunkRecord> SplitOversizedCodeBlock(
        Paragraph para,
        string[] allLines,
        int startChunkIndex,
        Section section,
        string fileId,
        ChunkingOptions options)
    {
        var result = new List<ChunkRecord>();
        var lines = para.Content.Split('\n');
        var buffer = new List<string>();
        var bufferStartLine = para.StartLine;
        var nextLine = para.StartLine;
        var chunkIndex = startChunkIndex;

        for (int i = 0; i < lines.Length; i++)
        {
            // 判定基于候选整块的估算，避免逐行累加与整块估算的整数除舍入差异
            if (buffer.Count > 0)
            {
                var candidate = string.Join("\n", buffer) + "\n" + lines[i];
                var candidateTokens = _tokenizer?.CountTokens(candidate) ?? TokenEstimator.EstimateTokens(candidate);
                if (candidateTokens > options.MaxTokens)
                {
                    result.Add(CreateChunk(
                        string.Join("\n", buffer),
                        allLines,
                        bufferStartLine,
                        nextLine - 1,
                        chunkIndex++,
                        section.HeadingPath,
                        section.Level,
                        fileId,
                        ChunkType.Code
                    ));
                    buffer.Clear();
                    bufferStartLine = nextLine;
                }
            }

            buffer.Add(lines[i]);
            nextLine++;
        }

        if (buffer.Count > 0)
        {
            result.Add(CreateChunk(
                string.Join("\n", buffer),
                allLines,
                bufferStartLine,
                nextLine - 1,
                chunkIndex,
                section.HeadingPath,
                section.Level,
                fileId,
                ChunkType.Code
            ));
        }

        return result;
    }

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

    private static string CountBufferContent(List<Paragraph> paragraphs)
        => string.Join("\n\n", paragraphs.Select(p => p.Content));

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
            // 代码段落与表格段落不作为跨块重叠段落，确保语法树完整与零重复
            if (buffer[i].IsCode || buffer[i].IsTable) continue;

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
        public bool IsCode { get; set; }
        public bool IsTable { get; set; }
        public TableElement? TableElement { get; set; }
    }
}
