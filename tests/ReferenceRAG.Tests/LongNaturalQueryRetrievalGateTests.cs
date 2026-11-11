using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Core.Features.Search.Lexical;
using ReferenceRAG.Infrastructure.Features.KnowledgeBase.Storage;
using Xunit;

namespace ReferenceRAG.Tests;

public class LongNaturalQueryRetrievalGateTests : IDisposable
{
    private readonly string _testDbPath;

    public LongNaturalQueryRetrievalGateTests()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"kb_test_{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_testDbPath))
            {
                File.Delete(_testDbPath);
            }
        }
        catch { }
    }

    [Fact]
    public void LongQueryRefiner_Should_Produce_Valid_Tier1_Near_Plan_For_Natural_Query()
    {
        var query = "这对未婚夫妻从未见过面，忽然在公主闺房中相会";
        var plan = LongQueryRefiner.Default.PlanQuery(query);

        Assert.NotNull(plan);
        Assert.NotNull(plan.Tier1StrictNearQuery);
        Assert.NotEmpty(plan.Tier2WeightedQuery);
        Assert.NotEmpty(plan.Tier3FallbackQuery);

        // Tier 1 应包含 NEAR 语法
        Assert.Contains("NEAR(", plan.Tier1StrictNearQuery);
        // 关键实体词应被提取
        Assert.Contains("未婚", plan.Tier1StrictNearQuery);
        Assert.Contains("夫妻", plan.Tier1StrictNearQuery);
        Assert.Contains("公主", plan.Tier1StrictNearQuery);
    }

    [Fact]
    public async Task SqliteKnowledgeBaseStore_RecallAsync_Should_Hit_DeerCauldron_Chunk_Top1()
    {
        var targetText = "这对未婚夫妻从未见过面，忽然在公主闺房中相会，一个既害羞，又是气恼，一个目瞪口呆，大感为难。韦小宝心念电转：“建宁公主既然是假太后的亲生女儿，这驸马爷吴应熊，自然也不是真驸马了。”";
        var distractor1 = "韦小宝大喜，心想今日遇上建宁公主，倒是一场好戏。";
        var distractor2 = "陈近南说道：“小宝，天地会各位兄弟都在等你，不可在闺房中留连忘返。”";

        var store = new SqliteKnowledgeBaseStore("kb_test", _testDbPath);
        await store.InitializeAsync();

        // 提交测试文档与分块
        var batch = new IndexBatch(
            KnowledgeBaseId: "kb_test",
            ExpectedIndexEpoch: 1,
            ExpectedDocumentVersion: 0,
            ConnectorId: "c_gold",
            SourceKey: "full/三联版/鹿鼎记.md",
            Locator: "full/三联版/鹿鼎记.md",
            SourceRevision: "rev1",
            ContentHash: "hash123",
            Chunks: new[]
            {
                new ChunkData("chunk_target", 0, targetText, 50),
                new ChunkData("chunk_noise1", 1, distractor1, 30),
                new ChunkData("chunk_noise2", 2, distractor2, 30)
            }
        );

        var commitResult = await store.CommitDocumentAsync(batch);
        Assert.True(commitResult.Success);

        // 执行长自然语言查询
        var query = "这对未婚夫妻从未见过面，忽然在公主闺房中相会";
        var recallResult = await store.RecallAsync(new RecallRequest(
            Query: query,
            TopK: 5,
            UseLexicalTokenizer: true,
            AllowDegrade: true
        ));

        Assert.NotNull(recallResult);
        Assert.NotEmpty(recallResult.Items);

        // 验证目标切片以 Top 1 命中
        var topHit = recallResult.Items.First();
        Assert.Equal("chunk_target", topHit.ChunkId);
        Assert.Contains("这对未婚夫妻从未见过面", topHit.Text);
    }
}
