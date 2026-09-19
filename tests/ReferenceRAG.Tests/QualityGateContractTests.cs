using System.Reflection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReferenceRAG.Core.Helpers;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Service.Services;
using ReferenceRAG.Storage;
using Xunit;

namespace ReferenceRAG.Tests;

/// <summary>
/// 质量门禁契约测试：固化架构约束、通信协议与数据安全边界，防止历史缺陷回归。
/// </summary>
public class QualityGateContractTests : IDisposable
{
    private readonly string _testDir;

    public QualityGateContractTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"qg-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, true);
        }
        catch { }
    }

    // ── 1. 架构门禁：MafChatService 内部服务调用绝不能走本地 HTTP 环路 ──
    [Fact]
    public void Architecture_MafChatService_HasNoInternalHttpLoopbackCalls()
    {
        // 检查 MafChatService 源码文件，严禁引入 Rag.LocalApi 或本地端口客户端
        var baseDir = AppContext.BaseDirectory;
        var solutionRoot = FindSolutionRoot(baseDir);
        Assert.NotNull(solutionRoot);

        var mafChatFile = Path.Combine(solutionRoot, "Business", "ReferenceRAG.Business", "Features", "Chat", "Services", "MafChatService.cs");
        Assert.True(File.Exists(mafChatFile), $"未找到 MafChatService.cs: {mafChatFile}");

        var code = File.ReadAllText(mafChatFile);
        Assert.DoesNotContain("Rag.LocalApi", code);
        Assert.DoesNotContain("_localBaseUrl", code);

        // 通过反射检查内部工具方法，确认其不依赖 HttpClient 作为工具调用通道
        var methods = typeof(MafChatService).GetMethods(BindingFlags.NonPublic | BindingFlags.Instance);
        var toolMethodNames = new[] { "SearchKnowledgeAsync", "DrillDownChunkAsync", "BM25SearchAsync", "SearchNoteByTitleAsync", "GetFileStructureAsync", "ReadFileContentAsync", "GetIndexStatusAsync" };
        foreach (var name in toolMethodNames)
        {
            var m = methods.FirstOrDefault(x => x.Name == name);
            Assert.NotNull(m);
            var parameters = m.GetParameters();
            Assert.DoesNotContain(parameters, p => p.ParameterType == typeof(HttpClient));
        }
    }

    // ── 2. 协议门禁：TrimHistory 截断后首条非系统消息必须且严格为 User 角色 ──
    [Fact]
    public void ChatProtocol_TrimHistory_GuaranteesUserRoleImmediatelyAfterSystem()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Chat:MaxMessagesPerSession"] = "4"
            })
            .Build();

        var services = new ServiceCollection()
            .AddLogging()
            .AddHttpClient()
            .BuildServiceProvider();

        var service = new MafChatService(
            configuration,
            services.GetRequiredService<IServiceScopeFactory>(),
            services.GetRequiredService<IHttpClientFactory>(),
            NullLogger<MafChatService>.Instance);

        // 构造超长历史：System -> User -> Assistant(ToolCall) -> Tool(Resp) -> Assistant -> User -> Assistant
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "system prompt"),
            new(ChatRole.User, "user query 1"),
            new(ChatRole.Assistant, "calling tool"),
            new(ChatRole.Tool, "tool result"),
            new(ChatRole.Assistant, "thought intermediate"),
            new(ChatRole.User, "user query 2"),
            new(ChatRole.Assistant, "final answer")
        };

        service.TrimHistory(messages);

        Assert.NotNull(messages);
        Assert.True(messages.Count >= 2);
        Assert.Equal(ChatRole.System, messages[0].Role);
        // 关键断言：无论截断点落在何处，System 后的第一条消息必须是 User
        Assert.Equal(ChatRole.User, messages[1].Role);
        Assert.Equal("user query 2", messages[1].Text);
    }

    [Fact]
    public void ChatProtocol_TrimHistory_WhenTruncationFallsOnAssistantOrTool_PrunesUntilUser()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Chat:MaxMessagesPerSession"] = "3"
            })
            .Build();

        var services = new ServiceCollection().AddLogging().AddHttpClient().BuildServiceProvider();
        var service = new MafChatService(
            configuration,
            services.GetRequiredService<IServiceScopeFactory>(),
            services.GetRequiredService<IHttpClientFactory>(),
            NullLogger<MafChatService>.Instance);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "system"),
            new(ChatRole.User, "u1"),
            new(ChatRole.Assistant, "a1"),
            new(ChatRole.Tool, "t1"),
            new(ChatRole.Assistant, "a2"),
            new(ChatRole.User, "u2")
        };

        service.TrimHistory(messages);

        Assert.Equal(ChatRole.System, messages[0].Role);
        Assert.Equal(ChatRole.User, messages[1].Role);
        Assert.Equal("u2", messages[1].Text);
    }

    // ── 3. 检索召回门禁：文件夹过滤时绝不因前置非目标候选而早退饥饿 ──
    [Fact]
    public async Task SearchRecall_WithFoldersFilter_OnlyCollectsMatchingFoldersUpToTopK()
    {
        var mockVectorStore = new Mock<IVectorStore>();
        var mockEmbeddingService = new Mock<IEmbeddingService>();
        var mockTextEnhancer = new Mock<ITextEnhancer>();

        mockEmbeddingService.Setup(e => e.EncodeAsync(It.IsAny<string>(), It.IsAny<EmbeddingMode>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new float[128]);
        mockEmbeddingService.Setup(e => e.ModelName).Returns("test-model");

        // 构造候选集：前 20 个为 /Other 目录，后 10 个为目标目录，再 20 个为前缀相似目录（假匹配）
        var pool = new List<SearchResult>();
        for (int i = 0; i < 20; i++)
        {
            pool.Add(new SearchResult
            {
                ChunkId = $"c_other_{i}",
                FilePath = $"D:/Vault/OtherFolder/doc_{i}.md",
                Source = "Vault",
                Score = 0.95f - (i * 0.01f)
            });
        }
        for (int i = 0; i < 10; i++)
        {
            pool.Add(new SearchResult
            {
                ChunkId = $"c_target_{i}",
                FilePath = $"D:/Vault/TargetFolder/sub/doc_{i}.md",
                Source = "Vault",
                Score = 0.70f - (i * 0.01f)
            });
        }
        for (int i = 0; i < 20; i++)
        {
            pool.Add(new SearchResult
            {
                // TargetFolder-FalseMatch 不应被 TargetFolder 匹配（边界检查）
                ChunkId = $"c_false_{i}",
                FilePath = $"D:/Vault/TargetFolder-FalseMatch/doc_{i}.md",
                Source = "Vault",
                Score = 0.50f - (i * 0.01f)
            });
        }

        int capturedLimit = 0;
        mockVectorStore.Setup(v => v.SearchAsync(It.IsAny<float[]>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<float[], string, int, CancellationToken>((_, _, limit, _) => capturedLimit = limit)
            .ReturnsAsync(pool);

        var originalDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_testDir);
        ConfigManager configManager;
        try
        {
            configManager = new ConfigManager();
            var cfg = configManager.Load();
            cfg.Sources = [new SourceFolder { Name = "Vault", Path = "D:/Vault", Enabled = true }];
            configManager.Save(cfg);
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDir);
        }

        var searchService = new SearchService(
            mockVectorStore.Object,
            mockEmbeddingService.Object,
            mockTextEnhancer.Object,
            configManager,
            NullLogger<SearchService>.Instance);

        var request = new AIQueryRequest
        {
            Query = "test query",
            TopK = 10,
            Mode = QueryMode.Quick,
            Sources = new List<string> { "Vault" },
            Filters = new SearchFilter
            {
                // 用反斜杠传入，验证跨平台路径标准化
                Folders = new List<string> { "D:\\Vault\\TargetFolder" }
            }
        };

        var response = await searchService.SearchAsync(request);

        // 契约断言 1：深度候选限制被放大（Math.Max(topK * 5, 50) => 50）
        Assert.True(capturedLimit >= 50, $"候选上限 {capturedLimit} 低于门禁要求的 50");

        // 契约断言 2：虽然前 20 个候选都不匹配，但必须完整收集到目标目录全部 10 条结果
        Assert.Equal(10, response.Chunks.Count);
        foreach (var c in response.Chunks)
        {
            Assert.StartsWith("D:/Vault/TargetFolder/", c.FilePath);
            Assert.DoesNotContain("FalseMatch", c.FilePath);
        }
    }

    // ── 4. 图谱生命周期门禁：重建必须彻底清空、孤儿清理必须清除度数为 0 的 Tag 与 External ──
    [Fact]
    public async Task GraphRebuild_And_OrphanCleanup_Degree0TagsAndExternals()
    {
        var dbFile = Path.Combine(_testDir, "graph-gate.db");
        using var store = new SqliteGraphStore(dbFile);

        // 1. 验证 ClearAllAsync 清空契约（重建前必须清空避免幽灵节点）
        await store.UpsertNodeAsync(new GraphNode { Id = "old.md", Title = "Old Doc", Type = "document" });
        await store.UpsertEdgesAsync(new[] { new GraphEdge { FromId = "old.md", ToId = "#oldtag", Type = "tag" } });
        var statsBefore = await store.GetStatsAsync();
        Assert.True(statsBefore.NodeCount > 0);

        await store.ClearAllAsync();
        var statsCleared = await store.GetStatsAsync();
        Assert.Equal(0, statsCleared.NodeCount);
        Assert.Equal(0, statsCleared.EdgeCount);

        // 2. 写入测试拓扑：
        // - doc1.md -> #active_tag (有边连接)
        // - #orphan_tag (无边孤儿 tag)
        // - ext_orphan (无边孤儿 external)
        // - deleted.md (将被视为已被文件系统删除的孤儿 document)
        // - deleted.md#H1 (deleted.md 下的 heading)
        await store.UpsertNodeAsync(new GraphNode { Id = "doc1.md", Title = "Doc 1", Type = "document" });
        await store.UpsertNodeAsync(new GraphNode { Id = "#active_tag", Title = "#active_tag", Type = "tag" });
        await store.UpsertNodeAsync(new GraphNode { Id = "#orphan_tag", Title = "#orphan_tag", Type = "tag" });
        await store.UpsertNodeAsync(new GraphNode { Id = "ext_orphan", Title = "External Orphan", Type = "external" });
        await store.UpsertNodeAsync(new GraphNode { Id = "deleted.md", Title = "Deleted Doc", Type = "document" });
        await store.UpsertNodeAsync(new GraphNode { Id = "deleted.md#H1", Title = "Heading 1", Type = "heading" });

        await store.UpsertEdgesAsync(new[]
        {
            new GraphEdge { FromId = "doc1.md", ToId = "#active_tag", Type = "tag" }
        });

        // 仅 doc1.md 属于有效文件
        var cleanedCount = await store.CleanupOrphanNodesAsync(new[] { "doc1.md" });
        Assert.True(cleanedCount > 0);

        // 验证保留与清理
        var activeDoc = await store.GetNodeAsync("doc1.md");
        Assert.NotNull(activeDoc);

        var activeTag = await store.GetNodeAsync("#active_tag");
        Assert.NotNull(activeTag);

        var orphanTag = await store.GetNodeAsync("#orphan_tag");
        Assert.Null(orphanTag); // 度数为 0 的孤儿 tag 必须被清理

        var orphanExt = await store.GetNodeAsync("ext_orphan");
        Assert.Null(orphanExt); // 度数为 0 的外部孤儿必须被清理

        var deletedDoc = await store.GetNodeAsync("deleted.md");
        Assert.Null(deletedDoc);

        var deletedHeading = await store.GetNodeAsync("deleted.md#H1");
        Assert.Null(deletedHeading);
    }

    // ── 5. 设置持久化门禁：配置保存必须完整保留 Sources 列表 ──
    [Fact]
    public void Settings_ConfigManager_PreservesConfiguredProperties()
    {
        var originalDir = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(_testDir);
            var cm = new ConfigManager();
            var config = new ObsidianRagConfig
            {
                DataPath = "custom_data",
                Sources =
                [
                    new SourceFolder { Name = "MainVault", Path = "D:/Vault1", Enabled = true },
                    new SourceFolder { Name = "SecondaryVault", Path = "D:/Vault2", Enabled = false }
                ]
            };
            config.Embedding.ModelName = "bge-m3";
            cm.Save(config);

            var loaded = cm.Load();
            Assert.Equal(2, loaded.Sources.Count);
            Assert.Equal("MainVault", loaded.Sources[0].Name);
            Assert.Equal("SecondaryVault", loaded.Sources[1].Name);
            Assert.Equal("bge-m3", loaded.Embedding.ModelName);
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDir);
        }
    }

    private static string? FindSolutionRoot(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ReferenceRAG.slnx")) ||
                File.Exists(Path.Combine(dir.FullName, "ReferenceRAG.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
