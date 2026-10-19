using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace ReferenceRAG.DesktopHost.Platform;

/// <summary>
/// 开始菜单快捷方式自愈守卫：
/// Windows 11 任务栏按钮图标会解析开始菜单快捷方式的 IconLocation。
/// 当该引用失效或指向旧图标时，本守卫在后台异步检测并自愈快捷方式并刷新外壳图标缓存。
/// </summary>
internal static unsafe partial class ShortcutGuard
{
    private static readonly Guid ShellLinkClsid = new("00021401-0000-0000-C000-000000000046");
    private static readonly Guid ShellLinkIid = new("000214F9-0000-0000-C000-000000000046");

    public static long EnsureValid(string appName, string? customIconPath = null)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var shortcutPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Windows", "Start Menu", "Programs", appName + ".lnk");
            if (!OperatingSystem.IsWindows()) return stopwatch.ElapsedMilliseconds;

            var hr = ShortcutGuardNativeMethods.CoCreateInstance(
                ShellLinkClsid, IntPtr.Zero, 1 /* CLSCTX_INPROC_SERVER */, ShellLinkIid, out var shellLinkPtr);
            if (hr != 0 || shellLinkPtr == IntPtr.Zero) return stopwatch.ElapsedMilliseconds;

            var link = ComInterfaceMarshaller<IShellLinkW>.ConvertToManaged((void*)shellLinkPtr);
            var persistFile = ComInterfaceMarshaller<IPersistFile>.ConvertToManaged((void*)shellLinkPtr);
            if (link is null || persistFile is null) return stopwatch.ElapsedMilliseconds;

            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) return stopwatch.ElapsedMilliseconds;
            var exeDir = Path.GetDirectoryName(exe) ?? string.Empty;

            var desiredIconPath = !string.IsNullOrWhiteSpace(customIconPath) && File.Exists(customIconPath)
                ? customIconPath
                : exe;

            var isExisting = File.Exists(shortcutPath);
            if (isExisting)
            {
                persistFile.Load(shortcutPath, 0 /* STGM_READ */);
            }

            var target = isExisting ? ReadString(buffer => link.GetPath(buffer, 1024, IntPtr.Zero, 0)) : string.Empty;
            var iconReference = isExisting ? ReadString(buffer => link.GetIconLocation(buffer, 1024, out _)) : string.Empty;

            if (!string.Equals(target, exe, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(iconReference, desiredIconPath, StringComparison.OrdinalIgnoreCase))
            {
                link.SetPath(exe);
                link.SetWorkingDirectory(exeDir);
                link.SetIconLocation(desiredIconPath, 0);
                persistFile.Save(shortcutPath, 1 /* fRemember: TRUE */);

                var shortcutPtr = Marshal.StringToCoTaskMemUni(shortcutPath);
                try
                {
                    // SHCNE_UPDATEITEM = 0x00002000, SHCNF_PATHW = 0x0005
                    ShortcutGuardNativeMethods.SHChangeNotify(0x00002000, 0x0005, shortcutPtr, IntPtr.Zero);
                }
                finally
                {
                    Marshal.FreeCoTaskMem(shortcutPtr);
                }

                // SHCNE_ASSOCCHANGED = 0x08000000, SHCNF_IDLIST = 0x0000
                ShortcutGuardNativeMethods.SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
            }
        }
        catch
        {
            // 自愈是尽力而为：忽略异常，不影响启动
        }
        return stopwatch.ElapsedMilliseconds;
    }

    private static string ReadString(Action<IntPtr> fill)
    {
        const int bufferBytes = 2048;
        var buffer = Marshal.AllocCoTaskMem(bufferBytes);
        try
        {
            Marshal.Copy(new byte[bufferBytes], 0, buffer, bufferBytes);
            fill(buffer);
            return Marshal.PtrToStringUni(buffer)?.TrimEnd('\0') ?? string.Empty;
        }
        finally
        {
            Marshal.FreeCoTaskMem(buffer);
        }
    }
}

internal static partial class ShortcutGuardNativeMethods
{
    [LibraryImport("ole32.dll")]
    internal static partial int CoCreateInstance(in Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, in Guid riid, out IntPtr ppv);

    [LibraryImport("shell32.dll")]
    internal static partial void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("000214F9-0000-0000-C000-000000000046")]
internal partial interface IShellLinkW
{
    void GetPath(IntPtr pszFile, int cch, IntPtr pfd, uint fFlags);
    void GetIDList(out IntPtr ppidl);
    void SetIDList(IntPtr pidl);
    void GetDescription(IntPtr pszName, int cch);
    void SetDescription(string pszName);
    void GetWorkingDirectory(IntPtr pszDir, int cch);
    void SetWorkingDirectory(string pszDir);
    void GetArguments(IntPtr pszArgs, int cch);
    void SetArguments(string pszArgs);
    void GetHotkey(out short pwHotkey);
    void SetHotkey(short wHotkey);
    void GetShowCmd(out int piShowCmd);
    void SetShowCmd(int iShowCmd);
    void GetIconLocation(IntPtr pszIconPath, int cch, out int piIcon);
    void SetIconLocation(string pszIconPath, int iIcon);
    void SetRelativePath(string pszPathRel, uint dwReserved);
    void Resolve(IntPtr hwnd, uint fFlags);
    void SetPath(string pszFile);
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("0000010B-0000-0000-C000-000000000046")]
internal partial interface IPersistFile
{
    void GetClassID(out Guid pClassID);
    void IsDirty();
    void Load(string pszFileName, uint dwMode);
    void Save(string pszFileName, int fRemember);
    void SaveCompleted(string pszFileName);
    void GetCurFile(IntPtr ppszFileName);
}
