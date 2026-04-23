using Microsoft.Data.Sqlite;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using System.Text.Json;

namespace ReferenceRAG.Storage;

public class SqliteGraphStore : IGraphStore, IDisposable
{
    private readonly SqliteConnection _connection;
    private bool _disposed;

    public SqliteGraphStore(string dbPath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        };
        _connection = new SqliteConnection(builder.ToString());
        _connection.Open();
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

    public Task UpsertNodeAsync(GraphNode node, CancellationToken ct = default)
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
        return Task.CompletedTask;
    }

    public Task UpsertEdgesAsync(IEnumerable<GraphEdge> edges, CancellationToken ct = default)
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
        var pTo   = cmd.Parameters.Add("@to", SqliteType.Text);
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
        return Task.CompletedTask;
    }

    public Task DeleteNodeAsync(string nodeId, CancellationToken ct = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            DELETE FROM graph_nodes WHERE id = @id;
            DELETE FROM graph_edges WHERE from_id = @id OR to_id = @id;
            """;
        cmd.Parameters.AddWithValue("@id", nodeId);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task<GraphNode?> GetNodeAsync(string nodeId, CancellationToken ct = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id, title, type, chunk_ids, metadata FROM graph_nodes WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", nodeId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return Task.FromResult<GraphNode?>(null);
        return Task.FromResult<GraphNode?>(ReadNode(reader));
    }

    public Task<GraphTraversalResult> GetNeighborsAsync(
        string nodeId, int depth = 1, string[]? edgeTypes = null, CancellationToken ct = default)
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

            var node = GetNodeSync(current);
            if (node != null) result.Nodes.Add(node);

            if (remaining <= 0) continue;

            var edges = GetEdgesSync(current, edgeTypes);
            foreach (var e in edges)
            {
                result.Edges.Add(e);
                var neighbor = e.FromId == current ? e.ToId : e.FromId;
                if (!visited.Contains(neighbor))
                    queue.Enqueue((neighbor, remaining - 1));
            }
        }

        return Task.FromResult(result);
    }

    private GraphNode? GetNodeSync(string nodeId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id, title, type, chunk_ids, metadata FROM graph_nodes WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", nodeId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadNode(r) : null;
    }

    private List<GraphEdge> GetEdgesSync(string nodeId, string[]? edgeTypes)
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
                ToId = r.GetString(1),
                Type = r.GetString(2),
                LineNumber = r.GetInt32(3)
            });
        return edges;
    }

    public Task<List<GraphNode>> SearchNodesAsync(string query, int limit = 10, CancellationToken ct = default)
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
        return Task.FromResult(nodes);
    }

    public Task<GraphStats> GetStatsAsync(CancellationToken ct = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT
              (SELECT COUNT(*) FROM graph_nodes) AS node_count,
              (SELECT COUNT(*) FROM graph_edges) AS edge_count
            """;
        using var r = cmd.ExecuteReader();
        r.Read();
        return Task.FromResult(new GraphStats
        {
            NodeCount = r.GetInt32(0),
            EdgeCount = r.GetInt32(1)
        });
    }

    private static GraphNode ReadNode(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        Title = r.GetString(1),
        Type = r.GetString(2),
        ChunkIds = JsonSerializer.Deserialize<List<string>>(r.GetString(3)) ?? new(),
        Metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(4)) ?? new()
    };

    public void Dispose()
    {
        if (!_disposed)
        {
            _connection.Dispose();
            _disposed = true;
        }
    }
}
