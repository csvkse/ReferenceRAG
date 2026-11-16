using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ReferenceRAG.ApiHost.Services;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;

namespace ReferenceRAG.Service.McpTools;

/// <summary>已注册的源（配置源 + 知识库连接器）的统一定义。</summary>
/// <param name="AuthorizationName">
/// 用于 API Key 源作用域判定的名称：配置源是自身名称，KB 连接器继承其路径所属配置源的名称；
/// null 表示该路径不在任何配置源内（仅由知识库投影，授权交由 KB 层裁决）。
/// </param>
public sealed record SourceDescriptor(
    string Name,
    string Path,
    bool Enabled,
    string Origin,
    string? KnowledgeBaseId = null,
    string? ConnectorId = null,
    string? AuthorizationName = null);

/// <summary>
/// 源解析的唯一入口。文件类工具曾各自为政：ManagedFileSearchEngine 合并
/// config.Sources 与 KB 连接器，而 get_file_tree / get_source_info 只读 config.Sources，
/// 导致 list_sources 给出的名字在另一个工具里解析不了（生产实测 `Notes - Note`）。
/// </summary>
public interface ISourceCatalog
{
    Task<IReadOnlyList<SourceDescriptor>> ListAsync(CancellationToken ct = default);

    /// <summary>按名称或路径解析（大小写不敏感）；解析不到返回 null。</summary>
    Task<SourceDescriptor?> ResolveAsync(string nameOrPath, CancellationToken ct = default);
}

public sealed class SourceCatalog : ISourceCatalog
{
    private readonly IServiceProvider _services;

    public SourceCatalog(IServiceProvider services) => _services = services;

    public async Task<IReadOnlyList<SourceDescriptor>> ListAsync(CancellationToken ct = default)
    {
        using var scope = _services.CreateScope();
        var config = scope.ServiceProvider.GetRequiredService<ConfigManager>().Load();
        var catalog = scope.ServiceProvider.GetService<ICatalogStore>();

        var list = config.Sources
            .Select(s => new SourceDescriptor(s.Name, s.Path, s.Enabled, "SourceConfig", AuthorizationName: s.Name))
            .ToList();
        if (catalog == null) return list;

        var knownPaths = new HashSet<string>(
            list.Select(s => NormalizePath(s.Path)), StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var kb in await catalog.ListKnowledgeBasesAsync(ct))
            {
                if (!kb.Enabled) continue;
                foreach (var conn in await catalog.GetConnectorsByKnowledgeBaseAsync(kb.Id, ct))
                {
                    if (!conn.Enabled) continue;

                    // 与 REST /api/Sources 的投影行命名保持一致：{KB 名} - {目录名}
                    var folder = Path.GetFileName(conn.TargetPath?.TrimEnd('/', '\\') ?? conn.Id);
                    var display = string.IsNullOrWhiteSpace(kb.Name)
                        ? folder
                        : $"{kb.Name} - {folder}";

                    // 连接器路径若落在某个配置源内，则沿用该配置源的授权名；否则交由 KB 授权裁决。
                    var authName = list.FirstOrDefault(s =>
                        FileBrowserPath.IsWithin(conn.TargetPath, s.Path))?.AuthorizationName;

                    list.Add(new SourceDescriptor(
                        display, conn.TargetPath, conn.Enabled, "KnowledgeBaseOnly", kb.Id, conn.Id, authName));
                    knownPaths.Add(NormalizePath(conn.TargetPath));
                }
            }
        }
        catch
        {
            // 目录服务不可用不应让所有文件工具瘫痪：降级为仅配置源。
        }

        return list;
    }

    public async Task<SourceDescriptor?> ResolveAsync(string nameOrPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(nameOrPath)) return null;
        var list = await ListAsync(ct);

        return list.FirstOrDefault(s =>
                   string.Equals(s.Name, nameOrPath, StringComparison.OrdinalIgnoreCase)) 
               ?? list.FirstOrDefault(s =>
                   string.Equals(NormalizePath(s.Path), NormalizePath(nameOrPath), StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizePath(string? path) =>
        (path ?? string.Empty).TrimEnd('/', '\\').Replace('\\', '/');
}

/// <summary>
/// 文件访问策略：所有读取/枚举类工具都必须经过它，绝对路径只允许落在已注册源根内。
///
/// 背景（P0 安全缺陷）：get_note_outline / read_note_content 在 sourceName 为空时
/// 直接 Normalize(filePath) 而不做包含性校验，生产实测 `/etc/passwd` 与
/// `/app/data/appsettings.json` 均可被读取（后者含 API Key）。
/// </summary>
public interface IFileAccessPolicy
{
    /// <summary>解析并校验路径；越界抛出 <see cref="FileAccessDeniedException"/>。</summary>
    Task<string> ResolveAsync(string filePath, string? sourceName, CancellationToken ct = default);

    /// <summary>把工具收到的绝对路径判定是否位于任一已注册源根内。</summary>
    Task<SourceDescriptor?> FindOwnerAsync(string absolutePath, CancellationToken ct = default);
}

public sealed class FileAccessDeniedException : Exception
{
    public FileAccessDeniedException(string message) : base(message) { }
}

public sealed class SourceScopedFileAccessPolicy : IFileAccessPolicy
{
    private readonly ISourceCatalog _catalog;

    public SourceScopedFileAccessPolicy(ISourceCatalog catalog) => _catalog = catalog;

    public async Task<string> ResolveAsync(string filePath, string? sourceName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new FileAccessDeniedException("文件路径不能为空");

        var sources = await _catalog.ListAsync(ct);

        if (!string.IsNullOrWhiteSpace(sourceName))
        {
            var source = sources.FirstOrDefault(s =>
                             string.Equals(s.Name, sourceName, StringComparison.OrdinalIgnoreCase))
                         ?? throw new FileAccessDeniedException($"未找到指定的数据源: {sourceName}");

            var root = FileBrowserPath.Normalize(source.Path);
            // 允许相对路径：在源沙箱内拼接后再做包含性校验
            var candidate = filePath.StartsWith('/') || Path.IsPathFullyQualified(filePath)
                ? FileBrowserPath.Normalize(filePath)
                : FileBrowserPath.Join(root, filePath);

            if (!FileBrowserPath.IsWithin(candidate, root))
                throw new FileAccessDeniedException("文件路径超出该数据源允许的沙箱范围");
            return candidate;
        }

        var normalized = FileBrowserPath.Normalize(filePath);
        var owner = sources
            .Where(s => s.Enabled)
            .FirstOrDefault(s => FileBrowserPath.IsWithin(normalized, FileBrowserPath.Normalize(s.Path)));

        if (owner == null)
            throw new FileAccessDeniedException(
                $"路径不在任何已注册数据源内: {normalized}。请先调用 list_sources 选择合法源，或传 sourceName 指定源。");

        return normalized;
    }

    public async Task<SourceDescriptor?> FindOwnerAsync(string absolutePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(absolutePath)) return null;
        var normalized = FileBrowserPath.Normalize(absolutePath);
        var sources = await _catalog.ListAsync(ct);
        return sources
            .Where(s => s.Enabled)
            .FirstOrDefault(s => FileBrowserPath.IsWithin(normalized, FileBrowserPath.Normalize(s.Path)));
    }
}
