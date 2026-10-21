namespace ReferenceRAG.Infrastructure.Features.Storage.Bootstrap;

using System;
using System.IO;

/// <summary>
/// 配置文件物理路径解析器
/// 默认统一使用：程序所在目录/data/appsettings.json
/// </summary>
public static class ConfigPathResolver
{
    public const string DefaultConfigFileName = "appsettings.json";
    public const string ConfigPathEnvVar = "REFERENCERAG_CONFIG_PATH";

    /// <summary>
    /// 解析配置文件绝对物理路径
    /// 默认直接使用：{DataPath}/appsettings.json（即 程序所在目录/data/appsettings.json）
    /// 支持通过 REFERENCERAG_CONFIG_PATH 显式覆盖，支持通过 REFERENCERAG_DATA_PATH / DataPath 自定义 data 目录
    /// </summary>
    public static string ResolveConfigPath(string? baseDirectory = null)
    {
        var explicitConfigPath = Environment.GetEnvironmentVariable(ConfigPathEnvVar);
        if (!string.IsNullOrWhiteSpace(explicitConfigPath))
        {
            return Path.GetFullPath(explicitConfigPath);
        }

        var resolvedBaseDir = string.IsNullOrWhiteSpace(baseDirectory)
            ? (Environment.GetEnvironmentVariable("REFERENCERAG_CONTENT_ROOT") ?? Directory.GetCurrentDirectory())
            : Path.GetFullPath(baseDirectory);

        var dataDir = Environment.GetEnvironmentVariable("REFERENCERAG_DATA_PATH")
                      ?? Environment.GetEnvironmentVariable("DataPath")
                      ?? Environment.GetEnvironmentVariable("OBSIDIAN_RAG_DATA_PATH")
                      ?? Path.Combine(resolvedBaseDir, "data");

        var fullDataDir = Path.IsPathRooted(dataDir)
            ? Path.GetFullPath(dataDir)
            : Path.GetFullPath(Path.Combine(resolvedBaseDir, dataDir));

        return Path.Combine(fullDataDir, DefaultConfigFileName);
    }
}
