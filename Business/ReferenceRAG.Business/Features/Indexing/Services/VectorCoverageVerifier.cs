using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Service.Services;

namespace ReferenceRAG.Business.Features.Indexing.Services;

/// <summary>Checks the rebuilt index while retaining per-file locks through publication.</summary>
public sealed class VectorCoverageVerifier
{
    private readonly IVectorStore _vectors;
    private readonly IBM25Store _bm25;
    private readonly IEmbeddingService _embedding;
    private readonly IFileProcessingGuard _guard;
    private readonly Func<ObsidianRagConfig>? _currentConfig;
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public VectorCoverageVerifier(IVectorStore vectors, IBM25Store bm25, IEmbeddingService embedding,
        IFileProcessingGuard guard, Func<ObsidianRagConfig>? currentConfig = null)
    {
        _vectors = vectors;
        _bm25 = bm25;
        _embedding = embedding;
        _guard = guard;
        _currentConfig = currentConfig;
    }

    public async Task VerifyAndCommitAsync(ObsidianRagConfig config, Func<Task> commit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(commit);
        var fingerprint = Fingerprint(config);
        var sources = config.Sources.Where(s => s.Enabled).ToArray();
        var sourceNames = sources.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (sources.Length == 0 || sourceNames.Count != sources.Length)
            throw new InvalidOperationException("无法验证空数据源或重复数据源名称。");
        var diskBefore = Scan(sources, ct);
        var initialFiles = (await _vectors.GetAllFilesAsync(ct)).ToList();
        var lockPaths = diskBefore.Keys.Concat(initialFiles.Where(f => f.Source != null && sourceNames.Contains(f.Source))
                .Select(f => Path.GetFullPath(f.Path)))
            .Distinct(PathComparer).OrderBy(p => p, PathComparer).ToArray();
        var held = new List<string>();
        try
        {
            foreach (var path in lockPaths)
            {
                ct.ThrowIfCancellationRequested();
                if (!_guard.TryAcquire(path)) throw new InvalidOperationException($"文件正在索引，无法验证覆盖: {path}");
                held.Add(path);
            }
            var disk = Scan(sources, ct);
            if (!SameInventory(diskBefore, disk)) throw new InvalidOperationException("扫描期间文件集合发生变化。");
            var files = (await _vectors.GetAllFilesAsync(ct)).ToList();
            if (files.Any(f => string.IsNullOrWhiteSpace(f.Source)))
                throw new InvalidOperationException("索引中存在来源未知的文件，无法证明全局覆盖。");
            var indexed = new Dictionary<string, FileRecord>(PathComparer);
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                var path = Path.GetFullPath(file.Path);
                var inScope = file.Source != null && sourceNames.Contains(file.Source);
                if (!inScope && !disk.ContainsKey(path)) continue;
                if (!inScope || !indexed.TryAdd(path, file))
                    throw new InvalidOperationException("索引包含来源错误或重复的文件。");
            }
            if (indexed.Count != disk.Count || disk.Keys.Any(path => !indexed.ContainsKey(path)))
                throw new InvalidOperationException("磁盘文件与向量索引文件集合不一致。");
            if (indexed.Values.Select(f => f.Id).Distinct(StringComparer.Ordinal).Count() != indexed.Count)
                throw new InvalidOperationException("索引中的文件标识重复。");
            foreach (var (path, source) in disk)
            {
                var file = indexed[path];
                if (!string.Equals(file.Source, source, StringComparison.OrdinalIgnoreCase) || file.IndexedStatus != "complete" || file.ChunkCount <= 0)
                    throw new InvalidOperationException($"文件索引未完整完成: {path}");
                var before = new FileInfo(path);
                var length = before.Length;
                var modified = before.LastWriteTimeUtc;
                var content = await File.ReadAllTextAsync(path, ct);
                var after = new FileInfo(path);
                if (!after.Exists || after.Length != length || after.LastWriteTimeUtc != modified ||
                    !string.Equals(file.ContentHash, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"文件内容与索引不一致: {path}");
            }
            var chunks = new List<ChunkRecord>();
            foreach (var ids in indexed.Values.Select(f => f.Id).Chunk(250))
                chunks.AddRange(await _vectors.GetChunksByFileIdsAsync(ids, ct));
            var byFile = chunks.GroupBy(c => c.FileId).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            if (chunks.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != chunks.Count ||
                indexed.Values.Any(f => !byFile.TryGetValue(f.Id, out var count) || count != f.ChunkCount) ||
                byFile.Keys.Any(id => !indexed.Values.Any(f => f.Id == id)))
                throw new InvalidOperationException("文件分块集合不完整。");
            var expected = chunks.ToDictionary(c => c.Id, c => c.Content, StringComparer.Ordinal);
            foreach (var ids in expected.Keys.Chunk(250))
            {
                var vectors = await _vectors.GetVectorsByChunkIdsAsync(ids, _embedding.ModelName, ct);
                if (ids.Any(id => !vectors.TryGetValue(id, out var vector) ||
                    !string.Equals(vector.ModelName, _embedding.ModelName, StringComparison.Ordinal) ||
                    vector.Dimension != _embedding.Dimension || vector.Dimension <= 0 ||
                    vector.Vector.Length != vector.Dimension || vector.Vector.Any(v => !float.IsFinite(v))))
                    throw new InvalidOperationException("向量缺失或模型、维度不匹配。");
            }
            if (!await _bm25.VerifyExactIndexAsync(expected, ct))
                throw new InvalidOperationException("BM25 索引与文件分块不一致。");
            foreach (var (path, file) in indexed)
            {
                var content = await File.ReadAllTextAsync(path, ct);
                if (!string.Equals(file.ContentHash, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"验证期间文件内容发生变化: {path}");
            }
            if (!SameInventory(disk, Scan(sources, ct)) || Fingerprint(_currentConfig?.Invoke() ?? config) != fingerprint)
                throw new InvalidOperationException("验证期间数据源或配置发生变化。");
            ct.ThrowIfCancellationRequested();
            await commit();
        }
        finally
        {
            foreach (var path in held) _guard.Release(path);
        }
    }

    internal static Dictionary<string, string> ScanSources(SourceFolder[] sources, CancellationToken ct) => Scan(sources, ct);

    private static Dictionary<string, string> Scan(SourceFolder[] sources, CancellationToken ct)
    {
        var files = new Dictionary<string, string>(PathComparer);
        foreach (var source in sources)
            foreach (var path in IndexService.EnumerateLexicalFiles(source, ct))
            {
                var owner = IndexService.FindLexicalSource(path, sources);
                if (owner != null && IndexService.IsLexicalFileAllowed(path, owner)) files[path] = owner.Name;
            }
        return files;
    }

    internal static bool SameInventory(Dictionary<string, string> left, Dictionary<string, string> right) =>
        left.Count == right.Count && left.All(p => right.TryGetValue(p.Key, out var source) &&
            string.Equals(p.Value, source, StringComparison.OrdinalIgnoreCase));

    internal static string Fingerprint(ObsidianRagConfig config) => JsonSerializer.Serialize(new
    {
        config.Sources, config.Chunking, config.Embedding, config.Indexing.EmbeddingEnabled, config.Search.EnableLexical
    });
}
