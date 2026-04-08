using System.Text.Json;
using ObsidianRAG.Core.Interfaces;
using ObsidianRAG.Core.Models;

namespace ObsidianRAG.Storage;

/// <summary>
/// JSON 向量存储（开发测试用）
/// </summary>
public class JsonVectorStore : IVectorStore, IDisposable
{
    private readonly string _dataPath;
    private readonly JsonSerializerOptions _jsonOptions;
    private Dictionary<string, FileRecord> _files = new();
    private Dictionary<string, ChunkRecord> _chunks = new();
    private Dictionary<string, VectorRecord> _vectors = new();
    private bool _disposed;

    public JsonVectorStore(string? dataPath = null)
    {
        _dataPath = dataPath ?? Path.Combine(Environment.CurrentDirectory, "data");
        Directory.CreateDirectory(_dataPath);
        
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        LoadDataAsync().GetAwaiter().GetResult();
    }

    // ==================== 文件操作 ====================

    public async Task UpsertFileAsync(FileRecord file, CancellationToken cancellationToken = default)
    {
        _files[file.Id] = file;
        await SaveDataAsync(cancellationToken);
    }

    public Task<FileRecord?> GetFileAsync(string id, CancellationToken cancellationToken = default)
    {
        _files.TryGetValue(id, out var file);
        return Task.FromResult(file);
    }

    public Task<FileRecord?> GetFileByPathAsync(string path, CancellationToken cancellationToken = default)
    {
        var file = _files.Values.FirstOrDefault(f => f.Path == path);
        return Task.FromResult(file);
    }

    public Task<FileRecord?> GetFileByHashAsync(string contentHash, CancellationToken cancellationToken = default)
    {
        var file = _files.Values.FirstOrDefault(f => f.ContentHash == contentHash);
        return Task.FromResult(file);
    }

    public async Task DeleteFileAsync(string id, CancellationToken cancellationToken = default)
    {
        _files.Remove(id);
        
        var chunkIds = _chunks.Values.Where(c => c.FileId == id).Select(c => c.Id).ToList();
        foreach (var chunkId in chunkIds)
        {
            _chunks.Remove(chunkId);
            _vectors.Remove(chunkId);
        }
        
        await SaveDataAsync(cancellationToken);
    }

    public Task<IEnumerable<FileRecord>> GetAllFilesAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_files.Values.AsEnumerable());
    }

    // ==================== 分段操作 ====================

    public async Task UpsertChunkAsync(ChunkRecord chunk, CancellationToken cancellationToken = default)
    {
        _chunks[chunk.Id] = chunk;
        await SaveDataAsync(cancellationToken);
    }

    public async Task UpsertChunksAsync(IEnumerable<ChunkRecord> chunks, CancellationToken cancellationToken = default)
    {
        foreach (var chunk in chunks)
        {
            _chunks[chunk.Id] = chunk;
        }
        await SaveDataAsync(cancellationToken);
    }

    public Task<ChunkRecord?> GetChunkAsync(string id, CancellationToken cancellationToken = default)
    {
        _chunks.TryGetValue(id, out var chunk);
        return Task.FromResult(chunk);
    }

    public Task<IEnumerable<ChunkRecord>> GetChunksByFileAsync(string fileId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_chunks.Values.Where(c => c.FileId == fileId).AsEnumerable());
    }

    public async Task DeleteChunksByFileAsync(string fileId, CancellationToken cancellationToken = default)
    {
        var chunkIds = _chunks.Values.Where(c => c.FileId == fileId).Select(c => c.Id).ToList();
        foreach (var chunkId in chunkIds)
        {
            _chunks.Remove(chunkId);
            _vectors.Remove(chunkId);
        }
        await SaveDataAsync(cancellationToken);
    }

    // ==================== 向量操作 ====================

    public async Task UpsertVectorAsync(VectorRecord vector, CancellationToken cancellationToken = default)
    {
        _vectors[vector.ChunkId] = vector;
        await SaveDataAsync(cancellationToken);
    }

    public async Task UpsertVectorsAsync(IEnumerable<VectorRecord> vectors, CancellationToken cancellationToken = default)
    {
        foreach (var vector in vectors)
        {
            _vectors[vector.ChunkId] = vector;
        }
        await SaveDataAsync(cancellationToken);
    }

    public Task<VectorRecord?> GetVectorAsync(string id, CancellationToken cancellationToken = default)
    {
        var vector = _vectors.Values.FirstOrDefault(v => v.Id == id);
        return Task.FromResult(vector);
    }

    public Task<VectorRecord?> GetVectorByChunkIdAsync(string chunkId, CancellationToken cancellationToken = default)
    {
        _vectors.TryGetValue(chunkId, out var vector);
        return Task.FromResult(vector);
    }

    public async Task DeleteVectorAsync(string id, CancellationToken cancellationToken = default)
    {
        var vector = _vectors.Values.FirstOrDefault(v => v.Id == id);
        if (vector != null)
        {
            _vectors.Remove(vector.ChunkId);
        }
        await SaveDataAsync(cancellationToken);
    }

    // ==================== 检索操作 ====================

    public Task<IEnumerable<SearchResult>> SearchAsync(
        float[] queryVector, 
        int topK, 
        CancellationToken cancellationToken = default)
    {
        var results = new List<(string ChunkId, float Score)>();

        foreach (var (chunkId, vector) in _vectors)
        {
            var score = CosineSimilarity(queryVector, vector.Vector);
            results.Add((chunkId, score));
        }

        var topResults = results
            .OrderByDescending(r => r.Score)
            .Take(topK)
            .ToList();

        var searchResults = BuildSearchResults(topResults);
        return Task.FromResult(searchResults.AsEnumerable());
    }

    public Task<IEnumerable<SearchResult>> SearchByAggregateTypeAsync(
        float[] queryVector,
        AggregateType aggregateType,
        int topK,
        CancellationToken cancellationToken = default)
    {
        var results = new List<(string ChunkId, float Score)>();

        foreach (var (chunkId, vector) in _vectors)
        {
            var chunk = _chunks.GetValueOrDefault(chunkId);
            if (chunk == null || chunk.AggregateType != aggregateType) continue;

            var score = CosineSimilarity(queryVector, vector.Vector);
            results.Add((chunkId, score));
        }

        var topResults = results
            .OrderByDescending(r => r.Score)
            .Take(topK)
            .ToList();

        var searchResults = BuildSearchResults(topResults);
        return Task.FromResult(searchResults.AsEnumerable());
    }

    public Task<IEnumerable<SearchResult>> SearchInIdsAsync(
        float[] queryVector,
        IEnumerable<string> ids,
        int topK,
        CancellationToken cancellationToken = default)
    {
        var idSet = ids.ToHashSet();
        var results = new List<(string ChunkId, float Score)>();

        foreach (var (chunkId, vector) in _vectors)
        {
            if (!idSet.Contains(chunkId)) continue;

            var score = CosineSimilarity(queryVector, vector.Vector);
            results.Add((chunkId, score));
        }

        var topResults = results
            .OrderByDescending(r => r.Score)
            .ToList();

        var searchResults = BuildSearchResults(topResults);
        return Task.FromResult(searchResults.AsEnumerable());
    }

    public async Task DeleteBySourceAsync(string source, CancellationToken cancellationToken = default)
    {
        var fileIds = _files.Values.Where(f => f.Source == source).Select(f => f.Id).ToHashSet();
        
        foreach (var fileId in fileIds)
        {
            _files.Remove(fileId);
        }

        var chunkIds = _chunks.Values.Where(c => fileIds.Contains(c.FileId)).Select(c => c.Id).ToList();
        foreach (var chunkId in chunkIds)
        {
            _chunks.Remove(chunkId);
            _vectors.Remove(chunkId);
        }

        await SaveDataAsync(cancellationToken);
    }

    public async Task StoreBatchAsync(IEnumerable<VectorRecord> records, CancellationToken cancellationToken = default)
    {
        foreach (var record in records)
        {
            _vectors[record.ChunkId] = record;
        }
        await SaveDataAsync(cancellationToken);
    }

    // ==================== 辅助方法 ====================

    private List<SearchResult> BuildSearchResults(List<(string ChunkId, float Score)> topResults)
    {
        var searchResults = new List<SearchResult>();
        
        foreach (var (chunkId, score) in topResults)
        {
            var chunk = _chunks.GetValueOrDefault(chunkId);
            if (chunk == null) continue;

            var file = _files.GetValueOrDefault(chunk.FileId);
            if (file == null) continue;

            searchResults.Add(new SearchResult
            {
                ChunkId = chunk.Id,
                FileId = chunk.FileId,
                FilePath = file.Path,
                Source = file.Source,
                Title = file.Title,
                Content = chunk.Content,
                Score = score,
                StartLine = chunk.StartLine,
                EndLine = chunk.EndLine,
                HeadingPath = chunk.HeadingPath,
                Level = chunk.Level,
                ChildChunkCount = chunk.ChildChunkCount,
                AggregateType = chunk.AggregateType
            });
        }

        return searchResults;
    }

    private float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;

        float dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        var denominator = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denominator < 1e-10f ? 0 : dot / denominator;
    }

    private async Task LoadDataAsync()
    {
        var filesPath = Path.Combine(_dataPath, "files.json");
        var chunksPath = Path.Combine(_dataPath, "chunks.json");
        var vectorsPath = Path.Combine(_dataPath, "vectors.json");

        if (File.Exists(filesPath))
        {
            var json = await File.ReadAllTextAsync(filesPath);
            _files = JsonSerializer.Deserialize<Dictionary<string, FileRecord>>(json, _jsonOptions) ?? new();
        }

        if (File.Exists(chunksPath))
        {
            var json = await File.ReadAllTextAsync(chunksPath);
            _chunks = JsonSerializer.Deserialize<Dictionary<string, ChunkRecord>>(json, _jsonOptions) ?? new();
        }

        if (File.Exists(vectorsPath))
        {
            var json = await File.ReadAllTextAsync(vectorsPath);
            _vectors = JsonSerializer.Deserialize<Dictionary<string, VectorRecord>>(json, _jsonOptions) ?? new();
        }
    }

    private async Task SaveDataAsync(CancellationToken cancellationToken = default)
    {
        var filesPath = Path.Combine(_dataPath, "files.json");
        var chunksPath = Path.Combine(_dataPath, "chunks.json");
        var vectorsPath = Path.Combine(_dataPath, "vectors.json");

        await File.WriteAllTextAsync(filesPath, JsonSerializer.Serialize(_files, _jsonOptions), cancellationToken);
        await File.WriteAllTextAsync(chunksPath, JsonSerializer.Serialize(_chunks, _jsonOptions), cancellationToken);
        await File.WriteAllTextAsync(vectorsPath, JsonSerializer.Serialize(_vectors, _jsonOptions), cancellationToken);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            SaveDataAsync().GetAwaiter().GetResult();
            _disposed = true;
        }
    }
}
