using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReferenceRAG.Core.Features.ModelManagement.Telemetry;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Service.Controllers;
using ReferenceRAG.Service.Services;
using Xunit;

namespace ReferenceRAG.Host.Tests;

[Collection("CurrentDirectoryCollection")]
public class DisabledSourceGatekeeperTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _originalDir;
    private readonly Mock<IVectorStore> _vectorStoreMock;
    private readonly Mock<IEmbeddingService> _embeddingMock;
    private readonly Mock<ITextEnhancer> _enhancerMock;
    private readonly Mock<IHybridSearchService> _hybridMock;
    private readonly Mock<IFileIndexPipeline> _pipelineMock;
    private readonly Mock<IFileMonitorService> _fileMonitorMock;

    public DisabledSourceGatekeeperTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"disabled-source-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _originalDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_tempDir);

        _vectorStoreMock = new Mock<IVectorStore>();
        _embeddingMock = new Mock<IEmbeddingService>();
        _enhancerMock = new Mock<ITextEnhancer>();
        _hybridMock = new Mock<IHybridSearchService>();
        _pipelineMock = new Mock<IFileIndexPipeline>();
        _fileMonitorMock = new Mock<IFileMonitorService>();

        _embeddingMock.Setup(e => e.ModelName).Returns("test-embedding");
        _embeddingMock.Setup(e => e.Dimension).Returns(384);
        _embeddingMock.Setup(e => e.EncodeAsync(It.IsAny<string>(), It.IsAny<EmbeddingMode>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new float[384]);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_originalDir))
                Directory.SetCurrentDirectory(_originalDir);
            else
                Directory.SetCurrentDirectory(AppContext.BaseDirectory);
        }
        catch { }

        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    private SearchService CreateSearchService(ObsidianRagConfig config)
    {
        var configManager = new ConfigManager(config);
        configManager.Save(config);

        return new SearchService(
            _vectorStoreMock.Object,
            _embeddingMock.Object,
            _enhancerMock.Object,
            configManager,
            NullLogger<SearchService>.Instance,
            _hybridMock.Object
        );
    }

    [Fact]
    public async Task SearchAsync_WhenAllSourcesDisabled_FastShortCircuitsWithZeroResults()
    {
        // 场景：知识库中有且仅有1个源“新建文件夹”，状态为停用 (Enabled = false)
        var config = new ObsidianRagConfig
        {
            Sources = new List<SourceFolder>
            {
                new() { Name = "新建文件夹", Path = Path.Combine(_tempDir, "新建文件夹"), Enabled = false }
            }
        };

        var searchService = CreateSearchService(config);

        var request = new AIQueryRequest
        {
            Query = "5.4 测试关键词",
            Mode = QueryMode.HybridRerank,
            TopK = 10
        };

        var response = await searchService.SearchAsync(request);

        // 断言：结果为0，成功拦截
        Assert.NotNull(response);
        Assert.Empty(response.Chunks);
        Assert.Empty(response.Files);
        Assert.Equal(0, response.Stats.TotalMatches);

        // 核心门禁校验：前置短路发生，完全未调用 Embedding 编码推理，耗费 0 算力
        _embeddingMock.Verify(
            e => e.EncodeAsync(It.IsAny<string>(), It.IsAny<EmbeddingMode>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SearchAsync_WhenTargetSourceIsDisabled_ReturnsZeroMatches()
    {
        // 场景：配置中有启用源A和停用源B，用户明确限定搜索源B
        var config = new ObsidianRagConfig
        {
            Sources = new List<SourceFolder>
            {
                new() { Name = "ActiveSource", Path = Path.Combine(_tempDir, "ActiveSource"), Enabled = true },
                new() { Name = "DisabledSource", Path = Path.Combine(_tempDir, "DisabledSource"), Enabled = false }
            }
        };

        var searchService = CreateSearchService(config);

        var request = new AIQueryRequest
        {
            Query = "搜索限定词",
            Mode = QueryMode.HybridRerank,
            TopK = 10,
            Sources = new List<string> { "DisabledSource" }
        };

        var response = await searchService.SearchAsync(request);

        // 断言：请求的限定源已停用，立即短路返回 0 条匹配
        Assert.NotNull(response);
        Assert.Empty(response.Chunks);
        Assert.Equal(0, response.Stats.TotalMatches);

        _embeddingMock.Verify(
            e => e.EncodeAsync(It.IsAny<string>(), It.IsAny<EmbeddingMode>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SearchAsync_WhenMultipleSources_OnlyReturnsChunksFromEnabledSources()
    {
        // 场景：混杂源召回，停用源的分块必须 100% 被过滤
        var config = new ObsidianRagConfig
        {
            Sources = new List<SourceFolder>
            {
                new() { Name = "ActiveSource", Path = Path.Combine(_tempDir, "ActiveSource"), Enabled = true },
                new() { Name = "DisabledSource", Path = Path.Combine(_tempDir, "DisabledSource"), Enabled = false }
            }
        };

        _hybridMock.Setup(h => h.SearchAsync(
            It.IsAny<string>(),
            It.IsAny<int>(),
            It.IsAny<float>(),
            It.IsAny<float>(),
            It.IsAny<IEnumerable<string>?>(),
            It.IsAny<CancellationToken>(),
            It.IsAny<float[]?>()
        )).ReturnsAsync(new List<HybridSearchResult>
        {
            new() { ChunkId = "c1", Source = "ActiveSource", Content = "有效源内容 A", Score = 0.95f },
            new() { ChunkId = "c2", Source = "DisabledSource", Content = "已停用源内容 B", Score = 0.99f },
            new() { ChunkId = "c3", Source = "ActiveSource", Content = "有效源内容 C", Score = 0.88f }
        });

        var searchService = CreateSearchService(config);

        var request = new AIQueryRequest
        {
            Query = "测试混杂内容",
            Mode = QueryMode.Hybrid,
            TopK = 10
        };

        var response = await searchService.SearchAsync(request);

        Assert.NotNull(response);
        Assert.Equal(2, response.Chunks.Count);
        Assert.All(response.Chunks, c => Assert.Equal("ActiveSource", c.Source));
        Assert.DoesNotContain(response.Chunks, c => c.Source == "DisabledSource");
    }

    [Fact]
    public async Task SearchAsync_WhenSourceIsEmptyOrNull_FilteredOutWhenSourcesAreConfigured()
    {
        // 场景：存在已配置的源时，任何 null 或空 Source 的脏数据分块不得逃逸
        var config = new ObsidianRagConfig
        {
            Sources = new List<SourceFolder>
            {
                new() { Name = "ValidSource", Path = Path.Combine(_tempDir, "ValidSource"), Enabled = true }
            }
        };

        _hybridMock.Setup(h => h.SearchAsync(
            It.IsAny<string>(),
            It.IsAny<int>(),
            It.IsAny<float>(),
            It.IsAny<float>(),
            It.IsAny<IEnumerable<string>?>(),
            It.IsAny<CancellationToken>(),
            It.IsAny<float[]?>()
        )).ReturnsAsync(new List<HybridSearchResult>
        {
            new() { ChunkId = "c1", Source = "ValidSource", Content = "正常合法内容", Score = 0.95f },
            new() { ChunkId = "c2", Source = null, Content = "空 Source 脏数据", Score = 0.98f },
            new() { ChunkId = "c3", Source = "", Content = "空字符串 Source 脏数据", Score = 0.97f }
        });

        var searchService = CreateSearchService(config);

        var request = new AIQueryRequest
        {
            Query = "测试防空源穿透",
            Mode = QueryMode.Hybrid,
            TopK = 10
        };

        var response = await searchService.SearchAsync(request);

        Assert.NotNull(response);
        Assert.Single(response.Chunks);
        Assert.Equal("ValidSource", response.Chunks[0].Source);
    }

    [Fact]
    public async Task DrillDownAsync_WhenChunkBelongsToDisabledSource_BlocksContextExpansion()
    {
        // 场景：停用源的分块即使通过旧 refId 请求 DrillDown，也必须被拦截
        var config = new ObsidianRagConfig
        {
            Sources = new List<SourceFolder>
            {
                new() { Name = "DisabledSource", Path = Path.Combine(_tempDir, "DisabledSource"), Enabled = false }
            }
        };

        var chunk = new ChunkRecord
        {
            Id = "chunk-disabled-1",
            FileId = "file-disabled-1",
            Content = "敏感或已停用分块",
            StartLine = 10,
            EndLine = 20
        };

        var file = new FileRecord
        {
            Id = "file-disabled-1",
            Path = "C:\\Docs\\Disabled.md",
            Source = "DisabledSource"
        };

        _vectorStoreMock.Setup(v => v.GetChunkAsync("chunk-disabled-1", default))
            .ReturnsAsync(chunk);
        _vectorStoreMock.Setup(v => v.GetFileAsync("file-disabled-1", default))
            .ReturnsAsync(file);

        var searchService = CreateSearchService(config);

        var response = await searchService.DrillDownAsync(new DrillDownRequest
        {
            RefIds = new List<string> { "chunk-disabled-1" },
            ExpandContext = 2
        });

        Assert.NotNull(response);
        Assert.Empty(response.ExpandedChunks);
        Assert.Empty(response.FullContext);
    }

    [Fact]
    public async Task SourcesController_GetPaths_WhenEnabledOnlyTrue_ExcludesDisabledSources()
    {
        // 场景：前端路径树接口 /api/paths 默认过滤停用源
        var config = new ObsidianRagConfig
        {
            Sources = new List<SourceFolder>
            {
                new() { Name = "ActiveSource", Path = "C:\\Active", Enabled = true },
                new() { Name = "DisabledSource", Path = "C:\\Disabled", Enabled = false }
            }
        };

        var configManager = new ConfigManager(config);
        configManager.Save(config);

        _vectorStoreMock.Setup(v => v.GetAllFilesAsync(default))
            .ReturnsAsync(new List<FileRecord>
            {
                new() { Path = "C:\\Active\\sub\\doc1.md", Source = "ActiveSource" },
                new() { Path = "C:\\Disabled\\doc2.md", Source = "DisabledSource" }
            });

        var controller = new SourcesController(
            configManager,
            _vectorStoreMock.Object,
            _pipelineMock.Object,
            _fileMonitorMock.Object,
            NullLogger<SourcesController>.Instance
        );

        var actionResult = await controller.GetPaths(enabledOnly: true);
        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var response = Assert.IsType<PathsResponse>(okResult.Value);

        Assert.Single(response.Sources);
        Assert.Equal("ActiveSource", response.Sources[0].Name);
        Assert.DoesNotContain(response.Sources, s => s.Name == "DisabledSource");
    }
}
