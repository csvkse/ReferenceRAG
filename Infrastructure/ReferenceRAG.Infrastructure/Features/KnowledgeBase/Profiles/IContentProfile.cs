using ReferenceRAG.Core.Features.KnowledgeBase.Domain;

namespace ReferenceRAG.Core.Features.KnowledgeBase.Profiles;

/// <summary>
/// 内容画像契约（Gate G3 规范）
/// 提供领域特定的切块边界探测、粒度与元数据提取策略（如通用、法律合同、小说长文等）
/// </summary>
public interface IContentProfile
{
    string ProfileId { get; }
    string DisplayName { get; }
    int TargetChunkSize { get; }
    int ChunkOverlap { get; }

    /// <summary>
    /// 对文档正文执行画像特定的分块提取
    /// </summary>
    IReadOnlyList<ChunkData> Split(string text, string documentId);
}

/// <summary>
/// 内容画像注册表契约
/// </summary>
public interface IContentProfileRegistry
{
    void Register(IContentProfile profile);
    IContentProfile GetProfile(string profileId);
    IReadOnlyList<IContentProfile> ListProfiles();
}
