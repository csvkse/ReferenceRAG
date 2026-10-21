# 桌面端窗口启动尺寸异常与托盘最小化关闭拦截架构优化

> 2026-10-21 更正：本文关于 InfiniFrame 自动将固定尺寸按 DPI 换算的结论不成立，固定传入 1360×860 仍会在高 DPI 下压缩页面视口。实际调研及已实施的启动尺寸修复见 [启动 DPI 尺寸调研与修复](desktop-startup-dpi-sizing-investigation-and-fix.md)。本文其余历史推断不作为本次修复的验证依据。

## 1. 问题背景与现象分析

### 1.1 异常现象
在桌面端启动时，应用主窗体出现明显的尺寸异常缩小现象（初始窗口仅约 1000×680 像素，在高分屏 125%~150% DPI 缩放下甚至更小）。由于宽度严重不足，导致：
- 左侧边栏、对话会话列表与右侧主工作区被严重压缩挤扁；
- 顶部模型切换器与状态指示器发生换行错位；
- 页面底部出现不应有的横向滚动条，严重破坏了桌面原生应用的排版和交互体验。

用户要求参考同构成熟桌面项目 `E:\WorkProject\CSharp\PrivateProject\MultiPlatform\infiniReq`，彻底根治因处理最小化问题而导致的窗口启动大小异常问题。

---

## 2. 根因深度剖析（Root Cause Analysis）

通过对比代码历史发现，此前在解决窗体黑屏与最小化托盘恢复时，引入了 `InitialWindowBounds.cs`，试图在 `InfiniFrameWindowBuilder.Build()` 之前动态计算屏幕逻辑工作区并注入初始宽高：

```csharp
// 【有缺陷的历史逻辑】
var monitor = MonitorFromPoint(new POINT(), MonitorDefaultToPrimary);
...
uint dpi = GetDpiForSystem();
var scale = dpi / 96d;

var workWidth = info.Work.Right - info.Work.Left;
var workHeight = info.Work.Bottom - info.Work.Top;
var logicalWorkWidth = (int)Math.Round(workWidth / scale);
var logicalWorkHeight = (int)Math.Round(workHeight / scale);

var logicalWidth = Math.Clamp(
    (int)Math.Round(logicalWorkWidth * 0.78),
    Math.Min(1000, logicalWorkWidth),
    logicalWorkWidth);
var logicalHeight = Math.Clamp(
    (int)Math.Round(logicalWorkHeight * 0.82),
    Math.Min(680, logicalWorkHeight),
    logicalWorkHeight);

return (logicalWidth, logicalHeight);
```

### 致命问题一：Math.Clamp 边界计算反转与硬编码压缩
在主流笔记本或办公显示器（1920×1080 @ 125% 或 150% 缩放）：
- 150% 缩放下，`scale = 1.5`，屏幕逻辑宽度 `logicalWorkWidth = 1920 / 1.5 = 1280`；
- `logicalWorkWidth * 0.78` 仅为 `998.4`；
- 而下界为 `Math.Min(1000, 1280) = 1000`；
- 执行 `Math.Clamp(998.4, 1000, 1280)` 后，计算结果被强制截断锁死在 **1000**！
- 高度同样被截断锁死在 **680**；
- 最终传入 `SetSize(1000, 680)`，使主窗口启动时严重缩水，只有正常桌面界面的三分之一可用工作区。

### 致命问题二：InfiniFrame 自带 PerMonitorV2 缩放与二次除法的冲突
`InfiniFrame` 底层在 Windows 平台配合 `app.manifest` 的 `<dpiAwareness>PerMonitorV2, PerMonitor</dpiAwareness>` 时，已经具备原生 DPI 适配能力。外部强行预先除以 `scale`，导致高分屏下尺寸被二次缩减。

### 致命问题三：RegisterWindowClosingHandler 的事件时序冲突
此前依赖 `windowBuilder.RegisterWindowClosingHandler((_, _) => { ShowWindow(handle, 0); return WindowClosingResult.Cancel; })` 拦截窗口关闭并隐藏至托盘。由于该回调运行在 WebView2 / Chromium 内部事件派发管线中，调用 `Cancel` 易与 WebView2 生命周期、DWM 遮挡剔除产生状态死锁或黑屏回退。

---

## 3. infiniReq 生产级方案对比与对齐

在深度调研 `infiniReq`（`src/InfiniReq.Host.Desktop`）的工业级实践后，发现其具备极简且极其稳定的三大架构支柱：

| 维度 | ReferenceRAG 旧方案 | infiniReq 黄金方案 | 对齐后方案 |
| :--- | :--- | :--- | :--- |
| **初始窗口尺寸** | 动态计算 + 错误截断（1000×680） | 固定声明黄金比例（1360×860） | **1360 × 860** 标准桌面规格 |
| **最小尺寸限制** | 900×600 | 960×640 | **960 × 640** 标准桌面规格 |
| **关闭到托盘拦截** | `RegisterWindowClosingHandler` | Win32 子类化 `WindowCloseHook` | **原生 Win32 `SetWindowSubclass` 拦截** |
| **退出信号控制** | 标志位 `exiting` 传递 | `WindowCloseHook.RequestExplicitExit()` | **显式注销子类化 + 投递 WM_CLOSE** |
| **托盘唤醒聚焦** | 手工多重 API 调用 | `TrayIconManager.RestoreAndFocusWindow` | **对齐 `RestoreAndFocusWindow` 规范** |

---

## 4. 具体落地实现

### 4.1 引入 `WindowCloseHook.cs`（原生 Win32 子类化拦截）
文件位置：`Host/ReferenceRAG.DesktopHost/Platform/WindowCloseHook.cs`
- 使用 `comctl32.dll` 提供的 `SetWindowSubclass` 与 `RemoveWindowSubclass`；
- 拦截 `WM_CLOSE (0x0010)`，在未显式请求退出时直接转换为 `ShowWindow(hWnd, SW_HIDE)`，并触发 `WindowHidden` 事件；
- 托盘右键选择“退出”时，调用 `WindowCloseHook.RequestExplicitExit()`，安全解绑子类化并投递真正的 `WM_CLOSE` 退出消息循环；
- 彻底移除容易引发 WebView2 崩溃与时序冲突的 `RegisterWindowClosingHandler`。

### 4.2 彻底移除 `InitialWindowBounds.cs` 并规范窗口构建
文件位置：`Host/ReferenceRAG.DesktopHost/Program.cs`
- 废除 `InitialWindowBounds.GetInitialLogicalSize()`；
- 使用标准黄金比例流式配置：
  ```csharp
  var windowBuilder = InfiniFrameWindowBuilder.Create(events: events)
      .SetTitle("ReferenceRAG")
      .SetSize(1360, 860)
      .CenteredOnMainMonitor(true)
      .SetStartPageUrl("app://localhost/index.html");
  windowBuilder.Features.Size.SetMinWidth(960);
  windowBuilder.Features.Size.SetMinHeight(640);
  ```
- 窗口构建完成后立即安装钩子：
  ```csharp
  var window = windowBuilder.Build();
  var handle = window.WindowHandle;
  if (OperatingSystem.IsWindows() && handle != IntPtr.Zero)
  {
      WindowCloseHook.Install(handle);
      WindowCloseHook.WindowHidden += () => windowState?.SetMinimizedToTray(true);
  }
  ```

### 4.3 托盘唤醒与关闭联动
```csharp
void ShowMainWindow()
{
    if (handle != IntPtr.Zero)
    {
        TrayIconManager.RestoreAndFocusWindow(handle);
        // 托盘恢复唤醒：强制通知外壳与 WebView2 渲染宿主窗口拓扑结构刷新，确保子窗口重绘
        SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
        RedrawWindow(handle, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_UPDATENOW | RDW_ALLCHILDREN);
        windowState?.SetMinimizedToTray(false);
    }
}

Action onTrayExit = () =>
{
    try
    {
        WindowCloseHook.RequestExplicitExit();
        window.Close();
    }
    catch { }
};
```

---

## 5. 质量门禁与防回归保障

在 `tests/ReferenceRAG.Host.Tests/DesktopHostIdentityGateTests.cs` 中增加自动化门禁测试：
1. **尺寸规范门禁**：断言 `Program.cs` 配置了 `.SetSize(1360, 860)`，且最小宽高为 960×640；
2. **钩子依赖门禁**：断言必须调用 `WindowCloseHook.Install` 与 `RequestExplicitExit`；
3. **旧缺陷绝迹门禁**：断言项目中彻底删除了 `InitialWindowBounds.cs`，代码中严禁出现 `InitialWindowBounds` 与 `RegisterWindowClosingHandler`；
4. **集成门禁**：已挂载至 `scripts/verify-gate.ps1`，确保日常构建与 CI/CD 自动拦截任何尺寸缩小或钩子退化代码。
