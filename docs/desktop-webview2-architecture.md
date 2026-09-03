# Desktop 桌面端架构：WPF + WebView2 + 内嵌 Kestrel

## 概述

`ReferenceRAG.Desktop` 是一个 WPF 应用，**不依赖外部浏览器或独立服务进程**。它在同一个进程内同时运行：

- **Kestrel HTTP 服务器**（ASP.NET Core WebApplication）—— 提供 REST API + Vue SPA 静态文件
- **WebView2**（Chromium 内核嵌入浏览器）—— 渲染 `http://localhost:{port}/`，即上面那个本地服务

两者共享同一个 .NET 进程，通信走 `localhost`，无需网络权限，也不暴露公网端口。

---

## 项目结构

```
src/ReferenceRAG.Desktop/
├── App.xaml                  # Application 声明：托盘图标资源、事件挂接
├── App.xaml.cs               # 启动主控：单例锁、端口决策、后端启动、托盘菜单
├── MainWindow.xaml           # 窗口布局：WebView2 控件 + 加载面板
├── MainWindow.xaml.cs        # 窗口逻辑：WebView2 初始化、导航、最小化到托盘
├── HostBootstrapper.cs       # Kestrel 构建封装：服务注册、中间件、静态文件缓存策略
├── PortHelper.cs             # 端口工具：检测空闲端口、申请随机端口
├── StartupManager.cs         # 启动行为管理：注册表自启、本地 JSON 最小化启动配置
└── Assets/
    └── tray-icon.ico         # 托盘图标
```

依赖关系：

```
ReferenceRAG.Desktop
  ├── ReferenceRAG.Service    (控制器、Hub、扩展方法)
  ├── Microsoft.Web.WebView2  (WebView2 WPF 控件)
  └── Hardcodet.NotifyIcon.Wpf (托盘图标)
```

---

## 启动流程

```
Application_Startup
  │
  ├─ CheckWebView2Runtime()       检测 WebView2 Runtime，缺失则提示下载并退出
  ├─ AcquireSingleInstanceMutex() 全局 Mutex 防多开，已有实例则提示退出
  ├─ BuildTrayContextMenu()       构建托盘右键菜单（打开/自启/最小化启动/退出）
  ├─ ResolvePort()                读 appsettings.json 配置端口，被占用则随机申请空闲端口
  │
  ├─ new MainWindow(port).Show()  提前显示窗口（加载面板状态），让用户立即看到 UI
  │
  ├─ HostBootstrapper.Build(port) 构建 WebApplication（注册服务、配置中间件，不启动）
  ├─ InitializeSearchAsync()      初始化 BM25 索引（耗时操作，在 Kestrel 启动前完成）
  ├─ webApp.RunAsync(_cts.Token)  在后台线程启动 Kestrel
  │
  ├─ WaitForKestrelReady()        轮询 HTTP GET，最多等 10s 确认 Kestrel 就绪
  │
  └─ mainWindow.OnBackendReady()  通知窗口：后端就绪，可以导航
```

### 双阶段就绪逻辑

WebView2 初始化和 Kestrel 启动**并发进行**，两者都就绪后才导航。

```
MainWindow_Loaded          OnBackendReady (由 App 调用)
       │                          │
  CoreWebView2Environment.CreateAsync
  webView.EnsureCoreWebView2Async
       │                          │
  _webView2Initialized = true     _backendReady = true
       │                          │
       └──────── 双方互相检查对方标志 ──────┘
                          │
                     ShowAppAsync()
                          │
                  ClearBrowsingDataAsync(DiskCache)
                  loadingPanel → Collapsed
                  webView       → Visible
                  Navigate("http://localhost:{port}/")
```

这样无论哪个先完成，都不会死等，也不会提前导航到尚未就绪的地址。

---

## 关键文件详解

### App.xaml.cs — 启动主控

| 职责 | 方法 |
|------|------|
| 全局异常捕获 | `DispatcherUnhandledException` + `AppDomain.UnhandledException` → `crash.log` |
| 单实例保证 | `AcquireSingleInstanceMutex()` — 全局 Mutex `Global\ReferenceRAG_Desktop_SingleInstance` |
| 端口决策 | `ResolvePort()` — 先读配置（默认 7897），端口被占用则 `PortHelper.GetFreeTcpPort()` |
| 后端启动 | `HostBootstrapper.Build()` → `InitializeSearchAsync()` → `webApp.RunAsync()` |
| Kestrel 探活 | `WaitForKestrelReady()` — 每 100ms 发一次 HTTP GET，超时 10s 抛 `TimeoutException` |
| 托盘菜单 | `BuildTrayContextMenu()` — 动态构建，含 `IsCheckable` 菜单项状态控制 |
| 退出清理 | `Application_Exit` — 取消 CancellationToken，等后台任务最多 3s，释放 WebApp/Mutex/托盘 |

### MainWindow.xaml / .xaml.cs — 主窗口

**XAML 布局**（两层叠加）：
```xml
<Grid>
    <wv2:WebView2 x:Name="webView" Visibility="Hidden" />  <!-- 后端就绪后显示 -->
    <Grid x:Name="loadingPanel" Background="#1E1E2E">       <!-- 启动时显示 -->
        <StackPanel>
            <TextBlock>ReferenceRAG</TextBlock>
            <TextBlock x:Name="loadingText" />              <!-- 状态文字 -->
            <ProgressBar x:Name="loadingProgress" IsIndeterminate="True" />
        </StackPanel>
    </Grid>
</Grid>
```

**关键行为**：

- **最小化 → 托盘**：`OnStateChanged` 捕获 `Minimized`，执行 `Hide()` + `ShowInTaskbar = false`
- **关闭按钮 → 托盘**：`OnClosing` 取消关闭，执行 `Hide()` + `ShowInTaskbar = false`（退出须走托盘菜单）
- **从托盘恢复**：`RestoreFromTray()` — `Show()` + `WindowState = Normal` + `Activate()`

### HostBootstrapper.cs — Kestrel 构建封装

Desktop 与 Service（`Program.cs`）共用同一套 RAG 服务逻辑，通过扩展方法抽离：

```csharp
// 服务注册（与 Service/Program.cs 共用）
builder.Services.AddRagCoreServices(builder.Configuration);

// 中间件（Desktop 特有）
app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles(staticFileOptions);  // 见静态文件缓存策略

// 端点注册（与 Service/Program.cs 共用）
app.UseRagEndpoints();
// 包含：MapControllers / MapFallbackToFile("index.html") / MapHub<IndexHub>
```

**Desktop 相比 Service 禁用的功能**：Swagger、MCP、API Key 认证（本地单用户场景不需要）

**静态文件缓存策略**：

| 文件 | Cache-Control |
|------|--------------|
| `index.html` | `no-cache, no-store, must-revalidate` |
| 其他（带 hash 的 JS/CSS） | `public, max-age=31536000, immutable` |

### PortHelper.cs — 端口工具

```csharp
// 检测端口是否空闲：尝试绑定 Loopback，成功则空闲
PortHelper.IsPortFree(7897)

// 向 OS 申请空闲端口：绑定 port=0，读取实际分配的端口号
PortHelper.GetFreeTcpPort()
```

### StartupManager.cs — 启动行为管理

| 功能 | 存储位置 | 说明 |
|------|---------|------|
| 开机自启动 | 注册表 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` | 写入 `"{exe}" --minimized` |
| 最小化启动 | `AppContext.BaseDirectory/desktop-settings.json` | JSON 持久化 `DesktopSettings` record |

---

## Vue SPA 集成

**构建产物复制**（`.csproj` MSBuild Target `CopyVueSpaFiles`）：

```
dashboard-vue/dist/**  →  {OutputDir}/wwwroot/
```

- Build 时：dist 不存在则自动执行 `npm install && npm run build`
- Publish 时：先清空旧 `wwwroot`（防旧 hash 文件残留），再复制新 dist

**运行时**：Kestrel 的 `UseStaticFiles` 服务 `wwwroot/`，`MapFallbackToFile("index.html")` 支持 Vue Router history 模式。

---

## CORS 策略

WebView2 加载 `http://localhost:{port}/`，向同地址发 API 请求，**严格同源**：

```csharp
policy.WithOrigins($"http://localhost:{port}")
      .WithMethods("GET", "POST", "PUT", "DELETE", "PATCH", "OPTIONS")
      .WithHeaders("Content-Type", "Authorization", "X-API-Key")
      .AllowCredentials();
```

端口在运行时确定，CORS 策略与端口动态绑定，避免固定端口带来的冲突问题。

---

## WebView2 数据目录

```csharp
var env = await CoreWebView2Environment.CreateAsync(
    browserExecutableFolder: null,                           // 使用系统安装的 WebView2 Runtime
    userDataFolder: Path.Combine(AppContext.BaseDirectory, "webview2-data"));
```

- 用户数据（Cookie、LocalStorage 等）存于应用目录下，随应用卸载一并清除
- 每次导航前清除磁盘缓存（`ClearBrowsingDataAsync(DiskCache)`），保证前端始终加载最新版本

---

## 发布流程

脚本：`resource/scripts/deploy-desktop.bat` / `deploy-desktop.ps1`

```
Step 1: Stop-DesktopProcess        停止正在运行的 ReferenceRAG.Desktop 进程
Step 2: Clear-WebView2Cache        清除发布目录下的 WebView2 磁盘缓存
Step 3: Build-Frontend             npm install (首次) + npm run build
Step 4: Publish-Desktop            dotnet publish -r win-x64 --self-contained false -o publish/desktop/
```

**输出目录**：`{ProjectRoot}/publish/desktop/`

**参数**：
```powershell
.\deploy-desktop.ps1                # Release + 构建前端
.\deploy-desktop.ps1 -SkipFrontend # 跳过前端构建（前端未变动时）
.\deploy-desktop.ps1 -Launch       # 发布完成后自动启动
.\deploy-desktop.ps1 -Configuration Debug
```

---

## 依赖运行时要求

| 依赖 | 说明 |
|------|------|
| .NET 10 Runtime | 必须，非 self-contained 发布 |
| WebView2 Runtime | 必须，启动时 `CheckWebView2Runtime()` 检测，缺失弹提示 |
| Node.js / npm | 仅构建时需要，运行时不需要 |
