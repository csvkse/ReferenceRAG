using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Business.Features.Indexing.Adapters;

/// <summary>
/// SQLite 索引存储适配器实现：
/// 1. 外键防护机制（FK-Safe）：写入 chunks 前确保父 files 记录存在，防止 SQLite Error 19
/// 2. 路径统一归一化：解决 Windows/WSL 正反斜杠与大小写差异导致的误删
/// 3. 四重防御安全清理：绝不删除正在被当前任务索引的文件或物理存在的文件
/// </summary>
public class SqliteIndexStorageAdapter : IIndexStorageAdapter
{
    private readonly IVectorStore _vectorStore;
    private readonly IFileIndexPipeline _pipeline;
    private readonly IFileProcessingGuard _guard;
    private readonly ILogger<SqliteIndexStorageAdapter> _logger;

    public SqliteIndexStorageAdapter(
        IVectorStore vectorStore,
        IFileIndexPipeline pipeline,
        IFileProcessingGuard guard,
        ILogger<SqliteIndexStorageAdapter>? logger = null)
    {
        _vectorStore = vectorStore ?? throw new ArgumentNullException(nameof(vectorStore));
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SqliteIndexStorageAdapter>.Instance;
    }

    /// <inheritdoc/>
    public string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try
        {
            var fullPath = Path.GetFullPath(path);
            return fullPath.Replace('/', '\\').TrimEnd('\\');
        }
        catch
        {
            return path.Replace('/', '\\').TrimEnd('\\');
        }
    }

    /// <inheritdoc/>
    public async Task<bool> EnsureFileRecordExistsAsync(FileRecord fileRecord, CancellationToken ct = default)
    {
        if (fileRecord == null) return false;

        var existing = await _vectorStore.GetFileAsync(fileRecord.Id, ct);
        if (existing == null)
        {
            await _vectorStore.UpsertFileAsync(fileRecord, ct);
            return true;
        }

        return false;
    }

    /// <inheritdoc/>
    public async Task SafeUpsertChunksAsync(IEnumerable<ChunkRecord> chunks, CancellationToken ct = default)
    {
        var chunkList = chunks as List<ChunkRecord> ?? chunks.ToList();
        if (chunkList.Count == 0) return;

        // 1. 按 FileId 分组校验外键完整性
        var chunksByFile = chunkList.GroupBy(c => c.FileId).ToList();

        foreach (var group in chunksByFile)
        {
            var fileId = group.Key;
            if (string.IsNullOrEmpty(fileId))
            {
                _logger.LogWarning("检测到分块缺失 FileId，自动跳过异常分块");
                continue;
            }

            var fileRecord = await _vectorStore.GetFileAsync(fileId, ct);
            if (fileRecord == null)
            {
                // 关键防御：若 files 表中无该记录（可能因重入、清理竞争或事务中断缺失），
                // 自动补建占位文件记录，彻底避免 SQLite Error 19 'FOREIGN KEY constraint failed'
                var firstChunk = group.First();
                _logger.LogWarning("分块所属父文件记录不存在(file_id={FileId})，触发自愈防护机制", fileId);

                var fallback = new FileRecord
                {
                    Id = fileId,
                    Path = !string.IsNullOrEmpty(firstChunk.Source) ? firstChunk.Source : $"recovered_{fileId}",
                    FileName = Path.GetFileName(firstChunk.Source ?? "") ?? $"file_{fileId}",
                    Source = firstChunk.Source ?? "unknown",
                    IndexedStatus = "pending",
                    IndexedAt = DateTime.UtcNow,
                    ChunkCount = group.Count()
                };

                await _vectorStore.UpsertFileAsync(fallback, ct);
            }
        }

        // 2. 写入分块
        await _vectorStore.UpsertChunksAsync(chunkList, ct);
    }

    /// <inheritdoc/>
    public async Task SafeUpsertVectorsAsync(IEnumerable<VectorRecord> vectors, CancellationToken ct = default)
    {
        var vectorList = vectors as List<VectorRecord> ?? vectors.ToList();
        if (vectorList.Count == 0) return;

        await _vectorStore.UpsertVectorsAsync(vectorList, ct);
    }

    /// <inheritdoc/>
    public async Task<int> CleanDeletedFilesSafeAsync(
        IEnumerable<string> sourceNames,
        ISet<string> diskFilePaths,
        ISet<string> activeFilePaths,
        CancellationToken ct = default)
    {
        var sourceNameSet = new HashSet<string>(sourceNames, StringComparer.OrdinalIgnoreCase);
        var dbFiles = (await _vectorStore.GetAllFilesAsync(ct))
            .Where(f => f.Source != null && sourceNameSet.Contains(f.Source))
            .ToList();

        // 规范化活跃文件集合与磁盘文件集合用于比较
        var normalizedDiskPaths = diskFilePaths.Select(NormalizePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var normalizedActivePaths = activeFilePaths.Select(NormalizePath).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var cleanedCount = 0;

        foreach (var dbFile in dbFiles)
        {
            if (ct.IsCancellationRequested) break;
            var normalizedDbPath = NormalizePath(dbFile.Path);

            // 防御 1：绝不删除当前批次正在处理的文件
            if (normalizedActivePaths.Contains(normalizedDbPath) || activeFilePaths.Contains(dbFile.Path))
            {
                continue;
            }

            // 防御 2：磁盘扫描列表中包含该路径，则非删除
            if (normalizedDiskPaths.Contains(normalizedDbPath) || diskFilePaths.Contains(dbFile.Path))
            {
                continue;
            }

            // 防御 3：二次真实文件系统核实，若物理文件依然存在，绝对不删（防止通配符/过滤差异导致的误删）
            if (File.Exists(dbFile.Path) || File.Exists(normalizedDbPath))
            {
                continue;
            }

            // 防御 4：跨服务互斥锁保护
            if (!_guard.TryAcquire(dbFile.Path))
            {
                continue;
            }

            try
            {
                await _pipeline.DeleteFileAsync(dbFile.Id, dbFile.Path, ct);
                cleanedCount++;
                _logger.LogInformation("安全清理已删除文件索引: {Path} (Id: {Id})", dbFile.Path, dbFile.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "清理文件索引失败: {Path}", dbFile.Path);
            }
            finally
            {
                _guard.Release(dbFile.Path);
            }
        }

        return cleanedCount;
    }
}
