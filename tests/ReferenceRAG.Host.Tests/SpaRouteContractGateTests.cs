using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace ReferenceRAG.Host.Tests;

/// <summary>
/// SPA 深链路由一致性门禁（G-SPA-1）。
///
/// 背景：后端 SPA fallback 列表曾写 "/api-keys"，而前端 router 的 path 是 "apikeys"。
/// 请求 /api-keys 返回 200 + index.html，但 Vue Router 匹配不到任何路由，页面空白 —— 
/// 状态码和 Content-Type 都"正常"，只有真实渲染才暴露。
/// </summary>
public sealed class SpaRouteContractGateTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReferenceRAG.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string ReadRepoFile(params string[] relative)
        => File.ReadAllText(Path.Combine(RepoRoot(), Path.Combine(relative)));

    [Fact]
    public void SpaFallbackRoutes_MatchFrontendRouterPaths()
    {
        var hostSrc = ReadRepoFile("Host", "ReferenceRAG.ApiHost", "Extensions", "RagWebHostExtensions.cs");
        var routerSrc = ReadRepoFile("Business", "ReferenceRAG.Business", "wwwroot", "app", "router", "index.js");

        // 后端 MapGet 列表
        var fallbackBlock = Regex.Match(hostSrc, @"foreach \(var route in new\[\] \{([^}]+)\}");
        Assert.True(fallbackBlock.Success, "未找到 SPA fallback 路由列表");
        var backendRoutes = fallbackBlock.Groups[1].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim().Trim('"'))
            .Where(s => s.StartsWith("/") && s.Length > 1)
            .ToList();

        // 前端 router 的全部 path（顶层 /login 与 children 都要算；alias 也收）
        var frontendPaths = Regex.Matches(routerSrc, @"path: '([a-z0-9/-]+)'")
            .Select(m => "/" + m.Groups[1].Value.TrimStart('/'))
            .Concat(Regex.Matches(routerSrc, @"alias: '([a-z0-9-/]+)'")
                .Select(m => "/" + m.Groups[1].Value.TrimStart('/')))
            .ToList();

        var missing = backendRoutes.Where(r => !frontendPaths.Contains(r, StringComparer.OrdinalIgnoreCase)).ToList();
        Assert.True(missing.Count == 0,
            "后端 SPA fallback 列了前端不存在的路径（深链会 200 但空白）：" + string.Join(", ", missing));
    }

    [Fact]
    public void ApiKeysPage_HasBothDeepLinkForms()
    {
        // 两个入口都要能打开：侧边栏用 name=ApiKeys（router path apikeys），
        // 历史/文档里用过 /api-keys —— 必须 alias，否则一边是死链。
        var routerSrc = ReadRepoFile("Business", "ReferenceRAG.Business", "wwwroot", "app", "router", "index.js");
        Assert.Contains("path: 'apikeys'", routerSrc);
        Assert.Contains("alias: '/api-keys'", routerSrc);
    }
}
