namespace ReferenceRAG.Infrastructure.Features.Storage.Bootstrap;

using System.Text.Json;
using System.Text.Json.Nodes;
using ReferenceRAG.Core.Models;

/// <summary>
/// 强类型工厂默认配置生成策略（优先级 2 兜底）
/// 当外部模板文件丢失时，直接从 C# 领域模型在内存中构造合法配置并序列化
/// 保证 100% 自愈能力
/// </summary>
public class FactoryDefaultSeedStrategy : IConfigSeedStrategy
{
    public string Name => "FactoryDefaultSeed";
    public int Priority => 2;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public bool CanSeed(IConfigEnvironmentAdapter adapter) => true;

    public string GenerateConfigJson(IConfigEnvironmentAdapter adapter)
    {
        var defaultConfig = new ObsidianRagConfig
        {
            SchemaVersion = 2,
            DataPath = "data",
            ModelsRootPath = "models"
        };

        var root = new JsonObject
        {
            ["Logging"] = new JsonObject
            {
                ["LogLevel"] = new JsonObject
                {
                    ["Default"] = "Information",
                    ["Microsoft.AspNetCore"] = "Warning",
                    ["ReferenceRAG"] = "Information"
                }
            },
            ["AllowedHosts"] = "*",
            ["SwaggerEnabled"] = true,
            ["ReferenceRAG"] = JsonSerializer.SerializeToNode(defaultConfig, _jsonOptions)
        };

        return root.ToJsonString(_jsonOptions);
    }
}
