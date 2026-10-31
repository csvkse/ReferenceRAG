using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReferenceRAG.Business.Features.KnowledgeBase.Services;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Core.Features.Search.Fusion;
using ReferenceRAG.Service.McpTools;
using Xunit;

namespace ReferenceRAG.Host.Tests;

public class KnowledgeBaseMcpToolsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ServiceProvider _serviceProvider;
    private readonly CatalogStore _catalogStore;
    private readonly KnowledgeBaseStorageManager _storageManager;

    public KnowledgeBaseMcpToolsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ReferenceRAG_McpTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var services = new ServiceCollection();
        var catalogDb = Path.Combine(_tempDir, "system", "catalog.db");
        _catalogStore = new CatalogStore(catalogDb);
        _catalogStore.InitializeAsync().GetAwaiter().GetResult();

        _storageManager = new KnowledgeBaseStorageManager(_tempDir);

        services.AddSingleton<ICatalogStore>(_catalogStore);
        services.AddSingleton<IKnowledgeBaseStorageManager>(_storageManager);
        services.AddSingleton<ISearchFusionStrategy, RrfSearchFusionStrategy>();
        services.AddScoped<IKnowledgeBaseService, KnowledgeBaseService>();
        services.AddScoped<IKnowledgeBaseSearchService, KnowledgeBaseSearchService>();

        _serviceProvider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _storageManager.Dispose();
        _catalogStore.Dispose();
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public async Task KnowledgeBaseMcpTools_ListAndSearch_ShouldWorkProperly()
    {
        var kbService = _serviceProvider.GetRequiredService<IKnowledgeBaseService>();
        var storageManager = _serviceProvider.GetRequiredService<IKnowledgeBaseStorageManager>();

        // 1. 创建测试知识库
        var kb = await kbService.CreateKnowledgeBaseAsync("Mcp Test KB", "For testing MCP protocol");

        // 写入测试文档
        await using (var lease = await storageManager.AcquireLeaseAsync(kb.Id, StorageAccess.ReadWrite))
        {
            await lease.Store.CommitDocumentAsync(new IndexBatch(
                KnowledgeBaseId: kb.Id,
                ExpectedIndexEpoch: 1,
                ExpectedDocumentVersion: 0,
                ConnectorId: "conn_mcp",
                SourceKey: "mcp_spec.md",
                Locator: null,
                SourceRevision: null,
                ContentHash: "mcp_hash",
                Chunks: new[]
                {
                    new ChunkData("chunk_mcp_1", 0, "Model Context Protocol tools allow AI agents to query knowledge bases securely.", 15)
                }
            ));
        }

        var mcpTools = new KnowledgeBaseMcpTools(_serviceProvider);

        // 2. 测试 KnowledgeBaseList
        var listJson = await mcpTools.KnowledgeBaseList();
        using var listDoc = JsonDocument.Parse(listJson);
        Assert.True(listDoc.RootElement.GetProperty("success").GetBoolean());
        Assert.True(listDoc.RootElement.GetProperty("count").GetInt32() >= 1);

        // 3. 测试 KnowledgeBaseSearch
        var searchJson = await mcpTools.KnowledgeBaseSearch("Model Context Protocol", topK: 3);
        using var searchDoc = JsonDocument.Parse(searchJson);
        Assert.True(searchDoc.RootElement.GetProperty("success").GetBoolean());
        var count = searchDoc.RootElement.GetProperty("count").GetInt32();
        Assert.Equal(1, count);

        var items = searchDoc.RootElement.GetProperty("items");
        var firstItem = items[0];
        Assert.Equal("chunk_mcp_1", firstItem.GetProperty("chunkId").GetString());
        Assert.Contains("Model Context Protocol", firstItem.GetProperty("text").GetString());
    }
}
