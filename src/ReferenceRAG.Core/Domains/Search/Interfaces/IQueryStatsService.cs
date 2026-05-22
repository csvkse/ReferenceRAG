using System;
using ReferenceRAG.Core.Services;

namespace ReferenceRAG.Core.Interfaces;

/// <summary>
/// 查询统计服务接口
/// </summary>
public interface IQueryStatsService : IDisposable
{
    Task RecordQueryAsync(string query, long durationMs, int resultCount,
        List<string>? sources = null, string? mode = null);

    Task<double> GetAverageQueryTimeAsync(int lastNDays = 7);

    Task<QueryStatsSummary> GetSummaryAsync(int lastNDays = 7);

    Task<List<QueryStatRecord>> GetRecentQueriesAsync(int limit = 100);
}
