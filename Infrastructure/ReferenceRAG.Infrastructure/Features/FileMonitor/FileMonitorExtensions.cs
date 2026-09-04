using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Services;

namespace ReferenceRAG.Core.Extensions;

public static class FileMonitorExtensions
{
    public static IServiceCollection AddFileMonitor(this IServiceCollection services)
    {
        services.AddSingleton<IFileChangeDetector>(sp =>
        {
            var configManager = sp.GetRequiredService<ConfigManager>();
            var config = configManager.Load();
            var firstSource = config.Sources.FirstOrDefault();
            return new FileChangeDetector(
                firstSource?.Path ?? Directory.GetCurrentDirectory(),
                config.Indexing?.DebounceMs ?? 500,
                firstSource?.FilePatterns);
        });

        services.AddSingleton<IFileMonitorService>(sp =>
        {
            var configManager = sp.GetRequiredService<ConfigManager>();
            var config = configManager.Load();
            var debounceMs = config.Indexing?.DebounceMs ?? 500;
            var logger = sp.GetService<ILogger<FileMonitorService>>();
            return new FileMonitorService(debounceMs, logger);
        });

        return services;
    }
}
