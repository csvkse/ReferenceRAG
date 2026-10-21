namespace ReferenceRAG.ApiHost.Configuration;

using System;
using Microsoft.Extensions.Configuration;

/// <summary>
/// Web 宿主网络服务地址与端口解析结果
/// </summary>
public record NetworkBindingResult(
    string EffectiveUrls,
    bool AllowNetworkAccess,
    string Host,
    string Port);

/// <summary>
/// Web 宿主网络端点与监听地址解析器
/// 负责在不同运行环境（Docker 容器、本地开发、生产服务）下解析出安全且正确的监听 URL
/// </summary>
public static class NetworkBindingResolver
{
    /// <summary>
    /// 解析服务监听 URL 与网络访问策略
    /// </summary>
    public static NetworkBindingResult Resolve(
        IConfiguration configuration,
        bool? isContainerOverride = null,
        string? aspnetcoreUrlsOverride = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var serviceConfig = configuration.GetSection("ReferenceRAG:Service");
        var isContainer = isContainerOverride ?? string.Equals(
            Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
            "true",
            StringComparison.OrdinalIgnoreCase);

        // 1. 判断是否允许外部网络访问
        // 规则优先级：
        // a. 配置文件中显式指定的 allowNetworkAccess 布尔值
        // b. 配置文件中显式指定的 host (若为 0.0.0.0 或 + 则为 true)
        // c. 若未显式配置：容器环境下默认允许 (true，监听 0.0.0.0)，非容器环境默认安全回环 (false，监听 localhost)
        bool allowNetwork;
        if (bool.TryParse(serviceConfig["allowNetworkAccess"], out var explicitAllowNet))
        {
            allowNetwork = explicitAllowNet;
        }
        else
        {
            var hostSetting = serviceConfig["host"];
            if (!string.IsNullOrWhiteSpace(hostSetting) &&
                (string.Equals(hostSetting, "0.0.0.0", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(hostSetting, "+", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(hostSetting, "*", StringComparison.OrdinalIgnoreCase)))
            {
                allowNetwork = true;
            }
            else
            {
                allowNetwork = isContainer;
            }
        }

        var host = allowNetwork ? "0.0.0.0" : "localhost";
        var port = serviceConfig["port"] ?? "5000";
        var defaultUrl = $"http://{host}:{port}";

        // 2. 解析最终生效的 URLs
        // 优先级：
        // 1. 显式配置的 urls (例如命令行参数 --urls 或配置中的 urls 字段)
        // 2. 容器环境下的 ASPNETCORE_URLS (如 http://+:5000)
        // 3. 基于 allowNetwork 与 port 计算出的 defaultUrl
        var explicitUrls = configuration["urls"];
        var aspnetcoreUrls = aspnetcoreUrlsOverride ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS");

        string effectiveUrls;
        if (!string.IsNullOrWhiteSpace(explicitUrls))
        {
            effectiveUrls = explicitUrls;
        }
        else if (isContainer && !string.IsNullOrWhiteSpace(aspnetcoreUrls))
        {
            effectiveUrls = aspnetcoreUrls;
        }
        else
        {
            effectiveUrls = defaultUrl;
        }

        return new NetworkBindingResult(effectiveUrls, allowNetwork, host, port);
    }
}
