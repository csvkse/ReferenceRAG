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
        return services;
    }
}
