# InfiniTool 架构迁移交付记录

日期：2026-09-03。设计依据：[迁移可行性报告](infinitool-architecture-migration-feasibility.md)。

## 代码与边界

- 原工作区已保存在 GitHub `Old` 分支，快照提交 `9d8311bdcb3b84ff7cf8604148b342ee566bbce8`。
- 迁移位于 `codex/infinitool-migration` 工作区，尚未提交或推送。
- 不改变数据库结构、索引算法和模型配置格式；真实数据、模型和本机配置没有参与测试或迁移。
- 两端均采用 .NET 10 普通发布，明确关闭 AOT 和裁剪。

## 已实现

| 层 | 内容 |
|---|---|
| Infrastructure | 数据模型、配置、SQLite/FTS5、向量/图谱存储、模型推理及文件监控 |
| Business | 检索/索引/聊天编排、后台任务、统一组合入口及共享 wwwroot |
| ApiHost | MVC、MCP、鉴权、实时事件及进程内请求适配 |
| WebHost | HTTP、SignalR、静态 ES Modules、浏览器历史路由 |
| DesktopHost | InfiniFrame、嵌入资源、IPC、hash 路由、托盘及本地目录选择 |

相比设计目录增加共享 ApiHost 适配库：IPC 在进程内执行原 MVC 管线，复用路由、参数绑定、API Key 校验、状态码和错误语义，避免重新实现两套路由。桌面默认不监听 HTTP；聊天内部工具同样走进程内请求。设置 `Desktop:EnableLocalApi=true` 可开放本机 HTTP/MCP。

前端保留 Vue 和 Naive UI，以本地 ES Modules 加载。17 个组件转为 JS 和运行时模板；去除 Vite、TypeScript/SFC 发布编译和 npm 构建步骤。第三方库预置于 vendor 并附许可证。Vue 模板仍在运行时编译，与可行性报告约定一致。

传输层统一 Axios/Fetch 调用，桌面处理请求关联、超时、取消、SSE 分块及索引事件；Web 保留 HTTP/SSE/SignalR。新增 Markdown 渲染过滤、平台能力检测、聊天停止按钮、任务退出等待和数据目录单写入者锁。

旧 WPF 工程、旧后端工程入口和 dashboard-vue 源码已移除；历史保留于 Old。解决方案、Windows 发布配置、GitHub 构建流程及两个 Dockerfile 已指向新宿主。配置已归入 `support/config/local`，服务脚本归入 `support/tools/scripts`；根目录不再保留 `src`。

## 运行与发布

```powershell
dotnet run --project Host/ReferenceRAG.WebHost
dotnet run --project Host/ReferenceRAG.DesktopHost
dotnet publish Host/ReferenceRAG.WebHost -c Release -r win-x64 --self-contained true -o support/artifacts/publish/web
dotnet publish Host/ReferenceRAG.DesktopHost -c Release -r win-x64 --self-contained true -o support/artifacts/publish/desktop
```

桌面运行 `ReferenceRAG.DesktopHost.exe`，需要 WebView2 Runtime。Web 运行 `ReferenceRAG.WebHost.exe`，地址以启动配置为准。原 `support/config/local/appsettings*.json` 在开发构建时复制到宿主目录；部署时使用发布目录配置。`REFERENCERAG_CONTENT_ROOT` 可指定配置根目录，相对数据路径以此为基准。桌面自动启动设置仍在发布目录 `desktop-settings.json`。

Web 编辑共享静态文件后需复制到运行目录；桌面嵌入资源需重新发布 C# 宿主。Node 只参与检查，不参与生产前端生成。`support/tools/migration/convert-frontend.cjs` 和 `move-backend.ps1` 是历史一次性工具，不应在迁移后的工作区重复执行。

## 实际验证

- Release 解决方案构建成功；Web 和 Desktop 的 win-x64 自包含发布成功。
- 最终 Web 发布程序实际启动成功：页面 HTTP 200、索引统计读取成功，缺失 JS 资源返回 404。
- 61 项相关业务/存储回归测试通过：查询统计、内容哈希、Wiki 链接、BM25、FTS5、图谱。
- 6 项宿主测试通过：鉴权/绑定/状态码、中文流式输出、取消活动流、数据目录单写入者。
- 前端 17 个组件模板与 JS 语法检查通过，3 项传输测试通过。
- 隔离测试数据中完成文件索引；Web 浏览器验证登录、源列表、检索结果及聊天流式完成，其他主要路由完成加载检查。
- InfiniFrame 实际窗口成功显示登录页，启动日志证明进程内 API 接收请求。自动化无法激活该窗口，未将桌面登录后交互、原生目录选择及托盘退出标为通过。

测试使用本地确定性 AI 服务和独立测试目录；并未验证真实 ONNX/GPU 推理效果、模型下载、生产规模性能、Windows 服务安装或 Docker 实际运行。发布流程已在本机验证，GitHub Actions 尚未远程运行。

## 剩余限制与回退

依赖保留既有版本。NuGet 仍报告 SQLitePCLRaw.lib.e_sqlite3 2.1.11 和 Microsoft.OpenApi 2.4.1 的高严重性漏洞警告，以及现有平台/空值警告；需另行安排兼容性升级。构建通过不代表这些警告已解决。

新宿主间的数据锁不约束 Old 旧程序，禁止新旧同时写同一目录。回退使用 Old 代码及原配置；被忽略的数据库、模型和配置不属于 Git 快照，切换前应停应用并自行备份需要保留的数据。迁移测试没有对真实数据执行重建、清理或格式转换。
