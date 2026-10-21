# 桌面黑屏复现：启动可见性时序、参考对照与回归验证

日期：2026-10-21

> 后续更新：静默启动的短暂可见问题已按 AITool 思路增加首次显示前透明准备，前端就绪升级为双帧握手，并补充超时恢复和四场景原生测试。当前实现以 [静默启动防闪窗适配](desktop-silent-start-no-flash-aitool-adaptation.md) 为准；下文保留首次黑屏调查时的阶段性方案和证据。

## 1. 本次证据与结论

用户反馈普通启动也会黑屏，而非仅托盘恢复。现场进程为发布目录下的 ReferenceRAG.DesktopHost（PID 7544），WebView 命令行记录产品提交为 `00a96cd`。该发布目录的 `desktop-settings.json` 保存了 `StartMinimized: true`，因此即使不带命令行参数，普通双击仍会走最小化启动分支。

本次 19:28:48 启动日志显示窗口创建和 DPI 尺寸计算完成；19:28:49 日志包含 Dashboard 的 `/api/index/summary`、`/api/Sources`、`/api/index/jobs` IPC 请求及 200 响应。原生取证时主窗口可见、非最小化，而 `Chrome_WidgetWin_0`、`Chrome_WidgetWin_1`、`Intermediate D3D Window` 不可见，边界非零。说明前端代码已执行到请求业务数据阶段，不能把黑屏简单归因于页面模块未加载。

**已复现的触发条件：在 WebView 初始化完成之前执行 SW_HIDE，随后仅恢复顶级窗口。** 隔离功能测试中，页面能发送就绪消息，但 WebView 宿主子窗口一直不可见，25 秒超时。移除提前隐藏，或采用本次就绪后协调方案，测试通过。

这定位到宿主可见性与异步初始化时序失配。没有采集 WebView2 内部 COM `IsVisible`、GPU trace 或交换链 Present，因此不声称已证明 Chromium 遮挡算法“永久锁死”，也不将某个内部 D3D 实现细节作为既定根因。

## 2. infiniReq 文档与代码对照

参考目录：`E:\WorkProject\CSharp\PrivateProject\MultiPlatform\infiniReq`。本次仅只读对照。

- `docs/quality/quality-strategy.md`：Host lifecycle 需要 Browser/Device/Runtime 证据，静态断言不能代替运行验证；门禁只有正反样例和实际统一执行/CI 证据后才可标记 enforced。
- `docs/operations/build-release-and-upgrade.md`：桌面发布需要真实启动 smoke，并验证本地资源、桥接和退出。
- `docs/development-process.md`：Host/WebView 生命周期应使用 Runtime/Host Test。
- 当前 docs 未发现专门的黑屏 RCA，也没有“禁用 CalculateNativeWinOcclusion 即可修复黑屏”的已验证方案。
- `src/InfiniReq.Host.Desktop/Program.cs`：构建后设置图标、关闭钩子和托盘，然后进入消息循环，没有启动时 SW_HIDE 分支。
- `Platform/TrayIconManager.cs`：仍按 IsIconic 选择 SW_RESTORE 或 SW_SHOWNORMAL，没有额外 GPU 参数。不能笼统断言 SW_SHOWNORMAL/SW_RESTORE 自身一定引发黑屏。
- `InfiniReq.Host.Desktop.csproj` 使用 InfiniFrame 0.60.0，本项目为 0.62.1。不能将参考项目在不同版本、不同启动行为下的经验当作本项目的直接证明。

### 排除矩阵

| 假设 | 本次证据 | 判定 |
| --- | --- | --- |
| 所有前端模块都没有启动 | Dashboard 已通过 IPC 请求数据 | 不是本次现场的首要原因；不排除未来独立的 JS 异常 |
| 模型页面样式导致整个原生窗口黑屏 | 无业务代码的最小 WebView 页面也能复现 | 排除为该复现的必要条件 |
| DPI/窗口尺寸必须异常才黑屏 | 最小 700×450 窗口、不执行业务 DPI 换算，提前隐藏仍失败 | 排除为必要条件；不据此证明所有多屏场景均无问题 |
| HTTP 端口、服务端数据错误 | 业务 IPC 200；独立测试不启用业务服务或 HTTP API | 排除为该复现的必要条件 |
| 缓存损坏 | 隔离测试使用测试进程 WebView 环境也可复现 | 不需要清理用户缓存作为修复前提 |
| 必须禁用 GPU/Chromium 遮挡检测 | 同一引擎下参考流程和新流程通过，提前隐藏流程失败 | 无证据需要全局改 GPU 参数；未证明内部 GPU 路径完全无关 |
| 初始化前隐藏导致可见性失配 | 单变量改变提前隐藏时机即可从通过变失败 | 已复现的触发条件，优先修复 |

## 3. 实施方案

1. 增加 `StartupWindowCoordinator`，集中协调原生句柄、前端就绪、静默启动设置、用户主动显示四种输入。
2. 前端 `app.js` 在路由就绪、Vue 挂载及 nextTick 后，通过独立 `desktop-ready` Post 消息通知宿主。不复用业务 API 的成功作为窗口就绪条件。
3. 初始化前不隐藏。静默启动在就绪后才隐藏；若用户已经主动显示，迟到的就绪事件不能再隐藏窗口。就绪先于句柄到达时延后执行；重复就绪只处理一次。
4. 正常启动就绪后与托盘显示共用恢复入口。普通显示采用 SW_SHOW，实际最小化时采用 SW_RESTORE，保留已最大化窗口的状态。
5. 恢复后投递专用窗口消息，由 HWND 所属线程使用实际客户区边界发送 WM_SIZE，让框架重新设置 WebView Bounds，再请求父子窗口重绘。无需改变尺寸制造重绘，不直接改 Chromium 子窗口，不禁用 GPU。
6. 检查 `SetWindowSubclass` 的返回值；安装失败明确报错，避免把未安装的恢复/关闭逻辑误当作生效。零句柄不允许进入协调器。

如果前端永远没有就绪消息，启动协调器不会隐藏窗口。日志将停留在 `waiting for frontend-ready`，便于与已挂载后的呈现问题区分。本次没有添加超时后强制隐藏，避免把前端错误状态再次隐藏。

首次静默启动在前端就绪之前可能短暂显示原生窗口。这是当前最小修复的可见权衡；不引入透明分层窗口、全局浏览器参数或新的原生控制器 API。

## 4. 自动化证据

### 单元与前端行为测试

- 新增 `StartupWindowCoordinatorTests`：就绪前不隐藏；正常就绪后显示同步；用户显示优先于迟到隐藏；就绪早于句柄；零句柄拒绝。
- 旧立即隐藏行为：5 项中 4 项失败、1 项通过。修复后结合身份和 DPI 测试共 21 项通过。
- 前端新增 DOM commit 前不发 ready、原生 Post 消息信封、无桌面桥接仍可启动的行为测试。前端共 17 项通过。
- 前端 AST 检查通过：33 个 JS 模块、16 个可执行 Vue 模板。

### 真正的 WebView2 功能测试

`tests/ReferenceRAG.Desktop.SmokeTests` 是独立交互桌面测试进程，链接生产启动协调器、恢复入口和原生钩子，使用真实 InfiniFrame/WebView2、本地自定义协议页面。不会启用业务服务、修改开机启动注册表或替换正在运行的用户进程。

```text
dotnet run --project tests/ReferenceRAG.Desktop.SmokeTests -- --reference-flow
dotnet run --project tests/ReferenceRAG.Desktop.SmokeTests -- --reproduce-early-hide
dotnet run --project tests/ReferenceRAG.Desktop.SmokeTests
dotnet run --project tests/ReferenceRAG.Desktop.SmokeTests -- --silent
```

| 场景 | 实测结果 |
| --- | --- |
| 参考可见性流程：初始化阶段不隐藏、普通恢复 | 退出码 0，5 轮通过 |
| 旧流程反例：初始化阶段提前隐藏 | 退出码 1，`Timed out: ready=True, stage=1` |
| 新流程正常启动 | 退出码 0，5 轮通过 |
| 新流程静默启动 | 退出码 0，5 轮通过 |

**测试边界：** 断言真实页面通过 WebView 发送就绪消息、静默就绪后主窗口隐藏，以及恢复后主窗口和直接 WebView 宿主子窗口可见。尚未做截图像素断言，也未覆盖所有显卡、DPI 和多屏组合；可见性通过不等同于对每一像素的呈现证明。

`scripts/verify-gate.ps1` 默认筛选已加入协调器和 DPI 测试；`-DesktopSmoke` 显式运行正常/静默两种真实窗口测试，需要 Windows 交互桌面与 WebView2。本次直接运行了这些命令，但未运行整个仓库统一脚本，未修改 CI，因此不声称原生 smoke 已成为 CI 强制门禁。

桌面宿主构建成功，0 警告、0 错误。当前用户发布进程未热替换；需使用新构建验证实际业务窗口。

## 5. 对历史结论的修正与参考链接

此前黑屏文档将遮挡算法锁死、MoveWindow 竞态等推断写成确定根因，并把框架调用字符串存在当作修复证据，证据强度不足。本次采用同一引擎的正反运行对照，参考 infiniReq 的质量证据分层，保留必要边界。

NuGet 0.62.1 元数据记录框架提交为 `3b6b0e1ad9430ad5ea70296c7746aef63b82d1c2`。对应 [窗口消息处理源码](https://github.com/InfiniLore/InfiniFrame/blob/3b6b0e1ad9430ad5ea70296c7746aef63b82d1c2/src/InfiniFrame.NativeBridge/Native/src/Runtime/Platform/Windows/Core/WindowProc.Win32.cpp) 中，非最小化 WM_SIZE 会调用 RefitContent；[WebView 创建源码](https://github.com/InfiniLore/InfiniFrame/blob/3b6b0e1ad9430ad5ea70296c7746aef63b82d1c2/src/InfiniFrame.NativeBridge/Native/src/Runtime/Platform/Windows/WebView/WebView2Attach.Win32.cpp) 明确采用异步环境与控制器创建。
