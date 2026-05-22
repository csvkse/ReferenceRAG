namespace ReferenceRAG.Core.Interfaces;

/// <summary>
/// 文件处理互斥锁接口
/// </summary>
public interface IFileProcessingGuard
{
    bool TryAcquire(string filePath);
    void Release(string filePath);
}
