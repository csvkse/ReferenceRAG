# MCP 工具注册、传输协商与参数语义：可行性与优化方案调研

> 对象：SDK `SDK/McpHelper`（`McpHelper.Core` **2.1.0**，git `f7a09a7`）＋ 消费方 ReferenceRAG（生产 `referencerag:deploy-f940657`）
> 验证环境：生产 MCP 端点（`http://<生产主机>:5123/api/mcp`）、隔离探针 `ModelContextProtocol.AspNetCore 2.2.0`（`/tmp/mcpspike`）
> 日期：2026-11-16

## 结论先行

| 议题 | 结论 | 处置 |
| :--- | :--- | :--- |
| ① 不切官方 `WithTools` 静态注册 | 上一轮「无法直接映射」的说法**不准确**。`ToolCollection` 支持运行期增删，`CallToolHandler` 自动回落承接集合外工具，官方 filter 同时包住集合工具与 handler —— 技术上完全可切。但切换**净收益接近零、局部还会倒退**。真正的架构债不在注册方式，而在**可见性判定有三份、调用路径手写**。 | 保持注册方式；补 3 项低风险收口（§2.1.4）；把切换条件写成触发式清单（§2.1.5）。 |
| ② `Accept` 单值 406 | **可在应用层彻底消除**，不必等 SDK。406 是官方 handler 的**无条件前置校验**（实测 2026-07-28 带全头仍 406），SDK 未暴露「仅 JSON 响应」开关。 | 采用**方案 B′**：端点前置 Accept 归一化 + 单帧 SSE 转 JSON（§2.2.3），默认开启并带开关可回滚。 |
| ③ `list_files.sourceId` | 比原判断更严重：KB 分支是**未转义 LIKE**，生产实测 `sourceId="%"` 匹配全库、`"_"` 为单字符通配；旧索引分支因 `IKnowledgeBaseService` 恒注册而**不可达**，「双语义」实际是「只有一个错语义」。 | 按指定执行 **A 方案**（§2.3）：加 `connectorId`、`sourceId` 改名 `pathKeyword`、LIKE 加 `ESCAPE`、回显 `appliedFilters`、删除死分支。 |

设计模式结论：**Adapter 必需、Strategy 局部必需、StateMachine 复用而非新建**（`BackendConnectionStatus` 已是事实状态机，缺的是「状态到可见性」的单一投影）；`Composite` 已被 `McpCategoryCatalog` 使用，无需扩展。

---

## 1. 问题与证据

### 1.1 现状链路

```
services.AddMcpServer(...)                     // 官方 SDK 2.2.0
  .WithListToolsHandler(...)                   // 手写：从 IMcpToolRegistry 抄描述符   L404
  .WithCallToolHandler(...)                    // 手写：路由 + 结果桥                 L442
  .WithHttpTransport(h => h.Stateless = ...)   // L392
```

`WithTools*` 五个重载（`ModelContextProtocol.dll` 的 `McpServerBuilderExtensions`）**从未被调用**。上一轮已把 schema 生成、注解、结构化结果三项能力补进桥里（`McpToolSchemaBuilder` / `BuildAnnotations` / `McpToolResultWriters`），所以现在协议面上与官方等价，唯一缺口是 `outputSchema`（本项目所有工具都未声明 `UseStructuredContent`）。

### 1.2 406 的真实来源（不在 McpHelper 里）

`grep -rn "must accept" SDK/src` 零命中。字符串来自 `ModelContextProtocol.AspNetCore`，反编译对照官方仓库 `StreamableHttpHandler.cs:70-80`：

```csharp
// The Streamable HTTP spec mandates the client MUST accept both application/json and text/event-stream.
// ASP.NET Core Minimal APIs mostly try to stay out of the business of response content negotiation,
// so we have to do this manually. The spec doesn't mandate that servers MUST reject these requests,
// but it's probably good to at least start out trying to be strict.
var typedHeaders = context.Request.GetTypedHeaders();
if (!typedHeaders.Accept.Any(MatchesApplicationJsonMediaType)
 || !typedHeaders.Accept.Any(MatchesTextEventStreamMediaType))
{
    await WriteJsonRpcErrorAsync(context,
        "Not Acceptable: Client must accept both application/json and text/event-stream",
        StatusCodes.Status406NotAcceptable);
    return;
}
```

三个实测事实：

1. **v1.0.0 / v1.2.0 / v2.2.0 / main 分支都有同一段**，升级 SDK 不解决。
2. **对协议版本无豁免**：探针在带齐 `MCP-Protocol-Version: 2026-07-28` + `Mcp-Method` + 合法 `_meta` 的情况下，单值 `Accept` 依旧 406（双值才 200）。
3. **`*/*` 能过**：生产实测 `Accept: */*` → 200 `text/event-stream`。

规范侧（2025-06-18 与 2026-07-28 措辞一致）：

> The client **MUST** include an `Accept` header listing both `application/json` and `text/event-stream` as supported content types.
> If the body is a JSON-RPC *request*, the server **MUST** return either `Content-Type: application/json` or `Content-Type: text/event-stream`. The client **MUST** support both.

即 **MUST 全在客户端，规范没有要求服务端 406**。406 是 SDK 的保守实现选择。

### 1.3 list_files 的四层缺陷（生产实测）

| 层 | 事实 | 证据 |
| :--- | :--- | :--- |
| 描述与行为不符 | 参数说明写「旧索引的**数据源名称**」，KB 分支却拿到路径子串关键词 | `IndexStatusTools.cs:229` / `:254` |
| 同名参数两种语义 | KB 分支 = 模糊关键词；旧分支 = 精确源名相等 | `:254` vs `:290-292` |
| 旧分支在生产不可达 | `IKnowledgeBaseService` 恒注册 → `GetService` 恒非 null → 永远进 KB 分支 | `KnowledgeBaseBusinessExtensions.cs:12` |
| LIKE 未转义 | `%{search}%` 直拼，`%` 匹配全库、`_` 单字符通配 | `SqliteKnowledgeBaseStore.cs:2899` |

生产实测（`limit=20`）：

- `sourceId="ZZZNOTEXIST"` → `success:true, count:0`（无法区分「过滤生效但没命中」与「参数没生效」）
- `sourceId="_"`、`sourceId="%"` → `success:true, count:20`（**过滤器退化为无过滤**）
- 延迟：plain 47–72ms、`%` 49–84ms、`.md` 40–67ms（未转义全表走法没有明显劣化，因为 `source_key` 无索引，LIKE 本就全扫）

---

## 2. 具体方案

### 2.1 议题①：不切 `WithTools` 的架构判断与低风险收口

#### 2.1.1 官方真实能力（探针实测，非文档推测）

| 能力 | 实测结果 | 探针证据 |
| :--- | :--- | :--- |
| `McpServerOptions.ToolCollection` 运行期增删 | `TryAdd` 后 `tools/list` 立即出现新工具；`Remove` 后立即消失 | count 1 → 2 → 1 |
| 集合外工具回落 `CallToolHandler` | `proxy_tool` 调用命中 handler 返回 `fallback-handler:proxy_tool` | 是 |
| 集合工具优先于 handler | 同名 `echo` 走集合，不走 handler | 是 |
| `ListToolsHandler` 输出与集合合并 | 同一个 `tools/list` 同时出现 `handler_only_tool`（handler）与 `echo`（集合） | 是 |
| 官方 filter 包住集合工具 | `CallToolFilters` 在调用集合工具 `echo` 时也触发 | `[FILTER] call echo` |
| 反射注册等价 `WithTools` | `McpServerTool.Create(MethodInfo, instance, opts)` 自动生成 schema / outputSchema / annotations | `adv_echo` / `adv_obj` |
| 集合变更自动发 `list_changed` | 仅 `HasStatefulTransport` 时订阅 `Changed`（`_sessionTransport is not StreamableHttpTransport { Stateless: true }`）；无状态模式下**不发** | `McpServerImpl.cs:113-133` |

#### 2.1.2 所以「不能映射」为什么错

三件事都能映射：

1. 本地工具 → `ToolCollection`（反射 `McpServerTool.Create` 或 `WithTools`）
2. 代理工具（`DynamicBackendToolProvider` / `StdioToolProvider`）→ 继续走 `CallToolHandler` 回落（官方文档明确：`CallTool` 先在集合里查，查不到才用 handler）
3. 延迟后端占位符 → `ToolCollection` 运行期替换 + `Changed` 事件

上一轮判断过头的根因是**把「占位符需要运行期替换」当成了「集合不能运行期替换」**。

#### 2.1.3 但切换会倒退的三点（实测）

| 项 | 现在 | 切到官方后 |
| :--- | :--- | :--- |
| 参数校验可读性 | `参数 'topK' 类型无效，期望 integer` + `structuredContent.errorCode=invalid_argument` | **`An error occurred invoking 'adv_echo'.`** —— 缺参、类型错、业务异常三种情况文本完全相同，且无 errorCode |
| 占位符 `list_changed` | 无状态模式下本来就发不出（SDK 只记日志） | 同样发不出，官方只是不再假装 |
| 工具可见性过滤 | `IMcpToolFilter` + `GetAllTools` + `InvokeToolAsync` 三处各自判定 | 需重写进 `ListToolsFilters` / `CallToolFilters`（好在 filter 天然包住两侧，反而能收敛为一处） |
| 权限矩阵 | 25 工具 × API Key scope 已验收 | 全部重跑 |

第二点（错误文本）是**实质倒退**：官方 `CreateToolCallErrorResult`（`McpServerImpl.cs:1763`）对非 `McpException` 一律输出固定文案，不区分缺参/类型错。当前 SDK 的 `McpToolArgumentException` 反而更好。

#### 2.1.4 不迁移也能做的三项收口（推荐立即执行）

1. **可见性判定收敛为一处（Adapter/单一投影）**
   现状 `McpToolRegistry` 里 `GetAllTools`（LIST）与 `InvokeToolAsync`（CALL）各自调用 `IsToolEnabled` / `IsToolExposeUpstream` / `IsBackendOrigin`，共 11 处引用。一旦两处判定漂移，就会出现「列表可见但调用被拒」或反之（`AllowUpstreamProxy` 的历史 P0 正是这类）。
   收口：抽出 `IToolVisibilityPolicy`（`CanList(descriptor)` / `CanInvoke(descriptor)` 共用同一个 `IsVisible`），`GetAllTools` 与 `InvokeToolAsync` 都只调它。

2. **补 `outputSchema`（可选但便宜）**
   给回显型工具（`get_index_status` / `list_sources` / `get_document_info`）加 `UseStructuredContent = true`，客户端可拿到强类型结果。SDK 侧需在 `McpToolDescriptor` 增一个布尔并透传到 `Tool.OutputSchema`（现 `Tool` 构造缺这个字段）。

3. **补 `ttlMs` / `cacheScope`（仅对新协议客户端生效）**
   `ListToolsResult` 有 `TimeToLive` / `CacheScope`，SDK 只在 2026-07-28+ 请求上盖默认值（探针实测 2025-06-18 线上不输出这两个字段）。当前 `list` 走 `WithListToolsHandler` → 不返回这两个字段 → 旧协议客户端不受影响；若要吃上缓存提示，只需在 handler 返回里填 `TimeToLive = 30s`、`CacheScope = Private`（因为工具集按 API Key scope 过滤，属私有不共享）。

#### 2.1.5 切换的触发式条件（写到 README 的「何时该切」）

满足任意一条即重做注册层：

- 需要官方 `outputSchema` 的自然 schema 形塑（`SupportsNaturalOutputSchemas`）或 `icons` / `meta`
- 需要 JSON 响应模式、会话（`Mcp-Session-Id`）、`subscriptions/listen`
- 代理后端数量达到「手工前缀 + 手工注册」难以维护的量级（如 > 10 个上游）
- 需要官方 `McpServerToolCreateOptions.Services` 级别的 DI 注入语义（当前 SDK 已有等价手写）

### 2.2 议题②：`Accept` 单值 406 的优化路径

#### 2.2.1 三个候选方案的真实代价

| 方案 | 做法 | 判定 |
| :--- | :--- | :--- |
| A 保持现状 | 文档要求双值 | 规范正确；严格单值客户端不可用（OpenAI Responses connector 实测因 406 变 424） |
| B 中间件补头（只改请求） | 把单值改写为双值 | **会把客户端违规换成服务端违规**：服务器可能返回 `text/event-stream` 给一个从未声明接受的客户端 |
| C 让服务器回 JSON | 单值时返回 JSON | SDK 无开关：`HttpServerTransportOptions` 只有 `SessionMode/Stateless/EnableLegacySse/EventStreamStore/IdleTimeout/MaxIdleSessionCount/PerSessionExecutionContext/TimeProvider`，无「强制 JSON 响应」 |

#### 2.2.2 关键事实：本服务的 SSE 只是 JSON 的外壳

生产实测每个 POST 恰好返回**一帧**：

```
event: message
data: {"result":{...},"id":1,"jsonrpc":"2.0"}
```

没有进度通知、没有多帧、没有服务端发起的请求。也就是说 `text/event-stream` 在这里仅承担「把 JSON 包成 data: 一行」的包装职责。**因此把它降级成 `application/json` 不丢任何语义**，反而是更诚实的协商结果。

#### 2.2.3 方案 B′：前置归一化 + 响应体回落为 JSON

```
[ApiKeyMiddleware] → [McpAcceptNegotiationMiddleware] → [MapMcp handler]
```

中间件逻辑（只对 `SseEndpoint`，且只在 POST）：

1. 读 `Accept`。若同时含 `application/json` 与 `text/event-stream` → 原样放行（严格客户端零影响）。
2. 若只含 `application/json`（或不含 `text/event-stream`）→ 设 `HttpContext.Items["Mcp.ForceJson"] = true`，并把 `Accept` 改写为双值，让官方 handler 通过校验。
3. 若 `Accept` 缺失/`*/*` → 同 2（`*/*` 语义上本就接受 JSON）。
4. 响应阶段：若 `Mcp.ForceJson` 且 `Content-Type` 为 `text/event-stream`，则**缓冲响应体、剥掉 `event:/data:` 外壳、以 `application/json` 输出单条 JSON**。

第 4 步的正确性依据：客户端只声明接受 `application/json`，我们回 JSON —— 合规；同时因为该客户端没声明接受 SSE，我们**不**给它回 SSE —— 也不再是「服务端违规」。

实现要点（避免踩坑）：

- 只做**单帧**场景：若响应体出现第二帧（未来引入 progress 通知），**保持 SSE 原样输出并去掉改写头**，退回 406 语义或按客户端真实 Accept 处理。这是唯一需要显式拒绝的边界。
- 缓冲必须设上限（如 8MB）与超时（如 60s），超限则保持 SSE 流式透传（避免大结果 OOM）。
- `Content-Type` 只在**首个字节写出前**可改；因此实现应包 `IHttpResponseBodyFeature` 或在 `Response.OnStarting` 之后接管 `Body`。
- 不改 GET（`GET /api/mcp` 在 2026-07-28 已是 405，旧协议才用 SSE 长连，那里必须保留双值要求）。
- `stateless=true` 下官方不保证 `text/event-stream` 帧序外的任何东西，所以剥壳是安全的。

#### 2.2.4 备选：等 SDK

官方 issue #349（`arabold/docs-mcp-server`）维护者曾提出同样的「归一化 Accept」补丁，最终结论是「下一个版本讲 2026-07-28，走该协议版本的请求不再检查 Accept」。C# SDK main 分支目前**仍在所有分支上检查**（`StreamableHttpHandler.HandlePostRequestAsync` 首段），说明该豁免尚未落地或仅针对特定路径。**因此不应把它当作短期方案。**

### 2.3 议题③：`list_files` A 方案

#### 2.3.1 目标签名

```csharp
public async Task<string> ListFiles(
    string? knowledgeBaseIds = null,   // JSON 数组，[,..]
    string? connectorId = null,       // 新增：结构化精确过滤
    string? pathKeyword = null,       // 由 sourceId 改名：source_key / locator 子串
    int limit = 20)
```

- `connectorId`：走 `GetDocumentsAsync(kb.Id, 1, limit, search: null, connectorId: connectorId)` → SQL `d.connector_id = @connectorId`（`SqliteKnowledgeBaseStore.cs:2897`，已有索引 `idx_docs_conn`）。
- `pathKeyword`：走 `search` 通道，但**必须转义**（见下）。
- 两者可同时给：`connectorId` 精确 + `pathKeyword` 模糊，交集。
- 未知 `connectorId` → `isError` + `connector_not_found`（不是 0 条 `success:true`）。

#### 2.3.2 LIKE 转义

现状：

```csharp
whereClauses.Add("(d.source_key LIKE @search OR d.locator LIKE @search)");
// 参数 = "%{search}%"
```

改为：

```csharp
whereClauses.Add("(d.source_key LIKE @search ESCAPE '\\' OR d.locator LIKE @search ESCAPE '\\')");
// 参数 = "%" + EscapeLikePattern(search) + "%"
```

仓库**已有**现成实现 `EscapeLikePattern`（`SqliteKnowledgeBaseStore.cs:2443`，同时转义 `\ % _`），只需把它从 `private` 提到可复用位置或在本方法内引用。注意 `PUT` 处已有 `ESCAPE '\'` 用法（`:2588-2589`），口径一致。

同一缺陷也存在于 `:3415`（chunk 文本 LIKE）与 `MafChatService.cs:1405`（按文件名检索），属同类问题，建议一并修但**分属不同 PR**，避免一次改动过大。

#### 2.3.3 删除死分支

`:285-300` 的旧索引分支（`IVectorStore.GetAllFilesAsync` + `f.Source.Equals(sourceId)`）在生产永远不可达（`IKnowledgeBaseService` 恒注册）。按项目「无历史兼容」原则直接删除，同时删掉 `sourceId` 参数与相关注释。

#### 2.3.4 响应形状（可判别）

```json
{
  "success": true,
  "appliedFilters": { "connectorId": "0b6059c5...", "pathKeyword": ".md" },
  "count": 20,
  "totalMatched": 148,
  "truncated": true,
  "files": [...]
}
```

- `totalMatched`：`GetDocumentsAsync` 已返回 `TotalCount`（现在被 `_` 丢弃，`IndexStatusTools.cs:254`），直接使用。
- `truncated`：`totalMatched > limit`。这样「过滤生效但没命中」= `count:0, totalMatched:0`；「参数没生效」不再可能（未知 connectorId 直接报错）。

#### 2.3.5 现有基础设施复用清单

| 需要 | 已有 | 位置 |
| :--- | :--- | :--- |
| 连接器唯一标识 | `list_sources` 已输出 `connectorId` | `SourceManagementTools.cs:111` |
| 精确过滤 SQL | `d.connector_id = @connectorId` + 索引 | `SqliteKnowledgeBaseStore.cs:2897` / `:120` |
| LIKE 转义 | `EscapeLikePattern` | `SqliteKnowledgeBaseStore.cs:2443` |
| 总数 | `GetDocumentsAsync` 返回 `(Docs, TotalCount)` | `IKnowledgeBaseService.cs:132` |
| 源名到连接器 | `ISourceCatalog.ListAsync` 返回 `SourceDescriptor(..., ConnectorId)` | `SourceCatalog.cs:20-34` |

### 2.4 设计模式映射（头脑风暴结论）

| 模式 | 适用点 | 现状 | 建议 |
| :--- | :--- | :--- | :--- |
| **Adapter** | 官方协议对象 ↔ 内部 `McpToolDescriptor` / `McpToolResult` | 已建成（`McpToolSchemaBuilder`、`McpToolResultWriters`、`BuildAnnotations`） | 补 `outputSchema` 适配即可；**不要**再新起一层 |
| **Strategy** | 结果写出（5 种形态）、排序/融合策略 | 已建成（`IMcpToolResultWriter`、检索侧 Strategy） | `list_files` 的过滤方式可做小 Strategy（`ConnectorFilter` / `KeywordFilter` / `CompositeFilter`），但只有 3 个分支，**不建议抽象成接口**（过度设计） |
| **StateMachine** | 后端连接状态 | **已存在**：`BackendConnectionStatus`（NotConnected/Connecting/Connected/Failed/Disabled）+ 重试间隔 + 连续失败计数 | 缺的是「状态 → 工具可见性」的单一投影；可引入 `IFallbackVisibility` 由状态机派生，**无需新建状态机** |
| **Composite** | 业务模块 → 工具树 | 已用于 `McpCategoryCatalog`（McpCatalogRegistry） | 不动 |
| **Decorator** | 统计缓存 | 已用于 `McpStatsCache`（30s TTL + 单飞） | 不动 |

反面结论（明确不要做）：

- 不要为 `list_files` 建完整状态机。它的输入是「有/无 connectorId + 有/无 keyword」四组合，不属于有生命周期的对象。
- 不要为 `Accept` 协商建状态机。它是单次请求的**纯函数**（Accept 串 → 决定 forceJson?），用中间件 + 一个布尔 Item 即可。
- 不要引入第二套「工具注册 DSL」。现状 `RegisterLocalTool` + 描述符已是单一入口。

---

## 3. 同类门禁与回归测试

| 编号 | 门禁 | 锁定的不变量 |
| :--- | :--- | :--- |
| G-N1 | `list_files(connectorId=已知)` | 只返回该连接器的文档；`appliedFilters.connectorId` 回显 |
| G-N2 | `list_files(connectorId=未知)` | `isError:true` + `errorCode=connector_not_found`（**不是** 0 条 success） |
| G-N3 | `list_files(pathKeyword="%")` | 命中数等于「无关键字」总数**不成立**（证明 ESCAPE 生效），且不返回全库 |
| G-N4 | `list_files(pathKeyword="不存在的词")` | `success:true, count:0, totalMatched:0`（可判别） |
| G-N5 | `list_files(connectorId + pathKeyword)` | 结果 ⊆ 单独 connectorId 的结果集 |
| G-N6 | tools/list schema | `list_files` 必须暴露 `connectorId` 与 `pathKeyword`，**不得**再暴露 `sourceId` |
| G-N7 | Accept 归一化 | `Accept: application/json` → 200 + `Content-Type: application/json` + 合法 JSON-RPC 结果 |
| G-N8 | Accept 严格客户端 | `Accept: application/json, text/event-stream` → 行为与今日一致（SSE），防回归 |
| G-N9 | Accept 多帧保护 | 人为注入两帧 → 保持 SSE，**不得**被剥壳成 JSON |
| G-N10 | 可见性单一投影 | `GetAllTools` 与 `InvokeToolAsync` 对同一描述符判定必须一致（参数化遍历所有工具） |
| G-N11 | Guide 文档同步 | `guide-skill-contract.test.mjs` 工具清单只认新参数名 |

---

## 4. 明确不做

| 项 | 理由 |
| :--- | :--- |
| 切官方 `WithTools` 静态注册 | 触发条件未满足（§2.1.5）；切换会丢参数错误可读性 |
| 中间件「只补 Accept 头不改响应」 | 会把客户端违规换成服务端违规（§2.2.1 方案 B） |
| `list_files` 引入过滤 Strategy 接口 | 3 个分支，抽象收益低于维护成本 |
| 为 `Accept` 协商/`list_files` 建状态机 | 无生命周期，属于纯函数/组合查询 |
| 全局修所有 `LIKE` 未转义点 | 与 A 方案同批会放大回归面；`chunk 文本 LIKE`、`MafChatService` 单独立项 |

---

## 5. 工作量与顺序（按风险升序）

| 序 | 任务 | 触碰面 | 风险 |
| :--- | :--- | :--- | :--- |
| 1 | `list_files` A 方案 + G-N1..G-N6 | `IndexStatusTools.cs` 一个方法、store 一处 LIKE/CONT | 低（单工具、有门禁） |
| 2 | 可见性收敛 `IToolVisibilityPolicy` + G-N10 | `McpToolRegistry.cs` | 中（触及所有工具路由，必须先补门禁） |
| 3 | Accept 归一化中间件 + G-N7..G-N9 | 新增 1 个中间件 + 端点注册顺序 | 中（影响传输层，务必带开关） |
| 4 | `outputSchema` + `ttlMs/cacheScope` | SDK 描述符 + Tool 构造 | 低 |

---

## 6. 参考文献与实测记录

- 官方源码：`modelcontextprotocol/csharp-sdk` `src/ModelContextProtocol.AspNetCore/StreamableHttpHandler.cs:70-80`（Accept 校验）、`src/ModelContextProtocol.Core/Server/McpServerImpl.cs:110-133`（`Changed` 订阅）、`:1500-1610`（list 合并与 filter 管线）、`:1763`（错误文案）、`:1904-1925`（ttlMs/cacheScope 盖默认值）
- SDK XML 文档：`McpServerOptions.ToolCollection`、`McpServerPrimitiveCollection.DeferChangedEvents/RaiseChanged`、`McpRequestFilters.ListToolsFilters/CallToolFilters`
- MCP 规范：Streamable HTTP「Sending Messages」第 2 条；2026-07-28 新增 `ttlMs`/`cacheScope` 与 `subscriptions/listen` 变更
- 社区：`arabold/docs-mcp-server#349`（406 与 OpenAI Responses 424）、`microsoft/vscode#330022`、`modelcontextprotocol/python-sdk#1641`（`*/*` 与 Accept 合规）
- 探针实测（本机）：`ToolCollection` 增删、handler 回落、filter 包住集合、反射注册 schema/annotations/outputSchema、异常文案、2026-07-28 单值 Accept 仍 406、ttlMs 仅在 2026-07-28 线上输出
- 生产实测：`tools/list` 10 次延迟 5.6–97.7ms；`list_files` plain 47–72ms、`%` 49–84ms；`list_sources` 4 行含 3 个 connectorId；`Accept: */*` → 200 SSE
