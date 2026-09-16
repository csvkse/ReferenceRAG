# 索引导航审计复核与修复任务计划

- 日期：2026-09-16
- 范围：两份复核报告的合并项（已去重）+ 本次独立复核新增项
- 复核方式：只读源码逐行核对（复核阶段）+ 按计划实施修复（执行阶段）

## 执行状态（2026-09-16 完成）

计划中的全部修复已实施并通过验证：主力测试 339（291 通过 + 48 跳过）、Host 测试 9，均为 0 失败；前端文件通过 node 语法检查。
新增回归测试 18 个（AuditFixRegressionTests）覆盖：overlap 预算、代码块内标题、超长单句/代码块拆分、LIKE 通配符转义、chunk 扩展字段往返、pending 搜索过滤、token 估算一致性、配置校验、pending 恢复、ChunkingHash。
本文件「三、修复任务计划」部分已打勾标注完成项，实施细节见 git diff。

## 复核结论

报告中的 21 项问题**全部属实**，无推翻结论的误报。其中部分项影响比报告描述的更广（见「新发现问题」）。
另新增 4 项报告未覆盖的问题（2 项同类的 SqliteVectorStore 未持锁方法、1 项 _modelDimensions 并发读、1 项前端 Dashboard 加载态）。

---

## 一、逐项复核结果

### P1（9 项 + 补充项）

| # | 位置 | 结论 | 证据 |
|---|------|------|------|
| 1 | FileIndexPipeline.cs:54 | ✅ 属实 | `SimpleTokenizer.CountTokensSpan` 中 `chineseTokens = (int)(chineseCount / 1.5)`；`TokenEstimator.EstimateTokens` 同样是 `chineseCount / 1.5 + otherCount / 4`。`MaxTokens=512` 时纯中文 chunk 可达 ~768 字，而 BGE 类 tokenizer 每汉字约 1 token，超限被静默截断 |
| 2 | FileIndexPipeline.cs:126 | ✅ 属实 | `TextEnhancer.Enhance` 追加 `[标题]/[章节]/[标签]/[领域]/[要点]` 前缀；`LimitEmbeddingInputAsync` 在 `Mode != "openai"` 时直接返回 `(0,false)`，ONNX 模式无任何预算预检；`EmbeddingService.cs:445` 的 `_tokenizer.Tokenize(textList, MaxSequenceLength, ...)` 静默截断 |
| 3 | ObsidianRagConfig.cs:181 | ✅ 属实 | `ChunkingConfig.MaxTokens=512`、`EmbeddingConfig.MaxSequenceLength=512`、`ApiMaxInputTokens=512` 三个值各自独立，ConfigManager 内无交叉校验 |
| 4 | MarkdownChunker.cs:267 | ✅ 属实 | flush 后 `overlap = TakeTrailingParagraphs(buffer, OverlapTokens)`（≤50）+ 新段落（≤512），`bufferTokens` 可到 562 > 512，后续整块成 chunk 超限 |
| 5 | MarkdownChunker.cs:379 | ✅ 属实 | `SplitLongParagraph` 中单句 > MaxTokens 时仍入 buffer 成 chunk；`para.IsCode && PreserveCodeBlocks` 整块成 chunk 不拆分 |
| 6 | MarkdownChunker.cs:146 | ✅ 属实 | `ExtractSections` 直接 `HeadingPattern.Match(line)`，无 fenced code 状态；代码内 `# comment` 会被当标题，且 `PreserveHeadings=false` 时该行被丢弃（`sectionStart = lineNum + 1`，标题行不进入内容） |
| 7 | FileIndexPipeline.cs:369 | ✅ 属实 | `SplitWithExactTokenCounterAsync` 新 chunk 写 `Content = part`、`EnhancedContent = part`（覆盖原始内容，BM25/展示用错文本）；未复制 `Source`、`AggregateRange`、`ChunkOrder`、`ContentHash`（回落默认值） |
| 8 | FileIndexPipeline.cs:159 | ✅ 属实 | `PrepareVectorOnlyAsync` 对存量 chunk 调用 `LimitEmbeddingInputAsync`，精确拆分模式会改 chunk 列表并写回 `Content`；`FinalizeAsync` 中 BM25 用拆分后文本重建，图谱不更新。前端 Sources.js:363 文案「仅重推向量，不动分块/BM25/图谱」与实际不符 |
| 9 | StartupSyncService.cs:425 | ✅ 属实 | `ProcessModifiedFilesAsync` 仅按 `diskMtime > IndexedAt+1s` 粗筛，全文件无 `pending` 扫描（grep 无命中）；Phase1 后崩溃或 VectorOnly 删向量后崩溃会滞留 pending 文件 |
| 10 | FileRecord.cs:20 | ✅ 属实 | `FileRecord` 无 chunking 配置 hash/version，改 `MaxTokens/MinTokens/Preserve*` 后未变更文件因 hash+mtime 相同被跳过 |
| 11 | SqliteVectorStore.cs:529 | ✅ 属实 | `StreamAllFilesAsync`、`GetVectorByChunkIdAsync`、`GetVectorsByChunkIdsAsync`、`SearchAsync`（两重载）均不取 `_writeLock`，直接共享 `_connection` 并发读写 |
| 12 | SqliteGraphStore.cs:153 | ✅ 属实 | `DeleteHeadingNodesAsync`/`UpsertFileGraphAsync` 用 `fileNodeId + "#%"` 做 `LIKE`；`nodeId = NormalizeNodeId(file.Path)` 含路径，`_`（如 `my_file.md`）和 `%` 会被当通配符，可能误删 `myXfile.md#...` 节点 |
| 13 | SqliteVectorStore.cs:709 | ✅ 属实（补充项） | `UpsertChunkAsync`/`UpsertChunkBatchAsync` 的 SQL 列缺 `source`、`enhanced_content`、`tags`、`keywords`、`aggregate_range`；`ReadChunkRecord` 同样不还原这些字段 → VectorOnly 与重启后 `EnhancedContent`（null 时退化回 `Content`）、`Source`、`AggregateRange` 全丢 |

### P2（8 项）

| # | 位置 | 结论 | 证据 |
|---|------|------|------|
| 14 | FileIndexPipeline.cs:329 | ✅ 属实 | `LimitEmbeddingInputAsync` 只捕获 `NotSupportedException`；`LlamaCppTokenCounter` 对 404/405 抛 `NotSupportedException`，但 `EnsureSuccessStatusCode()` 对 401/400/500 抛 `HttpRequestException`，会直接阻断整个索引 |
| 15 | StorageExtensions.cs:15 | ✅ 属实 | vectors.db（共享连接+锁）、bm25.db、graph.db 三库独立，`FinalizeAsync` 无跨库事务；失败窗口存在新旧 chunk 混合/BM25 孤儿/图谱滞后 |
| 16 | index.js:60 | ✅ 属实 | `connection.on('IndexProgress', (_data) => {...})` 参数命名为 `_data` 并丢弃，`progressUpdates` 永远是空数组 |
| 17 | IndexService.cs:224 | ✅ 属实 | `IndexJob.CurrentFile`（IndexService.cs:384）从未被赋值，`/api/index/jobs` currentFile 永远空串；仅在 `IndexProgress` 事件里带 `ctx.FileRecord.FileName` |
| 18 | IndexService.cs:221 | ✅ 属实 | 事件只在 Phase3 finalize 成功后才发布，不代表正在读取/分块/嵌入；并发下单一 `ProcessedFiles` 字段语义不足 |
| 19 | IndexService.cs:258 | ✅ 属实 | skip 文件（`PrepareAsync` 返回 null）不计入 `ProcessedFiles`；`IndexCompleted` 的 `TotalChunks = TotalVectors = job.ProcessedFiles`（文件数，非 chunk/向量数） |
| 20 | Dashboard.js:75 | ✅ 属实 | 模板读 `progress.sourceId/sourceName/status/error/processedFiles/totalFiles/currentFile`，但 `IndexProgressEvent` 只有 `indexId/processedFiles/totalFiles/currentFile/timestamp`，字段错位 |
| 21 | SimpleTokenizer.cs:31 | ✅ 属实 | 与 `TokenEstimator` 逻辑不一致：SimpleTokenizer 数字按 /3、其它按 /2，TokenEstimator 全部其它按 /4；且边界判断 `c > 0x4E00 && c < 0x9FFF` 排除了 U+4E00/U+9FFF 本身，SimpleTokenizer 用 `>=/<=` 含两端。测试用 TokenEstimator 断言分片结果会掩盖偏差 |

---

## 二、新发现问题（本次复核新增）

| # | 优先级 | 位置 | 说明 |
|---|--------|------|------|
| N1 | P1 | SqliteVectorStore.cs:705 `UpsertChunkAsync` | 单条写 chunk 不取 `_writeLock`（批量路径 `UpsertChunksAsync` 持锁，但公开的单条 API 可被并发调用） |
| N2 | P1 | SqliteVectorStore.cs:1175 `DeleteVectorAsync` | 遍历模型表 DELETE 不取锁，索引/删模型并发时会与写入冲突 |
| N3 | P2 | SqliteVectorStore.cs:1071/1113 | `GetVectorByChunkIdAsync`/`GetVectorsByChunkIdsAsync` 遍历 `_modelDimensions`（运行期可被 `ReloadModelAsync`/模型注册修改），未持锁读字典 + 未持锁查库，并发换模型时可能抛「集合已修改」 |
| N4 | P3 | Dashboard.js:58-64 `handleStartIndex` | 本地 `isIndexing` 在 `await startJob()` 返回后立即复位，只覆盖 HTTP 请求期而非整个索引过程；按钮 loading 一闪而过，真正进度依赖 store 轮询 |

补充说明：
- `SearchByAggregateTypeAsync`/`SearchInIdsAsync`（SqliteVectorStore.cs:1327/1351）同样不持锁，属 N1/N2 同类，修复时一并对齐。
- `TokenEstimator.EstimateTokens` 的 `otherCount/4` 与 SimpleTokenizer 的 `other/2 + number/3` 不同——统一实现时可顺手消除。

---

## 三、修复任务计划

按「数据正确性 → 生命周期可靠性 → 存储层 → 事件/前端 → 兼容性」分组，每项给出验收标准。

### 阶段 A：分块与嵌入预算（数据正确性，P1）✅ 已完成

**A1. 统一 token 估算**
- 文件：SimpleTokenizer.cs、TokenEstimator.cs、MarkdownChunker.cs、FileIndexPipeline.cs
- 以 `ITokenizer` 为唯一入口（DI 已注册 SimpleTokenizer），删除/弃用 `TokenEstimator` 的散落调用，或让 TokenEstimator 内部复用 SimpleTokenizer；测试断言统一改为注入的 tokenizer。
- 验收：`grep TokenEstimator` 仅剩内部一处；对同一中文文本，chunker 与 pipeline 的预算口径一致。

**A2. 中文保守预算**
- 文件：SimpleTokenizer.cs
- 中文改为 1 字符/token（`chineseTokens = chineseCount`），保留英文 4 字符/token、数字 3 字符/token。
- 验收：`MaxTokens=512` 时纯中文 chunk ≤ 512 字；新增 `SimpleTokenizerTests` 断言中文 100 字 = 100 tokens。

**A3. Embedding 预算预检（含 ONNX）**
- 文件：FileIndexPipeline.cs（`LimitEmbeddingInputAsync`）
- 将预算检查从「仅 openai 模式」改为对所有模式生效：ONNX 用 `Embedding.MaxSequenceLength - 预留` 做字符预算（按 A2 口径），超限截断或拆分；openai 模式保留精确 token 探测。
- 预留值至少覆盖 special tokens（BOS/EOS/[CLS]/[SEP]）与 `TextEnhancer` 前缀（可先按增强文本字符数 + 4 估算）。
- 验收：ONNX 路径构造超限 chunk 时输出的 `EnhancedContent` 不超过 MaxSequenceLength；新增测试断言无静默超限。

**A4. 拆分 payload 与展示内容分离**
- 文件：FileIndexPipeline.cs（`SplitWithExactTokenCounterAsync`）
- `Content` 永远保留原始增强前文本；拆分输入写入独立字段（如复用 `EnhancedContent` 存拆分后文本），新段复制 `Source`、`AggregateRange`、`ChunkOrder`、`ContentHash`、`StartColumn/EndColumn`。
- 验收：精确拆分后 `Content` 拼接 == 原始 `Content`；新段 BM25 源文本与显示一致；`ChunkOrder` 保持单调递增。

**A5. 配置一致性校验**
- 文件：ObsidianRagConfig.cs / ConfigManager
- 启动与保存时校验：`Chunking.MaxTokens ≤ Embedding.MaxSequenceLength(或 ApiMaxInputTokens) - 预留`；不满足时给出明确错误/告警并提示调整。
- 验收：配置 `MaxTokens=1024 > MaxSequenceLength=512` 时保存被拒或明确告警；新增 ConfigManager 校验测试。

**A6. MarkdownChunker overlap 重检**
- 文件：MarkdownChunker.cs:267-283
- overlap 与新段落合并后再计算 `bufferTokens`，超 `MaxTokens` 时缩减 overlap（复用 `TakeTrailingParagraphs` 的预算逻辑）直至不超限。
- 验收：任意段落序列下所有 chunk 的估算 token ≤ MaxTokens（除 A7 的强制拆分路径）；新增覆盖 overlap 边界测试。

**A7. 超长单元处理**
- 文件：MarkdownChunker.cs（`SplitLongParagraph`、代码块分支）
- 单句/超长代码块超出预算时：按句内标点继续拆分；代码块超限时按行拆分并在 `ChunkType` 标记（如新增 `ChunkType.Truncated`）或至少记 warning「该 chunk 会被嵌入截断」。
- 验收：构造 3000 字无分段文本与 2000 行代码块，输出 chunk 均 ≤ MaxTokens 或带截断标记。

**A8. ExtractSections fenced code 状态**
- 文件：MarkdownChunker.cs:134-223
- 在 `ExtractSections` 中维护 ``` 围栏状态，围栏内不匹配标题；`PreserveHeadings=false` 时围栏内 `#` 行保留进内容。
- 验收：包含 ``` 代码块（内含 `#`/`##` 行）的文档，分段结果与原代码块一致；新增回归测试。

### 阶段 B：索引生命周期可靠性（P1）✅ 已完成

**B1. 启动同步纳入 pending**
- 文件：StartupSyncService.cs、FileIndexPipeline.cs
- 启动扫描时显式加入 `IndexedStatus == "pending"` 的文件（无论 mtime），以 `force` 重跑完整流水线。
- 验收：模拟 Phase1 后中断（pending 文件 + hash 一致），重启后自动重新索引并置 complete。

**B2. 分块配置版本化**
- 文件：FileRecord.cs、SqliteVectorStore（files 表）、PrepareAsync
- 持久化 `ChunkingHash`（MaxTokens/MinTokens/OverlapTokens/Preserve* 的哈希）；`PrepareAsync` 跳过条件增加 `ChunkingHash 一致` 判断。
- 验收：仅修改 MaxTokens 后文件被重新分块；写入迁移脚本（ALTER TABLE files ADD COLUMN chunking_hash）。

**B3. VectorOnly 语义修正**
- 文件：FileIndexPipeline.cs（`PrepareVectorOnlyAsync`、`LimitEmbeddingInputAsync`）、Sources.js 文案
- VectorOnly 不修改已存 chunk：生成临时 embedding payload（新 chunk 副本 + 新 GUID）用于嵌入，不写回 chunks 表；前端文案改为「仅重推向量，不修改分块/BM25 内容」；若保留拆分语义则同时说明「拆分产生的临时段不在图谱中」。
- 验收：VectorOnly 前后 `GetChunksByFileAsync` 结果一致（id/content 不变），仅 vectors 表变化。

### 阶段 C：存储层（P1）✅ 已完成

**C1. SqliteVectorStore 统一持锁**
- 文件：SqliteVectorStore.cs
- 为 `StreamAllFilesAsync`、`GetVectorByChunkIdAsync`、`GetVectorsByChunkIdsAsync`、`SearchAsync`（两重载）、`SearchByAggregateTypeAsync`、`SearchInIdsAsync`、`UpsertChunkAsync`、`DeleteVectorAsync` 补 `_writeLock`（StreamAll 需在枚举器生命周期内持锁或改快照读取）。
- 验收：新增并发测试——索引线程 + 搜索线程 + 删除线程同时跑 N 轮，无 `SQLiteException: database is locked / command not prepared`；`_modelDimensions` 读改为线程安全（Copy 到局部或 ConcurrentDictionary）。

**C2. Graph LIKE 通配符转义**
- 文件：SqliteGraphStore.cs:153/174-196
- 使用 `LIKE ... ESCAPE '\'` 对 `fileNodeId` 中的 `%`/`_`/`\` 转义；或改为精确匹配：`id = @prefix + '#' + ...` 不可行时先查 `WHERE id LIKE @p ESCAPE` 再按前缀精确过滤。
- 验收：构造 `my_file.md` 与 `myXfile.md` 两个文件节点，删除一个时另一个不受影响；新增测试。

**C3. chunk 字段持久化**
- 文件：SqliteVectorStore.cs（SQL + ReadChunkRecord）、迁移脚本
- chunks 表新增 `source`、`enhanced_content`、`tags`、`keywords`、`aggregate_range` 列（JSON 序列化 tags/keywords）；读写映射补齐。
- 验收：Upsert → Reload 往返后 `EnhancedContent/Source/Tags/Keywords/AggregateRange` 完整；VectorOnly 与重启后向量输入不再退化回 Content。

### 阶段 D：事件与前端（P2）✅ 已完成

**D1. 进度事件落地**
- 文件：wwwroot/app/stores/index.js、IndexService.cs
- store 保存每个 `IndexProgress` payload（按 indexId 聚合：processedFiles/totalFiles/currentFile/error），暴露 `getProgress(indexId)`；`IndexCompleted` 时保留最终状态。
- 验收：Dashboard 完成一次索引后能看到进度列表与当前文件。

**D2. 事件模型与 Dashboard 字段对齐**
- 文件：IndexService.cs（事件定义）、Dashboard.js
- 统一 `IndexProgressEvent`/`Dashboard` 使用的字段名（indexId/processedFiles/totalFiles/currentFile/status/timestamp），去掉模板中对不存在字段（sourceId/sourceName/error）的依赖；错误经 `IndexCompleted.errors` 展示。
- 验收：模板字段与事件模型一一对应；eslint/运行时无 undefined。

**D3. Job 状态增强**
- 文件：IndexService.cs、IndexJob.cs
- 维护 per-job active files 集合（Phase1 开始加入、Phase3 完成移除），`/api/index/jobs` 返回 `currentFiles` 数组 + `processed/skipped/total`；`IndexCompleted` 改报真实 `totalChunks/totalVectors`（从 pipeline/IndexingPipeline 结果累加）。
- 验收：任务运行中 jobs API 可见正在处理的多个文件；完成统计中 chunks/vectors 为真实数量，skipped 单独计数。

### 阶段 E：兼容性与健壮性（P2）✅ 已完成

**E1. tokenize 探测容错**
- 文件：FileIndexPipeline.cs、LlamaCppTokenCounter.cs
- 识别 401/400/501/5xx 为「不支持/不可用」：401/400/501 降级为字符截断（记录 warning），网络错误（Timeout/HttpRequestException 中的连接类）走重试（指数退避 2-3 次）后再降级。
- 验收：mock tokenize 返回 501 → 索引继续（字符截断）；返回 503 两次 → 第三次成功或按降级处理，不抛阻断异常。

**E2. 跨库一致窗口**
- 文件：StorageExtensions.cs / FileIndexPipeline.cs（FinalizeAsync）
- 短期方案：搜索过滤 `IndexedStatus != 'complete'` 的文件（query 侧 join files 状态）；长期方案：引入版本化候选集（chunk 带 `index_version`，搜索只认最新版本）。
- 验收：索引进行中发起搜索，不返回半成品 chunk；单元测试构造 pending 文件断言被过滤。

### 阶段 F：测试补充（贯穿以上各项）✅ 已完成

补齐既有测试未覆盖的边界：

1. pending 恢复：pending 文件 + hash 一致 → 启动同步重跑。
2. VectorOnly 拆分：断言 chunks 表不变、vectors 更新、文案修正。
3. 代码块内标题：``` 内 `# comment` 不影响分段。
4. SQLite 并发读：并发 Search/Delete/Index 无异常。
5. LIKE 通配符：`_`/`%` 路径图清理不误删。
6. 真实 tokenizer 截断：ONNX 路径超限输入 + BGE 口径预算断言。
7. chunk 字段往返：EnhancedContent/Source/Tags/Keywords/AggregateRange 持久化。
8. token 估算一致性：SimpleTokenizer 与 chunker/pipeline 同口径。

---

## 四、建议的执行顺序

1. **A1+A2**（估算口径，影响分块正确性，后续测试依赖它）
2. **B3 + C3**（VectorOnly 与持久化字段，互相依赖，先定数据模型再做迁移）
3. **A3+A4**（预算预检与拆分 payload，依赖 A1）
4. **A6/A7/A8**（chunker 三项，独立可并行）
5. **C1/C2**（存储锁与转义，独立）
6. **B1/B2 + E1**（生命周期与容错）
7. **D1/D2/D3**（事件与前端）
8. **E2 + F**（一致性窗口与测试收尾）

预计工作量：阶段 A/B/C 为核心（约 60%），D/E 为外围，F 与各阶段并行补充。