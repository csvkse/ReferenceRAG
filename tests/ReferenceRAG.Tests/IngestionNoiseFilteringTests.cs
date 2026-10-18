using ReferenceRAG.Business.Features.Indexing.Adapters;
using ReferenceRAG.Business.Features.Indexing.Services;
using ReferenceRAG.Business.Features.Indexing.Strategies;
using ReferenceRAG.Core.Models;
using Xunit;

namespace ReferenceRAG.Tests;

public class IngestionNoiseFilteringTests
{
    [Theory]
    [InlineData("=== (43-48) ===", NoiseCategory.PaginationMarker)]
    [InlineData("=== (1-10) ===", NoiseCategory.PaginationMarker)]
    [InlineData("---", NoiseCategory.SeparatorLine)]
    [InlineData("***", NoiseCategory.SeparatorLine)]
    [InlineData("======", NoiseCategory.SeparatorLine)]
    public void SeparatorNoiseAdapter_DetectsNoiseSeparators(string content, NoiseCategory expectedCategory)
    {
        var adapter = new SeparatorNoiseAdapter();
        var chunk = new ChunkRecord { Content = content, StartLine = 1, EndLine = 2 };
        var context = new ChunkEvaluationContext { ChunkIndex = 1, TotalChunks = 3, HasPreviousChunk = true, HasNextChunk = true };

        var result = adapter.Evaluate(chunk, context);

        Assert.False(result.IsQualified);
        Assert.Equal(expectedCategory, result.Category);
        Assert.Equal(IngestionActionType.AbsorbToAdjacent, result.RecommendedAction);
    }

    [Fact]
    public void SeparatorNoiseAdapter_AllowsMeaningfulContent()
    {
        var adapter = new SeparatorNoiseAdapter();
        var chunk = new ChunkRecord
        {
            Content = "## 系统架构概览\n本系统采用多层防噪拦截流水线，确保高信噪比召回。",
            StartLine = 1,
            EndLine = 5
        };
        var context = new ChunkEvaluationContext { ChunkIndex = 0, TotalChunks = 1 };

        var result = adapter.Evaluate(chunk, context);

        Assert.True(result.IsQualified);
        Assert.Equal(IngestionActionType.Accept, result.RecommendedAction);
    }

    [Theory]
    [InlineData("1.")]
    [InlineData("...")]
    [InlineData("   ")]
    [InlineData("——")]
    public void InformationDensityAdapter_DetectsLowDensityFragments(string content)
    {
        var adapter = new InformationDensityAdapter();
        var chunk = new ChunkRecord { Content = content, StartLine = 10, EndLine = 10 };
        var context = new ChunkEvaluationContext { ChunkIndex = 1, TotalChunks = 2, HasPreviousChunk = true };

        var result = adapter.Evaluate(chunk, context);

        Assert.False(result.IsQualified);
        Assert.True(result.Category == NoiseCategory.PunctuationOnly || result.Category == NoiseCategory.LowDensityFragment);
    }

    [Fact]
    public void InformationDensityAdapter_AllowsHeadingsEvenIfShort()
    {
        var adapter = new InformationDensityAdapter();
        var chunk = new ChunkRecord
        {
            Content = "# 结论",
            HeadingPath = "结论",
            StartLine = 20,
            EndLine = 20
        };
        var context = new ChunkEvaluationContext { ChunkIndex = 0, TotalChunks = 1 };

        var result = adapter.Evaluate(chunk, context);

        Assert.True(result.IsQualified);
    }

    [Fact]
    public void AbsorbToAdjacentStrategy_MergesForwardWhenPreviousChunkExists()
    {
        var strategy = new AbsorbToAdjacentStrategy();
        var prevChunk = new ChunkRecord
        {
            ChunkIndex = 0,
            Content = "这是前一个具有完整意义的分块段落。",
            StartLine = 1,
            EndLine = 10
        };
        var noiseChunk = new ChunkRecord
        {
            ChunkIndex = 1,
            Content = "=== (11-12) ===",
            StartLine = 11,
            EndLine = 12
        };

        var allChunks = new List<ChunkRecord> { prevChunk, noiseChunk };
        var accepted = new List<ChunkRecord> { prevChunk };

        var result = strategy.Apply(noiseChunk, 1, allChunks, accepted);

        Assert.False(result.ChunkRetained);
        Assert.Equal(12, prevChunk.EndLine);
        Assert.Contains("=== (11-12) ===", prevChunk.Content);
    }

    [Fact]
    public void AbsorbToAdjacentStrategy_MergesBackwardWhenFirstChunkIsNoise()
    {
        var strategy = new AbsorbToAdjacentStrategy();
        var noiseChunk = new ChunkRecord
        {
            ChunkIndex = 0,
            Content = "---",
            StartLine = 1,
            EndLine = 2
        };
        var nextChunk = new ChunkRecord
        {
            ChunkIndex = 1,
            Content = "这是后续的第一个主体分块内容。",
            StartLine = 3,
            EndLine = 15
        };

        var allChunks = new List<ChunkRecord> { noiseChunk, nextChunk };
        var accepted = new List<ChunkRecord>();

        var result = strategy.Apply(noiseChunk, 0, allChunks, accepted);

        Assert.False(result.ChunkRetained);
        Assert.Equal(1, nextChunk.StartLine);
        Assert.StartsWith("---\n\n这是后续", nextChunk.Content);
    }

    [Fact]
    public void ChunkGatekeeperService_EndToEnd_FiltersNoiseAndReportsTelemetry()
    {
        var gatekeeper = new ChunkGatekeeperService();

        var chunks = new List<ChunkRecord>
        {
            new() { ChunkIndex = 0, Content = "第一节：引言与系统总体设计。", StartLine = 1, EndLine = 10 },
            new() { ChunkIndex = 1, Content = "=== (11-15) ===", StartLine = 11, EndLine = 15 }, // 噪声1
            new() { ChunkIndex = 2, Content = "第二节：核心业务流水线与分块准入评估机制。", StartLine = 16, EndLine = 30 },
            new() { ChunkIndex = 3, Content = "------", StartLine = 31, EndLine = 32 },            // 噪声2
            new() { ChunkIndex = 4, Content = "第三节：双路混合检索与重排精排链路验证。", StartLine = 33, EndLine = 50 }
        };

        var (cleanChunks, telemetry) = gatekeeper.GatekeepChunks(chunks, "C:/docs/architecture.md");

        Assert.Equal(3, cleanChunks.Count);
        Assert.Contains("=== (11-15) ===", cleanChunks[0].Content);
        Assert.Equal(15, cleanChunks[0].EndLine);
        Assert.Contains("------", cleanChunks[1].Content);
        Assert.Equal(32, cleanChunks[1].EndLine);

        Assert.Equal(5, telemetry.TotalExtracted);
        Assert.Equal(3, telemetry.QualifiedCount);
        Assert.Equal(2, telemetry.AbsorbedCount);
        Assert.Equal(0, telemetry.DiscardedCount);
        Assert.Equal(2, telemetry.SavedEmbeddingsCount);
        Assert.True(telemetry.SavedGpuTimeEstimateMs > 0);
    }
}
