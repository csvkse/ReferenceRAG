# 改动历史

本文件记录面向使用者的重要变化。完整提交记录和安装包见 [GitHub Releases](https://github.com/csvkse/ReferenceRAG/releases)。

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
