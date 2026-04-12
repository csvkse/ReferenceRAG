using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using Moq;

namespace ReferenceRAG.Tests;

public class MetricsCollectorTests
{
    [Fact]
    public void RecordQueryMetric_IncrementsCounter()
    {
        // Skip due to bug in MetricsCollector where Value = 0 (int) is later cast to long
        var mockVectorStore = new Mock<IVectorStore>();
        var collector = new MetricsCollector(mockVectorStore.Object);
        collector.RecordQueryMetric("test query", 100, 5);

        var summary = collector.GetSummary();
        Assert.Equal(1, summary.TotalQueries);
    }

    [Fact]
    public void RecordQueryMetric_TracksLatency()
    {
        var mockVectorStore = new Mock<IVectorStore>();
        var collector = new MetricsCollector(mockVectorStore.Object);
        collector.RecordQueryMetric("query 1", 100, 5);
        collector.RecordQueryMetric("query 2", 200, 10);

        var summary = collector.GetSummary();
        Assert.Equal(150, summary.AvgQueryLatencyMs);
    }

    [Fact]
    public void RecordQueryMetric_TracksResultCount()
    {
        var mockVectorStore = new Mock<IVectorStore>();
        var collector = new MetricsCollector(mockVectorStore.Object);
        collector.RecordQueryMetric("query", 100, 5);

        var summary = collector.GetSummary();
        Assert.Equal(5, summary.AvgResultsPerQuery);
    }

    [Fact]
    public async Task CollectSystemMetricsAsync_ReturnsValidMetrics()
    {
        var mockVectorStore = new Mock<IVectorStore>();
        var collector = new MetricsCollector(mockVectorStore.Object);

        var metrics = await collector.CollectSystemMetricsAsync();

        Assert.True(metrics.MemoryUsageMB > 0);
        Assert.True(metrics.ThreadCount > 0);
        Assert.True(metrics.Uptime.TotalSeconds >= 0);
    }

    [Fact]
    public async Task CollectIndexMetricsAsync_WithNoFiles_ReturnsZeroCounts()
    {
        var mockVectorStore = new Mock<IVectorStore>();
        mockVectorStore.Setup(x => x.GetAllFilesAsync(default))
            .ReturnsAsync(new List<FileRecord>());
        var collector = new MetricsCollector(mockVectorStore.Object);

        var metrics = await collector.CollectIndexMetricsAsync();

        Assert.Equal(0, metrics.TotalFiles);
        Assert.Equal(0, metrics.TotalChunks);
    }

    [Fact]
    public void GetSummary_WithNoData_ReturnsZeros()
    {
        var mockVectorStore = new Mock<IVectorStore>();
        var collector = new MetricsCollector(mockVectorStore.Object);

        var summary = collector.GetSummary();

        Assert.Equal(0, summary.TotalQueries);
        Assert.Equal(0, summary.AvgQueryLatencyMs);
    }
}
