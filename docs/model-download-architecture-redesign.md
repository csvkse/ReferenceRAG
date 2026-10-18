# 模型下载系统重构设计：策略模式 + 适配器模式 + 状态机

## 1. 背景与问题定义

用户反馈在 Web UI 中下载 `gte-multilingual-reranker-base` 重排模型时，任务长期停滞在 `0% 处理中`，无任何下载进度更新。排查发现以下系统性缺陷：

1. **元数据缺失文件大小（Size）导致进度计算失效**：
   Hugging Face 官方 API 的 `siblings` 列表中部分大文件（如 `model.safetensors`）未下发 `size` 字段（为 null 或 0）。原代码计算 `totalSize = sum(files.Size) = 0`，导致流式下载时的进度回调 `if (totalSize > 0)` 永远不会执行，前端被置于 0% 假死状态。
2. **镜像重定向与多变体全量下载缺陷**：
   国内镜像源 `hf-mirror.com` 对特定模型下发 `HTTP 308 Permanent Redirect`；在探测到 `onnx-community` 社区镜像后，代码错误地将 `onnx/` 子目录下的所有量化变体（共 8 个版本、总计逾 4.8GB）全数加入下载队列，而非仅下载选定的单一模型文件。
3. **缺乏状态机与进度阶段划分**：
   仅有全局状态 `downloading`，无法区分“解析清单”、“探测镜像”、“下载权重”、“校验完整性”与“本地转换”，缺乏操作透明度与取消恢复机制。
4. **巨型单体类高耦合**：
   `HuggingFaceModelDownloader.cs` 达 1600+ 行，网络通信、变体解析、进程调用与多源探查杂糅，违背单一职责原则（SRP）。

---

## 2. 总体架构设计

采用经典的三模式融合架构：

```
                    ┌─────────────────────────────────┐
                    │     ModelDownloadCoordinator    │
                    └────────────────┬────────────────┘
                                     │
        ┌────────────────────────────┼────────────────────────────┐
        ▼                            ▼                            ▼
┌───────────────────────┐  ┌───────────────────────┐  ┌───────────────────────┐
│     适配器模式        │  │        状态机         │  │       策略模式        │
│  IModelSourceAdapter  │  │  DownloadStateMachine │  │ IModelDownloadStrategy│
├───────────────────────┤  ├───────────────────────┤  ├───────────────────────┤
│• HuggingFaceAdapter   │  │• Idle                 │  │• DirectOnnxStrategy   │
│  (hf-mirror/官方/Token)│  │• ResolvingManifest    │  │• DirectGgufStrategy   │
│• ModelScopeAdapter    │  │• ProbingMirrors       │  │• CommunityMirror-     │
│  (国内魔搭社区快速通道)  │  │• DownloadingFiles     │  │  Strategy             │
│• LocalCacheAdapter    │  │• VerifyingFiles       │  │• PyTorchExportStrategy│
│                       │  │• Converting           │  │                       │
│                       │  │• Completed/Failed     │  │                       │
└───────────────────────┘  └───────────────────────┘  └───────────────────────┘
```

---

## 3. 状态机设计（DownloadStateMachine）

### 3.1 状态枚举与模型定义

```csharp
public enum DownloadStage
{
    Idle = 0,               // 空闲
    ResolvingManifest = 1,  // 正在解析模型清单与元数据
    ProbingMirrors = 2,     // 正在探测社区镜像与格式
    DownloadingFiles = 3,   // 正在下载权重与配置文件
    VerifyingFiles = 4,     // 正在校验文件结构与完整性
    Converting = 5,         // 正在执行格式转换与优化
    Completed = 6,          // 完成
    Failed = 7,             // 失败
    Cancelled = 8           // 已取消
}
```

### 3.2 进度详情字段（DownloadProgress）

- `Stage`: 当前生命周期阶段。
- `StageMessage`: 面向用户的中文阶段提示（例如：“正在从 onnx-community 下载 model.onnx (125.4 MB / 612.0 MB)...”）。
- `CurrentFileName`: 当前传输文件名。
- `CurrentFileIndex` / `TotalFiles`: 当前文件序号及总文件数。
- `CurrentFileBytes` / `CurrentFileTotalBytes`: 当前单文件字节进度。
- `BytesReceived` / `TotalBytes`: 跨文件总字节进度（通过 HTTP HEAD 兜底补齐准确大小）。
- `SpeedBytesPerSecond`: 实时传输速率。
- `EstimatedSecondsRemaining`: 预计剩余时间。

---

## 4. 适配器模式设计（IModelSourceAdapter）

### 4.1 接口职责

统一不同上游托管源的元数据与传输协议：
- 处理 301/308 HTTP 重定向与鉴权（HF_TOKEN）。
- 兜底探测：当清单中文件大小为 0 时，通过轻量级 HTTP `HEAD` 请求获取准确的 `Content-Length`。
- 多端点自动切换（`hf-mirror.com` 优先，官方 `huggingface.co` 备用）。

```csharp
public interface IModelSourceAdapter
{
    string SourceName { get; }
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
    Task<ModelManifest> GetManifestAsync(string modelId, CancellationToken cancellationToken = default);
    Task<long> ResolveFileSizeAsync(string modelId, string relativePath, CancellationToken cancellationToken = default);
    Task DownloadFileAsync(
        string modelId,
        string relativePath,
        string targetFilePath,
        IProgress<FileChunkProgress>? progress,
        CancellationToken cancellationToken = default);
}
```

---

## 5. 策略模式设计（IModelDownloadStrategy）

### 5.1 策略分类

1. **`DirectOnnxDownloadStrategy`**：
   - 官方已存在原生 ONNX 模型时触发。
   - 修复变体选择逻辑：只选择指定的变体或默认的 `model.onnx`，坚决杜绝全量下载整个变体子目录。
2. **`DirectGgufDownloadStrategy`**：
   - 检测到 GGUF 权重时触发，优先选用 Q4_K_M / Q4_0 规格，供 LLamaSharp 原生高效加载。
3. **`CommunityMirrorDownloadStrategy`**：
   - 官方仅有 PyTorch 权重时触发，自动在 `onnx-community`、`Xenova` 等空间探查。
   - 探查命中后，委托给 `DirectOnnxDownloadStrategy` 执行单变体下载。
4. **`PyTorchExportDownloadStrategy`**：
   - 官方与社区皆无 ONNX/GGUF 时的兜底策略。
   - 校验 Python/Optimum 环境，下载 raw 权重后执行进程外导出。

---

## 6. 前端 UI 交互设计

1. **卡片动态阶段徽章**：依据 `stage` 渲染不同颜色和文案（如蓝色的“正在下载”、紫色的“格式转换中”）。
2. **文件级传输指示**：展示当前文件名、第几个文件、速度及预估剩余时间。
3. **一键取消下载**：任务卡片提供取消按钮，调用后端取消令牌，及时释放网络与磁盘资源。
