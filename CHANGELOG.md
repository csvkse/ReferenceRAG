# 改动历史

本文件记录面向使用者的重要变化。完整提交记录和安装包见 [GitHub Releases](https://github.com/csvkse/ReferenceRAG/releases)。

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

[1.0.16]: https://github.com/csvkse/ReferenceRAG/releases/tag/v1.0.16
[1.0.15]: https://github.com/csvkse/ReferenceRAG/releases/tag/v1.0.15
[1.0.14]: https://github.com/csvkse/ReferenceRAG/releases/tag/v1.0.14
[1.0.13]: https://github.com/csvkse/ReferenceRAG/releases/tag/v1.0.13
[1.0.7]: https://github.com/csvkse/ReferenceRAG/releases/tag/v1.0.7
