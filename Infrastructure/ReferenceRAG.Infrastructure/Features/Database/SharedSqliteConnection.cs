using Microsoft.Data.Sqlite;

namespace ReferenceRAG.Storage;

/// <summary>
/// 共享 SQLite 连接 + 锁 — 让同一 DB 文件的多个 Store 共用一个连接和一把顺序锁。
///
/// 设计原理：
///   SqliteConnection 不是线程安全的，不能被多个线程同时使用。
///   将连接和锁集中到此类，确保：
///     1. 只有一个物理连接打开，消除 WAL 多连接序列化开销
///     2. 所有 DB 操作通过同一把 SemaphoreSlim(1,1) 串行化
///     3. SqliteVectorStore / Fts5BM25Store / SqliteGraphStore 共用此实例
/// </summary>
public sealed class SharedSqliteConnection : IDisposable
{
    public SqliteConnection Connection { get; }

    // 全局顺序锁：所有 Store 的读写操作都通过此锁串行化
    public SemaphoreSlim Lock { get; } = new(1, 1);

    private bool _disposed;

    public SharedSqliteConnection(string dbPath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        };
        Connection = new SqliteConnection(builder.ConnectionString);
        Connection.Open();
    }

    public void Dispose()
    {
        if (_disposed) return;
        Lock.Dispose();
        Connection.Close();
        Connection.Dispose();
        _disposed = true;
    }
}
