# ReferenceRAG 桌面端开机启动黑屏与尺寸异常深度调研与直接复刻 infiniReq 方案

## 1. 现象与故障复现

### 1.1 故障表现
- **开机启动黑屏**：电脑开机自启或开机后立即启动 ReferenceRAG，通过托盘唤醒应用时，主界面客户区呈现死寂的纯黑色（RGB `#000000`）。
- **启动窗口大小异常**：主窗口呈现非预期的畸形尺寸（实测仅约 680 × 430 像素），导致 Vue/Naive UI 栅格组件（如 `<n-grid :cols="4">`）被极度压缩，数字文本竖向折行破坏布局。
- **后台服务查验**：通过日志查验，后台 InProcessServer、AI 核心服务与前端静态资源托管完全正常运行，但窗口展现异常。

---

## 2. 根本原因深度剖析（Root Cause Analysis）

通过对比标准工程 `infiniReq`（`E:\WorkProject\CSharp\PrivateProject\MultiPlatform\infiniReq`），彻底理清了故障机理：

### 2.1 根因一：`--minimized` 启动突发 `SW_HIDE` 导致 WebView2 合成断裂（黑屏根因）
1. **原有的非标自启逻辑**：
   原有代码中 `StartupManager.cs` 在开机启动项注册表中写入了 `"--minimized"` 参数。
2. **生命周期竞争**：
   在 `Program.cs` 启动时，一旦带有 `--minimized`，代码随即调用 `ShowWindow(handle, 0)`（`SW_HIDE`）。此时 Edge WebView2 正在初始化 DirectComposition 交换链，突发的 `SW_HIDE` 导致 Chromium 将其标记为 Occluded（永久遮挡），挂起了 DirectComposition Present 流程。
3. **对比 infiniReq**：
   `infiniReq` 从不使用 `--minimized` 强隐窗口，也不在主窗口创建后随意调度 `ShowWindow(handle, 0)` / `ShowWindow(handle, 5)` 打断窗口原生状态。

### 2.2 根因二：人为介入尺寸定位（`InitialWindowBounds` / 尺寸计算错位）导致的 680×430 异常
1. **DPI 空间映射与 50% 物理尺寸混淆**：
   应用设定的标准规格为 `1360 × 860`。
   在 200% DPI（192 DPI，缩放倍率 2.0）环境下：
   $$\frac{1360 \text{ 物理像素}}{2.0} = 680 \text{ 逻辑像素 (DIPs)}$$
   $$\frac{860 \text{ 物理像素}}{2.0} = 430 \text{ 逻辑像素 (DIPs)}$$
2. **人为附加尺寸修正代码破坏了 InfiniFrame 原生布局**：
   此前引入的 `InitialWindowBounds.Apply(handle)` 或其他自定义位置/大小修正，试图在不可见或未完成 DPI 初始化的早期介入窗口坐标，导致物理像素被当成逻辑像素计算，锁定了 `680 × 430` 的异常缩小形态。
3. **对比 infiniReq**：
   `infiniReq` 保持极其纯粹的标准构建链：
   ```csharp
   var builder = InfiniFrameWindowBuilder.Create(events: events)
       .SetTitle("InfiniReq")
       .SetSize(1360, 860)
       .SetMinSize(960, 640)
       .SetStartPageUrl($"{AppOrigin}/index.html");
   ```
   不额外添加 `CenteredOnMainMonitor`、不调用各种 P/Invoke `MoveWindow` / `SetWindowPos` / `ShowWindow`，交由框架与 Windows DWM 自然完成高分屏 PerMonitorV2 适配。

---

## 3. infiniReq 工业级实现方案 1:1 直接复刻对比

| 模块 / 机制 | ReferenceRAG 早期代码 | infiniReq 黄金标准（本次完全复刻） |
| :--- | :--- | :--- |
| **窗口构建链** | 多重配置、自定义底色、`CenteredOnMainMonitor`、手动尺寸设置 | **纯粹 Fluent 构建链**：`.SetSize(1360, 860).SetMinSize(960, 640)` |
| **窗口初始呈现** | 判断 `--minimized` 执行 `ShowWindow(handle, 0)` 或 `ShowWindow(handle, 5)` | **交由 `windowBuilder.Build()` 原生呈现**，无任何外置 `ShowWindow` 干扰 |
| **关闭拦截** | 框架 Closing 回调 / 复杂状态判断 | **`WindowCloseHook`**：原生 Win32 子类化拦截 `WM_CLOSE` 并转为 `ShowWindow(hWnd, SW_HIDE)` |
| **唤醒恢复** | 自定义自愈宏、`EnumChildWindows`、`RedrawWindow` | **`TrayIconManager.RestoreAndFocusWindow`**：标准 `IsIconic ? SW_RESTORE : SW_SHOWNORMAL` + `ShowWindowAsync` + `SetForegroundWindow` + `BringWindowToTop` |
| **托盘消息循环** | 异步混杂多线程、带有自启勾选等非标项 | **独立的 STA `Thread` 承载 `TrayIconManager.RunMessageLoop`**，原生轻量 Win32 消息泵 |
| **图标与快捷方式** | 易产生缓存失效或白色占位方块 | **`WindowsWindowIcon.Apply`** + **`ShortcutGuard.EnsureValid`** 自动自愈任务栏与标题栏图标 |

---

## 4. 复刻落地的关键代码

### 4.1 窗口创建与启动（`Program.cs`）
```csharp
// 1. 设置进程 DPI 上下文与 AUMID
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

// 2. 纯粹标准的窗口构建器
var windowBuilder = InfiniFrameWindowBuilder.Create(events: events)
    .SetTitle("ReferenceRAG")
    .SetSize(1360, 860)
    .SetMinSize(960, 640)
    .SetStartPageUrl($"{AppOrigin}/index.html");

if (shortcutGuardTask != null)
{
    _ = shortcutGuardTask.Wait(TimeSpan.FromMilliseconds(100));
}

var window = windowBuilder.Build();
var windowHandle = window.WindowHandle;
platform.Window = window;

// 3. 原生 Win32 托盘与关闭钩子协同
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

window.WaitForClose();
```

### 4.2 原生托盘管理与唤醒（`TrayIconManager.cs`）
```csharp
public static void RestoreAndFocusWindow(nint targetHwnd)
{
    if (targetHwnd == nint.Zero || !OperatingSystem.IsWindows()) return;

    const int SW_SHOWNORMAL = 1;
    const int SW_RESTORE = 9;

    int cmd = IsIconic(targetHwnd) ? SW_RESTORE : SW_SHOWNORMAL;
    ShowWindowAsync(targetHwnd, cmd);
    SetForegroundWindow(targetHwnd);
    BringWindowToTop(targetHwnd);
}
```

### 4.3 窗口关闭拦截（`WindowCloseHook.cs`）
```csharp
[UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
private static nint SubclassProcedure(
    nint hWnd,
    uint uMsg,
    nint wParam,
    nint lParam,
    nuint uIdSubclass,
    nuint dwRefData)
{
    if (uMsg == WM_CLOSE && !_isExitRequested)
    {
        ShowWindow(hWnd, SW_HIDE);
        return nint.Zero; // 拦截关闭消息，将窗体最小化隐藏至系统托盘
    }

    return DefSubclassProc(hWnd, uMsg, wParam, lParam);
}
```

---

## 5. 验证与门禁测试

1. **架构与规范一致性门禁**：
   - `DesktopHostIdentityGateTests`: 验证 AUMID、PerMonitorV2、ICO 资源、快捷方式守卫、`1360x860` 尺寸与 `960x640` 最小尺寸、Win32 托盘线程与关闭钩子。
   - 结果：**测试通过**。
2. **全量 Host 自动化回归**：
   - `ReferenceRAG.Host.Tests`: **220 个测试 100% 全部通过**。
3. **打包发布**：
   - 产物构建输出至 `Host/ReferenceRAG.DesktopHost/bin/Release/net10.0-windows/publish/`，完全对齐 infiniReq 独立可执行程序标准。
