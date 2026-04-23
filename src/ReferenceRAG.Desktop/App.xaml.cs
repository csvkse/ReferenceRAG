using System.Net.Http;
using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace ReferenceRAG.Desktop;

public partial class App : Application
{
    private static Mutex? _instanceMutex;
    private const string MutexName = "Global\\ReferenceRAG_Desktop_SingleInstance";

    private WebApplication? _webApp;
    private readonly CancellationTokenSource _cts = new();
    private Task? _hostTask;
    private MainWindow? _mainWindow;

    public static int ServicePort { get; private set; }

    private async void Application_Startup(object sender, StartupEventArgs e)
    {
        if (!CheckWebView2Runtime()) return;

        if (!AcquireSingleInstanceMutex())
        {
            MessageBox.Show(
                "ReferenceRAG 已在运行中。",
                "ReferenceRAG",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        ServicePort = ResolvePort();
        Console.WriteLine($"[Desktop] 分配端口: {ServicePort}");

        _webApp = HostBootstrapper.Build(ServicePort);
        await HostBootstrapper.InitializeSearchAsync(_webApp);

        _hostTask = Task.Run(() => _webApp.RunAsync(_cts.Token));

        try
        {
            await WaitForKestrelReady(ServicePort, TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
            MessageBox.Show(
                $"服务启动超时（端口 {ServicePort}），请重试或检查防火墙设置。",
                "启动失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            _cts.Cancel();
            Shutdown();
            return;
        }

        _mainWindow = new MainWindow(ServicePort);
        _mainWindow.Show();
    }

    private async void Application_Exit(object sender, ExitEventArgs e)
    {
        _cts.Cancel();
        if (_hostTask != null)
        {
            try { await _hostTask.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
            catch (TimeoutException) { }
        }
        _webApp?.DisposeAsync().AsTask().Wait(2000);

        var trayIcon = TryFindResource("TrayIcon") as TaskbarIcon;
        trayIcon?.Dispose();

        _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
    }

    private void TrayIcon_LeftMouseDown(object sender, RoutedEventArgs e)
    {
        RestoreMainWindow();
    }

    private void OpenMenuItem_Click(object sender, RoutedEventArgs e)
    {
        RestoreMainWindow();
    }

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _cts.Cancel();
        var trayIcon = TryFindResource("TrayIcon") as TaskbarIcon;
        trayIcon?.Dispose();
        Shutdown();
    }

    private void RestoreMainWindow()
    {
        if (_mainWindow == null) return;
        _mainWindow.RestoreFromTray();
    }

    /// <summary>
    /// 读取配置中的 DesktopPort（默认 7898），端口被占用时回退随机端口。
    /// </summary>
    private static int ResolvePort()
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .Build();

        int configured = config.GetValue("ReferenceRAG:Service:DesktopPort", 7898);

        if (PortHelper.IsPortFree(configured))
        {
            Console.WriteLine($"[Desktop] 使用配置端口: {configured}");
            return configured;
        }

        int fallback = PortHelper.GetFreeTcpPort();
        Console.WriteLine($"[Desktop] 配置端口 {configured} 已占用，回退到随机端口: {fallback}");
        return fallback;
    }

    private static bool AcquireSingleInstanceMutex()
    {
        _instanceMutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            _instanceMutex.Dispose();
            _instanceMutex = null;
            return false;
        }
        return true;
    }

    private static bool CheckWebView2Runtime()
    {
        try
        {
            var version = Microsoft.Web.WebView2.Core.CoreWebView2Environment
                .GetAvailableBrowserVersionString();
            if (!string.IsNullOrEmpty(version))
            {
                Console.WriteLine($"[Desktop] WebView2 Runtime 版本: {version}");
                return true;
            }
        }
        catch { }

        MessageBox.Show(
            "未检测到 WebView2 Runtime。\n\n" +
            "请访问以下地址下载安装程序：\n" +
            "https://developer.microsoft.com/microsoft-edge/webview2\n\n" +
            "或运行随附的 MicrosoftEdgeWebview2Setup.exe。",
            "需要 WebView2 Runtime",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        return false;
    }

    private static async Task WaitForKestrelReady(int port, TimeSpan timeout)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var response = await client.GetAsync($"http://localhost:{port}/");
                if (response.IsSuccessStatusCode ||
                    response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    Console.WriteLine($"[Desktop] Kestrel 就绪，端口 {port}");
                    return;
                }
            }
            catch { }
            await Task.Delay(100);
        }
        throw new TimeoutException($"Kestrel 在 {timeout} 内未启动于端口 {port}");
    }
}