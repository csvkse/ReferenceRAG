using Microsoft.Extensions.DependencyInjection;
using ReferenceRAG.Business.Features.KnowledgeBase.Services;

namespace ReferenceRAG.Business.Features.KnowledgeBase;

public static class KnowledgeBaseBusinessExtensions
{
    public static IServiceCollection AddKnowledgeBaseServices(this IServiceCollection services)
    {
        services.AddScoped<IKnowledgeBaseService, KnowledgeBaseService>();
        services.AddScoped<IKnowledgeBaseSearchService, KnowledgeBaseSearchService>();
        services.AddScoped<IKnowledgeBaseChatService, KnowledgeBaseChatService>();

        // P2 事件驱动与漂移治理（remaining-plan §1）：进程内同步事件派发 + 投影缓存 + 定时对账。
        services.AddSingleton<ISourceLinkProjectionCache, SourceLinkProjectionCache>();
        services.AddSingleton<ISourceLinkEventHandler, SourceProjectionCacheInvalidationHandler>();
        services.AddSingleton<ISourceLinkEventPublisher, SourceLinkEventPublisher>();
        services.AddSingleton<IReconciliationState, ReconciliationState>();
        services.AddHostedService<ReconciliationJob>();

        // 词法倒排索引后台补齐：查询读路径只投递意图，构建全部在后台完成（消除首查 16 秒阻塞）。
        services.AddHostedService<LexicalIndexBackfillWorker>();
        return services;
    }
}
