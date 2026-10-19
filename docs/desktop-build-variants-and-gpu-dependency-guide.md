# ReferenceRAG 桌面端硬件加速构建方案与运行依赖选型指南

- **文档状态**：已归档（发布与分发操作指南）
- **更新时间**：2026-10-20
- **归档位置**：`docs/desktop-build-variants-and-gpu-dependency-guide.md`
- **关联脚本**：`support/tools/scripts/build-desktop.bat`
- **关联配置**：`Infrastructure/ReferenceRAG.Infrastructure/ReferenceRAG.Infrastructure.csproj`

---

## 一、 核心结论

> 💡 **核心结论**：
> - **使用【[1] 通用版】（默认）时，用户什么额外 SDK 都不需要装！只要电脑正常安装了显卡驱动就能直接运行。**
> - **只有选择了【[2] NVIDIA 专属满血版】时，才需要 CUDA 12 的底层动态链接库。**

---

## 二、 各构建方案的依赖安装需求对比

在运行 `support/tools/scripts/build-desktop.bat` 构建桌面安装包时，交互菜单提供了以下 4 种构建变体：

| 构建方案 | 需要装 CUDA / ROCm / oneAPI 吗？ | 用户电脑需要满足什么条件？ | 运行体验与推荐场景 |
| :--- | :--- | :--- | :--- |
| **[1] 通用版**<br>*(DirectML + Vulkan)*<br>**【默认 - 强烈推荐】** | **完全不需要！**<br>❌ 免装 CUDA<br>❌ 免装 AMD ROCm<br>❌ 免装 Intel oneAPI | **只需正常安装了显卡驱动即可**。<br>Windows 10/11 系统的 DirectX 12 与显卡驱动自带的 Vulkan 会自动承接硬件算力。 | **真正的开箱即用**。<br>发给任何有独显或核显的用户，双击直接 GPU 加速。日常分发、团队协作与商业交付首选。 |
| **[2] NVIDIA 专属满血版**<br>*(CUDA 12 + 内嵌微型库)* | **免装 Toolkit！**<br>已内置核心库 | **开箱即用满血加速**：<br>已内置 ~85MB 核心 DLL（`cublasLt64_12.dll` 等），解压即可满血激活 Tensor Core 算力。 | **开箱即用极致算力**，对纯小白和全新系统极其友好，无需配置任何环境变量。 |
| **[3] NVIDIA 专属轻量版**<br>*(CUDA 12 - 无依赖精简版)* | **自动借用本机环境** | **需要电脑已装有 CUDA 12 或 AI 软件**：<br>体积极小（~70MB），自动借用本机已有 Ollama、ComfyUI、SD-WebUI 或 PyTorch 的 CUDA 12 核心库。 | **极客首选**，下载体积仅 1/10，已有 AI 环境开发者的最轻量选择。 |
| **[4] AMD 专属优化版**<br>*(DirectML + Vulkan)* | **完全不需要！**<br>❌ 免装 AMD ROCm / HIP SDK | **只需装了 AMD 官方 Adrenalin 显卡驱动**。<br>DirectX 12 驱动层会直接调用 RDNA 架构硬件算力。 | **AMD 独显与 Ryzen/APU 核显直接加速**。<br>底层依赖与通用版同构。 |
| **[5] Intel 专属优化版**<br>*(DirectML + Vulkan)* | **完全不需要！**<br>❌ 免装 Intel oneAPI Toolkit | **只需装了 Intel 官方 Arc / 酷睿显卡驱动**。<br>DirectX 12 会自动调用 Xe 核心与核显算力。 | **Intel Arc 独显与酷睿 Ultra 核显直接加速**。<br>底层依赖与通用版同构。 |
| **[6] 纯 CPU 兼容版**<br>*(Pure CPU AVX2)* | **完全不需要！**<br>❌ 免装任何显卡驱动与 SDK | **任何支持 x64 的 Windows 电脑**。<br>不需要电脑拥有任何独立或集成 GPU。 | **🛡️ 极致兼容**。<br>专为云虚拟机、老旧电脑、无 GPU 环境量身打造，纯 CPU 多核多线程运行。 |

---

## 三、 为什么【通用版】能做到“零额外依赖”？

根本原因在于 Windows 平台的底层架构设计与图形驱动标准规范：

1. **向量与重排（ONNX Runtime）**：
   - 走的是微软官方与 DirectX 12 紧密集成的 **DirectML**。
   - DirectX 12 是 Windows 10/11 系统底层的原生图形/计算 API，AMD、Intel、NVIDIA 在发布显卡驱动时就已经将 DX12 硬件驱动打包进去了，应用随包携带 `DirectML.dll` 即可直接调度 GPU。
2. **大模型对话与嵌入（llama.cpp / LLamaSharp）**：
   - 走的是国际跨平台标准的 **Vulkan**。
   - 任何支持 3D 渲染的现代显卡驱动均原生在 `C:\Windows\System32` 内置了 `vulkan-1.dll`，无需用户安装 Vulkan 开发包（SDK），应用自带预编译算子库即可直接点火。

---

## 四、 NVIDIA 专属版的一键微型 CUDA 便携包与幂等分发

针对【[2] NVIDIA 专属满血版】，ReferenceRAG 提供了**开箱即用的一键微型 CUDA 12 绿色库机制**与**严格幂等检测**：

### 1. 一键微型下载脚本 (`download-cuda-minimal.bat` / `.ps1`)
无需下载庞大的 3GB+ 完整 NVIDIA CUDA Toolkit，只需运行根目录脚本：
```bat
download-cuda-minimal.bat
```
- **自动极速拉取**：从国内清华/阿里开源镜像源自动下载 `cublas64_12.dll`、`cublasLt64_12.dll` 与 `cudart64_12.dll`；
- **严格幂等防重下**：若本地或目标路径已存在有效运行库，**耗时 0.05 秒立即返回**，零网络开销，绝不重复下载；
- **支持强制刷新**：通过 `download-cuda-minimal.bat -Force` 可强制覆盖更新。

### 2. 桌面打包自动联动 (`build-desktop.bat`)
在构建 NVIDIA 专属版时：
```bat
build-desktop.bat --cuda --bundle-cuda
```
- **全自动检测与打包**：自动检测本地 `runtimes\cuda`，若不存在则提示或自动拉取，并打包入 `%OUTPUT%\runtimes\cuda`；
- **发布目录幂等跳过**：若发布目录中已存在有效的 CUDA 动态库，直接跳过复制，实现完全幂等；
- **独立分发**：生成出的安装包即使拷贝到全新无 CUDA 环境的 NVIDIA 显卡电脑上，也能以 100% 满血算力直接运行。

---

## 五、 总结建议与分发最佳实践

> 🚀 **总结建议**：
> - **常规交付 / 全显卡通用**：直接回车选择 **[1] 通用版**，全显卡兼备，零依赖开箱即用。
> - **追求极致 RTX 算力**：选择 **[2] NVIDIA 专属满血版** 并附带 `--bundle-cuda`，兼备极限性能与绿色免配置体验。

