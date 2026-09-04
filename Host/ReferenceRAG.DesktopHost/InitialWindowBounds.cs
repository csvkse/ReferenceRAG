using System;
using System.Runtime.InteropServices;

namespace ReferenceRAG.DesktopHost;

internal static class InitialWindowBounds
{
    public static void Apply(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !OperatingSystem.IsWindows()) return;

        var monitor = MonitorFromPoint(new POINT(), MonitorDefaultToPrimary);
        if (monitor == IntPtr.Zero) return;

        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return;

        var dpi = GetDpiForWindow(windowHandle);
        if (dpi == 0) dpi = 96;
        var scale = dpi / 96d;
        var workWidth = info.Work.Right - info.Work.Left;
        var workHeight = info.Work.Bottom - info.Work.Top;
        var logicalWorkWidth = (int)Math.Round(workWidth / scale);
        var logicalWorkHeight = (int)Math.Round(workHeight / scale);

        var logicalWidth = Math.Clamp(
            (int)Math.Round(logicalWorkWidth * 0.78),
            Math.Min(1000, logicalWorkWidth),
            logicalWorkWidth);
        var logicalHeight = Math.Clamp(
            (int)Math.Round(logicalWorkHeight * 0.82),
            Math.Min(680, logicalWorkHeight),
            logicalWorkHeight);
        var width = (int)Math.Round(logicalWidth * scale);
        var height = (int)Math.Round(logicalHeight * scale);
        var left = info.Work.Left + (workWidth - width) / 2;
        var top = info.Work.Top + (workHeight - height) / 2;

        MoveWindow(windowHandle, left, top, width, height, true);
    }

    private const uint MonitorDefaultToPrimary = 1;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool MoveWindow(
        IntPtr windowHandle,
        int x,
        int y,
        int width,
        int height,
        bool repaint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public int Size;
        public RECT Monitor;
        public RECT Work;
        public int Flags;
    }
}
