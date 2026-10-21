using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ReferenceRAG.DesktopHost.Platform;

/// <summary>
/// 原生 Win32 托盘图标管理器（完全复刻 infiniReq 工业级实现）。
/// 完全基于 Win32 消息循环，无 WinForms/WPF 依赖，具备 Native AOT 零裁剪兼容性。
/// 支持高分屏图标加载、鼠标左键/双击唤醒主窗体、右键菜单响应。
/// </summary>
internal static partial class TrayIconManager
{
    private const uint UserMessage = 0x0400;
    private const uint TrayMessage = UserMessage + 1;
    private const uint CommandMessage = 0x0111;
    private const uint CloseMessage = 0x0010;
    private const uint DestroyMessage = 0x0002;
    private const uint LeftButtonUp = 0x0202;
    private const uint LeftButtonDoubleClick = 0x0203;
    private const uint RightButtonUp = 0x0205;
    private const uint StringMenuFlag = 0;
    private const uint SeparatorMenuFlag = 0x0800;
    private const uint NotifyMessageFlag = 0x0001;
    private const uint NotifyIconFlag = 0x0002;
    private const uint NotifyTipFlag = 0x0004;
    private const uint NotifyAdd = 0;
    private const uint NotifyDelete = 2;
    private const int ApplicationIconId = 32512;
    private const int ShowMenuId = 1001;
    private const int ExitMenuId = 1002;

    private static nint _windowHandle;
    private static NotifyIconData _notifyIcon;

    public static event Action? ShowRequested;
    public static event Action? ExitRequested;

    public static void RunMessageLoop(string tooltip, string? iconPath = null)
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            _ = SetThreadDpiAwarenessContext((nint)(-4)); // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
        }
        catch { }

        var instance = GetModuleHandle(null);
        var className = $"ReferenceRAG.Tray.{Environment.ProcessId}";

        nint myIcon = nint.Zero;
        if (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath))
        {
            const uint IMAGE_ICON = 1;
            const uint LR_LOADFROMFILE = 0x00000010;
            int cx = 32;
            int cy = 32;
            try
            {
                uint dpi = GetDpiForSystem();
                if (dpi > 0)
                {
                    cx = GetSystemMetricsForDpi(49 /* SM_CXSMICON */, dpi);
                    cy = GetSystemMetricsForDpi(50 /* SM_CYSMICON */, dpi);
                }
                else
                {
                    cx = GetSystemMetrics(49);
                    cy = GetSystemMetrics(50);
                }
            }
            catch
            {
                cx = GetSystemMetrics(49);
                cy = GetSystemMetrics(50);
            }

            if (cx <= 0) cx = 32;
            if (cy <= 0) cy = 32;

            myIcon = LoadImage(nint.Zero, iconPath, IMAGE_ICON, cx, cy, LR_LOADFROMFILE);
            if (myIcon == nint.Zero)
            {
                myIcon = LoadImage(nint.Zero, iconPath, IMAGE_ICON, 0, 0, LR_LOADFROMFILE);
            }
        }

        unsafe
        {
            var windowClass = new WindowClass
            {
                Size = (uint)Marshal.SizeOf<WindowClass>(),
                Instance = instance,
                ClassName = className,
                Icon = myIcon,
                SmallIcon = myIcon,
                WindowProcedure = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint>)&WindowProcedure
            };
            RegisterClassEx(ref windowClass);
        }

        _windowHandle = CreateWindowEx(
            0,
            className,
            "ReferenceRAGTrayWindow",
            0,
            0,
            0,
            0,
            0,
            nint.Zero,
            nint.Zero,
            instance,
            nint.Zero);

        _notifyIcon = new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(),
            WindowHandle = _windowHandle,
            Id = 1,
            Flags = NotifyMessageFlag | NotifyIconFlag | NotifyTipFlag,
            CallbackMessage = TrayMessage,
            Icon = myIcon != nint.Zero ? myIcon : LoadIcon(nint.Zero, (nint)ApplicationIconId),
            Tip = tooltip,
            Info = string.Empty,
            InfoTitle = string.Empty
        };
        ShellNotifyIcon(NotifyAdd, ref _notifyIcon);

        while (GetMessage(out var message, nint.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }

        ShellNotifyIcon(NotifyDelete, ref _notifyIcon);
        _windowHandle = nint.Zero;
    }

    public static void Stop()
    {
        if (_windowHandle != nint.Zero)
        {
            PostMessage(_windowHandle, CloseMessage, nint.Zero, nint.Zero);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WindowProcedure(
        nint windowHandle,
        uint message,
        nint wordParameter,
        nint longParameter)
    {
        switch (message)
        {
            case TrayMessage:
                var mouseMessage = (uint)longParameter;
                if (mouseMessage is LeftButtonUp or LeftButtonDoubleClick)
                {
                    ShowRequested?.Invoke();
                }
                else if (mouseMessage == RightButtonUp)
                {
                    ShowMenu(windowHandle);
                }
                return nint.Zero;

            case CommandMessage:
                var menuId = unchecked((short)(long)wordParameter);
                if (menuId == ShowMenuId)
                {
                    ShowRequested?.Invoke();
                }
                else if (menuId == ExitMenuId)
                {
                    ExitRequested?.Invoke();
                    PostQuitMessage(0);
                }
                return nint.Zero;

            case CloseMessage:
            case DestroyMessage:
                PostQuitMessage(0);
                return nint.Zero;
        }

        return DefWindowProc(windowHandle, message, wordParameter, longParameter);
    }

    private static void ShowMenu(nint windowHandle)
    {
        var menu = CreatePopupMenu();
        AppendMenu(menu, StringMenuFlag, (nint)ShowMenuId, "显示主界面");
        AppendMenu(menu, SeparatorMenuFlag, nint.Zero, null);
        AppendMenu(menu, StringMenuFlag, (nint)ExitMenuId, "退出 ReferenceRAG");
        GetCursorPos(out var point);
        SetForegroundWindow(windowHandle);
        TrackPopupMenu(menu, 0x0002, point.X, point.Y, 0, windowHandle, nint.Zero);
        DestroyMenu(menu);
    }

    /// <summary>
    /// 唤醒并聚焦指定的目标窗口，如果最小化则自动还原。
    /// </summary>
    public static void RestoreAndFocusWindow(nint targetHwnd)
    {
        if (targetHwnd == nint.Zero || !OperatingSystem.IsWindows()) return;

        const int SW_SHOWNORMAL = 1;
        const int SW_RESTORE = 9;

        int cmd = IsIconic(targetHwnd) ? SW_RESTORE : SW_SHOWNORMAL;
        ShowWindowAsync(targetHwnd, cmd);
        SetForegroundWindow(targetHwnd);
        BringWindowToTop(targetHwnd);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size;
        public uint Style;
        public nint WindowProcedure;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public string? MenuName;
        public string ClassName;
        public nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint WindowHandle;
        public uint Id;
        public nint WordParameter;
        public nint LongParameter;
        public uint Time;
        public Point Location;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint WindowHandle;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;
        public uint InfoFlags;
        public Guid ItemGuid;
        public nint BalloonIcon;
    }

    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WindowClass windowClass);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern nint DefWindowProc(
        nint windowHandle,
        uint message,
        nint wordParameter,
        nint longParameter);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll", EntryPoint = "GetMessageW")]
    private static extern int GetMessage(
        out Message message,
        nint windowHandle,
        uint minimum,
        uint maximum);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static extern nint DispatchMessage(ref Message message);

    [DllImport("user32.dll")]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(
        nint menu,
        uint flags,
        nint itemId,
        string? label);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(
        nint menu,
        uint flags,
        int x,
        int y,
        int reserved,
        nint windowHandle,
        nint rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(nint menu);

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("user32.dll")]
    private static extern nint LoadIcon(nint instance, nint iconName);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadImage(
        nint instance,
        string name,
        uint type,
        int cx,
        int cy,
        uint load);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(
        nint windowHandle,
        uint message,
        nint wordParameter,
        nint longParameter);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForSystem();

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetricsForDpi(int index, uint dpi);

    [LibraryImport("user32.dll")]
    private static partial nint SetThreadDpiAwarenessContext(nint dpiContext);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindowAsync(nint hWnd, int nCmdShow);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BringWindowToTop(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint hWnd);
}
