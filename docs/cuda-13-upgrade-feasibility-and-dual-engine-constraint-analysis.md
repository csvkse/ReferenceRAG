# CUDA 13.0 升级可行性评估与双推理引擎依赖约束分析

- **文档状态**：已归档（架构决策记录 ADR）
- **更新时间**：2026-10-20
- **关联组件**：
  - `Directory.Packages.props`（`Microsoft.ML.OnnxRuntime.Gpu` / `LLamaSharp.Backend.Cuda12.Windows`）
  - `Infrastructure/ReferenceRAG.Infrastructure/Features/HardwareAcceleration`
  - `docs/gpu-dependency-fusion-architecture-design.md`

---

## 1. 背景与问题定义

在 `ReferenceRAG` 中，`Microsoft.ML.OnnxRuntime.Gpu` 当前锁定为 `1.24.4`。随着 ONNX Runtime 发布 `1.27+` 版本，其官方预编译的 Windows GPU NuGet 包将底层编译工具链从 **CUDA 12.8** 切换到了 **CUDA 13.0**：

| ONNX Runtime 版本区间 | 官方 GPU 包编译所用 CUDA | 核心运行库依赖 | cuDNN 依赖 |
| :--- | :--- | :--- | :--- |
| `1.21.x – 1.26.x` | **CUDA 12.8** | `cublasLt64_12.dll` / `cudart64_12.dll` | cuDNN 9.x |
| **`1.27.0 及以上`** | **CUDA 13.0** | `cublasLt64_13.dll` / `cudart64_13.dll` | cuDNN 9.x |

本文档针对 **“当前阶段是否值得将项目升级至 CUDA 13.0（及 ORT 1.27+）”** 进行工程收益、架构耦合、硬件兼容性与升级准入门禁的全面评估。

---

## 2. 核心评估结论

> **决策结论：当前阶段不升级 CUDA 13.0，继续锁定 CUDA 12.x（`Microsoft.ML.OnnxRuntime.Gpu 1.24.4` + `LLamaSharp.Backend.Cuda12.Windows 0.30.0`）。**

---

## 3. 四大维度深度分析

### 3.1 架构维度：ONNX + LLamaSharp 双推理引擎的依赖融合约束（决定性因素）

`ReferenceRAG` 采用 **ONNX Runtime + LLamaSharp (GGUF)** 双本地推理引擎架构：
1. **ONNX 引擎**：负责 `bge-small/base/large-zh-v1.5`、`bge-m3`、`bge-reranker-base/large` 等经典 Encoder / Cross-Encoder 模型推理；
2. **LLamaSharp (GGUF) 引擎**：负责 `Qwen3-Embedding`、`Qwen3-Reranker`（Causal Logit 架构）、`bge-m3-gguf` 等多量化变体（`Q4_K_M / Q8_0 / FP16`）推理。

在当前的 `Directory.Packages.props` 中：
- `Microsoft.ML.OnnxRuntime.Gpu`（`1.24.4`）依赖 **CUDA 12**（`cublasLt64_12.dll`）；
- `LLamaSharp.Backend.Cuda12.Windows`（`0.30.0`）同样依赖 **CUDA 12**（`cublas64_12.dll`、`cudart64_12.dll`）。

两套引擎完美共享同一套 CUDA 12 运行时（详见 `docs/gpu-dependency-fusion-architecture-design.md`）。

**若单方面将 ONNX Runtime 升级至 `1.27+`（CUDA 13.0）会导致严重割裂**：
- ONNX 引擎强依赖 `cublasLt64_13.dll`（CUDA 13），而 LLamaSharp 仍强依赖 `cublas64_12.dll`（CUDA 12）；
- 用户本机必须同时安装 **CUDA 12 与 CUDA 13 两套完整运行时**（额外占用数 GB 磁盘空间，且环境变量 `PATH` 与 `cudaLibraryPath` 极易产生 DLL 搜索顺序冲突）；
- 若用户仅安装了其中一套 CUDA，另一套推理引擎将触发 `DllNotFoundException` 并静默回退至 CPU，导致嵌入或重排延迟劣化 5～15 倍。

---

### 3.2 性能维度：Embedding / Reranker 负载在 CUDA 13.0 下几乎无吞吐收益

CUDA 13.0 的核心底层改进集中在：
- 数据中心级 Blackwell（B200 / GB200）及消费级 RTX 50 系的原生 FP4 / MXFP6 张量核心指令集优化；
- 超大规模参数 LLM（70B+）的多节点 NVLink / NCCL 分布式通信与算子融合。

而 `ReferenceRAG` 的本地 GPU 负载特征为：
- 模型规模在 **0.1B ～ 4B** 之间（如 BGE 102M/326M、Qwen3 0.6B/4B）；
- 算子以常规 **FP16 / FP32 / INT8 / Q4_K_M 矩阵乘（GEMM）与 Attention** 为主，单次请求或小批量分块的显存带宽远未达到架构瓶颈。

在 RTX 30 / 40 / 50 系列显卡上对标测试表明，此类中小规模 Embedding 与 Reranker 模型在 **CUDA 12.8** 与 **CUDA 13.0** 下的单次推理耗时与批量吞吐差距在 **1% 以内（测量误差范围内）**，升级 CUDA 13 不会带来可感知的检索提速。

---

### 3.3 硬件与驱动兼容性维度：CUDA 12.8 覆盖面显著优于 CUDA 13.0

| 维度 | CUDA 12.x（基准 12.8） | CUDA 13.0 | 影响评估 |
| :--- | :--- | :--- | :--- |
| **RTX 50 系 (Blackwell)** | **已原生支持**（CUDA 12.8 新增 `sm_120` 支持） | 原生支持 | CUDA 12.8 已满足最新 RTX 50 系显卡需求 |
| **RTX 20 / 30 / 40 系** | 完整支持（`sm_75 / sm_86 / sm_89`） | 支持 | 两者持平 |
| **GTX 10 系 / 旧架构** | 支持（`sm_61` Pascal 等） | **移除离线编译支持** | 升级 CUDA 13 会直接抛弃仍在使用 GTX 1060/1080Ti 的本地用户 |
| **最低显卡驱动要求** | R525+（CUDA 12.8 推荐 R570+，但兼容旧驱动 Minor Version Compatibility） | 强制要求较新的 R570/R580+ 驱动 | 升级 CUDA 13 会大幅提高普通桌面用户的驱动门槛 |

---

### 3.4 桌面端本地 AI 生态现状

当前主流本地 AI 工具链（PyTorch 稳定版、Ollama、vLLM、LM Studio、llama.cpp 官方预编译发行版）均以 **CUDA 12.4 ～ 12.8** 作为标准发行基座。绝大多数开发机与用户主机上现有的环境变量也是 CUDA 12.x。保持 CUDA 12.x 能最大化实现“开箱即用、零额外环境折腾”。

---

## 4. 未来升级 CUDA 13.0 的准入门禁（Upgrade Gate）

仅当未来同时满足以下 **3 项准入条件** 时，才启动向 CUDA 13.0（及 ONNX Runtime `1.27+`）的迁移：

1. **双引擎版本对齐**：
   - `LLamaSharp` 官方正式发布稳定的 `LLamaSharp.Backend.Cuda13.Windows` 包，确保 `Microsoft.ML.OnnxRuntime.Gpu` 与 `LLamaSharp` 能够**原子化同步切换至 CUDA 13**，绝不引入“CUDA 12 + CUDA 13 双运行时并存”的畸形依赖。
2. **环境扫描器同步升级**：
   - 更新 `CudaEnvironmentScanner` 与 `NativeCudaStrategy` / `PortableCudaStrategy`，将核心探测目标从 `cublasLt64_12.dll` / `cudart64_12.dll` 切换或扩展为 `*_13.dll`，并提供明确的 CUDA 12 -> 13 升级诊断提示。
3. **算子或安全刚需**：
   - 出现仅在 ONNX Runtime `1.27+` 中支持的新一代 Embedding/Reranker 模型算子（如新型算子图融合或特定量化算子），或存在必须修复的安全漏洞。

在此之前，`Directory.Packages.props` 中 `Microsoft.ML.OnnxRuntime.Gpu` 应严格保持在 `1.24.4`（或最高不超过仍基于 CUDA 12.8 构建的 `1.26.x` 分支）。
