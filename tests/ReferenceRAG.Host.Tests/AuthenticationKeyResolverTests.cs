using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using ReferenceRAG.ApiHost.Authentication;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using Xunit;

namespace ReferenceRAG.Host.Tests;

public class AuthenticationKeyResolverTests
{
    [Fact]
    public void ConfigFileKey_HasHighestPriority_OverEnvironmentVariables()
    {
        // Arrange
        const string expectedConfigFileKey = "ConfigFileKey_TopPriority_123456";
        const string envVariableKey = "EnvVarKey_ShouldBeIgnored_789012";

        var configStrategy = new MockKeyStrategy(1, KeyResolutionSource.ConfigFile, expectedConfigFileKey);
        var envStrategy = new MockKeyStrategy(2, KeyResolutionSource.EnvironmentVariable, envVariableKey);

        var coordinator = new AuthenticationKeyResolutionCoordinator(new[] { configStrategy, envStrategy });

        // Act
        var result = coordinator.ResolveKey();

        // Assert: 必须无条件命中配置文件中的 Key，且来源标注为 ConfigFile
        Assert.True(result.IsEnabled);
        Assert.Equal(expectedConfigFileKey, result.ApiKey);
        Assert.Equal(KeyResolutionSource.ConfigFile, result.Source);
        Assert.Contains("配置文件优先命中", result.Details);
    }

    [Fact]
    public void WhenConfigFileKeyIsEmpty_FallbackToEnvironmentVariable()
    {
        // Arrange
        const string expectedEnvKey = "Docker_Env_ApiKey_abcdef123";

        var configStrategy = new MockKeyStrategy(1, KeyResolutionSource.ConfigFile, null);
        var envStrategy = new MockKeyStrategy(2, KeyResolutionSource.EnvironmentVariable, expectedEnvKey);

        var coordinator = new AuthenticationKeyResolutionCoordinator(new[] { configStrategy, envStrategy });

        // Act
        var result = coordinator.ResolveKey();

        // Assert: 配置文件为空时，成功回退至环境变量
        Assert.True(result.IsEnabled);
        Assert.Equal(expectedEnvKey, result.ApiKey);
        Assert.Equal(KeyResolutionSource.EnvironmentVariable, result.Source);
        Assert.Contains("Docker/系统环境变量命中", result.Details);
    }

    [Fact]
    public void WhenBothConfigFileAndEnvAreEmpty_AuthenticationIsDisabled()
    {
        // Arrange
        var configStrategy = new MockKeyStrategy(1, KeyResolutionSource.ConfigFile, null);
        var envStrategy = new MockKeyStrategy(2, KeyResolutionSource.EnvironmentVariable, "");

        var coordinator = new AuthenticationKeyResolutionCoordinator(new[] { configStrategy, envStrategy });

        // Act
        var result = coordinator.ResolveKey();

        // Assert: 均未配置时认证处于关闭状态
        Assert.False(result.IsEnabled);
        Assert.Null(result.ApiKey);
        Assert.Equal(KeyResolutionSource.None, result.Source);
    }

    [Theory]
    [InlineData("REFERENCERAG_API_KEY", "Key1")]
    [InlineData("API_KEY", "Key2")]
    [InlineData("AUTH_TOKEN", "Key3")]
    public void EnvironmentVariableStrategy_ResolvesExpectedVariables(string varName, string keyValue)
    {
        // Arrange
        var envMap = new Dictionary<string, string>
        {
            [varName] = keyValue
        };

        var strategy = new EnvironmentVariableKeyResolutionStrategy(key => envMap.TryGetValue(key, out var v) ? v : null);

        // Act
        var resolved = strategy.TryResolveKey();

        // Assert
        Assert.Equal(keyValue, resolved);
    }

    [Fact]
    public void EnvironmentVariableStrategy_PriorityOrder_ReferenceragApiKeyWins()
    {
        // Arrange: 同时存在多个候选变量时，REFERENCERAG_API_KEY 优先级最高
        var envMap = new Dictionary<string, string>
        {
            ["REFERENCERAG_API_KEY"] = "SpecializedKey",
            ["API_KEY"] = "GenericKey",
            ["AUTH_TOKEN"] = "LegacyToken"
        };

        var strategy = new EnvironmentVariableKeyResolutionStrategy(key => envMap.TryGetValue(key, out var v) ? v : null);

        // Act
        var resolved = strategy.TryResolveKey();

        // Assert
        Assert.Equal("SpecializedKey", resolved);
    }

    private class MockKeyStrategy : IKeyResolutionStrategy
    {
        public int Priority { get; }
        public KeyResolutionSource Source { get; }
        private readonly string? _key;

        public MockKeyStrategy(int priority, KeyResolutionSource source, string? key)
        {
            Priority = priority;
            Source = source;
            _key = key;
        }

        public string? TryResolveKey() => _key;
    }
}
