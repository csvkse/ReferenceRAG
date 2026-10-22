using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Services;

namespace ReferenceRAG.ApiHost.Authentication;

/// <summary>
/// 访问密钥仲裁协调器（严格保证“配置文件密钥优先级最高，其次是环境变量”）
/// </summary>
public sealed class AuthenticationKeyResolutionCoordinator : IAuthenticationKeyResolver
{
    private readonly List<IKeyResolutionStrategy> _strategies;
    private readonly ILogger? _logger;

    public AuthenticationKeyResolutionCoordinator(
        IEnumerable<IKeyResolutionStrategy>? strategies = null,
        ConfigManager? configManager = null,
        IConfiguration? configuration = null,
        ILogger? logger = null)
    {
        _logger = logger;
        if (strategies != null && strategies.Any())
        {
            _strategies = strategies.OrderBy(s => s.Priority).ToList();
        }
        else
        {
            // 默认策略流水线：严格 配置文件(1) -> 环境变量(2)
            _strategies = new List<IKeyResolutionStrategy>
            {
                new ConfigFileKeyResolutionStrategy(configManager, configuration),
                new EnvironmentVariableKeyResolutionStrategy()
            }.OrderBy(s => s.Priority).ToList();
        }
    }

    public AuthenticationKeyResolutionResult ResolveKey()
    {
        foreach (var strategy in _strategies)
        {
            var key = strategy.TryResolveKey();
            if (!string.IsNullOrEmpty(key))
            {
                var masked = MaskKey(key);
                var details = strategy.Source switch
                {
                    KeyResolutionSource.ConfigFile => $"配置文件优先命中 (Key: {masked})",
                    KeyResolutionSource.EnvironmentVariable => $"Docker/系统环境变量命中 (Key: {masked})",
                    _ => $"来源未知 (Key: {masked})"
                };

                return new AuthenticationKeyResolutionResult(
                    IsEnabled: true,
                    ApiKey: key,
                    Source: strategy.Source,
                    Details: details
                );
            }
        }

        return new AuthenticationKeyResolutionResult(
            IsEnabled: false,
            ApiKey: null,
            Source: KeyResolutionSource.None,
            Details: "未配置访问密钥（API Key 鉴权未启用）"
        );
    }

    private static string MaskKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return "";
        if (key.Length <= 4) return "****";
        return $"{key[..2]}****{key[^2..]}";
    }
}
