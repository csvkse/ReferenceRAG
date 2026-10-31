using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;

namespace ReferenceRAG.Core.Features.KnowledgeBase.Connectors;

/// <summary>
/// 本地文件夹连接器实现
/// </summary>
public sealed class LocalFolderConnector : IDataConnector
{
    public string Id { get; }
    public string KnowledgeBaseId { get; }
    public string Kind => "Folder";
    public string RootPath { get; }
    public string FilePattern { get; }

    private readonly ILogger<LocalFolderConnector>? _logger;
    private bool _disposed;

    public LocalFolderConnector(
        string id,
        string knowledgeBaseId,
        string rootPath,
        string? filePattern = null,
        ILogger<LocalFolderConnector>? logger = null)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        KnowledgeBaseId = knowledgeBaseId ?? throw new ArgumentNullException(nameof(knowledgeBaseId));
        RootPath = rootPath ?? throw new ArgumentNullException(nameof(rootPath));
        FilePattern = string.IsNullOrWhiteSpace(filePattern) ? "*.*" : filePattern;
        _logger = logger;
    }

    public async IAsyncEnumerable<SourceChange> ScanAsync(
        SyncRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!Directory.Exists(RootPath))
        {
            _logger?.LogWarning("[LocalFolderConnector] 目标目录不存在: {RootPath}", RootPath);
            yield break;
        }

        var patterns = FilePattern.Split([';', '|', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (patterns.Length == 0) patterns = ["*.*"];

        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pattern in patterns)
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(RootPath, pattern, SearchOption.AllDirectories);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[LocalFolderConnector] 枚举文件失败: {Path} ({Pattern})", RootPath, pattern);
                continue;
            }

            foreach (var filePath in files)
            {
                ct.ThrowIfCancellationRequested();

                string sourceKey;
                try
                {
                    sourceKey = Path.GetRelativePath(RootPath, filePath).Replace('\\', '/');
                }
                catch
                {
                    sourceKey = Path.GetFileName(filePath);
                }

                if (!seenKeys.Add(sourceKey)) continue;

                string? revision = null;
                try
                {
                    var fileInfo = new FileInfo(filePath);
                    revision = $"{fileInfo.LastWriteTimeUtc.Ticks}_{fileInfo.Length}";
                }
                catch { }

                yield return new SourceChange(
                    ConnectorId: Id,
                    SourceKey: sourceKey,
                    Kind: ChangeKind.Upsert,
                    Locator: filePath,
                    SourceRevision: revision);

                await Task.Yield();
            }
        }
    }

    public ValueTask<Stream> OpenReadAsync(SourceChange item, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var path = item.Locator;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            path = Path.Combine(RootPath, item.SourceKey.Replace('/', Path.DirectorySeparatorChar));
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"文件未找到: {path}", path);
        }

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return ValueTask.FromResult<Stream>(stream);
    }

    public void Dispose()
    {
        _disposed = true;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// 单文件连接器实现
/// </summary>
public sealed class SingleFileConnector : IDataConnector
{
    public string Id { get; }
    public string KnowledgeBaseId { get; }
    public string Kind => "SingleFile";
    public string FilePath { get; }

    private readonly ILogger<SingleFileConnector>? _logger;
    private bool _disposed;

    public SingleFileConnector(
        string id,
        string knowledgeBaseId,
        string filePath,
        ILogger<SingleFileConnector>? logger = null)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        KnowledgeBaseId = knowledgeBaseId ?? throw new ArgumentNullException(nameof(knowledgeBaseId));
        FilePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        _logger = logger;
    }

    public async IAsyncEnumerable<SourceChange> ScanAsync(
        SyncRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!File.Exists(FilePath))
        {
            _logger?.LogWarning("[SingleFileConnector] 目标文件不存在: {FilePath}", FilePath);
            yield break;
        }

        var sourceKey = Path.GetFileName(FilePath);
        string? revision = null;
        try
        {
            var info = new FileInfo(FilePath);
            revision = $"{info.LastWriteTimeUtc.Ticks}_{info.Length}";
        }
        catch { }

        yield return new SourceChange(
            ConnectorId: Id,
            SourceKey: sourceKey,
            Kind: ChangeKind.Upsert,
            Locator: FilePath,
            SourceRevision: revision);

        await Task.Yield();
    }

    public ValueTask<Stream> OpenReadAsync(SourceChange item, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!File.Exists(FilePath))
        {
            throw new FileNotFoundException($"文件未找到: {FilePath}", FilePath);
        }

        var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return ValueTask.FromResult<Stream>(stream);
    }

    public void Dispose()
    {
        _disposed = true;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
