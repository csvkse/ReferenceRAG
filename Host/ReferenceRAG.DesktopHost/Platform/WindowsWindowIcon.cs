using System;
using System.IO;
using System.Runtime.InteropServices;

namespace ReferenceRAG.DesktopHost.Platform;

/// <summary>
/// 高分屏（High-DPI）窗体图标管理器。
/// 覆盖底层框架（如 InfiniFrame）可能使用的默认小图标，
/// 根据当前窗口实际屏幕 DPI 动态加载并设置原生物理尺寸的高清窗口图标与类图标。
/// </summary>
internal static partial class WindowsWindowIcon
{
    private const uint WM_SETICON = 0x0080;
    private const nint ICON_SMALL = 0;
    private const nint ICON_BIG = 1;
    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x00000010;

    private const int SM_CXICON = 11;
    private const int SM_CYICON = 12;
    private const int SM_CXSMICON = 49;
    private const int SM_CYSMICON = 50;

    private const int GCLP_HICON = -14;
    private const int GCLP_HICONSM = -34;

    public static void Apply(nint windowHandle, string? iconPath)
    {
        if (!OperatingSystem.IsWindows() || windowHandle == 0 || string.IsNullOrEmpty(iconPath) || !File.Exists(iconPath))
        {
            return;
        }

        try
        {
            uint dpi = 0;
            try
            {
                dpi = GetDpiForWindow(windowHandle);
            }
            catch { }

            if (dpi == 0)
            {
                try
                {
                    dpi = GetDpiForSystem();
                }
                catch { }
            }

            if (dpi == 0)
            {
                dpi = 96;
            }

            int smallW = 16;
            int smallH = 16;
            int bigW = 32;
            int bigH = 32;

            try
            {
                smallW = GetSystemMetricsForDpi(SM_CXSMICON, dpi);
                smallH = GetSystemMetricsForDpi(SM_CYSMICON, dpi);
                bigW = GetSystemMetricsForDpi(SM_CXICON, dpi);
                bigH = GetSystemMetricsForDpi(SM_CYICON, dpi);
            }
            catch
            {
                smallW = GetSystemMetrics(SM_CXSMICON);
                smallH = GetSystemMetrics(SM_CYSMICON);
                bigW = GetSystemMetrics(SM_CXICON);
                bigH = GetSystemMetrics(SM_CYICON);
            }

            if (smallW <= 0) smallW = 16;
            if (smallH <= 0) smallH = 16;
            if (bigW <= 0) bigW = 32;
            if (bigH <= 0) bigH = 32;

            var hSmall = LoadImage(nint.Zero, iconPath, IMAGE_ICON, smallW, smallH, LR_LOADFROMFILE);
            if (hSmall == nint.Zero)
            {
                hSmall = LoadImage(nint.Zero, iconPath, IMAGE_ICON, 0, 0, LR_LOADFROMFILE);
            }

            var hBig = LoadImage(nint.Zero, iconPath, IMAGE_ICON, bigW, bigH, LR_LOADFROMFILE);
            if (hBig == nint.Zero)
            {
                hBig = LoadImage(nint.Zero, iconPath, IMAGE_ICON, 0, 0, LR_LOADFROMFILE);
            }

            if (hSmall != nint.Zero)
            {
                _ = SendMessage(windowHandle, WM_SETICON, ICON_SMALL, hSmall);
                _ = SetClassLongPtr(windowHandle, GCLP_HICONSM, hSmall);
            }

            if (hBig != nint.Zero)
            {
                _ = SendMessage(windowHandle, WM_SETICON, ICON_BIG, hBig);
                _ = SetClassLongPtr(windowHandle, GCLP_HICON, hBig);
            }
        }
        catch
        {
            // 防御式捕获：图标设置异常不阻断主窗口启动
        }
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForSystem();

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetricsForDpi(int nIndex, uint dpi);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadImage(nint hInst, string name, uint type, int cx, int cy, uint fuLoad);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial nint SendMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "SetClassLongPtrW")]
    private static partial nint SetClassLongPtr(nint hWnd, int nIndex, nint dwNewLong);
}
