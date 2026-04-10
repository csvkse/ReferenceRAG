using System.Runtime.InteropServices;

namespace ObsidianRAG.Core.Services;

/// <summary>
/// 跨平台路径工具类 - 处理 Windows 和 WSL 之间的路径转换
/// </summary>
public static class PathUtility
{
    /// <summary>
    /// 检查是否运行在 WSL 环境中
    /// </summary>
    public static bool IsWsl { get; } = CheckIsWsl();

    private static bool CheckIsWsl()
    {
        // 检查是否在 WSL 环境中
        // WSL2 中 /proc/version 会包含 "Microsoft" 或 "WSL"
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var versionContent = File.ReadAllText("/proc/version");
                return versionContent.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) ||
                       versionContent.Contains("WSL", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex)
        {
            // 读取失败（非 Linux 或权限不足），假设不是 WSL
            Console.WriteLine($"[PathUtility] WSL 检测失败: {ex.Message}");
        }
        return false;
    }

    /// <summary>
    /// 将路径转换为当前环境可用的格式
    /// 如果在 WSL 环境中且路径是 Windows 格式 (如 E:\)，转换为 /mnt/e/
    /// </summary>
    public static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;

        // 如果不是 WSL 或不是 Windows 路径，直接返回
        if (!IsWsl || !IsWindowsPath(path))
            return path;

        return ConvertWindowsPathToWsl(path);
    }

    /// <summary>
    /// 检查路径是否是 Windows 格式 (如 E:\ 或 E:)
    /// </summary>
    public static bool IsWindowsPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        // 检查是否是 Windows 路径格式: X:\ 或 X: 开头
        // 例如: E:\, E:, C:\Users\...
        if (path.Length >= 2 && path[1] == ':')
        {
            var firstChar = path[0];
            // 检查是否是盘符 (A-Z, a-z)
            return (firstChar >= 'A' && firstChar <= 'Z') ||
                   (firstChar >= 'a' && firstChar <= 'z');
        }
        return false;
    }

    /// <summary>
    /// 将 Windows 路径转换为 WSL 路径
    /// E:\LinuxWork\Obsidian -> /mnt/e/LinuxWork/Obsidian
    /// </summary>
    public static string ConvertWindowsPathToWsl(string windowsPath)
    {
        if (string.IsNullOrEmpty(windowsPath))
            return windowsPath;

        // 提取盘符并转换为小写
        var driveLetter = windowsPath[0];
        var drivePart = char.ToLower(driveLetter);

        // 移除盘符和冒号，换掉反斜杠为正斜杠
        var remaining = windowsPath.Substring(2).Replace('\\', '/');

        return $"/mnt/{drivePart}{remaining}";
    }

    /// <summary>
    /// 将 WSL 路径转换为 Windows 路径
    /// /mnt/e/LinuxWork/Obsidian -> E:\LinuxWork\Obsidian
    /// </summary>
    public static string ConvertWslPathToWindows(string wslPath)
    {
        if (string.IsNullOrEmpty(wslPath))
            return wslPath;

        // 检查是否是 /mnt/X 格式
        if (!wslPath.StartsWith("/mnt/") || wslPath.Length < 6)
            return wslPath;

        var driveLetter = wslPath[5]; // 获取盘符
        var remaining = wslPath.Substring(6).Replace('/', '\\');

        return $"{char.ToUpper(driveLetter)}:\\{remaining}";
    }
}
