using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace ReferenceRAG.Core.Features.HardwareAcceleration.Injection;

/// <summary>
/// Win32 动态环境注入器
/// 利用 SetDllDirectoryW、AddDllDirectory 以及进程级 PATH 变量注入运行库目录
/// </summary>
public class Win32EnvironmentInjector : IEnvironmentInjector
{
    private readonly ILogger<Win32EnvironmentInjector>? _logger;
    private readonly ConcurrentDictionary<string, bool> _injectedDirectories = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _pathLock = new();

    public Win32EnvironmentInjector(ILogger<Win32EnvironmentInjector>? logger = null)
    {
        _logger = logger;
    }

    #region Win32 P/Invoke

    // 注：不使用 SetDllDirectoryW，因为 SetDllDirectory 会全局覆盖主执行目录导致 LLamaSharp/ONNX 等运行库原生相对解析失效
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AddDllDirectory(string NewDirectory);

    #endregion

    public IReadOnlyList<string> InjectDirectories(IEnumerable<string> directories)
    {
        var injectedThisBatch = new List<string>();
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        foreach (var rawDir in directories)
        {
            if (string.IsNullOrWhiteSpace(rawDir)) continue;

            var fullPath = Path.GetFullPath(rawDir.Trim());
            if (!Directory.Exists(fullPath)) continue;

            if (_injectedDirectories.TryAdd(fullPath, true))
            {
                if (isWindows)
                {
                    try
                    {
                        // 尝试添加至进程级 DLL 目录列表（若系统支持 AddDllDirectory）
                        AddDllDirectory(fullPath);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug(ex, "[Win32EnvironmentInjector] P/Invoke 注入异常: {Path}", fullPath);
                    }
                }

                injectedThisBatch.Add(fullPath);
            }
        }

        // 同步更新当前进程环境变量 PATH 与 Linux LD_LIBRARY_PATH
        if (injectedThisBatch.Count > 0)
        {
            lock (_pathLock)
            {
                var pathSeparator = isWindows ? ';' : ':';
                var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
                var existingParts = new HashSet<string>(
                    currentPath.Split(pathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    isWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal
                );

                var toAdd = injectedThisBatch.Where(p => !existingParts.Contains(p)).ToList();
                if (toAdd.Count > 0)
                {
                    var newPath = string.Join(pathSeparator.ToString(), toAdd) + pathSeparator + currentPath;
                    Environment.SetEnvironmentVariable("PATH", newPath, EnvironmentVariableTarget.Process);
                    _logger?.LogInformation("[EnvironmentInjector] 已动态注入运行库路径到进程 PATH: {Paths}", string.Join(pathSeparator.ToString(), toAdd));
                }

                if (!isWindows)
                {
                    var currentLd = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH") ?? "";
                    var existingLdParts = new HashSet<string>(
                        currentLd.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                        StringComparer.Ordinal
                    );
                    var toAddLd = injectedThisBatch.Where(p => !existingLdParts.Contains(p)).ToList();
                    if (toAddLd.Count > 0)
                    {
                        var newLd = string.Join(":", toAddLd) + (string.IsNullOrEmpty(currentLd) ? "" : ":" + currentLd);
                        Environment.SetEnvironmentVariable("LD_LIBRARY_PATH", newLd);
                        _logger?.LogInformation("[EnvironmentInjector] 已动态注入运行库路径到进程 LD_LIBRARY_PATH: {Paths}", string.Join(":", toAddLd));
                    }
                }
            }
        }

        return _injectedDirectories.Keys.ToList();
    }

    public IReadOnlyList<string> GetInjectedDirectories()
    {
        return _injectedDirectories.Keys.ToList();
    }
}
