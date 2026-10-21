namespace ReferenceRAG.Host.Tests;

using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using ReferenceRAG.ApiHost.Configuration;
using Xunit;

public class NetworkBindingResolverTests
{
    [Fact]
    public void Resolve_NonContainerDefault_ReturnsLocalhostAndFalse()
    {
        var config = new ConfigurationBuilder().Build();

        var result = NetworkBindingResolver.Resolve(
            configuration: config,
            isContainerOverride: false,
            aspnetcoreUrlsOverride: null);

        Assert.False(result.AllowNetworkAccess);
        Assert.Equal("localhost", result.Host);
        Assert.Equal("5000", result.Port);
        Assert.Equal("http://localhost:5000", result.EffectiveUrls);
    }

    [Fact]
    public void Resolve_ContainerDefault_ReturnsAllInterfacesAndTrue()
    {
        var config = new ConfigurationBuilder().Build();

        var result = NetworkBindingResolver.Resolve(
            configuration: config,
            isContainerOverride: true,
            aspnetcoreUrlsOverride: null);

        Assert.True(result.AllowNetworkAccess);
        Assert.Equal("0.0.0.0", result.Host);
        Assert.Equal("5000", result.Port);
        Assert.Equal("http://0.0.0.0:5000", result.EffectiveUrls);
    }

    [Fact]
    public void Resolve_ContainerWithAspNetCoreUrls_PreservesAspNetCoreUrls()
    {
        var config = new ConfigurationBuilder().Build();

        var result = NetworkBindingResolver.Resolve(
            configuration: config,
            isContainerOverride: true,
            aspnetcoreUrlsOverride: "http://+:5000");

        Assert.True(result.AllowNetworkAccess);
        Assert.Equal("http://+:5000", result.EffectiveUrls);
    }

    [Fact]
    public void Resolve_ExplicitAllowNetworkFalse_HonorsSettingEvenInContainer()
    {
        var dict = new Dictionary<string, string?>
        {
            ["ReferenceRAG:Service:allowNetworkAccess"] = "false"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();

        var result = NetworkBindingResolver.Resolve(
            configuration: config,
            isContainerOverride: true,
            aspnetcoreUrlsOverride: null);

        Assert.False(result.AllowNetworkAccess);
        Assert.Equal("localhost", result.Host);
        Assert.Equal("http://localhost:5000", result.EffectiveUrls);
    }

    [Fact]
    public void Resolve_ExplicitHost0000_EnablesNetworkAccess()
    {
        var dict = new Dictionary<string, string?>
        {
            ["ReferenceRAG:Service:host"] = "0.0.0.0"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();

        var result = NetworkBindingResolver.Resolve(
            configuration: config,
            isContainerOverride: false,
            aspnetcoreUrlsOverride: null);

        Assert.True(result.AllowNetworkAccess);
        Assert.Equal("0.0.0.0", result.Host);
        Assert.Equal("http://0.0.0.0:5000", result.EffectiveUrls);
    }

    [Fact]
    public void Resolve_ExplicitUrls_OverridesDefaultAndAspNetCoreUrls()
    {
        var dict = new Dictionary<string, string?>
        {
            ["urls"] = "http://127.0.0.1:9090",
            ["ReferenceRAG:Service:port"] = "5000"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();

        var result = NetworkBindingResolver.Resolve(
            configuration: config,
            isContainerOverride: true,
            aspnetcoreUrlsOverride: "http://+:5000");

        Assert.Equal("http://127.0.0.1:9090", result.EffectiveUrls);
    }
}
