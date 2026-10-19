namespace ReferenceRAG.Infrastructure.Features.ModelManagement.Download.Adapters;

/// <summary>
/// 模型源适配器接口 - 统一不同上游模型托管平台协议 (HuggingFace / hf-mirror / ModelScope 等)
/// </summary>
public interface IModelSourceAdapter : IDisposable
{
    /// <summary>
    /// 适配器名称 (例如: "HuggingFace", "ModelScope")
    /// </summary>
    string SourceName { get; }

    /// <summary>
    /// 当前活跃的上游端点基地址 (例如: "https://hf-mirror.com")
    /// </summary>
    string ActiveEndpoint { get; }

    /// <summary>
    /// 获取指定仓库的文件清单
    /// </summary>
    Task<ModelManifest> GetManifestAsync(string modelId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 探测或补齐缺失的文件大小 (通过 HTTP HEAD 或 Range 请求解析 Content-Length)
    /// </summary>
    Task<long> ResolveFileSizeAsync(string modelId, string relativePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 流式下载单个文件到本地目标路径
    /// </summary>
    /// <param name="modelId">模型标识 (例如 "Alibaba-NLP/gte-multilingual-reranker-base")</param>
    /// <param name="relativePath">仓库内相对路径</param>
    /// <param name="targetFilePath">本地落盘路径</param>
    /// <param name="onChunkReceived">每接收一块数据时的回调 (bytesDelta, totalBytesReadForThisFile)</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task DownloadFileAsync(
        string modelId,
        string relativePath,
        string targetFilePath,
        Action<long, long>? onChunkReceived = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 搜索上游模型托管平台中的变种或社区量化仓库
    /// </summary>
    Task<List<ReferenceRAG.Infrastructure.Features.ModelManagement.Download.Models.ModelVariantSearchResult>> SearchVariantsAsync(
        string query,
        string? format = null,
        int limit = 15,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取特定变种仓库的详细信息与文件清单
    /// </summary>
    Task<ReferenceRAG.Infrastructure.Features.ModelManagement.Download.Models.ModelVariantDetail?> GetVariantDetailAsync(
        string repositoryId,
        CancellationToken cancellationToken = default);
}
