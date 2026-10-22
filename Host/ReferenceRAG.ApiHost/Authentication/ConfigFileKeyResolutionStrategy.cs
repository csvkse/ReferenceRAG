using Microsoft.Extensions.Configuration;
using ReferenceRAG.Core.Services;

namespace ReferenceRAG.ApiHost.Authentication;

/// <summary>
/// 配置文件密钥解析策略（最高优先级 Priority = 1）
/// 读取 ConfigManager 业务配置以及 appsettings.json
/// </summary>
public sealed class ConfigFileKeyResolutionStrategy : IKeyResolutionStrategy
{
    private readonly ConfigManager? _configManager;
    private readonly IConfiguration? _configuration;

    public int Priority => 1;
    public KeyResolutionSource Source => KeyResolutionSource.ConfigFile;

    public ConfigFileKeyResolutionStrategy(ConfigManager? configManager = null, IConfiguration? configuration = null)
    {
        _configManager = configManager;
        _configuration = configuration;
    }

    public string? TryResolveKey()
    {
        // 1. 优先从 ConfigManager 读取当前活跃配置文件的 Service.ApiKey
        try
        {
            if (_configManager != null)
            {
                var cfg = _configManager.Load();
                var key = cfg.Service?.ApiKey?.Trim();
                if (!string.IsNullOrEmpty(key))
                {
                    return key;
                }
            }
        }
        catch
        {
            // 忽略配置读取瞬态异常，降级到 IConfiguration 检查
        }

        // 2. 从原生配置树中检查 ApiKey:Key
        if (_configuration != null)
        {
            var key = _configuration.GetValue<string>("ApiKey:Key")?.Trim();
            if (!string.IsNullOrEmpty(key))
            {
                return key;
            }

            var serviceKey = _configuration.GetValue<string>("ReferenceRAG:Service:ApiKey")?.Trim();
            if (!string.IsNullOrEmpty(serviceKey))
            {
                return serviceKey;
            }
        }

        return null;
    }
}
