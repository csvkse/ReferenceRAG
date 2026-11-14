# 移动端适配与前端作业 UX 架构优化可行性调研

> 日期：2026-11-14
> 范围：`Business/ReferenceRAG.Business/wwwroot` 全部前端 + `Host/ReferenceRAG.ApiHost` 推送/静态资源管线
> 触发：① 前端样式能否适配移动端；② reindex 作业化功能（见《vector-model-switch-reindex-timeout-root-cause-and-async-job-architecture》）在可行性、逻辑、性能上是否存在欠缺，能否用设计模式（适配器/策略/状态机）优化；③ 现有库与业界方案对比；④ 本系统可复用基础设施确认。
> 方法：代码逐文件审计（file:line 证据）+ 生产环境只读实测（静态资源响应头）+ 业界方案 Web 调研。**本文档为调研与方案，不含已实施改动。**

---

## 0. 结论速览

| 问题 | 结论 |
| :--- | :--- |
| 移动端适配可行吗？ | **可行且低风险**。viewport meta 已正确、naive-ui 2.44.1 自带全部所需响应式原语（`n-grid` 响应式 cols、`n-drawer placement="bottom"`、`n-layout-sider collapse-mode="transform"`），无需换 UI 框架、无需引入构建链。当前全前端仅 **1 处** `@media` 查询，属"从未系统适配"而非"适配失败"。 |
| 最大移动端阻塞点 | 固定 240px 侧边栏（375px 手机展开态内容区仅 ≈115px）、固定 520/560px 抽屉、680px 无 max-width 弹窗、固定列数网格（4/3/6 列）、`calc(100vh - 300px)` 魔法高度、Login 卡片固定 400px。全部 P0 可修，纯前端。 |
| 最大移动端性能瓶颈 | **生产实测**：`vendor/naive-ui.js` 传输 1,702,484 字节、**无 Content-Encoding（零压缩）**、`Cache-Control: no-cache`（每次加载全量重验证）。修复 = ResponseCompression + vendor 长缓存，首屏传输量预计降 ~75%，重复访问 vendor 0 字节。 |
| reindex 功能有欠缺吗？ | 后端作业化本身健全（G1-G9 门禁全绿）；**前端集成层有 8 项欠缺**（§2）：绕过既有 JobCenter 双轨并存、无 SignalR 推送（基础设施已在！）、keep-alive 下轮询不停、后台标签页不暂停、取消竞态 404 误报为错误、隐式布尔状态机、多 KB 并行无忙碌矩阵、无乐观 Running 态。 |
| 设计模式能优化吗？ | 能，且**仓库已有大量先例可对齐**：后端 IndexJobStateMachine（状态机）、IIndexExecutionStrategy（策略）、SpecEmbeddingAdapterResolver（适配器）；前端 transport 适配器（SignalR/IPC 同构）、JobCenter（观察者+乐观注册）。新增提案：抽屉显式 FSM、作业源适配器、推送优先/轮询兜底传输策略、断点响应策略（§6）。 |
| 需要新第三方库吗？ | **不需要任何新运行时依赖**。VueUse/XState 的能力用 ~30 行 `useBreakpoints` + ~60 行 FSM 手写等价物覆盖（免构建 ESM 约束下 vendor 维护成本 > 收益）；SignalR 8.0.17 已 vendor 化并在用。对比与取舍见 §4。 |

---

## 1. 现状审计（证据表）

### 1.1 移动端缺陷清单

| # | 缺陷 | 证据（file:line） | 移动端影响 | 级别 |
| :-- | :--- | :--- | :--- | :--- |
| M1 | 侧边栏固定宽 240/64，`collapse-mode="width"`，无断点逻辑，`collapsed` 初始 false | `app/layouts/MainLayout.js:18,174` | 375px 手机：展开态内容区 ≈115px；折叠态仍被 64px sider + 40px padding 占去 104px | **P0** |
| M2 | reindex 抽屉 `:width="520"` 固定 | `app/views/KnowledgeBases.js:1536` | 超宽溢出/遮罩盖满但内容横向裁切 | **P0** |
| M3 | 连接器抽屉 `:width="560"` 固定 | `app/views/KnowledgeBases.js:1644` | 同上 | **P0** |
| M4 | 新建知识库弹窗 `width: 680px` **无 max-width** | `app/views/KnowledgeBases.js:1367` | 375px 屏直接横向溢出（对比：切换/重分段弹窗已有 `max-width: 95vw`，1480/1771） | **P0** |
| M5 | Dashboard 统计网格 `:cols="4"` 固定 | `app/views/Dashboard.js:175` | 4 张统计卡挤成 ~80px/张，文字截断 | **P0** |
| M6 | KB 卡片网格 `:cols="3"`、表单网格 `:cols="2"` 固定 | `app/views/KnowledgeBases.js:1269,1452` | 卡片/表单列挤压 | **P0** |
| M7 | Graph 筛选网格 `:cols="6"` 固定 | `app/views/Graph.js:349` | 6 列控件不可用（注：Graph 是列表/标签管理视图，**非 canvas**，无触摸手势问题） | **P0** |
| M8 | Chat 消息区 `height: calc(100vh - 300px)` | `app/views/Chat.js:762`（template） | 移动端工具栏 flex-wrap 后实际高度 ≫300px → 消息区被输入区顶出视口/负高度；100vh 含 iOS 地址栏 | **P0** |
| M9 | Login 卡片 `width: 400px` 固定 | `app/views/Login.js:59` + `styles/pages.css:410`（min-height:100vh） | 375/360px 手机横向溢出，首屏即坏 | **P0** |
| M10 | Logs `calc(100vh - 120px)`、Reader `calc(100vh - 100px/240px)` | `app/views/Logs.js:311`、`app/views/Reader.js:689,762,791` | iOS 地址栏收缩时底部裁切 | P1 |
| M11 | 9 个表格视图中 7 个无 `scroll-x`（仅 Models/Performance 有） | `ApiKeys/Dashboard/Graph/Logs/ModelsSimple/Sources/System.js` | 表格横向撑破容器而非受控横滚 | P1 |
| M12 | `html/body/#app` `overflow:hidden` + 100vh 体系 | `styles/pages.css:8-12` | 与 M8/M10 同源的 vh 问题 | P1 |
| M13 | 全前端仅 1 处 `@media`（reindex-stats 560px 两列） | `styles/pages.css:591` | 无系统性断点体系 | 佐证 |
| M14 | 触控目标过小：`size="small/tiny"` 按钮 22-28px | Chat 消息操作行等（`Chat.js:762` template） | 低于 44px 触控标准，误触率高 | P2 |
| M15 | hover-only 交互（`n-tooltip trigger="hover"`、sider show-trigger） | Chat/ApiKeys 等 | 触屏无 hover，提示不可达 | P2 |
| M16 | FileSearch `:cols="4"`、Guide `:cols="3"` | `app/views/FileSearch.js:333`、`app/views/Guide.js:299,457` | 列挤压 | P1 |

**已做对的部分**（避免误判）：viewport meta 正确（`index.html:5`）；Chat 滚动已有触摸处理（`@touchstart.passive/@pointerdown/@wheel.passive` + `shared/conversation-scroll.js`）；切换/重分段弹窗已有 `max-width: 95vw`；ApiKeys 弹窗已有 90-92vw 上限；图标已按需子集化（`vendor/icons.js` 仅 2.7KB）。

### 1.2 性能实测与开销面

**生产静态资源响应头（只读 curl 实测，`<PROD_HOST>:5123/vendor/naive-ui.js`，请求带 `Accept-Encoding: br,gzip`）**：

```
Content-Length: 1702484        ← 1.7MB 原样传输
Content-Type: text/javascript
Cache-Control: no-cache        ← 每次加载强制重验证
ETag: "1dd7432e9db9bd4"        ← 有 ETag（304 可用，但每文件一次 RTT）
（无 Content-Encoding 头）      ← 零压缩
```

根因：`Host/ReferenceRAG.WebHost/Program.cs:161` 对**所有**静态文件统一 `no-cache`，且全管线无 `AddResponseCompression/UseResponseCompression`（仅 FileSystemController 有无关的下载压缩）。

**vendor 体积表**（`vendor/README.md`：vue 3.5.32 / naive-ui 2.44.1 / vue-router 4.6.4 / axios 1.14.0 / marked 18.0.4 / signalr 8.0.17 / ionicons5 0.12.0，全部本地托管、免构建）：

| 文件 | 原始字节 | 备注 |
| :--- | ---: | :--- |
| naive-ui.js | 1,702,484 | 全量 ESM bundle，**主导成本**；Brotli 后预计 ~380-450KB |
| vue.js | 168,519 | |
| signalr.js | 47,649 | head 同步 UMD；另有 78B ESM shim |
| marked.js | 42,097 | |
| axios.js | 37,195 | |
| vue-router.js | 27,215 | |
| icons.js | 2,687 | 已按需子集（既有性能实践先例） |

**轮询/推送开销面**：

| 面 | 机制 | 证据 | 问题 |
| :--- | :--- | :--- | :--- |
| reindex 抽屉 | `setInterval` 1500ms，抽屉开着且作业活跃即轮询 | `KnowledgeBases.js:198-213` | 无推送、无可见性暂停、keep-alive 切路由不停（M17） |
| JobCenter | 推送优先 + 2000ms 自适应轮询兜底（有活跃作业才轮询，清空即停） | `core/jobs/JobCenter.js:320-329` | 设计正确，但同样无 visibilitychange 感知 |
| 索引状态 | SignalR 纯推送（IndexStarted/Progress/Completed/Complete） | `app/stores/index.js:55-88` | 无轮询，范式标杆 |
| 全局 | `visibilitychange`/`document.hidden` 全前端 **0 处** | grep app/ core/ 为空 | 后台标签页持续轮询，移动端耗电耗流量 |

**M17（逻辑缺陷，keep-alive 轮询泄漏）**：`MainLayout.js:174` 用 `<keep-alive :max="20">` 缓存视图；`KnowledgeBases.js:2` 只导入 `onActivated`（无 `onDeactivated`），`:282` 仅 `onUnmounted(stopReindexPolling)`。用户开着抽屉切到别的页面 → 组件 deactivated 而非 unmounted → **1500ms 轮询在后台持续运行**。`onActivated`（:1195）恢复时也未重新对齐作业快照。

### 1.3 可复用基础设施清单（本次调研核心发现）

| 基础设施 | 位置 | 现状 | 复用方向 |
| :--- | :--- | :--- | :--- |
| **JobCenter 作业中枢** | `core/jobs/JobCenter.js`（353 行） | SignalR 推送优先 + 断连自适应轮询兜底 + 重连重同步（`onreconnected → JoinIndexGroup + syncActiveJobs`）+ 乐观作业注册（`local-*` → `replaceJobId` 无缝替换，15s 超时判 Faulted）+ 资源键控忙碌态 + 监听器分发 + 失效标签自动刷新。Dashboard/Sources 已接入（`Dashboard.js:125`、`Sources.js:691`） | **reindex 抽屉接入**：扩展为多作业源（§6.2），KB 卡片获得忙碌徽标/乐观态/自动刷新 |
| **useAsyncJob 组合式门面** | `core/jobs/useAsyncJob.js`（150 行） | 乐观 Running、phase/progress 响应式映射、资源忙碌判断、onUnmounted 自动退订。**已建成但无任何视图引用**（grep views 为 0） | reindex 抽屉直接改用，消除手写 setInterval |
| **InvalidationRegistry** | `core/jobs/InvalidationRegistry.js` | jobType→失效标签策略注册表（Rechunk/Incremental/FullRebuild/VectorOnly/Cleanup/ModelDownload/Default） | 增加 `Reindex: ['knowledge_bases','vector_index','dashboard_stats']` |
| **传输适配器** | `core/transport/index-events.js` | Web=SignalR（withAutomaticReconnect + API Key 双通道鉴权）、桌面=IPC，**同构接口** {on,onreconnected,start,stop,invoke} | 已是适配器模式先例；KB 作业事件走同一连接 |
| **IndexHub 推送面** | `Host/ReferenceRAG.ApiHost/Hubs/IndexHub.cs`（67 行） | group "index-watchers" + 静态 `BroadcastIndexStarted/Progress/Completed/FileChanged(IHubContext, payload)` 助手；`RagWebHostExtensions.cs:140` 映射 `/hubs/index` | 照抄范式增加 `BroadcastReindexJob*`（或泛化 JobProgress 事件）；后端 worker 阶段跃迁点已存在（KnowledgeBaseJobCoordinator/JobStateMachine） |
| **后端模式先例** | `Business/Features/Indexing/StateMachine/IndexJobStateMachine.cs`、`Strategies/IIndexExecutionStrategy.cs`（Incremental/Rebuild）、`SpecEmbeddingAdapterResolver.cs`、`KnowledgeBaseJobCoordinator.cs` | 状态机/策略/适配器/单飞协调器全部手写、无第三方依赖 | 前端 FSM 手写有直接先例，风格对齐 |
| **门禁与测试基建** | `check.mjs`（AST 符号绑定/模板/导出作用域）、`tests/jobs.test.mjs`（JobCenter+InvalidationRegistry 单测）、`tests/transport.test.mjs`、`tests/reindex-job.test.mjs` | 全绿（89/89） | 新门禁 G-M1..G-M9 直接扩展这些文件（§7） |
| **naive-ui 响应式原语** | vendor 2.44.1 | `n-grid cols="1 s:2 m:4"` 响应式断点字符串、`n-drawer placement="bottom" :height`、`n-layout-sider collapse-mode="transform"`（overlay 模式）、`n-modal` style max-width | 零新依赖覆盖全部 P0 布局修复 |
| **Chat 触摸滚动** | `shared/conversation-scroll.js` | 已实现触摸/指针/滚轮统一处理 | 无需重做，仅修高度策略 |

### 1.4 既有设计模式盘点

| 模式 | 后端 | 前端 | 部署脚本 |
| :--- | :--- | :--- | :--- |
| 状态机 | IndexJobStateMachine、KB JobStateMachine（Queued/Running/Cancelling/终态） | —（抽屉为隐式布尔态，**欠缺**） | INIT→PROBE→SYNC→BUILD→DEPLOY→VERIFY |
| 策略 | IIndexExecutionStrategy（Incremental/Rebuild）、同步策略 | InvalidationRegistry（jobType→标签映射）、JobCenter 推送/轮询自适应 | Auto/GitArchive/Direct 同步策略、硬件 profile |
| 适配器 | SpecEmbeddingAdapterResolver（本地/API 嵌入统一接口） | transport（SignalR/IPC 同构） | Windows OpenSSH 执行适配器 |
| 观察者 | — | JobCenter listeners（onProgress/onCompleted/onFailed）+ registerRefresher | — |
| 乐观更新 | — | registerStartedJob/replaceJobId（**reindex 未复用**） | — |
| 单飞/协调器 | KnowledgeBaseJobCoordinator（per-KB TryBeginReindex 409） | — | — |

---

## 2. reindex 与作业 UX 头脑风暴：逻辑/性能欠缺（G-a..G-h）

| # | 欠缺 | 证据/机理 | 后果 | 优化方向 |
| :-- | :--- | :--- | :--- | :--- |
| G-a | **双作业系统并存**：KB reindex 完全绕过 JobCenter | `KnowledgeBases.js` 无 JobCenter/useAsyncJob 导入；抽屉手写 `pollReindexStatus`+`setInterval` | KB 卡片无忙碌徽标、无乐观态、完成后手动 `loadKnowledgeBases`（失效刷新范式未复用）；两套轮询逻辑漂移风险 | 作业源适配器接入 JobCenter（§6.2） |
| G-b | **无推送**：reindex 阶段跃迁只落库不广播 | IndexHub 仅 Index* 事件；后端 worker 已有阶段跃迁点 | 进度延迟 ≤1.5s；每个开抽屉的客户端持续 HTTP 轮询，移动端无线电频繁唤醒 | 扩展 Hub 广播 ReindexJob 事件，推送优先/轮询兜底（§6.3） |
| G-c | **轮询生命周期不完整** | M17：无 onDeactivated；全局无 visibilitychange | keep-alive 切路由/后台标签页持续轮询，耗电耗流量 | FSM + 生命周期策略：deactivated/hidden → 暂停，activated/visible → 立即同步一次再恢复 |
| G-d | **取消竞态 UX 误分类** | 验收实测：作业在取消请求抵达前已 Succeeded → 后端正确返回 404「任务不可取消或不存在」→ 前端 toast 归为"取消失败"错误 | 用户误以为操作失败（实际作业已成功） | FSM `CANCEL_404` 转移 → 归类 info「作业已结束」+ 拉取终态快照刷新（后端行为无需改） |
| G-e | **隐式布尔状态机** | `showReindexDrawer/reindexActive/reindexJob/reindexGeneration` 四变量隐式耦合 | 非法转移无守卫（终态瞬间点取消、回滚中再触发等）靠运气而非结构 | 显式 FSM（§6.1），非法转移在状态表层面拒绝 |
| G-f | **多 KB 并行无忙碌矩阵** | 协调器 per-KB 单飞（不同 KB 可并行作业），前端单抽屉单作业视角 | KB-A 作业中，KB-B 卡片无感知；全局作业面板缺失 | JobCenter `activeJobsByResource`（`kb:{id}` 键）天然支持；卡片徽标 + 全局作业抽屉（P2 可选） |
| G-g | **enqueue 乐观盲区** | 点击确认 → HTTP 响应之间无本地 Running 态 | 慢网络下按钮短暂"没反应"，可能重复点击（后端 409 兜底但体验差） | `useAsyncJob.runJob` 已实现乐观注册+失败清理，直接复用 |
| G-h | **事件契约索引专用** | `JobCenter._normalizeJob` 硬编码 currentFile/sources/rechunk/vectorOnly 字段 | 直接塞 KB 作业会污染归一化逻辑 | 源适配器各自 normalize，JobCenter 只消费 UnifiedJob（§6.2） |

**可行性结论**：以上全部为**集成层/UX 层**欠缺，不触及已验收的蓝绿影子存储与作业协调器正确性（G1-G9 门禁继续生效）；后端改动仅"阶段跃迁处增加广播调用"一类加法，风险低。

---

## 3. 业界主流方案对比

### 3.1 响应式布局与导航

| 方案 | 要点 | 对本项目的适用性 |
| :--- | :--- | :--- |
| **Material Design 3 断点 + 规范布局** | compact(<600)/medium(600-840)/expanded(840+)；导航随断点切换：compact→模态抽屉/底部导航，expanded→永久侧栏；list-detail/feed/supporting-pane 三规范布局 | **采纳其断点思想**：单断点 767px（手机）起步，1024px（平板）可选。管理后台低频导航场景，overlay 抽屉足够，底部 tab bar 列为 P2 可选 |
| **Ant Design Pro 移动适配** | sider 断点折叠 + 移动端抽屉导航 + 表格横滚 | 同思路；naive-ui `collapse-mode="transform"` 即 overlay sider，等价实现零依赖 |
| **naive-ui 社区实践** | 官方不提供完整响应式布局方案；社区用 CSS Grid/Flex + 媒体查询 + `n-grid` 响应式 cols（`"1 s:2 m:4"`）+ `n-drawer placement="bottom"` | **主路线**：与本项目免构建 ESM + naive-ui 现状完全一致 |
| **Vant / Ant Design Mobile** | 移动端专用组件库 | **拒绝**：引入第二套设计语言与运行时（双 UI 库体积翻倍、主题/暗色模式双维护），naive-ui 原语已够用 |

### 3.2 长任务进度反馈

| 方案 | 要点 | 取舍 |
| :--- | :--- | :--- |
| **Hangfire 官方文档** | "Polling 更易理解，但 server push 更舒适，避免对服务器的不必要调用" | 权威佐证推送优先 |
| **SignalR per-job group** | 后台作业 `hubContext.Clients.Group(jobId).SendAsync(...)` 单调用推送；按作业/资源分组避免全量广播 | **采纳**：IndexHub 已有 group 范式（index-watchers），扩展 `kb-{id}` group 或统一 JobProgress 事件 |
| **SSE (Server-Sent Events)** | 单向推送、HTTP 原生、实现简单 | **不引入**：SignalR 8.0.17 已 vendor 化 + 自动重连 + 桌面 IPC 同构适配器已在用；SSE 需新端点/新客户端/鉴权重做，收益为负 |
| **纯轮询 + 退避** | 实现最简单 | 保留为**兜底策略**（JobCenter 已实现自适应轮询），不作为主通道 |

### 3.3 前端状态机

| 方案 | 要点 | 取舍 |
| :--- | :--- | :--- |
| **XState v5** | 完整 statechart 运行时（actors/guards/delays/并行状态/可视化） | **不引入**：能力远超单抽屉 6 状态生命周期；免构建约束下新增 vendor 有维护成本；仓库后端状态机（IndexJobStateMachine）即手写先例 |
| **手写 FSM + 转移表单测** | ~60 行：状态×事件→次态+守卫，非法转移显式拒绝 | **采纳**：与后端风格对齐，可被 node --test 直接覆盖（G-M2） |
| **Pinia store 布尔态** | 现状等价物 | 不解决非法转移问题，仅作 FSM 的响应式载体 |

### 3.4 静态资源压缩与缓存

| 方案 | 要点 | 取舍 |
| :--- | :--- | :--- |
| **ResponseCompression 中间件** | `AddResponseCompression`（Brotli/Gzip provider，MIME 白名单）+ `UseResponseCompression` 置于 StaticFiles 之前 | **采纳（P2 首选）**：两行注册 + MIME 配置，对现有管线零结构改动 |
| **.NET 10 MapStaticAssets** | 构建期指纹 + 自动压缩 + 缓存头一体化 | **备选评估**：本项目 wwwroot 由嵌入资源在发布期链接拷贝，需验证静态 web assets 清单兼容性；不作为阻塞项 |
| **vendor 长缓存** | `Cache-Control: public, max-age=31536000, immutable` + importmap 版本查询（`/vendor/vue.js?v=<vendorVersion>`，升级 vendor 时 bump） | **采纳**：重复访问 0 字节 0 RTT；版本查询杜绝陈旧；app 代码保持 no-cache |
| **naive-ui vendor 瘦身** | 一次性 esbuild tree-shake 定制 bundle | **P2 可选、谨慎**：改变 vendor 维护流程（README 声明"copied from installed browser distribution"）；压缩+缓存已拿走 ~75% 收益，瘦身边际收益递减 |

### 3.5 移动视口单位

`100vh` 在 iOS Safari/移动 Chrome 按"最大可能视口"计算，地址栏收缩时内容被浏览器 chrome 遮挡；`dvh` 动态跟随（web.dev / caniuse：Chrome 108+ / Safari 15.4+，已 Baseline）。**采纳**：`100dvh` + flex 布局替代魔法 `calc(100vh - Npx)`；容器查询（`@container`）用于卡片内部自适应（P2 可选，Baseline 已广）。

---

## 4. 库与依赖决策表

| 候选 | 用途 | 成本 | 决策 | 理由 |
| :--- | :--- | :--- | :--- | :--- |
| naive-ui 内置原语 | 响应式 grid/bottom drawer/transform sider/modal max-width | 0（已 vendor） | **采纳** | 覆盖全部 P0 布局修复 |
| 手写 `useBreakpoints`（core/responsive.js） | matchMedia 断点响应式单例 | ~30 行 + 单测 | **采纳** | 借鉴 VueUse API 设计（breakpoints.smaller('md') 语义），不引入整包 |
| VueUse（useBreakpoints/useMediaQuery/useDocumentVisibility） | 同上 + 可见性 | 新 vendor（core+shared 两包）、免构建下需手工 ESM 化 | **不引入，借鉴 API** | 三个能力手写 <60 行；vendor 维护成本 > 收益 |
| 手写 FSM | 抽屉生命周期 | ~60 行 + 转移表单测 | **采纳** | 后端 IndexJobStateMachine 先例；XState 能力过剩 |
| XState v5 | statechart 运行时 | 新 vendor + 学习成本 | **不引入** | 单抽屉 6 状态用不到 actors/并行态 |
| @microsoft/signalr 8.0.17 | ReindexJob 推送 | 0（已 vendor + 自动重连 + 鉴权适配已在用） | **复用** | G-b 主通道 |
| Vant / Ant Design Mobile | 移动组件 | 双 UI 库体积/主题维护 | **拒绝** | §3.1 |
| CSS dvh / 容器查询 / :has() | 视口与卡片自适应 | 0（原生 Baseline） | **采纳** | §3.5 |
| ResponseCompression | 静态/API 压缩 | 0（框架内置） | **采纳** | §3.4，生产实测零压缩 |
| MapStaticAssets (.NET 10) | 指纹+压缩一体 | 管线兼容性待验证 | **P2 评估** | 不阻塞 P0/P1 |

**结论：P0/P1/P2 全程零新增运行时依赖。**

---

## 5. 具体方案（分阶段）

### P0 移动端阻塞修复（纯前端，预估 1-2 天）

| 项 | 改动 | 文件 | 验收 |
| :--- | :--- | :--- | :--- |
| P0-1 | 新增 `core/responsive.js`：`useBreakpoints()` matchMedia 单例（`isMobile <768`、`isTablet <1024`），响应式 ref + 自动清理 | core/responsive.js（新） | G-M1 单测（mock matchMedia 断点翻转） |
| P0-2 | MainLayout 移动态：sider `collapse-mode="transform"` + 默认 collapsed + 汉堡触发；内容 padding 20→12px；header 面包屑截断 | app/layouts/MainLayout.js | 375px 内容区全宽；桌面态像素级不变 |
| P0-3 | 抽屉响应式：`:width="isMobile ? '100%' : 520"`（reindex）/560（connector）；或移动态 `placement="bottom" :height="'85dvh'"`（二选一，推荐 bottom 弹层更符合移动直觉） | app/views/KnowledgeBases.js:1536,1644 | 375px 无横向溢出，关闭按钮可达 |
| P0-4 | 全部 n-modal 补 `max-width: 95vw`（新建 680px 优先）；编辑类弹窗移动态 `label-placement="top"` | KnowledgeBases.js:1367,1741,1809 等 | G-M5 静态门禁 |
| P0-5 | n-grid 固定 cols → 响应式字符串：Dashboard `"1 s:2 m:4"`、KB 卡片 `"1 s:2 m:3"`、Graph `"2 s:3 m:6"`、FileSearch `"1 s:2 m:4"`、表单 `"1 s:2"` | Dashboard.js:175、KnowledgeBases.js:1269,1452、Graph.js:349、FileSearch.js:333 | 375px 单列/双列，桌面不变 |
| P0-6 | Login 卡片 `width: min(400px, 92vw)`；容器 min-height 100dvh | Login.js:59、pages.css:410 | 360px 无溢出 |
| P0-7 | Chat/Logs/Reader：外层 flex 列布局（header auto + 消息区 flex:1 min-height:0 + 输入区 auto），废除 `calc(100vh - Npx)`；根容器 100dvh | Chat.js:762、Logs.js:311、Reader.js:689,762,791、pages.css:8-12 | iOS Safari 地址栏收缩无裁切；G-M5 vh 白名单 |
| P0-8 | 7 个表格视图补 `:scroll-x`（列宽合计） | ApiKeys/Dashboard/Graph/Logs/ModelsSimple/Sources/System | 移动态受控横滚不撑破 |

### P1 架构统一：推送优先 + FSM + JobCenter 接入（预估 2-4 天）

| 项 | 改动 | 文件 | 验收 |
| :--- | :--- | :--- | :--- |
| P1-1 | 后端：reindex worker 阶段跃迁/进度批次处广播 `ReindexJobStarted/Progress/Completed`（照 IndexHub.Broadcast* 静态助手范式；payload：jobId/kbId/status/phase/processed/total/percent/speed/error） | ApiHost/Hubs/IndexHub.cs（或新 JobHub）、Business KB 作业 worker | G-M6 广播门禁 + G-M7 契约门禁 |
| P1-2 | 前端：JobCenter 多作业源适配器（IndexJobSource 现有逻辑迁入 / KbJobSource 新增：normalize→UnifiedJob、fetchActive→/api/jobs?operation=Reindex、事件订阅）；`InvalidationRegistry.Reindex` 标签 | core/jobs/JobCenter.js、InvalidationRegistry.js、新 core/jobs/sources/*.js | G-M3/G-M4；Dashboard/Sources 行为不变（回归） |
| P1-3 | reindex 抽屉改用 `useAsyncJob('kb:{id}')` + 显式 FSM（§6.1）；删除手写 setInterval；取消 404→info「作业已结束」+ 终态快照 | app/views/KnowledgeBases.js | G-M2；四条验收路径（黄金/回滚/409/取消）复测 |
| P1-4 | 生命周期策略：`onDeactivated`/`visibilitychange hidden` → 暂停轮询与心跳；`onActivated`/`visible` → 立即 syncActiveJobs 一次再恢复；JobCenter 与抽屉共用 | core/jobs/JobCenter.js、KnowledgeBases.js | G-M3 可见性用例；后台标签页 0 请求 |
| P1-5 | KB 卡片忙碌徽标（`JobCenter.isResourceBusy('kb:{id}')`）+ enqueue 乐观 Running（useAsyncJob.runJob） | KnowledgeBases.js | 慢网络下点击即有反馈；多 KB 并行各自徽标 |

### P2 性能与打磨（预估 1-2 天）

| 项 | 改动 | 验收 |
| :--- | :--- | :--- |
| P2-1 | `AddResponseCompression`（Brotli+Gzip，MIME：text/javascript、text/css、application/json、image/svg+xml）+ `UseResponseCompression` 在 StaticFiles 前；vendor 路径 `Cache-Control: public,max-age=31536000,immutable` + importmap `?v=<vendorVersion>`（升级 vendor 时 bump）；app 代码保持 no-cache | G-M8：curl 断言 Content-Encoding 存在、naive-ui 传输 ≤500KB、vendor 缓存头正确 |
| P2-2 | 触控目标：移动态 media query 按钮最小命中区 44px（padding 扩展不改视觉）；n-tooltip 移动态 trigger 补 click/focus | 真机抽检 |
| P2-3 | 表格移动态卡片化（Sources/Models/Logs 优先，n-data-table → 卡片列表切换） | 375px 无横滚可读 |
| P2-4 | PWA manifest（name/icons/theme-color/display:standalone）+ apple-touch-icon 已有 → 可安装到主屏 | Lighthouse PWA 可安装性 |
| P2-5 | （可选评估）MapStaticAssets 替代 UseStaticFiles；naive-ui tree-shake 瘦身；底部 tab bar 导航 | 独立评审，不阻塞 |

---

## 6. 设计模式落地细节

### 6.1 抽屉显式 FSM（状态模式，手写 ~60 行）

状态：`idle / enqueuing / active / cancelling / terminal(Succeeded|Failed|Cancelled) / rollingBack`

| 当前态 | 事件 | 次态 | 守卫/动作 |
| :--- | :--- | :--- | :--- |
| idle | ENQUEUE | enqueuing | 乐观注册 local-*（useAsyncJob.runJob） |
| enqueuing | ENQUEUED(job) | active | replaceJobId；开抽屉 |
| enqueuing | ENQUEUE_409(job) | active | warning toast + 复用活跃作业抽屉 |
| enqueuing | ENQUEUE_FAIL | idle | 清理乐观态、error toast |
| active | PROGRESS | active | 更新进度/阶段/速度 |
| active | TERMINAL(r) | terminal(r) | 停轮询；失效标签刷新；toast |
| active | CANCEL | cancelling | 二次确认后发 cancel |
| cancelling | CANCEL_ACK | active | 等待终态事件（Cancelling 仍属活跃） |
| cancelling | CANCEL_404 | terminal(拉取快照) | **info「作业已结束」**（G-d 修复） |
| cancelling | TERMINAL(r) | terminal(r) | 正常收敛 |
| terminal | ROLLBACK | rollingBack | 仅 CanRollback=true 可达 |
| rollingBack | ROLLBACK_DONE | idle | 刷新代次与卡片 |
| terminal/active | CLOSE | 保持 | 关抽屉不停作业；deactivated 暂停观测 |
| 任意非法组合 | — | 拒绝 | 转移表缺省 = 非法，按钮 disabled 由状态派生 |

载体：`reactive({ state, job, generation })`；模板按钮可用性全部由 `state` 派生（消灭四布尔耦合）。单测直接驱动转移表（G-M2），与后端 IndexJobStateMachine 测试风格对齐。

### 6.2 作业源适配器（适配器模式，G-a/G-h 修复）

```js
// core/jobs/sources/KbJobSource.js（提案）
export const KbJobSource = {
  key: 'kb',
  events: { started: 'ReindexJobStarted', progress: 'ReindexJobProgress', completed: 'ReindexJobCompleted' },
  normalize(raw) { return { jobId: raw.jobId ?? raw.Id, targetResource: `kb:${raw.knowledgeBaseId ?? raw.KnowledgeBaseId}`,
    jobType: 'Reindex', status: raw.status ?? raw.Status, phase: raw.phase ?? raw.Phase,
    progressPercent: raw.percent ?? raw.Percent ?? 0, currentMessage: raw.message ?? '', success: ... }; },
  async fetchActive(api) { return api.listJobs({ operation: 'Reindex', activeOnly: true }); },
  statusPath: (kbId) => `/api/knowledge-bases/${kbId}/reindex/status`,
};
```

JobCenter 只消费 UnifiedJob；IndexJobSource 把现有 `_normalizeJob` 索引专用逻辑迁入自身适配器（Dashboard/Sources 零行为变化）。InvalidationRegistry 增加 `Reindex: ['knowledge_bases','vector_index','dashboard_stats']`。

### 6.3 推送优先/轮询兜底（策略模式，G-b/G-c 修复）

| 传输态 | 策略 | 请求开销 |
| :--- | :--- | :--- |
| SignalR 已连接 + 页面可见 | 纯推送 + 10s 心跳对账（防事件丢失） | ≈0（心跳 1 次/10s） |
| SignalR 断连（自动重连中） | 2s 自适应轮询（仅有活跃作业时） | JobCenter 现有兜底 |
| 页面 hidden / 组件 deactivated | **全暂停**（推送事件仍入队，恢复时 syncActiveJobs 对账） | 0 |
| 重连成功 / visible / activated | 立即 syncActiveJobs 一次 → 回到推送态 | 1 次 |

### 6.4 断点响应策略（策略模式）

`useBreakpoints()` 单例产出 `isMobile/isTablet/isDesktop`；布局组件按断点选择渲染策略（sider permanent/transform、drawer right/bottom、grid cols 字符串、label-placement left/top）。CSS 侧用同一断点常量（767/1023px）写 media query，JS/CSS 断点值由 G-M5 静态门禁锁定一致。

---

## 7. 回归门禁（G-M1..G-M9，防同类问题复发）

| 门禁 | 层 | 断言 | 落点 |
| :--- | :--- | :--- | :--- |
| G-M1 | 前端单测 | useBreakpoints：mock matchMedia 初始/翻转/清理；单例复用 | tests/responsive.test.mjs（新） |
| G-M2 | 前端单测 | FSM 转移表全合法路径 + 非法转移拒绝 + CANCEL_404→info 分类 + 终态按钮派生 | tests/reindex-job.test.mjs（扩展） |
| G-M3 | 前端单测 | KbJobSource 适配器：fake connection 推送→响应式 Map；断连→轮询兜底；hidden/deactivated→0 定时器；恢复→对账 | tests/jobs.test.mjs（扩展） |
| G-M4 | 前端单测 | InvalidationRegistry.Reindex 标签；Reindex 完成触发 knowledge_bases 刷新 | tests/jobs.test.mjs（扩展） |
| G-M5 | 静态门禁 | 所有 n-drawer 有响应式 width/bottom 策略；所有 n-modal 有 max-width≤95vw；n-grid cols 为响应式字符串（白名单豁免需注释）；新增代码禁止 100vh（dvh 白名单）；JS/CSS 断点常量一致 | check.mjs 扩展或 scripts/check-mobile.mjs（新） |
| G-M6 | 后端单测 | reindex worker 每次阶段跃迁/进度批次必广播（fake IHubContext 计数与 payload） | Host.Tests / Business.Tests 扩展 |
| G-M7 | 契约测试 | ReindexJob* 事件 payload 形状（jobId/kbId/status/phase/processed/total/percent）前后端一致；_normalize 双向兼容 PascalCase/camelCase | tests/transport.test.mjs + 后端契约测试 |
| G-M8 | 性能预算 | CI curl 静态资源：Content-Encoding ∈ {br,gzip}；naive-ui 传输 ≤500KB；vendor Cache-Control immutable + importmap 版本查询存在；首屏 JS 总传输 ≤800KB | scripts/ 冒烟脚本（新） |
| G-M9 | E2E | 375×667 / 768×1024 / 1440×900 三视口：登录→KB→绑定弹窗→抽屉→取消→回滚黄金路径；无横向溢出（document.scrollWidth ≤ innerWidth）；控制台零错误 | 既有生产检索页验收脚本体系扩展 |

---

## 8. 实施状态与验收结果（2026-11-15 全部完成）

P0-1..8 / P1-1..5 / P2-1..4 全部落地，G-M1..G-M9 全绿。P2-5（MapStaticAssets / naive-ui tree-shake / 底部 tab bar）为可选评估项，本次按约定跳过（ResponseCompression 路线已达标，不依赖它）。

### 8.1 门禁实测

| 门禁 | 命令 | 结果 |
| :--- | :--- | :--- |
| G-M1..M4 | `npm test`（wwwroot，113 用例） | 全绿（responsive 单例、FSM 转移表、双源适配器、失效注册） |
| G-M5 | `node scripts/check-mobile.mjs` | 全绿：24 个模板（双引号/反引号两种书写风格）全部编译通过；抽屉全响应式、弹窗 ≤95vw、n-grid 响应式列、禁 100vh、断点常量一致 |
| G-M6 | Host.Tests / Business.Tests | 全绿（阶段跃迁/进度批次广播计数与 payload） |
| G-M7 | tests/transport.test.mjs + 后端契约测试 | 全绿（事件 payload 形状 + Pascal/camel 双向归一化）。importmap 化后同步修复 FrontendViewIntegrityTests：importmap 值携带 `?v=` 查询，磁盘定位前剥离 |
| G-M8 | `node scripts/check-perf.mjs`（静态+网络） | 全绿：naive-ui 408.3KB（br，预算 500KB）；首屏 JS 530.8KB（预算 800KB）；vendor 全 immutable；app no-cache；ETag 304 零字节；manifest/icons 200 |
| G-M9 | `npm run e2e:mobile` | 全绿：三视口黄金/取消/回滚路径 + 传输策略 5 断言，零控制台错误、零 4xx/5xx、全程无横向溢出 |

`scripts/verify-gate.ps1` 已把 G-M5/G-M8 静态部分纳入既有门禁链（前端单测之后执行）。

### 8.2 关键实测数据

| 指标 | 实测 | 预算 | 判定 |
| :--- | :--- | :--- | :--- |
| naive-ui.js 传输（br Optimal） | 408,310 B | ≤500 KB | 达标。Fastest 实测 568.3KB 超预算已弃用；vendor immutable 使压缩成本仅每客户端每版本一次 |
| 首屏 JS 总传输（br） | 530,840 B | ≤800 KB | 达标（18 个首屏资源） |
| vendor 重复访问 | 0 字节（immutable 命中） | 0 字节 | 达标 |
| 抽屉进度延迟（推送在线） | 推送即时（无轮询驱动，8-10s 窗口 0 次轮询） | ≤200 ms | 达标 |
| 推送在线时轮询请求 | 心跳 1 次/10s（双源共 2 请求/10s，12s 窗口实测 2 次） | ≤6 次/分钟 | 达标 |
| 后台标签页轮询 | 0（6s 窗口实测 0 次） | 0 | 达标 |
| 恢复可见对账 | 立即 1 次（3s 窗口实测 2 次，双源各 1） | 1 次 | 达标 |

### 8.3 压缩级别决策记录

Brotli 取 **Optimal** 而非 Fastest：Fastest 实测 naive-ui 传输 568.3KB 超出 G-M8 500KB 预算。vendor immutable 强缓存意味着压缩开销只在每个客户端每个版本首次加载时发生一次，Optimal 的额外 CPU 成本可忽略，换取 160KB/客户端的传输节省。Gzip 同样取 Optimal 兜底（不识别 br 的客户端）。中间件顺序：UseResponseCompression 在 UseDefaultFiles/UseStaticFiles 之前。

---

## 9. 性能预算（验收线对照）

---

## 10. 风险与回滚

| 风险 | 等级 | 缓解 |
| :--- | :--- | :--- |
| 推送事件契约漂移/丢失 | 中 | 轮询兜底永远保留（策略降级而非移除）；10s 心跳对账；G-M7 契约门禁 |
| sider transform 模式桌面回归 | 中 | 断点隔离：≥768px 走原 permanent 路径，代码分支不共享；G-M9 三视口 |
| 压缩中间件 NAS CPU 开销 | 低 | Brotli Fastest / Gzip Optimal 起步；静态资源压缩结果可被 OutputCache 缓存（P2 评估）；实测 CPU 前后对比 |
| vendor immutable 陈旧 | 中 | importmap `?v=<vendorVersion>` 版本查询，升级 vendor 必 bump（G-M5 锁定）；过渡期可先 max-age=86400 |
| JobCenter 重构影响 Dashboard/Sources | 中 | 源适配器迁移保持 UnifiedJob 形状不变；jobs.test.mjs 既有 6 用例全绿为合并前提 |
| keep-alive 清理遗漏 | 低 | G-M3 显式断言 deactivated 后定时器数为 0 |
| MapStaticAssets 与嵌入资源管线不兼容 | 低 | 仅 P2 评估项，ResponseCompression 路线不依赖它 |

回滚：P0 纯前端可按文件 revert；P1 推送为加法（事件不订阅即回到轮询态）；P2 压缩/缓存为中间件开关级回滚。

---

## 11. 验收清单（实施时逐项勾选）

- [x] 375×667 真机/模拟器：登录→Dashboard→KB→绑定弹窗→进度抽屉→取消→回滚，全程无横向溢出、无裁切、按钮可达（G-M9 mobile 视口 19 断言全过）
- [x] iOS Safari 地址栏收缩/展开：Chat/Logs/Reader 底部输入区始终可见（100dvh + flex 列布局，G-M5 vh 白名单锁定）
- [x] 桌面 1440×900：现有布局像素级不变（sider permanent、抽屉 520px、grid 原列数）（G-M9 desktop 视口 19 断言全过）
- [x] 推送在线：抽屉进度延迟 ≤200ms，Network 面板无 1.5s 轮询（8-10s 窗口实测 0 次轮询，纯推送驱动）
- [ ] 拔线（DevTools offline）：自动降级 2s 轮询，恢复后对账无状态跳变（逻辑随 JobCenter 断连兜底实现，G-M3 单测覆盖 onclose→2s 路径；浏览器级离线验收未单独执行）
- [x] 切路由/切后台：Network 静默；恢复后 1 次对账请求（DOM 级可见性仿真：hidden 6s 窗口 0 请求；恢复 3s 窗口 2 次=双源各 1）
- [x] 取消竞态：作业恰在取消前完成 → info「作业已结束」+ 终态快照（FSM CANCEL_404 转移，G-M2 单测断言）
- [x] 多 KB 并行：各自卡片忙碌徽标独立（getExactJob 精确匹配，E2E 断言徽标存在）
- [x] curl 静态资源：br/gzip 命中、vendor immutable、首屏 ≤800KB（G-M8 全绿）
- [x] G-M1..G-M9 全绿 + 既有前端用例/核心/Host 门禁不回归（前端 113/113；Host.Tests 457/457；核心 792 中 743 通过、47 跳过、2 失败——`QueryEmbeddingCacheProxy_*` 2 例在干净 HEAD 上同样失败，属存量问题，与本次改动无关）
- [x] P2-5（可选）按约定跳过：MapStaticAssets 与既有静态资源管线不兼容风险低但无收益（ResponseCompression 已达标）；naive-ui tree-shake 与底部 tab bar 属独立改版，另行评审

---

## 12. 参考来源

- [Material Design 3: Breakpoints](https://m3.material.io/foundations/layout/breakpoints)
- [Android Developers: Canonical layouts](https://developer.android.com/develop/ui/views/layout/canonical-layouts)
- [Vue3 + Naive UI 在多端适配项目中的落地经验与踩坑总结（掘金）](https://juejin.cn/post/7502600450945531930)
- [Naive UI 官网](https://www.naiveui.com/) / [n-grid 响应式列数 issue #6277](https://github.com/tusen-ai/naive-ui/issues/6277)
- [VueUse: useBreakpoints](https://vueuse.org/core/usebreakpoints/) / [useMediaQuery](https://vueuse.org/core/usemediaquery/)
- [Hangfire: Tracking the Progress（polling vs push）](https://docs.hangfire.io/en/latest/background-processing/tracking-progress.html)
- [Providing Real-Time Feedback About Long-Running Tasks with SignalR](https://blog.stevanfreeborn.com/providing-real-time-feedback-about-long-running-task-with-signal-r)
- [Real-Time at Last: SSE and SignalR in ASP.NET Core](https://www.binaryintellect.net/articles/d0294aea-cdb8-4bd7-a6b0-b34c5129035f.aspx)
- [Microsoft Learn: Serve static files in ASP.NET Core（.NET 10 MapStaticAssets）](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/static-files?view=aspnetcore-10.0)
- [Milan Jovanović: Response Compression in ASP.NET Core](https://milanjovanovic.tech/blog/response-compression-in-aspnetcore)
- [dotnet/aspnetcore discussions #25938: Static files / compression / caching](https://github.com/dotnet/aspnetcore/discussions/25938)
- [web.dev: The large, small, and dynamic viewport units](https://web.dev/blog/viewport-units) / [Can I use: viewport unit variants](https://caniuse.com/viewport-unit-variants)
- [100dvh vs 100vh: Fix Mobile Viewport Height in CSS](https://modern-css.com/mobile-viewport-height-without-100vh-hack/)
- [Stately: XState 文档](https://stately.ai/docs/xstate)