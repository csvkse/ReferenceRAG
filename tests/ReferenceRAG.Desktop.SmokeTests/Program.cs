using System.Runtime.InteropServices;
using System.Text;
using InfiniFrame;
using ReferenceRAG.DesktopHost;
using ReferenceRAG.DesktopHost.Platform;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var silent = args.Contains("--silent");
        var reproduceEarlyHide = args.Contains("--reproduce-early-hide");
        var referenceFlow = args.Contains("--reference-flow");
        var earlyShow = args.Contains("--early-show");
        var noReady = args.Contains("--no-ready");
        var ready = false;
        var passed = false;
        var paintRequested = false;
        var painted = false;
        var stage = 0;
        nint handle = 0;
        var events = new InfiniFrameEventsStore();
        var coordinator = new StartupWindowCoordinator(silent, StartupWindowSurface.Hide, StartupWindowSurface.Reveal, StartupWindowSurface.Prepare);
        var html = """
            <!doctype html><html><body style="background:#156b48;color:white"><h1>Desktop rendering smoke test</h1>
            <script type="module">import {notifyDesktopReady} from '/shared/desktop-startup.js';
            document.body.dataset.ready='true';
            window.chrome.webview.addEventListener('message', event => {
              if (event.data === 'smoke-ping') requestAnimationFrame(() => requestAnimationFrame(() =>
                window.chrome.webview.postMessage(JSON.stringify({id:'smoke-painted',command:'Post',version:2,data:'painted'}))));
            });
            if (__SEND_READY__) await notifyDesktopReady(()=>Promise.resolve(),window.chrome.webview);</script>
            </body></html>
            """;
        html = html.Replace("__SEND_READY__", noReady ? "false" : "true");
        events.CustomScheme.Add("app", (_, url) => new Uri(url).AbsolutePath == "/shared/desktop-startup.js"
            ? (typeof(Program).Assembly.GetManifestResourceStream("desktop-startup.js")!, "text/javascript")
            : (new MemoryStream(Encoding.UTF8.GetBytes(html)), "text/html"));
        events.WebMessagePostData.Add("desktop-ready", (_, payload) =>
        {
            if (payload != "ready") return;
            ready = true;
            if (!reproduceEarlyHide && !referenceFlow) coordinator.SignalReady();
        });
        events.WebMessagePostData.Add("smoke-painted", (_, payload) => painted = payload == "painted");
        IInfiniFrameWindow window;
        using (var scope = silent ? new StartupWindowSurface.SilentCreationScope() : null)
        {
            window = InfiniFrameWindowBuilder.Create(events: events).SetTitle("ReferenceRAG lifecycle smoke")
                .SetSize(700, 450).SetStartPageUrl("app://localhost/index.html").Build();
            scope?.EnsurePrepared(window.WindowHandle);
            if (scope != null && !scope.PreparedBeforeShow) throw new InvalidOperationException("First show was not transparent");
        }
        handle = window.WindowHandle;
        if (handle == 0) throw new InvalidOperationException("No native HWND");
        var style = GetWindowLongPtr(handle, -20).ToInt64();
        if (silent)
        {
            if ((style & 0x80080) != 0x80080 ||
                !GetLayeredWindowAttributes(handle, out _, out var alpha, out _) || alpha != 0)
                throw new InvalidOperationException("Silent window must be transparent and absent from taskbar before initialization completes");
        }
        else if ((style & 0x80080) != 0)
            throw new InvalidOperationException("Normal startup must not gain temporary layered/toolwindow styles");
        WindowCloseHook.Install(handle);
        coordinator.SetWindowHandle(handle);
        if (earlyShow) coordinator.RequestShow();
        if (reproduceEarlyHide) ShowWindow(handle, 0);
        using var timer = new System.Windows.Forms.Timer { Interval = 500 };
        var deadline = DateTime.UtcNow.AddSeconds(25);
        var fallbackAt = DateTime.UtcNow.AddSeconds(2);
        timer.Tick += (_, _) =>
        {
            if (DateTime.UtcNow > deadline)
            {
                Console.Error.WriteLine($"Timed out: ready={ready}, stage={stage}");
                WindowCloseHook.RequestExplicitExit();
                return;
            }
            if (!ready && noReady && DateTime.UtcNow >= fallbackAt)
            {
                if (!coordinator.SignalTimeout()) throw new InvalidOperationException("Fallback was not applied");
                ready = true;
            }
            if (!ready) return;
            if (stage == 0)
            {
                if (silent && !earlyShow && !noReady && IsWindowVisible(handle)) return;
                if (reproduceEarlyHide || referenceFlow) ShowWindow(handle, 1);
                else coordinator.RequestShow();
                stage++;
                return;
            }
            var child = FindWindowEx(handle, 0, "Chrome_WidgetWin_0", null);
            if (!IsWindowVisible(handle) || child == 0 || !IsWindowVisible(child)) return;
            if ((GetWindowLongPtr(handle, -20).ToInt64() & 0x80080) != 0)
                throw new InvalidOperationException("Temporary transparent/toolwindow styles remained after reveal");
            if (!paintRequested)
            {
                paintRequested = true;
                window.SendWebMessage("smoke-ping");
                return;
            }
            if (!painted) return;
            Console.WriteLine($"Visible host and WebView: scenario={(silent ? "silent" : "normal")}, cycle={stage}");
            if (stage >= 5)
            {
                passed = true;
                timer.Stop();
                WindowCloseHook.RequestExplicitExit();
                return;
            }
            ShowWindow(handle, 0);
            painted = false;
            paintRequested = false;
            if (reproduceEarlyHide || referenceFlow) ShowWindow(handle, 1);
            else coordinator.RequestShow();
            stage++;
        };
        timer.Start();
        window.WaitForClose();
        return passed ? 0 : 1;
    }

    [DllImport("user32.dll")] private static extern bool ShowWindow(nint h, int command);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowEx(nint parent, nint after, string name, string? title);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint handle, int index);
    [DllImport("user32.dll")]
    private static extern bool GetLayeredWindowAttributes(nint handle, out uint color, out byte alpha, out uint flags);
}
