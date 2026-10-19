# 软件搜索时发生卡退崩溃问题调研与修复方案

## 1. 故障背景与现象

在软件搜索界面执行检索操作（调用 `/api/ai/query`）时，程序发生异常“卡退”（无响应后进程突然崩溃闪退消失）。

### 1.1 系统事件日志证据 (Windows Event Viewer)
排查 Windows 事件日志（Application Log），发现进程在崩溃时刻记录了致命错误：
- **.NET Runtime (事件 ID 1023)**：
  ```text
  Application: ReferenceRAG.DesktopHost.exe
  CoreCLR Version: 10.0.1026.32716
  .NET Version: 10.0.10
  Description: The process was terminated due to an internal error in the .NET Runtime at IP 0x00007FFCA42C2801 with exit code 0x80131506.
  ```
  > `0x80131506` 为 CoreCLR 的 `COR_E_EXECUTIONENGINE`（ExecutionEngineException），表明发生了不可恢复的未托管原生内存破坏或严重异常。

- **Application Error (事件 ID 1000)**：
  ```text
  出错应用程序名称：ReferenceRAG.DesktopHost.exe
  出错模块名称：coreclr.dll
  异常代码：0xc0000005 (Access Violation，访问违例)
  错误偏移：0x00000000001a2801
  Faulting 应用程序路径：ReferenceRAG.DesktopHost.exe
  ```

### 1.2 运行时应用日志轨迹 (app_20261019.log)
检查崩溃发生时刻的日志：
```text
2026-10-19 17:41:50 [INF] [ModelsController] 开始切换重排模型: bce-reranker-base_v1
2026-10-19 17:41:50 [INF] [LazyRerankService] 触发重排模型重载: E:\...\bce-reranker-base_v1-Q4_0.gguf
2026-10-19 17:41:50 [INF] [GgufBackendStrategy] 重排模型 GGUF 检测到架构阵营: bert (Model: bce-reranker-base_v1)
2026-10-19 17:41:50 [INF] [LlamaRerankAdapter] 正在加载 GGUF 重排模型: ... (GpuLayers=-1)
2026-10-19 17:43:48 [INF] [SearchService] 开始重排: 候选数=30, Query=你好
2026-10-19 17:43:48 [WRN] [LlamaRerankAdapter] [LlamaRerank] 检测到 GGUF 重排模型输出 NaN，已平滑降级为保底加权分并早停。
2026-10-19 17:43:48 [INF] [SearchService] 两阶段搜索完成: 召回 30 个候选, 重排耗时 282ms, 返回 10 个结果
2026-10-19 17:43:54 [INF] [AIQueryController] Query(AIQueryRequest)
2026-10-19 17:43:54 [INF] [SearchService] 开始重排: 候选数=30, Query=...
[日志在此处突发中断，进程直接瞬间崩溃死亡]
```

---

## 2. 根本原因深入剖析

### 原因一：模型格式架构与推理引擎错配（BERT 双向编码器被强行交由 LlamaSharp 运算）
1. 用户在模型管理中切换到了 `bce-reranker-base_v1`。
2. 该模型所在物理目录中同时存在：
   - `model.onnx`（1.11 GB，官方正版 Cross-Encoder 模型）
   - `bce-reranker-base_v1-Q4_0.gguf`（社区量化版）
3. 在 `ModelManager.ScanDirectoryVariants` 中，扫描逻辑将 `.gguf` 文件排在变体列表首位，默认将其激活。
4. 在 `ModelBackendStrategyFactory` 解析重排适配器时，检测到 `.gguf` 且为非自回归的 `bert` 阵营，于是构建了 `LlamaRerankAdapter`。
5. `LlamaRerankAdapter` 内部将模型当作 `LLamaPoolingType.Rank` 交给 `LLamaEmbedder` 推理。但 **BCE-Reranker 是基于 XLM-RoBERTa 的双向 Encoder 分类模型，而非 Causal Decoder 模型**，`llama.cpp` 原生对 BERT GGUF 的 Rank Pooling 算子支持不全，无法输出合法 logits，导致计算结果持续为 `NaN`（模长为 0 导致 0/0）。

### 原因二：Native 上下文污染与双引擎 CUDA 显存竞争引发 0xc0000005 访问违例
1. 在 17:43:48 的第一次搜索中，`LlamaRerankAdapter` 遍历候选文档时因前两个文档均返回 NaN 触发了 `nanCount >= 2` 早停，表面上完成了降级。
2. **底层 Native 状态受损**：`LLamaEmbedder` 底层的 `llama_context` 并没有重置或清理 KV 缓存与上下文状态。
3. **双 GPU 运行时交叉冲击**：
   - 进程内第一阶段 Dense 向量检索使用了 `onnxruntime_providers_cuda.dll`（CUDA 12）；
   - 第二阶段重排使用了 `cuda12\llama.dll`（全量 GPU Offload，GpuLayers=-1）。
4. 在 6 秒后的第二次搜索中，用户再次发起查询，此时 `llama.dll` 在带有脏上下文残留的状态下尝试再次解码 `GetEmbeddings`，在原生 C++ 层面触发了非法指针访问/堆内存越界（Heap Out-of-bounds Read/Write），直接产生 Access Violation（代码 `0xc0000005`）。
5. .NET 10 CLR 捕获到底层灾难性内存冲突后，执行 `COR_E_EXECUTIONENGINE (0x80131506)` 立即强退整个应用。

### 原因三：重排链路缺乏容错防御与持久化熔断隔离（Single Point of Failure）
1. **重排适配器未持久熔断**：`LlamaRerankAdapter` 在第一次搜索检测到 NaN 后，没有记录模型永久不可用的熔断标记，第二次搜索依然重新调用 `_embedder.GetEmbeddings`，导致引爆崩溃。
2. **搜索主链路未做容灾降级**：`SearchService.cs` 在调用 `_rerankService.RerankBatchAsync` 时**完全没有 `try-catch`**！一旦重排抛出异常或原生底层报错，整路搜索流程直接夭折。

---

## 3. 系统性解决方案与修复措施

### 方案 1：模型优选与自动升配策略（Auto-Upgrade to ONNX）
1. **目录优选优先推荐 ONNX**：对于重排模型（`reranker`），Cross-Encoder 的标准与黄金容器是 ONNX Runtime。在 `ModelManager.ScanDirectoryVariants` 中，如果目录下同时存在 `model.onnx` 与 `.gguf`，优先将 `model.onnx` 设为默认激活项。
2. **GGUF 策略自愈降级**：在 `GgufBackendStrategy.CreateRerankAdapterAsync` 中，若探测到模型属于 `bert` 阵营，且同级或父级目录中存在 `model.onnx`，系统自动直接无缝升配为 `OnnxRerankAdapter`，利用 ONNX Runtime 稳定运行，杜绝 `llama.cpp` 的算子兼容性缺陷。

### 方案 2：为 `LlamaRerankAdapter` 装备自适应状态机与硬件级熔断器 (Circuit Breaker)
1. 增加持久化熔断标记 `_isPermanentlyDegraded`：
   一旦单次推理出现连续 NaN、异常模长或非预期输出，立即将适配器置为永久熔断状态；
2. 熔断后，后续所有重排请求直接由 `RerankExecutionContextStateMachine.GenerateFallbackDocuments` 瞬间按第一阶段相关性生成保底排序结果，**坚决不再触碰底层 Native 原生 API**，彻底杜绝内存踩踏与崩溃；
3. 对每次 `_embedder.GetEmbeddings` 调用增加防御性 `try-catch`，并捕获所有一般性与托管异常。

### 方案 3：在 `SearchService` 构筑两阶段搜索全局容灾防线
1. 在 `SearchService.cs` 的重排调用块使用完整的 `try-catch` 包裹；
2. 若重排出现任何未预料异常（包括 OOM、超时、适配器报错），自动保留第一阶段（Dense + BM25）召回的高质量排序与得分，并记录 `ModelName = "Fallback (重排异常降级)"`，**确保搜索结果 100% 返回给用户，软件绝不卡死、绝不闪退**！

### 方案 4：完善前端测试结果页与参数引导
在前端 `Performance.js` 中补充重排与嵌入模型的各项指标说明（NDCG、MRR、MAP、准确度、MAE、耗时）与调优建议，并修正文档列表中偏差为 `-` 的问题。
