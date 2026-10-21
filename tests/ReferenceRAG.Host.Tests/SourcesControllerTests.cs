using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Service.Controllers;
using ReferenceRAG.Service.Services;
using Xunit;

namespace ReferenceRAG.Host.Tests;

[Collection("CurrentDirectoryCollection")]
public class SourcesControllerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _originalDir;
    private readonly Mock<IVectorStore> _vectorStoreMock;
    private readonly Mock<IFileIndexPipeline> _pipelineMock;
    private readonly Mock<IFileMonitorService> _fileMonitorMock;

    public SourcesControllerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sources-ctrl-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _originalDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_tempDir);

        _vectorStoreMock = new Mock<IVectorStore>();
        _pipelineMock = new Mock<IFileIndexPipeline>();
        _fileMonitorMock = new Mock<IFileMonitorService>();

        _vectorStoreMock.Setup(v => v.GetSourceStatsAsync(default))
            .ReturnsAsync(new List<SourceFileStat>());
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

    private (SourcesController Controller, ConfigManager ConfigManager) CreateController(ObsidianRagConfig config)
    {
        var configManager = new ConfigManager(config);
        configManager.Save(config);

        var controller = new SourcesController(
            configManager,
            _vectorStoreMock.Object,
            _pipelineMock.Object,
            _fileMonitorMock.Object,
            NullLogger<SourcesController>.Instance
        );

        return (controller, configManager);
    }

    [Fact]
    public async Task Delete_WithChineseName_DeletesSourceAndMonitor()
    {
        var initialConfig = new ObsidianRagConfig
        {
            Sources = new List<SourceFolder>
            {
                new() { Name = "小说", Path = "C:\\Books\\Novel", Enabled = true }
            }
        };

        var (controller, configManager) = CreateController(initialConfig);

        var result = await controller.Delete("小说", deleteData: false);

        Assert.IsType<NoContentResult>(result);
        var updatedConfig = configManager.Load();
        Assert.DoesNotContain(updatedConfig.Sources, s => s.Name == "小说");
        _fileMonitorMock.Verify(m => m.RemoveSource("小说"), Times.Once);
        _pipelineMock.Verify(p => p.DeleteSourceAsync(It.IsAny<string>(), default), Times.Never);
    }

    [Fact]
    public async Task Delete_WithUrlEncodedChineseName_DeletesSourceAndData()
    {
        var initialConfig = new ObsidianRagConfig
        {
            Sources = new List<SourceFolder>
            {
                new() { Name = "小说", Path = "C:\\Books\\Novel", Enabled = true }
            }
        };

        var (controller, configManager) = CreateController(initialConfig);

        // %E5%B0%8F%E8%AF%B4 is URL-encoded for "小说"
        var result = await controller.Delete("%E5%B0%8F%E8%AF%B4", deleteData: true);

        Assert.IsType<NoContentResult>(result);
        var updatedConfig = configManager.Load();
        Assert.DoesNotContain(updatedConfig.Sources, s => s.Name == "小说");
        _pipelineMock.Verify(p => p.DeleteSourceAsync("小说", default), Times.Once);
        _fileMonitorMock.Verify(m => m.RemoveSource("小说"), Times.Once);
    }

    [Fact]
    public async Task Delete_NonExistentSource_ReturnsNotFound()
    {
        var initialConfig = new ObsidianRagConfig();
        var (controller, _) = CreateController(initialConfig);

        var result = await controller.Delete("不存在的源", deleteData: false);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task Delete_CaseInsensitive_MatchesAndDeletes()
    {
        var initialConfig = new ObsidianRagConfig
        {
            Sources = new List<SourceFolder>
            {
                new() { Name = "MyNovel", Path = "C:\\Books\\Novel", Enabled = true }
            }
        };

        var (controller, configManager) = CreateController(initialConfig);

        var result = await controller.Delete("mynovel", deleteData: false);

        Assert.IsType<NoContentResult>(result);
        var updatedConfig = configManager.Load();
        Assert.DoesNotContain(updatedConfig.Sources, s => s.Name.Equals("MyNovel", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Get_WithUrlEncodedChineseName_ReturnsSource()
    {
        var initialConfig = new ObsidianRagConfig
        {
            Sources = new List<SourceFolder>
            {
                new() { Name = "小说", Path = "C:\\Books\\Novel", Enabled = true }
            }
        };

        var (controller, _) = CreateController(initialConfig);

        var result = await controller.Get("%E5%B0%8F%E8%AF%B4");

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var detail = Assert.IsType<SourceDetail>(okResult.Value);
        Assert.Equal("小说", detail.Name);
    }

    [Fact]
    public void Toggle_WithUrlEncodedChineseName_TogglesState()
    {
        var initialConfig = new ObsidianRagConfig
        {
            Sources = new List<SourceFolder>
            {
                new() { Name = "小说", Path = "C:\\Books\\Novel", Enabled = true }
            }
        };

        var (controller, configManager) = CreateController(initialConfig);

        var result = controller.Toggle("%E5%B0%8F%E8%AF%B4", new ToggleRequest { Enabled = false });

        Assert.IsType<OkResult>(result);
        var updatedConfig = configManager.Load();
        var source = updatedConfig.Sources.FirstOrDefault(s => s.Name == "小说");
        Assert.NotNull(source);
        Assert.False(source.Enabled);
    }

    [Fact]
    public void Add_DefaultFilePatterns_IncludesBothMdAndTxt()
    {
        var sourceDir = Path.Combine(_tempDir, "NovelDir");
        Directory.CreateDirectory(sourceDir);

        var initialConfig = new ObsidianRagConfig();
        var (controller, _) = CreateController(initialConfig);

        var result = controller.Add(new AddSourceRequest
        {
            Name = "NovelSource",
            Path = sourceDir
        });

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var source = Assert.IsType<SourceFolder>(created.Value);
        Assert.Contains("*.md", source.FilePatterns);
        Assert.Contains("*.txt", source.FilePatterns);
    }

    [Fact]
    public void Scan_ReturnsPhysicalDiskDiagnostics_And_HandlesWildcardRules()
    {
        var sourceDir = Path.Combine(_tempDir, "ScanDiagDir");
        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(Path.Combine(sourceDir, "doc1.txt"), "hello txt");
        File.WriteAllText(Path.Combine(sourceDir, "doc2.md"), "hello md");
        File.WriteAllText(Path.Combine(sourceDir, "code.c"), "int main(){}");

        var initialConfig = new ObsidianRagConfig
        {
            Sources = new List<SourceFolder>
            {
                new()
                {
                    Name = "DiagSource",
                    Path = sourceDir,
                    Enabled = true,
                    FilePatterns = new List<string> { "*.md" }
                }
            }
        };

        var (controller, configManager) = CreateController(initialConfig);

        // 1. 规则为 *.md 时：磁盘 3 个文件，匹配 1 个，识别出所有磁盘扩展名
        var scan1 = controller.Scan("DiagSource");
        var ok1 = Assert.IsType<OkObjectResult>(scan1.Result);
        var res1 = Assert.IsType<ScanResult>(ok1.Value);
        Assert.Equal(3, res1.TotalDiskFiles);
        Assert.Equal(1, res1.TotalFiles);
        Assert.Contains(".c", res1.DiskExtensions.Keys);
        Assert.Contains(".txt", res1.DiskExtensions.Keys);
        Assert.Contains(".md", res1.DiskExtensions.Keys);

        // 2. 规则为 *.* 通配符时：全部 3 个文件均被正确匹配
        var source = configManager.Load().Sources.First(s => s.Name == "DiagSource");
        source.FilePatterns = new List<string> { "*.*" };
        configManager.Save(configManager.Load());

        var scan2 = controller.Scan("DiagSource");
        var ok2 = Assert.IsType<OkObjectResult>(scan2.Result);
        var res2 = Assert.IsType<ScanResult>(ok2.Value);
        Assert.Equal(3, res2.TotalDiskFiles);
        Assert.Equal(3, res2.TotalFiles);

        // 3. 规则为带空格的多个规则时 (如 " *.c ", " .txt ")：正确去除空格并匹配
        source.FilePatterns = new List<string> { " *.c ", " .txt " };
        configManager.Save(configManager.Load());

        var scan3 = controller.Scan("DiagSource");
        var ok3 = Assert.IsType<OkObjectResult>(scan3.Result);
        var res3 = Assert.IsType<ScanResult>(ok3.Value);
        Assert.Equal(3, res3.TotalDiskFiles);
        Assert.Equal(2, res3.TotalFiles);
    }
}
