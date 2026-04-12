using Microsoft.Data.Sqlite;
using System.Text;

namespace ReferenceRAG.Core.Services;

/// <summary>
/// 查询统计服务 - 持久化记录每次查询的耗时和结果数
/// </summary>
public class QueryStatsService : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly string _dbPath;
    private bool _disposed;

    public QueryStatsService(string dbPath)
    {
        _dbPath = dbPath;
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        };
        _connection = new SqliteConnection(builder.ConnectionString);
        _connection.Open();
        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        var sql = @"
            CREATE TABLE IF NOT EXISTS query_stats (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                query_text TEXT NOT NULL,
                query_hash TEXT NOT NULL,
                duration_ms INTEGER NOT NULL,
                result_count INTEGER NOT NULL,
                sources TEXT,
                mode TEXT,
                created_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_query_stats_created ON query_stats(created_at);
            CREATE INDEX IF NOT EXISTS idx_query_stats_hash ON query_stats(query_hash);
        ";
        using var command = new SqliteCommand(sql, _connection);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 记录一次查询
    /// </summary>
    public async Task RecordQueryAsync(string query, long durationMs, int resultCount,
        List<string>? sources = null, string? mode = null)
    {
        var sql = @"
            INSERT INTO query_stats (query_text, query_hash, duration_ms, result_count, sources, mode, created_at)
            VALUES (@queryText, @queryHash, @durationMs, @resultCount, @sources, @mode, @createdAt)
        ";

        using var command = new SqliteCommand(sql, _connection);
        command.Parameters.AddWithValue("@queryText", query);
        command.Parameters.AddWithValue("@queryHash", ComputeHash(query));
        command.Parameters.AddWithValue("@durationMs", durationMs);
        command.Parameters.AddWithValue("@resultCount", resultCount);
        command.Parameters.AddWithValue("@sources", sources != null ? string.Join(",", sources) : "");
        command.Parameters.AddWithValue("@mode", mode ?? "");
        command.Parameters.AddWithValue("@createdAt", DateTime.UtcNow.ToString("O"));

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 获取平均查询时间（毫秒）
    /// </summary>
    public async Task<double> GetAverageQueryTimeAsync(int lastNDays = 7)
    {
        var cutoff = DateTime.UtcNow.AddDays(-lastNDays).ToString("O");
        var sql = @"
            SELECT AVG(duration_ms) FROM query_stats
            WHERE created_at >= @cutoff
        ";

        using var command = new SqliteCommand(sql, _connection);
        command.Parameters.AddWithValue("@cutoff", cutoff);
        var result = await command.ExecuteScalarAsync();
        return result == DBNull.Value ? 0 : Convert.ToDouble(result);
    }

    /// <summary>
    /// 获取查询统计摘要
    /// </summary>
    public async Task<QueryStatsSummary> GetSummaryAsync(int lastNDays = 7)
    {
        var cutoff = DateTime.UtcNow.AddDays(-lastNDays).ToString("O");
        var sql = @"
            SELECT
                COUNT(*) as total_queries,
                AVG(duration_ms) as avg_duration,
                MAX(duration_ms) as max_duration,
                MIN(duration_ms) as min_duration,
                AVG(result_count) as avg_results
            FROM query_stats
            WHERE created_at >= @cutoff
        ";

        using var command = new SqliteCommand(sql, _connection);
        command.Parameters.AddWithValue("@cutoff", cutoff);
        using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return new QueryStatsSummary
            {
                TotalQueries = reader.IsDBNull(0) ? 0 : reader.GetInt64(0),
                AvgDurationMs = reader.IsDBNull(1) ? 0 : reader.GetDouble(1),
                MaxDurationMs = reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
                MinDurationMs = reader.IsDBNull(3) ? 0 : reader.GetInt64(3),
                AvgResultCount = reader.IsDBNull(4) ? 0 : reader.GetDouble(4)
            };
        }
        return new QueryStatsSummary();
    }

    /// <summary>
    /// 获取最近 N 条查询记录
    /// </summary>
    public async Task<List<QueryStatRecord>> GetRecentQueriesAsync(int limit = 100)
    {
        var sql = @"
            SELECT id, query_text, duration_ms, result_count, sources, mode, created_at
            FROM query_stats
            ORDER BY created_at DESC
            LIMIT @limit
        ";

        var records = new List<QueryStatRecord>();
        using var command = new SqliteCommand(sql, _connection);
        command.Parameters.AddWithValue("@limit", limit);
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            records.Add(new QueryStatRecord
            {
                Id = reader.GetInt64(0),
                QueryText = reader.GetString(1),
                DurationMs = reader.GetInt64(2),
                ResultCount = reader.GetInt32(3),
                Sources = reader.GetString(4),
                Mode = reader.GetString(5),
                CreatedAt = DateTime.Parse(reader.GetString(6))
            });
        }
        return records;
    }

    private static string ComputeHash(string text)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes)[..16];
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _connection.Close();
            _connection.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// 查询统计摘要
/// </summary>
public class QueryStatsSummary
{
    public long TotalQueries { get; set; }
    public double AvgDurationMs { get; set; }
    public long MaxDurationMs { get; set; }
    public long MinDurationMs { get; set; }
    public double AvgResultCount { get; set; }
}

/// <summary>
/// 单条查询记录
/// </summary>
public class QueryStatRecord
{
    public long Id { get; set; }
    public string QueryText { get; set; } = "";
    public long DurationMs { get; set; }
    public int ResultCount { get; set; }
    public string Sources { get; set; } = "";
    public string Mode { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}