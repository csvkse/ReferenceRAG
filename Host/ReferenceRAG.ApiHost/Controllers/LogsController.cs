using Microsoft.AspNetCore.Mvc;
using ReferenceRAG.ApiHost.Features.Logging.Models;
using ReferenceRAG.ApiHost.Features.Logging.Services;

namespace ReferenceRAG.Service.Controllers;

/// <summary>
/// 开发工具：日志管理与审计接口 (供开发工具菜单下的日志页面使用)
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class LogsController : ControllerBase
{
    private readonly ILogFileService _logFileService;
    private readonly IOperationLogStore _operationLogStore;

    public LogsController(ILogFileService logFileService, IOperationLogStore operationLogStore)
    {
        _logFileService = logFileService;
        _operationLogStore = operationLogStore;
    }

    /// <summary>
    /// 获取可用日志文件列表（按时间倒序）
    /// </summary>
    [HttpGet("Files")]
    public ActionResult<IReadOnlyList<LogFileInfo>> GetFiles()
    {
        var files = _logFileService.GetLogFiles();
        return Ok(files);
    }

    /// <summary>
    /// 读取指定日志文件末尾 N 行（默认 300 行，上限 5000 行）
    /// </summary>
    [HttpGet("Files/{name}")]
    public async Task<ActionResult<LogContentResult>> GetFileContent(
        string name,
        [FromQuery] int tail = 300,
        CancellationToken cancellationToken = default)
    {
        var result = await _logFileService.ReadTailAsync(name, tail, cancellationToken);
        if (result == null)
        {
            return NotFound(new { error = $"日志文件 '{name}' 不存在或文件名不合法" });
        }

        return Ok(result);
    }

    /// <summary>
    /// 下载完整的日志文件
    /// </summary>
    [HttpGet("Files/{name}/download")]
    public IActionResult DownloadFile(string name)
    {
        var file = _logFileService.OpenRead(name);
        if (file == null)
        {
            return NotFound(new { error = $"日志文件 '{name}' 不存在或文件名不合法" });
        }

        return File(file.Value.Stream, file.Value.ContentType, file.Value.DownloadName, enableRangeProcessing: true);
    }

    /// <summary>
    /// 分页检索操作审计日志
    /// </summary>
    [HttpGet("Operations")]
    public async Task<ActionResult<PagedResult<OperationLogEntry>>> GetOperations(
        [FromQuery] OperationLogQuery query,
        CancellationToken cancellationToken = default)
    {
        var result = await _operationLogStore.QueryAsync(query, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// 记录自定义操作审计日志
    /// </summary>
    [HttpPost("Operations")]
    public async Task<ActionResult> RecordOperation(
        [FromBody] OperationLogEntry entry,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(entry.Action))
        {
            return BadRequest(new { error = "操作名称 (Action) 不能为空" });
        }

        var clientIp = HttpContext.Connection.RemoteIpAddress?.ToString();
        var record = new OperationLogEntry
        {
            Action = entry.Action,
            TargetType = entry.TargetType,
            TargetName = entry.TargetName,
            Detail = entry.Detail,
            ClientIp = entry.ClientIp ?? clientIp,
            Success = entry.Success,
            Timestamp = entry.Timestamp == default ? DateTime.UtcNow : entry.Timestamp
        };

        var id = await _operationLogStore.RecordAsync(record, cancellationToken);
        return Ok(new { id, message = "操作日志记录成功" });
    }
}
