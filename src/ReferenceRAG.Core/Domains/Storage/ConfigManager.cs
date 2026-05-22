using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;

using ReferenceRAG.Core.Helpers;
using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Core.Services;

/// <summary>
/// 配置管理器 - 统一从 appsettings.json 读取和保存 ReferenceRAG 配置
/// </summary>
public class ConfigManager
{
    private ObsidianRagConfig? _config;
    private static readonly ILogger _logger = StaticLogger.GetLogger("ConfigManager");

    private static readonly JsonSerializerOptions _readOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private static readonly JsonSerializerOptions _writeOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static string GetAppSettingsPath()
    {
        return Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json");
    }

    /// <summary>
    /// 加载配置 - 仅从 appsettings.json 的 ReferenceRAG 节读取
    /// </summary>
    public ObsidianRagConfig Load()
    {
        if (_config != null) return _config;

        var appSettingsPath = GetAppSettingsPath();
        if (File.Exists(appSettingsPath))
        {
            try
            {
                var json = File.ReadAllText(appSettingsPath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("ReferenceRAG", out var ragConfig))
                {
                    _config = ragConfig.Deserialize<ObsidianRagConfig>(_readOptions);
                    if (_config != null)
                    {
                        _logger.LogInformation("[ConfigManager] 已从 appsettings.json 加载配置");
                        ApplyEnvironmentVariables(_config);
                        return _config;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[ConfigManager] 配置文件解析失败: {ex.Message}");
            }
        }

        _config ??= new ObsidianRagConfig();
        ApplyEnvironmentVariables(_config);

        return _config;
    }

    /// <summary>
    /// 保存配置 - 写回 appsettings.json 的 ReferenceRAG 节
    /// </summary>
    public void Save(ObsidianRagConfig config)
    {
        var appSettingsPath = GetAppSettingsPath();

        try
        {
            JsonObject root;
            if (File.Exists(appSettingsPath))
            {
                var json = File.ReadAllText(appSettingsPath);
                root = JsonNode.Parse(json)?.AsObject() ?? new JsonObject();
            }
            else
            {
                root = new JsonObject();
            }

            root["ReferenceRAG"] = JsonSerializer.SerializeToNode(config, _writeOptions) ?? new JsonObject();
            File.WriteAllText(appSettingsPath, root.ToJsonString(_writeOptions));

            _config = config;
            _logger.LogInformation($"[ConfigManager] 配置已保存到: {appSettingsPath}");
        }
        catch (Exception ex)
        {
            _logger.LogError($"[ConfigManager] 保存配置失败: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// 添加源文件夹
    /// </summary>
    public bool AddSource(SourceFolder source)
    {
        var config = Load();

        if (config.Sources.Any(s => s.Path.Equals(source.Path, StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogInformation($"[ConfigManager] 源路径已存在: {source.Path}");
            return false;
        }

        if (string.IsNullOrEmpty(source.Name))
        {
            source.Name = Path.GetFileName(source.Path) ?? $"Source{config.Sources.Count + 1}";
        }

        config.Sources.Add(source);
        Save(config);

        _logger.LogInformation($"[ConfigManager] 已添加源: {source.Name} ({source.Path})");
        return true;
    }

    /// <summary>
    /// 移除源文件夹
    /// </summary>
    public bool RemoveSource(string pathOrName)
    {
        var config = Load();

        var source = config.Sources.FirstOrDefault(s =>
            s.Path.Equals(pathOrName, StringComparison.OrdinalIgnoreCase) ||
            s.Name.Equals(pathOrName, StringComparison.OrdinalIgnoreCase));

        if (source == null)
        {
            _logger.LogInformation($"[ConfigManager] 未找到源: {pathOrName}");
            return false;
        }

        config.Sources.Remove(source);
        Save(config);

        _logger.LogInformation($"[ConfigManager] 已移除源: {source.Name}");
        return true;
    }

    /// <summary>
    /// 启用/禁用源
    /// </summary>
    public bool ToggleSource(string pathOrName, bool enabled)
    {
        var config = Load();

        var source = config.Sources.FirstOrDefault(s =>
            s.Path.Equals(pathOrName, StringComparison.OrdinalIgnoreCase) ||
            s.Name.Equals(pathOrName, StringComparison.OrdinalIgnoreCase));

        if (source == null)
        {
            _logger.LogInformation($"[ConfigManager] 未找到源: {pathOrName}");
            return false;
        }

        source.Enabled = enabled;
        Save(config);

        _logger.LogInformation($"[ConfigManager] 已{(enabled ? "启用" : "禁用")}源: {source.Name}");
        return true;
    }

    /// <summary>
    /// 获取所有启用的源
    /// </summary>
    public List<SourceFolder> GetEnabledSources()
    {
        var config = Load();
        return config.Sources.Where(s => s.Enabled).ToList();
    }

    /// <summary>
    /// 应用环境变量覆盖
    /// </summary>
    private void ApplyEnvironmentVariables(ObsidianRagConfig config)
    {
        var sourcePath = Environment.GetEnvironmentVariable("OBSIDIAN_RAG_SOURCE_PATH");
        if (!string.IsNullOrEmpty(sourcePath))
        {
            var name = Environment.GetEnvironmentVariable("OBSIDIAN_RAG_SOURCE_NAME") ?? "Default";

            if (!config.Sources.Any(s => s.Path.Equals(sourcePath, StringComparison.OrdinalIgnoreCase)))
            {
                config.Sources.Add(new SourceFolder
                {
                    Path = sourcePath,
                    Name = name,
                    Type = SourceType.Markdown
                });
                _logger.LogInformation($"[ConfigManager] 从环境变量添加源: {name} ({sourcePath})");
            }
        }

        var sourcesJson = Environment.GetEnvironmentVariable("OBSIDIAN_RAG_SOURCES");
        if (!string.IsNullOrEmpty(sourcesJson))
        {
            try
            {
                var sources = JsonSerializer.Deserialize<List<SourceFolder>>(sourcesJson, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (sources != null)
                {
                    foreach (var source in sources)
                    {
                        if (!config.Sources.Any(s => s.Path.Equals(source.Path, StringComparison.OrdinalIgnoreCase)))
                        {
                            config.Sources.Add(source);
                        }
                    }
                    _logger.LogInformation($"[ConfigManager] 从环境变量加载 {sources.Count} 个源");
                }
            }
            catch (Exception ex)
            {
                _logger.LogInformation($"[ConfigManager] 解析环境变量 OBSIDIAN_RAG_SOURCES 失败: {ex.Message}");
            }
        }

        var dataPath = Environment.GetEnvironmentVariable("OBSIDIAN_RAG_DATA_PATH");
        if (!string.IsNullOrEmpty(dataPath))
        {
            config.DataPath = dataPath;
        }

        var modelPath = Environment.GetEnvironmentVariable("OBSIDIAN_RAG_MODEL_PATH");
        if (!string.IsNullOrEmpty(modelPath))
        {
            config.Embedding.ModelPath = modelPath;
        }

        var modelName = Environment.GetEnvironmentVariable("OBSIDIAN_RAG_MODEL_NAME");
        if (!string.IsNullOrEmpty(modelName))
        {
            config.Embedding.ModelName = modelName;
        }

        var useCuda = Environment.GetEnvironmentVariable("OBSIDIAN_RAG_USE_CUDA");
        if (!string.IsNullOrEmpty(useCuda) && bool.TryParse(useCuda, out var cuda))
        {
            config.Embedding.UseCuda = cuda;
        }

        var port = Environment.GetEnvironmentVariable("OBSIDIAN_RAG_PORT");
        if (!string.IsNullOrEmpty(port) && int.TryParse(port, out var portNum))
        {
            config.Service.Port = portNum;
        }
    }

    /// <summary>
    /// 验证配置
    /// </summary>
    public (bool Valid, List<string> Errors, List<string> Warnings) Validate(ObsidianRagConfig config)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        if (config.Sources.Count == 0)
        {
            errors.Add("未配置任何源文件夹");
        }
        else
        {
            foreach (var source in config.Sources)
            {
                if (string.IsNullOrEmpty(source.Path))
                {
                    errors.Add($"源 '{source.Name}' 路径为空");
                }
                else if (!Directory.Exists(source.Path))
                {
                    warnings.Add($"源 '{source.Name}' 路径不存在: {source.Path}");
                }

                if (source.FilePatterns.Count == 0)
                {
                    warnings.Add($"源 '{source.Name}' 未配置文件模式，将使用默认 *.md");
                }
            }
        }

        if (string.IsNullOrEmpty(config.DataPath))
        {
            errors.Add("DataPath 未配置");
        }

        if (!string.IsNullOrEmpty(config.Embedding.ModelPath) &&
            !File.Exists(config.Embedding.ModelPath))
        {
            warnings.Add($"模型文件不存在: {config.Embedding.ModelPath} (将使用模拟模式)");
        }

        if (config.Service.Port < 1 || config.Service.Port > 65535)
        {
            errors.Add($"无效的端口号: {config.Service.Port}");
        }

        return (errors.Count == 0, errors, warnings);
    }

    /// <summary>
    /// 获取配置文件路径
    /// </summary>
    public static string GetConfigPath() => GetAppSettingsPath();
}
