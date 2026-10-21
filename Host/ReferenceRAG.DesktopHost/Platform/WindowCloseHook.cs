using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ReferenceRAG.DesktopHost.Platform;

/// <summary>
/// 原生 Win32 窗口关闭钩子（完全复刻 infiniReq 工业级实现）。
/// 拦截 WM_CLOSE 消息并转为隐藏窗体到系统托盘，避免点击关闭或 Alt+F4 意外退出进程。
/// 仅当托盘右键显式请求退出时才放行关闭消息。
/// </summary>
internal static partial class WindowCloseHook
{
    private const uint WM_CLOSE = 0x0010;
    private const uint RefreshMessage = 0x8000 + 71;
    private const int SW_HIDE = 0;
    private static readonly nuint SubclassId = 1001;

    private static volatile bool _isExitRequested;
    private static nint _hookedWindowHandle;

    public static void Install(nint windowHandle)
    {
        if (windowHandle == nint.Zero || !OperatingSystem.IsWindows()) return;
        _hookedWindowHandle = windowHandle;
        unsafe
        {
            if (!SetWindowSubclass(
                windowHandle,
                (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nuint, nuint, nint>)&SubclassProcedure,
                SubclassId,
                nuint.Zero))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "无法安装主窗口生命周期钩子");
        }
    }

    public static void RequestRefresh(nint windowHandle)
    {
        if (windowHandle != 0 && windowHandle == _hookedWindowHandle)
            PostMessage(windowHandle, RefreshMessage, 0, 0);
    }

    public static void RequestExplicitExit()
    {
        _isExitRequested = true;
        if (_hookedWindowHandle != nint.Zero && OperatingSystem.IsWindows())
        {
            unsafe
            {
                RemoveWindowSubclass(
                    _hookedWindowHandle,
                    (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nuint, nuint, nint>)&SubclassProcedure,
                    SubclassId);
            }
            PostMessage(_hookedWindowHandle, WM_CLOSE, nint.Zero, nint.Zero);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint SubclassProcedure(
        nint hWnd,
        uint uMsg,
        nint wParam,
        nint lParam,
        nuint uIdSubclass,
        nuint dwRefData)
    {
        if (uMsg == RefreshMessage)
        {
            // Run on the HWND owner thread, after queued show/restore operations.
            if (GetClientRect(hWnd, out var bounds))
            {
                var width = bounds.Right - bounds.Left;
                var height = bounds.Bottom - bounds.Top;
                if (width > 0 && height > 0)
                {
                    SendMessage(hWnd, 0x0005 /* WM_SIZE */, IsZoomed(hWnd) ? 2 : 0,
                        (nint)((height << 16) | (width & 0xffff)));
                    RedrawWindow(hWnd, 0, 0, 0x0001 | 0x0080 | 0x0100);
                }
            }
            return 0;
        }

        if (uMsg == WM_CLOSE && !_isExitRequested)
        {
            ShowWindow(hWnd, SW_HIDE);
            return nint.Zero; // 拦截关闭消息，将窗体最小化隐藏至系统托盘
        }

        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    [LibraryImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowSubclass(
        nint hWnd,
        nint pfnSubclass,
        nuint uIdSubclass,
        nuint dwRefData);

    [LibraryImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RemoveWindowSubclass(
        nint hWnd,
        nint pfnSubclass,
        nuint uIdSubclass);

    [LibraryImport("comctl32.dll")]
    private static partial nint DefSubclassProc(
        nint hWnd,
        uint uMsg,
        nint wParam,
        nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint hWnd, int nCmdShow);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(nint handle, out Rect rect);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial nint SendMessage(nint handle, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsZoomed(nint handle);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RedrawWindow(nint handle, nint rect, nint region, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(
        nint windowHandle,
        uint message,
        nint wordParameter,
        nint longParameter);
}
