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
    private readonly IGraphIndexingService? _graphIndexing;
    private readonly IEmbeddingTokenCounter? _tokenCounter;
    private readonly ILogger<FileIndexPipeline>? _logger;

    public FileIndexPipeline(
        IVectorStore vectorStore,
        IBM25Store bm25Store,
        IMarkdownChunker chunker,
        IEmbeddingService embeddingService,
        ITextEnhancer textEnhancer,
        ConfigManager configManager,
        IGraphIndexingService? graphIndexing = null,
        IEmbeddingTokenCounter? tokenCounter = null,
        ILogger<FileIndexPipeline>? logger = null)
    {
        _vectorStore = vectorStore;
        _bm25Store = bm25Store;
        _chunker = chunker;
        _embeddingService = embeddingService;
        _textEnhancer = textEnhancer;
        _configManager = configManager;
        _graphIndexing = graphIndexing;
        _tokenCounter = tokenCounter;
        _logger = logger;
    }

    public async Task<FileProcessContext?> PrepareAsync(
        string filePath,
        IReadOnlyList<SourceFolder> sources,
        bool force,
        CancellationToken ct = default)
    {
        var config = _configManager.Load();
        var chunking = config.Chunking;

        var content = await File.ReadAllTextAsync(filePath, ct);
        var contentHash = ComputeHash(content);
        var chunkingHash = ComputeChunkingHash(chunking);

        var matchedSource = sources
            .OrderByDescending(s => s.Path.Length)
            .FirstOrDefault(s =>
                filePath.StartsWith(s.Path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                filePath.Equals(s.Path, StringComparison.OrdinalIgnoreCase) ||
                filePath.StartsWith(PathUtility.NormalizePath(s.Path) + '/', StringComparison.OrdinalIgnoreCase) ||
                filePath.Equals(PathUtility.NormalizePath(s.Path), StringComparison.OrdinalIgnoreCase));

        var existingFile = await _vectorStore.GetFileByPathAsync(filePath, ct);
        var fileId = existingFile?.Id ?? Guid.NewGuid().ToString();

        // 跳过条件：hash 匹配 AND 上次已完整完成（status='complete'） AND 分块配置未变更
        // status='pending' 说明上次中断，即使 hash 一致也必须重新索引
        // chunking hash 不一致说明分块参数变更，必须重新分块
        if (!force && existingFile != null
            && existingFile.ContentHash == contentHash
            && existingFile.IndexedStatus == "complete"
            && existingFile.ChunkingHash == chunkingHash)
        {
            _logger?.LogDebug("内容未变化，跳过: {FileName}", Path.GetFileName(filePath));
            return null;
        }

        var chunks = _chunker.Chunk(content, new ChunkingOptions
        {
            MaxTokens = chunking.MaxTokens,
            MinTokens = chunking.MinTokens,
            OverlapTokens = chunking.OverlapTokens,
            PreserveHeadings = chunking.PreserveHeadings,
            PreserveCodeBlocks = chunking.PreserveCodeBlocks
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
            ChunkingHash = chunkingHash,
            Title = Path.GetFileNameWithoutExtension(filePath),
            ModifiedAt = File.GetLastWriteTime(filePath),
            ChunkCount = chunks.Count,
            IndexedAt = DateTime.UtcNow,
            IndexedStatus = "pending"   // Phase3 完成后改为 complete
        };

        await _vectorStore.UpsertFileAsync(fileRecord, ct);

        var oldChunks = await _vectorStore.GetChunksByFileAsync(fileId, ct);
        var oldChunkIds = oldChunks.Select(c => c.Id).ToList();

        var context = new FileProcessContext
        {
            FileRecord = fileRecord,
            Content = content,
            Chunks = chunks,
            OldChunkIds = oldChunkIds
        };

        foreach (var chunk in chunks)
        {
            chunk.FileId = fileId;
            chunk.Id = Guid.NewGuid().ToString();
            chunk.Source = sourceName;
            // P5: 预计算增强内容用于 embedding，原始 Content 保留给 BM25 和显示
            chunk.EnhancedContent = _textEnhancer.Enhance(chunk, fileRecord);
        }

        var (changedCount, exact) = await LimitEmbeddingInputAsync(context.Chunks, config, ct);
        if (changedCount > 0)
        {
            fileRecord.ChunkCount = chunks.Count;
            await _vectorStore.UpsertFileAsync(fileRecord, ct);
        }

        if (changedCount > 0)
        {
            _logger?.LogWarning(
                "{Action} {Count}/{Total} 条嵌入输入: {File}",
                exact ? "已精确拆分" : "已截断", changedCount, chunks.Count, filePath);
        }

        return context;
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

        // VectorOnly 不修改已存 chunk：截断结果只保存在临时嵌入输入（EmbeddingInputs），
        // 不写回 chunks 表，保证"仅重推向量"语义，图谱/BM25 引用不变。
        var config = _configManager.Load();
        var inputs = await BuildVectorOnlyEmbeddingInputsAsync(chunks, config, ct);
        var changedCount = inputs.Count;

        if (changedCount > 0)
        {
            _logger?.LogWarning(
                "已截断 {Count}/{Total} 条存量嵌入输入: {File}",
                changedCount, chunks.Count, filePath);
        }

        return new FileProcessContext
        {
            FileRecord = existingFile,
            Content = "",
            Chunks = chunks,
            OldChunkIds = new List<string>(),
            EmbeddingInputs = inputs
        };
    }

    public async Task FinalizeAsync(
        FileProcessContext ctx,
        IReadOnlyDictionary<string, string> filenameMap,
        CancellationToken ct = default,
        bool updateGraph = true,
        bool updateBm25 = true)
    {
        // VectorOnly：只重推向量，不改 BM25/图谱（updateBm25=false 由调用方传入）
        if (updateBm25)
            await _bm25Store.IndexBatchAsync(ctx.Chunks.Select(c => (c.Id, c.Content)));

        if (updateGraph && _graphIndexing != null)
        {
            Func<string, string?> resolver = shortId =>
                filenameMap.TryGetValue(shortId, out var full) ? full : null;
            await _graphIndexing.UpdateGraphAsync(ctx.FileRecord, ctx.Content, ctx.Chunks, ct, resolver);
        }

        // 新向量/BM25/图谱都已写入后再清理旧版本，避免推理失败造成搜索空窗。
        foreach (var oldChunkId in ctx.OldChunkIds)
            await _vectorStore.DeleteChunkAsync(oldChunkId, ct);
        if (ctx.OldChunkIds.Count > 0 && updateBm25)
            await _bm25Store.DeleteDocumentsByIdsAsync(ctx.OldChunkIds);

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

    /// <summary>
    /// 分块配置指纹：影响分块结果的参数变更后，文件必须重新分块。
    /// 与具体参数值一致即可，无关顺序（拼接后统一哈希）。
    /// </summary>
    private static string ComputeChunkingHash(Core.Models.ChunkingConfig chunking)
    {
        using var sha = SHA256.Create();
        var text = string.Join("|",
            chunking.MaxTokens,
            chunking.MinTokens,
            chunking.OverlapTokens,
            chunking.PreserveHeadings,
            chunking.PreserveCodeBlocks);
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(text)));
    }

    private async Task<(int ChangedCount, bool Exact)> LimitEmbeddingInputAsync(
        List<ChunkRecord> chunks,
        Core.Models.ObsidianRagConfig config,
        CancellationToken ct)
    {
        var maxInputTokens = config.Embedding.ApiMaxInputTokens ?? config.Embedding.MaxSequenceLength;

        if (_tokenCounter != null)
        {
            try
            {
                var splitCount = await SplitWithExactTokenCounterAsync(chunks, config, ct);
                return (splitCount, true);
            }
            catch (NotSupportedException)
            {
                _logger?.LogDebug("嵌入服务不支持精确分词，回退到保守字符截断。");
            }
            catch (HttpRequestException ex)
            {
                // 401/400/500 等非"不支持"响应：不阻断索引，降级为字符截断
                _logger?.LogWarning(ex, "嵌入服务 tokenize 探测失败（{Status}），回退到字符截断。",
                    ex.StatusCode?.ToString() ?? "无状态码");
            }
            catch (InvalidOperationException ex)
            {
                _logger?.LogWarning(ex, "嵌入服务 tokenize 响应异常，回退到字符截断。");
            }
            catch (TimeoutException ex)
            {
                _logger?.LogWarning(ex, "嵌入服务 tokenize 超时，回退到字符截断。");
            }
        }

        // ONNX 与降级路径统一按字符预算截断（maxInputTokens 已含 MaxSequenceLength 兜底）
        var truncatedCount = TruncateEmbeddingInput(chunks, Math.Max(1, maxInputTokens - 2));
        return (truncatedCount, false);
    }

    /// <summary>
    /// VectorOnly：用精确 token 计数逐条检查，超限 chunk 生成临时嵌入输入（截断文本）。
    /// 不修改 chunk 对象、不写回仓库；返回值 chunkId → 嵌入文本，供嵌入阶段使用。
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> BuildVectorOnlyEmbeddingInputsAsync(
        List<ChunkRecord> chunks,
        Core.Models.ObsidianRagConfig config,
        CancellationToken ct)
    {
        var maxInputTokens = config.Embedding.ApiMaxInputTokens ?? config.Embedding.MaxSequenceLength;
        var budget = Math.Max(1, maxInputTokens - 2);
        var inputs = new Dictionary<string, string>();

        foreach (var chunk in chunks)
        {
            var text = chunk.EnhancedContent ?? chunk.Content;
            var needsTruncate = text.Length > budget;

            if (needsTruncate && _tokenCounter != null)
            {
                try
                {
                    var tokenCount = await _tokenCounter.CountTokensAsync(text, ct);
                    needsTruncate = tokenCount > budget;
                }
                catch (NotSupportedException)
                {
                    _logger?.LogDebug("嵌入服务不支持精确分词，VectorOnly 回退到保守字符截断。");
                }
                catch (HttpRequestException ex)
                {
                    _logger?.LogWarning(ex, "嵌入服务 tokenize 探测失败，VectorOnly 回退到字符截断。");
                }
            }

            if (needsTruncate)
                inputs[chunk.Id] = TruncateToCharacterBudget(text, budget);
        }

        return inputs;
    }

    private async Task<int> SplitWithExactTokenCounterAsync(
        List<ChunkRecord> chunks,
        Core.Models.ObsidianRagConfig config,
        CancellationToken ct)
    {
        var maxInputTokens = config.Embedding.ApiMaxInputTokens ?? config.Embedding.MaxSequenceLength;
        var tokenBudget = Math.Max(1, maxInputTokens - 2); // llama.cpp embeddings adds BOS/EOS.
        var output = new List<ChunkRecord>(chunks.Count);

        foreach (var chunk in chunks)
        {
            var text = chunk.EnhancedContent ?? chunk.Content;
            var tokenCount = await _tokenCounter!.CountTokensAsync(text, ct);
            if (tokenCount <= tokenBudget)
            {
                chunk.TokenCount = tokenCount;
                output.Add(chunk);
                continue;
            }

            // 增强前缀（TextEnhancer 追加的 [标题]/[章节] 等）仅用于嵌入输入，
            // 拆分时从正文片段中剔除，保证 Content/BM25 仍是原始正文语义。
            var prefix = ExtractEnhancePrefix(chunk, text);

            var parts = await SplitTextByTokenBudgetAsync(text, tokenBudget, ct);
            for (var partIndex = 0; partIndex < parts.Count; partIndex++)
            {
                var part = parts[partIndex];
                if (string.IsNullOrWhiteSpace(part)) continue;

                var partTokenCount = await _tokenCounter.CountTokensAsync(part, ct);
                if (partTokenCount > tokenBudget)
                    throw new InvalidOperationException($"精确分词拆分后仍超过限制: {partTokenCount} > {tokenBudget}");

                var plainContent = StripPrefix(part, prefix);

                output.Add(new ChunkRecord
                {
                    Id = partIndex == 0 ? chunk.Id : Guid.NewGuid().ToString(),
                    FileId = chunk.FileId,
                    // A4: Content 保留原始正文（去除嵌入专用前缀），BM25/展示语义不被破坏
                    Content = plainContent,
                    // 嵌入输入：含前缀的拆分文本，单独存放
                    EnhancedContent = part,
                    TokenCount = partTokenCount,
                    StartLine = chunk.StartLine,
                    EndLine = chunk.EndLine,
                    StartColumn = chunk.StartColumn,
                    EndColumn = chunk.EndColumn,
                    HeadingPath = chunk.HeadingPath,
                    Level = chunk.Level,
                    ChunkType = ChunkType.Forced,
                    Weight = chunk.Weight,
                    Tags = chunk.Tags,
                    Keywords = chunk.Keywords,
                    Source = chunk.Source,
                    AggregateType = chunk.AggregateType,
                    AggregateRange = chunk.AggregateRange,
                    ChildChunkCount = chunk.ChildChunkCount,
                    // 保持同源 chunk 的排序连续性
                    ChunkOrder = chunk.ChunkOrder + partIndex * 0.000001,
                    ContentHash = chunk.ContentHash
                });
            }
        }

        var changedCount = output.Count - chunks.Count;
        for (var i = 0; i < output.Count; i++)
            output[i].ChunkIndex = i;

        chunks.Clear();
        chunks.AddRange(output);
        return Math.Max(0, changedCount);
    }

    /// <summary>
    /// 提取嵌入输入的增强前缀：EnhancedContent 以 "\n" 拼接方式追加在原文之后，
    /// 前缀 = EnhancedContent 中位于原文之前的固定部分。
    /// </summary>
    private static string ExtractEnhancePrefix(ChunkRecord chunk, string enhancedText)
    {
        if (string.IsNullOrEmpty(chunk.Content) || enhancedText == chunk.Content)
            return string.Empty;
        if (enhancedText.EndsWith(chunk.Content, StringComparison.Ordinal))
            return enhancedText[..^chunk.Content.Length];
        return string.Empty;
    }

    private static string StripPrefix(string part, string prefix)
    {
        if (!string.IsNullOrEmpty(prefix) && part.StartsWith(prefix, StringComparison.Ordinal))
            return part[prefix.Length..];
        return part;
    }

    private async Task<List<string>> SplitTextByTokenBudgetAsync(
        string text,
        int tokenBudget,
        CancellationToken ct)
    {
        var parts = new List<string>();
        var current = new StringBuilder();

        async Task FlushAsync()
        {
            if (current.Length > 0)
            {
                parts.Add(current.ToString());
                current.Clear();
            }
        }

        async Task<bool> AppendAsync(string candidate)
        {
            var combined = current.Length == 0 ? candidate : current + "\n" + candidate;
            if (await _tokenCounter!.CountTokensAsync(combined, ct) <= tokenBudget)
            {
                current.Clear();
                current.Append(combined);
                return true;
            }

            return false;
        }

        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (await _tokenCounter!.CountTokensAsync(line, ct) <= tokenBudget)
            {
                if (!await AppendAsync(line))
                {
                    await FlushAsync();
                    if (!await AppendAsync(line))
                        throw new InvalidOperationException("无法按 token 预算拆分嵌入输入。");
                }

                continue;
            }

            await FlushAsync();
            foreach (var sentence in SplitSentenceCandidates(line))
            {
                if (await _tokenCounter.CountTokensAsync(sentence, ct) <= tokenBudget)
                {
                    if (!await AppendAsync(sentence))
                    {
                        await FlushAsync();
                        if (!await AppendAsync(sentence))
                            throw new InvalidOperationException("无法按 token 预算拆分嵌入输入。");
                    }

                    continue;
                }

                await foreach (var part in SplitOversizedTextAsync(sentence, tokenBudget, ct))
                    parts.Add(part);
            }
        }

        await FlushAsync();
        return parts;
    }

    private async IAsyncEnumerable<string> SplitOversizedTextAsync(
        string text,
        int tokenBudget,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var start = 0;
        var estimate = Math.Max(1, text.Length * tokenBudget / Math.Max(1, await _tokenCounter!.CountTokensAsync(text, ct)));

        while (start < text.Length)
        {
            var length = Math.Min(estimate, text.Length - start);
            if (start + length < text.Length && char.IsHighSurrogate(text[start + length - 1]))
                length--;
            if (length <= 0) length = 1;

            var candidate = text.Substring(start, length);
            var tokens = await _tokenCounter.CountTokensAsync(candidate, ct);
            while (tokens > tokenBudget && length > 1)
            {
                estimate = Math.Max(1, (int)((double)length * tokenBudget / tokens));
                length = Math.Min(length - 1, estimate);
                if (start + length < text.Length && char.IsHighSurrogate(text[start + length - 1]))
                    length--;
                if (length <= 0) length = 1;
                candidate = text.Substring(start, length);
                tokens = await _tokenCounter.CountTokensAsync(candidate, ct);
            }

            yield return candidate;
            start += length;
        }
    }

    private static IEnumerable<string> SplitSentenceCandidates(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('。' or '！' or '？' or '.' or '!' or '?' or '；' or ';'))
                continue;

            var end = i + 1;
            if (end < text.Length && text[end] is '"' or '”' or '」')
                end++;

            yield return text[start..end];
            start = end;
            i = end - 1;
        }

        if (start < text.Length)
            yield return text[start..];
    }

    private int TruncateEmbeddingInput(IEnumerable<ChunkRecord> chunks, int maxCharacters)
    {
        var truncatedCount = 0;

        foreach (var chunk in chunks)
        {
            var text = chunk.EnhancedContent ?? chunk.Content;
            if (text.Length <= maxCharacters) continue;

            chunk.EnhancedContent = TruncateToCharacterBudget(text, maxCharacters);
            truncatedCount++;
        }

        return truncatedCount;
    }

    private static string TruncateToCharacterBudget(string text, int maxCharacters)
    {
        if (text.Length <= maxCharacters) return text;

        var length = maxCharacters;
        if (length > 0 && char.IsHighSurrogate(text[length - 1]))
            length--;

        return text[..length];
    }
}
