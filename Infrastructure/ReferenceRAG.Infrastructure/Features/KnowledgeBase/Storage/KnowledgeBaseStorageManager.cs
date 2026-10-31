using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;

namespace ReferenceRAG.Core.Features.KnowledgeBase.Storage;

/// <summary>
/// 存储租约实现（带原子引用计数与快照版本）
/// </summary>
public sealed class StorageLease : IStorageLease
{
    private readonly SqliteKnowledgeBaseStore _store;
    private readonly Action _onRelease;
    private int _isDisposed;

    public string KnowledgeBaseId => _store.KnowledgeBaseId;
    public long IndexEpochSnapshot { get; }
    public long ReadRevisionSnapshot { get; }
    public IKnowledgeBaseStore Store => _store;

    internal StorageLease(
        SqliteKnowledgeBaseStore store,
        long indexEpochSnapshot,
        long readRevisionSnapshot,
        Action onRelease)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        IndexEpochSnapshot = indexEpochSnapshot;
        ReadRevisionSnapshot = readRevisionSnapshot;
        _onRelease = onRelease ?? throw new ArgumentNullException(nameof(onRelease));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) == 0)
        {
            _onRelease();
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// 知识库存储管理器实现（管理每库 kb.db 实例生命周期与并发隔离）
/// </summary>
public sealed class KnowledgeBaseStorageManager : IKnowledgeBaseStorageManager, IDisposable
{
    private readonly string _dataRoot;
    private readonly ILogger<KnowledgeBaseStorageManager>? _logger;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ConcurrentDictionary<string, StoreEntry> _stores = new();
    private bool _disposed;

    private sealed class StoreEntry
    {
        public required SqliteKnowledgeBaseStore Store { get; init; }
        private int _activeLeases;

        public int ActiveLeases => Volatile.Read(ref _activeLeases);

        public void IncrementLease() => Interlocked.Increment(ref _activeLeases);
        public void DecrementLease() => Interlocked.Decrement(ref _activeLeases);
    }

    public KnowledgeBaseStorageManager(
        string dataRoot,
        ILogger<KnowledgeBaseStorageManager>? logger = null,
        ILoggerFactory? loggerFactory = null)
    {
        _dataRoot = dataRoot ?? throw new ArgumentNullException(nameof(dataRoot));
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    private string GetKbDbPath(string kbId)
    {
        return Path.Combine(_dataRoot, "kbs", kbId, "kb.db");
    }

    public async ValueTask<IStorageLease> AcquireLeaseAsync(
        string knowledgeBaseId,
        StorageAccess access = StorageAccess.ReadOnly,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var entry = _stores.GetOrAdd(knowledgeBaseId, id =>
        {
            var dbPath = GetKbDbPath(id);
            var storeLogger = _loggerFactory?.CreateLogger<SqliteKnowledgeBaseStore>();
            var store = new SqliteKnowledgeBaseStore(id, dbPath, storeLogger);
            return new StoreEntry { Store = store };
        });

        await entry.Store.InitializeAsync(ct);
        var manifest = await entry.Store.GetManifestAsync(ct);

        entry.IncrementLease();
        return new StorageLease(
            entry.Store,
            manifest.IndexEpoch,
            manifest.ReadRevision,
            () => entry.DecrementLease());
    }

    public Task<bool> ExistsAsync(string knowledgeBaseId, CancellationToken ct = default)
    {
        var dbPath = GetKbDbPath(knowledgeBaseId);
        return Task.FromResult(File.Exists(dbPath));
    }

    public async Task DeleteKnowledgeBaseAsync(string knowledgeBaseId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_stores.TryGetValue(knowledgeBaseId, out var entry))
        {
            // 等待在途借用排空
            var spin = 0;
            while (entry.ActiveLeases > 0 && spin < 50)
            {
                await Task.Delay(50, ct);
                spin++;
            }

            if (entry.ActiveLeases > 0)
            {
                throw new InvalidOperationException($"无法删除知识库 {knowledgeBaseId}: 仍有 {entry.ActiveLeases} 个活跃借用未归还");
            }

            if (_stores.TryRemove(knowledgeBaseId, out var removed))
            {
                await removed.Store.CloseAsync(ct);
                removed.Store.Dispose();
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            }
        }

        // 删除磁盘目录
        var kbDir = Path.Combine(_dataRoot, "kbs", knowledgeBaseId);
        if (Directory.Exists(kbDir))
        {
            try
            {
                Directory.Delete(kbDir, recursive: true);
                _logger?.LogInformation("[KnowledgeBaseStorageManager] 物理删除知识库目录: {Dir}", kbDir);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[KnowledgeBaseStorageManager] 删除知识库目录时发生警告: {Dir}", kbDir);
                throw;
            }
        }
    }

    public async Task BackupKnowledgeBaseAsync(string knowledgeBaseId, string backupFilePath, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var sourceDbPath = GetKbDbPath(knowledgeBaseId);
        if (!File.Exists(sourceDbPath))
        {
            throw new FileNotFoundException($"知识库物理文件不存在: {sourceDbPath}");
        }

        var dir = Path.GetDirectoryName(backupFilePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // 使用短只读租约保障备份期间的引用计数保护
        await using var lease = await AcquireLeaseAsync(knowledgeBaseId, StorageAccess.ReadOnly, ct);
        using var sourceConn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={sourceDbPath};Mode=ReadOnly;Pooling=false;");
        using var destConn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={backupFilePath};Pooling=false;");
        await sourceConn.OpenAsync(ct);
        await destConn.OpenAsync(ct);

        // 调用 SQLite 原生在线备份 API (0停机、一致性快照)
        sourceConn.BackupDatabase(destConn);
        _logger?.LogInformation("[KnowledgeBaseStorageManager] 知识库 {KbId} 在线热备份完成: {BackupPath}", knowledgeBaseId, backupFilePath);
    }

    public async Task RestoreKnowledgeBaseAsync(string knowledgeBaseId, string backupFilePath, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!File.Exists(backupFilePath))
        {
            throw new FileNotFoundException($"备份文件不存在: {backupFilePath}");
        }

        var destDbPath = GetKbDbPath(knowledgeBaseId);
        var destDir = Path.GetDirectoryName(destDbPath);
        if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
        {
            Directory.CreateDirectory(destDir);
        }

        // 先排空并清理现有 store
        if (_stores.TryRemove(knowledgeBaseId, out var existing))
        {
            await existing.Store.CloseAsync(ct);
            existing.Store.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }

        using var sourceConn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={backupFilePath};Mode=ReadOnly;Pooling=false;");
        using var destConn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={destDbPath};Pooling=false;");
        await sourceConn.OpenAsync(ct);
        await destConn.OpenAsync(ct);

        sourceConn.BackupDatabase(destConn);
        _logger?.LogInformation("[KnowledgeBaseStorageManager] 知识库 {KbId} 从备份成功恢复: {BackupPath}", knowledgeBaseId, backupFilePath);
    }

    public void Dispose()
    {
        if (_disposed) return;
        foreach (var entry in _stores.Values)
        {
            try
            {
                entry.Store.Dispose();
            }
            catch { }
        }
        _stores.Clear();
        _disposed = true;
    }
}
