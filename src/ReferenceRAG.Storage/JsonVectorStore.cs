using System.Text.Json;
using ReferenceRAG.Core.Helpers;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Storage;

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
    private bool _initialized;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public JsonVectorStore(string? dataPath = null)
    {
        _dataPath = dataPath ?? Path.Combine(Environment.CurrentDirectory, "data");
        Directory.CreateDirectory(_dataPath);

        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        // 延迟初始化：首次访问时加载数据，避免构造函数中同步等待
        _initialized = false;
    }

    /// <summary>
    /// 确保数据已加载（线程安全的延迟初始化）
    /// </summary>
    private async Task EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;
            await LoadDataAsync(cancellationToken);
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    // ==================== 文件操作 ====================

    public async Task UpsertFileAsync(FileRecord file, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        _files[file.Id] = file;
        await SaveDataAsync(cancellationToken);
    }

    public async Task<FileRecord?> GetFileAsync(string id, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        _files.TryGetValue(id, out var file);
        return file;
    }

    public async Task<FileRecord?> GetFileByPathAsync(string path, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        var file = _files.Values.FirstOrDefault(f => f.Path == path);
        return file;
    }

    public async Task<FileRecord?> GetFileByHashAsync(string contentHash, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        var file = _files.Values.FirstOrDefault(f => f.ContentHash == contentHash);
        return file;
    }

    public async Task DeleteFileAsync(string id, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        _files.Remove(id);

        var chunkIds = _chunks.Values.Where(c => c.FileId == id).Select(c => c.Id).ToList();
        foreach (var chunkId in chunkIds)
        {
            _chunks.Remove(chunkId);
            _vectors.Remove(chunkId);
        }

        await SaveDataAsync(cancellationToken);
    }

    public async Task<IEnumerable<FileRecord>> GetAllFilesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        return _files.Values.AsEnumerable();
    }

    public async Task<IAsyncEnumerable<FileRecord>> StreamAllFilesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        return _files.Values.ToAsyncEnumerable();
    }

    // ==================== 分段操作 ====================

    public async Task UpsertChunkAsync(ChunkRecord chunk, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        _chunks[chunk.Id] = chunk;
        await SaveDataAsync(cancellationToken);
    }

    public async Task UpsertChunksAsync(IEnumerable<ChunkRecord> chunks, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        foreach (var chunk in chunks)
        {
            _chunks[chunk.Id] = chunk;
        }
        await SaveDataAsync(cancellationToken);
    }

    public async Task<ChunkRecord?> GetChunkAsync(string id, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        _chunks.TryGetValue(id, out var chunk);
        return chunk;
    }

    public async Task<IEnumerable<ChunkRecord>> GetChunksByFileAsync(string fileId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        return _chunks.Values.Where(c => c.FileId == fileId).AsEnumerable();
    }

    public async Task<IEnumerable<ChunkRecord>> GetAdjacentChunksByFileAsync(
        string fileId,
        string chunkId,
        int windowSize,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        if (windowSize < 0) windowSize = 0;

        var orderedChunks = _chunks.Values
            .Where(c => c.FileId == fileId)
            .OrderBy(c => c.ChunkIndex)
            .ToList();

        var hitIndex = orderedChunks.FindIndex(c => c.Id == chunkId);
        if (hitIndex < 0)
        {
            return Array.Empty<ChunkRecord>();
        }

        var startIndex = Math.Max(0, hitIndex - windowSize);
        var endIndex = Math.Min(orderedChunks.Count - 1, hitIndex + windowSize);
        return orderedChunks.Skip(startIndex).Take(endIndex - startIndex + 1).ToList();
    }

    public async Task DeleteChunksByFileAsync(string fileId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
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
        await EnsureInitializedAsync(cancellationToken);
        _chunks.Remove(id);
        _vectors.Remove(id);
        await SaveDataAsync(cancellationToken);
    }

    // ==================== 向量操作 ====================

    public async Task UpsertVectorAsync(VectorRecord vector, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        _vectors[vector.ChunkId] = vector;
        await SaveDataAsync(cancellationToken);
    }

    public async Task UpsertVectorsAsync(IEnumerable<VectorRecord> vectors, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        foreach (var vector in vectors)
        {
            _vectors[vector.ChunkId] = vector;
        }
        await SaveDataAsync(cancellationToken);
    }

    public async Task<VectorRecord?> GetVectorAsync(string id, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        var vector = _vectors.Values.FirstOrDefault(v => v.Id == id);
        return vector;
    }

    public async Task<VectorRecord?> GetVectorByChunkIdAsync(string chunkId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        _vectors.TryGetValue(chunkId, out var vector);
        return vector;
    }

    public async Task<IReadOnlyDictionary<string, VectorRecord>> GetVectorsByChunkIdsAsync(
        IEnumerable<string> chunkIds,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        var result = new Dictionary<string, VectorRecord>();

        foreach (var chunkId in chunkIds.Distinct())
        {
            if (_vectors.TryGetValue(chunkId, out var vector))
            {
                result[chunkId] = vector;
            }
        }

        return result;
    }

    public async Task DeleteVectorAsync(string id, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        var vector = _vectors.Values.FirstOrDefault(v => v.Id == id);
        if (vector != null)
        {
            _vectors.Remove(vector.ChunkId);
        }
        await SaveDataAsync(cancellationToken);
    }

    // ==================== 检索操作 ====================

    public async Task<IEnumerable<SearchResult>> SearchAsync(
        float[] queryVector,
        int topK,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        // JsonVectorStore 不区分模型，使用默认实现
        return await SearchAsync(queryVector, "default", topK, cancellationToken);
    }

    public async Task<IEnumerable<SearchResult>> SearchAsync(
        float[] queryVector,
        string modelName,
        int topK,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
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
        return searchResults.AsEnumerable();
    }

    public async Task<IEnumerable<SearchResult>> SearchByAggregateTypeAsync(
        float[] queryVector,
        AggregateType aggregateType,
        int topK,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
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
        return searchResults.AsEnumerable();
    }

    public async Task<IEnumerable<SearchResult>> SearchInIdsAsync(
        float[] queryVector,
        IEnumerable<string> ids,
        int topK,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
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
        return searchResults.AsEnumerable();
    }

    public async Task DeleteBySourceAsync(string source, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
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
        await EnsureInitializedAsync(cancellationToken);
        foreach (var record in records)
        {
            _vectors[record.ChunkId] = record;
        }
        await SaveDataAsync(cancellationToken);
    }

    // ==================== 统计与管理操作 ====================

    public async Task<List<VectorStats>> GetVectorStatsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
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

        return stats;
    }

    public async Task<int> DeleteVectorsByModelAsync(string modelName, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
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
        await EnsureInitializedAsync(cancellationToken);
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

    private async Task LoadDataAsync(CancellationToken cancellationToken = default)
    {
        var filesPath = Path.Combine(_dataPath, "files.json");
        var chunksPath = Path.Combine(_dataPath, "chunks.json");
        var vectorsPath = Path.Combine(_dataPath, "vectors.json");

        if (File.Exists(filesPath))
        {
            var json = await File.ReadAllTextAsync(filesPath, cancellationToken);
            _files = JsonSerializer.Deserialize<Dictionary<string, FileRecord>>(json, _jsonOptions) ?? new();
        }

        if (File.Exists(chunksPath))
        {
            var json = await File.ReadAllTextAsync(chunksPath, cancellationToken);
            _chunks = JsonSerializer.Deserialize<Dictionary<string, ChunkRecord>>(json, _jsonOptions) ?? new();
        }

        if (File.Exists(vectorsPath))
        {
            var json = await File.ReadAllTextAsync(vectorsPath, cancellationToken);
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
            // 同步保存数据（Dispose 必须是同步的）
            try
            {
                SaveDataAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[JsonVectorStore] Dispose 保存数据失败: {ex.Message}");
            }
            _initLock.Dispose();
            _disposed = true;
        }
    }

}
