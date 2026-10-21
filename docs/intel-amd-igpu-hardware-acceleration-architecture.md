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

---

## 3. 本次代码改进与自愈方案

针对上述根因，系统做出了针对性改进：

### 3.1 增强 Docker 容器 /dev/dri 与渲染节点探测 (`HardwareDetector.cs`)
- 扩展 DRM 设备扫描：不仅匹配 `card*`，同时支持 `renderD*` 节点；
- 增加 `/dev/dri` 降级探测：当 `/sys/class/drm` 不可用时，直接检查 `/dev/dri` 设备节点与 Intel Level Zero / OpenCL（`libze_intel_gpu.so.1`、`libigdrcl.so`）运行库，准确定位 `Intel Graphics (核显 / Arc 独显)` 与 `AMD Radeon GPU / APU`。

### 3.2 接入 OpenVINO 后端回显与精准文案 (`SettingsController.cs`)
- 在硬件后端列表中加入 `Intel OpenVINO (Arc独显 / 酷睿核显)`；
- 精准文案区分核显与独显：
  - 核显：`检测到图形加速硬件: Intel Graphics (核显 / Arc 独显) (集成核显 / 共享内存)，推荐加速方案: OpenVino`；
  - 纯 CPU：`系统未检测到物理加速显卡，已就绪 CPU 高精度运算`。

---

## 4. Docker 部署 Intel 核显最佳实践

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
