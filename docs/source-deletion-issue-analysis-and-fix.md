# 数据源删除失败问题调研、根本原因分析与优化方案

## 1. 问题背景与现状调研

在 ReferenceRAG 桌面端（DesktopHost）的「向量索引管理」->「源列表」中，用户尝试删除名称为包含中文字符的源（如“小说”）时，界面弹出「删除失败」Toast 提示，源未能被删除。

### 1.1 现场日志证据

通过对桌面宿主运行日志（`Host/ReferenceRAG.DesktopHost/bin/Release/net10.0-windows/publish/Logs/app_20261017.log`）进行排查，捕获到如下关键请求链路：

```text
2026-10-17 23:32:50 [INF] Route matched with {action = "Delete", controller = "Sources"}. Executing controller action with signature System.Threading.Tasks.Task`1[Microsoft.AspNetCore.Mvc.ActionResult] Delete(System.String, Boolean) on controller ReferenceRAG.Service.Controllers.SourcesController (ReferenceRAG.ApiHost).
2026-10-17 23:32:50 [INF] Executing NotFoundObjectResult, writing value of type '<>f__AnonymousType0`1[[System.String, System.Private.CoreLib, Version=10.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]]'.
2026-10-17 23:32:50 [INF] Executed action ReferenceRAG.Service.Controllers.SourcesController.Delete (ReferenceRAG.ApiHost) in 0.2706ms
2026-10-17 23:32:50 [INF] Request finished HTTP/1.1 DELETE http://localhost/api/Sources/%E5%B0%8F%E8%AF%B4?deleteData=false - 404 null application/json; charset=utf-8 0.5406ms
```

---

## 2. 根本原因深入分析 (Root Cause Analysis)

经过全链路（前端交互 -> IPC 通信层 -> 进程内 HTTP 模拟器 -> ASP.NET Core 路由 -> 控制器与数据服务）的代码级追踪，确认导致该问题的核心原因和次生缺陷共有 4 处：

### 原因一：InProcessServer 未按照 ASP.NET Core 规范对 Request.Path 解码（核心故障点）

1. **前端 URL 生成与 IPC 传输**：
   桌面端前端运行在 WebView2 / InfiniFrame 环境中，通过自定义适配器（`ragFetch`）将 HTTP 请求封装为 IPC 消息发送给后端。
   浏览器内核在执行 `new URL('/api/Sources/小说', location.href).pathname` 时，标准规范会自动将非 ASCII 字符转为百分比编码（Percent-Encoding）：
   `/api/Sources/%E5%B0%8F%E8%AF%B4`。

2. **InProcessServer 解析缺陷**：
   在 `Host/ReferenceRAG.ApiHost/Transport/InProcessServer.cs` 中：
   ```csharp
   var parts = path.Split('?', 2);
   var request = new HttpRequestFeature {
       Method = method,
       Scheme = "http",
       Protocol = "HTTP/1.1",
       Path = parts[0], // <--- 直接使用编码后的字符串 "/api/Sources/%E5%B0%8F%E8%AF%B4"
       QueryString = parts.Length == 2 ? "?" + parts[1] : "",
       Body = input,
       RawTarget = path
   };
   ```
3. **ASP.NET Core 规范不符**：
   按照 ASP.NET Core 官方规范，`HttpRequestFeature.Path` 必须是已解码（Unescaped）的路径字符串，而原始请求目标应保存在 `RawTarget` 中。
   在 Kestrel 等网络服务器中，收到 `%E5%B0%8F%E8%AF%B4` 时会自动解码为 `小说` 填入 `Path`。
   而在 `InProcessServer` 中，`Path` 保留了 `%E5%B0%8F%E8%AF%B4`。
   ASP.NET Core 端点路由在从 `Path` 提取路由变量 `{name}` 时，直接得到了字符串 `"%E5%B0%8F%E8%AF%B4"` 而不是 `"小说"`！

### 原因二：SourcesController 严格相等比对且缺少防御性解码

在 `SourcesController.Delete(string name, ...)` 中：
```csharp
var config = _configManager.Load();
var source = config.Sources.FirstOrDefault(s => s.Name == name);
if (source == null)
{
    return NotFound(new { error = $"源 '{name}' 不存在" });
}
```
- `s.Name` 为配置中保存的 `"小说"`，而传入的 `name` 为 `"%E5%B0%8F%E8%AF%B4"`；
- 两者比对失败，控制器直接返回 HTTP 404 `源 '%E5%B0%8F%E8%AF%B4' 不存在`；
- 此外，比对采用区分大小写的 `==`，若名称大小写不一致也会导致 404；且未做 `Uri.UnescapeDataString` 防御性处理。

### 原因三：控制器删除流程缺乏事务性与异常回滚机制

在 `SourcesController.Delete` 中：
```csharp
_configManager.RemoveSource(name);
_fileMonitor.RemoveSource(name);

if (deleteData)
    await _pipeline.DeleteSourceAsync(name);
```
- **执行顺序不合理**：先从配置中删除了源，再清理向量与分块数据。若数据清理过程发生异常（如数据库写入锁冲突、正在重建等），配置已被永久抹除，残留孤立向量与分块数据，且用户无法在 UI 再次操作清理；
- **缺乏统一异常捕获与日志**：未包含 `try-catch` 块，遇到未处理异常时直接对外抛出 500。

### 原因四：前端错误处理被完全吞噬，提示信息过于宽泛

在 `Business/ReferenceRAG.Business/wwwroot/app/views/Sources.js` 中：
```javascript
try {
    await sourcesApi.delete(source.name, deleteData);
    message.success(...);
} catch {
    message.error('删除失败'); // <--- 吞噬后端返回的 error 详情
}
```
后端返回的 `{ error: "源 '%E5%B0%8F%E8%AF%B4' 不存在" }` 被忽略，用户只能看到生硬的“删除失败”，无法定位问题。

---

## 3. 解决方案与优化设计

### 3.1 核心修复：InProcessServer 遵循 ASP.NET Core 规范解编码

在 `InProcessServer.DispatchAsync` 中，严格将 `parts[0]` 通过 `PathString.FromUriComponent` 转换为标准解码后的 `Path`，同时保留 `RawTarget` 为原始 URI 字符串，保证与 Kestrel 表现完全一致。

### 3.2 宿主与控制器层优化：健壮源查找与删除工作流

1. **统一源定位方法 `FindSource`**：
   在 `SourcesController` 中提供统一的源检索逻辑，支持原始名称匹配、URI 解码名称匹配，以及大小写不敏感匹配（`OrdinalIgnoreCase`），覆盖 `Delete`、`Get`、`GetFiles`、`Update`、`Toggle`、`Scan` 等所有依赖 `{name}` 的端点。
2. **重构 `Delete` 流程与错误处理**：
   - 先执行 `_pipeline.DeleteSourceAsync(source.Name)` 清理底层索引数据；
   - 清理成功后再从 `_configManager` 移除配置及停用监控；
   - 增加详尽的审计日志与规范的 `try-catch` 错误响应。
3. **同步加固 `IndexController.RebuildSource`**：
   对单独源重建端点同样加入 URI 解码与大小写兼容处理。

### 3.3 前端 API 与视图层优化

1. **前端 API 路由参数标准化**：
   在 `app/api/index.js` 中，对 `sourcesApi` 和 `indexApi` 中所有作为 URL 路径片段的动态参数（如 `name`、`modelName`）包裹 `encodeURIComponent`，防止空格、特殊符号与非 ASCII 字符引发路由错乱。
2. **友好具体的错误反馈**：
   在 `Sources.js` 的 `handleDelete` 及相关操作中，优先展示 `error.response?.data?.error || error.message`，提供清晰透明的用户反馈。

### 3.4 默认支持 .txt 纯文本文档

针对用户管理源（如小说、日常日志等纯文本）的需求，系统在以下各层将默认文件匹配规则扩展为 `["*.md", "*.txt"]`：
1. **配置实体层**（`ObsidianRagConfig.SourceFolder.FilePatterns`）：默认值从 `new() { "*.md" }` 扩展为 `new() { "*.md", "*.txt" }`；
2. **控制器与配置管理器**（`SourcesController.Add`、`Update` 与 `ConfigManager.AddSource`）：源创建或空规则回退时，自动装配 `["*.md", "*.txt"]`；
3. **文件监控与扫描**：底层管道天然支持纯文本分块与解析（`FileIndexPipeline` 与 `MarkdownChunker` 完整兼容无格式纯文本），扩展后直接参与增量监听与扫描索引；
4. **前端交互层**：编辑规则占位与默认说明更新为 `*.md, *.txt`。

---

## 4. 验证计划

1. **单元测试验证**：
   - 在 `ReferenceRAG.Host.Tests/TransportTests.cs` 中增加测试用例，验证 `InProcessServer` 在处理中文 URL 编码（如 `%E5%B0%8F%E8%AF%B4`）时，能正确解析路由并传递已解码的中文参数给 Controller/Minimal API。
   - 验证 `SourcesController` 删除中文源的场景（包括保留数据与清空数据两种模式）。
2. **回归测试验证**：
   - 运行全量单元测试与集成测试，确保未破坏任何现有功能。
