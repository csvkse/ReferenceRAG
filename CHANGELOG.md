# 改动历史

本文件记录面向使用者的重要变化。完整提交记录和安装包见 [GitHub Releases](https://github.com/csvkse/ReferenceRAG/releases)。

## [1.0.29] - 2026-10-22

### Intel Linux 容器集成 OpenVINO 硬件加速执行提供程序与原生共享库
- **分阶段提取与集成 Linux 原生 OpenVINO 共享库 (`Dockerfile.intel`)**：
  - 新增多阶段构建 `openvino-extract`，从官方 Linux `onnxruntime-openvino==1.24.1` 轮子中精准提取编译了 OpenVINO Execution Provider 的原生共享库（`libonnxruntime.so`、`libonnxruntime_providers_openvino.so`、`libopenvino.so`、`libopenvino_intel_gpu_plugin.so` 等）；
  - 覆盖到 `/app/runtimes/linux-x64/native/` 与 `/app/`，并在系统动态链接器配置 `/etc/ld.so.conf.d/openvino.conf` 中注册并执行 `ldconfig`；
  - 解决 `OrtSessionOptionsAppendExecutionProvider_OpenVINO` 缺失导致的加载失败，正式打通 Intel 核显 / Arc 独显的 ONNX OpenVINO GPU 硬件加速。

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
