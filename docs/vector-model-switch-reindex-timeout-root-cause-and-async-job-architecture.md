# 切换向量模型提示异常：根因调研与异步重构架构方案

> 日期：2026-11-14
> 环境：正式环境 `http://<PROD_HOST>:5123/knowledge-bases`（Docker，AMD Radeon APU / Vulkan，模型目录 `/app/models`）
> 现象：默认知识库（纯词法 `spec_lexical_only`）点击「绑定模型 → 确认绑定并计算切片向量」后，按钮长时间 loading，最终提示「重构失败: …」异常，重试必现。
> 结论先行：**「蓝绿影子重构」的存储层设计是正确的，但编排层把一次约 4.4 小时的全量重嵌入塞进了一个同步 HTTP 请求，而前端 axios 超时为 60 秒**。客户端 60s  abort → 服务端 `RequestAborted` 取消 → 重构中途废弃 → 每次重试都失败。这是问题本质；另有 4 个放大/潜伏缺陷（ModelRouter 工厂未注册、静默模拟降级、GGUF 512 截断、维度门禁缺失）一并给出同类门禁。

---

## 1. 正式环境事实基线（只读诊断取证）

| 项 | 值 | 取证接口 |
|---|---|---|
| 知识库 | `dadd847d18b54162` 默认知识库，`Mode=PureLexical`，`EmbeddingSpecId=spec_lexical_only`，`ChunkingSpecId=spec_chunk_lexical_dense`（MaxTokens=400） | `GET /api/knowledge-bases`、`/chunking-specs` |
| 规模 | **165 个文档 / 24,776 个切片** | `GET .../documents?pageSize=1`、`GET .../chunks?pageSize=1` |
| 目标规格 | `local_bge-m3`：BGE M3，1024d，MaxTokens=8192，**声明 `SearchBackend=LocalOnnx`** | `GET /api/knowledge-bases/embedding-specs` |
| 物理模型 | 仅有 **GGUF Q4_0**（`bge-m3-Q4_0.gguf`，`HasOnnx=false`），Vulkan 全量 offload，State=Ready | `GET /api/models/runtime-status`、`/api/models/downloaded` |
| 配置健康 | `Aligned`，分段门禁 400 ≤ 8192 通过 | `GET .../drift` |
| 批嵌入实测 | **32 条不同文本 ≈ 20.3s**（返回 32×1024d，HTTP 200） | `POST /v1/embeddings` 纯计算接口计时（`time_total=20.255s`） |

推算：24,776 / 32 = **775 批 × ~20.3s ≈ 15,700s ≈ 4.4 小时**（尚未计每批 ModelRouter 失败租约的额外开销）。

> 取证边界：对正式环境的 `POST .../reindex` 属于共享状态变更，被安全策略拦截未执行；异常机制由「同步链路代码 + 60s 客户端超时 + 实测吞吐」三方证据闭环证明，前端 toast 文案按 `KnowledgeBases.js:297` 推断为 `重构失败: timeout of 60000ms exceeded`（或代理层 504/Network Error，机制相同）。

## 2. 链路走读与异常抛出点

```
KnowledgeBases.js handleTriggerReindex (286-301, axios timeout=60000 @ api/index.js:24)
  → POST /api/knowledge-bases/{id}/reindex        KnowledgeBasesController.cs:464-488
  → KnowledgeBaseService.TriggerModelReindexAsync  KnowledgeBaseService.cs:1176-1287
      ├─ 分段相容门禁 1200-1226（本例通过）
      ├─ vectorProvider 1230-1259
      │    ├─ ModelRouter.AcquireLeaseAsync<IEmbeddingBackendAdapter> → 必抛「未注册模型适配器工厂」(ModelRouter.cs:128) → catch 回退
      │    └─ 回退全局 _embeddingService.EncodeBatchAsync（QueryEmbeddingCacheProxy → LazyEmbeddingService → GgufBackendStrategy）
      └─ lease.Store.ExecuteShadowModelReindexAsync  SqliteKnowledgeBaseStore.cs:3866-4019
           ├─ 775 批循环，每批 ct.ThrowIfCancellationRequested() (3921)
           ├─ 数量门禁 3926-3929（只查数量，不查维度）
           └─ 单事务原子换表 + manifest (3971-4008) → 事务外重载 SIMD 池 (4011)
```

### 2.1 直接原因：请求内同步长任务 × 60s 客户端超时

- 前端 `axios.create({ timeout: 60000 })`（[app/api/index.js:24](../Business/ReferenceRAG.Business/wwwroot/app/api/index.js#L24)），`handleTriggerReindex` 直接 `await` 整个重构（[KnowledgeBases.js:290](../Business/ReferenceRAG.Business/wwwroot/app/views/KnowledgeBases.js#L290)）。
- 控制器把 `CancellationToken ct`（ASP.NET Core 绑定为 `HttpContext.RequestAborted`）一路传进批循环。客户端 60s 断开 → `ct` 取消 → [SqliteKnowledgeBaseStore.cs:3921](../Infrastructure/ReferenceRAG.Infrastructure/Features/KnowledgeBase/Storage/SqliteKnowledgeBaseStore.cs#L3921) 抛 `OperationCanceledException` → 影子表残留、KB 不切换、连接器/规格不变。
- UI 文案承诺「**后台**影子表分批计算、零停机」（[KnowledgeBases.js:1226](../Business/ReferenceRAG.Business/wwwroot/app/views/KnowledgeBases.js#L1226)），但实现没有任何后台作业语义——**承诺与实现的架构性错位**是问题本质。

### 2.2 放大因素：ModelRouter 嵌入工厂在生产代码中从未注册

- 全仓库 `RegisterAdapterFactory<IEmbeddingBackendAdapter>` 仅出现在测试（`KnowledgeBaseArchitectureGateTests.cs:587,613` 注册的是 chat/agent 工厂，嵌入工厂连测试都没注册）。
- 于是每批都走 [ModelRouter.cs:128](../Infrastructure/ReferenceRAG.Infrastructure/Features/ModelManagement/Runtime/ModelRouter.cs#L128) `throw new InvalidOperationException("未注册模型适配器工厂: …")` → 被 [KnowledgeBaseService.cs:1246-1249](../Business/ReferenceRAG.Business/Features/KnowledgeBase/Services/KnowledgeBaseService.cs#L1246) catch → 回退**全局**模型。775 批 = 775 次失败租约 + 显存预留抖动 + 警告日志刷屏。
- 语义错误：目标规格声明的 backend（`LocalOnnx`）被完全忽略，永远用全局模型编码。本例全局恰好也是 bge-m3 才「碰巧同模型」；若目标与全局不同，将**静默写入错误向量空间/维度**。`KnowledgeBaseSyncWorker.cs:117-133` 的入库路径同病。

### 2.3 潜伏缺陷（本次未爆、同类必爆）

1. **静默模拟降级**：`EmbeddingService.LoadModel` 失败 → `_simulationMode=true, Dimension=384`（[EmbeddingService.cs:278-285](../Infrastructure/ReferenceRAG.Infrastructure/Features/ModelManagement/Services/EmbeddingService.cs#L278)）；`LlamaEmbeddingAdapter` 模型缺失 → 384d 模拟向量（[LlamaEmbeddingAdapter.cs:61-67](../Infrastructure/ReferenceRAG.Infrastructure/Features/ModelManagement/Adapters/LlamaEmbeddingAdapter.cs#L61)）。若全局模型不可用，reindex 会「成功」写入 384d 假向量，而 manifest 宣称 1024d。
2. **维度门禁缺失**：影子写入只校验数量（3926），不校验 `vec.Length == targetSpec.Dimension`；原子换表前无维度/模拟模式验收。
3. **GGUF 512 静默截断**：`_batchTokenCapacity = Math.Min(effectiveCtx, 512u)`（[LlamaEmbeddingAdapter.cs:78](../Infrastructure/ReferenceRAG.Infrastructure/Features/ModelManagement/Adapters/LlamaEmbeddingAdapter.cs#L78)），超长 token 直接 `tokens[..512]`（219-221）。规格宣称 8192，实际向量只看见前 512 token——长切片尾部语义丢失，无警告。
4. **查询缓存代理污染**：`QueryEmbeddingCacheProxy` 是问句 LRU，却对 `EmbeddingMode.Document` 的批向量也缓存（[QueryEmbeddingCacheProxy.cs:122-177](../Infrastructure/ReferenceRAG.Infrastructure/Features/ModelManagement/Services/QueryEmbeddingCacheProxy.cs#L122)），文档向量挤占问句缓存、且模型切换后仅 `ReloadModelAsync` 清缓存。
5. **原子性缝隙**：换表事务提交后，SIMD 池重载在事务外（4011）；其若抛异常，DB 已宣称 hybrid 而内存池陈旧。取消路径残留 `chunk_vectors_shadow`（下次运行才 DELETE）。

## 3. 业界主流方案对比

| 方案 | 迁移编排 | 切换原子性 | 验收/回滚 | 对本系统的借鉴 |
|---|---|---|---|---|
| **Elasticsearch** | 新建 index + `_reindex` **异步 task API**（task id、进度、可取消），旧 index 继续服务 | alias 原子 swap（单请求内 remove+add） | 双写过渡、count 校验后切 alias | 「请求只 enqueue、后台 task 跑全量」正是本系统缺失的一层 |
| **Qdrant**（官方迁移教程） | blue-green 双 collection：建_new → 开双写 → scroll 分批 backfill（**持久化 offset 可_resume**）→ 原子 alias swap | alias swap | 切前验收：count 对齐 + 标注查询检索质量对比；保留旧 collection 观察期可回滚；backfill 期间暂停 delete/partial-update 防脏写 | 与本系统影子表同构；补「双写/暂停写、offset 续跑、切前验收」三件事 |
| **Milvus 3.0 / Zilliz** | 新 collection 或新增 vector field → backfill → 检索评估 → 灰度 → `alter_alias` 原子切换 | alias | 评估集对比、渐进 rollout | 「先验收后切换」门禁 |
| **pgvector / AlloyDB AI**（Google 零停机方案） | 新列/新表分批 backfill + `CREATE INDEX CONCURRENTLY`，不阻塞写路径 | 事务内 swap/视图切换 | 批次可重试、失败可续 | 批处理 + 不占用请求线程 |
| **通用 RAG 工程共识**（TopK 等） | index 级 blue-green、双写、backfill、验证、原子切、保留旧代 | pointer/alias | 维度/数量/质量三验收 | 直接映射为本文 §5 门禁 |

共同原则（本系统违反第 1 条）：
1. **迁移是持久化异步作业**：enqueue 立即返回（202 + jobId），进度/取消/续跑齐全；绝不在请求线程里跑全量。
2. 读路径全程服务旧代；写路径双写或暂停，防 backfill 脏写。
3. 切换前验收：数量对齐、维度一致、抽样检索质量；切换是单点原子操作。
4. 旧代保留观察期，可回滚。

## 4. 可复用基础设施盘点（不需要引入新第三方库）

| 设施 | 位置 | 复用方式 |
|---|---|---|
| **KB 异步作业系统** | `JobDefinition`（Operation/Status/Phase/TotalUnits/ProcessedUnits/ErrorMessage/IdempotencyKey）、`_catalog.SaveJobAsync`、`_runningJobs` CTS 注册表、`CancelJobAsync`；[KnowledgeBaseService.cs:626-688](../Business/ReferenceRAG.Business/Features/KnowledgeBase/Services/KnowledgeBaseService.cs#L626) `TriggerSyncJobAsync` 即「enqueue + Task.Run worker」现成模板 | 新增 `Operation:"reindex"` 作业，照抄 sync 的 enqueue 模式；worker 内跑现有 `ExecuteShadowModelReindexAsync` |
| 作业 REST | `JobsController` `/api/jobs` GET/cancel/delete/prune（[KnowledgeBasesController.cs:851-905](../Host/ReferenceRAG.ApiHost/Controllers/KnowledgeBasesController.cs#L851)） | 直接复用，无需新控制器 |
| 作业前端 API | `knowledgeBasesApi.getJob/listJobs/cancelJob`（[app/api/index.js:325-329](../Business/ReferenceRAG.Business/wwwroot/app/api/index.js#L325)）已定义但**无视图消费** | KnowledgeBases.js 增加进度抽屉轮询 `getJob` |
| 进度状态机 | `JobStateMachine.UpdateProgressAsync`（worker 用法见 [KnowledgeBaseSyncWorker.cs:98](../Infrastructure/ReferenceRAG.Infrastructure/Features/KnowledgeBase/Jobs/KnowledgeBaseSyncWorker.cs#L98)） | 每批更新 ProcessedUnits，UI 显示 ETA |
| 作业 UI 范式 | System.js 活跃/历史/取消（265-353 行，legacy index jobs） | 照搬到 KB reindex 抽屉 |
| 多后端策略工厂 | `ModelBackendStrategyFactory`（GGUF/ONNX/OpenAI，[ModelBackendStrategyFactory.cs:322-347](../Infrastructure/ReferenceRAG.Infrastructure/Features/ModelManagement/Strategies/ModelBackendStrategyFactory.cs#L322)）+ `LazyEmbeddingService` 状态机两阶段热重载 | 为 ModelRouter 注册**按目标规格解析**的嵌入工厂，替代「全局回退」 |
| 影子表 + 原子换表 | `ExecuteShadowModelReindexAsync`（存储层已正确） | 保留，移入 worker；补维度门禁与取消清理 |
| 后台服务范式 | `LexicalIndexBackfillWorker` 等 `BackgroundService` | 如需进程重启续跑，可升级为 hosted worker + 持久化 offset |
| 既有门禁测试 | `KnowledgeBaseModelLifecycleGateTests.Gate3`（148 行）、`KnowledgeBasePureLexicalModeGateTests`（361-378 行）、`KnowledgeBaseChunkingModelCouplingGateTests`（224 行） | 扩展为 §6 的 G1-G8；注意 Gate3 现状未注入 embedding service，走的是**零向量兜底**（KnowledgeBaseService.cs:1257-1258），故从未暴露真实编码路径问题 |

第三方库评估：Hangfire/Quartz.NET 可提供持久化作业+重试+dashboard，但本仓库已有 catalog 持久化 `JobDefinition` + CTS 取消 + REST，**P0/P1 无需新依赖**；仅当要求「进程崩溃续跑」时再评估 hosted worker + offset 持久化（自研即可，与 Qdrant 教程的 persistent offset 同构）。

## 5. 具体解决方案

### P0 — 止血：reindex 作业化（消灭 60s 超时类问题）

1. **服务层**：新增 `TriggerModelReindexJobAsync(kbId, targetSpecId)`：
   - 前置校验（分段门禁、目标规格存在、**单飞**：同 KB 已有 Queued/Running 的 reindex 作业 → 抛 `InvalidOperationException` → 409）；
   - 创建 `JobDefinition(Operation:"reindex", TotalUnits: chunkCount)` 并 `SaveJobAsync`；注册独立 CTS（**不绑定 RequestAborted**）；`Task.Run` 内执行现有 `ExecuteShadowModelReindexAsync`，每批 `UpdateProgressAsync`；
   - 成功：更新 KB 规格（现有 1268-1275 逻辑移入 worker 尾部）；失败/取消：`JobStatus.Failed/Cancelled` + `ErrorMessage`，清理影子表。
2. **控制器**：`POST {id}/reindex` 改为 enqueue，立即 `202 + JobDefinition`（保留旧同步实现为内部方法供测试/小库可选）。
3. **前端**：`handleTriggerReindex` 改为 enqueue 成功后关闭弹窗、打开进度抽屉轮询 `getJob`（1-2s 间隔），显示 `ProcessedUnits/TotalUnits` 与 ETA、取消按钮；终态 toast 成功/失败（失败展示 `job.errorMessage`）。axios 60s 不再约束重构时长。
4. **vectorProvider 修正**：worker 内通过 `ModelBackendStrategyFactory.ResolveStrategy(目标模型资产路径, 目标backend)` 创建**目标规格**适配器（或正式注册 ModelRouter 工厂）；删除「静默回退全局模型」，回退必须显式记录且维度验收兜底。

### P1 — 正确性门禁（切前验收，业界三验收落地）

5. **维度/模拟门禁**：每批校验 `vectors.Count == batch.Count` 且 `∀ vec.Length == targetSpec.Dimension`；provider `IsSimulationMode == true` → 作业直接 Failed（「目标模型未就绪，拒绝写入模拟向量」），影子表 DROP，KB 不变。
6. **原子性收敛**：SIMD 池重载改为「提交后惰性刷新」——池已按 `vectorGeneration`  keyed（`EnsureTensorProjectionPoolLoadedAsync` 1375 行），换表后仅 bump generation，读路径下次访问自动重载；worker 不再于事务外抛致命异常。
7. **GGUF 截断治理**：`_batchTokenCapacity` 上限与规格 MaxTokens 对齐（ctx 允许范围内），超 capacity 的文本按语义边界拆分编码后合并或显式告警 + `producer_cohort` 标记；禁止无警告截断。
8. **缓存代理边界**：`QueryEmbeddingCacheProxy` 仅缓存 `EmbeddingMode.Query`；Document 批直通底层。

### P2 — 韧性与一致性（对齐 Qdrant 教程）

9. **双写/暂停写**：reindex 运行期间，`KnowledgeBaseSyncWorker.CommitDocumentAsync` 同时写影子表（或 per-KB 暂停入库写），防 backfill 脏写；取消/失败时 DROP 影子。
10. **续跑**：影子表已按 chunk_id upsert，记录 offset（已处理 ordinal 水位）持久化到 `kb_manifest` 或 job 元数据；重启/重试跳过已处理批。
11. **观察期回滚**：换表后 `chunk_vectors_old` 当前被立即 DROP（3985）；改为保留一代（延迟清理作业），支持「回滚到上一代向量」。

### 运维临时措施（修复上线前，由用户自行决定是否执行）

- 用**不限时客户端**直连同一接口可完成本次升级（服务端只在客户端断开时取消）：例如 `curl --no-buffer -m 0 -X POST -H "X-API-Key: …" -H "Content-Type: application/json" -d '{"targetEmbeddingSpecId":"local_bge-m3"}' http://<PROD_HOST>:5123/api/knowledge-bases/dadd847d18b54162/reindex`，预计 ~4.4h，期间保持连接与进程稳定。缺点：无进度、不可续跑、代理/重启即废——仅作应急，不作为方案。
- 或临时把该页 axios 调用单独提高 timeout（仍不解决无进度/不可取消/重启即废，不推荐）。

## 6. 问题本质的同类门禁与回归测试清单

本质门禁命题：**「任何全库级重嵌入/重索引操作，不得占用请求线程；客户端超时/断开不得终止作业；切换前必须通过数量+维度+非模拟三验收」**。以下测试落入 `tests/ReferenceRAG.Tests`（服务/存储级）与 `tests/ReferenceRAG.Host.Tests`（HTTP 级），并挂入 `smoke-test.bat` / e2e 套件：

| 编号 | 门禁 | 关键断言 | 落点 |
|---|---|---|---|
| G1 | **enqueue 时限** | 5k 切片 + 慢速假 provider（模拟 20s/批）下，`POST reindex` < 2s 返回 202+jobId；响应返回前 provider 调用次数 == 0 | Host.Tests（TestServer） |
| G2 | **客户端断开不杀作业** | enqueue 后立即取消请求 token；作业仍跑完；KB 规格最终切换；影子表无残留 | Host.Tests |
| G3 | **维度/模拟验收** | provider 返回 384d 或 `IsSimulationMode=true` → job Failed、`ErrorMessage` 含维度/模拟字样；`chunk_vectors` 与 KB 规格逐字节不变 | Tests（扩 Gate3） |
| G4 | **单飞** | 同 KB 并发二次 enqueue → 409；不同 KB 互不影响 | Tests |
| G5 | **取消语义** | 运行中 cancel → Cancelled、影子 DROP、KB 不变；随后重新 enqueue 成功 | Tests |
| G6 | **纯词法绑定 E2E** | lexical KB → 绑定 → hybrid 检索向量命中且维度==规格；解绑 → 词法完整（扩 `KnowledgeBasePureLexicalModeGateTests:361-378`，注入**真实非零**假向量而非零向量兜底） | Tests |
| G7 | **前端契约** | `handleTriggerReindex` 不 await 全量；轮询 getJob 渲染进度；终态 toast 正确（组件级或 Playwright smoke，参照 b6b5f74 截图验收范式） | Host.Tests/e2e |
| G8 | **截断门禁** | >512 token 切片经 GGUF 适配器 → 无静默截断（拆分/告警/cohort 标记三选一成立） | Tests |
| G9 | **回滚一代** | 换表后旧代向量保留至清理作业；回滚 API/操作后检索恢复旧代 | Tests（P2 完成后） |

CI 防回归 wiring：G1-G6 进 `dotnet test` 默认套件；G7 进既有生产检索页验收脚本体系；任何新增「全库级」操作（rechunk 已走 sync job，符合门禁）PR 模板强制勾选「是否请求线程内长任务」检查项。

## 7. 结论

- 异常不是模型或硬件故障：正式环境 BGE M3 GGUF 就绪且实测可编码；**故障点是编排架构**——4.4h 的迁移被当作 60s 预算的同步请求执行。
- 存储层蓝绿影子设计保留；把 sync 作业系统已有的 enqueue/进度/取消范式平移到 reindex，再补「目标规格解析、切前三验收、双写/续跑/保留旧代」即与 Qdrant/Elasticsearch/Milvus 业界范式对齐。
- 无需新增第三方依赖；门禁 G1-G9 将「请求内长任务」「静默降级写假向量」「无验收切换」三类同源问题永久挡在 CI 之外。

## 8. 实施状态（2026-11-14 落地完成）

> 按用户指示「没有正式发布，不考虑历史数据兼容性」：契约/表结构直接演进，未保留任何向后兼容垫片。分支 `feature/v2-knowledgebase-dualchannel`。

### 8.1 已实现功能点

| 层 | 落地 | 关键位置 |
| --- | --- | --- |
| P0 作业化 | `TriggerModelReindexJobAsync` 前置校验（分段门禁/目标规格存在/单飞 409）后立即 202 入队；`RunReindexWorkerAsync` 在独立 CTS 后台跑全量，`finally` 释放单飞槽 | `KnowledgeBaseService.cs`、`KnowledgeBaseJobCoordinator`（单例，`TryBeginReindex`/`TryGetCts`/`EndReindex`） |
| P0 目标解析 | `SpecEmbeddingAdapterResolver` 按目标规格解析适配器：API 分支（`IngestionBackend=="OpenAiApi"` / `prov_` 前缀 / `IngestionBaseUrl`）走 `OpenAiEmbeddingAdapter`；模拟模式与维度不匹配在编码前快速失败 | `SpecEmbeddingAdapterResolver.cs` |
| P1 三验收 | 每批 `ValidateVectorBatch`（数量/维度/有限值/范数 > 1e-12）；模拟模式拒写；切前数量对齐 | `SqliteKnowledgeBaseStore.cs` 影子重构 |
| P2 双写/续跑/回滚 | 影子表 `chunk_vectors_shadow` 以 `source_version`+`producer_cohort` 续跑（崩溃后跳过已完成批次）；catch-up 循环（≤64 轮）后 `_writeGate` 内原子 rename 换表；**换表恒保留旧代 `chunk_vectors_old`**（含升级前的空代）→ 首次词法→混合升级亦可回滚至 `spec_lexical_only`；回滚一次性消费旧代；提交/回滚后 `pool.Invalidate()` | `SqliteKnowledgeBaseStore.cs`、`RollbackModelReindexAsync` |
| 前端 | `handleTriggerReindex` enqueue 成功即关弹窗、开进度抽屉；`setInterval(pollReindexStatus,1500)` 渲染 hero/进度条/统计（已处理/总数/速度/ETA）/阶段时间线/代次描述；终态 toast + 成功刷新列表；409 复用抽屉；取消/回滚二次确认；`onUnmounted`/`@after-leave` 停轮询 | `KnowledgeBases.js`、`app/api/index.js` |
| 崩溃恢复 | 启动时孤儿 Running 作业标记 Failed（提示断点续跑）；匹配残留影子表续跑复用 | `GetReindexStatusAsync`、`CrashRecovery_*` 门禁 |

### 8.2 门禁与回归（全绿）

- **核心级** `tests/ReferenceRAG.Tests/KnowledgeBaseReindexJobGateTests.cs`（12 项）：G3 维度/模拟拒写、G4 同库单飞 409、G5 运行中取消清影子、G6 词法→混合逐字节向量校验+解绑回退、G8 截断窗口（`SplitTokenWindows<T>` 重叠/钳制/边界）、G9 二代回滚+首次升级回滚、2 项崩溃恢复。
- **HTTP 级** `tests/ReferenceRAG.Host.Tests/KnowledgeBaseReindexJobControllerGateTests.cs`（3 项）：G1 enqueue < 2s 且响应前 provider 调用 == 0、G1 冲突/参数校验、G2 请求令牌取消不终止作业。
- **前端级** `wwwroot/tests/reindex-job.test.mjs`（11 项）：G7 归一化（数字枚举/兜底/null 安全）、百分比钳制、handler 不 await 全量、轮询/停轮询/终态 toast/回滚门控/22 个 `__returned__` 绑定/5 个 API 端点契约。
- 既有 3 套件迁移到作业语义（分段耦合门禁改为断言抛错；模型生命周期/纯词法门禁注入 fake resolver 走真实编码路径）。
- 回归扫荡：核心 KnowledgeBase 过滤 **76/76**、Host KnowledgeBase 过滤 **25/25**、前端 **89/89**、`check.mjs` 通过（53 JS 模块 + 14 可执行 Vue 模板）。

### 8.3 浏览器验收（隔离实例 + OpenAI 兼容 mock）

环境：独立 WebHost（`127.0.0.1:5199`，全新数据目录 → 免密开放访问）+ Node mock 嵌入服务（`127.0.0.1:5198/v1`，确定性 16d 单位向量，`/__control` 可调每批延迟），经**真实** `SpecEmbeddingAdapterResolver` API 分支 + `OpenAiEmbeddingAdapter` 编码，无需下载模型。种子：纯词法库 60 文档 / 360 切片。

| 路径 | 结果 |
| --- | --- |
| 黄金路径 | enqueue toast「重构作业已入队…」→ 抽屉实时进度（62%→98%→100%，224→352→360，速度 28.2→37.4 切片/秒，ETA 递减，阶段时间线 规划准备→向量编码→原子切换）→ Succeeded toast「重构完成：360 个切片向量已原子切换至新代次」→ 代次描述刷新（生效规格 prov_acceptance_mock / 360 向量 / 影子表无）→ 卡片刷新为混合 + 新增「代次」按钮 |
| 回滚 | 二次确认 → toast「已回滚至上一代向量索引」→ 生效规格回退 spec_lexical_only / 0 向量 / CanRollback=false（一次性消费）→ 词法检索「独孤九剑」3 命中 RRF 8.4ms 完整 |
| 409 单飞 | 运行中再次触发 → warning「知识库已有进行中的重构索引作业 (…)，请等待完成或先取消。」→ 弹窗关闭、抽屉复用同一活跃作业 |
| 取消 | 运行中（10s/批宽窗口）取消 → 二次确认 → toast「已发送取消请求…」→「重构作业已取消，影子表已清理」→ 作业 Cancelled（0/360）、影子表清空、**活跃代 360 向量全程不受影响** |

后端逐项核对：`reindex/status` 与 `GET /api/jobs/{id}` 的 ActiveJob/Generation/Status 与 UI 一致；控制台无意外运行时错误（仅刻意触发的 409 与一次竞态 404，均为正确后端响应并被前端优雅处理）。

> 验收环境限制与观察：
> - 内置浏览器面板处于隐藏态，截图与 naive-ui 虚拟列表可视化渲染不可用（`visibilityState=hidden`）；改用结构化快照 + 运行时断言完成验收，下拉选项数据经组件状态核实正确（5 项含 mock 规格），选择经 v-model 响应式驱动。
> - 跨库检索端点 `/api/knowledge-base-search` 使用**全局**查询编码器（本隔离实例无真实全局模型 → 模拟 384d），与按库绑定的 16d mock 向量维度不一致 → 向量路 0 命中。此为既有检索服务架构特性，**与本次重构正确性无关**（库级混合检索由 G6 以匹配适配器证明）；生产环境全局模型与库模型维度一致时不复现。

### 8.4 生产部署记录（2026-11-14）

| 项 | 值 |
| :--- | :--- |
| 部署提交 | `90e615578b25`（`feature/v2-knowledgebase-dualchannel`，已推送 origin） |
| 生产镜像 | `referencerag:deploy-90e615578b25`（sha256 前缀 `a1290c942b77`，834MB，通用 CPU+Vulkan 配方） |
| 构建方式 | 沿用既有正式流程：`git archive HEAD` → ssh exec 通道流式传入正式宿主 → 宿主上 `docker build -f support/deploy/docker/Dockerfile`（后台构建 + 日志轮询） |
| 容器重建 | `scripts/deploy-linux.ps1 -Mode Official -EnvFile env/.envprod -Image referencerag:deploy-90e615578b25`；复用重建前刷新的 `/tmp/referencerag-prod.env` 快照（chmod 600），ROCm 设备（`/dev/kfd`+`/dev/dri`）与全部挂载（data/models/logs、`/lwt` 源卷）原样保留 |
| 回滚锚点 | `referencerag:deploy-968045f90602`（sha256 前缀 `da76af95c659`）保留在正式宿主，一条 `docker run` 可回退 |

部署后验证（全部只读，**未触发生产 reindex**）：

| 检查 | 结果 |
| :--- | :--- |
| 镜像 sha256 核对（防「healthy 但旧代码」坑） | 容器 `.Image` == 新构建镜像 ID，running/healthy |
| 健康探针 `/api/system/health` | 200 healthy |
| 新前端生效 | `/app/views/KnowledgeBases.js` 含全部 5 个重构抽屉标记（openReindexDrawer/pollReindexStatus/handleCancelReindex/handleRollbackReindex/reindexGeneration） |
| 数据卷完整性 | 知识库目录原样：`dadd847d18b54162`「默认知识库」纯词法绑定不变 |
| 新端点 | `GET …/reindex/status` 200（ActiveJob/Generation 结构）；`GET /api/jobs` 200（历史 sync 作业保留） |
| 检索回归 | 跨库词法检索 3 命中 RRF，无退化 |
| 浏览器验收（`<PROD_HOST>:5123`） | 登录 → 知识库页无异常渲染 → 「绑定模型」弹窗展示新作业化文案（后台影子分批/词法不受影响/原子切换）+ 配置健康 🟢 Aligned + 3 个真实规格选项（bge-small-zh 512d / BGE M3 1024d / 纯词法 0d）→ 取消关闭，控制台无错误，未产生任何作业 |

> 生产观察：新 `reindex/status` 端点暴露了**原始故障残留**——`ShadowExists=true, ShadowRowCount=320`（当初同步 reindex 超时中断留下的影子表碎片，旧代码下不可见）。按 P2 续跑规则，下次授权触发 reindex 时将按 `source_version + producer_cohort` 判定续跑或重建，无需人工清理；活跃代 0 向量、纯词法检索全程不受影响。

## 参考来源

- [Elastic: Changing Mapping with Zero Downtime](https://www.elastic.co/blog/changing-mapping-with-zero-downtime)
- [Elastic: Aliases](https://www.elastic.co/docs/manage-data/data-store/aliases)
- [Qdrant: Migrate to a New Embedding Model](https://qdrant.tech/documentation/tutorials-operations/embedding-model-migration/)
- [Milvus: How to Switch Embedding Models Without Downtime](https://milvus.io/zh/blog/milvus-3-0-embedding-model-migration.md)
- [Zilliz: Migrate to a New Embedding Model](https://docs.zilliz.com/docs/migrate-to-a-new-embedding-model)
- [Google Cloud: Upgrade Embedding Models in AlloyDB AI with Zero Downtime](https://cloud.google.com/blog/products/databases/upgrade-embedding-models-in-alloydb-ai-with-zero-downtime)
- [TopK: How Do You Upgrade Embedding Models?](https://www.topk.io/learn/upgrading-embedding-models)