namespace ReferenceRAG.Core.Features.HardwareAcceleration.Injection;

/// <summary>
/// 运行库动态环境注入器接口
/// </summary>
public interface IEnvironmentInjector
{
    /// <summary>
    /// 将指定目录列表注入当前进程的 DLL 搜索路径（Win32 SetDllDirectory / PATH）
    /// </summary>
    /// <param name="directories">待注入目录列表</param>
    /// <returns>注入成功的目录列表</returns>
    IReadOnlyList<string> InjectDirectories(IEnumerable<string> directories);

    /// <summary>
    /// 获取当前已注入的所有目录
    /// </summary>
    IReadOnlyList<string> GetInjectedDirectories();
}
