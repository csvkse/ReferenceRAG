using Microsoft.Data.Sqlite;

var dbPath = @"E:\LinuxWork\Obsidian\resource\data\vectors.db";
using var connection = new SqliteConnection($"Data Source={dbPath}");
connection.Open();

Console.WriteLine("=== 1. models 表 ===");
using (var cmd = connection.CreateCommand())
{
    cmd.CommandText = "SELECT * FROM models";
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
        Console.WriteLine($"  {reader.GetString(0)}: dimension={reader.GetInt32(1)}");
}

Console.WriteLine("\n=== 2. files 统计 ===");
using (var cmd = connection.CreateCommand())
{
    cmd.CommandText = "SELECT COUNT(*), SUM(chunk_count) FROM files";
    using var reader = cmd.ExecuteReader();
    if (reader.Read())
        Console.WriteLine($"  文件数: {reader.GetInt32(0)}, 分块数(SUM): {reader.GetInt32(1)}");
}

Console.WriteLine("\n=== 3. chunks 表 ===");
using (var cmd = connection.CreateCommand())
{
    cmd.CommandText = "SELECT COUNT(*) FROM chunks";
    Console.WriteLine($"  chunks 记录数: {(long)cmd.ExecuteScalar()}");
}

Console.WriteLine("\n=== 4. 向量表 ===");
var tables = new List<string>();
using (var cmd = connection.CreateCommand())
{
    cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name LIKE 'vec_%' AND name NOT LIKE '%_rowids' AND name NOT LIKE '%_config'";
    using var reader = cmd.ExecuteReader();
    while (reader.Read()) tables.Add(reader.GetString(0));
}
foreach (var t in tables)
{
    // 使用 rowids 表统计（避免 vec0 模块）
    try
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {t}_rowids";
        Console.WriteLine($"  {t}: {(long)cmd.ExecuteScalar()}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  {t}: 错误 - {ex.Message}");
    }
}

Console.WriteLine("\n=== 5. 孤立向量(向量存在但分块不存在) ===");
foreach (var t in tables)
{
    try
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {t}_rowids r WHERE NOT EXISTS (SELECT 1 FROM chunks c WHERE c.id = r.id)";
        Console.WriteLine($"  {t}: {(long)cmd.ExecuteScalar()} 个孤立向量");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  {t}: 错误 - {ex.Message}");
    }
}
