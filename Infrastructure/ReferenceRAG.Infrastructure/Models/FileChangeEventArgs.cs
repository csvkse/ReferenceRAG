namespace ReferenceRAG.Core.Models;

/// <summary>
/// 文件变动事件参数
/// </summary>
public class FileChangeEventArgs : EventArgs
{
    public string FilePath { get; set; } = string.Empty;
    public string? OldFilePath { get; set; }
    public ChangeType ChangeType { get; set; }
    public DateTime Timestamp { get; set; }
    public string? Source { get; set; }
}

/// <summary>
/// 文件移动结果
/// </summary>
public class FileMoveResult
{
    public string OldPath { get; set; } = string.Empty;
    public string NewPath { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;
}
