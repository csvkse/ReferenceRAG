using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using ReferenceRAG.ApiHost.Services;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;

namespace ReferenceRAG.Service.McpTools;

/// <summary>
/// 知识库文件与笔记探索工具（模块化拆分，支持目录树、流式行阅读、大纲解析与 Agent 级文本/正则检索）
/// </summary>
[McpServerToolType]
public class KnowledgeFileTools
{
    private readonly IServiceProvider _serviceProvider;

    public KnowledgeFileTools(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    [McpServerTool, Description("获取指定知识库数据源或子目录下的文件与目录列表结构。用于探索知识库结构、定位笔记文件。")]
    public async Task<string> GetFileTree(
        [Description("数据源名称（可选）。若为空则默认使用第一个可用知识源")] string? sourceName = null,
        [Description("相对子路径（可选），如 \"docs/api\"")] string? subPath = null)
    {
        using var scope = _serviceProvider.CreateScope();
        if (!CheckToolPermission(scope.ServiceProvider, nameof(GetFileTree), out var permErr))
        {
            return JsonSerializer.Serialize(new { success = false, error = permErr });
        }
        var config = scope.ServiceProvider.GetRequiredService<ConfigManager>().Load();

        try
        {
            var allowedSources = GetEffectiveSources(scope.ServiceProvider);

            var targetSource = string.IsNullOrWhiteSpace(sourceName)
                ? config.Sources.FirstOrDefault(s => s.Enabled && (allowedSources == null || allowedSources.Contains(s.Name, StringComparer.OrdinalIgnoreCase)))
                : config.Sources.FirstOrDefault(s => string.Equals(s.Name, sourceName, StringComparison.OrdinalIgnoreCase));

            if (targetSource == null)
            {
                return JsonSerializer.Serialize(new { success = false, error = $"未找到指定的数据源或无权访问: {sourceName}" });
            }

            if (allowedSources != null && !allowedSources.Contains(targetSource.Name, StringComparer.OrdinalIgnoreCase))
            {
                return JsonSerializer.Serialize(new { success = false, error = $"无权限访问数据源: {targetSource.Name}" });
            }

            var root = FileBrowserPath.Normalize(targetSource.Path);
            var targetDir = string.IsNullOrWhiteSpace(subPath) ? root : FileBrowserPath.Join(root, subPath);

            if (!FileBrowserPath.IsWithin(targetDir, root))
            {
                return JsonSerializer.Serialize(new { success = false, error = "路径超出该知识库允许的沙箱范围" });
            }

            if (!Directory.Exists(targetDir))
            {
                return JsonSerializer.Serialize(new { success = false, error = $"目录不存在: {targetDir}" });
            }

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

            return JsonSerializer.Serialize(new
            {
                success = true,
                sourceName = targetSource.Name,
                currentPath = targetDir,
                count = entries.Count,
                entries
            }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
    }

    [McpServerTool, Description("读取知识库中指定笔记的内容。支持指定起止行范围（startLine, endLine），避免一次性读取超长文件撑爆 Token。")]
    public async Task<string> ReadNoteContent(
        [Description("笔记文件相对路径或绝对路径（必填），如 \"docs/architecture.md\" 或完整路径")] string filePath,
        [Description("所属数据源名称（可选）。若提供则在该源沙箱内解析相对路径")] string? sourceName = null,
        [Description("起始行号（可选，从 1 开始，默认 1）")] int startLine = 1,
        [Description("结束行号（可选，默认 200；传 -1 或大数值读取至文件末尾，最大 2MB）")] int endLine = 200)
    {
        using var scope = _serviceProvider.CreateScope();
        if (!CheckToolPermission(scope.ServiceProvider, nameof(ReadNoteContent), out var permErr))
        {
            return JsonSerializer.Serialize(new { success = false, error = permErr });
        }
        var config = scope.ServiceProvider.GetRequiredService<ConfigManager>().Load();
        var fileReader = scope.ServiceProvider.GetService<IFileReaderService>();

        try
        {
            string resolvedPath;
            if (!string.IsNullOrWhiteSpace(sourceName))
            {
                var allowedSources = GetEffectiveSources(scope.ServiceProvider);
                if (allowedSources != null && !allowedSources.Contains(sourceName, StringComparer.OrdinalIgnoreCase))
                {
                    return JsonSerializer.Serialize(new { success = false, error = $"无权限访问数据源: {sourceName}" });
                }

                var targetSource = config.Sources.FirstOrDefault(s => string.Equals(s.Name, sourceName, StringComparison.OrdinalIgnoreCase));
                if (targetSource == null)
                    return JsonSerializer.Serialize(new { success = false, error = $"未找到指定的数据源: {sourceName}" });

                var root = FileBrowserPath.Normalize(targetSource.Path);
                resolvedPath = FileBrowserPath.Join(root, filePath);
                if (!FileBrowserPath.IsWithin(resolvedPath, root))
                    return JsonSerializer.Serialize(new { success = false, error = "文件路径超出该知识库沙箱范围" });
            }
            else
            {
                resolvedPath = FileBrowserPath.Normalize(filePath);
            }

            if (!File.Exists(resolvedPath))
            {
                return JsonSerializer.Serialize(new { success = false, error = $"文件不存在: {resolvedPath}" });
            }

            if (fileReader != null && endLine > 0 && !(startLine == 1 && endLine >= 100000))
            {
                var rangeResult = await fileReader.ReadLineRangeAsync(resolvedPath, startLine, endLine);
                return JsonSerializer.Serialize(new
                {
                    success = true,
                    filePath = resolvedPath,
                    name = rangeResult.Name,
                    startLine = rangeResult.StartLine,
                    endLine = rangeResult.EndLine,
                    totalLines = rangeResult.TotalLines,
                    returnedLines = rangeResult.ReturnedLines,
                    hasMore = rangeResult.HasMore,
                    content = rangeResult.Content
                }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            }

            var info = new FileInfo(resolvedPath);
            if (info.Length > 2 * 1024 * 1024)
            {
                return JsonSerializer.Serialize(new { success = false, error = "文件超过 2MB 大小限制，请使用 startLine 和 endLine 范围读取" });
            }

            var text = await File.ReadAllTextAsync(resolvedPath);
            return JsonSerializer.Serialize(new
            {
                success = true,
                filePath = resolvedPath,
                fileName = info.Name,
                size = info.Length,
                content = text
            }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
    }

    [McpServerTool, Description("获取指定文档的大纲目录（Markdown H1~H6 标题树与对应行号）、Frontmatter 元数据及向量知识库分块索引状态。")]
    public async Task<string> GetNoteOutline(
        [Description("笔记文件相对路径或绝对路径（必填）")] string filePath,
        [Description("所属数据源名称（可选）")] string? sourceName = null)
    {
        using var scope = _serviceProvider.CreateScope();
        if (!CheckToolPermission(scope.ServiceProvider, nameof(GetNoteOutline), out var permErr))
        {
            return JsonSerializer.Serialize(new { success = false, error = permErr });
        }
        var config = scope.ServiceProvider.GetRequiredService<ConfigManager>().Load();
        var fileReader = scope.ServiceProvider.GetService<IFileReaderService>();

        if (fileReader == null)
        {
            return JsonSerializer.Serialize(new { success = false, error = "文件大纲读取服务未注册" });
        }

        try
        {
            string resolvedPath;
            if (!string.IsNullOrWhiteSpace(sourceName))
            {
                var allowedSources = GetEffectiveSources(scope.ServiceProvider);
                if (allowedSources != null && !allowedSources.Contains(sourceName, StringComparer.OrdinalIgnoreCase))
                {
                    return JsonSerializer.Serialize(new { success = false, error = $"无权限访问数据源: {sourceName}" });
                }

                var targetSource = config.Sources.FirstOrDefault(s => string.Equals(s.Name, sourceName, StringComparison.OrdinalIgnoreCase));
                if (targetSource == null)
                    return JsonSerializer.Serialize(new { success = false, error = $"未找到指定的数据源: {sourceName}" });

                var root = FileBrowserPath.Normalize(targetSource.Path);
                resolvedPath = FileBrowserPath.Join(root, filePath);
                if (!FileBrowserPath.IsWithin(resolvedPath, root))
                    return JsonSerializer.Serialize(new { success = false, error = "文件路径超出该知识库沙箱范围" });
            }
            else
            {
                resolvedPath = FileBrowserPath.Normalize(filePath);
            }

            if (!File.Exists(resolvedPath))
            {
                return JsonSerializer.Serialize(new { success = false, error = $"文件不存在: {resolvedPath}" });
            }

            var meta = await fileReader.GetMetadataAsync(resolvedPath);
            return JsonSerializer.Serialize(new
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
            }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
    }

    [McpServerTool, Description("在知识库或源码工程中执行高性能关键字或正则表达式内容检索（类似 ripgrep / grep）。支持指定数据源、文件名过滤、返回带行号的匹配上下文代码块（Hunk）。专为精确查找函数、变量、配置项、错误码或 TODO 设计。")]
    public async Task<string> SearchFileContent(
        [Description("搜索文本或正则表达式（必填）")] string pattern,
        [Description("是否作为正则表达式解析（默认 false，纯文本极速查找）")] bool isRegex = false,
        [Description("是否大小写敏感（默认 false）")] bool caseSensitive = false,
        [Description("限定数据源名称（可选，如 \"Docs\"；留空则在所有允许的数据源中检索）")] string? sourceName = null,
        [Description("相对子目录（可选，如 \"src/api\"）")] string? subPath = null,
        [Description("文件名通配过滤（可选，如 \"*.md\" 或 \"*.cs\"）")] string? filePattern = null,
        [Description("匹配行前后上下文行数（0 ~ 5，默认 1）")] int contextLines = 1,
        [Description("最大匹配命中条数（默认 50，上限 300）")] int maxMatches = 50)
    {
        using var scope = _serviceProvider.CreateScope();
        if (!CheckToolPermission(scope.ServiceProvider, nameof(SearchFileContent), out var permErr))
        {
            return JsonSerializer.Serialize(new { success = false, error = permErr });
        }
        var searchEngine = scope.ServiceProvider.GetService<IFileSearchEngine>();

        if (searchEngine == null)
        {
            return JsonSerializer.Serialize(new { success = false, error = "文件搜索引擎未注册" });
        }

        try
        {
            var allowedSources = GetEffectiveSources(scope.ServiceProvider);
            List<string>? targetSources = null;

            if (!string.IsNullOrWhiteSpace(sourceName))
            {
                if (allowedSources != null && !allowedSources.Contains(sourceName, StringComparer.OrdinalIgnoreCase))
                {
                    return JsonSerializer.Serialize(new { success = false, error = $"无权限访问数据源: {sourceName}" });
                }
                targetSources = new List<string> { sourceName };
            }
            else if (allowedSources != null)
            {
                targetSources = allowedSources.ToList();
            }

            var query = new FileSearchQuery
            {
                Pattern = pattern,
                IsRegex = isRegex,
                CaseSensitive = caseSensitive,
                Sources = targetSources,
                SubPath = subPath,
                FilePattern = filePattern,
                ContextLines = contextLines,
                MaxMatches = maxMatches
            };

            var result = await searchEngine.SearchAsync(query);
            return JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
    }

    [McpServerTool, Description("根据通配符模式快速查找并定位知识库中的文件路径列表（类似 fd-find / glob，零内容读取开销）。")]
    public async Task<string> FindFilesByPattern(
        [Description("文件名或路径通配符（必填，如 \"*.json\"、\"*architecture*.md\"、\"*.cs\"）")] string globPattern,
        [Description("所属数据源名称（可选）")] string? sourceName = null,
        [Description("最大返回文件数（默认 100）")] int maxResults = 100)
    {
        using var scope = _serviceProvider.CreateScope();
        if (!CheckToolPermission(scope.ServiceProvider, nameof(FindFilesByPattern), out var permErr))
        {
            return JsonSerializer.Serialize(new { success = false, error = permErr });
        }
        var searchEngine = scope.ServiceProvider.GetService<IFileSearchEngine>();

        if (searchEngine == null)
        {
            return JsonSerializer.Serialize(new { success = false, error = "文件搜索引擎未注册" });
        }

        try
        {
            var allowedSources = GetEffectiveSources(scope.ServiceProvider);
            if (!string.IsNullOrWhiteSpace(sourceName) && allowedSources != null && !allowedSources.Contains(sourceName, StringComparer.OrdinalIgnoreCase))
            {
                return JsonSerializer.Serialize(new { success = false, error = $"无权限访问数据源: {sourceName}" });
            }

            var files = await searchEngine.FindFilesAsync(sourceName, globPattern, maxResults);
            return JsonSerializer.Serialize(new
            {
                success = true,
                pattern = globPattern,
                count = files.Count,
                files
            }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
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

    private static HashSet<string>? GetEffectiveSources(IServiceProvider sp)
    {
        var httpContextAccessor = sp.GetService<IHttpContextAccessor>();
        var currentKey = httpContextAccessor?.HttpContext?.Items["CurrentApiKey"] as ApiKeyEntity;
        var sources = ApiKeyPermissionMatrix.GetEffectiveAllowedSources(currentKey);
        return sources != null ? new HashSet<string>(sources, StringComparer.OrdinalIgnoreCase) : null;
    }
}
