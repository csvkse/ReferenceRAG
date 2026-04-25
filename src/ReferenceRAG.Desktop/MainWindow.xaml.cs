using System.ComponentModel;
using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace ReferenceRAG.Desktop;

public partial class MainWindow : Window
{
    private readonly int _port;
    private bool _webView2Initialized = false;
    private bool _backendReady = false;

    public MainWindow(int port)
    {
        _port = port;
        InitializeComponent();
    }

    /// <summary>
    /// Loaded 事件：初始化 WebView2 环境。
    /// 初始化完成后若后端已就绪则立即导航，否则等待 <see cref="OnBackendReady"/> 触发。
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

            if (_backendReady)
                await ShowAppAsync();
        }
        catch (Exception ex)
        {
            ShowError($"WebView2 初始化失败：{ex.Message}\n\n请确认 WebView2 Runtime 已安装。");
        }
    }

    /// <summary>
    /// 后端就绪时由 App.xaml.cs 调用（UI 线程）。
    /// 若 WebView2 已初始化则立即导航，否则等待 Loaded 事件中检测标志后导航。
    /// </summary>
    public async Task OnBackendReady()
    {
        _backendReady = true;

        if (_webView2Initialized && webView.CoreWebView2 != null)
            await ShowAppAsync();
        // else: MainWindow_Loaded 完成后会检查 _backendReady 并调用 ShowAppAsync
    }

    /// <summary>
    /// 清除缓存、隐藏加载面板、显示 WebView2 并导航到本地服务。
    /// </summary>
    private async Task ShowAppAsync()
    {
        await webView.CoreWebView2.Profile.ClearBrowsingDataAsync(
            CoreWebView2BrowsingDataKinds.DiskCache);

        loadingPanel.Visibility = Visibility.Collapsed;
        webView.Visibility = Visibility.Visible;
        webView.CoreWebView2.Navigate($"http://localhost:{_port}/");
    }

    /// <summary>
    /// 更新加载状态文字（可从任意线程调用）。
    /// </summary>
    public void UpdateLoadingStatus(string message) =>
        Dispatcher.Invoke(() => loadingText.Text = message);

    /// <summary>
    /// 在加载面板显示错误信息并隐藏进度条（不弹窗，保持窗口可见）。
    /// </summary>
    public void ShowError(string message) =>
        Dispatcher.Invoke(() =>
        {
            loadingText.Text = $"启动失败：{message}";
            loadingProgress.Visibility = Visibility.Collapsed;
        });

    /// <summary>
    /// 最小化按钮：隐藏窗口并从任务栏移除。
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
    /// 关闭按钮（X）：最小化到托盘而非退出，退出须通过托盘菜单"退出"。
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
        ShowInTaskbar = false;
        base.OnClosing(e);
    }

    /// <summary>
    /// 从托盘恢复窗口（由 App.xaml.cs 的托盘菜单事件调用）。
    /// </summary>
    public void RestoreFromTray()
    {
        if (IsVisible && WindowState == WindowState.Normal)
            return; // 已显示且正常状态，无需操作

        if (!IsVisible)
            Show();
        WindowState = WindowState.Normal;
        ShowInTaskbar = true;
        Activate();
        Focus();
    }
}
