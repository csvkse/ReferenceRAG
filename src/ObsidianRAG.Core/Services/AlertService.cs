namespace ObsidianRAG.Core.Services;

/// <summary>
/// 告警服务 - 监控告警规则
/// </summary>
public class AlertService
{
    private readonly MetricsCollector _metricsCollector;
    private readonly List<AlertRule> _rules;
    private readonly List<Alert> _activeAlerts;
    private readonly object _lock = new();

    public AlertService(MetricsCollector metricsCollector)
    {
        _metricsCollector = metricsCollector;
        _activeAlerts = new List<Alert>();
        _rules = new List<AlertRule>
        {
            new AlertRule
            {
                Name = "HighQueryLatency",
                Description = "查询延迟过高",
                Condition = "p95_query_latency_ms > 100",
                Severity = AlertSeverity.Warning,
                Threshold = 100
            },
            new AlertRule
            {
                Name = "HighMemoryUsage",
                Description = "内存使用过高",
                Condition = "memory_usage_mb > 2048",
                Severity = AlertSeverity.Warning,
                Threshold = 2048
            },
            new AlertRule
            {
                Name = "LowRecall",
                Description = "查询结果过少",
                Condition = "avg_results_per_query < 3",
                Severity = AlertSeverity.Info,
                Threshold = 3
            }
        };
    }

    /// <summary>
    /// 检查告警
    /// </summary>
    public async Task<List<Alert>> CheckAlertsAsync()
    {
        var newAlerts = new List<Alert>();
        var summary = _metricsCollector.GetSummary();
        var systemMetrics = await _metricsCollector.CollectSystemMetricsAsync();

        foreach (var rule in _rules)
        {
            var alert = rule.Name switch
            {
                "HighQueryLatency" => CheckThreshold(rule, summary.P95QueryLatencyMs, "ms"),
                "HighMemoryUsage" => CheckThreshold(rule, systemMetrics.MemoryUsageMB, "MB"),
                "LowRecall" => CheckThreshold(rule, summary.AvgResultsPerQuery, "results"),
                _ => null
            };

            if (alert != null)
            {
                newAlerts.Add(alert);
            }
        }

        // 更新活动告警
        lock (_lock)
        {
            _activeAlerts.Clear();
            _activeAlerts.AddRange(newAlerts);
        }

        return newAlerts;
    }

    /// <summary>
    /// 检查阈值
    /// </summary>
    private Alert? CheckThreshold(AlertRule rule, double value, string unit)
    {
        var isTriggered = rule.Name switch
        {
            "LowRecall" => value < rule.Threshold,
            _ => value > rule.Threshold
        };

        if (isTriggered)
        {
            return new Alert
            {
                RuleName = rule.Name,
                Description = rule.Description,
                Severity = rule.Severity,
                Value = value,
                Threshold = rule.Threshold,
                Unit = unit,
                TriggeredAt = DateTime.UtcNow
            };
        }

        return null;
    }

    /// <summary>
    /// 获取活动告警
    /// </summary>
    public List<Alert> GetActiveAlerts()
    {
        lock (_lock)
        {
            return new List<Alert>(_activeAlerts);
        }
    }

    /// <summary>
    /// 添加自定义告警规则
    /// </summary>
    public void AddRule(AlertRule rule)
    {
        _rules.Add(rule);
    }

    /// <summary>
    /// 移除告警规则
    /// </summary>
    public void RemoveRule(string ruleName)
    {
        _rules.RemoveAll(r => r.Name == ruleName);
    }

    /// <summary>
    /// 获取所有规则
    /// </summary>
    public List<AlertRule> GetRules()
    {
        return new List<AlertRule>(_rules);
    }
}

/// <summary>
/// 告警规则
/// </summary>
public class AlertRule
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Condition { get; set; } = string.Empty;
    public AlertSeverity Severity { get; set; }
    public double Threshold { get; set; }
}

/// <summary>
/// 告警
/// </summary>
public class Alert
{
    public string RuleName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public AlertSeverity Severity { get; set; }
    public double Value { get; set; }
    public double Threshold { get; set; }
    public string Unit { get; set; } = string.Empty;
    public DateTime TriggeredAt { get; set; }
}

/// <summary>
/// 告警严重程度
/// </summary>
public enum AlertSeverity
{
    Info,
    Warning,
    Critical
}
