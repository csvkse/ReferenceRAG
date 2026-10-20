namespace ReferenceRAG.Infrastructure.Features.Storage.Migrations;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Helpers;
using ReferenceRAG.Core.Services;

/// <summary>
/// 配置文件版本迁移运行器（在加载 appsettings.json 前自动顺序执行所有升级脚本）
/// </summary>
public static class ConfigMigrationRunner
{
    private static readonly IReadOnlyList<IConfigMigration> _migrations =
    [
        new V1ToV2ChatConfigMigration()
    ];

    private static readonly JsonSerializerOptions _writeOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static bool ApplyMigrations(string appSettingsPath)
    {
        if (!File.Exists(appSettingsPath)) return false;

        try
        {
            var json = File.ReadAllText(appSettingsPath);
            var root = JsonNode.Parse(json)?.AsObject();
            if (root == null) return false;

            int currentVersion = 1;
            if (root["ReferenceRAG"] is JsonObject rag && rag["schemaVersion"] != null)
            {
                currentVersion = rag["schemaVersion"]!.GetValue<int>();
            }

            bool anyMigrated = false;
            foreach (var migration in _migrations.OrderBy(m => m.TargetVersion))
            {
                if (currentVersion < migration.TargetVersion)
                {
                    bool migrated = migration.Migrate(root);
                    if (migrated) anyMigrated = true;
                    currentVersion = migration.TargetVersion;
                }
            }

            if (anyMigrated)
            {
                File.WriteAllText(appSettingsPath, root.ToJsonString(_writeOptions));
                StaticLogger.GetLogger("ConfigMigration").LogInformation(
                    "[ConfigMigration] 配置文件 {Path} 已成功迁移至 SchemaVersion {Version}",
                    appSettingsPath, currentVersion);
                return true;
            }
        }
        catch (Exception ex)
        {
            StaticLogger.GetLogger("ConfigMigration").LogError(
                ex, "[ConfigMigration] 执行配置迁移失败: {Path}", appSettingsPath);
        }

        return false;
    }
}
