using System.Text.Json;
using ObsidianRAG.Core.Models;

namespace ObsidianRAG.Core.Services;

/// <summary>
/// 配置管理器
/// </summary>
public class ConfigManager
{
    private readonly string _configPath;
    private ObsidianRagConfig? _config;

    public ConfigManager(string? configPath = null)
    {
        _configPath = configPath ?? GetConfigPathFromAppSettings() ?? GetDefaultConfigPath();
    }

    /// <summary>
    /// 从 appsettings.json 读取配置文件路径
    /// </summary>
    private static string? GetConfigPathFromAppSettings()
    {
        // 尝试从当前目录读取 appsettings.json
        var currentDir = Directory.GetCurrentDirectory();
        var appSettingsPath = Path.Combine(currentDir, "appsettings.json");

        if (!File.Exists(appSettingsPath))
        {
            // 尝试开发环境路径
            appSettingsPath = Path.Combine(currentDir, "appsettings.Development.json");
        }

        if (!File.Exists(appSettingsPath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(appSettingsPath);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("ObsidianRAG", out var obsidianRag) &&
                obsidianRag.TryGetProperty("ConfigPath", out var configPathElement))
            {
                var configPath = configPathElement.GetString();
                if (!string.IsNullOrEmpty(configPath))
                {
                    // 支持相对路径转换为绝对路径
                    if (!Path.IsPathRooted(configPath))
                    {
                        configPath = Path.GetFullPath(Path.Combine(currentDir, configPath));
                    }
                    Console.WriteLine($"[ConfigManager] 从 appsettings.json 读取配置路径: {configPath}");
                    return configPath;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ConfigManager] 读取 appsettings.json 失败: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// 获取默认配置路径
    /// </summary>
    private static string GetDefaultConfigPath()
    {
        var currentDir = Directory.GetCurrentDirectory();
        var localConfig = Path.Combine(currentDir, "obsidian-rag.json");
        if (File.Exists(localConfig)) return localConfig;

        var userConfig = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".obsidian-rag",
            "config.json"
        );
        if (File.Exists(userConfig)) return userConfig;

        return localConfig;
    }

    /// <summary>
    /// 加载配置
    /// </summary>
    public ObsidianRagConfig Load()
    {
        if (_config != null) return _config;

        if (File.Exists(_configPath))
        {
            try
            {
                var json = File.ReadAllText(_configPath);
                _config = JsonSerializer.Deserialize<ObsidianRagConfig>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip
                });

                Console.WriteLine($"[ConfigManager] 已加载配置文件: {_configPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ConfigManager] 配置文件解析失败: {ex.Message}");
                _config = new ObsidianRagConfig();
            }
        }
        else
        {
            Console.WriteLine($"[ConfigManager] 配置文件不存在，使用默认配置");
            _config = new ObsidianRagConfig();
        }

        ApplyEnvironmentVariables(_config!);
        MigrateOldConfig(_config!);

        return _config;
    }

    /// <summary>
    /// 迁移旧配置格式
    /// </summary>
    private void MigrateOldConfig(ObsidianRagConfig config)
    {
        // 兼容旧的 VaultPath 配置
#pragma warning disable CS0618 // 禁用过时警告
        var oldPath = config.VaultPath;
        if (!string.IsNullOrEmpty(oldPath) && config.Sources.Count == 0)
        {
            config.Sources.Add(new SourceFolder
            {
                Path = oldPath,
                Name = "Default",
                Type = SourceType.Markdown
            });
            Console.WriteLine($"[ConfigManager] 已迁移旧配置 VaultPath -> Sources[0]");
        }
#pragma warning restore CS0618
    }

    /// <summary>
    /// 保存配置
    /// </summary>
    public void Save(ObsidianRagConfig config)
    {
        var directory = Path.GetDirectoryName(_configPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        var json = JsonSerializer.Serialize(config, options);
        File.WriteAllText(_configPath, json);

        _config = config;
        Console.WriteLine($"[ConfigManager] 配置已保存到: {_configPath}");
    }

    /// <summary>
    /// 添加源文件夹
    /// </summary>
    public void AddSource(SourceFolder source)
    {
        var config = Load();
        
        // 检查是否已存在
        if (config.Sources.Any(s => s.Path.Equals(source.Path, StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine($"[ConfigManager] 源已存在: {source.Path}");
            return;
        }

        // 自动设置名称
        if (string.IsNullOrEmpty(source.Name))
        {
            source.Name = Path.GetFileName(source.Path) ?? $"Source{config.Sources.Count + 1}";
        }

        config.Sources.Add(source);
        Save(config);
        
        Console.WriteLine($"[ConfigManager] 已添加源: {source.Name} ({source.Path})");
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
            Console.WriteLine($"[ConfigManager] 未找到源: {pathOrName}");
            return false;
        }

        config.Sources.Remove(source);
        Save(config);
        
        Console.WriteLine($"[ConfigManager] 已移除源: {source.Name}");
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
            Console.WriteLine($"[ConfigManager] 未找到源: {pathOrName}");
            return false;
        }

        source.Enabled = enabled;
        Save(config);
        
        Console.WriteLine($"[ConfigManager] 已{(enabled ? "启用" : "禁用")}源: {source.Name}");
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
        // 单个源路径（兼容）
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
                Console.WriteLine($"[ConfigManager] 从环境变量添加源: {name} ({sourcePath})");
            }
        }

        // 多个源路径（JSON 格式）
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
                    Console.WriteLine($"[ConfigManager] 从环境变量加载 {sources.Count} 个源");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ConfigManager] 解析环境变量 OBSIDIAN_RAG_SOURCES 失败: {ex.Message}");
            }
        }

        // 数据路径
        var dataPath = Environment.GetEnvironmentVariable("OBSIDIAN_RAG_DATA_PATH");
        if (!string.IsNullOrEmpty(dataPath))
        {
            config.DataPath = dataPath;
        }

        // 模型配置
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

        // 端口
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

        // 验证源
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

        // 验证数据路径
        if (string.IsNullOrEmpty(config.DataPath))
        {
            errors.Add("DataPath 未配置");
        }

        // 验证模型路径（可选）
        if (!string.IsNullOrEmpty(config.Embedding.ModelPath) && 
            !File.Exists(config.Embedding.ModelPath))
        {
            warnings.Add($"模型文件不存在: {config.Embedding.ModelPath} (将使用模拟模式)");
        }

        // 验证端口
        if (config.Service.Port < 1 || config.Service.Port > 65535)
        {
            errors.Add($"无效的端口号: {config.Service.Port}");
        }

        return (errors.Count == 0, errors, warnings);
    }

    /// <summary>
    /// 创建默认配置文件
    /// </summary>
    public void CreateDefaultConfig()
    {
        var config = new ObsidianRagConfig
        {
            DataPath = "data",
            Sources = new List<SourceFolder>
            {
                new()
                {
                    Path = "",
                    Name = "示例源",
                    Type = SourceType.Markdown,
                    FilePatterns = new List<string> { "*.md" },
                    Recursive = true,
                    ExcludeDirs = new List<string> { ".git", "node_modules" }
                }
            },
            Embedding = new EmbeddingConfig
            {
                ModelPath = "models/bge-small-zh-v1.5/model.onnx",
                ModelName = "bge-small-zh-v1.5"
            },
            Chunking = new ChunkingConfig
            {
                MaxTokens = 512,
                MinTokens = 50,
                OverlapTokens = 50
            },
            Search = new SearchConfig
            {
                DefaultTopK = 10,
                ContextWindow = 1
            },
            Service = new ServiceConfig
            {
                Port = 5000,
                Host = "localhost"
            }
        };

        Save(config);
    }

    /// <summary>
    /// 获取配置文件路径
    /// </summary>
    public string GetConfigPath() => _configPath;
}
