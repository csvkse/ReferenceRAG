using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json.Serialization;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using InfiniFrame;
using InfiniFrame.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using ReferenceRAG.ApiHost.Transport;
using ReferenceRAG.DesktopHost.Platform;
using ReferenceRAG.Service.Extensions;
using ReferenceRAG.Service.Middleware;
using ReferenceRAG.Service.Controllers;
using ReferenceRAG.Service.Hubs;
using ReferenceRAG.Core.Interfaces;
using WebApiWindowsService;
using McpHelper.Extensions;

namespace ReferenceRAG.DesktopHost;

internal static partial class Program
{
    private const string AppUserModelId = "ReferenceRAG.AppWorkspace.v4";

    [STAThread]
    private static void Main(string[] args)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                _ = SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
            }
            catch
            {
                // Ignore shell AUMID errors on non-standard environments
            }

            try
            {
                // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4
                _ = SetProcessDpiAwarenessContext((nint)(-4));
            }
            catch
            {
                // Manifest already configures PerMonitorV2; fallback is best-effort
            }
        }

        try { Run(args); }
        catch(Exception ex) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"desktop-error.log"),ex.ToString()); MessageBox.Show(ex.Message,"ReferenceRAG 启动失败"); }
    }
    private static void Run(string[] args)
    {
        using var mutex=new Mutex(true,"Local\\ReferenceRAG_Desktop_SingleInstance",out var owner);
        if(!owner){MessageBox.Show("ReferenceRAG 已在运行，请从托盘打开。");return;}

        var appIconPath = ExtractAppIconPath();
        var windowIconPath = ExtractWindowIconPath() ?? appIconPath;
        Task? shortcutGuardTask = null;
        if (OperatingSystem.IsWindows())
        {
            shortcutGuardTask = Task.Run(() => ShortcutGuard.EnsureValid("ReferenceRAG", appIconPath));
        }

        Directory.SetCurrentDirectory(Environment.GetEnvironmentVariable("REFERENCERAG_CONTENT_ROOT") ?? AppContext.BaseDirectory);
        var builder=WebApplication.CreateBuilder(new WebApplicationOptions {Args=args,ContentRootPath=Directory.GetCurrentDirectory()});
        // 与 WebHost 共用日志配置：桌面端是 WinExe 无控制台，没有文件日志就无法排障。
        ServiceManager.ConfigureLogging(builder);
        // 是否监听 HTTP 由设置页的「启用 HTTP 服务」控制（ReferenceRAG:Service:enableHttpService）。
        // 默认关闭：前端经进程内 IPC 通信，不占端口。开启后额外监听，便于浏览器/curl/Swagger 调试。
        // 兼容旧的 Desktop:EnableLocalApi 配置项与命令行开关。
        // 命令行开关优先级最高，便于临时调试而不改配置。
        var configuredHttp = builder.Configuration.GetValue<bool?>("ReferenceRAG:Service:enableHttpService")
            ?? builder.Configuration.GetValue("Desktop:EnableLocalApi", false);
        var enableApi = args.Contains("--serve-http") || configuredHttp;
        var port=builder.Configuration["ReferenceRAG:Service:port"] ?? "7897";
        var allowNetwork = builder.Configuration.GetValue("ReferenceRAG:Service:allowNetworkAccess", false);
        // 即使开启了 HTTP，也默认只绑本机回环，除非显式允许外网访问。
        builder.WebHost.UseUrls($"http://{(allowNetwork ? "0.0.0.0" : "127.0.0.1")}:{port}");
        builder.Services.AddInProcessServer(enableApi);
        var platform = new DesktopPlatformActions();
        builder.Services.AddSingleton<ReferenceRAG.Business.Features.App.Contracts.IPlatformActions>(platform);
        builder.Services.AddControllers().AddApplicationPart(typeof(AIQueryController).Assembly).AddJsonOptions(o=>{
            o.JsonSerializerOptions.PropertyNamingPolicy=null;
            o.JsonSerializerOptions.NumberHandling=JsonNumberHandling.AllowNamedFloatingPointLiterals;
            o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });
        builder.Services.AddEndpointsApiExplorer();builder.Services.AddSwaggerGen();
        builder.Services.AddRagCoreServices(builder.Configuration);
        var app=builder.Build();
        app.UseAppMcpHelper();app.UseSwagger();
        // UseSwagger 只暴露 swagger.json；可视化界面需要 UseSwaggerUI 才存在。
        // 仅在开启 HTTP 服务时注册，避免默认（纯 IPC）模式下多挂载无用端点。
        if(enableApi) app.UseSwaggerUI(c=>c.SwaggerEndpoint("/swagger/v1/swagger.json","ReferenceRAG API v1"));
        app.UseApiKeyAuthentication();
        app.UseRagEndpoints();
        LogStartup("Initializing search");
        Task.Run(async()=>{await app.InitializeSearchAsync();await app.StartAsync();}).GetAwaiter().GetResult();
        LogStartup("Business host started");
        var windowState = app.Services.GetService<IWindowStateService>();

        var events=new InfiniFrameEventsStore();
        var resources=typeof(ReferenceRAG.Business.Composition.BusinessComposition).Assembly;
        events.CustomScheme.Add("app",(_,url)=>{
            var uri=new Uri(url);var path=Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
            if(uri.Host!="localhost" || path.Contains("..") || path.Contains('\\')) return (Stream.Null,"text/plain");
            if(string.IsNullOrEmpty(path)) path="index.html";
            var stream=resources.GetManifestResourceStream("ReferenceRAG.Business.wwwroot."+path.Replace('/','.'));
            var mime=Path.GetExtension(path) switch {".js"=>"text/javascript",".css"=>"text/css",".html"=>"text/html",".json"=>"application/json",".svg"=>"image/svg+xml",_=>"application/octet-stream"};
            return (stream??Stream.Null,mime);
        });
        IpcDispatcher? ipc=null;
        // Structured Chromium bridge is the only accepted message path.
        events.WebMessagePostData.Add("ipc-msg",(source,payload)=>{if(ipc!=null && payload!=null)_=ipc.ReceiveAsync(payload);});
        // InfiniFrame 0.62+ 把原先 Configuration 上的可写属性拆成了按功能分组的 feature 接口。
        var windowBuilder=InfiniFrameWindowBuilder.Create(events:events)
            .SetTitle("ReferenceRAG").SetSize(1360,860)
            .CenteredOnMainMonitor(true)
            .SetStartPageUrl("app://localhost/index.html");
        windowBuilder.Features.Size.SetMinWidth(900);
        windowBuilder.Features.Size.SetMinHeight(600);
        windowBuilder.SetTrustAllOrigins(false);windowBuilder.AddTrustedOrigin("app://localhost");
        windowBuilder.EnableWebSecurity(true);
        windowBuilder.EnableFileSystemAccess(false);
        windowBuilder.EnableBrowserPermissions(false);
        windowBuilder.SetTemporaryFilesPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ReferenceRAG","WebView"));
        windowBuilder.Debugging.EnableDevTools(builder.Environment.EnvironmentName=="Development");
        var exiting=false;IntPtr handle=IntPtr.Zero;
        windowBuilder.RegisterWindowClosingHandler((_,_)=>{
            if(exiting)return WindowClosingResult.Close;
            ShowWindow(handle,0);
            windowState?.SetMinimizedToTray(true);
            return WindowClosingResult.Cancel;
        });
        if (shortcutGuardTask != null)
        {
            _ = shortcutGuardTask.Wait(TimeSpan.FromMilliseconds(100));
        }
        LogStartup("Creating InfiniFrame window");
        var window=windowBuilder.Build();handle=window.WindowHandle;
        platform.Window=window;
        SetWindowText(handle,"ReferenceRAG");
        WindowsWindowIcon.Apply(handle, windowIconPath);
        InitialWindowBounds.Apply(handle);
        LogStartup("InfiniFrame window created");
        ipc=new IpcDispatcher(app.Services.GetRequiredService<InProcessServer>(),window.SendWebMessage,app.Services.GetRequiredService<ILogger<IpcDispatcher>>());
        var publisher=app.Services.GetRequiredService<IndexEventPublisher>();publisher.Published+=ipc.Publish;

        void ShowMainWindow()
        {
            ShowWindow(handle, 9);
            SetForegroundWindow(handle);
            windowState?.SetMinimizedToTray(false);
        }

        Action onTrayShow = ShowMainWindow;
        Action onTrayExit = () =>
        {
            exiting = true;
            window.Close();
        };

        TrayIconManager.ShowRequested += onTrayShow;
        TrayIconManager.ExitRequested += onTrayExit;
        TrayIconManager.Start(windowIconPath, "ReferenceRAG");

        var startMinimized = args.Contains("--minimized") || args.Contains("--silent") || ReferenceRAG.Desktop.StartupManager.GetStartMinimized();
        if(startMinimized) {
            ShowWindow(handle,0);
            windowState?.SetMinimizedToTray(true);
        } else {
            windowState?.SetMinimizedToTray(false);
        }
        using var stateCheckTimer = new System.Threading.Timer(_ =>
        {
            if (handle != IntPtr.Zero && windowState != null)
            {
                var isTrayOrMinimized = !IsWindowVisible(handle) || IsIconic(handle);
                if (windowState.IsMinimizedToTray != isTrayOrMinimized)
                {
                    windowState.SetMinimizedToTray(isTrayOrMinimized);
                }
            }
        }, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        try {window.WaitForClose();}
        finally {
            publisher.Published-=ipc.Publish;
            ipc.DisposeAsync().AsTask().GetAwaiter().GetResult();
            TrayIconManager.ShowRequested -= onTrayShow;
            TrayIconManager.ExitRequested -= onTrayExit;
            TrayIconManager.Stop();
            app.StopAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();app.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static string? ExtractAppIconPath() => ExtractEmbeddedIcon("app.ico", "app-v4.ico", "icon.ico");

    // 窗口标题栏、任务栏与系统托盘统一使用带主题色圆角边框的 R 图标
    private static string? ExtractWindowIconPath() => ExtractEmbeddedIcon("window.ico", "window-v4.ico", "window-icon.ico");

    private static string? ExtractEmbeddedIcon(string resourceName, string cacheFileName, string fallbackBaseFileName)
    {
        try
        {
            var iconDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ReferenceRAG",
                "Cache");
            Directory.CreateDirectory(iconDir);
            var iconPath = Path.Combine(iconDir, cacheFileName);

            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                var fallbackPath = Path.Combine(AppContext.BaseDirectory, fallbackBaseFileName);
                return File.Exists(fallbackPath) ? fallbackPath : null;
            }

            if (!File.Exists(iconPath) || new FileInfo(iconPath).Length != stream.Length)
            {
                using var fs = File.Create(iconPath);
                stream.CopyTo(fs);
            }

            return iconPath;
        }
        catch
        {
            return null;
        }
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SetCurrentProcessExplicitAppUserModelID(string appID);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessDpiAwarenessContext(nint value);

    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr handle,int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr handle);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern bool SetWindowText(IntPtr handle,string text);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    private static void LogStartup(string message)=>File.AppendAllText(Path.Combine(AppContext.BaseDirectory,"desktop-startup.log"),DateTimeOffset.Now+" "+message+Environment.NewLine);
}
