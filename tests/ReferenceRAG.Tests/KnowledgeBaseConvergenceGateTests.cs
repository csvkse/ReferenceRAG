using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using ReferenceRAG.Business.Features.KnowledgeBase.Services;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Core.Features.Search.Fusion;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Infrastructure.Features.KnowledgeBase.Storage;
using ReferenceRAG.Storage;
using Moq;
using Xunit;

namespace ReferenceRAG.Tests;

/// <summary>
/// 架构收敛与知识库独立自治质量门禁套件 (Quality Gates)
/// 覆盖：
/// 1. BM25 真实切片统计门禁（纯词法知识库严禁返回 0 或伪造向量数）
/// 2. 无向量知识库平滑降级门禁（严禁抛出 503 异常）
/// 3. 历史数据源自动迁移门禁（配置文件不可变与连接器自动入库）
/// 4. 统一检索门面跨库联合召回门禁（默认检索自动覆盖知识库）
/// </summary>
public class KnowledgeBaseConvergenceGateTests : IDisposable
{
    private readonly string _tempDir;

    public KnowledgeBaseConvergenceGateTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ReferenceRAG_ConvergenceTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
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
    public async Task Gate1_BM25Summary_ShouldReportActualChunkCounts_EvenWhenVectorsAreZero()
    {
        // Arrange: 创建包含 2 个切片但 0 向量的纯词法知识库
        var kbDbPath = Path.Combine(_tempDir, "kb_lexical.db");
        var store = new SqliteKnowledgeBaseStore("kb_test_zero_vec", kbDbPath);
        await store.InitializeAsync();

        var batch = new IndexBatch(
            KnowledgeBaseId: "kb_test_zero_vec",
            ExpectedIndexEpoch: 1,
            ExpectedDocumentVersion: 1,
            ConnectorId: "conn_1",
            SourceKey: "三联版/神雕侠侣.md",
            Locator: "loc1",
            SourceRevision: "rev1",
            ContentHash: "hash_sdxl",
            Chunks: new List<ChunkData>
            {
                new ChunkData(
                    ChunkId: "c1",
                    Ordinal: 0,
                    Text: "小龙女白衣胜雪，姿容秀美绝伦，自幼在终南山古墓中长大。",
                    TokenCount: 30
                ),
                new ChunkData(
                    ChunkId: "c2",
                    Ordinal: 1,
                    Text: "杨过抬头看去，只见小龙女脸色苍白，但眼神清澈柔和。",
                    TokenCount: 25
                )
            }
        );

        await store.CommitDocumentAsync(batch);

        // Act: 验证真实物理词法统计
        var stats = await store.GetLexicalStatsAsync();

        // Assert: 必须返回真实非零数字，绝不能是 0！
        Assert.Equal(2, stats.TotalChunks);
        Assert.Equal(1, stats.TotalDocuments);
        Assert.True(stats.AverageDocumentLength > 20.0);
        Assert.True(stats.TotalVocabularySize > 0);
    }

    [Fact]
    public async Task Gate2_ZeroVectorKnowledgeBase_ShouldSmoothlyDegradeToLexical_Without503Exception()
    {
        // Arrange: 创建包含“小龙女”内容的无向量知识库
        var kbDbPath = Path.Combine(_tempDir, "kb_graceful_fallback.db");
        var store = new SqliteKnowledgeBaseStore("kb_fallback_test", kbDbPath);
        await store.InitializeAsync();

        var batch = new IndexBatch(
            KnowledgeBaseId: "kb_fallback_test",
            ExpectedIndexEpoch: 1,
            ExpectedDocumentVersion: 1,
            ConnectorId: "conn_1",
            SourceKey: "神雕侠侣.md",
            Locator: "loc1",
            SourceRevision: "rev1",
            ContentHash: "hash_123",
            Chunks: new List<ChunkData>
            {
                new ChunkData(
                    ChunkId: "c_xl_1",
                    Ordinal: 0,
                    Text: "小龙女一生爱穿白衣，清冷绝俗，与杨过相伴终南山。",
                    TokenCount: 28
                )
            }
        );
        await store.CommitDocumentAsync(batch);

        // Act: 模拟请求传入了 QueryVector（例如混合搜索模式），但底层知识库完全没有向量
        var dummyVector = new float[1024];
        dummyVector[0] = 0.5f;

        // 验证：绝对不能抛出 InvalidOperationException（503 报错）！必须平滑降级并返回词法命中
        var recallResult = await store.RecallAsync(new RecallRequest(
            Query: "小龙女",
            TopK: 10,
            QueryVector: dummyVector,
            UseLexicalTokenizer: true,
            AllowDegrade: true
        ));

        // Assert:
        Assert.NotNull(recallResult);
        Assert.True(recallResult.Degraded);
        Assert.Contains("降级", recallResult.DegradationReason);
        Assert.Single(recallResult.Items);
        Assert.Equal("c_xl_1", recallResult.Items[0].ChunkId);
        Assert.Contains("小龙女", recallResult.Items[0].Text);
    }

    [Fact]
    public async Task Gate3_LegacySourceMigrator_ShouldAutoMigrateSources_ToConnectors()
    {
        // Arrange: 设置初始配置包含 2 个旧数据源
        var catalogDbPath = Path.Combine(_tempDir, "catalog.db");
        using var catalog = new CatalogStore(catalogDbPath);
        await catalog.InitializeAsync();

        var dummySourcePath1 = Path.Combine(_tempDir, "source1");
        var dummySourcePath2 = Path.Combine(_tempDir, "source2");
        Directory.CreateDirectory(dummySourcePath1);
        Directory.CreateDirectory(dummySourcePath2);

        var config = new ObsidianRagConfig
        {
            Sources = new List<SourceFolder>
            {
                new SourceFolder { Name = "Novel1", Path = dummySourcePath1, Enabled = true },
                new SourceFolder { Name = "Novel2", Path = dummySourcePath2, Enabled = true }
            }
        };
        var configManager = new ConfigManager(config);

        // Act: 执行迁移
        await LegacySourceMigrator.MigrateLegacySourcesAsync(catalog, configManager);

        // Assert: 检查 catalog 中自动创建了默认知识库与连接器
        var kbs = await catalog.ListKnowledgeBasesAsync();
        Assert.NotEmpty(kbs);
        var kb = kbs.First();

        var connectors = await catalog.GetConnectorsByKnowledgeBaseAsync(kb.Id);
        Assert.Equal(2, connectors.Count);
        Assert.Contains(connectors, c => c.TargetPath.Equals(Path.GetFullPath(dummySourcePath1), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(connectors, c => c.TargetPath.Equals(Path.GetFullPath(dummySourcePath2), StringComparison.OrdinalIgnoreCase));

        // 幂等性测试：重复运行不会重复创建
        await LegacySourceMigrator.MigrateLegacySourcesAsync(catalog, configManager);
        var connectorsAfter = await catalog.GetConnectorsByKnowledgeBaseAsync(kb.Id);
        Assert.Equal(2, connectorsAfter.Count);
    }

    [Fact]
    public async Task Gate4_SearchService_FederatesKnowledgeBases_WhenSourcesEmpty()
    {
        // Arrange: 构造 Mock 知识库搜索服务，包含《神雕侠侣》切片
        var mockKbSearch = new Mock<IKnowledgeBaseSearchService>();
        mockKbSearch.Setup(k => k.SearchAsync(It.IsAny<CrossKnowledgeBaseSearchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CrossKnowledgeBaseSearchResponse(
                Query: "小龙女",
                Items: new List<RecallItem>
                {
                    new RecallItem(
                        ChunkId: "chunk_sdxl_101",
                        DocumentId: "doc_sdxl",
                        SourceKey: "三联版/神雕侠侣.md",
                        Text: "忽听得门外一个女子声音说道：‘过儿，是你么？’正是小龙女的声音。",
                        Score: 12.5,
                        ScoreKind: ScoreKind.BM25,
                        DocumentVersion: 1,
                        ConnectorId: "conn_jinyong"
                    )
                },
                SuccessfulKnowledgeBaseIds: new List<string> { "kb_jinyong" },
                FailedKnowledgeBaseIds: new List<string>(),
                PartialSuccess: false,
                ScoreKind: ScoreKind.BM25,
                Elapsed: TimeSpan.FromMilliseconds(15)
            ));

        var mockCatalog = new Mock<ICatalogStore>();
        mockCatalog.Setup(c => c.ListKnowledgeBasesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<KnowledgeBaseDefinition>
            {
                new KnowledgeBaseDefinition("kb_jinyong", "金庸武侠知识库", null, true, 1, 1, "spec", DateTime.UtcNow, DateTime.UtcNow)
            });

        var config = new ObsidianRagConfig
        {
            Search = new SearchConfig { EnableLexical = true },
            Sources = new List<SourceFolder>() // 模拟全局源为空或不含神雕侠侣
        };

        var searchService = new SearchService(
            vectorStore: Mock.Of<IVectorStore>(),
            embeddingService: Mock.Of<IEmbeddingService>(),
            textEnhancer: Mock.Of<ITextEnhancer>(),
            configManager: new ConfigManager(config),
            logger: NullLogger<SearchService>.Instance,
            hybridSearchService: null,
            rerankService: null,
            graphStore: null,
            telemetryService: null,
            lexicalFactory: null,
            hasPersistedLexicalChanges: null,
            bm25StoreFactory: null,
            kbSearchService: mockKbSearch.Object,
            catalogStore: mockCatalog.Object
        );

        // Act: 发起默认词法查询（不指定 Sources）
        var request = new AIQueryRequest
        {
            Query = "小龙女",
            Mode = QueryMode.Lexical,
            TopK = 10
        };

        var response = await searchService.SearchAsync(request);

        // Assert: 必须自动联合召回知识库内容，绝不为空！
        Assert.NotNull(response);
        Assert.Single(response.Chunks);
        Assert.Equal("chunk_sdxl_101", response.Chunks[0].RefId);
        Assert.Contains("小龙女", response.Chunks[0].Content);
        Assert.Equal("神雕侠侣", response.Chunks[0].Title);
        Assert.Single(response.Files);
        Assert.Equal("神雕侠侣", response.Files[0].Title);
        Assert.True(response.Stats.TotalMatches >= 1);
        Assert.False(string.IsNullOrWhiteSpace(response.Context));
        Assert.Contains("小龙女", response.Context);
    }
}
