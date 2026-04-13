using Microsoft.AspNetCore.Mvc;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Services;
using System.Diagnostics;
using System.Runtime.InteropServices;

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
    private readonly IHostApplicationLifetime _appLifetime;

    public SystemController(
        MetricsCollector metricsCollector,
        AlertService alertService,
        IVectorStore vectorStore,
        ILogger<SystemController> logger,
        IHostApplicationLifetime appLifetime)
    {
        _metricsCollector = metricsCollector;
        _alertService = alertService;
        _vectorStore = vectorStore;
        _logger = logger;
        _appLifetime = appLifetime;
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

    /// <summary>
    /// 重启服务
    /// </summary>
    [HttpPost("restart")]
    public ActionResult RestartService()
    {
        _logger.LogInformation("收到重启服务请求");

        try
        {
            // 检测是否在 Windows 服务环境下运行
            var isWindowsService = IsRunningAsWindowsService();
            _logger.LogInformation("运行模式: {Mode}", isWindowsService ? "Windows 服务" : "控制台应用");

            if (isWindowsService)
            {
                return RestartWindowsService();
            }
            else
            {
                return RestartConsoleApplication();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "重启服务失败");
            return StatusCode(500, new { error = $"重启服务失败: {ex.Message}" });
        }
    }

    /// <summary>
    /// 检测是否作为 Windows 服务运行
    /// </summary>
    private bool IsRunningAsWindowsService()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return false;
        }

        try
        {
            // Windows 服务进程的父进程通常是 services.exe
            var currentProcess = Process.GetCurrentProcess();
            var parentProcess = GetParentProcess(currentProcess);

            if (parentProcess != null)
            {
                var isService = parentProcess.ProcessName.Equals("services", StringComparison.OrdinalIgnoreCase) ||
                               parentProcess.ProcessName.Equals("services.exe", StringComparison.OrdinalIgnoreCase);
                parentProcess.Dispose();
                return isService;
            }

            // 备用检测方式：检查是否在没有交互式会话中运行
            return Environment.UserInteractive == false &&
                   !Console.IsInputRedirected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 获取父进程
    /// </summary>
    private static Process? GetParentProcess(Process process)
    {
        try
        {
            // 使用 WMI 查询父进程
            var query = $"SELECT ParentProcessId FROM Win32_Process WHERE ProcessId = {process.Id}";
            using var searcher = new System.Management.ManagementObjectSearcher(query);
            using var results = searcher.Get();

            foreach (System.Management.ManagementObject result in results)
            {
                var parentId = Convert.ToInt32(result["ParentProcessId"]);
                return Process.GetProcessById(parentId);
            }
        }
        catch
        {
            // 忽略错误
        }

        return null;
    }

    /// <summary>
    /// 作为 Windows 服务重启
    /// </summary>
    private ActionResult RestartWindowsService()
    {
        var serviceName = GetServiceName();
        if (string.IsNullOrEmpty(serviceName))
        {
            _logger.LogWarning("无法获取服务名称，回退到控制台模式重启");
            return RestartConsoleApplication();
        }

        _logger.LogInformation("服务名称: {ServiceName}", serviceName);

        // 验证服务是否存在且正在运行
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                $"SELECT State FROM Win32_Service WHERE Name = '{serviceName}'");
            using var results = searcher.Get();

            if (results.Count == 0)
            {
                _logger.LogWarning("服务 '{ServiceName}' 未找到，回退到控制台模式", serviceName);
                return RestartConsoleApplication();
            }

            foreach (System.Management.ManagementObject result in results)
            {
                var state = result["State"]?.ToString();
                _logger.LogInformation("服务当前状态: {State}", state);

                if (state?.Equals("Stopped", StringComparison.OrdinalIgnoreCase) == true)
                {
                    _logger.LogWarning("服务已停止，尝试直接启动");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "验证服务状态失败，继续尝试重启");
        }

        // 创建重启脚本并执行
        // 使用 PowerShell 来执行服务重启，确保服务正确注册到 SCM
        var scriptPath = Path.Combine(Path.GetTempPath(), $"restart-{serviceName}-{Guid.NewGuid():N}.ps1");
        var script = $@"
$ErrorActionPreference = 'Stop'
$serviceName = '{serviceName}'
$logPath = Join-Path $env:TEMP 'ReferenceRAG-Restart-{{0}}.log' -f (Get-Date -Format 'yyyyMMdd-HHmmss')

function Write-Log {{
    param([string]$Message)
    $timestamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    $line = '[' + $timestamp + '] ' + $Message
    Write-Host $line
    Add-Content -Path $logPath -Value $line -ErrorAction SilentlyContinue
}}

try {{
    Write-Log ''Starting restart for service: $serviceName''

    # 等待当前服务停止
    Write-Log ''Stopping service...''
    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue

    # 等待服务完全停止
    $maxWait = 30
    $waited = 0
    while ((Get-Service -Name $serviceName -ErrorAction SilentlyContinue).Status -ne ''Stopped'' -and $waited -lt $maxWait) {{
        Start-Sleep -Seconds 1
        $waited++
    }}
    Write-Log ''Service stopped after $waited seconds''

    Write-Log ''Starting service...''
    Start-Service -Name $serviceName

    # 等待服务启动
    $waited = 0
    while ((Get-Service -Name $serviceName -ErrorAction SilentlyContinue).Status -ne ''Running'' -and $waited -lt 30) {{
        Start-Sleep -Seconds 1
        $waited++
    }}

    $finalStatus = (Get-Service -Name $serviceName -ErrorAction SilentlyContinue).Status
    Write-Log ''Service status: $finalStatus''

    if ($finalStatus -ne ''Running'') {{
        Write-Log ''ERROR: Service failed to start''
        exit 1
    }}

    Write-Log ''Service restarted successfully''
}} catch {{
    Write-Log (''ERROR: '' + $_.Exception.Message)
    exit 1
}} finally {{
    # 清理脚本文件
    Start-Sleep -Seconds 2
    Remove-Item -Path $PSCommandPath -Force -ErrorAction SilentlyContinue
}}
";

        System.IO.File.WriteAllText(scriptPath, script);

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        var helperProcess = Process.Start(startInfo);
        if (helperProcess == null)
        {
            System.IO.File.Delete(scriptPath);
            return StatusCode(500, new { error = "无法启动重启辅助进程" });
        }

        _logger.LogInformation("已启动 PowerShell 重启脚本，PID: {ProcessId}", helperProcess.Id);
        helperProcess.Dispose();

        var process = Process.GetCurrentProcess();
        return Ok(new RestartResponse
        {
            Message = "服务正在重启 (Windows 服务模式)",
            OldProcessId = process.Id,
            NewProcessId = 0, // 新进程 PID 需要服务恢复后才能确定
            ProcessPath = serviceName,
            Timestamp = DateTime.UtcNow,
            RestartMode = "WindowsService"
        });
    }

    /// <summary>
    /// 获取当前服务名称
    /// </summary>
    private string? GetServiceName()
    {
        try
        {
            // 方法1: 从环境变量获取 (推荐方式)
            var envServiceName = Environment.GetEnvironmentVariable("REFERENCERAG_SERVICE_NAME");
            if (!string.IsNullOrEmpty(envServiceName))
            {
                return envServiceName;
            }

            // 方法2: 从命令行参数中提取
            var args = Environment.GetCommandLineArgs();
            foreach (var arg in args)
            {
                if (arg.StartsWith("--serviceName=", StringComparison.OrdinalIgnoreCase) ||
                    arg.StartsWith("/serviceName:", StringComparison.OrdinalIgnoreCase))
                {
                    return arg.Split('=', ':')[1];
                }
            }

            // 方法3: 通过 WMI 查询当前进程对应的服务名称
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var currentProcessId = Environment.ProcessId;
                using var searcher = new System.Management.ManagementObjectSearcher(
                    $"SELECT Name FROM Win32_Service WHERE ProcessId = {currentProcessId}");
                using var results = searcher.Get();

                foreach (System.Management.ManagementObject result in results)
                {
                    var serviceName = result["Name"]?.ToString();
                    if (!string.IsNullOrEmpty(serviceName))
                    {
                        _logger.LogInformation("通过 WMI 检测到服务名称: {ServiceName}", serviceName);
                        return serviceName;
                    }
                }
            }

            // 方法4: 从进程路径推断服务名称 (默认为 ReferenceRAG)
            var processPath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(processPath))
            {
                var exeName = Path.GetFileNameWithoutExtension(processPath);
                if (exeName.Contains("ReferenceRAG", StringComparison.OrdinalIgnoreCase))
                {
                    return "ReferenceRAG";
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "获取服务名称失败");
            return null;
        }
    }

    /// <summary>
    /// 作为控制台应用重启
    /// </summary>
    private ActionResult RestartConsoleApplication()
    {
        // 获取当前进程信息
        var process = Process.GetCurrentProcess();
        var processPath = Environment.ProcessPath ?? process.MainModule?.FileName;
        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();

        if (string.IsNullOrEmpty(processPath))
        {
            return StatusCode(500, new { error = "无法获取进程路径" });
        }

        _logger.LogInformation("当前进程: {ProcessPath}, PID: {ProcessId}", processPath, process.Id);

        // 启动新进程
        var startInfo = new ProcessStartInfo
        {
            FileName = processPath,
            Arguments = string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)),
            UseShellExecute = true,
            WorkingDirectory = Environment.CurrentDirectory
        };

        _logger.LogInformation("启动新进程: {ProcessPath} {Args}", processPath, startInfo.Arguments);

        var newProcess = Process.Start(startInfo);

        if (newProcess == null)
        {
            return StatusCode(500, new { error = "启动新进程失败" });
        }

        _logger.LogInformation("新进程已启动，PID: {NewProcessId}", newProcess.Id);

        // 延迟关闭当前进程，确保响应已发送
        _ = Task.Run(async () =>
        {
            await Task.Delay(1000); // 等待 1 秒确保响应发送
            _logger.LogInformation("关闭当前进程...");
            _appLifetime.StopApplication();
        });

        return Ok(new RestartResponse
        {
            Message = "服务正在重启",
            OldProcessId = process.Id,
            NewProcessId = newProcess.Id,
            ProcessPath = processPath,
            Timestamp = DateTime.UtcNow,
            RestartMode = "Console"
        });
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

/// <summary>
/// 重启响应
/// </summary>
public class RestartResponse
{
    /// <summary>
    /// 消息
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// 旧进程 ID
    /// </summary>
    public int OldProcessId { get; set; }

    /// <summary>
    /// 新进程 ID (Windows 服务模式下为 0)
    /// </summary>
    public int NewProcessId { get; set; }

    /// <summary>
    /// 进程路径或服务名称
    /// </summary>
    public string ProcessPath { get; set; } = string.Empty;

    /// <summary>
    /// 时间戳
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// 重启模式: Console 或 WindowsService
    /// </summary>
    public string RestartMode { get; set; } = "Console";
}
