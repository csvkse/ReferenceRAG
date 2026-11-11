using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Core.Services;

namespace ReferenceRAG.Infrastructure.Features.KnowledgeBase.Storage;

/// <summary>
/// 历史旧数据源自动平滑迁移适配器 (Adapter Pattern)
/// 将 appsettings.json 中陈旧的 sources 自动迁移为 catalog.db 对应知识库中的 LocalFolderConnector
/// </summary>
public static class LegacySourceMigrator
{
    private static int _migratedState = 0;

    public static async Task MigrateLegacySourcesAsync(
        ICatalogStore catalogStore,
        ConfigManager configManager,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _migratedState, 1, 0) != 0)
            return;

        try
        {
            var config = configManager.Load();
            if (config.Sources == null || config.Sources.Count == 0)
                return;

            await catalogStore.InitializeAsync(ct);
            var kbs = await catalogStore.ListKnowledgeBasesAsync(ct);
            var defaultKb = kbs.FirstOrDefault(k => k.Enabled) ?? kbs.FirstOrDefault();

            if (defaultKb == null)
            {
                var newKb = new KnowledgeBaseDefinition(
                    Id: Guid.NewGuid().ToString("N")[..16],
                    Name: "默认知识库",
                    Description: "从旧版本全局数据源自动收敛迁移的默认知识库",
                    Enabled: true,
                    DesiredConfigRevision: 1,
                    EffectiveConfigRevision: 1,
                    EmbeddingSpecId: "default-spec",
                    CreatedAt: DateTime.UtcNow,
                    UpdatedAt: DateTime.UtcNow
                );
                defaultKb = await catalogStore.SaveKnowledgeBaseAsync(newKb, ct);
                logger?.LogInformation("[LegacySourceMigrator] 自动创建承载历史源的默认知识库: {KbId}", defaultKb.Id);
            }

            var existingConnectors = await catalogStore.GetConnectorsByKnowledgeBaseAsync(defaultKb.Id, ct);
            var existingPaths = existingConnectors
                .Select(c => c.TargetPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var src in config.Sources)
            {
                if (string.IsNullOrWhiteSpace(src.Path)) continue;
                string fullPath;
                try
                {
                    fullPath = Path.GetFullPath(src.Path);
                }
                catch
                {
                    fullPath = src.Path;
                }

                if (!existingPaths.Contains(fullPath) && !existingPaths.Contains(src.Path))
                {
                    var filePattern = src.FilePatterns != null && src.FilePatterns.Count > 0
                        ? string.Join(";", src.FilePatterns)
                        : "*.md;*.txt";

                    var connector = new ConnectorDefinition(
                        Id: Guid.NewGuid().ToString("N")[..16],
                        KnowledgeBaseId: defaultKb.Id,
                        Kind: "LocalFolder",
                        TargetPath: fullPath,
                        FilePattern: filePattern,
                        ConfigRevision: 1,
                        Enabled: src.Enabled,
                        CreatedAt: DateTime.UtcNow,
                        UpdatedAt: DateTime.UtcNow
                    );

                    await catalogStore.SaveConnectorAsync(connector, ct);

                    existingPaths.Add(fullPath);
                    existingPaths.Add(src.Path);
                    logger?.LogInformation("[LegacySourceMigrator] 已将旧源 [{Name}] ({Path}) 迁移为知识库连接器", src.Name, fullPath);
                }
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "[LegacySourceMigrator] 迁移旧源过程中捕获到非致命异常");
        }
    }
}
