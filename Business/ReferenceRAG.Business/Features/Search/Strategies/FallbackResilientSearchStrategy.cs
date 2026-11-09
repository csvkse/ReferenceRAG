using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ReferenceRAG.Business.Features.Search.StateMachine;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Infrastructure.Features.Resilience;

namespace ReferenceRAG.Business.Features.Search.Strategies;

/// <summary>
/// 容灾降级组合策略装饰器 (Fallback Resilient Search Strategy Decorator)
/// 自动守护主策略执行：当向量服务超时、网络异常、429限流、模型未就绪或被配置显式禁用时，
/// 拦截异常，记录状态机降级跃迁，平滑执行备选词法策略，保障检索服务 100% 可用。
/// </summary>
public class FallbackResilientSearchStrategy : ISearchExecutionStrategy
{
    private readonly ISearchExecutionStrategy _primaryStrategy;
    private readonly ISearchExecutionStrategy _fallbackStrategy;
    private readonly ResilienceCircuitBreaker _circuitBreaker;

    public FallbackResilientSearchStrategy(
        ISearchExecutionStrategy primaryStrategy,
        ISearchExecutionStrategy fallbackStrategy,
        ResilienceCircuitBreaker circuitBreaker)
    {
        _primaryStrategy = primaryStrategy ?? throw new ArgumentNullException(nameof(primaryStrategy));
        _fallbackStrategy = fallbackStrategy ?? throw new ArgumentNullException(nameof(fallbackStrategy));
        _circuitBreaker = circuitBreaker ?? throw new ArgumentNullException(nameof(circuitBreaker));
    }

    public ISearchExecutionStrategy PrimaryStrategy => _primaryStrategy;
    public ISearchExecutionStrategy FallbackStrategy => _fallbackStrategy;
    public QueryMode Mode => _primaryStrategy.Mode;

    public async Task<SearchStrategyResult> ExecuteAsync(
        SearchExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        // 门禁 1：若静态配置中全局禁用了 Embedding，直接零等待走备选词法策略
        if (!context.Config.Indexing.EmbeddingEnabled)
        {
            return await ExecuteFallbackAsync(context, "全局配置已禁用 Embedding 向量", cancellationToken);
        }

        // 门禁 2：检查断路器状态，若处于熔断状态，快速短路走备选词法策略
        if (_circuitBreaker.IsOpen)
        {
            return await ExecuteFallbackAsync(context, "向量模型端点处于熔断保护状态 (连续故障冷却中)", cancellationToken);
        }

        // 门禁 3：为云端/主策略分配超时预算
        var timeoutMs = context.Config.Search?.EmbeddingTimeoutMs > 0
            ? context.Config.Search.EmbeddingTimeoutMs
            : 2500;

        using var timeoutCts = new CancellationTokenSource(timeoutMs);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            var result = await _primaryStrategy.ExecuteAsync(context, linkedCts.Token);
            _circuitBreaker.RecordSuccess();
            return result;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _circuitBreaker.RecordFailure();
            return await ExecuteFallbackAsync(context, $"向量检索超时 ({timeoutMs}ms)", cancellationToken);
        }
        catch (Exception ex) when (IsRecoverableOrTransient(ex))
        {
            _circuitBreaker.RecordFailure();
            return await ExecuteFallbackAsync(context, $"向量服务异常: {ex.Message}", cancellationToken);
        }
    }

    private async Task<SearchStrategyResult> ExecuteFallbackAsync(
        SearchExecutionContext context,
        string reason,
        CancellationToken cancellationToken)
    {
        context.StateMachine.TransitionTo(SearchPipelinePhase.Degrading, 0, 0, $"容灾降级生效: {reason}");
        context.StateMachine.RecordDegradation(reason);

        var fallbackResult = await _fallbackStrategy.ExecuteAsync(context, cancellationToken);

        context.StateMachine.TransitionTo(SearchPipelinePhase.DegradedRetrieved, 0, fallbackResult.Results.Count, "降级词法召回完毕");
        return fallbackResult;
    }

    private static bool IsRecoverableOrTransient(Exception ex) =>
        ex is HttpRequestException or TimeoutException or SearchCapabilityException or InvalidOperationException;
}
