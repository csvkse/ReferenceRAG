using ReferenceRAG.Core.Features.Search.Lexical;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Infrastructure.Features.Database;
using ReferenceRAG.Storage;
using Xunit;

namespace ReferenceRAG.Tests;

public class LongQueryLexicalOptimizationTests : IDisposable
{
    private readonly string _tempDir;

    public LongQueryLexicalOptimizationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LexicalLongQueryTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            // Ignore cleanup errors in tests
        }
    }

    [Fact]
    public void PosAnalysisAdapter_CleansConversationalPrefixesAndWeightsNounsHigher()
    {
        var adapter = JiebaPosAnalysisAdapter.Default;
        var query = "我想请问一下关于张无忌在光明顶大战六大门派的武功绝学";

        var weightedTokens = adapter.AnalyzeAndWeight(query);

        Assert.NotEmpty(weightedTokens);

        // 验证去除了口语无意义疑问词 "我想请问一下关于"
        Assert.DoesNotContain(weightedTokens, t => t.Word == "我想");
        Assert.DoesNotContain(weightedTokens, t => t.Word == "请问");

        // 验证专有名词和关键实体被赋更高权重
        var zhangToken = weightedTokens.FirstOrDefault(t => t.Word == "张无忌");
        Assert.NotNull(zhangToken);
        Assert.True(zhangToken.Weight >= 2.0);

        var topWords = weightedTokens.OrderByDescending(t => t.Weight).Select(t => t.Word).ToList();
        Assert.Contains("张无忌", topWords);
    }

    [Fact]
    public void LongQueryRefiner_GeneratesTieredExecutionPlanWithNearProximity()
    {
        var refiner = LongQueryRefiner.Default;
        var query = "雪山飞狐中胡一刀与苗人凤在雪山之巅长达数日的惊世对决与刀剑互换";

        var plan = refiner.PlanQuery(query);

        Assert.NotNull(plan);
        Assert.NotEmpty(plan.DistilledTerms);

        // Tier 1 应包含 NEAR 语法
        Assert.NotNull(plan.Tier1StrictNearQuery);
        Assert.Contains("NEAR(", plan.Tier1StrictNearQuery);

        // Tier 2 应包含蒸馏出的核心词 OR 组合
        Assert.NotEmpty(plan.Tier2WeightedQuery);
        Assert.Contains("OR", plan.Tier2WeightedQuery);

        // Tier 3 为兜底广度检索
        Assert.NotEmpty(plan.Tier3FallbackQuery);
    }

    [Fact]
    public void StatisticalPrfOperator_MinesCooccurringTermsWithoutNeuralModel()
    {
        var seedTexts = new[]
        {
            "胡一刀与苗人凤英雄相惜，苗若兰在旁见证了这场旷世决战，宝树和尚与田归农却在暗中窥伺闯王宝藏。",
            "苗若兰对胡斐倾心相许，胡斐手持冷月宝刀，寻觅杀父仇人田归农的下落。",
            "雪山飞狐讲述了明末清初李自成闯王宝藏引发的恩怨情仇，苗家剑法与胡家刀法各具奇观。"
        };

        var queryTerms = new[] { "胡一刀", "苗人凤", "决斗" };

        var expansionTerms = StatisticalPrfOperator.MineExpansionTerms(seedTexts, queryTerms, maxTerms: 3);

        Assert.NotEmpty(expansionTerms);
        // 扩展词不应包含查询中已有的词
        foreach (var term in queryTerms)
        {
            Assert.DoesNotContain(term, expansionTerms);
        }
        // 应涌现出相关的高频高信息量实体（如 苗若兰、胡斐、田归农、宝藏 等）
        Assert.True(expansionTerms.Any(t => t is "苗若兰" or "胡斐" or "田归农" or "宝藏" or "宝树"));
    }

    [Fact]
    public async Task LexicalIndexCoordinator_LongQueryWithTieredRecallAndPrf_HitsAccurately()
    {
        var docDb = Path.Combine(_tempDir, "docs.db");
        var indexDb = Path.Combine(_tempDir, "index.db");

        await using var coordinator = new LexicalIndexCoordinator(docDb, indexDb);

        var file1 = new FileRecord
        {
            Id = "file-01",
            Path = "docs/huanzhu.txt",
            Title = "雪山飞狐纪要",
            Source = "novel",
            ModifiedAt = DateTime.UtcNow,
            ContentHash = "hash1",
            ChunkCount = 2
        };

        var chunk1 = new ChunkRecord
        {
            Id = "chunk-01",
            FileId = "file-01",
            Content = "胡一刀与苗人凤在极寒之地的雪山之巅决战数日，双方刀剑互换惺惺相惜，苗若兰在旁守候，田归农却在暗中往刀口抹毒。",
            StartLine = 1,
            EndLine = 10,
            HeadingPath = "第一章"
        };

        var chunk2 = new ChunkRecord
        {
            Id = "chunk-02",
            FileId = "file-01",
            Content = "江南水乡燕子坞，姑苏慕容复精通斗转星移，王语嫣熟读天下武学秘籍。",
            StartLine = 11,
            EndLine = 20,
            HeadingPath = "第二章"
        };

        await coordinator.StageDocumentsAsync(file1, "gen-1", new[] { chunk1, chunk2 });
        await coordinator.StageIndexAsync(file1.Id, "gen-1");
        await coordinator.PublishAsync(file1.Id, "gen-1");

        // 使用超过 20 个字符且带有提问套话的长句进行纯词法检索
        var query = "请问大家在雪山之巅胡一刀与苗人凤决战时田归农在暗地里抹毒药的真相是什么？";

        var results = await coordinator.SearchAsync(query, topK: 5);

        Assert.NotEmpty(results);
        Assert.Equal("chunk-01", results[0].ChunkId);
        Assert.True(results[0].Score > 0);
    }
}
