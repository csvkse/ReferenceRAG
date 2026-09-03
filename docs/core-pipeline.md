# ReferenceRAG 核心流水线技术文档

> 覆盖范围：文件切片 · 本地模型管理 · 切片向量 · BM25 · 图构建 · 文件变动监听与增量更新

---

## 目录

1. [整体架构](#整体架构)
2. [文件切片（MarkdownChunker）](#文件切片)
3. [本地模型管理（ModelManager）](#本地模型管理)
4. [切片向量（EmbeddingService）](#切片向量)
5. [BM25 全文索引（Fts5BM25Store）](#bm25-全文索引)
6. [图构建（GraphIndexingService）](#图构建)
7. [文件变动监听（FileMonitorService）](#文件变动监听)
8. [统一索引流水线（FileIndexPipeline）](#统一索引流水线)
9. [自动增量索引（AutoIndexService）](#自动增量索引)
10. [数据流全链路图](#数据流全链路图)

---

## 整体架构

```
文件系统
    │
    ▼
FileMonitorService          ← FSW 监听 + 防抖
    │  FileChanged 事件
    ▼
AutoIndexService            ← IHostedService，队列消费
    │
    ▼
FileIndexPipeline           ← 单文件索引总协调器
    ├─ MarkdownChunker       → 切片
    ├─ TextEnhancer          → 切片增强（用于 Embedding）
    ├─ EmbeddingService      → 切片向量
    ├─ SqliteVectorStore     → 向量持久化
    ├─ Fts5BM25Store         → BM25 全文索引
    └─ GraphIndexingService  → Wiki-link 图构建
```

所有索引数据共存于单一 SQLite 文件，通过 `SharedSqliteConnection` 共享连接和信号量锁。

---

## 文件切片

**实现**：`src/ReferenceRAG.Core/Services/Indexing/MarkdownChunker.cs`  
**接口**：`IMarkdownChunker`  
**数据结构**：`ChunkRecord`

### 分段策略（三层）

#### 第一层：按标题提取 Section

```
## 章节一          → Section(StartLine=1, Level=2, HeadingPath="章节一")
内容 A...

### 子节           → Section(StartLine=N, Level=3, HeadingPath="章节一/子节")
内容 B...
```

- 用 `^(#{1,6})\s+(.+)$` 正则匹配标题行
- 维护 `Stack<(Level, Text)>` 构建 `HeadingPath`（如 `"章节一/子节"`）
- 无标题文档整体作为一个 Section

#### 第二层：短 Section 合并（P0）

token 数 < `MinTokens`（默认 50）的 Section 合并到相邻 Section，避免纯标题 chunk 进入索引。

合并规则：
- 短 Section 先缓存，遇到足够长 Section 时前置合并
- 文档末尾残留短 Section 追加到上一个 Section

#### 第三层：过长 Section 细分

token 数 > `MaxTokens`（默认 512）时按段落切分，段落仍超长则按句子切分。

**重叠机制**：新 chunk 开头携带前一 chunk 末尾 `OverlapTokens`（默认 50）个 token 的段落，保证上下文连续性。

### Chunk 权重

标题级别越高权重越高：

| 标题级别 | 权重乘数 |
|----------|---------|
| H1       | ×1.5    |
| H2       | ×1.3    |
| H3       | ×1.1    |
| H4+/无标题 | ×1.0  |

> **注意**：短内容不额外加权（曾有 Bug：纯标题 chunk 因 `content.Length < 200` 获得 ×1.2 权重导致排名虚高，已移除）。

### ChunkRecord 关键字段

| 字段 | 含义 |
|------|------|
| `Id` | UUID，全局唯一 |
| `FileId` | 所属文件 ID |
| `ChunkIndex` | 文件内顺序编号 |
| `Content` | 原始文本（BM25 和展示用） |
| `EnhancedContent` | 增强后文本（Embedding 用，由 `TextEnhancer` 填充） |
| `HeadingPath` | 标题层级路径，如 `"一级/二级"` |
| `StartLine` / `EndLine` | 在源文件中的行号范围 |
| `TokenCount` | token 数估算值 |
| `Weight` | 排序权重 |

---

## 本地模型管理

**实现**：`src/ReferenceRAG.Core/Services/ModelManagement/ModelManager.cs`  
**接口**：`IModelManager`

### 预定义模型库

`ModelManager` 内置预定义注册表（`PredefinedModels`），涵盖：

| 系列 | 代表模型 | 维度 | 语言 |
|------|----------|------|------|
| BGE (BAAI) | bge-small/base/large-zh-v1.5, bge-m3 | 512/768/1024 | 中/英/多语言 |
| GTE (阿里) | gte-small/base/large-zh | 512/768/1024 | 中 |
| E5 (微软) | multilingual-e5-small/base | 384/768 | 多语言 |
| text2vec | text2vec-base/large-chinese | 768/1024 | 中 |
| MiniLM | all-MiniLM-L6-v2 | 384 | 英 |
| Reranker | bge-reranker-base/large, bce-reranker-base_v1, gte-multilingual-reranker-base | 768/1024 | 中英/多语言 |

### 目录结构

```
{ModelsRootPath}/
├── Embedding/
│   ├── bge-base-zh-v1.5/
│   │   ├── model.onnx          ← embedded 格式
│   │   ├── tokenizer.json
│   │   ├── config.json
│   │   └── 1_Pooling/config.json
│   └── bge-m3/
│       ├── model.onnx          ← 引用外部数据
│       └── model.onnx_data     ← external 格式
└── Reranker/
    └── bge-reranker-base/
        └── model.onnx
```

### ONNX 格式检测

`DetectOnnxFormat(modelDir)` 返回三种状态：

| 状态 | 含义 |
|------|------|
| `embedded` | 权重内嵌于 `.onnx` 文件，单文件部署 |
| `external` | 权重在独立 `.onnx_data` 文件，大模型常见 |
| `invalid` | `.onnx` 文件引用外部数据但 `.data` 文件缺失，或 PyTorch 模型存在但 `.onnx` < 10MB（转换失败残留） |

检测逻辑：
1. 存在 `model.onnx_data` → `external`
2. 扫描 `.onnx` 前 1MB 内容，含 `model.onnx.data` 或 `.onnx.data` 字符串 → `invalid`
3. PyTorch 源文件存在且 `.onnx` < 10MB → `invalid`
4. 否则 → `embedded`

### 模型下载流程

```
DownloadModelAsync(modelName)
    ├── 验证模型已注册
    ├── 创建目标目录（Embedding/ 或 Reranker/）
    ├── HuggingFaceModelDownloader.DownloadAsync()
    │   └── 从 HuggingFace Hub 下载 ONNX 文件
    ├── 验证 model.onnx 存在（含子目录搜索）
    ├── 若在子目录找到，复制到根目录 + 复制 _data 文件
    └── 更新 ModelInfo（IsDownloaded, LocalPath, OnnxFormat, Dimension）
```

### 模型切换事件

切换模型后触发事件，`EmbeddingService` 订阅后热重载 ONNX Session：

```
SwitchModelAsync → 更新配置 → 触发 EmbeddingModelSwitched 事件
SwitchRerankModelAsync → 更新配置 → 触发 RerankModelSwitched 事件
SetModelsPathAsync → 重扫目录 → 触发 ModelPathChanged 事件（卸载旧模型）
```

### 非对称编码配置

部分模型（BGE 系列）支持 Query/Document 不对称前缀：

```json
{
  "QueryPrefix": "query: ",
  "DocumentPrefix": "passage: "
}
```

`EmbeddingService` 根据 `EmbeddingMode`（Query / Document / Symmetric）自动加前缀。

---

## 切片向量

**实现**：`src/ReferenceRAG.Core/Services/Embedding/EmbeddingService.cs`  
**接口**：`IEmbeddingService`  
**存储**：`SqliteVectorStore`（`src/ReferenceRAG.Storage/SqliteVectorStore.cs`）

### 模型加载

启动时按顺序：

1. 检测 CUDA 可用性，配置 `SessionOptions`（`EnableMemoryPattern = false` 支持动态 batch）
2. 加载 ONNX InferenceSession
3. 读取输出形状确定向量维度（符号维度为 0 时从 `1_Pooling/config.json` 或 `config.json` 补读）
4. 读取 `1_Pooling/config.json` 确定 pooling 策略（Mean / CLS）
5. 检测输入形状是否固定（`_hasDynamicSeqLen`）
6. 加载分词器（见分词器优先级）

模型文件不存在时进入**模拟模式**，返回随机归一化向量（搜索结果无意义，仅保证服务不崩溃）。

### 分词器优先级

```
1. HuggingFaceTokenizer（tokenizer.json，原生 Rust 库，全管线）
    ↓ DLL 缺失或初始化失败
2. BertTokenizer（tokenizer.json，仅 vocab）
    ↓ 失败
3. MLBertTokenizer（vocab.txt，Microsoft.ML.Tokenizers）
    ↓ 无 vocab.txt
4. FallbackTokenizer（按字符编码，精度最低）
```

### 批量推理流程

```
EncodeBatchAsync(texts, mode)
    ├── ApplyModePrefix（非对称编码加前缀）
    ├── Tokenize（MaxSequenceLength, TrimToActualLength）
    ├── 构建 ONNX 输入 Tensor（input_ids, attention_mask[, token_type_ids]）
    ├── session.Run()
    ├── 提取输出（优先 sentence_embedding，否则取第一输出）
    │   ├── 2D [batch, dim] → 直接切片
    │   └── 3D [batch, seq, dim] → Mean Pooling 或 CLS Pooling
    └── L2 归一化（SIMD 加速）
```

### L2 归一化（SIMD）

```csharp
// 利用 System.Numerics.Vector<float> 向量化计算平方和与除法
// 显著加快大批量归一化速度
```

归一化后向量相似度 = 内积（无需除以模长）。

### VectorRecord 数据结构

| 字段 | 含义 |
|------|------|
| `Id` | UUID |
| `ChunkId` | 关联 ChunkRecord |
| `FileId` | 关联 FileRecord |
| `Vector` | float[] 向量（长度 = Dimension） |
| `Dimension` | 向量维度 |
| `ModelName` | 生成此向量的模型名 |
| `Source` | 所属源文件夹名 |

---

## BM25 全文索引

**实现**：`src/ReferenceRAG.Storage/Fts5BM25Store.cs`  
**接口**：`IBM25Store`

### 技术选型

使用 SQLite FTS5 虚拟表，借助其内置 `bm25()` 函数实现排序：

```sql
CREATE VIRTUAL TABLE bm25_fts USING fts5(
    id UNINDEXED,
    content,
    tokenize='unicode61 remove_diacritics 1'
);
```

- `id UNINDEXED`：chunk UUID，不参与全文索引
- `tokenize='unicode61 remove_diacritics 1'`：SQLite 内置 unicode 分词（但中文需要预处理）

### 中英文混合分词

FTS5 的 unicode61 分词器对中文不友好（按空格分），因此在写入前**预处理**为空格分隔 token：

```
输入："深度学习 deep learning"
处理后："深 度 学 习 deep learning"
```

规则：
- **中文字符**（U+4E00–U+9FFF 等范围）：每个字符作为独立 token
- **英文单词**：按空格分隔后转小写
- **标点符号**：作为分隔符，不进入 token
- **英文停用词**过滤（约 80 个常见词）

查询时同样预处理，每个 token 用 `"token"` 引号包裹后以 `OR` 连接：

```sql
-- 查询 "机器学习" → "机" OR "器" OR "学" OR "习"
SELECT id, content, bm25(bm25_fts, 1.5, 0.75) as score
FROM bm25_fts
WHERE bm25_fts MATCH '"机" OR "器" OR "学" OR "习"'
ORDER BY bm25(bm25_fts, 1.5, 0.75)
LIMIT 10
```

BM25 参数：k1=1.5，b=0.75（可在查询时自定义）。

### 并发控制

`Fts5BM25Store` 与 `SqliteVectorStore` 共用 `SharedSqliteConnection` 的 `SemaphoreSlim(1,1)` 锁，保证 SQLite WAL 模式下写操作串行。

### 批量写入

`IndexBatchAsync` 在单事务内批量 DELETE + INSERT，失败时回滚，保证原子性。

---

## 图构建

**实现**：`src/ReferenceRAG.Core/Services/Graph/GraphIndexingService.cs`  
**链接提取**：`WikiLinkExtractor.cs`  
**存储**：`SqliteGraphStore.cs`  
**接口**：`IGraphStore`

### 图模型

```
GraphNode {
    Id: string           // 归一化文件路径（\ → /）
    Title: string        // 文件名或标题
    Type: "document" | "heading" | "tag" | "external"
    ChunkIds: List<string>
}

GraphEdge {
    FromId: string       // 来源节点 ID
    ToId: string         // 目标节点 ID
    Type: "wikilink" | "tag" | ...
    LineNumber: int
}
```

### 图更新流程（UpdateGraphAsync）

```
UpdateGraphAsync(file, markdownContent, chunks)
    ├── 构建主节点（document 类型）
    ├── 提取所有标题 → heading 节点
    ├── WikiLinkExtractor.Extract(content) → 遍历所有链接
    │   ├── [[tag]] → tag 节点（resolvedId = "#tag"）
    │   ├── [[file]] → 通过 filenameMap 解析为完整路径节点
    │   │   ├── 找到 → resolvedId = 完整路径（含 #heading 后缀）
    │   │   └── 找不到 → external 节点（表示悬空链接）
    │   └── 构建 GraphEdge（FromId=当前文件, ToId=目标节点）
    └── UpsertFileGraphAsync()  ← 单事务写入节点+边
```

### 同名文件歧义处理（BuildFilenameMap）

```
BuildFilenameMap(allFiles)
    ├── 遍历所有 FileRecord
    ├── filename → nodeId 映射
    └── 同名文件（不同目录）标记为 ambiguous，短名解析返回 null
        → 悬空链接，节点类型标记为 external
```

### 节点 ID 规范化

所有路径统一转换为 `/` 分隔、去掉前导 `/`：

```csharp
NormalizeNodeId("E:\\notes\\foo.md") → "E:/notes/foo.md"
```

### 文件删除

```
RemoveAsync(filePath)
    ├── DeleteHeadingNodesAsync(nodeId)  ← 先删 heading 子节点
    └── DeleteNodeAsync(nodeId)          ← 删主节点及其所有边
```

---

## 文件变动监听

**实现**：`src/ReferenceRAG.Core/Services/Indexing/FileMonitorService.cs`  
**接口**：`IFileMonitorService`

### 监听机制

每个源文件夹 × 每个文件模式创建一个 `FileSystemWatcher`（watcher key = `{sourceName}_{pattern}`）：

```csharp
watcher.Filter = "*.md";             // 或 "*.txt" 等
watcher.IncludeSubdirectories = true;
watcher.NotifyFilter = FileName | LastWrite | Size;
```

默认文件模式：`["*.md", "*.txt"]`，可按源自定义。

### 防抖（Per-File Debounce）

每个文件独立维护一个 `Timer`，500ms 内重复事件重置定时器，超时后触发 `Flush`：

```
文件 A 变化  ──┐
文件 A 变化  ──┤─ 500ms 内重置 ─┐
               └────────────────→ Flush(A)  → FileChanged 事件
```

好处：多次快速保存只触发一次索引，且不同文件之间不相互阻塞。

### 变更类型判定

`Flush` 时根据文件是否存在确定最终类型（Renamed 保持不变）：

```
Created / Modified → 文件存在? Modified : Deleted
Renamed            → Renamed（携带 OldFilePath）
Deleted            → Deleted
```

### 混合模式（Hybrid Mode）

当监控源数量超过系统 inotify 限制（默认 8192）时自动启用：

- 活跃目录（5 分钟内有事件）：继续使用 FSW
- 非活跃目录：降级为 30 秒轮询扫描（`ScanIdleDirectories`）

### 大目录并行扫描

`ParallelScanFiles(root)` 利用第一层子目录并行（最多 8 线程）加速初始扫描：

```csharp
topDirs.AsParallel()
       .WithDegreeOfParallelism(Math.Min(ProcessorCount, 8))
       .SelectMany(dir => EnumerateFiles(dir, pattern, AllDirectories))
```

---

## 统一索引流水线

**实现**：`src/ReferenceRAG.Core/Services/Indexing/FileIndexPipeline.cs`  
**接口**：`IFileIndexPipeline`

### 三阶段设计

#### Phase 1：PrepareAsync（准备）

```
PrepareAsync(filePath, sources, force)
    ├── 读取文件内容，计算 SHA-256 hash
    ├── 匹配所属 SourceFolder（最长路径前缀优先）
    ├── 查询已有 FileRecord
    ├── 跳过条件：hash 相同 AND status='complete'（中断恢复：pending 强制重索引）
    ├── MarkdownChunker.Chunk()  → 切片列表
    ├── 写入 FileRecord（status='pending'）
    ├── 清理旧 Vector → 旧 Chunk → 旧 BM25（顺序不能颠倒）
    └── TextEnhancer.Enhance() 填充 EnhancedContent
```

> `status='pending'` 的核心作用：应用崩溃重启后，hash 相同的文件也会被重新索引，防止索引不完整。

#### Phase 2：向量计算（调用方负责）

```
// 调用方（IndexSingleAsync 或 IndexingPipeline）执行：
EmbeddingService.EncodeBatchAsync(chunks, EmbeddingMode.Document)
    → VectorStore.UpsertChunksAsync()
    → VectorStore.UpsertVectorsAsync()
```

#### Phase 3：FinalizeAsync（收尾）

```
FinalizeAsync(ctx, filenameMap)
    ├── BM25Store.IndexBatchAsync(chunks)         ← 总是执行
    ├── GraphIndexingService.UpdateGraphAsync()   ← updateGraph=true 时执行
    └── VectorStore.MarkFileStatusAsync('complete')  ← 标记完成
```

### VectorOnly 模式

仅重算向量而不重新分块（用于切换模型后补全向量）：

```
PrepareVectorOnlyAsync(filePath)
    ├── 从 DB 读取已有 Chunk（不重新分块）
    ├── 删旧向量
    └── MarkFileStatusAsync('pending')
    → 调用方重算向量 → FinalizeAsync(updateGraph: false)
```

### 删除操作

```
DeleteFileAsync(fileId)
    ├── 收集所有 ChunkId
    ├── VectorStore.DeleteFileAsync()  ← 级联删除 Chunk + Vector
    ├── BM25Store.DeleteDocumentsByIdsAsync(chunkIds)
    └── GraphIndexingService.RemoveAsync(filePath)

DeleteSourceAsync(sourceName)
    ├── 遍历所有属于该源的文件
    └── 依次执行上述删除逻辑
```

---

## 自动增量索引

**实现**：`src/ReferenceRAG.Service/Services/AutoIndexService.cs`  
实现 `IHostedService`，应用启动时自动运行。

### 事件驱动队列模型

```
FileMonitorService.FileChanged
    │
    ▼  OnFileChanged() 入队
Queue<FileChangeEventArgs>
    │
    ▼  Timer 每 1s 消费一条
ProcessChangeAsync()
    ├── ChangeType.Deleted  → pipeline.DeleteFileByPathAsync()
    └── Modified/Created/Renamed → pipeline.IndexSingleAsync()
                                    (Renamed 传 oldFilePath 清理旧路径索引)
```

### 互斥保护（FileProcessingGuard）

防止同一文件被多个任务并发索引（Bug D 修复）：

```csharp
if (!_guard.TryAcquire(filePath)) return;  // 已在处理中，跳过
try { await _pipeline.IndexSingleAsync(...); }
finally { _guard.Release(filePath); }
```

### 重命名处理（Bug B 修复）

`FileChanged` 事件携带 `OldFilePath`，`IndexSingleAsync` 内部先清理旧路径索引再索引新路径：

```csharp
IndexSingleAsync(newPath, sources, oldFilePath: e.OldFilePath)
    ├── DeleteFileByPathAsync(oldFilePath)  ← 清理旧路径
    └── PrepareAsync(newPath) → ... → FinalizeAsync()
```

---

## 数据流全链路图

```
磁盘文件（.md/.txt）
        │
        │ FileSystemWatcher / 轮询
        ▼
FileMonitorService
  └── 防抖 500ms
        │ FileChanged(path, ChangeType, source)
        ▼
AutoIndexService._indexQueue
        │ Timer 消费
        ▼
FileIndexPipeline.IndexSingleAsync(path)
        │
        ├─[1] SHA-256 hash 变化检测
        │
        ├─[2] MarkdownChunker.Chunk()
        │         按标题切片 → 短节合并 → 过长切分 → 重叠
        │
        ├─[3] TextEnhancer.Enhance()
        │         填充 EnhancedContent（标题 + 关键词增强）
        │
        ├─[4] EmbeddingService.EncodeBatchAsync(EnhancedContent, Document)
        │         tokenize → ONNX 推理 → mean/cls pooling → L2 归一化
        │
        ├─[5] SqliteVectorStore.UpsertChunksAsync() + UpsertVectorsAsync()
        │         向量 + 元数据写入 SQLite
        │
        ├─[6] Fts5BM25Store.IndexBatchAsync()
        │         中英文分词 → FTS5 虚拟表写入
        │
        ├─[7] GraphIndexingService.UpdateGraphAsync()
        │         提取 wiki-link → 构建 node/edge → 单事务写入
        │
        └─[8] MarkFileStatusAsync('complete')
                  防中断重索引标记
```

### 搜索时反向使用

检索时三路并行，结果通过 RRF（Reciprocal Rank Fusion）或加权融合：

```
用户查询
    ├── EmbeddingService.EncodeAsync(query, Query)  → 向量相似度检索
    ├── Fts5BM25Store.SearchAsync(query)            → BM25 全文检索
    └── GraphIndexingService（可选）                → 图邻居扩展
         ↓
    HybridSearchService → 结果融合 → OnnxRerankService（可选精排）
```

---

## 关键配置参数

| 配置路径 | 参数 | 默认值 | 说明 |
|----------|------|--------|------|
| `Embedding.BatchSize` | BatchSize | 32 | 单次向量化批次大小 |
| `Embedding.MaxSequenceLength` | MaxSequenceLength | 512 | token 最大长度 |
| `Embedding.ModelPath` | ModelPath | — | ONNX 文件绝对路径 |
| `Chunking.MaxTokens` | MaxTokens | 512 | 单 chunk 最大 token 数 |
| `Chunking.MinTokens` | MinTokens | 50 | 短节合并阈值 |
| `Chunking.OverlapTokens` | OverlapTokens | 50 | chunk 重叠 token 数 |
| `FileMonitor.DebounceMs` | debounceMs | 500 | 文件事件防抖延迟（ms） |
| `ModelsRootPath` | — | — | 模型根目录（含 Embedding/ Reranker/ 子目录） |

---

*文档生成于代码版本 commit `6d719cf`，如有结构变更请同步更新。*
