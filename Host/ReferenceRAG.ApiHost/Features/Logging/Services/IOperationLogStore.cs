using ReferenceRAG.ApiHost.Features.Logging.Models;

namespace ReferenceRAG.ApiHost.Features.Logging.Services;

/// <summary>
/// 操作审计日志仓储接口
/// </summary>
public interface IOperationLogStore
{
    /// <summary>
    /// 记录一条操作审计日志
    /// </summary>
    Task<Guid> RecordAsync(OperationLogEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// 分页查询操作审计日志
    /// </summary>
    Task<PagedResult<OperationLogEntry>> QueryAsync(OperationLogQuery query, CancellationToken cancellationToken = default);
}
