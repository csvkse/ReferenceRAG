# ReferenceRAG → InfiniTool 架构迁移可行性与方案

调研日期：2026-09-03。依据：ReferenceRAG 当前工作区及本地 InfiniTool 实际源码，辅以官方文档。本文是迁移设计，不代表已实现或通过运行验证。

后续执行记录见 [迁移交付说明](infinitool-migration-status.md)。下面的版本状态保留调研当时记录；Old 后续已推送 GitHub。

## 1. 结论与范围

**可行，建议采用 InfiniTool 的 Host / Business / Infrastructure 分层和共享免构建前端，新增 WebHost 适配浏览器。** 桌面使用 InfiniFrame、内嵌静态资源和 IPC；Web 使用 ASP.NET Core、同一份静态资源和 HTTP/实时通信。业务逻辑保持一份。

- 允许非 AOT：第一阶段两端均采用常规 .NET 10 发布，关闭 NativeAOT 和裁剪。无需为迁移改写现有反射、JSON、MVC、AI SDK 与 ONNX 相关实现。
- “Web 不再使用编译方案”按取消前端 Vite 打包、TypeScript 转译、SFC 编译理解。C# 后端仍需 build/publish；静态文件复制或嵌入不属于前端打包。
- 与 InfiniTool 一样使用 Vue ESM + JS 组件模板时，Vue 仍会在运行时编译模板。若连运行时编译也禁止，需要改用手写 render 函数或原生 DOM，不能原样采用其模板方案。
- 此次不迁移数据库、不改检索算法、不升级 AI 框架，不将 InfiniTool 的其他业务功能引入 ReferenceRAG。
- 桌面首要目标仍为 Windows。InfiniFrame 的跨平台定位不等于现有托盘、Windows API、GPU 原生依赖已跨平台。

## 2. 版本保留

已创建并切换到 `Old`，按用户追加授权提交原工作区现有修改及未被忽略的新增文件：

- 原提交：`1a49274630f67d8406007a4005f5a85835f858fb`。
- 快照提交：`9d8311b`，`chore: preserve current workspace before architecture migration`。
- 快照提交后 `git status --short` 无输出；当前分支为 `Old`。
- 被 Git 忽略的本地配置、模型、数据库、缓存等不在快照范围内。Old 是代码回退点，不是数据备份。
- 本报告在快照之后新增，未提交；没有推送远程、修改 master 或开始实施迁移。
- Git 检测到目录所有者不匹配，仅对本次 Git 命令设置精确目录的 `safe.directory`，没有修改全局配置或文件权限。

## 3. 源码证据与架构差异

以下路径以各自仓库根目录为基准。

| 方面 | ReferenceRAG 实际实现 | InfiniTool 实际实现 | 迁移含义 |
|---|---|---|---|
| 后端分层 | `src/ReferenceRAG.Core`、`Storage`、`Service`、`Desktop` | `Host/InfiniTool.DesktopHost`、`Business/InfiniTool.Business`、`Infrastructure/InfiniTool.Infrastructure` | 重组职责和依赖，保留领域实现 |
| 桌面 | WPF + WebView2，`HostBootstrapper.Build()` 启动本机 WebApplication，页面访问 localhost | `DesktopApplication.cs` 使用 InfiniFrame，`app://localhost/index.html`、资源拦截、WebMessage | 桌面 UI 改为 IPC，不再强依赖本机 HTTP 服务 |
| 前端 | Vue 3、TypeScript、SFC、Vite、Naive UI、Pinia、Vue Router | 本地 Vue ESM、JS 模板组件、原生 ES 模块、按 feature 组织 | 不能只删 Vite；页面与组件依赖必须迁移 |
| 构建 | Service 的 `PublishRunWebpack` 执行 npm install/build；Desktop 的 `CopyVueSpaFiles` 可能自动构建 | Business 嵌入 wwwroot；FrontendGating 只执行 Node 检查 | 去掉前端构建产物依赖，保留检查 |
| 通信 | Axios/HTTP、索引 SignalR、聊天 POST SSE | `core/ipc/ipc-bridge.js` 依赖 `chrome.webview`，无桥接即失败 | 浏览器需要新的 transport，不能直接复用桌面桥接 |
| 路由 | ASP.NET Controllers，多个 HTTP 动词与现有公开接口 | `IpcRouter.MapPost`、`IIpcRouteModule`、BusinessComposition | 共用业务服务，分别适配传输协议 |
| 生命周期 | AddHostedService、后台索引、启动同步、清理 | DesktopApplication 组装服务及桌面生命周期 | 保留 RAG 后台服务语义，不能仅复制窗口入口 |
| AOT | 现有项目有 Newtonsoft、Fody、GPU/分词等依赖 | DesktopHost 设置 PublishAot，Business 设置 IsAotCompatible | 借鉴结构即可，AOT 不是必要条件 |

注意：InfiniTool 根 README 的 Vanilla JS 描述落后于其当前前端。实际以 `Business/InfiniTool.Business/wwwroot/README.md`、`vendor/README.md`、`app/create-app.js` 为准。该方案也没有把 InfiniTool 当前实现视为已经验证过的 Web 产品。

## 4. 目标目录与依赖

```text
Host/
  ReferenceRAG.DesktopHost/       # InfiniFrame、窗口/托盘、IPC、桌面能力
  ReferenceRAG.WebHost/           # ASP.NET Core、认证、HTTP、SignalR/SSE、MCP
Business/
  ReferenceRAG.Business/
    Composition/                # 统一服务注册和业务模块组装
    Features/
      Search/ Indexing/ Sources/ Models/ Chat/
      Graph/ Settings/ Monitoring/
        Contracts/ Models/ Services/ Routes/
    wwwroot/
      index.html
      app.js
      app/                      # 壳层、页面、共享 Vue 组件
      core/transport/           # HTTP 与 IPC 实现、事件接入
      core/platform/            # 桌面/浏览器能力差异
      features/                 # 按功能组织 state 与 api
      shared/ styles/ vendor/
Infrastructure/
  ReferenceRAG.Infrastructure/
    Features/
      Database/ VectorStore/ Tokenization/ Inference/
      FileSystem/ Configuration/ Logging/ Ipc/
Tests/
  ReferenceRAG.Tests/
```

依赖遵循 InfiniTool 当前方向：`Host → Business → Infrastructure`，Host 也可引用 Infrastructure。Infrastructure 不反向引用 Business，更不能引用 Host。

ReferenceRAG 目前 `Storage → Core`，不能直接将 Storage 改名 Infrastructure、Core 改名 Business 后再让 Business 引用 Infrastructure，这会形成循环。迁移时逐个归类：SQLite、向量、文件、推理等技术接口及其必要数据类型放到 Infrastructure 的对应 feature；搜索/索引的用例、编排和业务 DTO 放 Business。跨层对象在业务边界映射，避免让 Infrastructure 引用业务 DTO。迁移期间允许旧 Core/Storage 暂时保留，待依赖闭合后移除，不要求一次完成物理目录搬迁。

InfiniFrame 包只出现在 DesktopHost；Business 不引用 WPF、WebView、HttpContext 或 IHubContext。将现有 AddRagCoreServices 拆为业务注册、存储注册及各宿主的传输注册。继续使用 Generic Host 管理后台服务、依赖注入和退出释放；无需照搬 InfiniTool 所有手工实例化方式。

## 5. 双端通信设计

前端 feature 只调用统一的业务 API 门面；门面依赖初始化时明确选择的 transport。IPC 失败不能自动降级到 HTTP，以免写操作重试造成重复执行。

| 能力 | 桌面适配 | Web 适配 | 共享部分 |
|---|---|---|---|
| 普通查询/保存 | InfiniFrame IPC 路由 | 现有 HTTP Controllers | DTO、验证、业务服务、错误码 |
| 索引启动/取消 | IPC 命令，返回 jobId | 现有 HTTP API | 索引任务与取消语义 |
| 索引进度 | 宿主 WebMessage 事件 | 保留 SignalR | 索引事件模型与业务通知接口 |
| AI 聊天 | IPC 启动/取消 + 增量事件 | 保留 POST SSE | MafChatService 的异步事件序列 |
| 打开目录/托盘 | Windows 能力接口 | 显式不支持或服务器管理能力 | 能力描述和页面条件展示 |
| 对外 MCP/API | 如需保留本地调用，则独立启用端口或适配器 | 保留原 HTTP/MCP 接口 | 工具实现和业务服务 |

具体拆分点：

1. `IndexService` 当前构造函数直接依赖 `IHubContext<IndexHub>`。提取索引事件发布接口，WebHost 转发到 Hub，DesktopHost 转发 IPC；后台服务本身不认识客户端传输。
2. `MafChatService.StreamAsync` 已提供 `IAsyncEnumerable<SseEvent>`，可以保留生成逻辑。`MafChatController` 的 HTTP 响应写入留在 WebHost，桌面另加事件适配，避免将 SSE 字节格式穿透业务层。
3. InfiniTool 基础 IpcRouter 只有普通请求/响应，handler 未接受 CancellationToken。RAG 的长任务不能通过一个长时间等待的 invoke 包装结束，需要明确 job/session/request 标识、取消命令、增量序号、完成/失败事件、窗口关闭后的订阅释放。
4. 同时处理多窗口/多浏览器连接时，按用户/会话隔离事件。重连后查询任务状态或补齐事件，避免漏进度；慢客户端采用有界缓冲/进度合并。
5. 保持 HTTP 路径、动词、响应字段大小写、API Key 行为和原 MCP SSE 兼容。IPC 的 POST 路径不必等同于公开 HTTP REST 路由，不把 InfiniTool 的通用 IPC 调度器直接暴露到公网。
6. Web 的“本地目录”指服务器目录，不能把浏览器用户机器上的路径传给服务器当作可访问文件。桌面使用文件夹选择器；Web 保留服务器侧源管理，并限定授权目录。

桌面是否需要继续为外部工具提供 HTTP/MCP，是迁移实施前需要明确的兼容项。推荐保留可配置的本地 API 服务能力，但让桌面 UI 本身通过 IPC 工作。若双宿主同时访问同一索引目录，必须采用单写者/服务连接模式；不能默认启动两套文件监控和索引写入。

## 6. 免构建前端

建议两端共用本地 `vue.esm-browser.prod.js`、原生 ES 模块、JS 组件与静态 CSS。用 importmap 或相对路径解析依赖，所有运行时依赖本地随包分发、锁定版本并保留许可证。

- `.vue` 的 template/script/style 拆为 JS 组件和 CSS；`script setup` 改为 `setup()`；TypeScript 类型转为 JSDoc/契约校验，浏览器不加载 TS。
- 取消自动导入、`@/` 隐式别名与 `import.meta.env.VITE_*`；依赖显式导入，配置改为服务端提供的非敏感运行时配置。API Key 等秘密不放到静态配置。
- Naive UI、图标及 Pinia 不能默认以现有 npm 包入口直接在浏览器运行。推荐逐页替换为 InfiniTool 式共享 Vue 组件和 feature state；保留依赖前先验证浏览器 ESM 产物、递归依赖与离线运行。
- Vue Router 可使用浏览器 ESM 版本；桌面用 hash 路由，Web 可用 history + 精确 SPA fallback，以保留已有 URL。采用 InfiniTool 页面状态导航时，也必须补齐浏览器前进/后退、深链接和登录跳转。
- 当前路由有登录和 12 个业务页面。按路由清单验收；`ModelsSimple.vue` 未出现在所读路由表中，不以文件数量当成功能数量，也不因未挂路由就直接删除。
- 保留 Markdown 展示功能，核查 marked 等依赖的本地产物与 HTML 净化。IPC 来源限制和导航限制在宿主完成，前端隐藏按钮不能替代授权。

**样式取舍：** InfiniTool 目前使用 Tailwind 浏览器即时编译器。Tailwind 官方明确该方式面向开发，不推荐生产。为了同时满足生产 Web 和无前端构建，推荐保留其主题变量、布局规范和组件风格，双端统一使用手写静态 CSS；这是对样式实现的调整，Host/Business/Infrastructure 与 Vue feature 架构不变。若要求完全照搬 Tailwind 运行时，需要单独验证启动耗时、样式闪烁和 CSP，并接受其生产限制。

**CSP 约束：** Vue 字符串模板涉及运行时编译，严格禁止动态代码生成的 CSP 环境需要额外验证；若必须使用严格策略，选择手写 render 函数，而不是为了保留模板随意放宽整站策略。

## 7. 发布、数据与兼容性

- WebHost 从共享 wwwroot 复制原始静态资源；DesktopHost 从 Business 程序集嵌入资源加载，开发模式读取源码目录。嵌入资源更新仍需重新发布 .NET 程序；Web 外置资源可独立部署，但必须原子切换同版本资源集合。
- 修改 Service/新 WebHost、DesktopHost 项目文件、发布配置及 `.github/workflows/desktop-release.yml`，移除 npm build/dist 路径；Node 可以继续用于语法检查与测试，不进入应用运行环境。
- 现有 Desktop 静态文件策略对非 HTML 设置一年 immutable 缓存，不能照搬给免打包后的 `app.js` 等稳定文件名。入口与模块使用重验证或整套版本目录，并验证滚动发布期间没有新旧文件混用。
- SPA fallback 不得把不存在的 `/api`、`/hubs`、MCP 路径或 JS 文件返回为 index.html；ES 模块 MIME、importmap 顺序和 app:// 加载需实测。
- 先保留现有配置路径、数据目录、SQLite schema、FTS/向量索引、模型位置和分词资源。不要同时切换到 InfiniTool 的 LocalAppData 数据策略。
- ONNX GPU、SQLite 向量扩展、HuggingFace 分词器的原生库及 jieba 资源需逐宿主检查输出；非 AOT 不会消除这些部署依赖。Windows 桌面优先验收，其他 Web 服务器平台另做原生依赖探针。
- 不假定二进制回退能恢复被新版本修改的数据。每次可能写数据的验证使用副本；若以后需要 schema 迁移，另设备份/回滚步骤。

## 8. 分阶段实施与验收

| 阶段 | 实施范围 | 完成条件 |
|---|---|---|
| P0 基线 | 从 Old 建实施分支；登记路由/API/数据配置与发布清单 | 原版代表性搜索、索引、聊天行为可复现，数据有独立副本 |
| P1 最小双端原型 | 同一免构建首页与搜索页，Web HTTP + 桌面 IPC，常规 .NET 发布 | 两端真实检索同一数据副本；离线加载依赖；不运行 Vite/npm build |
| P2 后端边界 | 提取业务注册、技术接口、索引通知和聊天传输 | 无循环项目引用；Business 无 Host/HTTP/WebView 依赖；HTTP 契约不变 |
| P3 前端分批 | 登录/壳层 → 搜索/源/设置 → 模型/监控/性能 → BM25/图谱 → 聊天/帮助 | 每批两端交互、错误/忙碌状态、桌面与窄屏验证通过 |
| P4 桌面与外部能力 | 托盘、单实例、启动/退出、可选本地 API/MCP | 关闭/恢复正确；任务取消与资源释放；原外部调用方式保持可用 |
| P5 发布切换 | 发布文件和 CI 去掉前端构建，静态资源版本策略 | 干净环境可发布并启动；包内无 dist 依赖；旧 API 客户端回归通过 |

P1 是是否继续迁移的技术关口。未验证 InfiniFrame 非 AOT 启动、app:// ES 模块、双端搜索与聊天流前，不大批搬页面。实施时先完成可观察行为的小测试再改动；文案与目录调整不机械增加测试。

重点验证矩阵：搜索排序/结果字段、索引增删改/取消/重连、模型加载与切换、聊天首段/增量/取消/错误、登录失效、目录权限、MCP 调用、数据重启一致性、离线前端加载、缓存更新，以及两端启动和关闭生命周期。复用现有检索/存储测试，但需要新增传输契约和浏览器行为验证，不能以现有测试文件存在代替已经通过。

## 9. 工作量和调研边界

这是中到较大规模迁移，主要成本来自 UI 依赖替换与双端行为验证，而非目录重命名。单名熟悉项目的开发者可先用 2–4 个工作日验证 P1；整体粗估 4–7 人周，属于排期参考，未做逐控件估算，不含功能扩展、跨平台桌面及多租户改造。P1 后再调整范围和时间。

本次实际完成：读取两项目的关键项目文件、宿主、路由/IPC、前端入口和依赖、后台服务耦合及发布流水线；核实官方免构建前端说明；创建并验证 Old 快照。未启动应用、运行构建/测试、发布探针或性能基准，因此“可行”是源码与架构层面结论，运行兼容性仍待 P1 证明。源码定位中个别猜测文件路径不存在，已通过实际文件列表定位到 MafChatController 等实现，不将路径查找失败当作功能缺失。

## 10. 可核查资料

- ReferenceRAG：`src/ReferenceRAG.Service/Extensions/RagWebHostExtensions.cs`、`src/ReferenceRAG.Desktop/HostBootstrapper.cs`、`src/ReferenceRAG.Service/Services/IndexService.cs`、`src/ReferenceRAG.Service/Services/MafChatService.cs`、`src/ReferenceRAG.Service/Controllers/MafChatController.cs`。
- ReferenceRAG 前端与发布：`dashboard-vue/package.json`、`dashboard-vue/src/router/index.ts`、`dashboard-vue/src/config/env.ts`、`dashboard-vue/src/stores/index.ts`、`dashboard-vue/src/views/Chat.vue`、两个宿主 csproj、`.github/workflows/desktop-release.yml`。
- InfiniTool：`Host/InfiniTool.DesktopHost/DesktopApplication.cs`、`Business/InfiniTool.Business/Composition/BusinessComposition.cs`、`Infrastructure/InfiniTool.Infrastructure/Features/Ipc/Routing/IpcRouter.cs`、`Business/InfiniTool.Business/wwwroot/core/ipc/ipc-bridge.js`、`Business/InfiniTool.Business/wwwroot/app/create-app.js`。
- [Vue 免构建使用方式](https://vuejs.org/guide/quick-start.html)：支持浏览器 ES 模块与 setup；SFC 工作流需要构建工具。
- [Vue 生产部署](https://vuejs.org/guide/best-practices/production-deployment)：自托管免构建生产环境使用生产版本。
- [Vue Router 浏览器安装](https://router.vuejs.org/installation)：有浏览器 ESM 分发版本。
- [Tailwind Play CDN](https://tailwindcss.com/docs/installation/play-cdn)：浏览器编译方案官方定位为开发用途。
- [InfiniFrame 官方仓库](https://github.com/InfiniLore/InfiniFrame)：桌面 WebView 框架；当前迁移以本地 InfiniTool 锁定的 0.13.1 调用方式为基准，不以最新主线替代版本验证。
