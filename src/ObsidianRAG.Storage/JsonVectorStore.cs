using System.Text.Json;
using ObsidianRAG.Core.Helpers;
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

    public async Task DeleteChunkAsync(string id, CancellationToken cancellationToken = default)
    {
        _chunks.Remove(id);
        _vectors.Remove(id);
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
        // JsonVectorStore 不区分模型，使用默认实现
        return SearchAsync(queryVector, "default", topK, cancellationToken);
    }

    public Task<IEnumerable<SearchResult>> SearchAsync(
        float[] queryVector,
        string modelName,
        int topK,
        CancellationToken cancellationToken = default)
    {
        var results = new List<(string ChunkId, float Score)>();

        foreach (var (chunkId, vector) in _vectors)
        {
            var score = MathHelper.CosineSimilarity(queryVector, vector.Vector);
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

            var score = MathHelper.CosineSimilarity(queryVector, vector.Vector);
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

            var score = MathHelper.CosineSimilarity(queryVector, vector.Vector);
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

    // ==================== 统计与管理操作 ====================

    public Task<List<VectorStats>> GetVectorStatsAsync(CancellationToken cancellationToken = default)
    {
        var stats = _vectors.Values
            .GroupBy(v => v.ModelName)
            .Select(g => new VectorStats
            {
                ModelName = g.Key,
                Dimension = g.First().Dimension,
                VectorCount = g.Count(),
                StorageBytes = g.Sum(v => v.Vector.Length * sizeof(float)),
                ModelExists = true,
                LastUpdated = g.Max(v => v.CreatedAt)
            })
            .ToList();

        return Task.FromResult(stats);
    }

    public async Task<int> DeleteVectorsByModelAsync(string modelName, CancellationToken cancellationToken = default)
    {
        var toRemove = _vectors.Values.Where(v => v.ModelName == modelName).ToList();
        foreach (var v in toRemove)
        {
            _vectors.Remove(v.ChunkId);
        }
        await SaveDataAsync(cancellationToken);
        return toRemove.Count;
    }

    public async Task<int> DeleteOrphanedVectorsAsync(IEnumerable<string> existingModelNames, CancellationToken cancellationToken = default)
    {
        var modelSet = existingModelNames.ToHashSet();
        var toRemove = _vectors.Values
            .Where(v => !string.IsNullOrEmpty(v.ModelName) && !modelSet.Contains(v.ModelName))
            .ToList();
        foreach (var v in toRemove)
        {
            _vectors.Remove(v.ChunkId);
        }
        await SaveDataAsync(cancellationToken);
        return toRemove.Count;
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

    public Task<int> BackfillSourceAsync(IDictionary<string, string> sourceNameToPath, CancellationToken cancellationToken = default)
    {
        var orphaned = _files.Values.Where(f => string.IsNullOrEmpty(f.Source)).ToList();
        var updated = 0;
        foreach (var file in orphaned)
        {
            var normalizedPath = file.Path.Replace('\\', '/');
            var match = sourceNameToPath.FirstOrDefault(kvp =>
                normalizedPath.StartsWith(kvp.Value.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
            if (match.Key != null)
            {
                file.Source = match.Key;
                updated++;
            }
        }
        return Task.FromResult(updated);
    }

    public Task<int> UpdateSourceNameAsync(string oldName, string newName, CancellationToken cancellationToken = default)
    {
        var files = _files.Values.Where(f => f.Source == oldName).ToList();
        foreach (var file in files)
        {
            file.Source = newName;
        }
        return Task.FromResult(files.Count);
    }
}
