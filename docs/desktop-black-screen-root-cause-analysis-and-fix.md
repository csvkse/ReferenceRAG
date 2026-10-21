# ReferenceRAG 桌面端黑屏根因排查与全链路防御加固报告

## 1. 现象与故障复现

- **用户反馈**：启动/唤醒 ReferenceRAG 桌面端客户端时，窗口客户区完全呈现纯黑色（RGB `#000000`），无侧边栏、无顶部导航、无任何文字或错误弹窗，仅外层 Win32 标题栏存在。
- **后台服务状态查验**：
  - 后端服务（IPC / InProcessServer 及 HTTP `:7897` 端口）完全正常运行。
  - Vue 前端启动后通过 IPC 发起的 `/api/index/summary`、`/api/Sources`、`/api/index/jobs` 接口请求全部返回 `200 OK`。
  - 说明 Vue 逻辑与应用代码并未在 JS 顶层抛出阻断异常，而是**底层渲染合成（Chromium DirectComposition Swapchain）在特定拓扑操作下未能向 Win32 父窗口客户区呈现（Present）帧内容**。

---

## 2. 根本原因剖析（Root Cause Analysis）

经过对进程树、Win32 窗口消息传递机制以及 Edge WebView2 / Chromium 内部机制的排查，锁定了以下四大协同诱发根因：

### 2.1 根因一：`desktop-settings.json` 的 `"StartMinimized": true` 与 `SW_HIDE` 引起的 Chromium Occlusion 锁死

1. **触发链路**：
   在设置页或托盘菜单中曾勾选过「启动时最小化到托盘」，本地配置持久化记录了 `{"StartMinimized": true}`。
2. **Chromium 机制冲突**：
   在 `Program.cs` 启动时，代码执行了：
   ```csharp
   var startMinimized = ... || ReferenceRAG.Desktop.StartupManager.GetStartMinimized();
   if (startMinimized) {
       ShowWindow(handle, 0); // SW_HIDE
       windowState?.SetMinimizedToTray(true);
   }
   ```
   Win32 `ShowWindow(handle, 0)` 将顶级窗口彻底隐藏（`SW_HIDE`）。此时 WebView2 的 `CoreWebView2Controller` 正在进行异步创建与 D3D 交换链初始化。
3. **Chromium 遮挡检测（Native Win Occlusion）机制**：
   Chromium 内部特性 `CalculateNativeWinOcclusion` 侦测到父级 HWND 为不可见状态后，为降低能耗将渲染器标记为 `OCCLUDED` 并挂起 DirectComposition 交换链的 Present。
   当用户在托盘菜单点击恢复窗口时，原本调用的 `ShowWindow(handle, 9)`（`SW_RESTORE`，专用于还原最小化窗体）未能触发全量外壳激活（Shell Activation），Chromium 内部依然将该窗体判断为被遮挡，**彻底拒绝重绘**，从而导致客户区为 Win32 默认黑底刷（`#000000`）。

### 2.2 根因二：Win32 `MoveWindow` 在 WebView2 异步初始化时强行重设 HWND 边界

1. **原实现**：
   `Program.cs` 在调用 `windowBuilder.SetSize(1360, 860).Build()` 创建窗口后，紧接着在后方调用了 `InitialWindowBounds.Apply(handle)`，其内部直接使用 Win32 API：
   ```csharp
   MoveWindow(windowHandle, left, top, width, height, true);
   ```
2. **生命周期竞争（Race Condition）**：
   `windowBuilder.Build()` 内部开启了 WebView2 的异步初始化管道。在 WebView2 的 `CoreWebView2Controller` 准备挂载 DirectComposition Visual 的过程中，Win32 顶级窗口被外部 `MoveWindow` 突然强行更改坐标与尺寸，而 WebView2 的控制器此时尚未就绪，错失了这一轮 `WM_WINDOWPOSCHANGED`。导致后续视口呈现缓冲区与宿主窗口尺寸错位或呈现失败。

### 2.3 根因三：托盘唤醒函数 `ShowMainWindow` 缺乏多层子控件重绘与拓扑刷新通知

1. **原实现**：
   ```csharp
   void ShowMainWindow()
   {
       ShowWindow(handle, 9);
       SetForegroundWindow(handle);
       windowState?.SetMinimizedToTray(false);
   }
   ```
2. **缺陷分析**：
   - 当窗体是被 `SW_HIDE` 隐藏而非最小化时，调用 `SW_RESTORE`（9）无法正确刷新非最小化隐藏窗体。
   - WebView2 依托于独立的跨进程子 HWND（`Chrome_WidgetWin_0`）。仅对父窗口调用 `ShowWindow` 并不能保证跨进程的 Chromium 子窗口及 D3D SwapChain 立即收到重绘信号。

---

## 3. 具体加固方案与实现细节

针对以上根本原因，实施了系统级的加固重构：

### 3.1 注入 Chromium 禁用原生遮挡遮蔽命令行参数

在 `Program.Run` 最前端，注入 Edge/Chromium 核心防黑屏环境变量：
```csharp
// 核心防黑屏门禁：禁用 Chromium 窗口原生遮挡剔除机制（避免从托盘恢复或启动隐藏时被误判为永久遮挡导致纯黑屏）
try
{
    var curArgs = Environment.GetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS") ?? string.Empty;
    if (!curArgs.Contains("CalculateNativeWinOcclusion"))
    {
        var combined = (curArgs + " --disable-features=CalculateNativeWinOcclusion").Trim();
        Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", combined);
    }
}
catch { }
```

### 3.2 重构窗体尺寸注入时机：消除异步竞态 `MoveWindow`

重构 [`InitialWindowBounds`](file:///e:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/Host/ReferenceRAG.DesktopHost/InitialWindowBounds.cs)：
- 新增 `GetInitialLogicalSize()` 方法：在 `windowBuilder.Build()` **之前**，基于主显示器的工作区 DPI 与物理分辨率，提前计算出最契合的逻辑宽高（保证在各类高分屏与笔记本屏幕上的 78% 优雅比例）。
- 直接将计算结果赋予 `windowBuilder.SetSize(initWidth, initHeight).CenteredOnMainMonitor(true)`。
- 废弃 `windowBuilder.Build()` 之后的 Win32 `MoveWindow` 强插逻辑，确保 WebView2 控制器在创建初始帧的第一时间就获得精准且同步的宿主尺寸。

### 3.3 增强托盘唤醒机制与 Win32 子窗口拓扑强刷

重构 `ShowMainWindow()` 唤醒逻辑：
```csharp
void ShowMainWindow()
{
    if (handle != IntPtr.Zero)
    {
        if (IsIconic(handle))
        {
            ShowWindow(handle, 9); // SW_RESTORE 还原真正最小化的窗体
        }
        else
        {
            ShowWindow(handle, 5); // SW_SHOW 显式呈现隐藏的窗体
        }
        SetForegroundWindow(handle);
        // 托盘恢复唤醒：强制通知外壳与 WebView2 渲染宿主窗口拓扑结构刷新，确保全子窗口立即重绘
        SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
        RedrawWindow(handle, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_UPDATENOW | RDW_ALLCHILDREN);
        windowState?.SetMinimizedToTray(false);
    }
}
```

### 3.4 启动默认显示保障与配置自愈

- 在非最小化启动路径下，显式调用 `ShowWindow(handle, 5)` 与 `SetForegroundWindow(handle)`，确保主窗体第一时间抢占前台焦点并完成绘制。
- 保证发布目录中的 `desktop-settings.json` 在非托盘启动时默认处于可见状态（`"StartMinimized": false`）。

---

## 4. 验证与门禁测试

1. **前端静态 AST 与单元测试**：
   - `check.mjs`：32 个 JS 模块与 16 个 Vue 模板静态 AST 符号绑定校验 100% 通过。
   - `frontend-logic.test.mjs`：24 项前端测试全部通过。
2. **后端契约与回归测试**：
   - 88 个测试用例全部通过（0 失败，0 告警阻断）。
3. **桌面端启动与渲染实测**：
   - 通过 `publish/ReferenceRAG.DesktopHost.exe` 启动后，Edge WebView2 子进程正常启动并加载 `--disable-features=CalculateNativeWinOcclusion` 参数。
   - 桌面客户端窗口成功创建，窗体句柄正常分配，前端界面立即渲染呈现，无黑屏故障。
