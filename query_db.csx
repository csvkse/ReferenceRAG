#!/usr/bin/env dotnet-script
#r "nuget: Microsoft.Data.Sqlite, 10.0.5"

using Microsoft.Data.Sqlite;

var dbPath = args.Length > 0 ? args[0] : @"E:\LinuxWork\Obsidian\resource\data\vectors.db";
using var connection = new SqliteConnection($"Data Source={dbPath}");
connection.Open();

Console.WriteLine("=== 1. 所有表 ===");
using (var cmd = connection.CreateCommand())
{
    cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
        Console.WriteLine($"  {reader.GetString(0)}");
}

Console.WriteLine("\n=== 2. models 表 ===");
using (var cmd = connection.CreateCommand())
{
    cmd.CommandText = "SELECT * FROM models";
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
        Console.WriteLine($"  Name: {reader.GetString(0)}, Dimension: {reader.GetInt32(1)}");
    }
}

Console.WriteLine("\n=== 3. files 表统计 ===");
using (var cmd = connection.CreateCommand())
{
    cmd.CommandText = "SELECT COUNT(*) as file_count, SUM(chunk_count) as total_chunks FROM files";
    using var reader = cmd.ExecuteReader();
    if (reader.Read())
    {
        Console.WriteLine($"  文件数: {reader.GetInt32(0)}");
        Console.WriteLine($"  分块数(chunk_count累加): {reader.GetInt32(1)}");
    }
}

Console.WriteLine("\n=== 4. chunks 表实际数量 ===");
using (var cmd = connection.CreateCommand())
{
    cmd.CommandText = "SELECT COUNT(*) FROM chunks";
    var count = (long)cmd.ExecuteScalar();
    Console.WriteLine($"  chunks 表记录数: {count}");
}

Console.WriteLine("\n=== 5. 向量表统计 ===");
// 先获取所有 vec_ 表
var vectorTables = new List<string>();
using (var cmd = connection.CreateCommand())
{
    cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name LIKE 'vec_%' AND name NOT LIKE '%_rowids' AND name NOT LIKE '%_config'";
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
        vectorTables.Add(reader.GetString(0));
}

foreach (var table in vectorTables)
{
    using var cmd = connection.CreateCommand();
    cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
    var count = (long)cmd.ExecuteScalar();
    Console.WriteLine($"  {table}: {count}");
}

Console.WriteLine("\n=== 6. 孤立向量检查 ===");
foreach (var table in vectorTables)
{
    var rowidsTable = $"{table}_rowids";
    try
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {rowidsTable} r WHERE NOT EXISTS (SELECT 1 FROM chunks c WHERE c.id = r.id)";
        var count = (long)cmd.ExecuteScalar();
        Console.WriteLine($"  {table}: {count} 个孤立向量");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  {table}: 查询失败 - {ex.Message}");
    }
}
