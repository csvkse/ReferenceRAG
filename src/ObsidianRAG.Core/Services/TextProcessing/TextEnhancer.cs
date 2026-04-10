using System.Text;
using System.Text.RegularExpressions;
using ObsidianRAG.Core.Interfaces;
using ObsidianRAG.Core.Models;

namespace ObsidianRAG.Core.Services;

/// <summary>
/// 文本增强服务 - 提升向量质量
/// </summary>
public class TextEnhancer : ITextEnhancer
{
    private readonly TextEnhanceOptions _options;
    private readonly HashSet<string> _stopWords;

    public TextEnhancer(TextEnhanceOptions? options = null)
    {
        _options = options ?? new TextEnhanceOptions();
        _stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "notes", "docs", "files", "attachments", "draft", "archive"
        };
    }

    /// <summary>
    /// 增强文本内容
    /// </summary>
    public string Enhance(ChunkRecord chunk, FileRecord file)
    {
        var context = new EnhancementContext
        {
            Title = file.Title,
            Tags = file.Tags,
            HeadingPath = chunk.HeadingPath,
            FilePath = file.Path,
            ParentFolder = file.ParentFolder
        };
        return Enhance(chunk.Content, context);
    }

    /// <summary>
    /// 增强文本内容（使用上下文）
    /// </summary>
    public string Enhance(string content, EnhancementContext context)
    {
        var parts = new List<string>();

        // 1. 标题增强（权重最高）
        if (_options.IncludeTitle && !string.IsNullOrEmpty(context.Title))
        {
            parts.Add($"[标题] {context.Title}");
        }

        // 2. 章节路径增强
        if (_options.IncludeHeading && !string.IsNullOrEmpty(context.HeadingPath))
        {
            parts.Add($"[章节] {context.HeadingPath}");
        }

        // 3. 标签增强
        if (_options.IncludeTags && context.Tags?.Count > 0)
        {
            parts.Add($"[标签] {string.Join(" ", context.Tags)}");
        }

        // 4. 文件夹路径增强（领域信息）
        if (_options.IncludeFolder && !string.IsNullOrEmpty(context.ParentFolder))
        {
            var folderKeywords = ExtractFolderKeywords(context.ParentFolder);
            if (!string.IsNullOrEmpty(folderKeywords))
            {
                parts.Add($"[领域] {folderKeywords}");
            }
        }

        // 5. 关键句提取（长文本）
        if (_options.ExtractKeySentences && content.Length > _options.KeySentenceThreshold * 4)
        {
            var keySentences = ExtractKeySentences(content);
            if (keySentences.Count > 0)
            {
                parts.Add($"[要点] {string.Join(" ", keySentences.Take(3))}");
            }
        }

        // 6. 核心内容
        parts.Add(content);

        return string.Join("\n", parts);
    }

    /// <summary>
    /// 提取关键句
    /// </summary>
    private List<string> ExtractKeySentences(string content)
    {
        var sentences = SplitSentences(content);
        var keySentences = new List<string>();

        if (sentences.Count == 0) return keySentences;

        // 首句（通常包含主题）
        keySentences.Add(sentences[0]);

        // 尾句（通常是结论）
        if (sentences.Count > 1)
        {
            keySentences.Add(sentences[^1]);
        }

        // 最长句（信息密度高）
        if (sentences.Count > 2)
        {
            var longest = sentences.OrderByDescending(s => s.Length).First();
            if (!keySentences.Contains(longest))
            {
                keySentences.Add(longest);
            }
        }

        return keySentences;
    }

    /// <summary>
    /// 提取文件夹关键词
    /// </summary>
    private string ExtractFolderKeywords(string folderPath)
    {
        // 提取有意义的文件夹名
        var parts = folderPath.Split('/', '\\')
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Where(p => !_stopWords.Contains(p))
            .Select(p => p.Replace("-", " ").Replace("_", " "))
            .Where(p => p.Length > 1);

        return string.Join(" ", parts);
    }

    /// <summary>
    /// 按句子切分
    /// </summary>
    private List<string> SplitSentences(string text)
    {
        var sentences = new List<string>();
        var current = new StringBuilder();

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

                var sentence = current.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(sentence))
                {
                    sentences.Add(sentence);
                }
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            var sentence = current.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(sentence))
            {
                sentences.Add(sentence);
            }
        }

        return sentences;
    }
}

/// <summary>
/// 文本增强配置
/// </summary>
public class TextEnhanceOptions
{
    /// <summary>
    /// 是否包含标题
    /// </summary>
    public bool IncludeTitle { get; set; } = true;

    /// <summary>
    /// 是否包含章节路径
    /// </summary>
    public bool IncludeHeading { get; set; } = true;

    /// <summary>
    /// 是否包含标签
    /// </summary>
    public bool IncludeTags { get; set; } = true;

    /// <summary>
    /// 是否包含文件夹路径
    /// </summary>
    public bool IncludeFolder { get; set; } = true;

    /// <summary>
    /// 是否提取关键句
    /// </summary>
    public bool ExtractKeySentences { get; set; } = true;

    /// <summary>
    /// 关键句提取阈值（token数）
    /// </summary>
    public int KeySentenceThreshold { get; set; } = 200;
}
