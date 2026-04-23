using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace ReferenceRAG.Desktop;

public partial class MainWindow : Window
{
    private readonly int _port;
    private bool _webView2Initialized = false;

    public MainWindow(int port)
    {
        _port = port;
        InitializeComponent();
    }

    /// <summary>
    /// Loaded 事件：窗口可见后初始化 WebView2。
    /// 必须在 Loaded（而非构造函数）中调用——WebView2 需要控件可见。
    /// </summary>
    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_webView2Initialized) return;
        _webView2Initialized = true;

        try
        {
            var env = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: Path.Combine(AppContext.BaseDirectory, "webview2-data"));

            await webView.EnsureCoreWebView2Async(env);
            webView.CoreWebView2.Navigate($"http://localhost:{_port}/");
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"WebView2 初始化失败：{ex.Message}\n\n请确认 WebView2 Runtime 已安装。",
                "WebView2 错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 最小化到托盘：隐藏窗口并从任务栏移除。
    /// </summary>
    protected override void OnStateChanged(EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            Hide();
            ShowInTaskbar = false;
        }
        base.OnStateChanged(e);
    }

    /// <summary>
    /// 从托盘恢复窗口（由 App.xaml.cs 的托盘菜单事件调用）。
    /// </summary>
    public void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        ShowInTaskbar = true;
        Activate();
        Focus();
    }
}