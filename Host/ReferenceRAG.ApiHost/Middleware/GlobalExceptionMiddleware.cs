using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace ReferenceRAG.Service.Middleware;

/// <summary>
/// 全局异常处理中间件：把未处理异常转成结构化 JSON 错误响应。
///
/// 修复的问题：控制器在 try 之外调用 JsonElement.GetString()（或其它未预期异常）时，
/// Kestrel 直接返回 "500 Internal Server Error" 且 Content-Length: 0，
/// 客户端拿不到任何错误原因，只能去翻服务端日志。
/// 这里统一输出 OpenAI 风格的 { "error": { message, type } } 并记录日志。
/// </summary>
public class GlobalExceptionMiddleware
{
    private const string ClientErrorMessage = "服务端内部错误，请稍后重试。若持续失败请查看服务端日志。";

    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionMiddleware> _logger;
    private readonly bool _includeExceptionDetails;

    public GlobalExceptionMiddleware(
        RequestDelegate next,
        ILogger<GlobalExceptionMiddleware> logger,
        IHostEnvironment environment)
    {
        _next = next;
        _logger = logger;
        // 仅开发环境回显异常明细，生产环境只暴露通用文案，避免泄漏内部细节。
        _includeExceptionDetails = environment.IsDevelopment();
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            // 客户端主动断开（刷新页面、取消请求）不是服务端缺陷，不记错误日志。
            if (context.RequestAborted.IsCancellationRequested)
            {
                _logger.LogInformation("请求在处理过程中被客户端中断: {Method} {Path}", context.Request.Method, context.Request.Path);
                return;
            }

            _logger.LogError(ex, "未处理异常: {Method} {Path}", context.Request.Method, context.Request.Path);

            // 响应可能已经开始写入（流式 SSE 已发出若干 chunk），此时无法再改写状态码。
            if (context.Response.HasStarted)
            {
                _logger.LogWarning("响应已开始写入，无法改写状态码，异常仅记录日志: {Path}", context.Request.Path);
                return;
            }

            context.Response.Clear();
            context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            context.Response.ContentType = "application/json; charset=utf-8";

            var payload = JsonSerializer.Serialize(new
            {
                error = new
                {
                    message = _includeExceptionDetails ? ex.Message : ClientErrorMessage,
                    type = "server_error"
                }
            });

            await context.Response.WriteAsync(payload, context.RequestAborted);
        }
    }
}

/// <summary>
/// GlobalExceptionMiddleware 注册扩展。
/// </summary>
public static class GlobalExceptionMiddlewareExtensions
{
    /// <summary>
    /// 注册全局异常处理中间件。应尽早注册，以覆盖后续中间件与控制器的异常。
    /// </summary>
    public static IApplicationBuilder UseGlobalExceptionHandler(this IApplicationBuilder app)
    {
        return app.UseMiddleware<GlobalExceptionMiddleware>();
    }
}
