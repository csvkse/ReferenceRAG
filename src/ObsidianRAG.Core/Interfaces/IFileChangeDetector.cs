using ObsidianRAG.Core.Models;

namespace ObsidianRAG.Core.Interfaces;

/// <summary>
/// 文件变动检测接口
/// </summary>
public interface IFileChangeDetector
{
    /// <summary>
    /// 文件变更事件
    /// </summary>
    event EventHandler<FileChangeEventArgs>? FileChanged;

    /// <summary>
    /// 文件删除事件
    /// </summary>
    event EventHandler<FileChangeEventArgs>? FileDeleted;

    /// <summary>
    /// 文件重命名事件
    /// </summary>
    event EventHandler<FileChangeEventArgs>? FileRenamed;

    /// <summary>
    /// 计算文件内容哈希
    /// </summary>
    Task<string> ComputeHashAsync(string filePath);

    /// <summary>
    /// 获取文件变动列表
    /// </summary>
    Task<List<FileChangeEventArgs>> GetChangesAsync(string directory, DateTime since);
}
