using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;

namespace ReferenceRAG.Core.Features.KnowledgeBase.Profiles;

/// <summary>
/// 通用内容画像（按段落与换行自适应切分）
/// </summary>
public sealed class GeneralContentProfile : IContentProfile
{
    public string ProfileId => "general";
    public string DisplayName => "通用文档画像";
    public int TargetChunkSize => 500;
    public int ChunkOverlap => 50;

    public IReadOnlyList<ChunkData> Split(string text, string documentId)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<ChunkData>();

        var paragraphs = text.Split(new[] { "\r\n\r\n", "\n\n", "\r\r" }, StringSplitOptions.RemoveEmptyEntries);
        var chunks = new List<ChunkData>();
        var currentBuffer = new List<string>();
        var currentLen = 0;
        var ordinal = 0;

        foreach (var p in paragraphs)
        {
            var trimmed = p.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            if (currentLen + trimmed.Length > TargetChunkSize && currentBuffer.Count > 0)
            {
                var chunkText = string.Join("\n\n", currentBuffer);
                chunks.Add(new ChunkData($"chk_{documentId}_{ordinal}", ordinal, chunkText, chunkText.Length / 2));
                ordinal++;
                currentBuffer.Clear();
                currentLen = 0;
            }

            currentBuffer.Add(trimmed);
            currentLen += trimmed.Length;
        }

        if (currentBuffer.Count > 0)
        {
            var chunkText = string.Join("\n\n", currentBuffer);
            chunks.Add(new ChunkData($"chk_{documentId}_{ordinal}", ordinal, chunkText, chunkText.Length / 2));
        }

        return chunks;
    }
}

/// <summary>
/// 合同与法律条款画像（识别“第X条/章/款”保留条款独立性与完整性）
/// </summary>
public sealed class ContractContentProfile : IContentProfile
{
    private static readonly Regex ClauseRegex = new(
        @"(?=第[一二三四五六七八九十0-9]+[条章节款]|Article\s+\d+|Section\s+\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public string ProfileId => "contract";
    public string DisplayName => "合同协议与法规画像";
    public int TargetChunkSize => 800;
    public int ChunkOverlap => 80;

    public IReadOnlyList<ChunkData> Split(string text, string documentId)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<ChunkData>();

        // 按条款正则表达式分割
        var rawClauses = ClauseRegex.Split(text);
        var chunks = new List<ChunkData>();
        var ordinal = 0;

        foreach (var clause in rawClauses)
        {
            var trimmed = clause.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            chunks.Add(new ChunkData($"chk_{documentId}_{ordinal}", ordinal, trimmed, trimmed.Length / 2));
            ordinal++;
        }

        // 若正文无明显条款标记，平滑回退至通用切块
        if (chunks.Count == 0 || (chunks.Count == 1 && chunks[0].Text.Length > TargetChunkSize * 2))
        {
            return new GeneralContentProfile().Split(text, documentId);
        }

        return chunks;
    }
}

/// <summary>
/// 长篇小说与文学叙事画像（识别章节回目标记，保留大篇幅叙事连贯性）
/// </summary>
public sealed class FictionContentProfile : IContentProfile
{
    private static readonly Regex ChapterRegex = new(
        @"(?=第[一二三四五六七八九十0-9]+[回章节卷]|Chapter\s+\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public string ProfileId => "fiction";
    public string DisplayName => "长篇小说与文学画像";
    public int TargetChunkSize => 1200;
    public int ChunkOverlap => 120;

    public IReadOnlyList<ChunkData> Split(string text, string documentId)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<ChunkData>();

        var rawChapters = ChapterRegex.Split(text);
        var chunks = new List<ChunkData>();
        var ordinal = 0;

        foreach (var section in rawChapters)
        {
            var trimmed = section.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            // 超长章节内部进一步按自然段切分
            if (trimmed.Length > TargetChunkSize)
            {
                var sub = new GeneralContentProfile().Split(trimmed, $"{documentId}_{ordinal}");
                foreach (var s in sub)
                {
                    chunks.Add(s with { Ordinal = ordinal, ChunkId = $"chk_{documentId}_{ordinal}" });
                    ordinal++;
                }
            }
            else
            {
                chunks.Add(new ChunkData($"chk_{documentId}_{ordinal}", ordinal, trimmed, trimmed.Length / 2));
                ordinal++;
            }
        }

        return chunks.Count > 0 ? chunks : new GeneralContentProfile().Split(text, documentId);
    }
}

/// <summary>
/// 内存线程安全的内容画像注册表实现
/// </summary>
public sealed class ContentProfileRegistry : IContentProfileRegistry
{
    private readonly ConcurrentDictionary<string, IContentProfile> _profiles = new(StringComparer.OrdinalIgnoreCase);

    public ContentProfileRegistry()
    {
        Register(new GeneralContentProfile());
        Register(new ContractContentProfile());
        Register(new FictionContentProfile());
    }

    public void Register(IContentProfile profile)
    {
        if (profile == null) throw new ArgumentNullException(nameof(profile));
        _profiles[profile.ProfileId] = profile;
    }

    public IContentProfile GetProfile(string profileId)
    {
        if (!string.IsNullOrWhiteSpace(profileId) && _profiles.TryGetValue(profileId, out var profile))
        {
            return profile;
        }
        return _profiles["general"];
    }

    public IReadOnlyList<IContentProfile> ListProfiles()
    {
        return _profiles.Values.ToList();
    }
}
