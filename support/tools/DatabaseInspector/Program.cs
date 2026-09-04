using Microsoft.Data.Sqlite;

if (args.Length != 1)
{
    Console.Error.WriteLine("用法: DatabaseInspector <vectors.db 路径>");
    return 1;
}

var dbPath = Path.GetFullPath(args[0]);
if (!File.Exists(dbPath))
{
    Console.Error.WriteLine($"数据库不存在: {dbPath}");
    return 2;
}

using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
connection.Open();

Console.WriteLine("=== 所有表 ===");
using (var command = connection.CreateCommand())
{
    command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
    using var reader = command.ExecuteReader();
    while (reader.Read())
        Console.WriteLine($"  {reader.GetString(0)}");
}

Console.WriteLine("\n=== 模型 ===");
using (var command = connection.CreateCommand())
{
    command.CommandText = "SELECT name, dimension FROM models";
    using var reader = command.ExecuteReader();
    while (reader.Read())
        Console.WriteLine($"  {reader.GetString(0)}: dimension={reader.GetInt32(1)}");
}

Console.WriteLine("\n=== 文件与分块 ===");
using (var command = connection.CreateCommand())
{
    command.CommandText = "SELECT COUNT(*), COALESCE(SUM(chunk_count), 0) FROM files";
    using var reader = command.ExecuteReader();
    if (reader.Read())
        Console.WriteLine($"  文件数: {reader.GetInt64(0)}, 文件记录分块数: {reader.GetInt64(1)}");
}

using (var command = connection.CreateCommand())
{
    command.CommandText = "SELECT COUNT(*) FROM chunks";
    Console.WriteLine($"  chunks 实际记录数: {(long)command.ExecuteScalar()!}");
}

Console.WriteLine("\n=== 向量与孤立记录 ===");
var vectorTables = new List<string>();
using (var command = connection.CreateCommand())
{
    command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name LIKE 'vec_%' AND name NOT LIKE '%_rowids' AND name NOT LIKE '%_config'";
    using var reader = command.ExecuteReader();
    while (reader.Read())
        vectorTables.Add(reader.GetString(0));
}

foreach (var table in vectorTables)
{
    var rowIdsTable = $"{table}_rowids";
    using var countCommand = connection.CreateCommand();
    countCommand.CommandText = $"SELECT COUNT(*) FROM \"{rowIdsTable}\"";

    using var orphanCommand = connection.CreateCommand();
    orphanCommand.CommandText = $"SELECT COUNT(*) FROM \"{rowIdsTable}\" r WHERE NOT EXISTS (SELECT 1 FROM chunks c WHERE c.id = r.id)";

    Console.WriteLine($"  {table}: {(long)countCommand.ExecuteScalar()!} 个向量，{(long)orphanCommand.ExecuteScalar()!} 个孤立向量");
}

return 0;
