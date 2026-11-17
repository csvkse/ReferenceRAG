using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using ReferenceRAG.ApiHost.Services;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;

namespace ReferenceRAG.Service.McpTools;

/// <summary>
/// 知识库文件与笔记探索工具（目录树、流式行阅读、大纲解析、Agent 级文本/正则检索）。
///
/// 三条硬约束（均由生产实测反推）：
/// 1. 路径必须经 <see cref="IFileAccessPolicy"/>：绝对路径只能落在已注册源根内（原先 `/etc/passwd` 可读）；
/// 2. 源名必须经 <see cref="ISourceCatalog"/>：此前 file_tree 只认 config.Sources，而 list_sources 会给出 KB 连接器名；
/// 3. 结果必须经 <see cref="McpResult"/>：顶层 `content` 会被协议层吞掉。
/// </summary>
[McpServerToolType]
public class KnowledgeFileTools
{
    private readonly IServiceProvider _serviceProvider;

    public KnowledgeFileTools(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    [McpServerTool(Title = "浏览目录树")]
    [Description("获取指定知识库数据源或子目录下的文件与目录列表结构。用于探索知识库结构、定位笔记文件。")]
    public async Task<string> GetFileTree(
        [Description("数据源名称（可选，可来自 list_sources）。若为空则默认使用第一个可用知识源")] string? sourceName = null,
        [Description("相对子路径（可选），如 \"docs/api\"")] string? subPath = null)
    {
        using var scope = _serviceProvider.CreateScope();
        if (!CheckToolPermission(scope.ServiceProvider, nameof(GetFileTree), out var permErr))
            return McpResult.Fail(permErr, McpErrorCodes.Forbidden);

        var allowed = GetAllowedSources(scope.ServiceProvider);

        try
        {
            var sources = await GetCatalog(scope.ServiceProvider).ListAsync();
            var visible = ApplyScope(sources, allowed);

            var target = string.IsNullOrWhiteSpace(sourceName)
                ? visible.FirstOrDefault(s => s.Enabled)
                : visible.FirstOrDefault(s => string.Equals(s.Name, sourceName, StringComparison.OrdinalIgnoreCase));

            if (target == null)
                return McpResult.Fail(
                    string.IsNullOrWhiteSpace(sourceName)
                        ? "当前没有可用的数据源，请通过 list_sources 确认"
                        : $"未找到指定的数据源或无权访问: {sourceName}（可用 list_sources 查看合法名称）",
                    McpErrorCodes.SourceNotFound);

            var root = FileBrowserPath.Normalize(target.Path);
            var targetDir = string.IsNullOrWhiteSpace(subPath) ? root : FileBrowserPath.Join(root, subPath);
            if (!FileBrowserPath.IsWithin(targetDir, root))
                return McpResult.Fail("路径超出该数据源允许的沙箱范围", McpErrorCodes.PathDenied);

            if (!Directory.Exists(targetDir))
                return McpResult.Fail($"目录不存在: {targetDir}", McpErrorCodes.NotFound);

            var dirInfo = new DirectoryInfo(targetDir);
            var entries = new List<object>();
            foreach (var d in dirInfo.EnumerateDirectories())
            {
                if (d.Name.StartsWith('.')) continue;
                entries.Add(new { name = d.Name, isDirectory = true, modified = d.LastWriteTimeUtc });
            }
            foreach (var f in dirInfo.EnumerateFiles())
            {
                entries.Add(new { name = f.Name, isDirectory = false, size = f.Length, modified = f.LastWriteTimeUtc });
            }

            return McpResult.OkBounded(new
            {
                success = true,
                sourceName = target.Name,
                currentPath = targetDir,
                count = entries.Count,
                entries
            });
        }
        catch (Exception ex)
        {
            return McpResult.FailException(ex);
        }
    }

    [McpServerTool(Title = "按行读取文件")]
    [Description("读取知识库中指定笔记的内容。支持指定起止行范围（startLine, endLine），避免一次性读取超长文件撑爆 Token。")]
    public async Task<string> ReadNoteContent(
        [Description("笔记文件相对路径或绝对路径（必填），如 \"docs/architecture.md\" 或完整路径")] string filePath,
        [Description("所属数据源名称（可选，可来自 list_sources）。若提供则在该源沙箱内解析相对路径")] string? sourceName = null,
        [Description("起始行号（可选，从 1 开始，默认 1）")] int startLine = 1,
        [Description("结束行号（可选，默认 200；传 -1 或大数值读取至文件末尾，最大 2MB）")] int endLine = 200)
    {
        using var scope = _serviceProvider.CreateScope();
        if (!CheckToolPermission(scope.ServiceProvider, nameof(ReadNoteContent), out var permErr))
            return McpResult.Fail(permErr, McpErrorCodes.Forbidden);

        var fileReader = scope.ServiceProvider.GetService<IFileReaderService>();
        var allowed = GetAllowedSources(scope.ServiceProvider);

        try
        {
            var resolvedPath = await ResolvePath(scope.ServiceProvider, filePath, sourceName, allowed);

            if (!File.Exists(resolvedPath))
                return McpResult.Fail($"文件不存在: {resolvedPath}", McpErrorCodes.NotFound);

            // 字段名禁止用 content：与 CallToolResult.content 冲突会整段丢失（见 McpResult 注释）。
            if (fileReader != null && endLine > 0 && !(startLine == 1 && endLine >= 100000))
            {
                var rangeResult = await fileReader.ReadLineRangeAsync(resolvedPath, startLine, endLine);
                return McpResult.Ok(new
                {
                    success = true,
                    filePath = resolvedPath,
                    name = rangeResult.Name,
                    startLine = rangeResult.StartLine,
                    endLine = rangeResult.EndLine,
                    totalLines = rangeResult.TotalLines,
                    returnedLines = rangeResult.ReturnedLines,
                    hasMore = rangeResult.HasMore,
                    text = rangeResult.Content
                });
            }

            var info = new FileInfo(resolvedPath);
            if (info.Length > 5 * 1024 * 1024)
                return McpResult.Fail("文件超过 5MB 大小限制，请使用 startLine 和 endLine 范围流式读取", McpErrorCodes.TooLarge);

            var text = await File.ReadAllTextAsync(resolvedPath);
            return McpResult.Ok(new
            {
                success = true,
                filePath = resolvedPath,
                fileName = info.Name,
                size = info.Length,
                text
            });
        }
        catch (FileAccessDeniedException ex)
        {
            return McpResult.Fail(ex.Message, McpErrorCodes.PathDenied);
        }
        catch (Exception ex)
        {
            return McpResult.FailException(ex);
        }
    }

    [McpServerTool(Title = "文件大纲")]
    [Description("获取指定文档的大纲目录（Markdown H1~H6 标题树与对应行号）、Frontmatter 元数据及向量知识库分块索引状态。")]
    public async Task<string> GetNoteOutline(
        [Description("笔记文件相对路径或绝对路径（必填）")] string filePath,
        [Description("所属数据源名称（可选，可来自 list_sources）")] string? sourceName = null)
    {
        using var scope = _serviceProvider.CreateScope();
        if (!CheckToolPermission(scope.ServiceProvider, nameof(GetNoteOutline), out var permErr))
            return McpResult.Fail(permErr, McpErrorCodes.Forbidden);

        var fileReader = scope.ServiceProvider.GetService<IFileReaderService>();
        if (fileReader == null) return McpResult.Fail("文件大纲读取服务未注册", McpErrorCodes.ServiceUnavailable);

        var allowed = GetAllowedSources(scope.ServiceProvider);

        try
        {
            var resolvedPath = await ResolvePath(scope.ServiceProvider, filePath, sourceName, allowed);

            if (!File.Exists(resolvedPath))
                return McpResult.Fail($"文件不存在: {resolvedPath}", McpErrorCodes.NotFound);

            var meta = await fileReader.GetMetadataAsync(resolvedPath);
            return McpResult.Ok(new
            {
                success = true,
                filePath = meta.Path,
                name = meta.Name,
                sizeBytes = meta.SizeBytes,
                totalLines = meta.TotalLines,
                lineEnding = meta.LineEnding,
                frontmatter = meta.Frontmatter,
                ragChunkCount = meta.RagChunkCount,
                ragLastIndexed = meta.RagLastIndexed,
                headings = meta.Headings
            });
        }
        catch (FileAccessDeniedException ex)
        {
            return McpResult.Fail(ex.Message, McpErrorCodes.PathDenied);
        }
        catch (Exception ex)
        {
            return McpResult.FailException(ex);
        }
    }

    [McpServerTool(Title = "文件内容检索")]
    [Description("在知识库或源码工程中执行高性能关键字或正则表达式内容检索（类似 ripgrep / grep）。支持指定数据源、文件名过滤、返回带行号的匹配上下文代码块（Hunk）。专为精确查找函数、变量、配置项、错误码或 TODO 设计。")]
    public async Task<string> SearchFileContent(
        [Description("搜索文本或正则表达式（必填）")] string pattern,
        [Description("是否作为正则表达式解析（默认 false，纯文本极速查找）")] bool isRegex = false,
        [Description("是否大小写敏感（默认 false）")] bool caseSensitive = false,
        [Description("限定数据源名称（可选，如 \"Docs\"；留空则在所有允许的数据源中检索）")] string? sourceName = null,
        [Description("相对子目录（可选，如 \"src/api\"）")] string? subPath = null,
        [Description("文件名通配过滤（可选，如 \"*.md\" 或 \"*.cs\"）")] string? filePattern = null,
        [Description("匹配行前后上下文行数（0 ~ 5，默认 1）")] int contextLines = 1,
        [Description("最大匹配命中条数（默认 50，上限 500，超出按 500 处理）")] int maxMatches = 50)
    {
        using var scope = _serviceProvider.CreateScope();
        if (!CheckToolPermission(scope.ServiceProvider, nameof(SearchFileContent), out var permErr))
            return McpResult.Fail(permErr, McpErrorCodes.Forbidden);

        var searchEngine = scope.ServiceProvider.GetService<IFileSearchEngine>();
        if (searchEngine == null) return McpResult.Fail("文件搜索引擎未注册", McpErrorCodes.ServiceUnavailable);

        try
        {
            var allowed = GetAllowedSources(scope.ServiceProvider);
            List<string>? targetSources = null;

            if (!string.IsNullOrWhiteSpace(sourceName))
            {
                var resolved = await GetCatalog(scope.ServiceProvider).ResolveAsync(sourceName);
                if (resolved == null || IsScopedOut(resolved, allowed))
                    return McpResult.Fail($"无权限访问数据源或不存在: {sourceName}（可用 list_sources 查看合法名称）", McpErrorCodes.SourceNotFound);

                // 连接器名与配置源名可能不同，统一用路径限定搜索引擎。
                targetSources = new List<string> { resolved.Path };
            }
            else if (allowed != null)
            {
                targetSources = allowed.ToList();
            }

            // 边界统一走适配器：0/负数属于调用方笔误，直接拒绝而不是静默夹成 1。
            int effectiveContext, effectiveMax;
            try
            {
                (_, effectiveContext) = McpInputAdapters.ClampInt(nameof(contextLines), contextLines, 0, 5);
                (_, effectiveMax) = McpInputAdapters.ClampInt(nameof(maxMatches), maxMatches, 1, 500);
            }
            catch (McpInputArgumentException ex)
            {
                return McpResult.Fail(ex.Message, McpErrorCodes.InvalidArgument);
            }

            var query = new FileSearchQuery
            {
                Pattern = pattern,
                IsRegex = isRegex,
                CaseSensitive = caseSensitive,
                Sources = targetSources,
                SubPath = subPath,
                FilePattern = filePattern,
                ContextLines = effectiveContext,
                MaxMatches = effectiveMax
            };

            var result = await searchEngine.SearchAsync(query);
            if (!result.Success)
                return McpResult.Fail(result.Error ?? "搜索失败", McpErrorCodes.SearchFailed);

            // 引擎内部已夹取 MaxMatches 到 [1,500]；超预算直接拒绝，避免 439KB 级响应。
            return McpResult.OkBounded(new
            {
                success = true,
                queryPattern = result.QueryPattern,
                isRegex = result.IsRegex,
                requestedContextLines = contextLines,
                effectiveContextLines = effectiveContext,
                requestedMaxMatches = maxMatches,
                effectiveMaxMatches = effectiveMax,
                totalMatches = result.TotalMatches,
                matchedFilesCount = result.MatchedFilesCount,
                totalFilesScanned = result.TotalFilesScanned,
                elapsedMilliseconds = result.ElapsedMilliseconds,
                truncated = result.Truncated,
                error = result.Error,
                files = result.Files
            });
        }
        catch (Exception ex)
        {
            return McpResult.FailException(ex);
        }
    }

    [McpServerTool(Title = "按模式找文件")]
    [Description("根据通配符模式快速查找并定位知识库中的文件路径列表（类似 fd-find / glob，零内容读取开销）。")]
    public async Task<string> FindFilesByPattern(
        [Description("文件名或路径通配符（必填，如 \"*.json\"、\"*architecture*.md\"、\"*.cs\"）")] string globPattern,
        [Description("所属数据源名称（可选，可来自 list_sources）")] string? sourceName = null,
        [Description("最大返回文件数，默认 100，上限 500（超出按 500 处理）")] int maxResults = 100)
    {
        using var scope = _serviceProvider.CreateScope();
        if (!CheckToolPermission(scope.ServiceProvider, nameof(FindFilesByPattern), out var permErr))
            return McpResult.Fail(permErr, McpErrorCodes.Forbidden);

        var searchEngine = scope.ServiceProvider.GetService<IFileSearchEngine>();
        if (searchEngine == null) return McpResult.Fail("文件搜索引擎未注册", McpErrorCodes.ServiceUnavailable);

        try
        {
            var allowed = GetAllowedSources(scope.ServiceProvider);

            if (!string.IsNullOrWhiteSpace(sourceName))
            {
                var resolved = await GetCatalog(scope.ServiceProvider).ResolveAsync(sourceName);
                if (resolved == null || IsScopedOut(resolved, allowed))
                    return McpResult.Fail($"无权限访问数据源或不存在: {sourceName}（可用 list_sources 查看合法名称）", McpErrorCodes.SourceNotFound);

                sourceName = resolved.Name;
            }

            // 硬上限 500：此前 maxResults=5000 会返回 500 条 / 77KB，且描述未声明上限。
            int effectiveMaxResults;
            try
            {
                (_, effectiveMaxResults) = McpInputAdapters.ClampInt(nameof(maxResults), maxResults, 1, 500);
            }
            catch (McpInputArgumentException ex)
            {
                return McpResult.Fail(ex.Message, McpErrorCodes.InvalidArgument);
            }
            var files = await searchEngine.FindFilesAsync(sourceName, globPattern, effectiveMaxResults);
            return McpResult.OkBounded(new
            {
                success = true,
                pattern = globPattern,
                requestedMaxResults = maxResults,
                effectiveMaxResults,
                count = files.Count,
                files
            });
        }
        catch (Exception ex)
        {
            return McpResult.FailException(ex);
        }
    }

    private static async Task<string> ResolvePath(
        IServiceProvider services, string filePath, string? sourceName, HashSet<string>? allowedSources)
    {
        if (!string.IsNullOrWhiteSpace(sourceName))
        {
            var resolved = await GetCatalog(services).ResolveAsync(sourceName);
            if (resolved == null || IsScopedOut(resolved, allowedSources))
                throw new FileAccessDeniedException($"无权限访问数据源或不存在: {sourceName}");

            // 用连接器/配置源的真实根拼相对路径，避免只认 config.Sources 的名字。
            var root = FileBrowserPath.Normalize(resolved.Path);
            var candidate = filePath.StartsWith('/') || Path.IsPathFullyQualified(filePath)
                ? FileBrowserPath.Normalize(filePath)
                : FileBrowserPath.Join(root, filePath);
            if (!FileBrowserPath.IsWithin(candidate, root))
                throw new FileAccessDeniedException("文件路径超出该数据源允许的沙箱范围");
            return candidate;
        }

        var policy = services.GetService<IFileAccessPolicy>()
                     ?? new SourceScopedFileAccessPolicy(new SourceCatalog(services));
        return await policy.ResolveAsync(filePath, null);
    }

    private static bool IsScopedOut(SourceDescriptor source, HashSet<string>? allowed) =>
        allowed != null
        && source.AuthorizationName != null
        && !allowed.Contains(source.AuthorizationName);

    private static ISourceCatalog GetCatalog(IServiceProvider sp) =>
        sp.GetService<ISourceCatalog>() ?? new SourceCatalog(sp);

    /// <summary>按 API Key 授权裁剪可见源（连接器用 AuthorizationName 判定，未继承配置源时交由 KB 层裁决）。</summary>
    private static IReadOnlyList<SourceDescriptor> ApplyScope(
        IReadOnlyList<SourceDescriptor> sources, HashSet<string>? allowed)
    {
        if (allowed == null) return sources;
        return sources
            .Where(s => s.AuthorizationName == null
                        || allowed.Contains(s.AuthorizationName))
            .ToList();
    }

    private static bool CheckToolPermission(IServiceProvider sp, string toolName, out string? error)
    {
        var httpContextAccessor = sp.GetService<IHttpContextAccessor>();
        var currentKey = httpContextAccessor?.HttpContext?.Items["CurrentApiKey"] as ApiKeyEntity;
        if (currentKey != null && !ApiKeyPermissionMatrix.IsMcpToolPermitted(currentKey, toolName))
        {
            error = $"此 API Key 未被授权调用 MCP 工具: {toolName}";
            return false;
        }
        error = null;
        return true;
    }

    private static HashSet<string>? GetAllowedSources(IServiceProvider sp)
    {
        var httpContextAccessor = sp.GetService<IHttpContextAccessor>();
        var currentKey = httpContextAccessor?.HttpContext?.Items["CurrentApiKey"] as ApiKeyEntity;
        var sources = ApiKeyPermissionMatrix.GetEffectiveAllowedSources(currentKey);
        return sources != null ? new HashSet<string>(sources, StringComparer.OrdinalIgnoreCase) : null;
    }
}
