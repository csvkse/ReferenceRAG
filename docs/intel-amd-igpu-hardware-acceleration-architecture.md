# ReferenceRAG: Intel & AMD 核显（集成显卡）硬件加速架构与 Docker 识别指南

## 1. 概述与结论

**ReferenceRAG 原生、全面支持 Intel 与 AMD 的核显（Integrated GPU / APU）加速。**

支持的核显涵盖但不限于：
- **Intel 平台**：
  - 第 11 ~ 14 代 Core 酷睿处理器集成的 **Intel Iris Xe Graphics**、**Intel UHD Graphics**；
  - 最新 Core Ultra（Meteor Lake / Lunar Lake / Arrow Lake）集成的 **Intel Arc Graphics 核显**；
  - Intel Arc A 系列独立显卡（A380、A750、A770 等）。
- **AMD 平台**：
  - Ryzen 6000 / 7000 / 8000 / AI 300 系列 APU（Radeon 680M / 780M / 880M / 890M 等）；
  - AMD Radeon RX 系列独立显卡。

---

## 2. 状态提示“未检测到独立显卡，当前就绪后端: Cpu”的原因剖析

当用户在 Unraid / 群晖 / Linux 宿主机的 Docker 容器日志或界面中看到：
```text
系统未检测到独立显卡，已就绪 CPU 高精度运算
当前就绪后端: Cpu | 自动回退保护: 开启
```
其本质原因有四点：

### 2.1 文案设计的词义偏差（“独立显卡” vs “集成核显”）
在原先代码实现中，显卡探测通过检查 GPU 专用显存（VRAM）：
- 独立显卡（Discrete GPU）具有板载独立 GDDR 显存（如 8GB、16GB）；
- 集成显卡（Integrated GPU / 核显）与 CPU 共享系统物理内存（动态显存，专用显存通常报告为 0 或数十 MB）。
原有的回显文案硬编码写成了 `"系统未检测到独立显卡"`，让使用者误以为系统不支持核显，实际上原意是“未发现加装专用显存的插卡式独立显卡”。

### 2.2 Docker 容器内的设备探测盲区
此前 `HardwareDetector.DetectLinuxGpuDevices()` 仅读取 `/sys/class/drm/card*`。
在很多 Docker 部署环境中，宿主机通过 `--device=/dev/dri` 将渲染设备传入容器，但容器内的 `/sys` 虚拟文件系统并未完整挂载，或者宿主暴露的是 `/dev/dri/renderD128`。这导致容器内的系统信息探测未能正确提取显卡芯片名称。

### 2.3 设置控制器此前未注册 OpenVINO 后端
在 `SettingsController.cs` 中，`availableBackends` 仅硬编码注册了 `Auto`、`Cuda`、`DirectML`、`Cpu`，缺少了针对 Intel 镜像的 `OpenVino`。因此即便底层的向量嵌入和重排早已在通过 OpenVINO 硬件加速，前端状态接口仍回退显示 `active: "Cpu"`。

### 2.4 双计算引擎的架构特性与“自动回退保护”
ReferenceRAG 拥有两套独立的计算引擎：
1. **向量检索与重排引擎（Embedding / Rerank，基于 ONNX Runtime）**：
   - **Linux / Docker**：在 `ghcr.io/csvkse/referencerag:intel` 镜像中，内置 `Intel.ML.OnnxRuntime.OpenVino` 与 Intel Level Zero 原生运行时。挂载 `--device=/dev/dri` 后，ONNX Runtime 直接调用核显 GPU 核心执行高并发向量运算。
   - **Windows**：AMD 与 Intel 核显均通过微软 **DirectML** 驱动实现全自动 GPU 加速。
2. **对话大模型引擎（Chat LLM，基于 llama.cpp / LLamaSharp）**：
   - 基于 GGUF 模型，跨平台通过 Vulkan 或多核 CPU 向量指令集运行。
   - 在 Docker 容器中，如果宿主机未配置 Vulkan ICD 映射，ReferenceRAG 的 **“自动回退保护”** 会主动触发，将大模型计算平滑降级至 CPU AVX2 / AVX-512，避免任何崩溃（SegFault），同时向量检索依然享受核显硬件加速。

### 2.5 选择“自动 (Auto)”后为何仍显示“就绪 CPU”？
当在设置页面中选择“自动感知协商 (Auto)”时，系统会按 `CUDA -> OpenVINO -> DirectML -> CPU` 的顺序探查并初始化最高优先级硬件。如果选择 Auto 后依然回退到 CPU，底层主要涉及两个关键因素：
1. **Docker 容器 `/dev/dri/renderD128` 节点权限不足 (EACCES)**：
   在 Unraid 或 NAS 默认以 `PUID=99 / PGID=100`（非 root 用户）运行容器时，宿主机挂载进来的 `/dev/dri/renderD128` 设备文件默认权限为 `0660` 且属组为宿主机的 render/video 组。容器进程降权至 99:100 后，因无法读写 GPU 设备节点导致 Level Zero / OpenVINO 初始化抛出权限被拒异常，进而触发自动回退保护降级为 CPU。
2. **Linux 下 ONNX 运行时原生库加载异常 (NativeMethods TypeInitializationException)**：
   NuGet 官方的 `Intel.ML.OnnxRuntime.OpenVino` 包**仅内置了 `win-x64` 的 Windows 动态库**，并未包含 Linux 版本的 `libonnxruntime.so`。在 Docker 镜像构建清理 Windows 库后，导致容器内完全缺少 ONNX Runtime 基础共享库，从而在初始化时抛出 `The type initializer for 'Microsoft.ML.OnnxRuntime.NativeMethods' threw an exception`。

---

## 3. 本次代码改进与自愈方案

针对上述根因，系统做出了针对性改进：

### 3.1 补充 Linux 原生 ONNX 库与 Mesa Vulkan 驱动 (`ReferenceRAG.Infrastructure.csproj` & `Dockerfile.intel`)
- 在 Linux 构建目标下统一引用 `Microsoft.ML.OnnxRuntime`，确保 `libonnxruntime.so` 稳定分发到容器 `/app/runtimes/linux-x64/native/`；
- 在 `Dockerfile.intel` 和 `Dockerfile.rocm` 中引入 `mesa-vulkan-drivers`，为 Intel 核显（ANV 驱动）与 AMD APU（RADV 驱动）提供原生的 Vulkan ICD 支持，确保大模型（LLaMA / Qwen）能直接跑在核显/独显上；
- 设置全局 `LD_LIBRARY_PATH` 环境变量包含 `/app/runtimes/linux-x64/native`，彻底解决 `DllNotFoundException` / `TypeInitializationException`。

### 3.2 增强 Docker 容器 /dev/dri 与渲染节点探测 (`HardwareDetector.cs`)
- 扩展 DRM 设备扫描：不仅匹配 `card*`，同时支持 `renderD*` 节点；
- 增加 `/dev/dri` 降级探测：当 `/sys/class/drm` 不可用时，直接检查 `/dev/dri` 设备节点与 Intel Level Zero / OpenCL（`libze_intel_gpu.so.1`、`libigdrcl.so`）运行库，准确定位 `Intel Graphics (核显 / Arc 独显)` 与 `AMD Radeon GPU / APU`。

### 3.3 设备权限自愈与降权解耦 (`docker-entrypoint.sh`)
- 容器启动入口以 root 执行时，执行 `chmod -R a+rwX /dev/dri 2>/dev/null || true`，无论宿主机挂载的用户组是什么，均自愈放行设备节点读写权限；
- 动态获取 `/dev/dri/*` 设备的 GID 并在容器内将 `appuser` 加入同 GID 组，确保后续降权到非 root 用户（如 99:100）时具备完整的设备读写权限。

### 3.4 接入 OpenVINO 后端回显与精准文案 (`SettingsController.cs`)
- 在硬件后端列表中加入 `Intel OpenVINO (Arc独显 / 酷睿核显)`；
- 精准文案区分核显与独显：
  - 核显：`检测到图形加速硬件: Intel Graphics (核显 / Arc 独显) (集成核显 / 共享系统内存)`；
  - 若回退 CPU，标题与诊断详情直接输出底层原因（如权限不足或设备未就绪）。
- `OnnxExecutionProviderHelper.TryAppendOpenVino` 增加对 `GPU`、`GPU_FP16`、`GPU_FP32`、`AUTO:GPU,CPU` 等精度的阶梯式探测与自愈，并展开内层异常提示。

---

## 4. 常见疑问解答 (FAQ)

### Q1: `chmod -R a+rw /dev/dri` 必须在宿主机执行吗？
**不需要**。
在 `v1.0.28+` 镜像中，容器启动入口 `docker-entrypoint.sh` 会在以 root 启动时先自愈放行容器内的 `/dev/dri` 设备读写权限，然后再安全降权至非 root 用户（如 Unraid 的 `99:100`）。用户无需在 Unraid 或宿主机上每次手动执行该命令。

### Q2: 为什么其它容器（如 Plex、Jellyfin、Ollama）不需要在宿主机 chmod？
1. **Ollama / vLLM 等容器**：
   默认直接以 **root (UID 0)** 身份运行。Linux DAC 权限检查（如 `0660`）对 root 进程完全不限制，因此不需要任何 chmod 即可直接读写显卡设备。
2. **LinuxServer.io 系列（Jellyfin、Plex 等）**：
   启动脚本中包含设备 GID 探查逻辑，在启动阶段动态读取 `/dev/dri/renderD128` 的宿主 GID，在容器内创建同 GID 组，并将运行用户加入该组；或者直接通过入口脚本自愈授权。

### Q3: 为什么加了 `--privileged`（特权模式）仍然会报权限不足？
- `--privileged` 给予容器的是 **内核能力（Capabilities）** 和设备挂载权限（绕过 cgroups / AppArmor / seccomp 设备黑白名单限制）；
- 但是，**Linux 标准的文件系统权限控制（DAC: Discretionary Access Control）仍然在起作用**！
- 宿主机上的 `/dev/dri/renderD128` 默认权限通常是 `0660`（属主 root 可读写、属组 render/video 可读写，其它用户没有任何读写权限）；
- 当容器降权至普通非 root 用户（如 `PUID=99 / PGID=100`）运行，且该用户不在属组中时，Linux DAC 校验依然会无情地拒绝访问并返回 `EACCES (Permission denied)`。这就是为什么必须在 entrypoint 降权前进行设备授权或组对齐。

---

## 5. Docker 部署 Intel 核显最佳实践与排查

### 4.1 临时快速修复（无需等待镜像更新）
若当前容器正在运行且显示“就绪 CPU”，可在宿主机直接对设备授权即可热生效：
```bash
# 在宿主机终端执行，为核显渲染节点开放读写权限：
chmod -R a+rw /dev/dri
# 或在容器内直接授权：
docker exec -it -u 0 reference-rag chmod -R a+rw /dev/dri
```
刷新设置页面即可立即看到 OpenVINO GPU 就绪。

### 4.2 标准运行命令

在 Unraid / 群晖 / 任何 Linux 宿主机上运行 Intel 核显加速时，推荐命令如下：

```bash
docker run -d \
  --name reference-rag \
  --device=/dev/dri \
  -p 5123:5000 \
  -v /mnt/disk1/domains/Docker/dc/referencerag/data:/app/data \
  -v /mnt/disk1/domains/Docker/dc/referencerag/models:/app/models \
  -v /mnt/disk1/domains/Docker/dc/referencerag/logs:/app/logs \
  -e PUID=99 \
  -e PGID=100 \
  --restart unless-stopped \
  ghcr.io/csvkse/referencerag:intel
```

> **注意要点**：
> 1. 务必保留 `--device=/dev/dri`，这是容器访问核显计算单元的通道；
> 2. ReferenceRAG 容器内置的 `docker-entrypoint.sh` 会在启动时自动探测 `/dev/dri/*` 设备的宿主 GID，并动态加入组中，无需在宿主机额外更改设备权限；
> 3. 配置文件自动存放在宿主机挂载的 `data/appsettings.json`，升级镜像不丢失。
