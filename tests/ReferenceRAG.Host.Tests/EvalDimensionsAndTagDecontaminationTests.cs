using Microsoft.AspNetCore.Mvc;
using Moq;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Service.Controllers;
using Xunit;

namespace ReferenceRAG.Host.Tests;

/// <summary>
/// 验证多维评测扩展与输入文本防污染（Tag解耦）规范
/// </summary>
public class EvalDimensionsAndTagDecontaminationTests
{
    private readonly RerankTestController _rerankController;
    private readonly SemanticTestController _semanticController;

    public EvalDimensionsAndTagDecontaminationTests()
    {
        var mockRerankService = new Mock<IRerankService>();
        var recordStore = new TestRecordStore();
        var mockLogger = new Mock<Microsoft.Extensions.Logging.ILogger<RerankTestController>>();
        _rerankController = new RerankTestController(mockRerankService.Object, recordStore, mockLogger.Object);

        var mockEmbeddingService = new Mock<IEmbeddingService>();
        var mockTokenizer = new Mock<ITokenizer>();
        var mockSemanticLogger = new Mock<Microsoft.Extensions.Logging.ILogger<SemanticTestController>>();
        _semanticController = new SemanticTestController(mockEmbeddingService.Object, mockTokenizer.Object, recordStore, mockSemanticLogger.Object);
    }

    [Fact]
    public void RerankPresets_ContainsNewDimensions_VersionTemporalAndNumericalEntity()
    {
        var actionResult = _rerankController.GetPresetSuites();
        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var suites = Assert.IsType<List<RerankPresetSuiteInfo>>(okResult.Value);

        Assert.Contains(suites, s => s.Name == "version-temporal-rerank");
        Assert.Contains(suites, s => s.Name == "numerical-entity-rerank");
        Assert.Contains(suites, s => s.Name == "antonym-polarity-rerank");
        Assert.True(suites.Count >= 12);
    }

    [Fact]
    public void RerankPresets_DocumentsAreDecontaminated_NoPollutedBracketsInText()
    {
        var actionResult = _rerankController.GetPresetSuites();
        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var suites = Assert.IsType<List<RerankPresetSuiteInfo>>(okResult.Value);

        foreach (var suite in suites)
        {
            // 通过反射获取私有方法 GetPresetSuite 检验所有预设文档文本
            var method = typeof(RerankTestController).GetMethod("GetPresetSuite", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var req = method?.Invoke(_rerankController, new object[] { suite.Name }) as RerankTestRequest;
            Assert.NotNull(req);
            Assert.Equal(10, req.Documents.Count);

            foreach (var doc in req.Documents)
            {
                // 验证绝对不存在【硬负例】、【硬对立】、【否定排他】、【版本废弃】等前缀污染
                Assert.DoesNotContain("【硬负例", doc.Text);
                Assert.DoesNotContain("【硬对立", doc.Text);
                Assert.DoesNotContain("【否定排他", doc.Text);
                Assert.DoesNotContain("【主谓与方向相反", doc.Text);
                Assert.DoesNotContain("【版本废弃", doc.Text);
            }
        }
    }

    [Fact]
    public void SemanticRetrievalPresets_ContainsNewDimensions_AndPassagesAreDecontaminated()
    {
        var actionResult = _semanticController.GetRetrievalPresetSuites();
        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var suites = Assert.IsType<List<RetrievalPresetSuiteInfo>>(okResult.Value);

        Assert.Contains(suites, s => s.Name == "version-temporal-retrieval");
        Assert.Contains(suites, s => s.Name == "numerical-constraint-retrieval");

        foreach (var suite in suites)
        {
            var res = _semanticController.GetRetrievalPresetData(suite.Name);
            var okData = Assert.IsType<OkObjectResult>(res.Result);
            var req = Assert.IsType<LongTextTestRequest>(okData.Value);
            Assert.Equal(10, req.Passages.Count);

            foreach (var p in req.Passages)
            {
                // 纯净文本断言
                Assert.DoesNotContain("【对抗硬负例】", p.Text);
                Assert.DoesNotContain("【硬负例", p.Text);
            }
        }
    }

    [Fact]
    public async Task RerankTest_PreservesTagInOutputResults()
    {
        var mockRerankService = new Mock<IRerankService>();
        mockRerankService.Setup(s => s.RerankBatchAsync(It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RerankResult
            {
                Documents = new List<RerankDocument>
                {
                    new() { Document = "纯净测试文本正文A", RelevanceScore = 0.95, Index = 0 },
                    new() { Document = "纯净测试文本正文B", RelevanceScore = 0.12, Index = 1 }
                }
            });

        Exception? loggedEx = null;
        var mockLogger = new Mock<Microsoft.Extensions.Logging.ILogger<RerankTestController>>();
        mockLogger.Setup(l => l.Log(
            Microsoft.Extensions.Logging.LogLevel.Error,
            It.IsAny<Microsoft.Extensions.Logging.EventId>(),
            It.IsAny<It.IsAnyType>(),
            It.IsAny<Exception>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback<Microsoft.Extensions.Logging.LogLevel, Microsoft.Extensions.Logging.EventId, object, Exception?, object>((level, eventId, state, ex, formatter) =>
            {
                loggedEx = ex;
            });

        var controller = new RerankTestController(mockRerankService.Object, new TestRecordStore(), mockLogger.Object);
        var request = new RerankTestRequest
        {
            Query = "测试查询",
            Documents = new List<RerankDocumentInput>
            {
                new() { Id = "doc1", Tag = "核心指征", Text = "纯净测试文本正文A", ExpectedRelevance = 0.95 },
                new() { Id = "doc2", Tag = "极性相反", Text = "纯净测试文本正文B", ExpectedRelevance = 0.10 }
            }
        };

        var actionResult = await controller.Test(request);
        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var testResult = Assert.IsType<RerankTestResult>(okResult.Value);

        Assert.Equal(2, testResult.Documents.Count);
        Assert.Equal("核心指征", testResult.Documents[0].Tag);
        Assert.Equal("极性相反", testResult.Documents[1].Tag);
        Assert.Equal("纯净测试文本正文A", testResult.Documents[0].Text);
    }
}
