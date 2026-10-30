using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Enumeration;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;

namespace ReferenceRAG.Infrastructure.Features.FileSystem;

/// <summary>
/// 纯托管高性能文件搜索引擎（零原生依赖，支持字串/正则/Glob、二进制探测与 Hunk 合并）
/// </summary>
public sealed class ManagedFileSearchEngine : IFileSearchEngine
{
    private const int BinaryProbeBytes = 512;
    private const long MaxScanFileSizeBytes = 10 * 1024 * 1024; // 10MB
    private const int MaxLineChars = 2048;
    private const int MaxFilesScannedLimit = 2500;

    private static readonly HashSet<string> IgnoredDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".hg", "node_modules", "bin", "obj", "dist", "build", "target",
        ".vs", ".vscode", ".codegraph", ".idea", ".system_generated"
    };

    private readonly ConfigManager _configManager;
    private readonly ILogger<ManagedFileSearchEngine>? _logger;

    public ManagedFileSearchEngine(ConfigManager configManager, ILogger<ManagedFileSearchEngine>? logger = null)
    {
        _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        _logger = logger;
    }

    /// <summary>
    /// 执行全源或指定源的高效文本/正则扫描
    /// </summary>
    public async Task<FileSearchResult> SearchAsync(FileSearchQuery query, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(query.Pattern))
        {
            return new FileSearchResult(
                Success: false,
                QueryPattern: string.Empty,
                IsRegex: query.IsRegex,
                TotalMatches: 0,
                MatchedFilesCount: 0,
                TotalFilesScanned: 0,
                ElapsedMilliseconds: 0,
                Truncated: false,
                Files: new List<FileSearchMatchGroup>(),
                Error: "搜索关键词或正则模式不能为空"
            );
        }

        IFileSearchStrategy strategy;
        try
        {
            strategy = SearchStrategyFactory.CreateStrategy(query.Pattern, query.IsRegex, query.CaseSensitive);
        }
        catch (ArgumentException ex)
        {
            return new FileSearchResult(
                Success: false,
                QueryPattern: query.Pattern,
                IsRegex: query.IsRegex,
                TotalMatches: 0,
                MatchedFilesCount: 0,
                TotalFilesScanned: 0,
                ElapsedMilliseconds: sw.ElapsedMilliseconds,
                Truncated: false,
                Files: new List<FileSearchMatchGroup>(),
                Error: ex.Message
            );
        }

        var config = _configManager.Load();
        var allSources = config.Sources.Where(s => s.Enabled).ToList();

        // 筛选目标数据源
        List<SourceFolder> targetSources;
        if (query.Sources != null && query.Sources.Count > 0)
        {
            var allowedNames = new HashSet<string>(query.Sources, StringComparer.OrdinalIgnoreCase);
            targetSources = allSources.Where(s => allowedNames.Contains(s.Name)).ToList();
        }
        else
        {
            targetSources = allSources;
        }

        if (targetSources.Count == 0)
        {
            return new FileSearchResult(
                Success: true,
                QueryPattern: query.Pattern,
                IsRegex: query.IsRegex,
                TotalMatches: 0,
                MatchedFilesCount: 0,
                TotalFilesScanned: 0,
                ElapsedMilliseconds: sw.ElapsedMilliseconds,
                Truncated: false,
                Files: new List<FileSearchMatchGroup>()
            );
        }

        var matchedGroups = new List<FileSearchMatchGroup>();
        var totalMatches = 0;
        var totalFilesScanned = 0;
        var truncated = false;
        var maxMatches = Math.Clamp(query.MaxMatches, 1, 500);

        foreach (var source in targetSources)
        {
            if (truncated || ct.IsCancellationRequested) break;

            var rootPath = NormalizePath(source.Path);
            if (!Directory.Exists(rootPath)) continue;

            var searchDir = rootPath;
            if (!string.IsNullOrWhiteSpace(query.SubPath))
            {
                var candidate = Path.GetFullPath(Path.Combine(rootPath, query.SubPath.TrimStart('/', '\\')));
                if (IsWithin(candidate, rootPath) && Directory.Exists(candidate))
                {
                    searchDir = candidate;
                }
            }

            var fileCandidates = EnumerateFilesSafe(searchDir, query.FilePattern, ct);

            foreach (var filePath in fileCandidates)
            {
                if (ct.IsCancellationRequested) break;

                totalFilesScanned++;
                if (totalFilesScanned > MaxFilesScannedLimit)
                {
                    truncated = true;
                    break;
                }

                // 1. 文件大小门禁
                var fi = new FileInfo(filePath);
                if (!fi.Exists || fi.Length == 0 || fi.Length > MaxScanFileSizeBytes)
                {
                    continue;
                }

                // 2. 探针嗅探是否为二进制文件
                if (await IsBinaryFileAsync(filePath, ct))
                {
                    continue;
                }

                // 3. 逐行流式游标 + Hunk 有限状态机
                var hunkStateMachine = new ContextHunkStateMachine(query.ContextLines);
                var fileMatches = 0;
                var lineNumber = 0;

                try
                {
                    await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 8192, useAsync: true);
                    using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 8192);

                    while (!ct.IsCancellationRequested)
                    {
                        var rawLine = await reader.ReadLineAsync(ct);
                        if (rawLine == null) break;
                        lineNumber++;

                        var scanLine = rawLine.Length > MaxLineChars ? rawLine[..MaxLineChars] : rawLine;
                        var isMatch = strategy.MatchesLine(scanLine.AsSpan(), out var mIdx, out var mLen);

                        if (isMatch)
                        {
                            fileMatches++;
                            totalMatches++;
                        }

                        hunkStateMachine.FeedLine(lineNumber, rawLine, isMatch, mIdx, mLen);

                        if (totalMatches >= maxMatches)
                        {
                            truncated = true;
                            break;
                        }
                    }

                    var hunks = hunkStateMachine.Finish();
                    if (fileMatches > 0 && hunks.Count > 0)
                    {
                        var relativePath = Path.GetRelativePath(rootPath, filePath).Replace('\\', '/');
                        matchedGroups.Add(new FileSearchMatchGroup(
                            SourceName: source.Name,
                            RelativePath: relativePath,
                            FullPath: filePath.Replace('\\', '/'),
                            MatchCount: fileMatches,
                            Hunks: hunks
                        ));
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "扫描文件失败跳过: {FilePath}", filePath);
                }

                if (truncated) break;
            }
        }

        sw.Stop();

        return new FileSearchResult(
            Success: true,
            QueryPattern: query.Pattern,
            IsRegex: query.IsRegex,
            TotalMatches: totalMatches,
            MatchedFilesCount: matchedGroups.Count,
            TotalFilesScanned: totalFilesScanned,
            ElapsedMilliseconds: sw.ElapsedMilliseconds,
            Truncated: truncated,
            Files: matchedGroups
        );
    }

    /// <summary>
    /// 仅按文件名通配模式快速查找定位文件路径（对标 fd-find）
    /// </summary>
    public Task<List<string>> FindFilesAsync(string? sourceName, string globPattern, int maxResults = 100, CancellationToken ct = default)
    {
        var config = _configManager.Load();
        var allSources = config.Sources.Where(s => s.Enabled).ToList();

        List<SourceFolder> targetSources;
        if (!string.IsNullOrWhiteSpace(sourceName))
        {
            targetSources = allSources.Where(s => string.Equals(s.Name, sourceName, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        else
        {
            targetSources = allSources;
        }

        var results = new List<string>();
        var pattern = string.IsNullOrWhiteSpace(globPattern) ? "*" : globPattern;
        var limit = Math.Clamp(maxResults, 1, 500);

        foreach (var source in targetSources)
        {
            if (results.Count >= limit || ct.IsCancellationRequested) break;

            var root = NormalizePath(source.Path);
            if (!Directory.Exists(root)) continue;

            foreach (var file in EnumerateFilesSafe(root, pattern, ct))
            {
                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                results.Add($"[{source.Name}] {rel}");
                if (results.Count >= limit) break;
            }
        }

        return Task.FromResult(results);
    }

    /// <summary>
    /// 安全遍历目录文件（浅层剪枝忽略黑名单子目录，避免扫描 .git/node_modules 等深坑）
    /// </summary>
    private static IEnumerable<string> EnumerateFilesSafe(string rootDirectory, string? filePattern, CancellationToken ct)
    {
        var stack = new Stack<string>();
        stack.Push(rootDirectory);

        var hasPattern = !string.IsNullOrWhiteSpace(filePattern) && filePattern != "*";

        while (stack.Count > 0 && !ct.IsCancellationRequested)
        {
            var current = stack.Pop();

            // 1. 尝试枚举文件
            IEnumerable<string>? files = null;
            try
            {
                files = Directory.EnumerateFiles(current);
            }
            catch { /* 忽略无权限或已删除目录 */ }

            if (files != null)
            {
                foreach (var file in files)
                {
                    if (hasPattern)
                    {
                        var fileName = Path.GetFileName(file);
                        if (!FileSystemName.MatchesSimpleExpression(filePattern!, fileName, ignoreCase: true))
                        {
                            continue;
                        }
                    }
                    yield return file;
                }
            }

            // 2. 尝试枚举子目录（黑名单浅层剪枝）
            IEnumerable<string>? subdirs = null;
            try
            {
                subdirs = Directory.EnumerateDirectories(current);
            }
            catch { /* 忽略无权限目录 */ }

            if (subdirs != null)
            {
                foreach (var dir in subdirs)
                {
                    var dirName = Path.GetFileName(dir);
                    if (string.IsNullOrEmpty(dirName) || dirName.StartsWith('.') || IgnoredDirectoryNames.Contains(dirName))
                    {
                        continue;
                    }
                    stack.Push(dir);
                }
            }
        }
    }

    /// <summary>
    /// 前 512 字节探测二进制文件（含有 0x00 则判定为二进制）
    /// </summary>
    private static async ValueTask<bool> IsBinaryFileAsync(string filePath, CancellationToken ct)
    {
        try
        {
            await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024, useAsync: true);
            var buffer = new byte[BinaryProbeBytes];
            var bytesRead = await fs.ReadAsync(buffer.AsMemory(0, BinaryProbeBytes), ct);
            if (bytesRead <= 0) return false;

            for (var i = 0; i < bytesRead; i++)
            {
                if (buffer[i] == 0)
                {
                    return true;
                }
            }
            return false;
        }
        catch
        {
            return true; // 无法读取的文件保守视为不可扫描
        }
    }

    private static string NormalizePath(string path)
    {
        var p = Path.GetFullPath(path).Replace('\\', '/');
        return p.EndsWith('/') ? p.TrimEnd('/') : p;
    }

    private static bool IsWithin(string childPath, string parentPath)
    {
        var normChild = NormalizePath(childPath);
        var normParent = NormalizePath(parentPath);
        return normChild.Equals(normParent, StringComparison.OrdinalIgnoreCase) ||
               normChild.StartsWith(normParent + "/", StringComparison.OrdinalIgnoreCase);
    }
}
