using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReferenceRAG.Core.Features.ModelManagement.StateMachine;
using ReferenceRAG.Core.Features.ModelManagement.Telemetry;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Service.McpTools;
using Xunit;

namespace ReferenceRAG.Host.Tests;

/// <summary>
/// G-M11：配置面与调用面的降级判断必须一致。
///
/// 背景（生产实测）：<c>rerank_results</c> 已如实报 <c>degraded=true</c>，
/// 但 <c>get_system_stats.rerank.health</c> 仍是 <c>unknown</c> —— 因为 provider 只注册为
/// <see cref="IRerankService"/>，直接 <c>GetService&lt;IRerankRuntimeTelemetryProvider&gt;()</c> 恒为 null。
/// 只靠接口断言发现不了，必须显式锁住解析路径。
/// </summary>
public sealed class McpRerankHealthGateTests
{
    private sealed class DegradedRerank : IRerankService, IRerankRuntimeTelemetryProvider
    {
        public string ModelName => "gguf-degraded";
        public bool IsLoaded => true;
        public bool IsDegraded => true;
        public string? DegradationReason => "GGUF 重排模型缺少 Rank 分类头";
        public string ModelPath => "/models/reranker.gguf";
        public bool UseCuda => false;
        public int DeviceId => 0;

        public ModelLifecycleSnapshot GetLifecycleSnapshot() => new() { State = ModelState.Ready, IsReady = true };
        public Task<double> RerankAsync(string query, string document, System.Threading.CancellationToken ct = default)
            => Task.FromResult(double.NaN);
        public Task<RerankResult> RerankBatchAsync(string query, IEnumerable<string> documents, System.Threading.CancellationToken ct = default)
            => Task.FromResult(new RerankResult { Query = query, Degraded = true, DegradationReason = DegradationReason });
        public Task<bool> ReloadModelAsync(string modelPath, string modelName) => Task.FromResult(true);
        public void UnloadModel() { }
    }

    [Fact]
    public void Resolver_FindsProviderRegisteredOnlyAsRerankService()
    {
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        // 模拟生产：只有 IRerankService 注册，IRerankRuntimeTelemetryProvider 未单独注册。
        var sp = new ServiceCollection()
            .AddSingleton<IRerankService>(new DegradedRerank())
            .BuildServiceProvider();

        var resolved = SourceManagementTools.ResolveRerankTelemetry(sp);

        Assert.NotNull(resolved);
        Assert.True(resolved!.IsDegraded);
        Assert.False(string.IsNullOrWhiteSpace(resolved.DegradationReason));
    }

    [Fact]
    public void Resolver_ReturnsNullWhenNoRerankService()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        Assert.Null(SourceManagementTools.ResolveRerankTelemetry(sp));
    }
}
