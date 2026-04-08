using ObsidianRAG.Core.Models;

namespace ObsidianRAG.Core.Interfaces;

/// <summary>
/// 搜索服务接口
/// </summary>
public interface ISearchService
{
    /// <summary>
    /// 搜索
    /// </summary>
    Task<AIQueryResponse> SearchAsync(AIQueryRequest request, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// 深入查询
    /// </summary>
    Task<DrillDownResponse> DrillDownAsync(DrillDownRequest request, CancellationToken cancellationToken = default);
}
