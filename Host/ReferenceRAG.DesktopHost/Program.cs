using System;
using System.IO;
using System.Linq;
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
using ReferenceRAG.Service.Extensions;
using ReferenceRAG.Service.Middleware;
using ReferenceRAG.Service.Controllers;
using ReferenceRAG.Service.Hubs;
using WebApiWindowsService;
using McpHelper.Extensions;

namespace ReferenceRAG.DesktopHost;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        try { Run(args); }
        catch(Exception ex) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"desktop-error.log"),ex.ToString()); MessageBox.Show(ex.Message,"ReferenceRAG 启动失败"); }
    }
    private static void Run(string[] args)
    {
        using var mutex=new Mutex(true,"Local\\ReferenceRAG_Desktop_SingleInstance",out var owner);
        if(!owner){MessageBox.Show("ReferenceRAG 已在运行，请从托盘打开。");return;}
        Directory.SetCurrentDirectory(Environment.GetEnvironmentVariable("REFERENCERAG_CONTENT_ROOT") ?? AppContext.BaseDirectory);
        var builder=WebApplication.CreateBuilder(new WebApplicationOptions {Args=args,ContentRootPath=Directory.GetCurrentDirectory()});
        // 与 WebHost 共用日志配置：桌面端是 WinExe 无控制台，没有文件日志就无法排障。
        ServiceManager.ConfigureLogging(builder);
        var enableApi=builder.Configuration.GetValue("Desktop:EnableLocalApi",false);
        var port=builder.Configuration["ReferenceRAG:Service:port"] ?? "7897";
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Services.AddInProcessServer(enableApi);
        var platform = new DesktopPlatformActions();
        builder.Services.AddSingleton<ReferenceRAG.Business.Features.App.Contracts.IPlatformActions>(platform);
        builder.Services.AddControllers().AddApplicationPart(typeof(AIQueryController).Assembly).AddJsonOptions(o=>{
            o.JsonSerializerOptions.PropertyNamingPolicy=null;o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });
        builder.Services.AddEndpointsApiExplorer();builder.Services.AddSwaggerGen();
        builder.Services.AddRagCoreServices(builder.Configuration);
        var app=builder.Build();
        app.UseAppMcpHelper();app.UseSwagger();app.UseApiKeyAuthentication();
        app.UseRagEndpoints();
        LogStartup("Initializing search");
        Task.Run(async()=>{await app.InitializeSearchAsync();await app.StartAsync();}).GetAwaiter().GetResult();
        LogStartup("Business host started");

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
        windowBuilder.RegisterWindowClosingHandler((_,_)=>{if(exiting)return WindowClosingResult.Close;ShowWindow(handle,0);return WindowClosingResult.Cancel;});
        LogStartup("Creating InfiniFrame window");
        var window=windowBuilder.Build();handle=window.WindowHandle;
        platform.Window=window;
        SetWindowText(handle,"ReferenceRAG");
        InitialWindowBounds.Apply(handle);
        LogStartup("InfiniFrame window created");
        ipc=new IpcDispatcher(app.Services.GetRequiredService<InProcessServer>(),window.SendWebMessage,app.Services.GetRequiredService<ILogger<IpcDispatcher>>());
        var publisher=app.Services.GetRequiredService<IndexEventPublisher>();publisher.Published+=ipc.Publish;
        NotifyIcon? tray=null;ApplicationContext? trayContext=null;
        var trayThread=new Thread(()=>{
            trayContext=new ApplicationContext();
            var menu=new ContextMenuStrip();
            _ = menu.Handle; // Ensure shutdown can marshal to this tray thread before the menu is opened.
            void Show(){ShowWindow(handle,9);SetForegroundWindow(handle);}
            menu.Items.Add("打开 ReferenceRAG",null,(_,_)=>Show());
            var autoStart=new ToolStripMenuItem("开机启动"){Checked=ReferenceRAG.Desktop.StartupManager.GetAutoStart(),CheckOnClick=true};
            autoStart.Click+=(_,_)=>ReferenceRAG.Desktop.StartupManager.SetAutoStart(autoStart.Checked);menu.Items.Add(autoStart);
            var minimized=new ToolStripMenuItem("最小化启动"){Checked=ReferenceRAG.Desktop.StartupManager.GetStartMinimized(),CheckOnClick=true};
            minimized.Click+=(_,_)=>ReferenceRAG.Desktop.StartupManager.SetStartMinimized(minimized.Checked);menu.Items.Add(minimized);
            menu.Items.Add("退出",null,(_,_)=>{exiting=true;window.Close();});
            tray=new NotifyIcon {Text="ReferenceRAG",Icon=new System.Drawing.Icon(Path.Combine(AppContext.BaseDirectory,"tray-icon.ico")),ContextMenuStrip=menu,Visible=true};
            tray.DoubleClick+=(_,_)=>Show();
            Application.Run(trayContext);tray.Dispose();menu.Dispose();
        }){IsBackground=true};
        trayThread.SetApartmentState(ApartmentState.STA);trayThread.Start();
        if(args.Contains("--minimized") || args.Contains("--silent") || ReferenceRAG.Desktop.StartupManager.GetStartMinimized()) ShowWindow(handle,0);
        try {window.WaitForClose();}
        finally {
            publisher.Published-=ipc.Publish;
            ipc.DisposeAsync().AsTask().GetAwaiter().GetResult();
            if(tray?.ContextMenuStrip is {IsHandleCreated:true} control) control.BeginInvoke(()=>trayContext?.ExitThread());
            app.StopAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();app.DisposeAsync().AsTask().GetAwaiter().GetResult();
            trayThread.Join(TimeSpan.FromSeconds(2));
        }
    }
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr handle,int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr handle);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern bool SetWindowText(IntPtr handle,string text);
    private static void LogStartup(string message)=>File.AppendAllText(Path.Combine(AppContext.BaseDirectory,"desktop-startup.log"),DateTimeOffset.Now+" "+message+Environment.NewLine);
}
