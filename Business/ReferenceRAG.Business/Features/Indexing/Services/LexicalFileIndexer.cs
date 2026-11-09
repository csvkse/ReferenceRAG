using System.Security.Cryptography;
using System.Text;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Storage;

namespace ReferenceRAG.Business.Features.Indexing.Services;

/// <summary>Builds immutable lexical generations without loading an embedding model.</summary>
public sealed class LexicalFileIndexer
{
    private readonly IMarkdownChunker _chunker;
    private readonly LexicalIndexCoordinator _coordinator;
    private readonly ConfigManager? _configManager;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LexicalFileIndexer(IMarkdownChunker chunker, LexicalIndexCoordinator coordinator, ConfigManager? configManager = null)
    {
        _chunker = chunker ?? throw new ArgumentNullException(nameof(chunker));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _configManager = configManager;
    }

    public Task<FileRecord> IndexAsync(string path, string source, CancellationToken ct = default) =>
        IndexAsync(path, source, false, ct);

    public async Task<FileRecord> IndexAsync(string path, string source, bool force, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        path = Path.GetFullPath(path);
        await _gate.WaitAsync(ct);
        try
        {
            var before = new FileInfo(path);
            var length = before.Length;
            var lastWrite = before.LastWriteTimeUtc;
            var content = await File.ReadAllTextAsync(path, ct);
            var after = new FileInfo(path);
            if (!after.Exists || after.Length != length || after.LastWriteTimeUtc != lastWrite)
                throw new IOException("文件读取期间发生变化，请重试索引。");
            var previous = await _coordinator.GetActiveFileByPathAsync(path, ct);
            var contentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
            var settings = _configManager?.Load().Chunking ?? new ChunkingConfig();
            var options = new ChunkingOptions { Mode = "Fixed", ModelProfile = null,
                MaxTokens = settings.MaxTokens, MinTokens = settings.MinTokens, OverlapTokens = settings.OverlapTokens,
                PreserveHeadings = settings.PreserveHeadings, PreserveCodeBlocks = settings.PreserveCodeBlocks };
            var fingerprint = $"lexical-fixed-v2:{options.MaxTokens}:{options.MinTokens}:{options.OverlapTokens}:{options.PreserveHeadings}:{options.PreserveCodeBlocks}";
            if (!force && previous != null && previous.ContentHash == contentHash &&
                previous.ChunkingHash == fingerprint &&
                string.Equals(previous.Source, source, StringComparison.OrdinalIgnoreCase) &&
                await _coordinator.IsActiveIndexCompleteAsync(previous.Id, ct))
                return previous;
            var file = new FileRecord
            {
                Id = previous?.Id ?? Guid.NewGuid().ToString(),
                Path = path,
                FileName = Path.GetFileName(path),
                ParentFolder = Path.GetDirectoryName(path),
                Title = Path.GetFileNameWithoutExtension(path),
                Source = source,
                ContentLength = content.Length,
                ContentHash = contentHash,
                ChunkingHash = fingerprint,
                ModifiedAt = lastWrite,
                IndexedAt = DateTime.UtcNow,
                IndexedStatus = "complete"
            };
            // Fixed-size chunking uses the existing model-independent tokenizer/estimator.
            var chunks = string.IsNullOrWhiteSpace(content)
                ? new List<ChunkRecord>()
                : _chunker.Chunk(content, options);
            foreach (var chunk in chunks)
            {
                chunk.Id = Guid.NewGuid().ToString();
                chunk.FileId = file.Id;
                chunk.Source = source;
            }
            file.ChunkCount = chunks.Count;
            var generation = Guid.NewGuid().ToString("N");
            await _coordinator.StageDocumentsAsync(file, generation, chunks, ct);
            await _coordinator.StageIndexAsync(file.Id, generation, ct);
            await _coordinator.PublishAsync(file.Id, generation, ct);
            return file;
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        path = Path.GetFullPath(path);
        await _gate.WaitAsync(ct);
        try
        {
            var file = await _coordinator.GetActiveFileByPathAsync(path, ct);
            if (file != null) await _coordinator.DeleteFileAsync(file.Id, ct);
        }
        finally { _gate.Release(); }
    }
}
