using Microsoft.Win32;
using System.IO;
using System.Text.Json;

namespace ReferenceRAG.Desktop;

/// <summary>
/// 管理开机自启动（注册表）和启动行为（本地配置）。
/// </summary>
public static class StartupManager
{
    private const string RegKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RegValueName = "ReferenceRAG";

    private static readonly string SettingsPath =
        Path.Combine(AppContext.BaseDirectory, "desktop-settings.json");

    // ── 开机自启动 ──────────────────────────────────────────────

    public static bool GetAutoStart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegKeyPath, writable: false);
        return key?.GetValue(RegValueName) != null;
    }

    public static void SetAutoStart(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegKeyPath, writable: true);
        if (key == null) return;

        if (enabled)
        {
            // 单文件发布时 Assembly.Location 为空，用 ProcessPath 或 BaseDirectory 推断
            var exe = Environment.ProcessPath
                ?? Path.Combine(AppContext.BaseDirectory, "ReferenceRAG.Desktop.exe");
            key.SetValue(RegValueName, $"\"{exe}\" --minimized");
        }
        else
        {
            key.DeleteValue(RegValueName, throwOnMissingValue: false);
        }
    }

    // ── 最小化启动 ──────────────────────────────────────────────

    public static bool GetStartMinimized()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return false;
            var s = JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(SettingsPath));
            return s?.StartMinimized ?? false;
        }
        catch { return false; }
    }

    public static void SetStartMinimized(bool value)
    {
        // 保留其他字段
        DesktopSettings existing = new();
        try
        {
            if (File.Exists(SettingsPath))
                existing = JsonSerializer.Deserialize<DesktopSettings>(
                    File.ReadAllText(SettingsPath)) ?? new();
        }
        catch { }

        File.WriteAllText(SettingsPath,
            JsonSerializer.Serialize(existing with { StartMinimized = value },
                new JsonSerializerOptions { WriteIndented = true }));
    }

    // ── 内部模型 ─────────────────────────────────────────────────

    private record DesktopSettings
    {
        public bool StartMinimized { get; init; } = false;
    }
}
