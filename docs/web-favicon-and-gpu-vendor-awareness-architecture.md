# 前端网页图标接入与多厂商显卡硬件状态感知架构设计

> **状态**：已评审并进入工程执行  
> **文档位置**：`docs/web-favicon-and-gpu-vendor-awareness-architecture.md`  
> **涉及模块**：`Business/wwwroot` (Web 前端) / `HardwareDetector` / `Settings.js` / `SettingsController`

---

## 一、 需求背景与痛点剖析

### 1. 前端网页缺乏标识图标 (Favicon)
- **现象**：浏览器标签页、收藏夹及 PWA/快捷方式中，ReferenceRAG 前端页面显示为默认的空白地球图标，缺乏品牌辨识度与现代 Web 应用的专业质感。
- **目标**：在 `<head>` 中规范化注入网页图标，支持现代高分屏矢量 SVG 与传统浏览器 `.ico` 双重适配。

### 2. 硬件加速探针与显卡类型混淆（非 NV 误报 CUDA 状态）
- **现象**：在 Intel 核显（如 N100、Core Ultra、Iris Xe）、Arc 独显、AMD Radeon 或无独显纯 CPU 环境下，当网络抖动或探针接口短暂重连时，设置页直接弹出：
  ```
  CUDA 状态
  无法探测硬件加速状态 (Failed to fetch)
  ```
- **核心痛点**：
  1. **生态绑架与语义误导**：CUDA 是 NVIDIA 的私有专有加速技术。非 NVIDIA 用户（如 Docker `--device=/dev/dri` 挂载 Intel 核显）看到“CUDA 状态无法探测”会误以为系统强依赖 CUDA 或安装失败；
  2. **过度兜底与历史遗留 Bug**：旧版前端在 `getHardwareAccelerationStatus` 异常时，无脑调用旧版已废弃的 `getCudaAvailability` 兜底，并将错误信息强行挂载到 `label="CUDA 状态"` 的表单项中；
  3. **重复探测性能损耗**：后端 `HardwareDetector` 实例缓存未静态化，每次 HTTP 请求重复执行耗时的 WMI 与 `nvidia-smi` 进程调用，容易因高耗时引发前端请求超时（`Failed to fetch`）。

---

## 二、 头脑风暴与核心维度技术选型

### 1. 网页图标标准（现代 Web 最佳实践）
* **业界方案对比**：
  - 单一 `favicon.ico`：尺寸固定（通常为 16x16 或 32x32），在高分屏（Retina / 4K）下模糊锯齿，无法根据操作系统深浅色主题自适应；
  - 现代标准方案：采用 **SVG 矢量图标为主 + ICO 降级为辅**。
* **ReferenceRAG 落地**：
  - 复用已有的几何美学徽标 `referencerag-appicon.svg`（采用高对比暗黑背景与 `#63E2B7` 主题色渐变）；
  - 在 `index.html` 中引入：
    ```html
    <link rel="icon" type="image/svg+xml" href="/favicon.svg">
    <link rel="alternate icon" type="image/x-icon" href="/favicon.ico">
    <link rel="apple-touch-icon" href="/favicon.svg">
    ```

### 2. 多厂商显卡感知与屏蔽规则（Strategy Pattern 策略模式）
根据 GPU 物理厂商实施精细化展示策略：

| 显卡厂商分类 | 硬件加速主方案 | 是否显示 CUDA 状态 | 是否显示 CUDA 库路径 | 异常时提示 |
| :--- | :--- | :--- | :--- | :--- |
| **NVIDIA 独立显卡** | CUDA 12 (满血版) | 仅当选定 CUDA 时显示 | 显示（支持自定义 DLL 目录） | NVIDIA CUDA 驱动探针异常 |
| **Intel 核显 / Arc 独显** | OpenVINO / DirectML | **绝对屏蔽** | **绝对屏蔽** | 硬件加速探针异常，安全降级至 CPU |
| **AMD Radeon / APU** | DirectML / ROCm | **绝对屏蔽** | **绝对屏蔽** | 硬件加速探针异常，安全降级至 CPU |
| **纯 CPU / 未知平台** | CPU 多核 SIMD | **绝对屏蔽** | **绝对屏蔽** | 硬件探针响应超时，默认采用 CPU 推理 |

### 3. 硬件状态探测状态机（State Machine Pattern）

```
                     ┌──────────────────────────┐
                     │      Uninitialized       │ (页面初始挂载)
                     └─────────────┬────────────┘
                                   │
                                   ▼
                     ┌──────────────────────────┐
                     │        Detecting         │ (发起探针请求)
                     └──────┬────────────┬──────┘
                            │            │
            HTTP 200 (成功) │            │ 接口超时/网络异常
                            ▼            ▼
             ┌─────────────────────┐   ┌─────────────────────────┐
             │      Detected       │   │       ProbeError        │
             └──────────┬──────────┘   └────────────┬────────────┘
                        │                           │
          ┌─────────────┴─────────────┐             ▼
          ▼                           ▼     统一以「硬件就绪状态」
   [HasNvidiaGpu = true]   [HasNvidiaGpu = false]   输出友好降级提示，
   允许渲染 CUDA 专属项     严禁渲染任何 CUDA 项    绝不甩锅给 CUDA！
```

### 4. 探针吞吐量与性能保障（后端静态缓存）
* 将 `HardwareDetector` 的探测结果升级为进程级静态缓存 `static HardwareContext? _cachedContext`，配合轻量级自旋锁保护。
* 物理显卡在容器或操作系统运行期间不会发生物理热拔插，首轮探测（~200ms）后，后续请求直达内存缓存（**< 0.1ms**），从根源杜绝并发查询导致的 WMI 阻塞与前端 `Failed to fetch`。

---

## 三、 业界主流方案对比

| 框架 / 产品 | 显卡感知与呈现方案 | 对非 NV 显卡的处理 | 在 ReferenceRAG 中的借鉴 |
| :--- | :--- | :--- | :--- |
| **PyTorch / Transformers** | `torch.cuda.is_available()`, `torch.xpu.is_available()` 分离 | 在非 NV 环境下调用 `torch.cuda` 直接为 false，不产生报错 | 严格区分 `hasNvidiaGpu` 与通用硬件加速，消除术语混淆 |
| **Ollama / vLLM** | 动态探测 runner（`ollama_llama_server` vs `ollama_llama_server_cuda`） | 非 NV 平台自动加载通用 CPU/ROCm 动态库，命令行不展示 CUDA 参数 | 界面表单按 GPU 厂商动态剪裁，非 NV 彻底隐藏 CUDA 输入项 |
| **LM Studio / Jan** | 硬件诊断卡片按物理设备展示（Intel Iris / Apple Metal / NVIDIA RTX） | 非 NV 显卡展示 Vulkan / Metal / OpenVINO，绝不提示 CUDA 缺失 | 统一归一化为「硬件就绪状态」，支持智能降级至 CPU |

---

## 四、 具体落地清单

1. **静态图标资源**：
   - 提取 `referencerag-appicon.svg` 与 `icon.ico` 部署至 `wwwroot/favicon.svg` 和 `wwwroot/favicon.ico`；
   - 在 `index.html` 的 `<head>` 中挂载标准图标链接。
2. **后端探测缓存**：
   - 重构 `HardwareDetector.cs`，引入 `_staticLock` 与 `_cachedContext`，实现单次探测全局复用。
3. **前端视图重构 (`Settings.js`)**：
   - 引入 `isNvidiaGpu` 与 `shouldShowCudaStatus` 计算属性；
   - 彻底废除旧版无脑兜底的 `cudaInfo` 赋值逻辑，接口异常时赋值给通用的 `hardwareProbeError`；
   - 模板中彻底移除对非 NV 显卡展示的 `CUDA 状态`，将状态标签统一为「硬件就绪状态」，当且仅当检测到 NVIDIA GPU 时才允许显示 CUDA 特有项。
4. **自动化单元测试**：
   - 编写单元测试验证非 NV 显卡识别逻辑、缓存命中与图标资源加载。
