using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ReferenceRAG.ApiHost.Features.Logging.Models;
using ReferenceRAG.Core.Services;

namespace ReferenceRAG.ApiHost.Features.Logging.Services;

/// <summary>
/// 内存环形缓冲区 + 轻量 JSONL 本地持久化操作审计仓储实现
/// </summary>
public sealed class OperationLogStore : IOperationLogStore
{
    private const int MaxInMemoryCount = 1000;
    private readonly List<OperationLogEntry> _memoryBuffer = new(MaxInMemoryCount);
    private readonly Lock _lock = new();
    private readonly string? _auditFilePath;
    private readonly ILogger<OperationLogStore> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    public OperationLogStore(
        IConfiguration configuration,
        IServiceProvider serviceProvider,
        ILogger<OperationLogStore> logger)
    {
        _logger = logger;

        try
        {
            var configManager = serviceProvider.GetService(typeof(ConfigManager)) as ConfigManager;
            var dataPath = configManager?.Load()?.DataPath;
            if (string.IsNullOrWhiteSpace(dataPath))
            {
                var workDir = configuration["Work:Directory"] ?? AppContext.BaseDirectory;
                dataPath = Path.Combine(workDir, "data");
            }

            var auditDir = Path.Combine(dataPath, "audit");
            if (!Directory.Exists(auditDir))
            {
                Directory.CreateDirectory(auditDir);
            }

            _auditFilePath = Path.Combine(auditDir, "operations.jsonl");
            LoadHistoricalLogs();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "初始化操作审计存储失败，将仅在内存中暂存");
        }
    }

    private void LoadHistoricalLogs()
    {
        if (string.IsNullOrEmpty(_auditFilePath) || !File.Exists(_auditFilePath))
        {
            return;
        }

        try
        {
            // 读取最新的最多 1000 条历史记录
            var lines = File.ReadLines(_auditFilePath).TakeLast(MaxInMemoryCount);
            lock (_lock)
            {
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var entry = JsonSerializer.Deserialize<OperationLogEntry>(line, JsonOptions);
                        if (entry != null)
                        {
                            _memoryBuffer.Add(entry);
                        }
                    }
                    catch
                    {
                        // 忽略格式损坏的单行
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "加载历史操作审计日志失败");
        }
    }

    public Task<Guid> RecordAsync(OperationLogEntry entry, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_memoryBuffer.Count >= MaxInMemoryCount)
            {
                _memoryBuffer.RemoveAt(0); // 移除最老的一条，维持上限
            }
            _memoryBuffer.Add(entry);
        }

        // 异步追加写入持久化文件（非阻塞）
        if (!string.IsNullOrEmpty(_auditFilePath))
        {
            _ = Task.Run(() =>
            {
                try
                {
                    var json = JsonSerializer.Serialize(entry, JsonOptions);
                    lock (_lock)
                    {
                        File.AppendAllText(_auditFilePath, json + Environment.NewLine);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "写入操作审计日志文件失败");
                }
            }, CancellationToken.None);
        }

        return Task.FromResult(entry.Id);
    }

    public Task<PagedResult<OperationLogEntry>> QueryAsync(
        OperationLogQuery query,
        CancellationToken cancellationToken = default)
    {
        List<OperationLogEntry> snapshot;
        lock (_lock)
        {
            snapshot = new List<OperationLogEntry>(_memoryBuffer);
        }

        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize <= 0 ? 20 : query.PageSize, 1, 100);

        IEnumerable<OperationLogEntry> filtered = snapshot;

        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var kw = query.Keyword.Trim();
            filtered = filtered.Where(x =>
                x.Action.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                x.TargetName.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                x.TargetType.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                x.Detail.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                (x.ClientIp != null && x.ClientIp.Contains(kw, StringComparison.OrdinalIgnoreCase)));
        }

        if (!string.IsNullOrWhiteSpace(query.Action))
        {
            filtered = filtered.Where(x => x.Action.Equals(query.Action.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(query.TargetType))
        {
            filtered = filtered.Where(x => x.TargetType.Equals(query.TargetType.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        var ordered = filtered.OrderByDescending(x => x.Timestamp).ToList();
        var total = ordered.Count;
        var pagedItems = ordered.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        var result = new PagedResult<OperationLogEntry>
        {
            Items = pagedItems,
            Total = total,
            Page = page,
            PageSize = pageSize
        };

        return Task.FromResult(result);
    }
}
