using Microsoft.AspNetCore.Mvc;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Services;

namespace ReferenceRAG.Service.Controllers;

/// <summary>
/// 系统管理 API
/// </summary>
[ApiController]
[Route("api/system")]
public class SystemController : ControllerBase
{
    private readonly MetricsCollector _metricsCollector;
    private readonly AlertService _alertService;
    private readonly IVectorStore _vectorStore;
    private readonly ILogger<SystemController> _logger;

    public SystemController(
        MetricsCollector metricsCollector,
        AlertService alertService,
        IVectorStore vectorStore,
        ILogger<SystemController> logger)
    {
        _metricsCollector = metricsCollector;
        _alertService = alertService;
        _vectorStore = vectorStore;
        _logger = logger;
    }

    /// <summary>
    /// 获取系统状态
    /// </summary>
    [HttpGet("status")]
    public async Task<ActionResult<SystemStatus>> GetStatus()
    {
        var systemMetrics = await _metricsCollector.CollectSystemMetricsAsync();
        var indexMetrics = await _metricsCollector.CollectIndexMetricsAsync();
        var alerts = _alertService.GetActiveAlerts();

        var status = new SystemStatus
        {
            Status = alerts.Any(a => a.Severity == AlertSeverity.Critical) ? "unhealthy" :
                     alerts.Any(a => a.Severity == AlertSeverity.Warning) ? "degraded" : "healthy",
            System = systemMetrics,
            Index = indexMetrics,
            ActiveAlerts = alerts
        };

        return Ok(status);
    }

    /// <summary>
    /// 获取系统指标
    /// </summary>
    [HttpGet("metrics")]
    public async Task<ActionResult<SystemMetrics>> GetSystemMetrics()
    {
        var metrics = await _metricsCollector.CollectSystemMetricsAsync();
        return Ok(metrics);
    }

    /// <summary>
    /// 获取索引指标
    /// </summary>
    [HttpGet("metrics/index")]
    public async Task<ActionResult<IndexMetrics>> GetIndexMetrics()
    {
        var metrics = await _metricsCollector.CollectIndexMetricsAsync();
        return Ok(metrics);
    }

    /// <summary>
    /// 获取查询指标摘要
    /// </summary>
    [HttpGet("metrics/queries")]
    public ActionResult<MetricsSummary> GetQueryMetrics()
    {
        var summary = _metricsCollector.GetSummary();
        return Ok(summary);
    }

    /// <summary>
    /// 获取所有原始指标
    /// </summary>
    [HttpGet("metrics/raw")]
    public ActionResult<Dictionary<string, MetricValue>> GetRawMetrics()
    {
        var metrics = _metricsCollector.GetAllMetrics();
        return Ok(metrics);
    }

    /// <summary>
    /// 获取活动告警
    /// </summary>
    [HttpGet("alerts")]
    public ActionResult<List<Alert>> GetAlerts()
    {
        var alerts = _alertService.GetActiveAlerts();
        return Ok(alerts);
    }

    /// <summary>
    /// 检查告警
    /// </summary>
    [HttpPost("alerts/check")]
    public async Task<ActionResult<List<Alert>>> CheckAlerts()
    {
        var alerts = await _alertService.CheckAlertsAsync();
        return Ok(alerts);
    }

    /// <summary>
    /// 获取告警规则
    /// </summary>
    [HttpGet("alerts/rules")]
    public ActionResult<List<AlertRule>> GetAlertRules()
    {
        var rules = _alertService.GetRules();
        return Ok(rules);
    }

    /// <summary>
    /// 添加告警规则
    /// </summary>
    [HttpPost("alerts/rules")]
    public ActionResult AddAlertRule([FromBody] AlertRule rule)
    {
        _alertService.AddRule(rule);
        return Ok();
    }

    /// <summary>
    /// 删除告警规则
    /// </summary>
    [HttpDelete("alerts/rules/{ruleName}")]
    public ActionResult RemoveAlertRule(string ruleName)
    {
        _alertService.RemoveRule(ruleName);
        return Ok();
    }

    /// <summary>
    /// 健康检查
    /// </summary>
    [HttpGet("health")]
    public async Task<ActionResult> HealthCheck()
    {
        try
        {
            // 检查数据库连接
            var files = await _vectorStore.GetAllFilesAsync();
            
            return Ok(new
            {
                status = "healthy",
                timestamp = DateTime.UtcNow,
                version = GetType().Assembly.GetName().Version?.ToString() ?? "1.0.0"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Health check failed");
            return StatusCode(503, new
            {
                status = "unhealthy",
                error = ex.Message,
                timestamp = DateTime.UtcNow
            });
        }
    }
}

/// <summary>
/// 系统状态
/// </summary>
public class SystemStatus
{
    public string Status { get; set; } = string.Empty;
    public SystemMetrics System { get; set; } = null!;
    public IndexMetrics Index { get; set; } = null!;
    public List<Alert> ActiveAlerts { get; set; } = new();
}
