# 最小化启动闪窗：AITool 方案适配与真实窗口验证

日期：2026-10-21；目标框架：InfiniFrame 0.62.1，Windows x64。

## 问题与方案

前一次黑屏修复将 SW_HIDE 推迟到前端挂载后，因此 WebView 可以完成初始化，但静默启动期间窗口会短暂可见。AITool 的思路是“逻辑可见、视觉透明”：静默窗口初始化时设置 `WS_EX_LAYERED | WS_EX_TOOLWINDOW`，Alpha 为 0；前端就绪后才隐藏。托盘打开时恢复不透明并清除临时样式。

参考位置：`E:\WorkProject\CSharp\PrivateProject\Agent\AITool` 下的 `docs/desktop-startup-troubleshooting.md`、`StartupWindowCoordinator.cs`、`WindowVisibilityController.cs` 和 `web/AITool.Web/app/main.js`。该项目使用 InfiniFrame 0.13.1，故本次必须在当前 0.62.1 上执行原生功能验证。

## 本项目的具体适配

### 首次显示前准备，而非 Build 后补救

AITool 在 Build 返回后将窗口透明，仍留下创建到透明之间的时间窗口。本项目新增 `StartupWindowSurface.SilentCreationScope`：仅静默启动时，在 Build 所属线程临时安装 `WH_CALLWNDPROC` 钩子。捕获该线程第一个 InfiniFrame 窗口的首次 `WM_SHOWWINDOW` 显示消息，在其窗口过程处理显示消息之前准备透明度与工具窗口样式。Build 完成立即注销钩子，并检查是否确实捕获了对应 HWND；不静默退回存在闪窗风险的后置准备。

钩子是线程局部的，不安装系统级钩子，不枚举桌面，不操作 Chromium 内部子窗口；异常在回调内记录，返回托管调用边界后再报告。普通启动不安装此钩子。

### 就绪、隐藏与恢复

- `StartupWindowCoordinator` 仅为静默启动调用 Prepare；原有普通启动的显示与同步逻辑保留，不新增透明度或任务栏样式变更。
- 前端生产模块 `shared/desktop-startup.js` 在 nextTick 后等待连续两次 requestAnimationFrame，再发送独立 `desktop-ready` 消息。这代表浏览器获得了布局/帧回调机会，不宣称等价于 GPU Present 完成。
- 就绪后若没有主动打开请求，则隐藏已透明窗口；用户主动打开时，恢复 Alpha=255、移除临时 Layered/ToolWindow 样式、刷新非客户区，再执行原有显示/恢复及 WebView 客户区同步。
- 用户提前打开的意图继续优先于迟到的就绪隐藏。
- 静默启动 10 秒仍无 ready 时，恢复为可见窗口并记录诊断日志；晚到 ready 不再隐藏。与 AITool 的“3 秒超时继续隐藏”不同，这里优先避免错误页面长期透明，或在尚未就绪时重新触发提前隐藏风险。测试使用更短的等待验证同一协调器超时分支。

不修改用户已保存的启动选项，不更改普通启动的尺寸、位置、主题或托盘菜单，不新增浏览器 GPU 参数。

## 回归验证

### 先失败，再修复

在真实 smoke 中先新增静默启动断言：初始化完成前必须已具备 Layered/ToolWindow 样式和 Alpha=0。旧流程运行失败，退出码 1，错误为 `Silent window must be transparent and absent from taskbar before initialization completes`。

### 当前版本真实窗口测试

`tests/ReferenceRAG.Desktop.SmokeTests` 链接生产启动协调器、透明准备、恢复和原生关闭/刷新实现，嵌入生产 `desktop-startup.js`，运行真实 InfiniFrame 0.62.1 与本机 WebView2。每次恢复都向 WebView 发 ping，页面经过两次动画帧回调后发回响应；不仅检查页面曾加载，也检查恢复后帧回调仍能执行。

检查项：

1. 静默窗口首次显示消息被预先拦截处理；初始化阶段 Alpha=0 且带工具窗口样式。
2. 普通启动没有获得临时 Layered/ToolWindow 样式。
3. 静默就绪后隐藏；恢复后清除临时样式，主窗口和 WebView 宿主子窗口均可见。
4. 每次恢复后真实页面可完成双帧回调并回复消息。
5. 正常启动、静默启动、提前打开、缺失 ready 超时四个场景，各完成 5 轮隐藏/恢复。

命令：

```text
dotnet run --project tests/ReferenceRAG.Desktop.SmokeTests
dotnet run --project tests/ReferenceRAG.Desktop.SmokeTests -- --silent
dotnet run --project tests/ReferenceRAG.Desktop.SmokeTests -- --silent --early-show
dotnet run --project tests/ReferenceRAG.Desktop.SmokeTests -- --silent --no-ready
```

上述四种场景已实际执行并通过。`scripts/verify-gate.ps1 -DesktopSmoke` 已接入四场景；本次直接执行对应命令，未运行整个统一脚本，也未修改 CI。

相关生命周期、DPI 与身份测试 23 项通过；前端行为测试 18 项通过；AST 验证 33 个 JS 模块和 16 个可执行模板通过；桌面宿主构建成功，0 警告、0 错误。后端测试构建中的既有可空性警告未在本次修改。

## 证据边界与交付状态

本次通过首次显示消息时序、真实 Alpha/窗口样式、WebView 可见性与恢复后帧回调进行验证，未录制高速视频或执行像素级 GPU 截图断言；不对全部 Windows/显卡/多屏组合保证绝对零闪帧。准备操作由当前 InfiniFrame 原生显示消息触发，未来升级框架应重新运行这些 smoke；如果类名或创建线程机制改变，显式失败而不是静默漏过准备。

当前旧发布进程未热替换，未修改开机启动注册表；需从新构建启动验证实际业务页面。本次修改未自动提交或推送。
