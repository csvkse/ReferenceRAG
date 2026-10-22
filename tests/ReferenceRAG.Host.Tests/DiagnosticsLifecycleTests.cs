using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using ReferenceRAG.ApiHost.Authentication;
using ReferenceRAG.ApiHost.Diagnostics;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using Xunit;

namespace ReferenceRAG.Host.Tests;

public class DiagnosticsLifecycleTests
{
    [Fact]
    public void WhenNoDiagKeyInConfigOrEnv_StateIsUnconfigured_And_Disabled()
    {
        // Arrange
        var envMap = new Dictionary<string, string>();
        var adapter = new TemporaryDiagnosticsAdapter(configManager: null, configuration: null, k => envMap.TryGetValue(k, out var v) ? v : null);

        // Act
        var report = adapter.EvaluateEnvironment();

        // Assert
        Assert.False(adapter.IsEnabled);
        Assert.False(report.IsEnabled);
        Assert.Equal(DiagnosticsLifecycleState.Unconfigured, adapter.CurrentState);
        Assert.Equal(KeyResolutionSource.None, report.Source);
        Assert.Contains("未在配置文件或环境变量中配置有效 DIAG_KEY", report.StatusMessage);
    }

    [Theory]
    [InlineData("123")]
    [InlineData("1234567")]
    [InlineData("abc")]
    public void WhenDiagKeyLengthIsLessThan8_StateIsInvalidEntropy_And_DisabledForSecurity(string weakKey)
    {
        // Arrange: 传入短于 8 位的弱口令
        var envMap = new Dictionary<string, string>
        {
            ["DIAG_KEY"] = weakKey
        };
        var adapter = new TemporaryDiagnosticsAdapter(configManager: null, configuration: null, k => envMap.TryGetValue(k, out var v) ? v : null);

        // Act
        var report = adapter.EvaluateEnvironment();

        // Assert: 必须处于安全防御状态，拒绝启动
        Assert.False(adapter.IsEnabled);
        Assert.False(report.IsEnabled);
        Assert.Equal(DiagnosticsLifecycleState.InvalidEntropy, adapter.CurrentState);
        Assert.Contains("长度不足 8 位", report.StatusMessage);
    }

    [Theory]
    [InlineData("DIAG_KEY", "SuperSecretDiagnosticKey@2026")]
    [InlineData("TEMPORARY_DIAGNOSTICS_KEY", "AlternativeDiagnosticKey#9988")]
    [InlineData("DIAGNOSTIC_ACCESS_KEY", "ThirdCandidateDiagKey!123456")]
    public void WhenValidDiagKeyProvidedInEnv_StateIsArmedAndActive(string varName, string strongKey)
    {
        // Arrange
        var envMap = new Dictionary<string, string>
        {
            [varName] = strongKey
        };
        var adapter = new TemporaryDiagnosticsAdapter(configManager: null, configuration: null, k => envMap.TryGetValue(k, out var v) ? v : null);

        // Act
        var report = adapter.EvaluateEnvironment();

        // Assert: 成功激活，且来源标识为 EnvironmentVariable
        Assert.True(adapter.IsEnabled);
        Assert.True(report.IsEnabled);
        Assert.Equal(DiagnosticsLifecycleState.ArmedAndActive, adapter.CurrentState);
        Assert.Equal("/__diag", adapter.RoutePrefix);
        Assert.Equal(KeyResolutionSource.EnvironmentVariable, report.Source);
        Assert.Contains("受控诊断中间件已就绪", report.StatusMessage);
    }

    [Fact]
    public void WhenConfigFileDiagKeyIsProvided_ConfigFileWins_OverEnvironmentVariable()
    {
        // Arrange: 配置文件与环境变量同时存在，配置文件必须无条件胜出
        const string expectedConfigFileKey = "ConfigFile_DiagKey_TopPriority@2026";
        const string envVarKey = "EnvVar_DiagKey_LowerPriority#123456";

        var config = new ObsidianRagConfig
        {
            Diagnostics = new DiagnosticsConfig
            {
                Key = expectedConfigFileKey
            }
        };
        var configManager = new ConfigManager(config);

        var envMap = new Dictionary<string, string>
        {
            ["DIAG_KEY"] = envVarKey
        };

        var adapter = new TemporaryDiagnosticsAdapter(configManager, configuration: null, k => envMap.TryGetValue(k, out var v) ? v : null);

        // Act
        var report = adapter.EvaluateEnvironment();

        // Assert: 必须命中配置文件中的密钥
        Assert.True(adapter.IsEnabled);
        Assert.Equal(DiagnosticsLifecycleState.ArmedAndActive, adapter.CurrentState);
        Assert.Equal(KeyResolutionSource.ConfigFile, report.Source);
        Assert.Contains("ConfigManager.Diagnostics.Key", report.StatusMessage);
    }

    [Fact]
    public void WhenServiceDiagKeyIsProvidedInConfig_ConfigFileWins()
    {
        // Arrange: 通过 Service.DiagKey 配置
        const string expectedServiceKey = "Service_DiagKey_TopPriority@2026";
        const string envVarKey = "EnvVar_DiagKey_LowerPriority#123456";

        var config = new ObsidianRagConfig
        {
            Service = new ServiceConfig
            {
                DiagKey = expectedServiceKey
            }
        };
        var configManager = new ConfigManager(config);

        var envMap = new Dictionary<string, string>
        {
            ["DIAG_KEY"] = envVarKey
        };

        var adapter = new TemporaryDiagnosticsAdapter(configManager, configuration: null, k => envMap.TryGetValue(k, out var v) ? v : null);

        // Act
        var report = adapter.EvaluateEnvironment();

        // Assert
        Assert.True(adapter.IsEnabled);
        Assert.Equal(DiagnosticsLifecycleState.ArmedAndActive, adapter.CurrentState);
        Assert.Equal(KeyResolutionSource.ConfigFile, report.Source);
        Assert.Contains("ConfigManager.Service.DiagKey", report.StatusMessage);
    }

    [Fact]
    public void WhenConfigFileKeyIsEmpty_FallbackToEnvironmentVariable()
    {
        // Arrange: 配置文件 Key 为空，回退读取环境变量
        const string expectedEnvVarKey = "EnvVar_DiagKey_Fallback#123456";

        var config = new ObsidianRagConfig
        {
            Diagnostics = new DiagnosticsConfig
            {
                Key = "" // 配置文件为空
            }
        };
        var configManager = new ConfigManager(config);

        var envMap = new Dictionary<string, string>
        {
            ["DIAG_KEY"] = expectedEnvVarKey
        };

        var adapter = new TemporaryDiagnosticsAdapter(configManager, configuration: null, k => envMap.TryGetValue(k, out var v) ? v : null);

        // Act
        var report = adapter.EvaluateEnvironment();

        // Assert: 成功回退至环境变量
        Assert.True(adapter.IsEnabled);
        Assert.Equal(DiagnosticsLifecycleState.ArmedAndActive, adapter.CurrentState);
        Assert.Equal(KeyResolutionSource.EnvironmentVariable, report.Source);
    }

    [Fact]
    public void CustomRoutePrefix_ConfigFilePriority_OverEnvironmentVariable()
    {
        // Arrange
        var config = new ObsidianRagConfig
        {
            Diagnostics = new DiagnosticsConfig
            {
                Key = "ValidDiagnosticSecret@2026",
                RoutePrefix = "/config_diag"
            }
        };
        var configManager = new ConfigManager(config);

        var envMap = new Dictionary<string, string>
        {
            ["DIAG_ROUTE_PREFIX"] = "/env_diag"
        };

        var adapter = new TemporaryDiagnosticsAdapter(configManager, configuration: null, k => envMap.TryGetValue(k, out var v) ? v : null);

        // Act
        var report = adapter.EvaluateEnvironment();

        // Assert: 配置文件的 RoutePrefix 胜出
        Assert.True(adapter.IsEnabled);
        Assert.Equal("/config_diag", adapter.RoutePrefix);
        Assert.Equal("/config_diag", report.RoutePrefix);
    }
}
