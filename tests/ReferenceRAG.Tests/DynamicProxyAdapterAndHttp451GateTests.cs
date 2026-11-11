using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Infrastructure.Features.Cognitive;
using ReferenceRAG.Infrastructure.Features.Network;
using Xunit;

namespace ReferenceRAG.Tests;

public class DynamicProxyAdapterAndHttp451GateTests
{
    [Fact]
    public void DynamicWebProxyAdapter_Should_HotReload_Config_Without_Restart()
    {
        var proxyConfig = new ProxyConfig
        {
            Enabled = false,
            ProxyUrl = "http://127.0.0.1:7890",
            BypassHosts = "localhost;127.0.0.1;*.internal.net"
        };

        var adapter = new DynamicWebProxyAdapter(() => proxyConfig);
        var testDest = new Uri("https://api.typesafe.ai/v1/systemone");

        // 1. 禁用时，不应返回代理
        Assert.Null(adapter.GetProxy(testDest));
        Assert.True(adapter.IsBypassed(testDest));

        // 2. 动态启用，无需重启实例，即刻生效
        proxyConfig.Enabled = true;
        Assert.NotNull(adapter.GetProxy(testDest));
        Assert.Equal("http://127.0.0.1:7890/", adapter.GetProxy(testDest)?.ToString());
        Assert.False(adapter.IsBypassed(testDest));

        // 3. Bypass 白名单测试
        var localDest = new Uri("http://localhost:5000/api/health");
        Assert.True(adapter.IsBypassed(localDest));
        Assert.Null(adapter.GetProxy(localDest));

        var internalDest = new Uri("https://service.internal.net/info");
        Assert.True(adapter.IsBypassed(internalDest));
        Assert.Null(adapter.GetProxy(internalDest));
    }

    [Fact]
    public void DynamicWebProxyAdapter_Should_Support_ProxyPolicy_Resolution()
    {
        var globalConfig = new ObsidianRagConfig
        {
            Network = new NetworkConfig
            {
                Proxy = new ProxyConfig
                {
                    Enabled = true,
                    ProxyUrl = "http://global-proxy:8080"
                }
            }
        };

        // 策略 1: Direct 强制直连
        var directProxy = DynamicWebProxyAdapter.ResolveProxy(globalConfig, ProxyPolicy.Direct);
        Assert.Null(directProxy);

        // 策略 2: Inherit 继承统一全局代理
        var inheritProxy = DynamicWebProxyAdapter.ResolveProxy(globalConfig, ProxyPolicy.Inherit);
        Assert.NotNull(inheritProxy);
        Assert.Equal("http://global-proxy:8080/", inheritProxy.GetProxy(new Uri("https://api.typesafe.ai"))?.ToString());

        // 策略 3: Custom 自定义专属代理
        var customProxy = DynamicWebProxyAdapter.ResolveProxy(globalConfig, ProxyPolicy.Custom, "http://custom-proxy:9090");
        Assert.NotNull(customProxy);
        Assert.Equal("http://custom-proxy:9090/", customProxy.GetProxy(new Uri("https://api.typesafe.ai"))?.ToString());
    }

    [Theory]
    [InlineData("socks5:127.0.0.1:1080", "socks5://127.0.0.1:1080")]
    [InlineData("socks5://127.0.0.1:1080", "socks5://127.0.0.1:1080")]
    [InlineData("http:127.0.0.1:1080", "http://127.0.0.1:1080")]
    [InlineData("127.0.0.1:1080", "http://127.0.0.1:1080")]
    public void DynamicWebProxyAdapter_NormalizeProxyUrl_Should_Normalize_Correctly(string input, string expected)
    {
        var normalized = DynamicWebProxyAdapter.NormalizeProxyUrl(input);
        Assert.Equal(expected, normalized);

        var adapter = new DynamicWebProxyAdapter(() => new ProxyConfig { Enabled = true, ProxyUrl = input });
        var proxyUri = adapter.GetProxy(new Uri("https://api.typesafe.ai/v1/systemone"));
        Assert.NotNull(proxyUri);
        Assert.Equal("127.0.0.1", proxyUri.Host);
        Assert.Equal(1080, proxyUri.Port);
    }

    [Fact]
    public async Task JevSystemOneClient_WhenHttp451Returned_Should_Fallback_Safely_Without_Crashing()
    {
        // 模拟 Cloudflare / Typesafe 针对特定区域返回 HTTP 451
        var mockHandler = new DelegatingHandlerStub((req, ct) =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.UnavailableForLegalReasons)
            {
                Content = new StringContent("{\"title\":\"Typesafe is not available in your region.\",\"status\":451}")
            };
            return Task.FromResult(resp);
        });

        using var client = new JevSystemOneClient("fake_key", "https://api.typesafe.ai/v1/systemone", "jev-latest", mockHandler);
        var engine = new JevDecisionEngine(client, enabled: true, retrievalThreshold: 0.65f);

        // 断言即使遇到 451 封锁，决策引擎也能优雅触发规则兜底（返回需检索），绝对不会抛出未捕获异常崩溃服务
        var result = await engine.ClassifyIntentAsync("金庸小说中杨过断的是哪只手臂？");

        Assert.NotNull(result);
        Assert.True(result.RequiresRetrieval);
        Assert.Contains("451", result.Rationale);
        Assert.Contains("决策异常兜底", result.Rationale);
    }

    private class DelegatingHandlerStub : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;
        public DelegatingHandlerStub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) => _handler = handler;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => _handler(request, cancellationToken);
    }
}
