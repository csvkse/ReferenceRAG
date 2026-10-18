using Microsoft.Extensions.DependencyInjection;
using ReferenceRAG.Business.Features.Indexing.Adapters;
using ReferenceRAG.Business.Features.Indexing.Services;
using ReferenceRAG.Business.Features.Indexing.Strategies;
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
        services.AddSingleton<IFileProcessingGuard, FileProcessingGuard>();
        services.AddSingleton<IFileIndexPipeline, FileIndexPipeline>();

        // 索引架构三剑客：存储适配器、执行策略与状态机工厂
        services.AddSingleton<IIndexStorageAdapter, SqliteIndexStorageAdapter>();
        services.AddSingleton<IIndexExecutionStrategy, VectorOnlyIndexStrategy>();
        services.AddSingleton<IIndexExecutionStrategy, RechunkIndexStrategy>();
        services.AddSingleton<IIndexExecutionStrategy, RebuildIndexStrategy>();
        services.AddSingleton<IIndexExecutionStrategy, IncrementalIndexStrategy>();
        services.AddSingleton<IndexStrategyFactory>();

        // 入库防噪架构：准入适配器、处置策略与准入网关服务
        services.AddSingleton<SeparatorNoiseAdapter>();
        services.AddSingleton<InformationDensityAdapter>();
        services.AddSingleton<IChunkQualificationAdapter>(sp =>
            new CompositeChunkQualificationAdapter(new IChunkQualificationAdapter[]
            {
                sp.GetRequiredService<SeparatorNoiseAdapter>(),
                sp.GetRequiredService<InformationDensityAdapter>()
            }));
        services.AddSingleton<IChunkGatekeeperService, ReferenceRAG.Business.Features.Indexing.Services.ChunkGatekeeperService>();

        return services;
    }
}
