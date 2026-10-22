using System.Collections.Generic;
using ReferenceRAG.ApiHost.Diagnostics;
using Xunit;

namespace ReferenceRAG.Host.Tests;

public class DiagnosticsLifecycleTests
{
    [Fact]
    public void WhenNoDiagKeyEnvironmentVariable_StateIsUnconfigured_And_Disabled()
    {
        // Arrange
        var envMap = new Dictionary<string, string>();
        var adapter = new TemporaryDiagnosticsAdapter(k => envMap.TryGetValue(k, out var v) ? v : null);

        // Act
        var report = adapter.EvaluateEnvironment();

        // Assert
        Assert.False(adapter.IsEnabled);
        Assert.False(report.IsEnabled);
        Assert.Equal(DiagnosticsLifecycleState.Unconfigured, adapter.CurrentState);
        Assert.Contains("未配置环境变量 DIAG_KEY", report.StatusMessage);
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
        var adapter = new TemporaryDiagnosticsAdapter(k => envMap.TryGetValue(k, out var v) ? v : null);

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
    public void WhenValidDiagKeyProvided_StateIsArmedAndActive(string varName, string strongKey)
    {
        // Arrange
        var envMap = new Dictionary<string, string>
        {
            [varName] = strongKey
        };
        var adapter = new TemporaryDiagnosticsAdapter(k => envMap.TryGetValue(k, out var v) ? v : null);

        // Act
        var report = adapter.EvaluateEnvironment();

        // Assert: 成功激活
        Assert.True(adapter.IsEnabled);
        Assert.True(report.IsEnabled);
        Assert.Equal(DiagnosticsLifecycleState.ArmedAndActive, adapter.CurrentState);
        Assert.Equal("/__diag", adapter.RoutePrefix);
        Assert.Contains("诊断中间件已就绪", report.StatusMessage);
    }

    [Fact]
    public void CustomRoutePrefix_ViaEnvironmentVariable_IsAppliedCorrectly()
    {
        // Arrange
        var envMap = new Dictionary<string, string>
        {
            ["DIAG_KEY"] = "ValidDiagKey123456!",
            ["DIAG_ROUTE_PREFIX"] = "/my_ops_diag"
        };
        var adapter = new TemporaryDiagnosticsAdapter(k => envMap.TryGetValue(k, out var v) ? v : null);

        // Act
        var report = adapter.EvaluateEnvironment();

        // Assert
        Assert.True(adapter.IsEnabled);
        Assert.Equal("/my_ops_diag", adapter.RoutePrefix);
        Assert.Equal("/my_ops_diag", report.RoutePrefix);
    }
}
