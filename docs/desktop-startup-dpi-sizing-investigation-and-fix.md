# 启动窗口尺寸与设置不一致：调研与修复

日期：2026-10-21

## 现象与结论

截图中窗口内容拥挤、统计卡片数字逐字换行。宿主声明了 1360×860，但这不是自动适配 DPI 的逻辑尺寸。高 DPI 下，WebView 页面按系统缩放显示，窗口外框仍按固定物理像素创建，可用 CSS 视口随之减小。

仅凭截图不能确定显示器 DPI 或浏览器缩放；本次读取当前进程时 MainWindowHandle 为 0，不能据此声称已实测当前窗口尺寸。以下根因依据是宿主调用链、框架实现和参考项目的差异。

## 调研证据

1. `Host/ReferenceRAG.DesktopHost/Program.cs` 使用 `.SetSize(1360, 860)`、`.SetMinSize(960, 640)`，创建后没有尺寸换算，也没有读取窗口尺寸配置的逻辑。
2. 本项目依赖 InfiniFrame 0.62.1。本地参考源码 `InfiniFrame-core/src/InfiniFrame.Shared/Window/Features/Size/IInfiniFrameWindowBuilderFeatureSizeExtensions.cs` 明确标注参数单位为 pixels；原生 `WindowCore.Win32.cpp` 将尺寸传给 `CreateWindowEx`，`WindowState.Win32.cpp` 的 `SetSize` 将参数直接传给 `SetWindowPos`。本地参考源码未证明与 NuGet 包逐字一致，因此以桌面构建和后续实机验收补充验证。
3. InfiniTool 的 `DesktopApplication.cs` 同样先声明 1360×860，但随后调用 `WindowsWindowManager.RestoreWindowBounds`。后者读取显示器工作区和 DPI，把逻辑尺寸乘以 `dpi / 96.0`，最终用 `MoveWindow` 应用物理尺寸。只复制构建器代码会遗漏这一步。
4. PerMonitorV2 声明决定 DPI 感知方式，不会替应用把任意整数尺寸自动解释成 DIP。此前 `desktop-window-startup-sizing-and-close-hook-architecture.md` 关于固定尺寸即可自动完成换算的结论不成立，以本文修正为准。与托盘关闭有关的历史推断不是本次尺寸修复的依据。

## 实施方案

保留启动尺寸为 1360×860 DIP、最小尺寸为 960×640 DIP 的意图。在 `Build()` 后读取真实 HWND 的 `GetDpiForWindow`，结合 `Screen.FromHandle(...).WorkingArea` 换算一次，应用到 InfiniFrame 的尺寸接口，并在该工作区居中。最小尺寸也按相同规则换算并限制在工作区内，避免小屏幕上窗口无法完整显示。

纯计算集中于 `StartupWindowSizing.Calculate`，可在不创建桌面窗口的情况下执行行为测试。DPI 为 0 时回退为 96。日志记录 DPI、工作区、目标尺寸和最小尺寸；不修改网页 zoom，不删除 WebView 用户数据，不引入窗口位置持久化，也不修改托盘生命周期。

| DPI / 缩放 | 大工作区内目标物理尺寸 | 最小物理尺寸 |
| --- | --- | --- |
| 96 / 100% | 1360×860 | 960×640 |
| 120 / 125% | 1700×1075 | 1200×800 |
| 144 / 150% | 2040×1290 | 1440×960 |
| 192 / 200% | 2720×1720 | 1920×1280 |

例如 1920×1040 工作区、150% 缩放时，目标尺寸限制为 1920×1040；无法保证小屏幕仍有完整的 1360 DIP 内容宽度。外框尺寸包括标题栏和边框，不等于网页客户区尺寸。

## 验证与边界

- 修复前以原始固定尺寸行为运行新增测试：8 个案例中 6 个按预期失败，覆盖高 DPI 和工作区限制。
- 修复后运行尺寸行为测试与现有桌面身份门禁：15 项通过，0 失败；测试构建报告一个既有的 `DisabledSourceGatekeeperTests.cs` 可空性警告。执行 `dotnet build Host/ReferenceRAG.DesktopHost/ReferenceRAG.DesktopHost.csproj --no-restore --verbosity quiet`：成功，0 警告、0 错误。
- 实机验收：在 100%、125%、150%、200% 下重新启动新构建，核对 `desktop-startup.log`，检查窗口在工作区内居中、页面布局及托盘恢复。当前正在运行的是 publish 目录下的旧进程，不能把源码修改视为该进程已更新。
- 本次仅处理启动尺寸；跨显示器拖动时的动态 DPI 处理仍由框架负责。用户手动更改浏览器缩放、小屏幕的响应式布局属于不同问题，若新版本仍异常，应分别检查实际 CSS 视口和 WebView 缩放。
