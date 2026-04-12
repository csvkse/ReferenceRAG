using ReferenceRAG.Core.Services;

namespace ReferenceRAG.Tests;

public class QueryStatsServiceTests : IDisposable
{
    private readonly QueryStatsService _statsService;
    private readonly string _testDbPath;

    public QueryStatsServiceTests()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"test_stats_{Guid.NewGuid()}.db");
        _statsService = new QueryStatsService(_testDbPath);
    }

    [Fact]
    public async Task RecordQueryAsync_RecordsQuery()
    {
        await _statsService.RecordQueryAsync("test query", 100, 5);
        
        var avgTime = await _statsService.GetAverageQueryTimeAsync();
        
        Assert.Equal(100, avgTime);
    }

    [Fact]
    public async Task RecordQueryAsync_RecordsMultipleQueries()
    {
        await _statsService.RecordQueryAsync("query 1", 100, 5);
        await _statsService.RecordQueryAsync("query 2", 200, 10);
        
        var avgTime = await _statsService.GetAverageQueryTimeAsync();
        
        Assert.Equal(150, avgTime);
    }

    [Fact]
    public async Task GetAverageQueryTimeAsync_WithNoData_ReturnsZero()
    {
        var emptyDbPath = Path.Combine(Path.GetTempPath(), $"empty_{Guid.NewGuid()}.db");
        var statsService = new QueryStatsService(emptyDbPath);

        var avgTime = await statsService.GetAverageQueryTimeAsync();

        Assert.Equal(0, avgTime);

        // Cleanup
        statsService.Dispose();
        for (int i = 0; i < 5; i++)
        {
            try
            {
                if (File.Exists(emptyDbPath))
                {
                    File.Delete(emptyDbPath);
                }
                break;
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
        }
    }

    [Fact]
    public async Task GetSummaryAsync_ReturnsCorrectStatistics()
    {
        await _statsService.RecordQueryAsync("query 1", 100, 5);
        await _statsService.RecordQueryAsync("query 2", 200, 10);
        await _statsService.RecordQueryAsync("query 3", 150, 7);
        
        var summary = await _statsService.GetSummaryAsync();
        
        Assert.Equal(3, summary.TotalQueries);
        Assert.Equal(150, summary.AvgDurationMs);
        Assert.Equal(200, summary.MaxDurationMs);
        Assert.Equal(100, summary.MinDurationMs);
        Assert.Equal(7.33, summary.AvgResultCount, 1);
    }

    [Fact]
    public async Task GetRecentQueriesAsync_ReturnsRecentQueries()
    {
        await _statsService.RecordQueryAsync("recent query", 100, 5);
        
        var recent = await _statsService.GetRecentQueriesAsync(10);
        
        Assert.NotEmpty(recent);
        Assert.Equal("recent query", recent[0].QueryText);
    }

    [Fact]
    public async Task GetRecentQueriesAsync_RespectsLimit()
    {
        for (int i = 0; i < 20; i++)
        {
            await _statsService.RecordQueryAsync($"query {i}", i * 10, i);
        }
        
        var recent = await _statsService.GetRecentQueriesAsync(5);
        
        Assert.Equal(5, recent.Count);
    }

    [Fact]
    public async Task GetRecentQueriesAsync_OrdersByCreatedAtDescending()
    {
        await _statsService.RecordQueryAsync("first", 100, 1);
        await Task.Delay(10);
        await _statsService.RecordQueryAsync("second", 200, 2);
        
        var recent = await _statsService.GetRecentQueriesAsync(2);
        
        Assert.Equal("second", recent[0].QueryText);
        Assert.Equal("first", recent[1].QueryText);
    }

    [Fact]
    public async Task RecordQueryAsync_WithSources_RecordsSources()
    {
        var sources = new List<string> { "source1", "source2" };
        
        await _statsService.RecordQueryAsync("query", 100, 5, sources, "standard");
        
        var recent = await _statsService.GetRecentQueriesAsync(1);
        
        Assert.Contains("source1", recent[0].Sources);
        Assert.Contains("source2", recent[0].Sources);
    }

    [Fact]
    public async Task RecordQueryAsync_WithMode_RecordsMode()
    {
        await _statsService.RecordQueryAsync("query", 100, 5, null, "deep");
        
        var recent = await _statsService.GetRecentQueriesAsync(1);
        
        Assert.Equal("deep", recent[0].Mode);
    }

    public void Dispose()
    {
        _statsService.Dispose();

        // Add delay and retry to handle SQLite file handle release
        for (int i = 0; i < 5; i++)
        {
            try
            {
                if (File.Exists(_testDbPath))
                {
                    File.Delete(_testDbPath);
                }
                break;
            }
            catch (IOException)
            {
                // Wait for file handle to be released
                Thread.Sleep(50);
            }
        }
    }
}
