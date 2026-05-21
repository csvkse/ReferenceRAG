using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services.Graph;
using System.Security.Cryptography;
using System.Text;

namespace ReferenceRAG.Core.Services;

/// <summary>
/// 单文件索引流水线实现。
/// 取代原 IndexCleaner + IndexService.PrepareFileAsync/FinalizeFileAsync + AutoIndexService内嵌逻辑。
/// </summary>
public class FileIndexPipeline : IFileIndexPipeline
{
    private readonly IVectorStore _vectorStore;
    private readonly IBM25Store _bm25Store;
    private readonly IMarkdownChunker _chunker;
    private readonly IEmbeddingService _embeddingService;
    private readonly ITextEnhancer _textEnhancer;
    private readonly ConfigManager _configManager;
    private readonly GraphIndexingService? _graphIndexing;
    private readonly ILogger<FileIndexPipeline>? _logger;

    public FileIndexPipeline(
        IVectorStore vectorStore,
        IBM25Store bm25Store,
        IMarkdownChunker chunker,
        IEmbeddingService embeddingService,
        ITextEnhancer textEnhancer,
        ConfigManager configManager,
        GraphIndexingService? graphIndexing = null,
        ILogger<FileIndexPipeline>? logger = null)
    {
        _vectorStore = vectorStore;
        _bm25Store = bm25Store;
        _chunker = chunker;
        _embeddingService = embeddingService;
        _textEnhancer = textEnhancer;
        _configManager = configManager;
        _graphIndexing = graphIndexing;
        _logger = logger;
    }

    public async Task<FileProcessContext?> PrepareAsync(
        string filePath,
        IReadOnlyList<SourceFolder> sources,
        bool force,
        CancellationToken ct = default)
    {
        var content = await File.ReadAllTextAsync(filePath, ct);
        var contentHash = ComputeHash(content);

        var matchedSource = sources
            .OrderByDescending(s => s.Path.Length)
            .FirstOrDefault(s =>
                filePath.StartsWith(s.Path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                filePath.Equals(s.Path, StringComparison.OrdinalIgnoreCase) ||
                filePath.StartsWith(PathUtility.NormalizePath(s.Path) + '/', StringComparison.OrdinalIgnoreCase) ||
                filePath.Equals(PathUtility.NormalizePath(s.Path), StringComparison.OrdinalIgnoreCase));

        var existingFile = await _vectorStore.GetFileByPathAsync(filePath, ct);
        var fileId = existingFile?.Id ?? Guid.NewGuid().ToString();

        // 跳过条件：hash 匹配 AND 上次已完整完成（status='complete'）
        // status='pending' 说明上次中断，即使 hash 一致也必须重新索引
        if (!force && existingFile != null
            && existingFile.ContentHash == contentHash
            && existingFile.IndexedStatus == "complete")
        {
            _logger?.LogDebug("内容未变化，跳过: {FileName}", Path.GetFileName(filePath));
            return null;
        }

        var chunks = _chunker.Chunk(content, new ChunkingOptions
        {
            MaxTokens = 512, MinTokens = 50, OverlapTokens = 50
        });
        if (chunks.Count == 0) return null;

        var sourceName = matchedSource?.Name ?? "unknown";
        var fileRecord = new FileRecord
        {
            Id = fileId,
            Path = filePath,
            FileName = Path.GetFileName(filePath),
            ParentFolder = Path.GetDirectoryName(filePath),
            Source = sourceName,
            ContentHash = contentHash,
            ContentLength = content.Length,
            Title = Path.GetFileNameWithoutExtension(filePath),
            ModifiedAt = File.GetLastWriteTime(filePath),
            ChunkCount = chunks.Count,
            IndexedAt = DateTime.UtcNow,
            IndexedStatus = "pending"   // Phase3 完成后改为 complete
        };

        await _vectorStore.UpsertFileAsync(fileRecord, ct);

        var oldChunks = await _vectorStore.GetChunksByFileAsync(fileId, ct);
        var oldChunkIds = oldChunks.Select(c => c.Id).ToList();

        await _vectorStore.DeleteChunksByFileAsync(fileId, ct);

        if (oldChunkIds.Count > 0)
            await _bm25Store.DeleteDocumentsByIdsAsync(oldChunkIds);

        foreach (var chunk in chunks)
        {
            chunk.FileId = fileId;
            chunk.Id = Guid.NewGuid().ToString();
            chunk.Source = sourceName;
            // P5: 预计算增强内容用于 embedding，原始 Content 保留给 BM25 和显示
            chunk.EnhancedContent = _textEnhancer.Enhance(chunk, fileRecord);
        }

        return new FileProcessContext
        {
            FileRecord = fileRecord,
            Content = content,
            Chunks = chunks,
            OldChunkIds = oldChunkIds
        };
    }

    public async Task<FileProcessContext?> PrepareVectorOnlyAsync(
        string filePath,
        CancellationToken ct = default)
    {
        var existingFile = await _vectorStore.GetFileByPathAsync(filePath, ct);
        if (existingFile == null) return null;

        var chunks = (await _vectorStore.GetChunksByFileAsync(existingFile.Id, ct)).ToList();
        if (chunks.Count == 0) return null;

        await _vectorStore.DeleteVectorsByFileAsync(existingFile.Id, ct);
        await _vectorStore.MarkFileStatusAsync(existingFile.Id, "pending", ct);

        return new FileProcessContext
        {
            FileRecord = existingFile,
            Content = "",
            Chunks = chunks,
            OldChunkIds = new List<string>()
        };
    }

    public async Task FinalizeAsync(
        FileProcessContext ctx,
        IReadOnlyDictionary<string, string> filenameMap,
        CancellationToken ct = default,
        bool updateGraph = true)
    {
        // P4: BM25 总是更新（含 VectorOnly 场景）；图谱仅在 updateGraph=true 时更新
        await _bm25Store.IndexBatchAsync(ctx.Chunks.Select(c => (c.Id, c.Content)));

        if (updateGraph && _graphIndexing != null)
        {
            Func<string, string?> resolver = shortId =>
                filenameMap.TryGetValue(shortId, out var full) ? full : null;
            await _graphIndexing.UpdateGraphAsync(ctx.FileRecord, ctx.Content, ctx.Chunks, ct, resolver);
        }

        // 所有阶段完成，标记为 complete，防止中断后因 hash 匹配被跳过
        await _vectorStore.MarkFileStatusAsync(ctx.FileRecord.Id, "complete", ct);
    }

    public async Task<bool> IndexSingleAsync(
        string filePath,
        IReadOnlyList<SourceFolder> sources,
        string? oldFilePath = null,
        CancellationToken ct = default)
    {
        // 重命名：先清旧路径的全部索引
        if (!string.IsNullOrEmpty(oldFilePath))
            await DeleteFileByPathAsync(oldFilePath, ct);

        if (!File.Exists(filePath)) return false;

        var ctx = await PrepareAsync(filePath, sources, force: false, ct);
        if (ctx == null) return false;

        var config = _configManager.Load();
        var batchSize = config.Embedding.BatchSize;
        var vectors = new List<VectorRecord>();

        for (int i = 0; i < ctx.Chunks.Count; i += batchSize)
        {
            var batch = ctx.Chunks.Skip(i).Take(batchSize).ToList();
            var embeddings = await _embeddingService.EncodeBatchAsync(
                batch.Select(c => c.EnhancedContent ?? c.Content), EmbeddingMode.Document, ct);

            for (int j = 0; j < batch.Count; j++)
                vectors.Add(new VectorRecord
                {
                    Id = Guid.NewGuid().ToString(),
                    ChunkId = batch[j].Id,
                    FileId = batch[j].FileId,
                    Vector = embeddings[j],
                    Dimension = embeddings[j].Length,
                    Source = batch[j].Source,
                    ModelName = _embeddingService.ModelName
                });
        }

        await _vectorStore.UpsertChunksAsync(ctx.Chunks, ct);
        await _vectorStore.UpsertVectorsAsync(vectors, ct);

        var allFiles = await _vectorStore.GetAllFilesAsync(ct);
        var filenameMap = GraphIndexingService.BuildFilenameMap(allFiles);
        await FinalizeAsync(ctx, filenameMap, ct);

        _logger?.LogInformation("单文件索引完成: {FileName} ({Chunks} chunks, {Vectors} vectors)",
            Path.GetFileName(filePath), ctx.Chunks.Count, vectors.Count);
        return true;
    }

    public async Task DeleteFileAsync(
        string fileId,
        string? filePath = null,
        CancellationToken ct = default)
    {
        var chunkIds = (await _vectorStore.GetChunksByFileAsync(fileId, ct))
            .Select(c => c.Id).ToList();

        await _vectorStore.DeleteFileAsync(fileId, ct);

        if (chunkIds.Count > 0)
            await _bm25Store.DeleteDocumentsByIdsAsync(chunkIds);

        if (filePath != null && _graphIndexing != null)
            await _graphIndexing.RemoveAsync(filePath, ct);
    }

    public async Task<bool> DeleteFileByPathAsync(
        string filePath,
        CancellationToken ct = default)
    {
        var file = await _vectorStore.GetFileByPathAsync(filePath, ct);
        if (file == null) return false;
        await DeleteFileAsync(file.Id, filePath, ct);
        return true;
    }

    public async Task DeleteSourceAsync(string sourceName, CancellationToken ct = default)
    {
        var files = (await _vectorStore.GetAllFilesAsync(ct))
            .Where(f => f.Source == sourceName)
            .ToList();

        var chunkIds = new List<string>();
        foreach (var file in files)
        {
            var chunks = await _vectorStore.GetChunksByFileAsync(file.Id, ct);
            chunkIds.AddRange(chunks.Select(c => c.Id));
        }

        await _vectorStore.DeleteBySourceAsync(sourceName, ct);

        if (chunkIds.Count > 0)
            await _bm25Store.DeleteDocumentsByIdsAsync(chunkIds);

        if (_graphIndexing != null)
            foreach (var file in files)
                await _graphIndexing.RemoveAsync(file.Path, ct);
    }

    private static string ComputeHash(string content)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(content)));
    }
}
