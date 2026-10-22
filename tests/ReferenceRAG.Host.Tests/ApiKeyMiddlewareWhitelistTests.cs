using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReferenceRAG.ApiHost.Transport;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Service.Controllers;
using ReferenceRAG.Service.Middleware;
using Xunit;

namespace ReferenceRAG.Host.Tests;

public class ApiKeyMiddlewareWhitelistTests
{
    private const string ExpectedKey = "test-secret-key-123";

    private static async Task<WebApplication> CreateTestAppAsync(bool enableAuth = true)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory
        });

        builder.Services.AddInProcessServer(listen: false);
        builder.Services.AddRouting();

        var app = builder.Build();

        app.Use(async (context, next) =>
        {
            if (enableAuth)
            {
                context.Items["ApiKeyEnabled"] = true;
                context.Items["ApiKeyValue"] = ExpectedKey;
            }
            else
            {
                context.Items["ApiKeyEnabled"] = false;
                context.Items["ApiKeyValue"] = null;
            }
            await next();
        });

        app.UseRouting();
        app.UseMiddleware<ApiKeyMiddleware>();

        // 匿名允许端点
        app.MapGet("/api/public/anonymous", [AllowAnonymous] () => Results.Ok(new { message = "anonymous-success" }));

        // 受保护业务 API
        app.MapGet("/api/protected/data", () => Results.Ok(new { message = "protected-success" }));
        app.MapGet("/v1/models", () => Results.Ok(new { message = "models-success" }));
        app.MapGet("/hubs/index", () => Results.Ok(new { message = "hub-success" }));
        app.MapGet("/swagger", () => Results.Ok(new { message = "swagger-ui" }));
        app.MapGet("/swagger/v1/swagger.json", () => Results.Ok(new { message = "swagger-json" }));
        app.MapGet("/api/mcp", () => Results.Ok(new { message = "mcp-success" }));

        // 模拟 SPA Fallback 与静态资源
        app.MapGet("/settings", () => Results.Text("<html>SPA Settings View</html>", "text/html"));
        app.MapGet("/app/styles.css", () => Results.Text("body { color: red; }", "text/css"));
        app.MapGet("/vendor/axios.js", () => Results.Text("console.log('axios')", "application/javascript"));
        app.MapGet("/__diag/status", () => Results.Ok(new { diag = true }));

        await app.StartAsync();
        return app;
    }

    private static async Task<(int StatusCode, string Body, IHeaderDictionary Headers)> DispatchAsync(
        WebApplication app,
        string method,
        string pathAndQuery,
        Dictionary<string, string>? headers = null,
        string? cookieHeader = null)
    {
        var requestHeaders = headers ?? new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(cookieHeader))
        {
            requestHeaders["Cookie"] = cookieHeader;
        }

        using var input = new MemoryStream();
        using var output = new MemoryStream();
        var response = new HttpResponseFeature();

        await app.Services.GetRequiredService<InProcessServer>().DispatchAsync(
            method,
            pathAndQuery,
            requestHeaders,
            input,
            output,
            response,
            default);

        var body = Encoding.UTF8.GetString(output.ToArray());
        return (response.StatusCode, body, response.Headers);
    }

    [Fact]
    public async Task AuthCheck_AlwaysAllowed_ReturnsTrueWhenEnabled()
    {
        await using var app = await CreateTestAppAsync(enableAuth: true);
        var (status, body, _) = await DispatchAsync(app, "GET", "/api/auth/check");

        Assert.Equal(200, status);
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("authRequired").GetBoolean());
    }

    [Fact]
    public async Task AllowAnonymousEndpoint_AllowedWithoutKey()
    {
        await using var app = await CreateTestAppAsync(enableAuth: true);
        var (status, body, _) = await DispatchAsync(app, "GET", "/api/public/anonymous");

        Assert.Equal(200, status);
        Assert.Contains("anonymous-success", body);
    }

    [Fact]
    public async Task ProtectedEndpoints_WithoutKey_Returns401()
    {
        await using var app = await CreateTestAppAsync(enableAuth: true);

        // 保护所有普通 API
        var (s1, _, _) = await DispatchAsync(app, "GET", "/api/protected/data");
        Assert.Equal(401, s1);

        // 保护 /v1/*
        var (s2, _, _) = await DispatchAsync(app, "GET", "/v1/models");
        Assert.Equal(401, s2);

        // 保护 SignalR /hubs/index
        var (s3, _, _) = await DispatchAsync(app, "GET", "/hubs/index");
        Assert.Equal(401, s3);

        // 保护 Swagger
        var (s4, _, _) = await DispatchAsync(app, "GET", "/swagger");
        Assert.Equal(401, s4);

        // 保护 MCP
        var (s5, _, _) = await DispatchAsync(app, "GET", "/api/mcp");
        Assert.Equal(401, s5);
    }

    [Fact]
    public async Task ProtectedEndpoint_WithWrongKey_Returns403()
    {
        await using var app = await CreateTestAppAsync(enableAuth: true);
        var headers = new Dictionary<string, string> { { "X-API-Key", "wrong-key" } };
        var (status, _, _) = await DispatchAsync(app, "GET", "/api/protected/data", headers);

        Assert.Equal(403, status);
    }

    [Fact]
    public async Task ProtectedEndpoint_WithValidHeader_Returns200()
    {
        await using var app = await CreateTestAppAsync(enableAuth: true);
        var headers = new Dictionary<string, string> { { "X-API-Key", ExpectedKey } };
        var (status, body, _) = await DispatchAsync(app, "GET", "/api/protected/data", headers);

        Assert.Equal(200, status);
        Assert.Contains("protected-success", body);
    }

    [Fact]
    public async Task ProtectedEndpoint_WithBearerToken_Returns200()
    {
        await using var app = await CreateTestAppAsync(enableAuth: true);
        var headers = new Dictionary<string, string> { { "Authorization", $"Bearer {ExpectedKey}" } };
        var (status, body, _) = await DispatchAsync(app, "GET", "/api/protected/data", headers);

        Assert.Equal(200, status);
        Assert.Contains("protected-success", body);
    }

    [Fact]
    public async Task HubEndpoint_WithQueryAccessToken_Returns200()
    {
        await using var app = await CreateTestAppAsync(enableAuth: true);
        var (status, body, _) = await DispatchAsync(app, "GET", $"/hubs/index?access_token={ExpectedKey}");

        Assert.Equal(200, status);
        Assert.Contains("hub-success", body);
    }

    [Fact]
    public async Task SwaggerEndpoint_WithQueryApiKey_Returns200_AndSetsCookie()
    {
        await using var app = await CreateTestAppAsync(enableAuth: true);
        var (status, body, headers) = await DispatchAsync(app, "GET", $"/swagger?apiKey={ExpectedKey}");

        Assert.Equal(200, status);
        Assert.Contains("swagger-ui", body);

        // 校验是否正确注入 Session Cookie
        Assert.True(headers.TryGetValue("Set-Cookie", out var setCookie));
        Assert.Contains("reference_rag_api_key=", setCookie.ToString());
    }

    [Fact]
    public async Task SwaggerJson_WithCookie_Returns200()
    {
        await using var app = await CreateTestAppAsync(enableAuth: true);
        var (status, body, _) = await DispatchAsync(
            app,
            "GET",
            "/swagger/v1/swagger.json",
            cookieHeader: $"reference_rag_api_key={ExpectedKey}");

        Assert.Equal(200, status);
        Assert.Contains("swagger-json", body);
    }

    [Fact]
    public async Task WhitelistedSpaAndAssets_AllowedWithoutKey()
    {
        await using var app = await CreateTestAppAsync(enableAuth: true);

        // SPA 页面路由
        var (s1, b1, _) = await DispatchAsync(app, "GET", "/settings");
        Assert.Equal(200, s1);
        Assert.Contains("SPA Settings View", b1);

        // 静态资源文件
        var (s2, b2, _) = await DispatchAsync(app, "GET", "/vendor/axios.js");
        Assert.Equal(200, s2);
        Assert.Contains("axios", b2);

        // 诊断通道放行（由诊断中间件独立负责）
        var (s3, b3, _) = await DispatchAsync(app, "GET", "/__diag/status");
        Assert.Equal(200, s3);
        Assert.Contains("diag", b3);
    }

    [Fact]
    public async Task DisabledAuthMode_AllEndpointsAccessibleWithoutKey()
    {
        await using var app = await CreateTestAppAsync(enableAuth: false);

        var (s1, _, _) = await DispatchAsync(app, "GET", "/api/protected/data");
        Assert.Equal(200, s1);

        var (s2, _, _) = await DispatchAsync(app, "GET", "/hubs/index");
        Assert.Equal(200, s2);

        var (s3, _, _) = await DispatchAsync(app, "GET", "/swagger");
        Assert.Equal(200, s3);
    }

    [Fact]
    public async Task SettingsController_Get_DesensitizesKeys_AndSavePreservesMaskedKeys()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"settings-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var origDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(tempDir);

        try
        {
            var configManager = new ConfigManager();
            var original = new ObsidianRagConfig
            {
                Service = new ServiceConfig
                {
                    ApiKey = "SuperSecretServiceKey123",
                    DiagKey = "DiagAccessKeySecret999"
                },
                Chat = new ChatModelConfig
                {
                    OpenAiApi = new OpenAiApiChatProfile
                    {
                        ApiKey = "sk-live-thirdparty-model-key"
                    }
                }
            };
            configManager.Save(original);

            var controller = new SettingsController(
                configManager,
                new Moq.Mock<IModelManager>().Object,
                NullLogger<SettingsController>.Instance);

            // 1. GET 验证脱敏
            var getResult = controller.Get();
            var returnedConfig = Assert.IsType<ObsidianRagConfig>(Assert.IsType<OkObjectResult>(getResult.Result).Value);

            Assert.True(returnedConfig.Chat.OpenAiApi.ApiKeyConfigured);
            Assert.Contains("****", returnedConfig.Chat.OpenAiApi.ApiKey);
            Assert.DoesNotContain("sk-live-thirdparty-model-key", returnedConfig.Chat.OpenAiApi.ApiKey);

            Assert.Contains("****", returnedConfig.Service.ApiKey);
            Assert.DoesNotContain("SuperSecretServiceKey123", returnedConfig.Service.ApiKey);

            // 2. POST 模拟前端未改动密码直接保存（提交掩码值）
            var postResult = await controller.Save(returnedConfig);
            Assert.IsType<OkObjectResult>(postResult.Result);

            // 3. 校验磁盘持久化依然是原有真实密钥，未被掩码覆盖
            var saved = configManager.Load();
            Assert.Equal("SuperSecretServiceKey123", saved.Service.ApiKey);
            Assert.Equal("DiagAccessKeySecret999", saved.Service.DiagKey);
            Assert.Equal("sk-live-thirdparty-model-key", saved.Chat.OpenAiApi.ApiKey);
        }
        finally
        {
            Directory.SetCurrentDirectory(origDir);
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}
