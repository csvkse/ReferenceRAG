namespace ReferenceRAG.Infrastructure.Features.Database;

using System;
using System.IO;

/// <summary>Prevents Web and Desktop from running independent writers against the same index directory.</summary>
public sealed class DataDirectoryLease : IDisposable
{
    private FileStream? _lease;

    public void Acquire(string dataPath)
    {
        if (_lease != null) return;
        var directory = Path.GetFullPath(dataPath);

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new UnauthorizedAccessException(
                $"无法创建或访问数据目录 '{directory}'：缺少文件系统权限。若在 Docker 容器中运行，请确保挂载卷目录所有权与权限正确（例如宿主机执行: chown -R 1000:1000 <host_dir>，或在 docker run 中添加环境变量 -e PUID=0 -e PGID=0）。", ex);
        }

        try
        {
            _lease = new FileStream(
                Path.Combine(directory, ".referencerag-writer.lock"),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new UnauthorizedAccessException(
                $"无法获取数据目录锁：对数据目录 '{directory}' 缺少写权限。若在 Docker 容器中运行，请确保挂载卷目录所有权与权限正确（例如宿主机执行: chown -R 1000:1000 <host_dir>，或在 docker run 中添加环境变量 -e PUID=0 -e PGID=0）。", ex);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException("此数据目录已被另一个 ReferenceRAG 宿主使用，请关闭另一宿主或配置独立数据目录。", ex);
        }
    }

    public void Dispose()
    {
        _lease?.Dispose();
        _lease = null;
    }
}
