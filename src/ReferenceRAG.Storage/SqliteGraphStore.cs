using Microsoft.Data.Sqlite;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using System.Text.Json;

namespace ReferenceRAG.Storage;

public class SqliteGraphStore : IGraphStore, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _lock;
    private readonly bool _ownsResources;
    private bool _disposed;

    /// <summary>生产用：接受共享连接，与其他 Store 共用同一连接和锁。</summary>
    public SqliteGraphStore(SharedSqliteConnection sharedDb)
    {
        _connection = sharedDb.Connection;
        _lock = sharedDb.Lock;
        _ownsResources = false;
        EnsureTables();
    }

    /// <summary>兼容构造函数（测试 / 独立使用）。</summary>
    public SqliteGraphStore(string dbPath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        };
        _connection = new SqliteConnection(builder.ToString());
        _connection.Open();
        _lock = new SemaphoreSlim(1, 1);
        _ownsResources = true;
        EnsureTables();
    }

    private void EnsureTables()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS graph_nodes (
                id       TEXT PRIMARY KEY,
                title    TEXT NOT NULL DEFAULT '',
                type     TEXT NOT NULL DEFAULT 'document',
                chunk_ids TEXT NOT NULL DEFAULT '[]',
                metadata  TEXT NOT NULL DEFAULT '{}'
            );
            CREATE TABLE IF NOT EXISTS graph_edges (
                from_id     TEXT NOT NULL,
                to_id       TEXT NOT NULL,
                type        TEXT NOT NULL DEFAULT 'wikilink',
                line_number INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (from_id, to_id, type)
            );
            CREATE INDEX IF NOT EXISTS idx_edges_from ON graph_edges(from_id);
            CREATE INDEX IF NOT EXISTS idx_edges_to   ON graph_edges(to_id);
            """;
        cmd.ExecuteNonQuery();
    }

    public async Task UpsertNodeAsync(GraphNode node, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO graph_nodes (id, title, type, chunk_ids, metadata)
                VALUES (@id, @title, @type, @chunkIds, @metadata)
                ON CONFLICT(id) DO UPDATE SET
                    title     = excluded.title,
                    type      = excluded.type,
                    chunk_ids = excluded.chunk_ids,
                    metadata  = excluded.metadata
                """;
            cmd.Parameters.AddWithValue("@id", node.Id);
            cmd.Parameters.AddWithValue("@title", node.Title);
            cmd.Parameters.AddWithValue("@type", node.Type);
            cmd.Parameters.AddWithValue("@chunkIds", JsonSerializer.Serialize(node.ChunkIds));
            cmd.Parameters.AddWithValue("@metadata", JsonSerializer.Serialize(node.Metadata));
            cmd.ExecuteNonQuery();
        }
        finally { _lock.Release(); }
    }

    public async Task UpsertEdgesAsync(IEnumerable<GraphEdge> edges, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var tx = _connection.BeginTransaction();
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO graph_edges (from_id, to_id, type, line_number)
                VALUES (@from, @to, @type, @line)
                ON CONFLICT(from_id, to_id, type) DO UPDATE SET line_number = excluded.line_number
                """;
            var pFrom = cmd.Parameters.Add("@from", SqliteType.Text);
            var pTo   = cmd.Parameters.Add("@to",   SqliteType.Text);
            var pType = cmd.Parameters.Add("@type", SqliteType.Text);
            var pLine = cmd.Parameters.Add("@line", SqliteType.Integer);

            foreach (var e in edges)
            {
                pFrom.Value = e.FromId;
                pTo.Value   = e.ToId;
                pType.Value = e.Type;
                pLine.Value = e.LineNumber;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        finally { _lock.Release(); }
    }

    public async Task DeleteNodeAsync(string nodeId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                DELETE FROM graph_nodes WHERE id = @id;
                DELETE FROM graph_edges WHERE from_id = @id OR to_id = @id;
                """;
            cmd.Parameters.AddWithValue("@id", nodeId);
            cmd.ExecuteNonQuery();
        }
        finally { _lock.Release(); }
    }

    public async Task<GraphNode?> GetNodeAsync(string nodeId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT id, title, type, chunk_ids, metadata FROM graph_nodes WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", nodeId);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadNode(reader) : null;
        }
        finally { _lock.Release(); }
    }

    public async Task<GraphTraversalResult> GetNeighborsAsync(
        string nodeId, int depth = 1, string[]? edgeTypes = null, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            depth = Math.Clamp(depth, 1, 3);
            var result = new GraphTraversalResult { RootId = nodeId, Depth = depth };
            var visited = new HashSet<string>();
            var queue = new Queue<(string id, int remainingDepth)>();
            queue.Enqueue((nodeId, depth));

            while (queue.Count > 0)
            {
                var (current, remaining) = queue.Dequeue();
                if (!visited.Add(current)) continue;

                var node = GetNodeInternal(current);
                if (node != null) result.Nodes.Add(node);

                if (remaining <= 0) continue;

                var edges = GetEdgesInternal(current, edgeTypes);
                foreach (var e in edges)
                {
                    result.Edges.Add(e);
                    var neighbor = e.FromId == current ? e.ToId : e.FromId;
                    if (!visited.Contains(neighbor))
                        queue.Enqueue((neighbor, remaining - 1));
                }
            }
            return result;
        }
        finally { _lock.Release(); }
    }

    public async Task<List<GraphNode>> SearchNodesAsync(string query, int limit = 10, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var nodes = new List<GraphNode>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT id, title, type, chunk_ids, metadata FROM graph_nodes
                WHERE title LIKE @q
                LIMIT @limit
                """;
            cmd.Parameters.AddWithValue("@q", $"%{query}%");
            cmd.Parameters.AddWithValue("@limit", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read()) nodes.Add(ReadNode(r));
            return nodes;
        }
        finally { _lock.Release(); }
    }

    public async Task<GraphStats> GetStatsAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT
                  (SELECT COUNT(*) FROM graph_nodes) AS node_count,
                  (SELECT COUNT(*) FROM graph_edges) AS edge_count
                """;
            using var r = cmd.ExecuteReader();
            r.Read();
            return new GraphStats { NodeCount = r.GetInt32(0), EdgeCount = r.GetInt32(1) };
        }
        finally { _lock.Release(); }
    }

    // 内部方法（调用方已持锁）
    private GraphNode? GetNodeInternal(string nodeId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id, title, type, chunk_ids, metadata FROM graph_nodes WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", nodeId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadNode(r) : null;
    }

    private List<GraphEdge> GetEdgesInternal(string nodeId, string[]? edgeTypes)
    {
        var edges = new List<GraphEdge>();
        var typeFilter = edgeTypes?.Length > 0
            ? $"AND type IN ({string.Join(",", edgeTypes.Select((_, i) => $"@t{i}"))})"
            : "";

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT from_id, to_id, type, line_number FROM graph_edges
            WHERE (from_id = @id OR to_id = @id) {typeFilter}
            """;
        cmd.Parameters.AddWithValue("@id", nodeId);
        if (edgeTypes != null)
            for (int i = 0; i < edgeTypes.Length; i++)
                cmd.Parameters.AddWithValue($"@t{i}", edgeTypes[i]);

        using var r = cmd.ExecuteReader();
        while (r.Read())
            edges.Add(new GraphEdge
            {
                FromId = r.GetString(0),
                ToId   = r.GetString(1),
                Type   = r.GetString(2),
                LineNumber = r.GetInt32(3)
            });
        return edges;
    }

    private static GraphNode ReadNode(SqliteDataReader r) => new()
    {
        Id       = r.GetString(0),
        Title    = r.GetString(1),
        Type     = r.GetString(2),
        ChunkIds = JsonSerializer.Deserialize<List<string>>(r.GetString(3)) ?? new(),
        Metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(4)) ?? new()
    };

    public void Dispose()
    {
        if (_disposed) return;
        if (_ownsResources)
        {
            _lock.Dispose();
            _connection.Dispose();
        }
        _disposed = true;
    }
}
