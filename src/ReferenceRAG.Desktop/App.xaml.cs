using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
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

    private MenuItem? _autoStartMenuItem;
    private MenuItem? _startMinimizedMenuItem;

    public static int ServicePort { get; private set; }

    private async void Application_Startup(object sender, StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, ex) =>
        {
            WriteCrashLog(ex.Exception);
            MessageBox.Show($"未处理异常：{ex.Exception.Message}\n\n详情见 crash.log",
                "ReferenceRAG 崩溃", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
            Shutdown(1);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            WriteCrashLog(ex.ExceptionObject as Exception);

        try
        {
            await StartupCore(e);
        }
        catch (Exception ex)
        {
            WriteCrashLog(ex);
            _mainWindow?.ShowError(ex.Message);
            if (_mainWindow == null)
            {
                MessageBox.Show($"启动失败：{ex.Message}\n\n详情见 crash.log",
                    "ReferenceRAG 启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
        }
    }

    private async Task StartupCore(StartupEventArgs e)
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

        BuildTrayContextMenu();

        ServicePort = ResolvePort();
        Console.WriteLine($"[Desktop] 分配端口: {ServicePort}");

        // 提前创建窗口：让用户立即看到加载界面，托盘"打开"也能响应
        bool startMinimized = e.Args.Contains("--minimized") || StartupManager.GetStartMinimized();
        _mainWindow = new MainWindow(ServicePort);
        if (!startMinimized)
            _mainWindow.Show();

        // 后端初始化（BM25 + Kestrel），期间窗口显示加载状态
        _mainWindow.UpdateLoadingStatus("正在初始化搜索索引...");
        _webApp = HostBootstrapper.Build(ServicePort);
        await HostBootstrapper.InitializeSearchAsync(_webApp);

        _mainWindow.UpdateLoadingStatus("正在启动服务...");
        _hostTask = Task.Run(() => _webApp.RunAsync(_cts.Token));

        try
        {
            await WaitForKestrelReady(ServicePort, TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
            _mainWindow.ShowError($"服务启动超时（端口 {ServicePort}）。\n请重试或检查防火墙设置。");
            _cts.Cancel();
            return;
        }

        // 后端就绪，通知窗口导航到应用
        await _mainWindow.OnBackendReady();
    }

    private static void WriteCrashLog(Exception? ex)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "crash.log");
            File.AppendAllText(path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]\n{ex}\n\n");
        }
        catch { }
    }

    private async void Application_Exit(object sender, ExitEventArgs e)
    {
        _cts.Cancel();
        if (_hostTask != null)
        {
            try { await _hostTask.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (OperationCanceledException) { }
            catch (TimeoutException) { }
        }

        // 不阻塞等待 DisposeAsync —— 进程即将退出，OS 会释放资源
        _ = _webApp?.DisposeAsync();

        var trayIcon = TryFindResource("TrayIcon") as TaskbarIcon;
        trayIcon?.Dispose();

        _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
    }

    // ── 托盘菜单 ──────────────────────────────────────────────────

    private void BuildTrayContextMenu()
    {
        _autoStartMenuItem = new MenuItem
        {
            Header = "开机自启动",
            IsCheckable = true,
            IsChecked = StartupManager.GetAutoStart()
        };
        _autoStartMenuItem.Click += AutoStartMenuItem_Click;

        _startMinimizedMenuItem = new MenuItem
        {
            Header = "最小化启动",
            IsCheckable = true,
            IsChecked = StartupManager.GetStartMinimized()
        };
        _startMinimizedMenuItem.Click += StartMinimizedMenuItem_Click;

        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "打开" });
        ((MenuItem)menu.Items[0]).Click += OpenMenuItem_Click;
        menu.Items.Add(new Separator());
        menu.Items.Add(_autoStartMenuItem);
        menu.Items.Add(_startMinimizedMenuItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "退出" });
        ((MenuItem)menu.Items[5]).Click += ExitMenuItem_Click;

        var trayIcon = TryFindResource("TrayIcon") as TaskbarIcon;
        if (trayIcon != null)
            trayIcon.ContextMenu = menu;
    }

    private void TrayIcon_LeftMouseDown(object sender, RoutedEventArgs e)
    {
        RestoreMainWindow();
    }

    private void OpenMenuItem_Click(object sender, RoutedEventArgs e)
    {
        RestoreMainWindow();
    }

    private void AutoStartMenuItem_Click(object sender, RoutedEventArgs e)
    {
        bool newValue = _autoStartMenuItem!.IsChecked;
        StartupManager.SetAutoStart(newValue);
    }

    private void StartMinimizedMenuItem_Click(object sender, RoutedEventArgs e)
    {
        bool newValue = _startMinimizedMenuItem!.IsChecked;
        StartupManager.SetStartMinimized(newValue);
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

    // ── 端口解析 ──────────────────────────────────────────────────

    private static int ResolvePort()
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .Build();

        int configured = config.GetValue("ReferenceRAG:Service:port", 7897);

        if (PortHelper.IsPortFree(configured))
        {
            Console.WriteLine($"[Desktop] 使用配置端口: {configured}");
            return configured;
        }

        int fallback = PortHelper.GetFreeTcpPort();
        Console.WriteLine($"[Desktop] 配置端口 {configured} 已占用，回退到随机端口: {fallback}");
        return fallback;
    }

    // ── 辅助方法 ──────────────────────────────────────────────────

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
