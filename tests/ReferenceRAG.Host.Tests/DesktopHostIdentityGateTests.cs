using System;
using System.IO;
using Xunit;

namespace ReferenceRAG.Host.Tests;

/// <summary>
/// 桌面宿主高分屏图标与原生托盘菜单门禁测试（对标 infiniReq DesktopHostIdentityGateTests）：
/// 确保 ReferenceRAG.DesktopHost 具备多分辨率高清 .ico 图标、PerMonitorV2 清单、
/// AppUserModelID、DPI 感知窗口图标注入与原生 Win32 托盘菜单。
/// </summary>
public sealed class DesktopHostIdentityGateTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    [Fact]
    public void DesktopHostCsproj_ConfiguresApplicationIconManifestAndEmbeddedResources()
    {
        var csprojPath = Path.Combine(RepoRoot, "Host", "ReferenceRAG.DesktopHost", "ReferenceRAG.DesktopHost.csproj");
        Assert.True(File.Exists(csprojPath), $"Expected {csprojPath} to exist");

        var content = File.ReadAllText(csprojPath);
        Assert.Contains("<ApplicationIcon>Assets/icon.ico</ApplicationIcon>", content, StringComparison.Ordinal);
        Assert.Contains("<ApplicationManifest>app.manifest</ApplicationManifest>", content, StringComparison.Ordinal);
        Assert.Contains("<EmbeddedResource Include=\"Assets/icon.ico\" LogicalName=\"app.ico\" />", content, StringComparison.Ordinal);
        Assert.Contains("<EmbeddedResource Include=\"Assets/window-icon.ico\" LogicalName=\"window.ico\" />", content, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopHostManifest_ConfiguresPerMonitorV2DpiAndCommonControlsV6()
    {
        var manifestPath = Path.Combine(RepoRoot, "Host", "ReferenceRAG.DesktopHost", "app.manifest");
        Assert.True(File.Exists(manifestPath), $"Expected {manifestPath} to exist");

        var content = File.ReadAllText(manifestPath);
        Assert.Contains("PerMonitorV2", content, StringComparison.Ordinal);
        Assert.Contains("Microsoft.Windows.Common-Controls", content, StringComparison.Ordinal);
        Assert.Contains("version=\"6.0.0.0\"", content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("icon.ico")]
    [InlineData("window-icon.ico")]
    [InlineData("tray-icon.ico")]
    public void DesktopHostIcons_ExistAndHaveValidMultiFrameHiDpiIcoHeader(string fileName)
    {
        var icoPath = Path.Combine(RepoRoot, "Host", "ReferenceRAG.DesktopHost", "Assets", fileName);
        Assert.True(File.Exists(icoPath), $"Expected {icoPath} to exist");

        var bytes = File.ReadAllBytes(icoPath);
        Assert.True(bytes.Length > 30_000, $"Expected multi-resolution HiDPI .ico file (> 30 KB), got {bytes.Length} bytes for {fileName}");
        // ICO magic header: 00 00 01 00
        Assert.Equal(0, bytes[0]);
        Assert.Equal(0, bytes[1]);
        Assert.Equal(1, bytes[2]);
        Assert.Equal(0, bytes[3]);

        // Image count in ICO directory header (uint16 little-endian at offset 4)
        var imageCount = bytes[4] | (bytes[5] << 8);
        Assert.True(imageCount >= 7, $"Expected at least 7 icon sizes (16..256) in {fileName}, got {imageCount}");
    }

    [Fact]
    public void DesktopHostProgram_AppliesAumidDpiAwarenessWindowIconAndNativeTray()
    {
        var programPath = Path.Combine(RepoRoot, "Host", "ReferenceRAG.DesktopHost", "Program.cs");
        var windowIconHelperPath = Path.Combine(RepoRoot, "Host", "ReferenceRAG.DesktopHost", "Platform", "WindowsWindowIcon.cs");
        var trayHelperPath = Path.Combine(RepoRoot, "Host", "ReferenceRAG.DesktopHost", "Platform", "TrayIconManager.cs");
        var shortcutGuardPath = Path.Combine(RepoRoot, "Host", "ReferenceRAG.DesktopHost", "Platform", "ShortcutGuard.cs");

        Assert.True(File.Exists(programPath));
        Assert.True(File.Exists(windowIconHelperPath));
        Assert.True(File.Exists(trayHelperPath));
        Assert.True(File.Exists(shortcutGuardPath));

        var programContent = File.ReadAllText(programPath);
        Assert.Contains("SetCurrentProcessExplicitAppUserModelID", programContent, StringComparison.Ordinal);
        Assert.Contains("SetProcessDpiAwarenessContext", programContent, StringComparison.Ordinal);
        Assert.Contains("ShortcutGuard.EnsureValid", programContent, StringComparison.Ordinal);
        Assert.Contains("WindowsWindowIcon.Apply", programContent, StringComparison.Ordinal);
        Assert.Contains("TrayIconManager.RunMessageLoop", programContent, StringComparison.Ordinal);
        Assert.DoesNotContain("new ContextMenuStrip()", programContent, StringComparison.Ordinal);

        var windowIconContent = File.ReadAllText(windowIconHelperPath);
        Assert.Contains("WM_SETICON", windowIconContent, StringComparison.Ordinal);
        Assert.Contains("SetClassLongPtrW", windowIconContent, StringComparison.Ordinal);
        Assert.Contains("GetDpiForWindow", windowIconContent, StringComparison.Ordinal);

        var trayContent = File.ReadAllText(trayHelperPath);
        Assert.Contains("SetThreadDpiAwarenessContext", trayContent, StringComparison.Ordinal);
        Assert.Contains("CreatePopupMenu", trayContent, StringComparison.Ordinal);
        Assert.Contains("TrackPopupMenu", trayContent, StringComparison.Ordinal);
        Assert.Contains("LeftButtonUp", trayContent, StringComparison.Ordinal);
        Assert.Contains("RestoreAndFocusWindow", trayContent, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopHostWindowSizingAndCloseHook_AlignWithInfiniReqStandard()
    {
        var programPath = Path.Combine(RepoRoot, "Host", "ReferenceRAG.DesktopHost", "Program.cs");
        var hookPath = Path.Combine(RepoRoot, "Host", "ReferenceRAG.DesktopHost", "Platform", "WindowCloseHook.cs");
        var initialBoundsPath = Path.Combine(RepoRoot, "Host", "ReferenceRAG.DesktopHost", "InitialWindowBounds.cs");

        Assert.True(File.Exists(programPath));
        Assert.True(File.Exists(hookPath), "WindowCloseHook.cs must exist in DesktopHost.Platform");
        Assert.False(File.Exists(initialBoundsPath), "InitialWindowBounds.cs must be removed to avoid dynamic downscaling");

        var programContent = File.ReadAllText(programPath);
        Assert.Contains(".SetSize(1360, 860)", programContent, StringComparison.Ordinal);
        Assert.Contains(".SetMinSize(960, 640)", programContent, StringComparison.Ordinal);
        Assert.Contains("WindowCloseHook.Install", programContent, StringComparison.Ordinal);
        Assert.Contains("WindowCloseHook.RequestExplicitExit", programContent, StringComparison.Ordinal);
        Assert.Contains("TrayIconManager.RestoreAndFocusWindow", programContent, StringComparison.Ordinal);
        Assert.DoesNotContain("InitialWindowBounds", programContent, StringComparison.Ordinal);
        Assert.DoesNotContain("RegisterWindowClosingHandler", programContent, StringComparison.Ordinal);

        var hookContent = File.ReadAllText(hookPath);
        Assert.Contains("SetWindowSubclass", hookContent, StringComparison.Ordinal);
        Assert.Contains("RemoveWindowSubclass", hookContent, StringComparison.Ordinal);
        Assert.Contains("WM_CLOSE", hookContent, StringComparison.Ordinal);
        Assert.Contains("RequestExplicitExit", hookContent, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ReferenceRAG.slnx")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate ReferenceRAG.slnx repository root.");
    }
}
