using Microsoft.Extensions.DependencyInjection;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Core.Services.Graph;

namespace ReferenceRAG.Core.Extensions;

public static class IndexingExtensions
{
    public static IServiceCollection AddIndexingPipeline(this IServiceCollection services)
    {
        services.AddSingleton<WikiLinkExtractor>();
        services.AddSingleton<IGraphIndexingService, GraphIndexingService>();
        services.AddSingleton<FileProcessingGuard>();
        services.AddSingleton<IFileIndexPipeline, FileIndexPipeline>();
        return services;
    }
}
