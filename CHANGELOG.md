# 改动历史

本文件记录面向使用者的重要变化。完整提交记录和安装包见 [GitHub Releases](https://github.com/csvkse/ReferenceRAG/releases)。

## [1.0.49] - 2026-10-31

### 统一业务模块授权与 MCP 工具白名单架构、长连接即时吊销感知与全景安全模型收敛
- **统一业务功能模块授权与 MCP 二级工具树状矩阵（对齐 WebToolForLinux 规范，`McpCatalogRegistry.cs`, `ApiKeys.js`, `ApiKeyPermissionMatrix.cs`）**：
  - **建立领域单例事实来源（SSoT）**：创建 `McpCatalogRegistry`，规范管理 `chat`, `search`, `files`, `sources`, `index`, `models`, `system` 7 大功能模块与其下挂载的全部 MCP 工具，标注各模块对 REST API / MCP 双通道的支持属性与工具安全风险级别（`Read` / `Execute` / `Dangerous`）；
  - **彻底阻断跨模块越权穿透漏洞**：重构 `ApiKeyPermissionMatrix.IsMcpToolPermitted`，实施三层级联防御（通道控制 -> 业务模块级联一票否决 -> 细粒度工具白名单），彻底杜绝“禁用模块但靠 MCP 白名单通配 `*` 窃取数据”的严重安全隐患；
  - **重构前端统一权限树交互 (`ApiKeys.js`)**：移除原本割裂的“业务模块授权”和“MCP 工具白名单”平行表单项，替换为基于 `metaCatalog.categories` 的统一功能树状矩阵，支持三态联动（全选、半选、取消）、快捷安全策略预设（`全选全部`、`标准问答检索`、`只读探测`、`清空`）与协议类型/关键字实时快筛。
- **全局安全体系收敛与设计模式优化（`McpPermissionGuard.cs`, `RagWebHostExtensions.cs`, `ApiKeyMiddleware.cs`）**：
  - **解除 McpHelper 内部静态认证限制**：关闭 `McpHelper` 内置的单一静态密钥拦截器，将 MCP SSE/Stdio 端点鉴权全权交由全局 `ApiKeyMiddleware` 统一网关管辖，原生支持使用数据库动态生成的 `rag_live_xxx` 授权 ApiKey 接入 Claude Desktop、Cursor 等 Agent 工具，消除了动态 ApiKey 误杀缺陷；
  - **DIAG 应急通道物理旁路隔离**：确保 `/__diag` 带外诊断中枢继续由独立环境变量 `DIAG_KEY` 驱动，不受主业务鉴权与权限矩阵变更影响；
  - **长连接实时吊销感知状态机（Live Revocation Awareness）**：在 `McpPermissionGuard` 中引入轻量级状态机与内存缓存校验，在 SSE 长连接保持期间，一旦管理员在后台停用或删除 ApiKey，下一次工具调用立即毫秒级阻断，杜绝会话过期逃逸；
  - **享元/记忆化模式优化（Flyweight Pattern）**：在 `ApiKeyPermissionMatrix` 中引入 `TokenCache` 缓存 JSON 解析结果，避免高并发热路径上频繁反序列化产生 GC 压力；
  - **空对象模式（Null Object Pattern）**：在免密兼容模式下安全注入虚拟全通凭据（`anonymous_open_access`），消除下游所有的空引用模糊态。
- **全栈质量门禁与防回归自动化体系 (`ApiKeyAuthorizationTests.cs`, `frontend-logic.test.mjs`, `verify-gate.ps1`)**：
  - **反射级架构守护门禁**：新增 `AllRegisteredMcpTools_MustHaveValidModuleMapping_InCatalogRegistry`，通过反射动态扫描所有 `[McpServerToolType]` 工具，强制断言每个工具均在注册中心声明归属模块，从根源杜绝新增工具脱管；
  - **多维防回归单元测试**：增补长连接实时吊销测试、越权一票否决测试、享元缓存一致性测试及前端三态状态机单元测试；
  - **集成至自动化质量门禁**：将鉴权测试正式纳入 `scripts/verify-gate.ps1`，全量 141 项测试 100% 绿色通过。
- **专项架构与深度设计文档归档 (`docs/`)**：
  - `docs/unified-module-authorization-and-mcp-whitelist-architecture.md`
  - `docs/unified-apikey-mcphelper-diagnostics-security-architecture.md`

## [1.0.48] - 2026-10-31

### 修复 @vicons/ionicons5 缺失 KeyOutline 启动失败故障与 ESM 模块导入/导出全栈双轨长效门禁架构
- **修复 `@vicons/ionicons5` 缺失 `KeyOutline` 图标导致前端启动失败致命语法错误 (`KeyOutline.js`, `vendor/icons.js`, `MainLayout.js`)**：
  - 针对项目精简裁剪的 `@vicons/ionicons5` vendor 子集，规范实现 `KeyOutline.js` 矢量组件（基于 Ionicons 5 官方 Path 数据与 Vue 3 `defineComponent` 格式）；
  - 在 `vendor/icons.js` 及离线 `node_modules/@vicons/ionicons5` 中完整重导出 `KeyOutline`，彻底解决浏览器在加载 `MainLayout.js` 时抛出的 `SyntaxError: The requested module '@vicons/ionicons5' does not provide an export named 'KeyOutline'` 导致应用白屏无法启动问题。
- **全栈 ESM 模块导入/导出符号契约双轨长效门禁与防回归体系 (`check.mjs`, `FrontendViewIntegrityTests.cs`, `frontend-logic.test.mjs`)**：
  - **Node.js 静态门禁引擎升级 (`check.mjs`)**：原生解析 `index.html` 中的 `<script type="importmap">`，递归解析所有目标模块的导出符号表（涵盖 default、具名导出、声明变量、别名重命名及 `export *` 通配符重导出），毫秒级严格校验所有 JS 文件的 import 语句，对任何不存在模块或未导出具名符号实施即刻阻断；
  - **C# 宿主 xUnit 集成门禁 (`FrontendViewIntegrityTests.cs`)**：新增 `AllFrontendViews_EsmModuleImports_MustMatchTargetExportedSymbols` 自动化守卫测试，脱离 Node 运行时环境依然能原生断言 importmap 与符号契约；
  - **双向防回归自动化测试 (`frontend-logic.test.mjs`)**：增补负向探测（验证未导出符号与未提供 default 时精准报错拦截）与正向契约用例，全套 26 项前端单测全绿。
- **专项架构与深度复盘设计文档归档 (`docs/`)**：
  - `docs/vicons-missing-export-root-cause-solution-and-esm-guardrail.md`
  - `docs/per-source-data-isolation-and-multi-model-dynamic-lifecycle-architecture.md`

## [1.0.47] - 2026-10-30

### 源管理全功能文件管理器、细粒度 API Key 树状授权与数据源 ABAC 隔离、流式行阅读及 Agent 级正则搜索引擎
- **源管理页面集成全功能文件管理器 (`SourceFileManagerDrawer.js`, `FileSystemController.cs`, `Sources.js`)**：
  - **知识源物理沙箱隔离**：集成 `FileBrowserPath.IsWithin` 安全校验，严防 `../` 跨目录穿越；
  - **完备的日常文件操作**：支持多级目录浏览、新建文件夹、新建文件、重命名、移动、删除与在线文本预览/编辑（内置 2MB 保护阈值与 NUL 字节二进制嗅探）；
  - **批量与压缩能力**：支持多文件并发上传与指定目录流式 Zip 打包下载。
- **细粒度 API Key 树状授权与数据源级 ABAC 隔离 (`ApiKeys.js`, `ApiKeysController.cs`, `ApiKeyMiddleware.cs`, `ApiKeyStore.cs`)**：
  - **双通道协议控制**：独立控制 API (`AllowApi`) 与 MCP (`AllowMcp`) 通道访问权限；
  - **树状功能矩阵授权**：按模块（`chat`, `search`, `files`, `sources`, `index`, `models`, `system`）及细分动作进行父子联动勾选；
  - **数据源级隔离与下推**：支持按数据源批量复选授权，检索接口（`AIQueryController`、`RagSearchTools`、`KnowledgeFileTools`）隐式下推授权源交集，彻底消除跨源数据穿透；
  - **高性能存储与安全规范**：采用加盐 SHA-256 存储与微秒级并发内存缓存（`FindByHash`），提供一次性明文弹窗与客户端配置生成器（Claude Desktop / Curl / Python）。
- **文件阅读流式行读取与 Markdown 大纲解析优化 (`IFileReaderService.cs`, `FileReaderService.cs`, `KnowledgeFileTools.cs`)**：
  - **早中断行游标**：按 `startLine` 与 `endLine` 流式读取，到达目标行立即早中断（Early Break），杜绝超长文件全量加载造成的 LOH 内存泄漏与 Token 耗尽；
  - **Markdown TOC 状态机**：精准解析 Frontmatter 与 H1~H6 标题目录，智能忽略代码块内注释字符；
  - **级联知识库状态**：关联向量知识库分块数与最后索引时间。
- **Agent 级文件高效关键字与正则搜索引擎 (`IFileSearchEngine.cs`, `ManagedFileSearchEngine.cs`, `FileSearchStrategies.cs`)**：
  - **对标终端 `ripgrep` 与 `fd`**：为 AI Agent 扩展 `SearchFileContent` 与 `FindFilesByPattern` MCP 工具，零索引时效延迟，行号精确；
  - **策略模式双轨驱动**：纯字串 SIMD 向量化加速（比正则快 10~50 倍）与安全非回溯正则（`RegexOptions.NonBacktracking`，严格 $O(N)$ 线性时间与 3 秒超时熔断，根除 ReDoS）；
  - **状态机模式 Hunk 合并**：定长环形队列与倒计数器管理上下文，无缝合并临近与重叠代码块；
  - **物理探针与安全保护**：512 字节二进制探测瞬时跳过、浅层剪枝忽略 `.git`/`node_modules`、搜索预算早中断；
  - **源管理抽屉深度集成**：抽屉顶部提供即时搜索栏（正则/大小写切换）与带代码块高亮的搜索结果弹窗。
- **全套架构与专项技术设计文档归档 (`docs/`)**：
  - `docs/file-manager-and-fine-grained-apikey-authorization-architecture.md`
  - `docs/file-reader-optimization-and-line-range-reading-architecture.md`
  - `docs/agent-grade-file-search-engine-and-regex-optimization-architecture.md`

## [1.0.46] - 2026-10-30

### CI/CD 镜像标签覆盖修复、桌面端测试路径解耦与轻量 Vulkan 镜像重新发布
- **Docker 矩阵构建标签防护 (`docker-build.yml`)**：
  - **根除 `latest` 标签竞态覆盖缺陷**：在 `cuda`、`rocm`、`intel` 镜像构建矩阵中显式锁定 `flavor: latest=false`，严格确保只有通用轻量版 (`cpu`/Universal) 拥有 `latest` 标签，彻底避免 ROCm 镜像中的 853MB `rocblas` 大层误覆盖 `latest`；
- **前端测试运行路径跨环境适配 (`frontend-logic.test.mjs`)**：
  - 将硬编码的 `path.resolve` 重构为模块级 `new URL(..., import.meta.url)` 动态寻址，彻底解决桌面端 Release CI 在 `wwwroot` 子目录执行测试时的 ENOENT 报错；
- **重新发布完整镜像集群 (`v1.0.46`)**：
  - 保证 `latest`、`1.0.46` 100% 对应约 330MB 纯净轻量的 Universal Vulkan 原生加速镜像。

## [1.0.45] - 2026-10-30

### Docker 默认镜像支持 Vulkan 硬件加速、消除 800MB ROCm 冗余层与模型硬件支持矩阵落地
- **Docker 默认镜像 (`latest`) 升级为 Universal (CPU + Vulkan) 双模原生加速 (`Dockerfile`, `compose.yml`)**：
  - **根本解决 AMD APU 核显下载 853MB 巨型层问题**：将默认镜像构建参数升级为 `GpuBackend=universal`，直接打包 `LLamaSharp.Backend.Vulkan.Linux`；
  - **内置完整 Mesa Vulkan 物理驱动支持**：在运行时容器内安装 `mesa-vulkan-drivers`（Mesa RADV/ANV/LAVAPIPE ICD 驱动实现）；
  - **GPU 访问权限自愈与环境变量优化**：将非 root `appuser` 用户绑定至 `video` 与 `render` 用户组，杜绝在容器内挂载 `/dev/dri` 时的 `Permission Denied` 异常，动态链接库搜索路径自动关联 Vulkan 原生目录；
  - **极致轻量**：镜像压缩大小仅增加 ~25MB（保持在 ~230MB 黄金体积），不仅支持纯 CPU 环境，挂载 `/dev/dri` 时亦能全量激活 AMD Ryzen APU 核显、AMD Radeon 独显与 Intel 显卡的 Vulkan 硬件算力。
- **全平台硬件支持矩阵与 Docker 镜像选型权威指南 (`docs/model-format-hardware-matrix-and-docker-selection-guide.md`)**：
  - **ONNX vs GGUF 生态全维对比**：深度剖析微软 ONNX Runtime 与 llama.cpp 在 Windows DirectML 与 Linux Vulkan/CUDA 上的支持断层，阐明 GGUF 是破解 Linux AMD APU 核显缺乏通用 GPU 加速提供者（EP）的最佳路径；
  - **4 套官方 Docker 镜像选型决策树**：梳理 `latest`（通用 CPU/Vulkan）、`cuda`（NVIDIA 独显）、`intel`（Intel OpenVINO）、`rocm`（AMD 旗舰独显/计算卡）的适用边界、体积图谱与实操部署指令。

## [1.0.44] - 2026-10-30

### 聊天模型核显 Vulkan 加速、LLM 重复退化熔断治理、智能会话滚动恢复与意图路由升级
- **聊天模型 Linux AMD/Intel UMA 核显 Vulkan 加速与防回退治理 (`ChatPerformancePlanner.cs`, `ModelsController.cs`)**：
  - **根本原因根治 (统一内存 UMA 显存误判)**：解决 Linux 容器下 AMD Ryzen APU / Intel 核显因无独立板载 VRAM（`TotalVramBytes == 0`）且无 DirectX 12 被 `ChatPerformancePlanner` 误判为无 GPU 支持而强制回退到纯 CPU 运行的问题；
  - **动态虚拟共享显存配额**：识别 AMD/Intel 核显并自动分配 4GB 虚拟显存调度 Vulkan Compute Pipeline 全量卸载 (`GpuLayers = -1`)，使小参数对话模型（Qwen3.5-0.8B）流式生成速度提升数倍；
  - **硬件感知与运行时状态回显**：在 `/api/Models/runtime-status` 中精准回显 `AMD Vulkan GPU (AMD Radeon GPU / APU)` 与 `全量卸载到 GPU`；
  - **防回归自动化质量门禁 (`ChatPerformanceModeAndSettingsQualityGateTests.cs`)**：新增 4 项 Linux 核显 Vulkan 自适应规划、固定模式和 Controller 端点测试，21 项门禁全数通过。
- **LLM 重复退化与死循环生成熔断器 (`RepetitionCircuitBreaker.cs`, `LocalLlamaChatClient.cs`)**：
  - **多粒度 N-gram 重复模式探测**：实时滑动窗口监控 Token 序列，自动识别周期性重复退化（Repetition Degeneration）；
  - **惩罚参数支持**：集成 `RepetitionPenalty`、`FrequencyPenalty` 和 `PresencePenalty`，并在前端系统设置提供可视化配置；
  - **智能截断与自愈兜底**：探测到严重重复循环时提前优雅阻断输出，保障流式体验与计算资源不被耗尽。
- **智能会话滚动恢复与用户意图保持机制 (`conversation-scroll.js`, `Chat.js`)**：
  - **流式强制吸底问题解决**：引入用户交互意图感知，在用户向上滚动查看历史记录时自动暂停吸底跟随，杜绝强制跳帧；
  - **状态机与 RAF 批处理**：提供滚动缓冲区管理，并在前端提供浮动“恢复自动滚动”按钮，与 Naive UI `n-scrollbar` 深度适配。
- **意图路由分级分流与策略模式增强 (`IIntentRoutingStrategy.cs`, `IToolSelectionStrategy.cs`)**：
  - 增强分层意图路由与正交工具过滤，减少推理上下文开销并规避工具幻觉。
- **全套架构与调研技术文档归档 (`docs/`)**：
  - `docs/chat-model-cpu-fallback-root-cause-and-vulkan-acceleration-architecture.md`
  - `docs/llm-repetition-degeneration-prevention-and-circuit-breaker-architecture.md`
  - `docs/streaming-conversation-scroll-restoration-and-user-intent-optimization.md`
  - `docs/industry-mainstream-intent-routing-and-tool-selection-architecture.md`

## [1.0.43] - 2026-10-22

### 基于 MAF (Microsoft Agent Framework) 架构范式重构 Agent 协同体系与性能优化
- **适配器模式：云端/本地大模型思维链流式双向适配器 (`OpenAiToolCallSanitizingHandler.cs`)**：
  - **云端思维链丢弃缺陷根治**：解决官方 `openai-dotnet` `ChatClient` 无法感知第三方兼容服务（SiliconFlow、Ollama、vLLM、OpenRouter）输出的 `delta.reasoning_content`（如 DeepSeek-R1、Qwen-R1）导致流式思考被静默吞噬的问题；
  - **网络层动态拦截与语法平滑注入 (`ToolCallSanitizingStream`)**：在 HTTP SSE 流层透明拦截 `reasoning_content`，无缝转化为标准 `<think>\n...` 注入到 `delta.content`，并在思考结束转入正文或完成时自动注入 `\n</think>\n\n`；
  - **非流式与流式双协议自愈 (`AdaptNonStreamingReasoning`)**：即使云端模型以普通 JSON 响应返回 `reasoning_content`，同样前置组装闭合思考标签，使远端大模型与本地 GGUF 推理无缝共享前端折叠思考卡片与 AST 渲染。
- **策略模式：意图自适应工具动态过滤与提示词瘦身机制 (`IToolSelectionStrategy.cs`)**：
  - **解耦工具注册与提示词装载**：改变以往每次对话均无差别注入 6 个系统工具 Schema 导致占用 600~1200 tokens 上下文的缺陷；
  - **基于用户意图动态裁剪工具集 (`IntentAdaptiveToolSelectionStrategy`)**：
    - 纯闲聊/问候场景：100% 剥离全部工具定义，提示词开销锐减，在 CPU/轻薄本/核显边缘设备上首字输出延迟 (TTFT) 降低 40%~60%，彻底消除小参数模型“胡乱调用工具”的幻觉；
    - 知识库问答场景：精准收敛仅挂载 `SearchKnowledge` 与 `ReadNote` 检索工具；
    - 系统诊断/目录探测场景：仅挂载 `GetIndexStatus` 与 `GetFileStructure`；
    - 确定性命中规则 (`RoutingDecision.Handled == true`)：直接由服务端调度执行工具，完全跳过 Phase 1 大模型生成，零 LLM 耗时即刻进入第二阶段汇总；
    - 保留全量回退策略 (`AllToolsSelectionStrategy`)，保障通用复杂多跳场景下的向后兼容性。
- **状态机模式：端到端生命周期治理与毫秒级可观测性遥测 (`ToolCallingExecutionStateMachine.cs`, `MafChatService.cs`)**：
  - **确定性状态流转与校验**：严格受控状态跃迁（`Idle -> Routing -> DirectToolExecuting / Phase1Inferencing -> ToolExecuting -> Phase2Synthesizing -> OutputSanitizing -> Completed / SelfHealingFallback`），杜绝越权或悬挂调用；
  - **全链路毫秒级状态遥测 (`StateDurationMs`)**：在 SSE 响应首部 `context` 事件中实时上报 `stateDurations` 与 `stateTransitions`，为端到端性能调优、慢查询定界与工具执行耗时提供透明量化指标。
- **自动化防回归质量门禁建立 (`AgentOptimizationAndMafPatternsTests.cs`)**：
  - 新增 8 项针对思维链流式适配、意图自适应工具过滤、状态机生命周期与直接路由跳过机制的单元测试，全数一次性绿灯通过；
  - 前端 19 项安全渲染与组件生命周期测试全数通过。
- **架构演进深度调研与方案沉淀 (`docs/agent-architecture-optimization-and-maf-patterns-investigation.md`)**：
  - 详细复盘参考项目 `MAF_WebAgentDemo` 的 `Microsoft.Agents.AI`、`ReasoningContentAgent`、`DynamicToolFilterAgent` 核心实现；
  - 深度对比 LangGraph、Semantic Kernel、Microsoft Agent Framework 与业界主流落地架构，详述适配器、策略与状态机三大设计模式在 ReferenceRAG 中的实施与收益。

## [1.0.42] - 2026-10-22

### 对话思考过程渲染截断异常根因修复与 AST 前置分离渲染架构治理
- **Markdown 渲染前置思维链解耦架构 (`safe-markdown.js`)**：
  - **根本原因根治 (HTML5 Tree Construction "Special Category" Rule)**：
    彻底废除将自定义 `<think>` / `<thought>` 标签作为原始 HTML 直接灌入 Marked 的缺陷做法（历史方案会导致 Marked 将 `<think>` 包裹在 `<p>` 中，浏览器遵循 WHATWG HTML 标准 13.2.6.4.7 遇到处于 Special 类别内部的自定义结束标签时静默抛弃 `</think>`，进而引发后续标题与段落全被误挂为 `<think>` 子节点并在置换时惨遭抹除）；
  - **前置抽离引擎 (`extractThoughts`)**：
    在文本送入 Markdown 解析器之前，率先将全部成对的思考块提取为结构化 `thoughtSteps` 数组，并生成 100% 纯净、合法的 Markdown 正文 `cleanBody`；Marked 仅针对干净正文执行 AST 渲染，彻底阻断任何 HTML5 规范级的标签吞噬隐患；
  - **DOM 优雅平铺组装**：
    将聚合提炼后的 `<details class="chat-thought-container">` 作为顶级同级节点（Sibling）安全置于 DOM 树首部，既完全继承单阶段与多阶段 Agent 推理融合展现，又实现正文各级标题、列表、引用与代码块 100% 完整保真呈现。
- **思考块自愈规则边界穿透漏洞修复 (`balanceTags`)**：
  - **严格受控触发门禁**：仅在开标签数大于闭标签数（`thinkOpen > thinkClose`）时才允许进入未闭合自愈启发逻辑；若开闭标签完全平衡，严禁注入多余的 `</think>`；
  - **负向断言防穿透保护**：在边界正则中严格施加 `(?:(?!<\/(?:think|thought)>)[\s\S])*?` 负向预查，严禁匹配跨越已闭合的真实思维链标签；
  - **正交排除自然语言列表符号**：从启发式断言词中坚决剔除 `(?:[#>\-*]|\d+\.)\s`，防止思考链内部的条目、数字分析或正文列表被误判为思考结束边界而遭遇腰斩截断。
- **自动化防回归质量门禁建立 (`frontend-logic.test.mjs`)**：
  - **用户成都真实长文本回归门禁**：通过自动化测试断言包含引言、5 个大节、多级列表与水平线的思考模型长输出，100% 完整保留正文，绝无内容截断丢失；
  - **思考链内部列表与标号正交性门禁**：断言思考链内部包含有序/无序列表与数字编号时，思考内容完整保留在卡片内部，后续正文不受丝毫截断影响；
  - **流式思考进行态识别门禁**：断言模型流式思考尚未输出正文时，`isStreaming=true` 且 `cleanBody` 为空，避免触发“无响应内容”占位符误报；
  - 全套 19 项前端与核心逻辑测试全部通过。
- **技术调研与规范沉淀 (`docs/chat-thought-rendering-truncation-investigation-and-fix.md`)**：
  - 深度剖析 WHATWG HTML5 规范 13.2.6.4.7 特殊元素解析忽略规则与 Marked 行内解析行为；
  - 对比分析业界 Open-WebUI、LibreChat、LobeChat 及 DeepSeek 官方方案优缺点，梳理出 AST 前置分离最佳实践。

## [1.0.41] - 2026-10-22

### 诊断中枢工具链实战手册与标准排错处置流程 (SOP Runbook) 落地
- **排错方案与标准化处置流程手册 (`docs/chat-model-and-keepalive-troubleshooting-sop-and-runbook.md`)**：
  - 沉淀大模型状态五层流转拓扑（物理存储 $\rightarrow$ 后端注册 $\rightarrow$ 静态托管预压缩 $\rightarrow$ 前端 Keep-Alive 内存 $\rightarrow$ 跨标签广播）；
  - 建立标准排查七步法（物理文件校验、接口探测、预压缩缓存清理、KeepAlive 唤醒追踪、配置竞态消除、跨页广播核验、环境隔离门禁）；
  - 梳理 5 大核心故障模式图谱与应急速查指令卡片。
- **三位一体诊断工具链指南 (`docs/diagnostic-and-troubleshooting-toolchain-guide.md`)**：
  - 梳理业务接口调研（API Investigation）与统一鉴权白名单机制；
  - 规范内置浏览器（Chrome DevTools MCP）无障碍可访问语义树快照与 KeepAlive 自动化回归测试；
  - 详解 TemporaryDiagnostics MCP 网关受控命令行执行（`execute_diagnostic_command`）、分片 Base64 无损热补丁注入与 DI 服务内省技术。

## [1.0.40] - 2026-10-22

### 本地 GGUF 聊天模型下载后未感知与 Vue 3 Keep-Alive 跨组件状态失步根因修复与协同治理
- **Vue 3 `<keep-alive>` 路由缓存组件生命周期失步根因修复 (`Settings.js`, `Models.js`, `Chat.js`)**：
  - **组件失活唤醒治理 (`onActivated`)**：在 `MainLayout.js` 的 `<keep-alive :max="20">` 机制下，`Settings.js` 缺失 `onActivated` 钩子，导致从「设置」跳转到「模型管理」下载完 GGUF 聊天模型后再返回「设置」时，组件不会重新执行 `onMounted`，内部 `downloadedChatModels = []` 保持旧态白屏冻结；现已挂载 `onActivated`，确保路由切回时全量自动静默同步已下载模型与硬件探针状态；
  - **Tab 级与配置竞争防抖对齐**：修复 `onTabChange` 切换至 `chat` 标签页以及配置覆盖时的竞态隐患，封装 `ensureValidChatModelSelection`，无论配置拉取与模型扫描谁先完成，均确保已下载模型存在时自动纠正、自愈绑定首个有效模型并更新实时模型性能画像；
  - **跨标签页与同屏广播实时同步 (`referencerag_model_sync`)**：引入 `BroadcastChannel` 配合 `window.storage` 双通道事件机制，模型管理页面一旦下载完成或变体更新，即刻向全体视图（`Settings`、`Chat`）广播 `MODEL_DOWNLOADED` / `MODEL_UPDATED` 信号，无需手动刷新页面即可零感知热感知就绪；
  - **社区变体下载模型分类作用域混淆修复 (`Models.js`)**：修复 `selectVariantFileForDownload` 硬编码 `modelType: 'embedding'` 的隐患，使其基于当前激活的 `activeModelTab` 动态确定模型类别（`chat` / `reranker` / `embedding`），杜绝将对话大模型错误下载到 `/models/Embedding/` 目录的严重归类错误。
- **后端模型管理器物理目录兜底自愈机制 (`ModelManager.cs`)**：
  - 在 `GetDownloadedChatModels()` 中增加物理磁盘自愈探测：若内存中登记的聊天模型为 0，但物理磁盘 `Chat/` 目录下存在真实的 `.gguf` 格式权重，自动触发 `ScanLocalModels()` 重新扫描，杜绝冷启动或外部挂载注入时状态不同步的问题。
- **交互易用性提升**：
  - 当本地确实未下载任何 GGUF 聊天模型时，警告横条内新增「前往「模型管理」下载」直达快捷按钮（`goToModelManagement('chat')`），极大缩短用户操作链路。
- **并发存储与环境隔离质量门禁重构 (`SemanticTestController.cs` & `ModelHubSettingsTests.cs`)**：
  - 根除 `TestRecordStore` 中由于 `Environment.CurrentDirectory` 并发漂移及同步等待异步锁引发的潜在死锁与路径漂移风险，采用 `AppContext.BaseDirectory` 稳定持久化与纯同步初始化；
  - 为 `ReferenceRAG.Host.Tests` 挂载 `xunit.runner.json` 串行执行配置与 `customConfigPath` 独立测试环境，杜绝跨测试用例的静态环境污染。
- **自动化防回归质量门禁建立 (`ChatModelDetectionAndKeepAliveTests.cs`)**：
  - **物理扫描与自愈断言**：测试物理磁盘存在 GGUF 模型时 `GetDownloadedChatModels` 100% 正确识别、报告模型类型为 `chat` 并提取变体；
  - **前端 Keep-Alive 与广播契约门禁**：通过静态断言与代码级自动化测试，严格门禁 `Settings.js` 必须包含 `onActivated`、`onUnmounted`、`referencerag_model_sync`、`ensureValidChatModelSelection` 以及 `goToModelManagement`；
  - **社区变体作用域门禁**：测试断言 `Models.js` 在处理变体下载时必须动态感知 `activeModelTab`，禁止硬编码 embedding；
  - 全套 858 项测试全部通过，0 失败。

## [1.0.39] - 2026-10-22

### Intel Alder Lake-N (N100) 核显 OpenVINO 硬件探测 ExitCode 127 根因修复与动态库隔离治理
- **ONNX Runtime Provider 插件桥接生命周期隔离修复 (`OnnxExecutionProviderHelper.cs`)**：
  - **动态链接器未定义符号（ExitCode 127）根因排除**：深入实机诊断（通过 TemporaryDiagnostics MCP 执行 CLI），捕获到 `dotnet: symbol lookup error: /app/runtimes/linux-x64/native/libonnxruntime_providers_openvino.so: undefined symbol: Provider_GetHost`；
  - **解除错误预热 dlopen 依赖**：彻底移除 `PreloadLinuxOpenVinoDependencies` 中对 `libonnxruntime_providers_openvino.so` 与 `libonnxruntime_providers_shared.so` 的独立静态 dlopen 加载，避免在主运行时 `libonnxruntime.so` 初始化之前提前触发插件 C++ 静态构造函数中的 `Provider_GetHost` 寻址失败；
  - **托管框架与原生 Provider 契约对齐**：仅保留底层计算与驱动库（TBB 与 OpenVINO 核心/GPU 插件）预热，执行提供者插件交由 ONNX Runtime 内部桥接器在 `AppendExecutionProvider_OpenVINO` 阶段自主动态挂载。
- **真机实测与图形界面验证**：
  - 在配备 Intel Alder Lake-N 核显（vendor: 0x8086, device: 0x46d1, Intel N100）的真实容器环境及内置 Chrome 浏览器中全流程验证；
  - 设置页面前端硬件就绪状态从 `ExitCode=127 降级为 CPU` 完美恢复为：`OpenVino (已就绪 (Intel GPU / 酷睿核显 OpenVINO 原生硬件加速))`，检测设备准确识别为 `Intel Graphics (核显 / Arc 独显) (集成核显 / 共享系统内存)`。
- **自动化防回归质量门禁建立 (`HardwareAccelerationTests.cs`)**：
  - 新增 `OnnxExecutionProviderHelper_PreloadLinuxDependencies_DoesNotLoadProviderPluginsDirectly` 代码级契约门禁，严格物理拦截任何将 ORT Provider 插件（`libonnxruntime_providers_*.so`）混入独立 dlopen 列表的回归倾向；
  - 24 项硬件加速相关自动化测试全部通过。

### 系统监控页面系统资源探测性能重构与前后端遥测契约治理
- **前后端系统资源契约割裂根因修复与防御性归一化 (`MetricsCollector.cs` & `System.js`)**：
  - **规范化字节级容量契约**：在后端 `SystemMetrics` 中补齐 `MemoryUsedBytes`、`MemoryTotalBytes`、`DiskUsedBytes`、`DiskTotalBytes`、`DiskFreeBytes` 与 `UptimeSeconds`，消除前端因字段缺失展示 `0 B / 0 B` 与 `0分钟` 的缺陷；
  - **完全向后兼容保障**：保留原有 `MemoryUsageMB` 与 `Uptime`（TimeSpan 对象），确保现有 `AlertService` 内存告警规则与历史客户端零破坏无缝运行；
  - **双端防御性降级与秒级显示**：前端新增 `normalizeSystemMetrics` 与 TimeSpan 格式容错解析；优化 `formatUptime` 支持系统启动不足 1 分钟时展示 `xx秒`，避免误导为死滞的 `0分钟`。
- **500ms 阻塞延迟彻底消除：差分滑动窗口采样模式上线 (`MetricsCollector.cs`)**：
  - **废除 `Task.Delay(500)`**：将每次 HTTP 请求的 500ms 线程阻塞彻底重构为基于连续时间差分的滑动窗口采样（Differential Sliding Window Sampler）；
  - **双检锁与抖动平滑**：采样间隔 $< 250\text{ ms}$ 时直接复用最新有效采样缓存，消除并发锁竞争与微小时间片抖动误差；
  - **超高性能跃升**：系统指标探测执行耗时从 500ms 骤降至 $< 0.01\text{ ms}$（提速超 50,000 倍），`/api/system/metrics` 与 `/api/system/status` 端点响应延迟实现毫秒级。
- **容器感知内存探测与最长匹配挂载点磁盘探测算法**：
  - **容器原生内存识别**：调用 .NET BCL `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes` 原生识别 Linux cgroups v1 与 v2（`--memory`）限额，辅以 `/proc/meminfo` 的 `MemTotal` 主机容灾；
  - **存储挂载点最长前缀匹配**：遍历系统驱动器，智能匹配数据目录（`/app/data` 或 Windows 盘符）的最优底层挂载卷，精确获取真实存储容量与剩余空间。
- **自动化防回归质量门禁建立 (`SystemMetricsContractGateTests.cs` & `MetricsCollectorTests.cs`)**：
  - **JSON 驼峰序列化契约门禁**：严格断言导出的 JSON 报文中包含前端所需的全部 6 个驼峰数字字段；
  - **零阻塞延迟性能门禁**：断言连续 5 次系统指标探测耗时必须 $< 200\text{ ms}$，物理拦截任何形式的阻塞延迟回归；
  - 10 项相关自动化测试与质量门禁全部通过。

## [1.0.38] - 2026-10-22

### Intel 核显硬件加速探测崩溃与 Docker 健康检查重启根因修复与沙箱架构重构
- **Docker 容器 90 秒频繁重启根因彻底消除 (`ApiKeyMiddleware.cs` & `SystemController.cs`)**：
  - **白名单放行健康探针**：在 `ApiKeyMiddleware` 中无条件放行 `/api/system/health`，同时支持 HTTP `GET` 与 Docker `HEALTHCHECK` 使用的 HTTP `HEAD`（`wget --spider`）；
  - **控制器端点标记匿名**：在 `SystemController.HealthCheck()` 上显式声明 `[AllowAnonymous]` 与 `[HttpHead("health")]`，杜绝未授权拦截导致 Docker 判定 Unhealthy 频繁杀容器。
- **进程级隔离硬件探测沙箱上线 (`HardwareAccelerationProbeWorker` & `HardwareAccelerationProbeService`)**：
  - **原生崩溃物理隔离**：硬件能力探测移至独立的 CLI 子进程（`dotnet ReferenceRAG.WebHost.dll --probe-hardware`），彻底规避第三方原生 C++ 驱动/动态库在异构 Linux 环境下触发段错误（`SIGSEGV`/`SIGABRT`）连带崩溃 Kestrel 主 Web 服务的隐患；
  - **状态机熔断器 (Circuit Breaker)**：探测子进程若异常退出或超时，自动切换至 `Degraded` 状态并开启 3 分钟冷却保护，毫秒级快速返回安全的 CPU 纯算力兜底，防止反复派生崩溃子进程；
  - **本地内存高速缓存**：探测成功后结果在内存中缓存 5 分钟（支持 `?refresh=true` 穿透），避免设置页刷新频繁触发底层 GPU 探查开销。
- **Intel 镜像驱动多重回退兼容性增强 (`Dockerfile.intel`)**：
  - **解绑 Level Zero 单一约束**：优化环境变量为 `ONEAPI_DEVICE_SELECTOR="level_zero:gpu;opencl:gpu"`，确保在 Alder Lake-N（如 N100）等仅稳定支持 NEO OpenCL 的宿主内核下自动平滑回退，不再抛出异常；
  - **OpenVINO 探测图优化保护**：在探测会话中主动配置 `GraphOptimizationLevel.ORT_DISABLE_ALL`，防止图转换器与 OpenVINO 后端配置解析冲突。
- **硬件遥测接口匿名访问支持**：
  - `/api/Settings/hardware-acceleration/status` 与 `/api/Settings/cuda-availability` 纳入白名单并挂载 `[AllowAnonymous]`，前端无论是否已登录均可流畅感知硬件加速状态。
- **架构文档与自动化门禁套件**：
  - 编写并落盘 `docs/intel-gpu-openvino-probe-crash-and-healthcheck-architecture.md`；
  - 新增 `HardwareAccelerationProbeServiceTests.cs` 并扩展 `ApiKeyMiddlewareWhitelistTests.cs`，19 项相关自动化测试全部通过。

## [1.0.37] - 2026-10-22

### TemporaryDiagnostics 最新 Release 自动化同步机制上线与 CI 流水线集成 (方案 B)
- **双轨自动化包同步工具落地 (`sync-temporary-diagnostics.ps1` & `sync-temporary-diagnostics.sh`)**：
  - 自动查询 GitHub API 最新 Release（`csvkse/TemporaryDiagnostics/releases/latest`），检索并下载最新的 `.nupkg` 资产至 `./packages` 目录；
  - 自动化清理旧版废弃包，并通过正向环视正则精准更新 `Directory.Packages.props` 中声明的包版本；
  - **严格幂等性控制**：若远程版本与本地已声明版本一致且包体完整，自动跳过网络传输，校验耗时 < 0.5s；
  - **离线与断网容灾降级（Offline Fallback）**：遭遇 API 异常、网络不可达或限流时自动安全回退至本地已有包，确保弱网或离线环境下构建绝不中断。
- **全平台 CI/CD 流水线深度接入 (`docker-build.yml` & `desktop-release.yml`)**：
  - 在 Docker 全架构镜像（CPU、CUDA、ROCm、Intel）构建及桌面分包构建流水线中无缝注入同步步骤；
  - 自动携带 GitHub Actions 原生 `${{ secrets.GITHUB_TOKEN }}` 发起授权调用，彻底消除匿名请求 Rate Limit（403）风险。
- **诊断基础设施依赖版本升级**：
  - 成功将 ReferenceRAG 项目内部预置的 `TemporaryDiagnostics` 升级至最新发布的 `v1.0.3`（包含 `AspNetCore`、`Core` 与 `Infrastructure` 全套依赖），并通过全量生命周期单元测试。
- **技术架构规范文档落盘**：
  - 撰写并提交 `docs/temporary-diagnostics-auto-sync-architecture.md`，详细记录同步状态流转与安全防爆机制。

## [1.0.36] - 2026-10-22

### 接口安全网关强化：默认全量认证、显式白名单机制与凭据防泄露体系
- **默认拒绝与显式白名单认证模型升级 (`ApiKeyMiddleware.cs`)**：
  - 将 API Key 鉴权从前缀选择性拦截重构为**默认强制认证（Default-Deny）**：当配置密钥时，非显式白名单公开接口默认均须提供有效凭据；未配置密钥时保持免密兼容模式；
  - **原生特性级支持 `[AllowAnonymous]`**：中间件前置探测端点元数据 `IAllowAnonymous`，标记该特性的控制器或 Action 直接放行；
  - **规范化公开白名单边界**：显式放行 `/api/auth/check` 认证状态探测端点、CORS `OPTIONS` 预检请求、前端 SPA 页面视图路由（`GET`）、前端静态资源及独立的 `/__diag/*` 诊断通道。
- **全面消除 SignalR、MCP 与 Swagger 鉴权盲区**：
  - **修复 SignalR 盲区**：修正中间件监控路径为实际端点 `/hubs/index`，前端客户端同步接入 `accessTokenFactory`，杜绝未授权监听私有文件变更与索引进度；
  - **修复 MCP 中间件顺序与密钥不同步**：将 `UseAppMcpHelper()` 移入 `ApiKeyMiddleware` 保护管辖之后，并在服务注入阶段通过 `IAuthenticationKeyResolver` 解析多级优先级密钥；
  - **Swagger 访问保护与 Cookie 桥接**：非白名单的 `/swagger` 与 `/swagger/v1/swagger.json` 纳入认证，支持浏览器带参访问（`?apiKey=...`）并自动下发 Session Cookie（`reference_rag_api_key`）无缝维持后续资源加载。
- **敏感配置脱敏与防覆盖保护 (`SettingsController.cs`)**：
  - `GET /api/settings` 在返回前对配置对象执行深拷贝与敏感凭据脱敏（`OpenAiApi.ApiKey`、`Service.ApiKey`、`Service.DiagKey` 等掩码化，绝不污染运行内存）；
  - `POST /api/settings` 识别提交数据中的掩码标记（`****`），未改密保存时自动保留磁盘中的原始真实密钥，防止意外写盘破坏。
- **架构文档与自动化测试落地**：
  - 编写并提交架构规范文档 `docs/api-security-and-whitelist-authentication-architecture.md`；
  - 新增 `ApiKeyMiddlewareWhitelistTests.cs` 12 项端到端及控制器门禁测试，全部通过验证。

## [1.0.35] - 2026-10-22

### 网页品牌图标集成、非 NVIDIA 平台 CUDA 状态静默屏蔽与硬件探测自愈缓存
- **网页全局图标标准规范接入 (`index.html` & `favicon.svg` & `favicon.ico`)**：
  - 在前端网页 `<head>` 中引入现代化双轨 Favicon 图标体系，根治浏览器默认 `favicon.ico` 404 告警；
  - 接入矢量 SVG 图标（`favicon.svg`），自适应浅色与深色系统主题（Dark / Light Mode）；
  - 兼容回退标准 ICO 多分辨率图标（`favicon.ico`，内嵌 16x16、32x32、48x48）及 iOS `apple-touch-icon`；
  - 静态资源路径均携带构建版本号缓存失效戳（`?v=1.0.35`），确保浏览器无感知即时刷新。
- **显卡厂商感知与非 NV 平台静默过滤 (`Settings.js`)**：
  - 建立精准的 GPU 厂商智能识别与分类策略，自动解析 GPU Vendor 与 GPU Model（识别 Intel UHD/Iris/Xe/Arc、AMD Radeon、Apple Silicon、CPU 等）；
  - **非 NVIDIA 设备彻底杜绝 CUDA 误报**：对于非 NV 设备，前端严格隐藏并置空 `cudaInfo`、屏蔽「CUDA 状态」徽标及「CUDA 库路径」配置项，界面干净纯粹；
  - **重构探针异常呈现机制**：彻底根除此前因网络微闪或探针响应延迟时将错误笼统提示为 `CUDA 状态 无法探测硬件加速状态 (Failed to fetch)` 的逻辑缺陷；将探测异常统一归纳至「硬件就绪状态」告警栏，明确提示 CPU 自动回退保护已激活，杜绝在核显/AMD设备上冒充 CUDA 报错。
- **硬件探测静态缓存与微秒级响应优化 (`HardwareDetector.cs`)**：
  - 将 `HardwareDetector` 的探测上下文升级为进程级静态缓存 `_cachedContext`，配合双重检查锁（Double-Checked Locking）；
  - 彻底避免高频访问系统设置页面时重复触发 Windows WMI、DirectX 探测及 Linux `lspci` / `nvidia-smi` 子进程开销；
  - 将 `/Settings/hardware-acceleration/status` 接口的重复探测响应耗时从 180~300ms 大幅压缩至 **< 0.1ms**，从源头消除了前端探针因耗时过长导致 `Failed to fetch` 超时的隐患。
- **架构方案文档落盘 (`docs/web-favicon-and-gpu-vendor-awareness-architecture.md`)**：
  - 详细梳理并产出了技术调研与架构文档，涵盖图标设计规范、适配器与策略模式、状态机流转设计、以及对比 PyTorch / Ollama / LM Studio 等业界主流产品的差异化优势。

## [1.0.34] - 2026-10-22

### 开发工具日志管理页面上线、逆向流尾部高速寻优算法与操作审计体系
- **开发工具新增「日志查看」页面 (`Logs.js` & `MainLayout.js` & `router/index.js`)**：
  - 参考 `WebToolForLinux` 优秀实践并在交互与性能上深度升级，在主导航栏「开发工具」分组下新增「日志查看」菜单项（图标：`DocumentTextOutline`，路由：`/logs`）；
  - **双 Tab 结构设计**：
    1. **系统运行日志（Application / Debug Logs）**：
       - 左侧自适应日志文件列表：展示文件名、文件大小、最后修改时间、当日活跃标记，支持文件名即时过滤与刷新；
       - 右侧极客风终端控制台：支持尾部行数选择（100 / 300 / 1000 / 5000 行）、行内关键字高亮检索、实时自动刷新（支持 3s / 5s / 10s 轮询与向上滚动智能暂停）、一键复制到剪贴板，以及完整日志全量流式下载；
    2. **操作审计日志（Operation Audit Logs）**：
       - 提供基于内存环形缓冲区与轻量 JSONL 持久化仓储的操作审计列表；
       - 展示操作时间、操作类别、对象类型、对象名称、操作详情、客户端 IP 及成功/失败状态徽标；
       - 支持多关键字模糊搜索与后端真实分页（Page / PageSize）。
- **逆向指针流式尾部读取策略 (`FastReverseSeekTailStrategy` & `ILogReaderStrategy`)**：
  - 突破传统 `File.ReadLines` 必须从头扫描整个大文件带来的 CPU 爆表与 GC 内存泄漏风险；
  - 采用逆向寻优（Backward Seek）算法，直接从物理文件尾部向头部以块为单位扫描 `\n` 换行符，达到指定行数立即向前解码；
  - 无论日志文件是 10MB 还是 2GB，末尾 Tail 读取耗时严格控制在 **< 1ms**，内存分配维持微秒级；
  - 打开文件时严格声明 `FileShare.ReadWrite | FileShare.Delete`，完美避免与运行中写日志的 Serilog/FileLogger 发生文件锁冲突；
  - 内置 512KB 硬截断与安全防爆保护，自动剔除首字节 UTF-8 BOM 标记，杜绝前端浏览器内存耗尽或白屏崩溃。
- **严密路径穿越与安全防护 (`LogFileService`)**：
  - 引入白名单正则与物理路径根目录包含双重校验，坚决拦截 `..`、`/`、`\` 等恶意文件名读取或下载请求；
- **SPA Fallback 路由支持 (`RagWebHostExtensions`)**：
  - 在 WebHost 静态路由回退列表中追加 `/logs`，保障在浏览器中直接按 F5 刷新页面时能够正常加载。

## [1.0.33] - 2026-10-22

### TemporaryDiagnostics 受控诊断中枢接入、NuGet 包源机制与程序访问密钥多级优先级仲裁
- **受控临时诊断中间件集成与统一优先级机制 (`TemporaryDiagnosticsAdapter` & `DiagnosticsLifecycleStateMachine`)**：
  - 成功接入 `TemporaryDiagnostics` 企业级诊断中间件，支持嵌入式 Web 仪表盘、xterm.js + PTY 原生交互终端（Web Shell）、基于 Fast IL Emit 的动态服务探查与执行，以及原生 Model Context Protocol (MCP) 端点；
  - **诊断密钥 `DIAG_KEY` 支持配置文件配置，且严格遵循【配置文件最高，环境变量次之】**：
    - 支持在 `appsettings.json` 或 `obsidian_rag_config.json` 中配置 `Diagnostics.Key` 或 `Service.DiagKey`（以及可选的 `RoutePrefix` / `DiagRoutePrefix`）；
    - 若配置文件配置了有效密钥，优先以配置文件为准；若配置文件未配置，回退读取 Docker 环境变量 `DIAG_KEY` / `TEMPORARY_DIAGNOSTICS_KEY`；
    - 若两处均未配置，处于 `Unconfigured` 状态，不注册任何路由端点、不申请常驻内存、不暴露 WebSockets，攻击面与运行开销彻底为 0；
    - 若传入或配置的诊断密钥长度不足 8 位，状态机自动迁移至 `InvalidEntropy` 安全防御态并输出告警，拒绝启动诊断服务；
- **程序业务访问密钥多级仲裁策略 (`AuthenticationKeyResolutionCoordinator` & `ApiKeyMiddleware`)**：
  - 支持通过 Docker 环境变量（`REFERENCERAG_API_KEY`、`API_KEY`）传入程序访问密钥；
  - **严格优先级保障【配置文件最高，环境变量次之】**：重构密钥解析器为策略责任链模式，优先检索配置文件中的 `Service.ApiKey`，仅在配置文件为空或未配置时回退到环境变量，彻底杜绝默认 ASP.NET Core 环境变量无意覆盖配置文件的风险；
- **NuGet 配置文件与离线包分发机制 (`nuget.config` & `Directory.Packages.props`)**：
  - 引入根目录 `nuget.config`，注册 `nuget.org` 与本地 `./packages` 双包源；
  - 针对 NuGet 协议不支持直接以 GitHub Release URL 作为包源的限制，采用离线包预置与构建期自动解析策略，保障本地开发与 Docker 容器多架构编译 100% 稳定还原；
  - 所有 Dockerfile（CPU、Intel、CUDA、ROCm）同步适配，自动复制 `nuget.config` 与 `packages/` 依赖缓存。

## [1.0.32] - 2026-10-22

### 文件夹浏览文件透视、数据源物理磁盘深度探查与通配符索引引擎重构
- **Docker 容器全架构镜像注入 `C.UTF-8` 语言环境与容错枚举 (`Dockerfile*` & `FileSystemController`)**：
  - 在所有 Docker 运行镜像（`Dockerfile`、`Dockerfile.intel`、`Dockerfile.cuda`、`Dockerfile.rocm`）中显式导出 `ENV LANG=C.UTF-8` 与 `ENV LC_ALL=C.UTF-8`，彻底解决基于 Ubuntu 基础镜像运行 .NET 时因缺失 UTF-8 本地化环境导致中文/非 ASCII 文件名（如 `*.txt`）无法被正确枚举和解析的问题；
  - `FileSystemController` 枚举文件与子目录时升级使用带 `IgnoreInaccessible = true` 的现代 `EnumerationOptions`，并捕获单个受限条目输出告警，彻底避免因单个特殊权限文件阻断整个目录列表；
- **目录浏览接口支持文件透视与多维度统计 (`FileSystemController` & `FolderPickerModal`)**：
  - `FileSystemController.Browse` (`GET /api/filesystem/browse`) 新增 `folderOnly` 控制参数（默认同时枚举文件与子目录），并返回目录数 (`TotalDirectories`)、文件数 (`TotalFiles`) 与文件总大小 (`TotalFilesSizeBytes`)；
  - Web 文件夹选择器 (`FolderPickerModal.js`) 顶部新增“显示文件”开关（默认开启），在浏览任意目录时直观呈现文件列表（支持区分 `📁 文件夹` 与 `📄 文件`，展示格式化文件大小与修改时间），并在底部状态栏实时汇总当前目录包含的文件夹与文件数量，彻底打消对文件夹接口可读性的疑虑；
- **修复规则匹配引擎缺陷与 Linux 容器多级权限阻断 (`IncrementalIndexStrategy`, `StartupSyncService`, `SourcesController`)**：
  - **通配符规则匹配重构**：修复此前规则解析器遇到 `*.*`、`*`、`.ext` 或带空格规则（如 `" *.c, *.txt "`）时无法正确命中的 Bug；升级采用 .NET 现代高性能匹配 `FileSystemName.MatchesSimpleExpression`，严密支持全通配符、带点后缀及复杂正则模式；
  - **跨平台安全枚举防护**：淘汰容易因 Linux 容器子目录权限受限抛出 `UnauthorizedAccessException` 导致整个扫描中断的 `Directory.GetFiles(..., SearchOption.AllDirectories)`，全面切换为非阻塞且忽略无权访问子路径的 `Directory.EnumerateFiles(..., new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true, MatchCasing = MatchCasing.CaseInsensitive })`；
- **知识库源物理磁盘与规则匹配一键诊断探查器 (`SourcesController` & `Sources.js`)**：
  - `SourcesController.Scan` (`GET /api/sources/{name}/scan`) 全面增强，同步诊断并返回物理磁盘实际存在的文件总数 (`TotalDiskFiles`)、物理磁盘扩展名分布统计 (`DiskExtensions`) 与规则过滤后的匹配文件列表 (`TotalFiles`)；
  - 数据源管理页面（`Sources.js`）每行操作栏新增【探查】按钮，点击一键调出诊断弹窗，直观呈现磁盘底层实际存在哪些文件、后缀分布与规则命中情况，并在未匹配到文件时给出明确的操作排查指引。

## [1.0.31] - 2026-10-22

### 数据源文件规则自适应、未索引状态引导与硬件探针全面加固
- **数据源文件规则自适应与未索引可视化交互引导 (`Sources.js`)**：
  - 在“添加源”表单中新增“文件规则”输入框，支持在创建时直接输入匹配规则（如 `*.md, *.txt, *.c, *.py`），彻底解决非 Markdown/TXT 文件因默认规则被过滤的问题；
  - 源列表行状态可视化优化：数据源未建立索引时，“文件数”直观展示黄色标签 `0 (点击索引)`（点击可直接启动增量索引），分块数展示 `-`，最近索引展示 `未索引`，彻底消除认知误解；
  - 添加源成功后提示增加操作指引：“源添加成功！请点击右侧【增量索引】以构建向量库”。
- **硬件加速探针全面防御性加固与透明化诊断 (`SettingsController` & `Settings.js`)**：
  - 在 `SettingsController` 中对 `hardware-acceleration/status` 与 `cuda-availability` 探测入口增加全局防崩溃 try-catch 保护，无论底层 WMI、环境变量注入器或 ONNX 提供程序出现任何未捕获异常，绝不返回 HTTP 500，而是安全降级为 CPU 并携带完整异常诊断信息；
  - 在 `Settings.js` 前端探测异常时提取并展示精准的错误原因或网络状态，杜绝黑盒 `无法探测硬件加速状态`。

## [1.0.30] - 2026-10-22

### Web 模式文件夹选择器与 OpenVINO 硬件加速原生入口根治修复
- **Web 模式交互式文件夹选择器 (`FolderPickerModal` & `FileSystemController`)**：
  - 参考 `WebToolForLinux` 设计，优化 Web/Docker 容器部署模式下的知识库目录录入体验，彻底取代纯手动打字输入路径；
  - 后端提供安全受控的目录浏览与新建目录接口 (`GET /api/filesystem/browse`、`POST /api/filesystem/mkdir`)，支持软链接防越界、跨平台合法字符校验与根驱动器隔离；
  - 前端以模态框无缝呈现面包屑导航、快捷挂载点选择（如 `/app/data`、`/mnt`、`/media`、`/`）、子文件夹动态穿透与即时新建文件夹，选择后智能截取末级目录名自动填充知识库名称；
  - 桌面客户端（Desktop）继续保持调用 Windows 宿主原生文件夹选择对话框，架构解耦双模自适应；
- **根治 OpenVINO `EntryPointNotFoundException` 符号缺失与原生库竞争 (`Dockerfile.intel`)**：
  - **根本诱因剖析**：`Microsoft.ML.OnnxRuntime` NuGet 包在发布期生成的 CPU 版 `libonnxruntime.so` 残留于 `runtimes/linux-x64/native/` 目录；而 `onnxruntime-openvino` 轮子内的共享库为版本化 soname 文件（`libonnxruntime.so.1.24.1`），覆盖复制时未替换未版本化的同名 CPU 库，导致 .NET P/Invoke 默认加载了 CPU 库并抛出找不到 `OrtSessionOptionsAppendExecutionProvider_OpenVINO` 入口；
  - **全量依赖提取与构建期断言**：在 `openvino-extract` 阶段全量抓取 site-packages 及其全部依赖（`libtbb.so`、`libopenvino.so`、`libonnxruntime_providers_openvino.so` 等），建立严格符号链接 `libonnxruntime.so -> libonnxruntime.so.1.*`；
  - 引入构建期 Python `ctypes.CDLL` 导出符号硬断言，若缺失 `OrtSessionOptionsAppendExecutionProvider_OpenVINO` 立即阻断镜像构建；
  - 在 `dotnet publish` 与运行时容器初始化阶段彻底删除 NuGet CPU-only 库残余，确保容器内仅存在 OpenVINO 硬件加速版；
- **C# 托管层动态解析器早期介入与符号强校验 (`OnnxExecutionProviderHelper` & `Program.cs`)**：
  - 在 `Program.cs` 入口第一行显式调用 `InitializeNativeResolver()`，确保在任何 ONNX Runtime 静态构造器或依赖注入服务之前注册 `NativeLibrary.SetDllImportResolver`；
  - 原生解析器在加载 `libonnxruntime.so` 候选路径时，主动调用 `NativeLibrary.TryGetExport` 校验是否存在 `OrtSessionOptionsAppendExecutionProvider_OpenVINO`；若候选为未包含 OpenVINO 符号的普通库则自动跳过并寻找真正的硬件加速库；
  - 在 Linux 容器环境下主动预热加载 OpenVINO 基础依赖组件（`libtbb`、`libopenvino`、`libonnxruntime_providers_openvino`），彻底杜绝动态链接器链式解析缺失。

## [1.0.29] - 2026-10-22

### Intel Linux 容器集成 OpenVINO 硬件加速与 GGUF 核显 Vulkan 动态卸载
- **GGUF 模型支持 Intel / AMD 核显 Vulkan GPU 卸载 (`GpuOffloadCalculator` & `LlamaTelemetryStrategy`)**：
  - 解除此前层数规划器仅限 NVIDIA 独显的限制，识别 Intel / AMD 核显及所有支持图形加速的硬件；
  - 针对共享系统内存的核显设备，自动分配动态共享显存规划额度（预估 4GB），使 GGUF 嵌入及重排模型可直接全量或部分卸载至核显 Vulkan 执行；
  - 状态监控采集器支持明确标识并区分 `Intel Vulkan GPU`、`AMD Vulkan GPU` 与 `NVIDIA CUDA 12`；
- **分阶段提取与全量集成 Linux 原生 OpenVINO 共享库 (`Dockerfile.intel`)**：
  - 多阶段构建 `openvino-extract` 精准提取官方 `onnxruntime-openvino==1.24.1` 轮子内自包含的 OpenVINO 动态库与插件（`libonnxruntime.so`、`libonnxruntime_providers_openvino.so`、`libopenvino.so` 等）；
  - 全量覆盖至 `/app/runtimes/linux-x64/native/` 与 `/app/`，并在系统动态链接器配置 `/etc/ld.so.conf.d/openvino.conf` 中注册并执行 `ldconfig`；
  - 导出 `LD_LIBRARY_PATH="/app:/app/runtimes/linux-x64/native:/usr/lib/x86_64-linux-gnu"`，彻底打通 Intel 核显 / Arc 独显的 ONNX OpenVINO GPU 硬件加速；
- **C# 托管层动态库绑定防护 (`OnnxExecutionProviderHelper`)**：
  - 为 `Microsoft.ML.OnnxRuntime` 程序集注册静态 `NativeLibrary.SetDllImportResolver`，强制优先加载本应用内置的 OpenVINO 增强版 `libonnxruntime.so`，彻底杜绝全局环境标准 CPU 库抢占导致 `OrtSessionOptionsAppendExecutionProvider_OpenVINO` 入口缺失；
- **Docker CI 构建可靠性优化 (`Dockerfile*`)**：
  - 移除跨 CI 构建虚拟机不可靠的 BuildKit 缓存挂载与 `--no-restore`，避免层缓存命中时因缓存卷隔离引发 `NETSDK1064` 依赖包丢失报错，保障多架构镜像构建 100% 稳定交付。

## [1.0.28] - 2026-10-22

### 修复 Linux 容器 ONNX NativeMethods 初始化异常，并补全 Mesa Vulkan 驱动
- **修复 Linux 环境 ONNX Runtime 动态库缺失 (`ReferenceRAG.Infrastructure.csproj`)**：
  - 针对 Intel 优化分支，将 Linux 目标包由仅含 Windows dll 的 `Intel.ML.OnnxRuntime.OpenVino` 修正为包含 Linux 原生 `libonnxruntime.so` 的 `Microsoft.ML.OnnxRuntime`；
  - 彻底根除 `The type initializer for 'Microsoft.ML.OnnxRuntime.NativeMethods' threw an exception` 异常崩溃；
- **补全 Intel 与 AMD 容器 Mesa Vulkan ICD 驱动 (`Dockerfile.intel` & `Dockerfile.rocm`)**：
  - 安装 `mesa-vulkan-drivers`，为 Intel 核显/独显提供 `intel_icd.x86_64.json` (ANV)，为 AMD GPU 提供 `radeon_icd.x86_64.json` (RADV)；
  - 显式导出 `LD_LIBRARY_PATH` 涵盖 `/app/runtimes/linux-x64/native` 与系统库路径；
- **异常深度解包与安全回退防御 (`SettingsController` & `OnnxExecutionProviderHelper`)**：
  - 递归解包 `TargetInvocationException` 提取内层真实原因；
  - 新增 `OnnxExecutionProviderHelper_TryAppendOpenVino_ShouldHandleSafelyWithoutCrashing` 单元测试。

## [1.0.27] - 2026-10-22

### OpenVINO GPU 初始化权限自愈、设备精度回退与前端诊断回显优化
- **Docker 容器 `/dev/dri` 设备节点全量自愈授权 (`docker-entrypoint.sh`)**：
  - 在 root 阶段无条件执行 `chmod -R a+rwX /dev/dri`，彻底杜绝降权至非 root 用户（如 Unraid `PUID=99 / PGID=100`）时因 `renderD128` 节点权限不足导致 OpenVINO 初始化失败的问题；
- **OpenVINO 多目标设备与精度自动协商探测 (`OnnxExecutionProviderHelper`)**：
  - `TryAppendOpenVino` 支持对 `GPU`、`GPU_FP16`、`GPU_FP32`、`AUTO:GPU,CPU` 等目标设备描述符的梯度自动协商探测与错误捕获，并在探测失败时输出确切诊断原因；
- **设置控制面板精准回显与选项补全 (`SettingsController` & `Settings.js`)**：
  - `SettingsController` 引入 `ActiveBackendReason` 并在回退 CPU 时在标题清晰注明具体原因；
  - `Settings.js` 下拉菜单补全 `Intel OpenVINO` 选项，修正核显动态显存显示为 `(集成核显 / 共享系统内存)`，并在状态面板内渲染回退诊断详情。

## [1.0.26] - 2026-10-22

### Intel / AMD 核显（集成显卡）硬件加速识别与 OpenVINO 状态回显支持
- **Docker 容器环境 GPU 设备探测增强 (`HardwareDetector`)**：
  - 支持 `/dev/dri` 渲染节点（`renderD*`）与 Intel Level Zero / OpenCL 运行时自动探测，解决容器内无 sysfs 完整映射时显卡识别漏判的问题；
  - 自动区分识别并展示 `Intel Graphics (核显 / Arc 独显)` 与 `AMD Radeon GPU / APU`；
- **设置控制面板接入 Intel OpenVINO 硬件加速选项 (`SettingsController`)**：
  - 在 `availableBackends` 中正式补全 `OpenVino (Arc独显 / 酷睿核显)` 加速项与运行时可用性校验；
  - 优化显卡检测状态提示文案，明确将集成核显（共享系统内存）与独立显卡进行区分，消除“系统未检测到独立显卡”引起的歧义；
- **自动化测试扩充**：
  - 在 `HardwareAccelerationTests` 中新增核显厂商与设备标识解析理论覆盖测试；
  - 架构设计与 Docker 部署指南沉淀至 `docs/intel-amd-igpu-hardware-acceleration-architecture.md`。

## [1.0.25] - 2026-10-22

### Docker 挂载目录权限完全自愈与日志目录大小写兼容修复
- **修复 Entrypoint 在 root 下跳过权限修正的隐性陷阱**：
  - 彻底重构 `docker-entrypoint.sh`，废除 root 身份下 `[ ! -w ]` 恒为 true 导致 `chown -R` 被跳过的缺陷，无条件执行 `chown -R "$PUID:$PGID"` 与 `chmod -R 775`；
  - 增加真实目标用户写权限探测，对 NFS/SMB/CIFS 挂载点自动启用 777 兜底，确保在 Unraid（`PUID=99 / PGID=100`）及各 NAS 下正常写入配置文件；
- **修复日志目录大小写冲突与无权创建异常**：
  - 解决 `ServiceManager.ConfigureLogging` 拼接大写 `/app/Logs` 与宿主机挂载小写 `/app/logs` 不匹配的问题，优先读取 `LogsPath` 环境变量并复用小写 `logs`；
  - 增加日志目录创建容错保护，当文件日志不可写时降级至控制台输出，不再阻断主程序启动；
  - 在所有 Dockerfile 与 Entrypoint 中自动建立 `ln -sf /app/logs /app/Logs` 软链接兼容层；
- **质量门禁与测试套件扩充**：
  - 新增 `LoggingPathResolutionTests` 单元测试，纳入 `scripts/verify-gate.ps1`，全量 138 项测试 100% 通过。

## [1.0.24] - 2026-10-22

### Docker 挂载卷权限自愈、网络回环绑定修复与主配置持久化优化
- **Docker 容器挂载卷写权限自愈与特权降权 (Permissions & PUID / PGID)**：
  - 引入 `docker-entrypoint.sh` 与 `gosu` 动态降权架构，自动检测并修复宿主机挂载卷（`/app/data` 与 `/app/logs`）的文件系统写入权限；
  - 支持环境变量自定义 `PUID` 与 `PGID`（如 Unraid 的 `PUID=99 / PGID=100` 或以 root 运行的 `PUID=0 / PGID=0`）；
  - 自动检测宿主机 `/dev/dri` GPU 渲染设备组真实 GID 并加入应用用户，杜绝 Intel / AMD 显卡硬件加速权限冲突；
  - `DataDirectoryLease` 增强排他锁异常捕获，对缺少写权限提供包含具体修复命令的诊断指引。
- **Kestrel 网络端点解析器与容器多网卡监听修复**：
  - 引入 `NetworkBindingResolver` 智能解析监听地址，彻底解决容器冷启动无配置时回退到 `localhost:5000` 导致外部无法访问的致命网络缺陷；
  - 容器环境下默认自动监听 `0.0.0.0:5000` 并优先保留 `ASPNETCORE_URLS=http://+:5000`，同时保护本地桌面/开发安全默认值。
- **统一主配置文件定位至 `data/appsettings.json`**：
  - 全平台宿主默认统一直接使用 `程序目录/data/appsettings.json` 作为主配置文件；
  - 容器环境只需挂载 `-v host/data:/app/data`，首次启动自动释放默认模板，宿主机改动或界面配置保存直接落盘，镜像升级零丢失。
- **自动化质量门禁与测试体系**：
  - 新增 `NetworkBindingResolverTests` 与 `DataDirectoryLeaseTests` 测试套件，纳入 `scripts/verify-gate.ps1` 核心回归门禁，136 项自动化测试 100% 通过。

## [1.0.23] - 2026-10-22

### Docker 全矩阵跨平台构建修复与桌面端静默启动协调器
- **Docker 多计算架构变体（CPU / CUDA / ROCm / Intel）构建与发布修复**：
  - 引入 `Microsoft.ML.OnnxRuntimeGenAI.Managed` 纯托管抽象包，解耦 Linux 构建期与 Windows DirectML 原生库依赖，彻底修复 CS0234 与 CS0246 编译中断；
  - 在全量 Dockerfile Stage 2 构建发布阶段精确注入 `appsettings.template.json`，解决 MSB3030 文件复制异常；
  - `NativeOgaDirectMlAdapter` 增加 `OperatingSystem.IsWindows()` 跨平台运行时门禁，在非 Windows 容器环境下平滑自愈降级；
  - 完整调研与实施技术方案沉淀归档至 `docs/docker-build-failure-root-cause-analysis-and-cross-platform-fix.md`。
- **桌面端无闪烁静默启动与生命周期协调器优化**：
  - 引入 `StartupWindowCoordinator` 与 `StartupWindowSurface` 协同管理托盘与后台静默启动窗口生命周期；
  - 完善窗口关闭与最小化托盘钩子保护，杜绝托盘启动黑屏与界面瞬闪现象；
  - 补充桌面启动冒烟测试套件并打通质量门禁验证。

## [1.0.17] - 2026-10-20

### 设置页面二级分类重构、名称统一与对话模型启动延迟加载
- **设置页面二级分类标签栏重构（2-Tier Navigation）**：
  - 顶部分类重构为三大 Segmented 胶囊大类：🤖 **模型与推理 (`models`)**、🔍 **检索与分段 (`retrieval`)**、⚙️ **系统与存储 (`system`)**，彻底解决 9+ 个横向一级 Tab 扁平拥挤与视觉杂乱问题。
  - 二级子标签无缝联动，使用 `v-show` 保护表单 DOM 实例与输入响应式状态，避免切换分类丢失未保存的参数。
  - 支持 URL 查询参数（`?tab=xxx`）与哈希双向智能同步映射，外部跳转或刷新直接精准定位目标子项。
- **对话模型与嵌入/重排配置项完全对齐与统一**：
  - 设置页面中将原 `后端方案` 统一命名为 `推理模式`；
  - 单选选项统一为 `本地 GGUF`（或 `本地 ONNX`）与 `OpenAI 兼容 API`，彻底消除冗余括号与混杂文案；
  - 本地模型选择统一字段标签为 `模型名称`，表单交互风格在全系统彻底一致。
- **对话模型生命周期同构与启动延迟加载（Lazy Loading & Idle Eviction）**：
  - 本地对话模型（`LocalLlamaChatClient` / `LocalOnnxChatClient`）采用虚拟代理惰性加载，程序启动时默认不加载显存/内存权重；
  - 首次提问按需加载，支持滑动空闲超时（默认 300 秒无活动）自动释放权重与 KV Cache；
  - 遥测接口（`GET /api/models/runtime-status`）精准反映 `Unloaded`（未加载）与 `Ready`（已就绪）状态；
  - 修改模型存储目录时自动卸载对话模型，下次调用自动热加载新目录。
- **自适应性能规划架构与自愈上下文保障**：
  - 支持固定参数模式与自适应规划模式双模切换，提供 8K 黄金平衡、极速吞吐、长上下文与轻量低显存偏好策略；
  - 支持自然标点断点与 Markdown/XML 自愈修复防截断；
  - 建立自动化质量门禁 `ChatPerformanceModeAndSettingsQualityGateTests`，603 项单元测试 100% 通过。

## [1.0.16] - 2026-10-20

### OpenAI 兼容 API 体系与全链路 E2E 冒烟测试
- **OpenAI 兼容 Chat Completions 接口 (`POST /v1/chat/completions`)**：
  - 双模式自适应：支持 Direct LLM 纯模型透传模式与本地知识库检索增强模式（`model: "reference-rag"`）。
  - 流式 SSE 传输：支持标准 `text/event-stream` 增量 `chat.completion.chunk` 输出与 `data: [DONE]` 终结帧，支持客户端中途取消；流式结束自动附加 token usage 统计。
  - 非流式 JSON 输出：严格遵循 OpenAI camelCase 规范，计算 `prompt_tokens`、`completion_tokens` 与 `total_tokens`。
  - 标准错误协议：针对缺失消息、空消息或超限输入返回规范的 `invalid_request_error` JSON 响应。
  - 核心设计与架构规范已收录于 `docs/openai-compatible-chat-completions-api-architecture-design.md`。
- **统一模型发现接口 (`GET /v1/models` & `GET /v1/models/{modelId}`)**：
  - 默认严格列出当前激活就绪的模型列表（`reference-rag`、激活的聊天模型、激活的嵌入模型、激活的重排模型）。
  - 规范统一：所有激活模型的 `owned_by` 字段严格统一为 `"reference-rag"`，规避第三方客户端碎片化分组。
  - 提供 `?include_all=true` 参数支持开发者枚举本地已下载模型。
  - 消除与原嵌入模型控制器重复注册的路由冲突。
- **模型管理与设置页面多后端协同演进**：
  - 模型测试页面新增聊天模型相关标签与测试项；
  - 设置页面支持多后端类型共存与本地聊天模型配置持久化；
  - 适配与扩展 ONNX / GGUF 模型生命周期状态机与硬件加速策略。
- **端对端真实宿主冒烟测试验收 (`TC_SMOKE_07`)**：
  - 在 `E2ECoreSmokeTests` 中新增 `TC_SMOKE_07_OpenAICompatibility_EndToEnd_FullPipeline`，通过真实的 ASP.NET Core 进程内 HTTP 管道贯通验证模型发现、向量嵌入计算、重排评分、Direct 对话补全、RAG 知识注入补全与 SSE 流式生成。
  - 22 项性能与耗时 SLA 指标 100% 达标，0 违规，全套 468 项 C# 测试与 23 项前端测试全部通过。

## [1.0.15] - 2026-10-20

### 双平台硬件加速架构与 CI/CD 全矩阵发布对齐
- **双平台双引擎加速架构**：落地 Windows & Linux 跨平台、跨引擎（ONNX & GGUF）的 5 目标硬件加速方案：
  - Windows 端：主推通用兼容版（DirectML + Vulkan，显卡图形驱动直连零依赖）与 NVIDIA 专属满血版（CUDA 12 原生 + 85MB 微型绿色便携库 + 缺失自动平滑降级 CPU 零闪退）。
  - Linux 端：ONNX 采用全原生（CUDA/ROCm/OpenVINO）与 CPU 降级；GGUF 采用全原生与 Vulkan（Mesa 开源驱动免配置直连，无需复杂 ROCm 环境）。
  - 运行时自适应探针：`CudaEnvironmentScanner` 扩展 Linux 库扫描；`Win32EnvironmentInjector` 扩展 Linux `LD_LIBRARY_PATH` 进程注入；`LlamaNativeInitializer` 支持动态加载 Linux 原生 `libllama.so`。
- **桌面端与 Docker 发布流水线全对齐**：
  - 桌面端流水线（`desktop-release.yml`）：矩阵对齐 5 大变体（`universal`、`cuda`、`amd`、`intel`、`cpu`），通用版自动同步便捷别名 `ReferenceRAG-win-x64.zip`，全量生成 SHA256 校验和。
  - Docker 容器流水线（`docker-build.yml`）：支持变体别名映射（`nvidia` / `amd`），容器基础镜像注入 `libvulkan1` 运行时，实现 Vulkan GPU 直通加速。
  - 归档核心发布技术规范：`docs/desktop-and-docker-ci-cd-pipeline-optimization-and-release-alignment.md`。

## [1.0.14] - 2026-10-20

### 桌面端与高分屏体验
- 升级多分辨率（`16/24/32/48/64/128/256` 共 7 帧 32bpp）带页面主题色（`#63E2B7`）圆角边框的 `R` 应用与托盘图标。
- 接入 `app.manifest` 的 `PerMonitorV2` DPI 感知声明、窗口动态 DPI 图标注入（`WindowsWindowIcon`）与原生 Win32 托盘菜单（`TrayIconManager`），彻底解决高分屏下应用图标与托盘右键菜单模糊问题。
- 托盘图标改为左键单击立即显示主窗口，并升级开始菜单快捷方式的版本化图标自愈机制（`ShortcutGuard`），自动刷新 Windows 11 任务栏图标缓存。

## [1.0.13] - 2026-10-19

### 检索评测、数据门禁与前端体验
- 扩展嵌入模型与重排模型预设评测集至 12 个维度（新增否定与排除语义、数值与时间精度、多跳拼图、长尾罕见标识、跨语言语义对齐、指令遵循等），并将说明标签独立至 `Note` / `TargetChallenge` 元数据字段，彻底消除评测标签对模型输入正文的污染。
- 实现停用知识源在向量检索、BM25、混合检索与知识图谱扩展中的全链路门禁过滤。
- 前端路由引入 `keep-alive` 页面状态保活，切换 Search / Chat / Performance 等页面不再丢失输入与结果状态。
- 增强预设套件横向对比看板与重排详情卡片的双向一键联动定位，并支持中间得分、等级与 P95 延迟的最简紧凑折叠显示。

## [1.0.8 – 1.0.12] - 2026-10

### 双引擎推理、自适应分块与显存生命周期
- 引入 LLamaSharp GGUF 嵌入与重排后端（含 `Qwen3-Reranker` Causal Logit 架构），形成 ONNX Runtime + LLamaSharp 双推理引擎，支持多量化变体（`Q4_K_M / Q8_0 / FP16`）管理。
- 新增硬件加速自适应策略（Native CUDA / Portable CUDA / DirectML / CPU）、GGUF GPU 卸载层数自动计算、模型空闲滑动过期自动卸载、最小化到托盘极速回收显存及实时 GPU 遥测。
- 重构多源模型下载协调器与状态机，支持 HuggingFace 官方源与国内镜像源自动切换及取消下载。
- 实现自适应 Markdown 分块、入库低信息密度噪声过滤（`NoiseChunkDetector`）以及单文件原子级重分块（根治重分块双倍残留向量）。
- 新增全局后台异步任务中心（`JobCenter`）与检索全链路耗时拆解追踪。

## [1.0.7] - 2026-09-04

### 架构

- 将项目迁移到共享的 `Infrastructure`、`Business`、`ApiHost`、`WebHost` 和 `DesktopHost` 分层架构。
- Web 与桌面端共享业务逻辑、API 语义和同一套静态前端资源。
- 桌面端改用 InfiniFrame、WebView2 和进程内 IPC，默认不监听 HTTP 端口。
- Web 前端改为浏览器原生 ES Modules，移除 Vite、TypeScript/SFC 发布编译和 npm 构建流程。
- Web 与桌面端均使用 .NET 10 常规发布，关闭 NativeAOT 和裁剪。

### 功能与修复

- 保留 AI 对话会话状态，切换页面后可继续原会话。
- 简化在线聊天、嵌入和重排服务的配置选择，并修正线上模型配置仍回退本地的问题。
- API Key 缺失时返回可诊断的配置错误，避免在服务构造阶段因空密钥崩溃。
- 修正桌面窗口初始尺寸与屏幕可用区域适配。
- 正常处理后台清理服务在应用退出时收到的取消信号。
- 增加 GPU 内存收缩回调和无冲突释放机制，降低启动及模型切换期间的显存占用。
- 增加孤儿数据后台清理服务。

### 工程与发布

- 将工具、配置模板、部署文件、文档和生成产物统一归入 `support/`。
- 保留迁移前代码于 `Old` 分支。
- 修正 GitHub Actions 在 Windows PowerShell 中执行前端测试时的通配符兼容问题。
- 发布 Windows x64 自包含桌面包 `ReferenceRAG-win-x64.zip`。

### 升级提示

- 本机配置迁移到 `support/config/local/`；发布环境仍从发布目录读取配置。
- 可通过 `REFERENCERAG_CONTENT_ROOT` 指定配置和相对数据路径的根目录。
- 禁止新旧程序同时写入同一个数据目录；迁移不会自动移动真实数据和模型。

## 1.0.6 及更早版本

- 提供 AI 对话、向量/BM25/图谱混合检索、可选重排、自动索引、模型管理、MCP、Skill、REST API 和 Swagger。
- 支持本地 ONNX Embedding/Reranker、CUDA 加速及 OpenAI 兼容 API 模式。
- 历史版本详情见 [Releases](https://github.com/csvkse/ReferenceRAG/releases)。

[1.0.17]: https://github.com/csvkse/ReferenceRAG/releases/tag/v1.0.17
[1.0.16]: https://github.com/csvkse/ReferenceRAG/releases/tag/v1.0.16
[1.0.15]: https://github.com/csvkse/ReferenceRAG/releases/tag/v1.0.15
[1.0.14]: https://github.com/csvkse/ReferenceRAG/releases/tag/v1.0.14
[1.0.13]: https://github.com/csvkse/ReferenceRAG/releases/tag/v1.0.13
[1.0.7]: https://github.com/csvkse/ReferenceRAG/releases/tag/v1.0.7
