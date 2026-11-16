# McpHelper SDK 协议适配层缺陷调研与优化方案

> 对象：`E:\WorkProject\CSharp\PrivateProject\SDK\McpHelper`（`McpHelper.Core` **2.0.1**，git `dc05ab9`，独立仓库 `git@github.com:csvkse/McpHelper.git`）
> 消费方：ReferenceRAG（当前生产 `referencerag:deploy-fe58970`，引用 `McpHelper.Core 1.0.9`）

**结论先行**：SDK 已经引进了官方 `ModelContextProtocol.AspNetCore 1.2.0`，却用一层手写桥把它的 **schema 生成、工具注解、结构化结果** 三项能力全部绕开了。消费方为此写了两个补偿件（`McpInputSchemaEnricher`、`McpResult`）。本轮调研确认根因在 SDK —— 修 SDK 可以**同时删掉这两个补偿件**，并把 `isError` / `annotations` 一次性做对。

已定位 **1 个安全级 + 3 个功能级 + 6 个契约/运维级** 缺陷，其中 4 个已用隔离探针（`/tmp/mcpprobe`，McpHelper 1.0.9 / 2.0.1 双版本）复现。

---

## 1. 问题与证据

### 1.1 架构现状：官方 SDK 在手，却走手写桥

`src/McpHelper.Core/Extensions/ServiceCollectionExtensions.cs` 的注册链路：

```
services.AddMcpServer(...)          // 官方 SDK
  .WithHttpTransport(...)           // 官方传输
  .WithListToolsHandler(...)        // ← 手写：从 IMcpToolRegistry 抄一遍  (L404)
  .WithCallToolHandler(...)         // ← 手写：自己转 CallToolResult     (L439)
```

官方自带的 `WithTools<T>()`（`ModelContextProtocol.dll` 的 `McpServerBuilderExtensions` 有 4 个重载 + `WithToolsFromAssembly`）**完全没有被调用**。因此官方那套「反射生成 schema、`Tool.Annotations`、`CallToolResult.StructuredContent`、框架参数注入」一个都没享受到。

### 1.2 P0-1 本地工具从不生成 `inputSchema`（功能级，根因）

`McpTools/McpToolRegistry.cs:380` 构造描述符时根本没有 schema 生成：

```csharp
var descriptor = new McpToolDescriptor
{
    Name = toolName,
    OriginalName = toolName,
    Description = description,
    ProviderName = Name,
    RequiredScope = requiredScope      // InputSchema 从未赋值
};
```

`McpTools/McpToolDescriptor.cs` 虽然声明了 `InputSchema` 属性，`LocalToolProvider` 从不填。于是 `WithListToolsHandler`（`ServiceCollectionExtensions.cs:433`）只能走兜底：

```csharp
InputSchema = ConvertToJsonElement(t.InputSchema)
              ?? JsonSerializer.SerializeToElement(new { type = "object", properties = new { } })
```

**探针实测**（3 个带参数工具）：

```json
{"name":"needs_services","inputSchema":{"type":"object","properties":{}}}
```

这是**合法的空 schema** 而非 `null` —— 所以「看起来合规」却「实际无参」，客户端据此会认为工具不接受任何参数。这正是 ReferenceRAG 在生产上撞到的那个问题，只是当时只在消费方补了 `McpInputSchemaEnricher`。

### 1.3 P0-2 `AllowUpstreamProxy` 把本地工具一起关掉（安全/可用性级）

`Models/ProgramInfoOptions.cs:157-161` 的文档写的是「是否允许**上游 MCP 服务代理调用**」，但 `McpToolRegistry.cs:94-98` 与 `:147-149` 的实际行为是：**非核心工具一律不返回、不可调用**，本地注册的工具首当其冲。

**探针实测**（`AllowUpstreamProxy=false`）：

```
tools/list → {"result":{"tools":[]}}
tools/call → {"content":[{"text":"上游代理调用已禁用"}],"isError":true}
```

而该配置项的默认值是 `true`，所以平时看不出来；一旦有人按字面意思关掉「上游代理」，整个 MCP 服务会静默变空。这是**文档语义与实现语义不一致**导致的配置级故障。

### 1.4 P0-3 结果信封转换丢弃结构化内容并吞掉同名键

`ServiceCollectionExtensions.cs:553-619` 的 `ConvertToCallToolResult`：

```csharp
if (result is JsonObject obj && obj.TryGetPropertyValue("content", out var contentNode))
{
    if (contentNode is JsonArray contentArray) { ... 逐个 item 转 TextContentBlock ... }
}
else
{
    // 没有标准格式，直接作为文本返回
    contentBlocks.Add(new TextContentBlock { Text = result.ToString() });
}
```

两个后果（均已探针复现）：

| 探针工具返回 | 实际 MCP 结果 | 说明 |
| :--- | :--- | :--- |
| `{"content":[{"type":"text","text":"body"}],"structuredContent":{"a":1}}` | `{"content":[{"type":"text","text":"body"}]}` | **`structuredContent` 被静默丢弃** |
| `{"success":true,"content":"BODY"}` | `{"content":[]}` | 工具自己的顶层 `content` 被当成协议信封解析，字符串进 `JsonArray` 分支不匹配 → 空数组 |

第二条正是 ReferenceRAG `read_note_content` 完全不可用的原因。消费方已用 `McpResult` 把自己的字段改名并加 `Sanitize` 兜底，但**任何 SDK 使用者都可能踩**，属于 SDK 的契约责任。

### 1.5 P1-1 框架注入参数不被识别（功能级）

`McpToolRegistry.cs` 的 `LocalToolProvider.InvokeToolAsync`（`L486`）只对 `CancellationToken` 做了特殊处理；其余参数一律要求从 `arguments` 取值，否则计入「缺少必需参数」。

**探针实测**：

```
tools/call needs_services → {"content":[{"text":"缺少必需参数: sp"}],"isError":true}
```

`IServiceProvider`、`McpServer`、`HttpContext` 这类框架参数本应由 DI 注入（官方 `McpServerTool.Create(MethodInfo, Object, ...)` 就是这么做的），SDK 却要求客户端传 `sp`。

### 1.6 P1-2 注解从未映射（契约级）

`McpTools/McpToolDescriptor.cs` 只有 `Name/Description/InputSchema/ProviderName/OriginalName/RequiredScope/Metadata` —— **没有 Title / ReadOnly / Idempotent / Destructive** 字段；`DiscoverTools`（`L333`）只读 `[Description]` 与 `[RequireApiKeyScope]`。

**探针实测**：

```json
{"name":"annotated","description":"readonly annotation probe","inputSchema":{"type":"object","properties":{}}}
```

`[McpServerTool(ReadOnly = true, Idempotent = true)]` 完全不生效。而官方 `Tool` 类型在 SDK 1.2.0 里**已经有** `Annotations`（`ToolAnnotations`：`Title / ReadOnlyHint / IdempotentHint / DestructiveHint / OpenWorldHint`），只是 SDK 没填。

现实影响：客户端无法自动放行只读工具、无法对危险操作弹确认——按业界实测，`readOnlyHint:true` 是客户端「免确认直放」的依据。

### 1.7 P1-3 蛇形命名有三套实现，工具名可能漂移

| 实现 | 位置 | `GetIPAddress` → | `LXTool` → |
| :--- | :--- | :--- | :--- |
| SDK 自研 `ToSnakeCase` | `McpToolRegistry.cs:426` | `get_ipaddress` | `lxtool` |
| 消费方 `PascalToSnakeRegex` | `McpInputSchemaEnricher.cs` | `get_i_p_address` | `l_x_tool` |
| 官方 `JsonNamingPolicy.SnakeCaseLower` | BCL | `get_ip_address` | `lx_tool` |

三者对常见名字（`SemanticSearch`、`KnowledgeBaseDrillDown`）一致，但对「连续大写缩写」会分叉。ReferenceRAG 的 `McpInputSchemaGateTests.ToSnakeCase_MatchesMcpHelperNaming` 正是为了让两者的协议名对齐而写的**对拍测试**——本质是在给 SDK 的命名实现做兼容层。

### 1.8 P2 其余六项

| # | 缺陷 | 位置 | 后果 |
| :--- | :--- | :--- | :--- |
| 1 | 参数类型转换不完整，异常原文外泄 | `McpToolRegistry.cs:588` `ConvertJsonNodeToType` | `topK:"abc"` → 模型看到 `An element of type 'String' cannot be converted to 'System.Nullable\`1[System.Int32]'` |
| 2 | 每次 `tools/list` 重新做 `JsonObject→string→JsonElement` | `ServiceCollectionExtensions.cs:433` | 25 工具实测约 1ms/请求；工具数上到 100+ 才显著，但可零成本消除 |
| 3 | 两套序列化路径并存 | `McpToolDescriptor.ToMcpToolFormat`（仅 `DynamicMcpToolHandler` 用）vs `WithListToolsHandler` | 一处加字段另一处忘加（本次注解缺失即此类） |
| 4 | `DynamicMcpToolHandler` 几乎死代码 | 仅 `GetService<ILogger<DynamicMcpToolHandler>>()` 用到 | 干扰阅读与维护 |
| 5 | `GetRequiredService<IServiceCollection>()` | `McpToolRegistryExtensions.cs:390` | 构建后的 provider 默认不注册 `IServiceCollection`；该路径抛异常后被 `catch` 吞掉 → 配置里的 provider 被静默跳过 |
| 6 | `ToolTypeCollectingRegistry` 是假注册表 | `McpToolRegistryExtensions.cs`（私有类，所有方法返回空） | 违反 LSP：拿到它的人调用任何方法都静默无效，只为收集类型而存在 |

### 1.9 版本偏斜（运维级）

消费方 `Directory.Packages.props` 里同时有：

```
McpHelper.Core               1.0.9      ← SDK 依赖 AspNetCore [1.2.0]（精确版本）
ModelContextProtocol         2.2.0      ← 消费方直引，覆盖了传递依赖
```

运行期实际解析（`ReferenceRAG.ApiHost.deps.json`）：

```
ModelContextProtocol           2.2.0
ModelContextProtocol.Core      2.2.0
ModelContextProtocol.AspNetCore 1.2.0   ← 与 Core 2.2.0 不是同一代
```

`ModelContextProtocol.AspNetCore 1.2.0` 的 nuspec 要求 `ModelContextProtocol [1.2.0]`（区间精确），NuGet 因消费方直引 2.2.0 而选中 2.2.0——这是**官方未支持的版本组合**。目前功能正常，但它解释了为什么升级官方 SDK 端口时会遇到行为差异，也让「到底跑的是哪一代」变得不可读。

---

## 2. 业界对照

| 议题 | 业界做法 | 本方案采纳 |
| :--- | :--- | :--- |
| schema 来源 | 官方 C# SDK 由 `McpServerTool.Create(MethodInfo, ...)` 反射生成，**与协议实现同一份代码**；FastMCP / Python SDK 同理 | 直接用官方生成，删除自研反射层 |
| 结构化结果 | 规范区分 `content`（模型可见）与 `structuredContent`（应用消费，按 `outputSchema` 校验）；官方 `McpServerToolCreateOptions.UseStructuredContent` | 桥接时保留 `structuredContent` 与 `outputSchema` |
| 失败语义 | `CallToolResult.IsError=true` 表示工具执行失败；协议错误走 JSON-RPC error | 由 registry 产出**类型化结果**，桥只做映射 |
| 工具注解 | `readOnlyHint` / `destructiveHint` / `idempotentHint` / `openWorldHint`；只读工具可被客户端自动放行 | 从 `[McpServerTool]` 特性 + `RiskLevel` 派生 |
| 框架注入 | 官方 `McpServerTool.Create` 区分「从 arguments 取」与「从 DI 解析」 | 用 `IsFrameworkInjected` 白名单 |
| 传输 | C# SDK 2.0 起 **HTTP 默认无状态**（单次自描述 POST），2.2 提供混合迁移；`HttpServerTransportOptions` 有 `Stateless`/`EnableLegacySse`/`IdleTimeout` | 升级到 2.2.0 并显式声明 `Stateless` |
| 命名 | 官方用 `JsonNamingPolicy.SnakeCaseLower` | 统一用 BCL 实现，删除自研 |
| 版本组合 | 官方 SDK 同一代 Core/AspNetCore 必须同版本 | 三包锁定同代（2.2.0） |

来源：
- [Announcing v2.0 of the official MCP C# SDK](https://devblogs.microsoft.com/dotnet/announcing-v20-of-the-official-mcp-csharp-sdk/)
- [MCP C# SDK 2.2.0: Stateless HTTP and Hybrid Migration Guide](https://essamamdani.com/blog/mcp-csharp-sdk-2-2-stateless-http-guide-2026)
- [MCP Goes Stateless: 2026-07-28 Migration Guide](https://www.developersdigest.tech/blog/mcp-stateless-migration-guide-2026)
- [MCP Specification — Server / Tools](https://modelcontextprotocol.io/specification/2025-06-18/server/tools)
- [MCP C# SDK — structured content 与 outputSchema](https://devblogs.microsoft.com/dotnet/mcp-csharp-sdk-2025-06-18-update/)
- [Tool annotations 作为风险词汇](https://blog.modelcontextprotocol.io/posts/2026-03-16-tool-annotations/)
- [Testing MCP Tool Annotations：readOnlyHint 与客户端自动放行](https://sunpeak.ai/blogs/testing-mcp-tool-annotations/)
- [MCP tool annotations：readOnlyHint 在 ChatGPT / VS Code / Copilot 的行为](https://www.sume.com/guides/mcp-tool-annotations-readonlyhint)

---

## 3. 可复用基础设施核查

| 需求 | 现成件 | 位置 | 结论 |
| :--- | :--- | :--- | :--- |
| schema 反射生成 | 官方 `McpServerTool.Create(MethodInfo, ...)` + `AIJsonSchemaCreateOptions` | `ModelContextProtocol`（**已在 SDK 依赖图里**） | 直接用，零新增 |
| 注解透出 | 官方 `Tool.Annotations` / `ToolAnnotations` | `ModelContextProtocol.Core 1.2.0` 已含 | 直接用 |
| 结构化结果 | 官方 `CallToolResult.StructuredContent` / `Tool.OutputSchema` | 同上 | 直接用 |
| 蛇形命名 | BCL `JsonNamingPolicy.SnakeCaseLower` | .NET 8+ | 直接用 |
| 版本对齐 | `ModelContextProtocol.AspNetCore 2.2.0` 已在本机缓存 | `~/.nuget/packages` | 升级零下载 |
| 测试基座 | `McpHelper.Core.Tests`（xunit.v3 + FluentAssertions + `WebApplicationFactory`，**236 例全通**） | `tests/McpHelper.Core.Tests` | 直接扩展 |
| 消费方补偿件 | `McpInputSchemaEnricher`、`McpResult` | ReferenceRAG `Host/.../McpTools` | **修好 SDK 后可删** |
| 工具风险字典 | `McpCatalogRegistry.RiskLevel`（Read/Execute/Dangerous） | ReferenceRAG | 注解数据源，无需重新维护 |

**净效果**：SDK 修复零新增依赖；消费方净减少约 250 行补偿代码 + 一条项目内部对拍门禁。

---

## 4. 方案（按风险排序）

### 4.1 P0（必须，且顺序有约束）

**S1 用官方 `WithTools` 替换手写桥（根治 schema/注解/结构化）**

```
foreach (var tool in registry.GetAllToolDescriptors())   // 描述符带上不再丢弃的信息
    mcpBuilder.WithTools(McpServerTool.Create(methodInfo, instance, options))
```

- `options.UseStructuredContent = true`（工具返回可序列化对象时）
- `options.ReadOnly / Idempotent / Destructive` 由 `[McpServerTool]` + `RiskLevel` 派生
- 保留 `IMcpToolFilter`（授权）与 `ILazyBackendManager`（后端代理）语义：它们作用于 `GetAllTools()`，与官方注册互不冲突

风险：后端代理工具（`DynamicBackendToolProvider`）返回的 schema 已经是 `JsonObject`，需要走 `McpServerTool.Create` 的 `InputSchema` 直通分支或保留现有 handler。**建议分两步**：先让本地工具走官方路径，后端代理继续走现有 handler（`WithListToolsHandler`/`WithCallToolHandler` 可与官方按 tool 名共存路由）。

**S2 修正 `AllowUpstreamProxy` 语义（安全级）**

现状：`AllowUpstreamProxy=false` 会让本地工具一起消失。两种正解，必须选一个并同步改文档：

| 选项 | 行为 | 适用 |
| :--- | :--- | :--- |
| A（推荐） | 只对**来自后端代理**的工具生效（按 `ProviderName` 前缀或 `Metadata["IsBackend"]` 判定），本地工具始终可见 | 与配置项名字一致 |
| B | 拆成两个开关：`AllowUpstreamProxy`（代理发现）+ `ExposeLocalTools`（本地暴露） | 需要更细的运维控制 |

门禁：`AllowUpstreamProxy=false` 时本地工具仍出现在 `tools/list` 且可调用；`DynamicBackendToolProvider` 的工具消失。

**S3 结果桥不得吞键（功能级）**

`ConvertToCallToolResult` 改为：

- 识别工具返回**已经是协议信封**（`content` 是数组 且/或 含 `isError`）→ 直通；
- 否则一律当作业务负载 → `TextContentBlock(businessJson)`，**不因为存在 `content` 键就改写语义**；
- 显式保留 `structuredContent` / `_meta`。

更彻底的做法：registry 返回**类型化结果**（`McpToolResult.Success(payload)` / `.Failure(error, code)`），桥只做映射，从根上消灭「字符串里藏信封」这类歧义。

**S4 框架参数注入**

`LocalToolProvider.InvokeToolAsync` 增加白名单：`CancellationToken` / `IServiceProvider` / `McpServer` / `HttpContext` / `IProgress<>` 从 DI 或上下文解析，不要求客户端传。官方 `McpServerTool.Create` 已实现该语义，S1 完成后此项自然消失。

### 4.2 P1（同批）

**S5 注解映射**：`McpToolDescriptor` 增 `Title / ReadOnly / Idempotent / Destructive / OpenWorld`；`DiscoverTools` 从 `[McpServerTool]` 读；若为空再查 `RiskLevel` 映射（`Read→ReadOnly=true`，`Execute→Destructive=false + Idempotent=true`，`Dangerous→Destructive=true`）。S1 后改由官方 `Tool.Annotations` 输出。

**S6 命名统一**：删除 `McpToolRegistry.ToSnakeCase`，改用 `JsonNamingPolicy.SnakeCaseLower.ConvertName`；消费方 `McpInputSchemaEnricher.ToSnakeCase` 同步删除，对拍测试改为「SDK 与 BCL 一致」。

**S7 参数转换收敛**：`ConvertJsonNodeToType` 改为 `JsonSerializer.Deserialize(node, targetType)` 统一路径（保留 `string` 的 `node.ToString()` 特例），异常包成 `errorCode: "invalid_argument"` + 只回显字段名与期望类型，不外泄内部异常文本。

### 4.3 P2（顺手清理）

| 项 | 做法 |
| :--- | :--- |
| 两套序列化路径 | 删除 `McpToolDescriptor.ToMcpToolFormat()`，只保留一处（S1 后即官方路径） |
| `DynamicMcpToolHandler` | 若确认仅日志用到，删除或改为 `internal` |
| `GetRequiredService<IServiceCollection>` | 改为在注册期（`AddMcpToolRegistry` 时）即把 provider 类型加入 `services`，运行期不再解析 |
| `ToolTypeCollectingRegistry` | 改为 `ToolTypeCollector`（纯数据收集器，不实现 `IMcpToolRegistry`），消除 LSP 违规 |
| 版本偏斜 | `McpHelper.Core` 依赖升级到 `ModelContextProtocol.AspNetCore 2.2.0`；消费方把三包锁同代（见 §5） |
| schema 往返 | S1 后官方直接持有 `JsonElement`，`ConvertToJsonElement` 删除 |

### 4.4 设计模式落点

| 模式 | 落点 | 解决什么 |
| :--- | :--- | :--- |
| **Adapter** | `McpToolBridge`（新）：`IMcpToolProvider` → 官方 `McpServerTool` | 手写桥与官方 SDK 之间的唯一转换点，schema/注解/结构化一次映射到位 |
| **Strategy** | `IMcpToolResultWriter`：`EnvelopeResultWriter`（已是信封）/ `BusinessPayloadWriter`（业务 JSON）/ `StructuredResultWriter` | 消灭 `ConvertToCallToolResult` 里的 if-else 分支判定 |
| **State Machine** | `McpRegistrationState`：`Declared → Discovered → Filtered → Exposed` | 每个工具的可见性判定（上游开关、ToolMode 白名单、Scope 过滤）按状态推进，避免四处 `continue` |
| **Registry** | 复用 `IMcpToolRegistry` + `McpCatalogRegistry` | 工具清单与权限/风险字典保持单一事实源 |
| **Facade** | `AddAppMcpHelper` 保留 | 保持现有 API 不变，消费方无需改注册代码 |

**明确不引入**：不另建 MCP 服务器框架（官方 SDK 已在）；不做工具动态热插拔网关（当前 25 工具没有这个量级需求）。

---

## 5. 迁移与兼容（无历史负担前提）

ReferenceRAG 未正式发布，因此：

1. `McpHelper.Core` 升到 **2.1.0**（2.0.1 + 本次修复），依赖 `ModelContextProtocol.AspNetCore 2.2.0`。
2. 消费方 `Directory.Packages.props`：`McpHelper.Core 2.1.0`、`ModelContextProtocol 2.2.0`、新增 `ModelContextProtocol.AspNetCore 2.2.0`（**三包同代**），消除 §1.9 的版本偏斜。
3. 消费方删除 `McpInputSchemaEnricher.cs`；`McpResult.Sanitize` 删除，保留 `McpResult.Ok/Fail` 作为语义糖（内部改为返回类型化结果）。
4. 消费方工具源码**不改**：`[McpServerTool]` + `[Description]` 保持不变，SDK 自行反射。
5. 门禁调整：`McpInputSchemaGateTests` 删除「对拍 SDK 命名」用例；新增「每个工具 schema properties 非空」「注解存在且与 RiskLevel 一致」。

---

## 6. 门禁与回归测试（落在 SDK 自己的测试工程）

| 门禁 | 位置 | 锁定不变量 |
| :--- | :--- | :--- |
| G-SDK-1 schema 非空 | `tests/.../Mcp/LocalToolSchemaTests.cs` | 带参数工具的 `inputSchema.properties` 非空；`required` 与「无默认值参数」一一对应 |
| G-SDK-2 无参工具 | 同上 | 无参工具 `additionalProperties:false`（规范推荐形态） |
| G-SDK-3 注解 | `tests/.../Mcp/ToolAnnotationTests.cs` | `[McpServerTool(ReadOnly=true)]` → `annotations.readOnlyHint=true` |
| G-SDK-4 结构化结果 | `tests/.../Mcp/CallToolResultEnvelopeTests.cs` | `structuredContent` 原样透出；业务 JSON 含 `content` 键时不得被吞 |
| G-SDK-5 失败语义 | 同上 | 业务失败 → `isError:true`；协议错误 → JSON-RPC error |
| G-SDK-6 框架参数 | `tests/.../Mcp/FrameworkInjectionTests.cs` | `IServiceProvider` / `HttpContext` / `CancellationToken` 参数不被要求由客户端传 |
| G-SDK-7 上游开关语义 | `tests/.../Mcp/UpstreamProxyGateTests.cs` | `AllowUpstreamProxy=false` 只隐藏后端工具，本地工具仍可见可调用 |
| G-SDK-8 命名一致 | `tests/.../Mcp/ToolNameDerivationTests.cs` | SDK 派生名 == `JsonNamingPolicy.SnakeCaseLower`（含 `GetIPAddress` 这类缩写） |
| G-SDK-9 版本对齐 | CI 脚本 | SDK 的 `ModelContextProtocol.AspNetCore` 版本 == 消费方 `ModelContextProtocol` 版本 |

---

## 7. 优先级与风险

| # | 项 | 等级 | 影响 | 建议 |
| :-- | :--- | :--- | :--- | :--- |
| S2 | `AllowUpstreamProxy` 语义 | **P0** | 一处配置即可让 MCP 静默变空 | 先修（改动最小） |
| S3 | 结果桥吞键 | **P0** | 任何使用者都可能丢结果 | 与 S1 同批 |
| S1 | 官方 `WithTools` 桥接 | **P0** | 根治 schema/注解/结构化 | 分两步（本地工具优先） |
| S4 | 框架参数注入 | P1 | 客户端被迫传 `sp` 这类脏参数 | S1 后自然消失 |
| S5 | 注解 | P1 | 客户端无法安全放行只读工具 | 同批 |
| S6 | 命名统一 | P1 | 缩写场景工具名漂移 | 同批 |
| S7 | 参数转换 | P2 | 异常原文外泄 | 次批 |
| P2 其余 | 死代码/简化 | P2 | 维护成本 | 顺手 |

**不做的事**：不改传输层（除版本对齐）；不为后端代理另建并行注册体系；不引入新包。
