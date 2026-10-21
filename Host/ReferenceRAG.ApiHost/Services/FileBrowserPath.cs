using System.IO;

namespace ReferenceRAG.ApiHost.Services;

public static class FileBrowserPath
{
    private static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return "/";
        }

        if (input.Length > 4096 || input.Any(char.IsControl))
        {
            throw new ArgumentException("路径过长或包含控制字符");
        }

        var path = input.Trim().Replace('\\', '/');

        if (OperatingSystem.IsWindows())
        {
            if (path == "/") return "/";

            if (path.StartsWith("//?", StringComparison.Ordinal) || path.StartsWith("//.", StringComparison.Ordinal)
                || path.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0)
            {
                throw new ArgumentException("不支持设备路径或包含非法字符");
            }

            // 如果只传盘符如 "C:"，补全斜杠
            if (path.Length == 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
            {
                path += "/";
            }

            if (!Path.IsPathFullyQualified(path) && !path.StartsWith('/'))
            {
                path = Path.GetFullPath(path).Replace('\\', '/');
            }

            var full = Path.GetFullPath(path).Replace('\\', '/');
            var root = Path.GetPathRoot(full)?.Replace('\\', '/').TrimEnd('/') + "/";
            if (string.IsNullOrEmpty(root))
            {
                return full.TrimEnd('/');
            }

            return full.TrimEnd('/').Equals(root.TrimEnd('/'), Comparison) ? root : full.TrimEnd('/');
        }

        // Linux / Unix 环境
        var segments = new List<string>();
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..")
            {
                if (segments.Count > 0) segments.RemoveAt(segments.Count - 1);
            }
            else
            {
                segments.Add(segment);
            }
        }

        var normalized = "/" + string.Join('/', segments);
        return normalized;
    }

    public static bool IsRoot(string path)
    {
        if (path == "/") return true;
        if (OperatingSystem.IsWindows())
        {
            var root = Path.GetPathRoot(path)?.Replace('\\', '/').TrimEnd('/');
            return !string.IsNullOrEmpty(root) && path.TrimEnd('/').Equals(root, Comparison);
        }
        return false;
    }

    public static string? Parent(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "/") return null;
        if (IsRoot(path))
        {
            // 在 Windows 上，盘符根 (如 C:/) 的上一级是虚拟根 "/" (磁盘列表)
            return "/";
        }

        if (OperatingSystem.IsWindows())
        {
            var parent = Path.GetDirectoryName(path.TrimEnd('/'));
            if (string.IsNullOrEmpty(parent)) return "/";
            return Normalize(parent);
        }

        var index = path.TrimEnd('/').LastIndexOf('/');
        return index <= 0 ? "/" : path[..index];
    }

    public static string Name(string path)
    {
        var clean = path.Replace('\\', '/').TrimEnd('/');
        if (clean == "" || clean == "/") return OperatingSystem.IsWindows() ? "磁盘" : "/";
        if (IsRoot(path)) return path.Replace('\\', '/');
        var idx = clean.LastIndexOf('/');
        return idx >= 0 ? clean[(idx + 1)..] : clean;
    }

    public static string Join(string directory, string name)
    {
        var dir = directory.Replace('\\', '/').TrimEnd('/');
        return dir + "/" + name.TrimStart('/');
    }
}
