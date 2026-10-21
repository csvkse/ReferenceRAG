using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace ReferenceRAG.DesktopHost.Platform;

internal static class StartupWindowSurface
{
    private const long Layered = 0x80000;
    private const long ToolWindow = 0x80;

    public static void Prepare(nint handle)
    {
        if (handle == 0) return;
        var style = GetWindowLongPtr(handle, -20).ToInt64();
        SetWindowLongPtr(handle, -20, (nint)(style | Layered | ToolWindow));
        if (!SetLayeredWindowAttributes(handle, 0, 0, 2))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法准备静默启动窗口");
    }

    public static void Hide(nint handle)
    {
        if (handle != 0) ShowWindowAsync(handle, 0);
    }

    public static void Reveal(nint handle)
    {
        if (handle == 0) return;
        var style = GetWindowLongPtr(handle, -20).ToInt64();
        if ((style & Layered) != 0)
        {
            SetLayeredWindowAttributes(handle, 0, 255, 2);
            SetWindowLongPtr(handle, -20, (nint)(style & ~Layered & ~ToolWindow));
            SetWindowPos(handle, 0, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020);
        }
        TrayIconManager.RestoreAndFocusWindow(handle);
    }

    // Local to the Build thread and its first InfiniFrame window. Removed immediately after Build.
    internal sealed class SilentCreationScope : IDisposable
    {
        private readonly HookProcedure _callback;
        private nint _hook;
        private nint _preparedHandle;
        private Exception? _failure;
        public bool PreparedBeforeShow => _preparedHandle != 0 && _failure == null;

        public SilentCreationScope()
        {
            _callback = OnMessage;
            _hook = SetWindowsHookEx(4 /* WH_CALLWNDPROC */, _callback, 0, GetCurrentThreadId());
            if (_hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        public void EnsurePrepared(nint handle)
        {
            if (_failure != null) throw new InvalidOperationException("静默窗口透明准备失败", _failure);
            if (handle == 0 || handle != _preparedHandle)
                throw new InvalidOperationException("未捕获静默主窗口首次显示，拒绝退回有闪窗风险的启动流程");
        }

        private nint OnMessage(int code, nint wParam, nint lParam)
        {
            if (code >= 0 && _preparedHandle == 0 && _failure == null)
            {
                try
                {
                    var message = Marshal.PtrToStructure<CallWindowMessage>(lParam);
                    if (message.Message == 0x0018 /* WM_SHOWWINDOW */ && message.WParam != 0 && message.Handle != 0)
                    {
                        var name = new StringBuilder(64);
                        GetClassName(message.Handle, name, name.Capacity);
                        if (name.ToString() == "InfiniFrame")
                        {
                            Prepare(message.Handle);
                            _preparedHandle = message.Handle;
                        }
                    }
                }
                catch (Exception error) { _failure = error; }
            }
            return CallNextHookEx(_hook, code, wParam, lParam);
        }

        public void Dispose()
        {
            if (_hook != 0)
            {
                UnhookWindowsHookEx(_hook);
                _hook = 0;
            }
            GC.KeepAlive(_callback);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CallWindowMessage { public nint LParam, WParam; public uint Message; public nint Handle; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint HookProcedure(int code, nint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint h, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint h, int index, nint value);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetLayeredWindowAttributes(nint h, uint color, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint h, int command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint h, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)] private static extern nint SetWindowsHookEx(int id, HookProcedure callback, nint module, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint handle, StringBuilder name, int capacity);
}
