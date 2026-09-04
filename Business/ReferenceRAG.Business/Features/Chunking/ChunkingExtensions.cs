using Microsoft.Extensions.DependencyInjection;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Services;

namespace ReferenceRAG.Core.Extensions;

public static class ChunkingExtensions
{
    public static IServiceCollection AddChunking(this IServiceCollection services)
    {
        services.AddSingleton<ITokenizer, SimpleTokenizer>();
        services.AddSingleton<ITextEnhancer, TextEnhancer>();
        services.AddSingleton<IMarkdownChunker, MarkdownChunker>();
        services.AddSingleton<ContentHashDetector>();
        services.AddSingleton<ContextBuilder>();
        services.AddSingleton<ObsidianLinkGenerator>();
        return services;
    }
}
