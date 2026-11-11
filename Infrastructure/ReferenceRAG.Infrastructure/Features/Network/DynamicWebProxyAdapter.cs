using System;
using System.Net;
using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Infrastructure.Features.Network;

/// <summary>
/// 动态自适应 Web 代理适配器
/// 支持运行时零重启热更新（Hot-Reloadable），自动应用 Bypass 白名单与认证凭据
/// </summary>
public sealed class DynamicWebProxyAdapter : IWebProxy
{
    private readonly Func<ProxyConfig> _configProvider;

    public DynamicWebProxyAdapter(Func<ProxyConfig> configProvider)
    {
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
    }

    private ICredentials? _explicitCredentials;

    public ICredentials? Credentials
    {
        get
        {
            if (_explicitCredentials != null) return _explicitCredentials;
            var cfg = _configProvider();
            if (cfg.Enabled && !string.IsNullOrWhiteSpace(cfg.Username))
            {
                return new NetworkCredential(cfg.Username, cfg.Password ?? string.Empty);
            }
            return null;
        }
        set
        {
            _explicitCredentials = value;
        }
    }

    public Uri? GetProxy(Uri destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var cfg = _configProvider();
        if (!cfg.Enabled || string.IsNullOrWhiteSpace(cfg.ProxyUrl))
        {
            return null;
        }

        if (IsBypassed(destination))
        {
            return null;
        }

        var normalizedUrl = NormalizeProxyUrl(cfg.ProxyUrl);
        if (Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var proxyUri))
        {
            return proxyUri;
        }

        return null;
    }

    /// <summary>
    /// 标准化代理 URL，支持自动补齐 scheme 和修复形如 socks5:127.0.0.1:1080 缺少 // 的格式
    /// </summary>
    public static string NormalizeProxyUrl(string? rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl)) return string.Empty;
        var trimmed = rawUrl.Trim();
        if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^(socks5|socks5h|http|https):(?!\/\/)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            return System.Text.RegularExpressions.Regex.Replace(trimmed, @"^(socks5|socks5h|http|https):(?!\/\/)", "$1://", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }
        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            return "http://" + trimmed;
        }
        return trimmed;
    }

    public bool IsBypassed(Uri host)
    {
        ArgumentNullException.ThrowIfNull(host);
        var cfg = _configProvider();
        if (!cfg.Enabled) return true;

        if (string.IsNullOrWhiteSpace(cfg.BypassHosts)) return false;

        var targetHost = host.Host;
        var rules = cfg.BypassHosts.Split(new[] { ';', ',', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var rule in rules)
        {
            if (string.Equals(rule, targetHost, StringComparison.OrdinalIgnoreCase))
                return true;

            if (rule.StartsWith("*.", StringComparison.Ordinal))
            {
                var suffix = rule[1..];
                if (targetHost.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(targetHost, rule[2..], StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            else if (rule.StartsWith('.') && targetHost.EndsWith(rule, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 根据配置与策略解析适用的 IWebProxy 实例
    /// </summary>
    public static IWebProxy? ResolveProxy(ObsidianRagConfig? config, ProxyPolicy policy, string? customProxyUrl = null)
    {
        if (policy == ProxyPolicy.Direct) return null;
        if (policy == ProxyPolicy.Custom)
        {
            if (string.IsNullOrWhiteSpace(customProxyUrl)) return null;
            return new DynamicWebProxyAdapter(() => new ProxyConfig { Enabled = true, ProxyUrl = customProxyUrl });
        }

        // Inherit
        var netProxy = config?.Network?.Proxy;
        if (netProxy != null && netProxy.Enabled && !string.IsNullOrWhiteSpace(netProxy.ProxyUrl))
        {
            return new DynamicWebProxyAdapter(() => netProxy);
        }
        return null;
    }
}
