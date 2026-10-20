namespace ReferenceRAG.Infrastructure.Features.Storage.Bootstrap;

/// <summary>
/// 存储与文件环境适配器接口
/// 用于隔离真实文件系统与操作系统差异，方便单测 Mock 与跨平台原子写入
/// </summary>
public interface IConfigEnvironmentAdapter
{
    /// <summary>
    /// 主配置文件物理绝对路径（如 appsettings.json）
    /// </summary>
    string ConfigPath { get; }

    /// <summary>
    /// 伴生模板文件物理绝对路径（如 appsettings.template.json）
    /// </summary>
    string TemplatePath { get; }

    /// <summary>
    /// 宿主基准根目录
    /// </summary>
    string BaseDirectory { get; }

    /// <summary>
    /// 检查指定文件是否存在
    /// </summary>
    bool FileExists(string path);

    /// <summary>
    /// 读取文件全文内容
    /// </summary>
    string ReadAllText(string path);

    /// <summary>
    /// 原子写入文件（写临时文件 -> Flush -> 替换/重命名）
    /// </summary>
    void WriteAtomic(string path, string content);

    /// <summary>
    /// 移动/重命名文件
    /// </summary>
    void Move(string sourcePath, string destinationPath, bool overwrite = false);

    /// <summary>
    /// 获取文件大小（字节数）
    /// </summary>
    long GetFileSize(string path);

    /// <summary>
    /// 探测目录是否可写
    /// </summary>
    bool IsDirectoryWritable(string directoryPath);
}
