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
    private const string AppOrigin = "app://localhost";

    [STAThread]
    private static void Main(string[] args)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                _ = SetCurrentProcessExplicitAppUserModelID("ReferenceRAG.AppWorkspace");
                _ = SetProcessDpiAwarenessContext((nint)(-4)); // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
            }
            catch { }
        }

        Task? shortcutGuardTask = null;
        if (OperatingSystem.IsWindows())
        {
            shortcutGuardTask = Task.Run(() => ShortcutGuard.EnsureValid("ReferenceRAG"));
        }

        try { Run(args, shortcutGuardTask); }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "desktop-error.log"), ex.ToString());
            MessageBox.Show(ex.Message, "ReferenceRAG 启动失败");
        }
    }

    private static void Run(string[] args, Task? shortcutGuardTask)
    {
        using var mutex = new Mutex(true, "Local\\ReferenceRAG_Desktop_SingleInstance", out var owner);
        if (!owner) { MessageBox.Show("ReferenceRAG 已在运行，请从托盘打开。"); return; }

        var appIconPath = ExtractAppIconPath();
        var windowIconPath = ExtractWindowIconPath() ?? appIconPath;

        Directory.SetCurrentDirectory(Environment.GetEnvironmentVariable("REFERENCERAG_CONTENT_ROOT") ?? AppContext.BaseDirectory);
        ReferenceRAG.Infrastructure.Features.Storage.Bootstrap.ConfigBootstrap.EnsureInitialized();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = Directory.GetCurrentDirectory() });
        ServiceManager.ConfigureLogging(builder);

        var configuredHttp = builder.Configuration.GetValue<bool?>("ReferenceRAG:Service:enableHttpService")
            ?? builder.Configuration.GetValue("Desktop:EnableLocalApi", false);
        var enableApi = args.Contains("--serve-http") || configuredHttp;
        var port = builder.Configuration["ReferenceRAG:Service:port"] ?? "7897";
        var allowNetwork = builder.Configuration.GetValue("ReferenceRAG:Service:allowNetworkAccess", false);
        builder.WebHost.UseUrls($"http://{(allowNetwork ? "0.0.0.0" : "127.0.0.1")}:{port}");
        builder.Services.AddInProcessServer(enableApi);
        var platform = new DesktopPlatformActions();
        builder.Services.AddSingleton<ReferenceRAG.Business.Features.App.Contracts.IPlatformActions>(platform);
        builder.Services.AddControllers().AddApplicationPart(typeof(AIQueryController).Assembly).AddJsonOptions(o => {
            o.JsonSerializerOptions.PropertyNamingPolicy = null;
            o.JsonSerializerOptions.NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals;
            o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });
        builder.Services.AddEndpointsApiExplorer(); builder.Services.AddSwaggerGen();
        builder.Services.AddRagCoreServices(builder.Configuration);
        var app = builder.Build();
        app.UseAppMcpHelper(); app.UseSwagger();
        if (enableApi) app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "ReferenceRAG API v1"));
        app.UseApiKeyAuthentication();
        app.UseRagEndpoints();
        LogStartup("Initializing search");
        Task.Run(async () => { await app.InitializeSearchAsync(); await app.StartAsync(); }).GetAwaiter().GetResult();
        LogStartup("Business host started");

        var events = new InfiniFrameEventsStore();
        var resources = typeof(ReferenceRAG.Business.Composition.BusinessComposition).Assembly;
        events.CustomScheme.Add("app", (_, url) => {
            var uri = new Uri(url); var path = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
            if (uri.Host != "localhost" || path.Contains("..") || path.Contains('\\')) return (Stream.Null, "text/plain");
            if (string.IsNullOrEmpty(path)) path = "index.html";
            var stream = resources.GetManifestResourceStream("ReferenceRAG.Business.wwwroot." + path.Replace('/', '.'));
            var mime = Path.GetExtension(path) switch { ".js" => "text/javascript", ".css" => "text/css", ".html" => "text/html", ".json" => "application/json", ".svg" => "image/svg+xml", _ => "application/octet-stream" };
            return (stream ?? Stream.Null, mime);
        });
        IpcDispatcher? ipc = null;
        events.WebMessagePostData.Add("ipc-msg", (source, payload) => { if (ipc != null && payload != null) _ = ipc.ReceiveAsync(payload); });

        // 构建器接受物理像素；创建后按实际窗口 DPI 应用逻辑尺寸。
        var windowBuilder = InfiniFrameWindowBuilder.Create(events: events)
            .SetTitle("ReferenceRAG")
            .SetSize(1360, 860)
            .SetMinSize(960, 640)
            .SetStartPageUrl($"{AppOrigin}/index.html");

        if (shortcutGuardTask != null)
        {
            _ = shortcutGuardTask.Wait(TimeSpan.FromMilliseconds(100));
        }

        LogStartup("Creating InfiniFrame window");
        var window = windowBuilder.Build();
        var windowHandle = window.WindowHandle;
        if (OperatingSystem.IsWindows() && windowHandle != 0)
        {
            var workArea = Screen.FromHandle(windowHandle).WorkingArea;
            var dpi = GetDpiForWindow(windowHandle);
            var size = StartupWindowSizing.Calculate(dpi, workArea.Width, workArea.Height);
            window.SetMinSize(size.MinWidth, size.MinHeight);
            window.SetSize(size.Width, size.Height);
            window.SetLocation(workArea.Left + (workArea.Width - size.Width) / 2,
                workArea.Top + (workArea.Height - size.Height) / 2);
            LogStartup($"Window sizing: DPI={dpi}, workArea={workArea}, target={size.Width}x{size.Height}, minimum={size.MinWidth}x{size.MinHeight}");
        }
        platform.Window = window;
        LogStartup("InfiniFrame window created");

        ipc = new IpcDispatcher(app.Services.GetRequiredService<InProcessServer>(), window.SendWebMessage, app.Services.GetRequiredService<ILogger<IpcDispatcher>>());
        var publisher = app.Services.GetRequiredService<IndexEventPublisher>();
        publisher.Published += ipc.Publish;

        if (OperatingSystem.IsWindows())
        {
            if (windowHandle != 0)
            {
                WindowsWindowIcon.Apply(windowHandle, windowIconPath);
                WindowCloseHook.Install(windowHandle);
            }

            TrayIconManager.ShowRequested += () =>
            {
                if (windowHandle != 0)
                {
                    TrayIconManager.RestoreAndFocusWindow(windowHandle);
                }
            };

            TrayIconManager.ExitRequested += () =>
            {
                try
                {
                    WindowCloseHook.RequestExplicitExit();
                    window.Close();
                }
                catch { }
            };

            var trayThread = new Thread(() => TrayIconManager.RunMessageLoop("ReferenceRAG · 知识检索助手", windowIconPath))
            {
                IsBackground = true,
                Name = "ReferenceRAG.Tray",
            };
            trayThread.SetApartmentState(ApartmentState.STA);
            trayThread.Start();
        }

        try
        {
            window.WaitForClose();
        }
        finally
        {
            publisher.Published -= ipc.Publish;
            ipc.DisposeAsync().AsTask().GetAwaiter().GetResult();
            TrayIconManager.Stop();
            app.StopAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
            app.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static string? ExtractAppIconPath() => ExtractEmbeddedIcon("app.ico", "app-v4.ico", "icon.ico");

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

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint windowHandle);

    private static void LogStartup(string message) =>
        File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "desktop-startup.log"), DateTimeOffset.Now + " " + message + Environment.NewLine);
}
