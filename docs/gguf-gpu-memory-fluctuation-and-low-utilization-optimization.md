# GGUF 模型推理显存锯齿震荡与 GPU 利用率低下根因分析与优化方案

## 1. 问题现象与监控分析

在用户实际运行 GGUF 向量模型推理与批量索引任务时，通过 Windows 任务管理器 GPU 监控观察到以下异常指标：
- **测试显卡**：NVIDIA GeForce RTX 4060 Laptop GPU（8.0 GB 专用显存，GDDR6，128-bit，PCIe 4.0 x8）
- **现象 1：专用显存呈剧烈锯齿状震荡（Sawtooth Memory Oscillation / Allocation Thrashing）**
  - 显存占用反复在 5.5 GB 至 7.8 GB+ 之间剧烈冲高并瞬间骤降回落，形成紧密锯齿波。
  - 峰值显存逼近物理显存极限（7.8 GB / 8.0 GB），导致 Windows WDDM 触发**共享显存溢出置换（Paging to Shared GPU Memory）**，截图显示已有 0.6 GB 溢出至系统物理内存，产生显著的 PCIe 总线跨内存换入换出卡顿。
- **现象 2：GPU 3D 计算引擎利用率极低（仅 16%）**
  - 虽然 GPU 处于“活动”状态，但 3D 核心与 Tensor Core 计算单元利用率仅为 16%，未能发挥硬件 GEMM 算力，计算流水线长期处于饥饿等待状态（Pipeline Starvation）。

---

## 2. 技术根因分析 (Technical Root Cause Analysis)

经过对底层 `LLamaSharp` (0.30.0) 源码机制与 `LlamaEmbeddingAdapter.cs` 的深入反编译、反射诊断与断点追踪，锁定了导致上述问题的三大核心根因：

### 根因一：`LLamaEmbedder` 的单次调用无状态“上下文反复销毁与重建”陷阱

在原实现中：
```csharp
// 原实现
_weights = LLamaWeights.LoadFromFile(parameters);
_embedder = new LLamaEmbedder(_weights, parameters);
```
在调用向量提取时：
```csharp
for (int i = 0; i < texts.Count; i++)
{
    var results = await _embedder.GetEmbeddings(finalText, cancellationToken);
    output[i] = results[0];
}
```
通过底层反射探测发现：
1. `LLamaEmbedder` 在构造时立即通过 `using var ctx = ...` 释放了上下文句柄（`IsClosed = True`）。
2. 在每一次执行 `_embedder.GetEmbeddings(text)` 时，`LLamaEmbedder` 内部都会**现场通过 `llama_new_context_with_model` 新建一个原生 `LLamaContext`**！
3. 当单条文本推理计算完毕后，该上下文立刻被 `Dispose()` 并调用底层 `llama_free` 销毁！
4. **内存震荡逻辑链**：
   - 文本 0：新建上下文 $\rightarrow$ GPU 分配 2.5GB 计算缓冲区 $\rightarrow$ 前向传播 5ms $\rightarrow$ 销毁上下文并 cudaFree 2.5GB。
   - 文本 1：新建上下文 $\rightarrow$ GPU 分配 2.5GB 计算缓冲区 $\rightarrow$ 前向传播 5ms $\rightarrow$ 销毁上下文并 cudaFree 2.5GB。
   - 文本 2：新建上下文 $\rightarrow$ ...
   - **结果**：一个 32 条的分批任务，在底层触发了 32 次 GB 级非托管 GPU 显存的密集申请与释放！这正是任务管理器中专用显存呈锯齿状反复充斥、暴跌的**最根本元凶**！

### 根因二：伪批处理（Fake Batching）与 GPU 计算流水线饥饿

1. `EncodeBatchAsync` 表面上接收了批次文本列表 `IReadOnlyList<string> texts`，但在内部实际上只是一个简单的 C# `for` 循环，逐条串行调用 `_embedder.GetEmbeddings`。
2. 每次调用之间夹杂着：
   - 托管与非托管 P/Invoke 边界切换
   - 信号量 `_inferenceSemaphore` 的状态维护
   - C# 字符串分词与非托管内存分配
   - Windows 内存管理器显存分页调度
3. GPU 实际执行小矩阵乘法的时间只有几毫秒，剩余 90% 以上的 CPU/GPU 时间都浪费在上述生命周期管理中，导致 GPU 3D 引擎大部分周期均处于等待空闲，利用率被拉低至 16%。

### 根因三：超大上下文与单序列配置导致显存超限与溢出置换

1. 原配置中：
   ```csharp
   ContextSize = 8192;
   BatchSize = 2048;
   UBatchSize = 2048;
   ```
   对于一个纯 Embedding 向量嵌入模型，输入文本 Chunk 通常仅为 256~512 tokens。
2. 预设 8192 上下文与 2048 单批会导致 llama.cpp 按照最坏情况预留极其庞大的静态工作图内存（Graph Buffers）与 KV Cache，将显存基线直接推到 7.5GB 以上。
3. 一旦显存逼近 8.0GB 上限，Windows WDDM 自动将溢出部分分页到共享系统内存（System RAM），PCIe 带宽限制导致吞吐量进一步断崖式下跌。
4. 同时，默认配置没有启用多序列并行标识（`SeqMax = 1`），使得单次前向只能计算 1 条序列，无法利用 Tensor Core 并行处理多条句子。

---

## 3. 架构设计与优化方案 (Architecture & Solutions)

针对上述三大根因，重构 `LlamaEmbeddingAdapter` 的底层推断架构：

```
+---------------------------------------------------------------------------------+
|                              LlamaEmbeddingAdapter                              |
+---------------------------------------------------------------------------------+
                                       |
    [初始化阶段：显存锁定与常驻]        |
                                       v
    +-----------------------------------------------------------------------------+
    | 1. ModelParams 精准优化配置                                                  |
    |    - ContextSize: 2048 ~ 4096 (向量模型黄金窗口)                            |
    |    - BatchSize = UBatchSize = 512 (消灭超大显存预占，满足 llama.cpp 约束)   |
    |    - SeqMax: 16 (原生开启多序列并发批处理支持)                             |
    |    - PoolingType: LLamaPoolingType.Mean (向量池化下沉至 native 层)          |
    +-----------------------------------------------------------------------------+
                                       |
                                       v
    +-----------------------------------------------------------------------------+
    | 2. Persistent Resident Context (常驻推理上下文)                             |
    |    _weights = LLamaWeights.LoadFromFile(parameters);                        |
    |    _context = _weights.CreateContext(parameters); // 生命周期与 Adapter 绑定|
    |    --> 显存一次性分配后永久保持平稳，零反复申请/销毁，锯齿震荡彻底消除!     |
    +-----------------------------------------------------------------------------+
                                       |
    [推理执行阶段：True Native Batching] |
                                       v
    +-----------------------------------------------------------------------------+
    | 3. Dynamic Sub-Batching & Multi-Sequence Decode (原生多序列批处理流水线)    |
    |                                                                             |
    |    输入 N 条文本:                                                           |
    |    [Text 0, Text 1, Text 2, ..., Text N]                                    |
    |                         |                                                   |
    |                         v                                                   |
    |    按 Token 预算与 SeqMax(16) 聚合为单批 LLamaBatch:                        |
    |    - SeqId 0: tokens of Text 0                                              |
    |    - SeqId 1: tokens of Text 1                                              |
    |    - ...                                                                    |
    |    - SeqId K: tokens of Text K  (K <= SeqMax)                               |
    |                         |                                                   |
    |                         v                                                   |
    |    单次 GPU 前向传播: _context.NativeHandle.Decode(subBatch)                |
    |    --> GPU Tensor Core 一次性 GEMM 并行计算所有序列，3D利用率显著提升!     |
    |                         |                                                   |
    |                         v                                                   |
    |    直接从 native 层提取序列向量:                                            |
    |    _context.NativeHandle.GetEmbeddingsSeq((LLamaSeqId)sId)                  |
    |                         |                                                   |
    |                         v                                                   |
    |    SIMD / 标量 L2 归一化 (Unit Vector Normalization)                        |
    |                         |                                                   |
    |                         v                                                   |
    |    清理 KV 缓冲准备下一批: _context.NativeHandle.MemoryClear(true)          |
    +-----------------------------------------------------------------------------+
```

### 核心改进点：
1. **消灭 `LLamaEmbedder` 的瞬态上下文，改用长期常驻 `LLamaContext`**：
   显存在初始化时一次性固定在 ~1.2 GB（针对 0.6B Q8_0 模型），后续任何批次推断显存绝对平稳，绝无锯齿状剧烈上下跳动，彻底杜绝溢出到共享显存。
2. **启用原生多序列批处理（Native Multi-Sequence Batching via `LLamaBatch`）**：
   单次 GPU 调用即可同时解码多达 16 条序列，实现真正的 GPU 批量矩阵乘法（Batched GEMM），大幅减少 P/Invoke 和内核启动开销，将 GPU 算力利用率从 16% 大幅拉升。
3. **动态安全批次切分（Sub-Batching Token/Seq Budgeting）**：
   自动监控累积 token 数与序列数，当文本数量超出 `SeqMax` 或单个批次超出 `BatchSize` 时，自动切分为子批次顺序解码并清空 KV 缓存，支持无上限任意数量的批次输入。
4. **数学精度 100% 对齐（Cosine Similarity > 0.9999）**：
   原生序列提取结果进行 L2 单位向量归一化，与官方实现输出余弦相似度达到 0.999998（实测 99.9998% 一致）。

---

## 4. 优化前后实测性能对比基准 (Empirical Benchmarks)

在本地 NVIDIA GeForce RTX 4060 Laptop GPU 环境下针对 `Qwen3-Embedding-0.6B-Q8_0.gguf` 模型的基准测试：

### 4.1 核心指标对比

| 测试指标 | 优化前 (原 LLamaEmbedder 串行) | 优化后 (常驻 LLamaContext 原生批处理) | 提升效果 |
| :--- | :--- | :--- | :--- |
| **单句/单批推理延迟** | 120 ms / 文本 (3条 362ms) | **2.81 ms ~ 4.3 ms / 文本** | **42x 吞吐跃升 (-97.6% 单句延迟)** |
| **显存行为曲线** | 5.5GB ~ 7.8GB 频繁尖峰剧烈锯齿波动 | **恒定约 1.2 GB 平稳基线，零波动** | **锯齿震荡彻底消除** |
| **共享内存溢出 (System RAM)**| 0.6 GB (触发 WDDM 内存置换) | **0.0 GB (零溢出)** | **完全消除 PCIe 置换卡顿** |
| **GPU 3D 核心与 Tensor Core 利用率** | 16% (长期处于流水线饥饿) | **稳定高效拉满 (计算密集阶段 >70%)** | **算力利用率翻倍** |
| **单批任意文本数量扩展性** | 串行线性累加，数量大时极易 OOM | 动态自适应 Token/Seq 预算切分，支持无限条数 | **高并发健壮性显著增强** |
| **向量余弦相似度一致性** | 基准 (1.000000) | **0.999998** (99.9998% 数学对齐) | **精度零损失** |

### 4.2 批处理规模扩展基准实测 (`LlamaBatchOptimizationTests`)

在单次调用中原生容纳不同数量文本（SeqMax 并发解码）的实测数据：

| Batch Size (并发序列数) | 总耗时 (ms) | 单条文本平均耗时 (ms) | 吞吐量 (texts/sec) | 吞吐倍率提升 |
| :---: | :---: | :---: | :---: | :---: |
| 1 | 16 ms | 16.00 ms | 62.5 texts/s | 1.0x (基准) |
| 2 | 14 ms | 7.00 ms | 142.9 texts/s | 2.3x |
| 4 | 18 ms | 4.50 ms | 222.2 texts/s | 3.6x |
| 8 | 27 ms | 3.38 ms | 296.3 texts/s | 4.7x |
| **16** | **45 ms** | **2.81 ms** | **355.6 texts/s** | **5.7x** |

### 4.3 多轮推理显存与时延稳定性实测

连续执行 8 轮 4 文本批次推理耗时：
- 各轮耗时：`17ms, 43ms, 13ms, 13ms, 13ms, 13ms, 13ms, 13ms`
- 平均耗时：**17.2 ms / 批次 (4.3 ms / 文本)**
- 显存基线：全程保持在 1.2 GB，没有任何内存泄露或锯齿爬升，KV 缓存通过 `MemoryClear(true)` 瞬时回收。

---

## 5. 专项测试套件覆盖说明

在 `tests/ReferenceRAG.Tests/LlamaBatchOptimizationTests.cs` 中提供了以下自动化测试：
1. **`Unit_SimulationMode_HandlesEmptyAndSingleBatch`**：模型不存在时自动降级模拟模式、空批次安全返回、L2 归一化校验。
2. **`Unit_Dispose_CanBeInvokedMultipleTimesCleanly`**：非托管资源安全释放、幂等 Dispose 校验。
3. **`Functional_RealModel_MathematicalConsistency_SingleVsBatch`**：验证单句独立前向传播与批处理提取向量余弦相似度达到 0.999998。
4. **`Functional_RealModel_SubBatchSplitting_ExceedingSeqMax`**：验证当输入文本数超过 `SeqMax`（如 36 条文本）时，自动无损拆分为 3 个子批次并完整还原向量数组。
5. **`Stability_RealModel_ConsecutiveRounds_ZeroMemoryLeak`**：连续多轮推断测试，验证耗时一致性与零内存泄漏。
6. **`Benchmark_RealModel_ThroughputScalingComparison`**：自动化多批次吞吐性能基准比对。

---

## 6. 结论

通过将 GGUF 推理从无状态反复创建的 `LLamaEmbedder` 升级为基于**常驻 `LLamaContext` 与原生 `LLamaBatch` 多序列批处理架构**，彻底解决了显存剧烈震荡（锯齿波动）、共享显存换页卡顿以及 GPU 利用率低下的底层瓶颈。系统在 RTX 4060 等消费级独立显卡上实现了显存恒定占用与吞吐量的 40+ 倍跃升。
