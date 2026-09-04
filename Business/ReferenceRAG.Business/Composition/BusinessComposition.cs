using Microsoft.Extensions.DependencyInjection;
using ReferenceRAG.Core.Extensions;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Service.Services;
using ReferenceRAG.Storage.Extensions;

namespace ReferenceRAG.Business.Composition;

public static class BusinessComposition
{
    public static IServiceCollection AddRagBusiness(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<ConfigManager>();
        services.AddSingleton<ReferenceRAG.Infrastructure.Features.Database.DataDirectoryLease>();
        var options = new HybridSearchOptions();
        configuration.GetSection("HybridSearch").Bind(options);
        try { options.Validate(); }
        catch (Exception ex) { Console.WriteLine($"Invalid HybridSearch configuration: {ex.Message}"); options = new(); }
        services.AddRagStorage().AddChunking().AddModelManagement().AddFileMonitor()
            .AddSearch(options).AddIndexingPipeline();
        services.AddSingleton<IndexService>();
        services.AddHostedService(sp => sp.GetRequiredService<IndexService>());
        services.AddHostedService<AutoIndexService>();
        services.AddHostedService<StartupSyncService>();
        services.AddHostedService<OrphanCleanupService>();
        services.AddHttpClient();
        services.AddSingleton<MafChatService>();
        return services;
    }
}
