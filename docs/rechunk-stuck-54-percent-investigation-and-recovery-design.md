# 重新切片停在 54%：生产调查、恢复方案与回归门禁

调查日期：2026-11-21，时间统一采用 Asia/Hong_Kong（UTC+08:00）。

范围：用户指定的 5123 正式实例、Notes 知识库及本地源码；本地源码基准 `36a6679`。仅实施只读诊断并新增本文档，没有取消任务、重启容器、修改生产数据库、重新触发同步或修改业务代码。按项目凭据规范，不在文档保存真实服务器地址、密钥及源文件绝对路径。

## 1. 结论与证据边界

**本次持续显示 54% 的首要原因，高置信度指向进程退出后遗留的孤儿作业：数据库保存了 Running，执行它的进程已经被替换，而 sync 没有启动恢复或中断收口机制。**

任务创建于 **11 月 20 日 20:39:57**，当前目标容器启动于 **11 月 21 日 01:48:28**。作业创建早于当前进程约 5 小时 8 分钟；调查时 API 仍返回 `Running / Chunking / 3677 / 6765`。本地调用链是持久化 Job 后启动不受宿主管理的 `Task.Run`，CTS 只保存在内存，启动注册中未发现 sync 恢复服务。这个机制完整解释了“服务健康，但任务永久停在旧进度”。

需要区分两个问题：

1. **为什么现在一直显示 54%**：持久化状态与执行者生命周期脱节，读取接口不校验执行者存活，已有生产时间证据与源码证据支持。
2. **为什么旧进程在当时退出，以及退出前是否已经处理变慢**：尚无旧容器退出事件、历史进程栈或最后一个文件阶段日志，不能断言是 OOM、发布、人工重启、PDF 解析或数据库死锁。当前容器 `OOMKilled=false` 不能证明前一个容器没有发生过 OOM。

54% 是文件计数比值的取整显示：`3677 / 6765 ≈ 54.35%`，不是某个固定的切片算法阶段。`Phase=Chunking` 覆盖提取、分段、存储及图谱等多个步骤，不能据此认定正在执行 SlidingWindow，或认定第 3678 个文件一定有问题。

尚未比对生产程序集与本地提交是否完全一致，也未通过运行时诊断直接读取目标作业 CTS 注册表。因此将“孤儿任务”列为高置信度结论，保留上述边界；不能把源码中的所有风险都写成本次已发生的故障。

## 2. 生产只读核验记录

### 2.1 观测结果

| 证据 | 实际结果 | 含义 |
| --- | --- | --- |
| 用户截图 | 54%，3677/6765，运行约 15h48m | 初始症状 |
| 12:31:52 `GET /api/jobs/{jobId}` | `324b7e62450e4d3b`，`sync`，`Running`，`Chunking`，3677/6765，`FinishedAt=null` | 后端持久化快照也未结束，并非仅前端缓存 |
| 12:35:06 再次查询同一 Job | 同样的状态与计数 | 两次采样间未推进；不能据此倒推出全部历史停滞时长 |
| 任务 `CreatedAt` | `2026-11-20T12:39:57.5651811+00:00`，即本地 20:39:57 | 时间线起点 |
| 目标容器 `State.StartedAt` | `2026-11-20T17:48:28.179200911Z`，即次日 01:48:28 | 当前执行进程晚于任务创建 |
| 目标容器 `docker top` | 当前 dotnet 进程启动于次日约 01:48:27 | 与容器启动时间相互印证 |
| `GET /api/index/jobs` | 相同 Job，`ProgressPercent=54`，`CurrentFile=正在同步文档 (3677/6765)` | 两个页面取到的是同一个后台事实；CurrentFile 并非文件名 |
| KB 定义 | `40745ffb28444648`，`spec_lexical_only`，`PureLexical`，`spec_chunk_lexical_dense` | 当前配置为纯词法，不能优先归咎 GPU/向量服务 |
| 目标规格 | `Mode=Adaptive`，`StrategyType=SlidingWindow`，MaxTokens=400，OverlapTokens=40 | 配置声明，不能等同实际命中的策略 |
| 当前连接器 | 两个均启用、可达；运行 Job 的连接器为 `0b6059c58d6f4809` | 目录当前可达不能证明每个文件均可读 |
| 目标连接器统计 | 8154 文档、232767 切片 | 是当前库存统计，与任务扫描时 6765 的口径和时间不同，不可直接相减判定丢失 |
| HTTP 与 Docker health | healthy / 200 | 探针不包含作业存活性，不能证明后台作业健康 |
| 容器单次资源采样 | CPU 约 0.06%，内存约 454.4MiB / 29.31GiB，45 PIDs | 未见当前 CPU 饱和；这不是线程栈或过去内存峰值证据 |
| 日志 API | 列出的文件最新止于 11 月 12 日 | 无法用这些日志解释 11 月 20 日任务中断 |
| 当前容器 stdout | 有次日 01:48 的启动输出 | 印证当前进程启动；未取得旧进程终止原因 |

鉴权使用项目 `env/.envdev` 中已有的 Master Key，仅放在请求头和内存中，没有写入本文。未鉴权的任务 API 返回 401；正确鉴权后返回 JSON。部分不存在的 GET 路径会被 SPA 回退为 HTML，即使返回 200 也不能当作 API 成功。

### 2.2 目标识别注意事项

本地环境文件的默认 SSH 宿主与本次用户指定的正式地址不相同。默认宿主上也存在同名/同端口服务，初步读取的数据已从本次根因证据中排除。上述容器时间和资源数据来自**用户指定正式宿主**，并通过 `docker ps --filter publish=5123` 确认承载目标端口的 `reference-rag` 容器后再读取。

后续操作必须固定目标宿主、端口和容器 ID；不能凭默认 `DEPLOY_CONTAINER_NAME` 或“同名容器”执行恢复。

## 3. 源码调用链与问题本质

以下引用均为本仓库相对路径，可直接在编辑器定位。

| 层次 | 位置 | 已确认行为 |
| --- | --- | --- |
| 全库重新分段 | `Business/ReferenceRAG.Business/Features/KnowledgeBase/Services/KnowledgeBaseService.cs:1766` | `TriggerRechunkAsync` 先保存规格与 Desired/Effective revision，再逐个调用连接器同步 |
| 作业启动 | 同文件 `:676`、`:709`、`:715` | 保存 `Operation=sync` 的 Job，注册内存 CTS，丢弃 `Task.Run` 返回值；没有持久化调度器接管 |
| 执行器 | `Infrastructure/ReferenceRAG.Infrastructure/Features/KnowledgeBase/Jobs/KnowledgeBaseSyncWorker.cs:58` | 全量扫描到 List，再串行处理；通常在一个文件全部处理后更新计数 |
| 状态机 | `Infrastructure/ReferenceRAG.Infrastructure/Features/KnowledgeBase/Jobs/JobStateMachine.cs:140` | 进度更新只是保存 ProcessedUnits/TotalUnits，没有心跳、最后进展时间或执行者身份 |
| 内存协调器 | `Business/ReferenceRAG.Business/Features/KnowledgeBase/Services/KnowledgeBaseJobCoordinator.cs:40` | sync 的 CTS 仅存 Dictionary；重启后消失 |
| 取消 | `KnowledgeBaseService.cs:744` | 找不到内存 CTS 返回 false，不能使重启前的 Running 收口；Controller 将其映射为 404 |
| 查询 | `KnowledgeBaseService.cs:734`；`Host/ReferenceRAG.ApiHost/Controllers/KnowledgeBasesController.cs:976` | 直接读取 Catalog，没有 sync 孤儿状态对账 |
| 监控聚合 | `Host/ReferenceRAG.ApiHost/Controllers/IndexJobsController.cs:61`、`:147` | 根据存储的 Status 过滤活跃任务；CurrentFile 拼接计数，Duration 从创建时间一直累加 |
| 启动注册 | `Business/ReferenceRAG.Business/Features/KnowledgeBase/KnowledgeBaseBusinessExtensions.cs:9` | 已有词法补齐、预热、挂载对账 HostedService，没有 sync 作业恢复 |
| 已有局部先例 | `KnowledgeBaseService.cs:1749` | reindex 查询会检查内存单飞位并将孤儿记录转 Failed，但仅覆盖 reindex |

故障链路如下：

```text
HTTP 触发 → Catalog: Queued → 内存 Task.Run → Catalog: Running, 3677/6765
                                     ↓ 进程退出
                         Task、CTS、扫描列表一并消失
                                     ↓ 新进程启动
              无 sync 恢复/中断收口 → Catalog 仍是 Running
                                     ↓
           监控接口原样投影 → 页面继续显示 54% 和不断增长的耗时
```

**持久化任务状态不等于持久化执行。** 前端轮询、SignalR、进度条和“任务中心”都是投影，不能补偿执行者丢失。仅把 `Task.Run` 改为 Channel 也不够：Channel 同样随进程退出丢失。

### 3.1 同类风险，尚未认定为本次直接原因

- **缺少单文档预算和可观测阶段**：同步 chunker 无 CancellationToken；PDF 库内部调用不一定能及时响应取消。一个文件内变慢时，文件计数不会变化。不能用增加总任务超时时间解决。
- **重复触发与同库写争用**：sync 只有 Register，没有 reindex 的单飞保护。全库重分段对多个连接器的调用会分别启动后台任务，并不等于等待前一个完成；StorageLease 是生命周期引用，不是任务互斥锁。
- **状态写入缺少 CAS**：`CatalogStore.SaveJobAsync` 的 UPSERT 无版本/owner/attempt 条件。多个状态机实例的局部 lock 不能防止晚到的 Running/进度覆盖新终态；PartiallySucceeded 也未被所有状态机终态判断包含。
- **取消异常可能先被通用 catch 捕获**：worker 单文档及模型分支有 `catch(Exception)`。最终仍可能在后续检查观察到取消，但异常类型、日志和终态语义容易混杂，需专门分流。
- **规格提前生效**：全库重切在子任务完成之前提升 EffectiveConfigRevision；过程中失败、重启或只成功部分连接器时，规格显示可能与实际切片不一致。
- **重复索引准备开销**：纯词法每个文件调用 `EnsureLexicalIndexAsync → BackfillLexicalIndexAsync`，而 CommitDocument 已事务双写词法索引。应测量后将“历史补齐”和“当前文档提交”分开，不能未经验证直接删掉历史补齐。
- **分词发生在写事务内**：CommitDocument 持有 `_writeGate` 并在循环内做分词，放大同库大文档写占用。WAL 不会提供多个并行写者。
- **策略名与实现不一致**：当前工厂内置 Balanced、PrecisionDense、DeepNarrative、CodeFirst；未知名称回退 Balanced。没有发现注册 SlidingWindow/DiscourseParagraph 的实现。因此 UI 显示 SlidingWindow 不足以证明算法命中；这是相关配置契约问题，非本次孤儿任务的直接根因。

## 4. 当前系统可以复用什么

| 现有基础 | 可复用部分 | 当前缺口 / 限制 |
| --- | --- | --- |
| `CatalogStore` / `ICatalogStore` | jobs 表、查询、状态持久化 | 缺少原子 claim、状态版本、owner、心跳及恢复 API |
| `JobAttempt` / `job_attempts` | attempt_number、checkpoint_data、错误、起止时间的结构 | 搜索仅发现建表、模型和清理代码，未发现 sync 实际写入/恢复尝试记录；不能宣称已有可用断点续跑 |
| `JobStateMachine` | 状态转换、持久化后通知、取消入口 | 要补完整终态集合和跨实例 CAS；不再用无条件 Save 覆盖状态 |
| `KnowledgeBaseJobCoordinator` | 单进程跨请求 CTS、reindex 单飞经验 | Dictionary 不是持久化锁，也不是多实例锁；扩展为所有互斥写操作的统一入口 |
| `LexicalIndexBackfillWorker` | BackgroundService、宿主取消、独立 DI scope | 复用工作方式；不能假设它负责 sync 恢复 |
| `LexicalIndexBackfillSignal` | 有界 Channel、唤醒与去重思路 | Channel 仅传 jobId 提示，Catalog 才是可靠来源 |
| `CommitDocumentAsync` | 单文档事务覆盖、IndexEpoch、文档版本、FTS/向量同步 | 当前 ExpectedDocumentVersion 常传 0；要补发布幂等与旧 attempt 栅栏，不能单靠 job 计数恢复 |
| reindex 影子表/代次 | 续跑校验、发布、回滚与相关测试经验 | 是向量代次能力；不可直接声称已经能原子切换全套文本切片与 FTS |
| `JobCenter` / `KbJobSource` / `IndexJobsController` | 前端归一化、轮询兜底、刷新订阅 | 复用现有 UI；需要保留 Cancelling、部分成功及 Interrupted 原因，不能将重切全部伪装成普通 sync |
| Serilog、TimingScope、日志 API、TemporaryDiagnostics | 结构化阶段耗时与诊断入口 | 必须验证日志落点与当前实例；不能因存在诊断包就假设已有 Job 心跳和 watchdog |
| xUnit、Moq、现有 crash worker | 测试夹具、真实进程崩溃注入方式 | 旧测试主要验证单文档和投影，需新增 sync 进程生命周期测试 |

依赖声明已包含 `Microsoft.Extensions.Hosting.Abstractions`、`Microsoft.Data.Sqlite`、`jieba.NET`、`PdfPig`、Markdig，以及测试用 xUnit/Moq；本次未发现 Hangfire、Quartz.NET、Temporal 的直接集成或 sync 调度注册。`System.Threading.Channels` 可由当前 .NET 共享框架提供，无须为了队列新增外部服务。

## 5. 主流方案对比与选型

以下是针对本问题的技术判断，不是将“安装任务库”等同于自动修复业务幂等性。

| 方案 | 能解决的核心部分 | 必须补齐 / 引入成本 | 对本项目建议 |
| --- | --- | --- | --- |
| .NET BackgroundService + 有界 Channel + 现有 SQLite Catalog | 宿主生命周期、DI scope、背压；用 Catalog 补可靠领取与恢复 | 持久化队列、claim、幂等、心跳和重启对账由应用实现；Channel 本身不持久化 | **当前单实例优先方案**，复用度最高，限定为知识库任务，不扩建通用工作流平台 |
| Hangfire | 持久化后台调度、状态、重试和取消支持 | 业务仍需可重入、细粒度检查点和幂等；选存储适配器、部署、Dashboard 权限；SQLite 适配不可直接假定官方支持且成熟 | 若后续需要统一治理大量后台任务，优先评估；本次不直接引入 |
| Quartz.NET + 持久化 JobStore | 定时触发、持久化调度、故障恢复和并发执行控制 | 恢复前提是持久化配置；恢复运行不是自动从第 3678 个文件续跑 | 适合定时同步计划；不是单独解决文档执行恢复的捷径 |
| Temporal .NET 工作流 | 持久化编排、Activity 超时、心跳和重试 | 引入 Temporal 服务、运维及工作流模型；Activity 副作用仍需幂等 | 多服务、大规模分布式编排时再考虑，本次成本过高 |

官方依据：

- [Microsoft：Hosted services](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/host/hosted-services?view=aspnetcore-10.0)介绍宿主后台服务、队列及后台创建 scope；[Channels](https://learn.microsoft.com/en-us/dotnet/core/extensions/channels)提供有界生产消费队列。它们提供执行框架，不承诺自动恢复进程内数据。
- [Hangfire：处理后台作业](https://docs.hangfire.io/en/latest/background-processing/processing-background-jobs)及[最佳实践](https://docs.hangfire.io/en/latest/best-practices.html)说明后台执行与方法可重入要求；[取消支持](https://docs.hangfire.io/en/latest/background-methods/using-cancellation-tokens.html)仍要求业务传递和响应取消。官方有 [SQL Server 存储](https://docs.hangfire.io/en/latest/configuration/using-sql-server.html)，[Redis 文档](https://docs.hangfire.io/en/latest/configuration/using-redis.html)使用 Hangfire.Pro.Redis；不能将“可用 Redis”写成没有授权成本的结论。
- [Quartz.NET：Job stores](https://www.quartz-scheduler.net/documentation/quartz-4.x/tutorial/job-stores.html)和[最佳实践](https://www.quartz-scheduler.net/documentation/best-practices.html)说明持久化、恢复及并发控制的前提。版本升级与 provider 兼容性需实施时再验证，本报告未指定待安装版本。
- [Temporal：Activity 故障检测](https://docs.temporal.io/encyclopedia/detecting-activity-failures)区分排队、执行、整体超时与心跳；本项目应借鉴这些独立的时间语义，而非用一个百分比或总耗时替代全部健康判断。
- [SQLite WAL](https://www.sqlite.org/wal.html)仍然只有一个写者；[Microsoft.Data.Sqlite async 限制](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async)说明 ADO.NET async 方法实际同步执行；[busy/locked 错误处理](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/database-errors)说明等待至 command timeout 的机制。不能因为代码有 await 就承诺数据库操作不会阻塞。

结论：**先修复执行生命周期与恢复协议，再按规模决定是否引入调度产品。更换 chunker、PDF 库或 BM25 库无法恢复已经消失的 Task。** 现有 PdfPig/Markdig/jieba 暂无本次替换依据。

## 6. 具体落地方案

### 6.1 P0：让孤儿任务可靠结束，避免再次假运行

第一阶段不承诺自动续跑，优先确保状态真实。

1. 新增宿主管理的 `KnowledgeBaseJobWorker : BackgroundService`，在 scope 内解析执行器，替代请求内裸 `Task.Run`；将用户取消与宿主停机 token 链接，宿主正常停止时记录 Interrupted 原因。异常退出仍由下次启动对账兜底。
2. 新增启动恢复扫描，检查 Queued/Running/Cancelling。对**已证明执行者消失**的旧作业，以 CAS 转为 `Failed`，附 `errorCode=JobInterrupted`、`FinishedAt` 和“进程中断，可重新执行”的文案，保留 3677/6765 原进度；对已过期的取消请求收口 Cancelled。无需立即增加会破坏旧前端枚举的新 Status。
3. 对没有 owner 字段的历史记录，仅在已经确认该数据目录单实例、独占运行且没有新 worker 接管时做一次迁移对账；多实例不得仅凭“本进程 Dictionary 为空”判断其他进程的 Job 死亡。无法证明时展示疑似失联、禁止自动重跑。
4. 给 sync/rechunk/reindex/bm25/graph 等写同库操作统一互斥策略。P0 至少实现单进程每库互斥；等待者保持 Queued，不占据写锁等待全流程。
5. 将 reindex 查询中的孤儿处理经验迁移到专用恢复服务；GET 最终只读，避免监控查询触发写状态和锁竞争。
6. GET 投影保留 Cancelling、PartiallySucceeded 和中断原因。未知 TotalUnits 显示“扫描中”，Failed 不伪装为 100%。增加“服务在线 / 后台任务异常”独立提示。

**当前生产恢复操作建议（本次未执行）**：先确认目标实例唯一性与旧 worker 已不存在，对 Catalog/kb.db 采取包含 WAL 一致性的备份；再通过带 CAS 的维护入口仅将该 Job 转为中断终态，保留审计。随后由操作人选择只重试该连接器或执行修复后的全库任务。不要直接删除 jobs 行、删 WAL、改成 Succeeded、清空所有切片，或直接重启后期待自动续跑。现有取消入口找不到 CTS 时可能返回 404，不应把它解释为任务已经成功结束。

### 6.2 P1：可靠队列、心跳、检查点与恢复

最小新增契约：

| 数据 | 建议字段 / 行为 |
| --- | --- |
| jobs | connector_id、parent_job_id、配置快照/哈希、owner_instance_id、lease_expires_at、last_heartbeat_at、last_progress_at、current_source_key、current_stage、cancel_requested_at、version |
| job_attempts | 复用现有表；补领取/完成 API，记录 attempt、错误和检查点；不能只建模型不执行 |
| 扫描计划 | 持久化 source_key、revision、顺序和状态；唯一键包含 job/connector/source。不要把易变目录枚举索引直接当检查点 |
| 单文档处理结果 | succeeded/failed/skipped、错误类型、配置哈希、发布操作键。Processed 是已结算文件数，不是成功数 |
| 领取 | 短事务条件更新并检查受影响行数，只有当前 owner/attempt 可心跳、推进与结算；新 attempt 拒绝旧 attempt 的写入 |

执行协议：

1. 接口事务性保存 Job 与参数后返回 202/jobId；Channel 只发送唤醒信号，信号丢失由周期扫描 Catalog 补偿。禁止入队委托捕获请求 scope。
2. Worker 原子领取 Queued 或已确认租约过期的任务，写入 attempt/owner。多个进程抢同一 Job 只能一个成功。
3. 扫描计划按批持久化，固定规格及源 revision；缺失文件记录明确失败/跳过原因。扫描中断本身也可以恢复。
4. 单文件按读取、提取、清洗、切片、词法分词、提交、图谱阶段记录耗时。心跳与文件完成进度分开；心跳在独立控制路径更新，避免同步长调用把心跳一并饿死。
5. **跨库事务窗口必须显式处理**：Catalog 与每库 kb.db 是不同数据库。可能“文档已提交，Catalog 检查点未写”就崩溃，因此在 kb.db 的同一个文档发布事务内写入唯一操作键（计划项 + 源 revision + 配置哈希），重放先判定已发布并补齐 Catalog。只有相同配置和内容的发布才可跳过；单靠 content_hash 会错误跳过重新切片。
6. Job 领取 CAS 不自动阻止旧执行者继续写 kb.db。交接时在 kb.db 内激活新的执行栅栏，CommitDocument 的发布事务核验当前 run epoch；新执行者须完成栅栏交接才开始写入。不得把现有静态 ExpectedIndexEpoch=1 当作已实现 attempt fencing。
7. 计数从计划项已结算状态重建，恢复多次也不重复累计；保留 succeeded/failed/skipped 分项及有限重试预算。确定性解析错误不无限重试；瞬时 I/O/锁等待按预算退避。
8. 用户取消先持久化 cancel intent，再向本地 CTS 发信号；无本地 CTS 时也能被恢复服务读取并收口。终态更新用 `CancellationToken.None` 配合独立短超时，不能用已经取消的作业 token 保存终态。

初始运行参数建议：心跳间隔 10s，租约 60s，30s 扫描过期作业；单文件无进展 5min 告警。以上是待测试调优的建议值，不是当前系统实测 SLA。心跳超时判断失联，进度超时判断卡慢，两者不能混用；大 PDF 预算应按大小/页数配置并提供硬上限。

同步/原生解析不可强制取消时，`Task.WaitAsync(timeout)` 只停止等待，旧工作可能仍占线程并继续写。需要硬隔离的解析放入子进程，超时终止该子进程，输出通过 IPC 返回；由唯一受管 worker 提交存储。没有隔离能力时不得在超时后无界启动新解析副本。

### 6.3 P1：重切父任务与配置生效语义

- `TriggerRechunkAsync` 创建 `operation=rechunk` 的父作业和每个启用连接器的子计划，返回真实 jobId；不要把连接器数量填入“已处理文档数”。
- 将目标规格及 revision 保存为本次不可变快照；开始时只推进 Desired。全部子任务成功并验证切片/词法一致后才推进 Effective。
- 保留当前“逐文档原子替换”的最低成本模型时，任务期间明确显示重建中/混合版本，并保存文档配置版本。失败或取消不应谎报全库已完成。
- 若业务要求全库一致快照，再独立实现文本 chunks + FTS + 图谱的 staging/generation 发布，复用向量代次的设计经验；这比修复孤儿任务范围更大，不列为 P0 前提。

### 6.4 P2：降低大库重跑成本

在生命周期修复后采样各阶段耗时，再做最小性能变更：将词法历史补齐移到任务准备阶段；正常文档提交保留事务双写。把可安全预计算的分词移出写事务，但在事务内验证输入版本、代次和配置快照。每库限制提交并发，跨库使用有界并发和全局内存预算。验证 SlidingWindow 策略注册，未知名称返回校验错误或明确降级提示，并展示 requested/effective 两个策略值。

## 7. 同类门禁与回归测试设计

**本节是待实现的验收规格，不是已新增并通过的测试。** 核心门禁测试可观察行为，不能用搜索 `Task.Run`、某方法名或 UI 文本来替代进程恢复证明。

| 门禁 | 故障注入 / 输入 | 必须断言 |
| --- | --- | --- |
| G-JOB-RECOVERY-01 | 子进程启动真实 sync，完成第 N 个文件后强制退出；新进程使用同一临时数据目录 | P0 在恢复窗口内明确 Interrupted/Failed；P1 从未结算项恢复；绝不无限 Running |
| G-JOB-RECOVERY-02 | 同一 Job 由两个 worker 同时领取 | 只有一个领取成功；接管后旧 attempt 心跳、进度及文档发布均被拒绝 |
| G-JOB-RECOVERY-03 | 文档事务已 commit、Catalog 检查点未写时杀进程 | 重放无重复切片/FTS、文档版本不因同一操作重复发布；已结算计数不重复 |
| G-JOB-RECOVERY-04 | Catalog 已建 Queued，但 Channel 唤醒前进程退出 | 新实例从数据库发现并执行/收口，队列信号丢失不丢任务 |
| G-JOB-CANCEL-01 | 重启后取消旧作业；分别在读文件、排队、提交前取消 | 无本地 CTS 仍能记录 cancel intent 并进入终态；取消不变成普通处理失败 |
| G-JOB-STATE-01 | 延迟一个旧 Running 更新，先提交 Cancelled/Succeeded/PartiallySucceeded | 晚到更新 CAS 失败，终态与 FinishedAt 不倒退；轮询 GET 不写数据库 |
| G-JOB-HEARTBEAT-01 | 假时间推进；一个长文件有心跳无进度，另一 worker 完全停止心跳 | 前者保持活动并按预算报警，后者租约到期可收口；单凭总耗时不误杀健康任务 |
| G-JOB-ISOLATION-01 | 解析器模拟永不返回且不响应 token | 超时后没有遗留写入、无无界线程/进程增长；隔离子进程可回收；后续文件可继续 |
| G-KB-MUTATION-01 | 同库同时触发 sync/rechunk/reindex/BM25；另一个库正常查询 | 按互斥策略排队或明确 409，不能并行破坏同库代次；其他库不被全局长锁阻塞 |
| G-RECHUNK-CONFIG-01 | 两个连接器，一个成功，一个失败或进程中断 | 父任务部分成功/失败真实，Effective 不提前追平 Desired；重试使用固定规格 |
| G-RECHUNK-PLAN-01 | 扫描后新增、删除、改名文件；恢复时枚举顺序变化 | 按持久化计划/源身份恢复，不按“第 N 个文件”跳过；错误/跳过/成功可核对 |
| G-RECHUNK-STRATEGY-01 | 每个可选择策略及未知策略名 | requested 与 effective 一致或显式报错；不能显示 SlidingWindow 却静默执行 Balanced |
| G-LEXICAL-COMMIT-01 | 文档更换规格重切，在事务中途退出 | chunks、FTS、词法索引和可选向量没有半份新旧数据；查询到完整旧版或完整新版 |
| G-JOB-UI-01 | REST 注入 Running、Cancelling、PartiallySucceeded、Interrupted；断连后重连并刷新页面 | 两页面同状态；已完成数/总数/失败数有明确口径；失联不继续伪装健康重建；取消中不映射回普通 Running |
| G-JOB-SCALE-01 | 合成 6765 个小文件 + 大文件、中文、超长行、表格、坏 PDF、Base64 和 Emoji | 任务有界完成或明确失败项；采集阶段耗时/内存/提交等待，结束计数可重建；不以达到某个固定百分比作为成功条件 |

实现方式：xUnit/Moq 覆盖可控错误与时间；真实 SQLite 和临时数据目录覆盖事务/CAS；独立 crash worker 进程覆盖崩溃窗口；浏览器或真实组件测试验证跨页面行为。复用 `tests/LexicalRecoveryCrashWorker` 的故障注入思路，新增 sync 模式或专用 worker，禁止在正式库做杀进程测试。

CI 接入不能只增加一个测试文件：当前 `.github/workflows/web-build.yml` 运行 Host 全套，但 Core 测试只筛 `ContractTests|AuditFixRegressionTests`，且 PR paths 没有 `tests/**`。新增恢复测试需显式加入过滤条件/独立步骤，并补 tests 路径触发；Linux 容器恢复场景增加独立 Linux job。客户端 pre-commit 当前主要运行安全扫描，不能把历史验收报告中的“已挂门禁”当作当前 CI 已覆盖崩溃恢复。

发布验收条件：G-JOB-RECOVERY-01/02/03/04、取消、终态 CAS 和父任务配置门禁必须通过；模拟重启后不再有无执行者的 Running，且相同任务重放不重复发布。性能改善以相同数据和硬件的前后测量为准，本次不承诺未经测量的加速倍数。

## 8. 本次实际验证与未完成项

已实际运行：

```powershell
dotnet test tests/ReferenceRAG.Host.Tests/ReferenceRAG.Host.Tests.csproj --no-restore --filter FullyQualifiedName~DocumentRechunkAndBackgroundTaskTests --verbosity quiet
dotnet test tests/ReferenceRAG.Tests/ReferenceRAG.Tests.csproj --no-restore --filter FullyQualifiedName~KnowledgeBasePureLexicalModeGateTests --verbosity quiet
node --test Business/ReferenceRAG.Business/wwwroot/tests/jobs.test.mjs
```

结果分别为 **5/5、7/7、10/10 通过**，均无跳过。构建输出包含现有 obsolete、nullable、平台及测试分析器警告，未在本任务顺带修改。以上验证说明单文档重切、纯词法基础行为和前端作业模型仍可用，**不证明重启恢复已实现**。

生产核验均为 GET、docker inspect/stats/top/logs/ps；没有部署、写生产数据或发送取消。已核对任务 API 多次快照和目标容器时间。未执行完整测试集、生产重切重放、线程转储、旧进程退出原因取证或新门禁实现。

交付为调查与设计文档。下一次实现应先落 P0 恢复收口和最小崩溃回归测试，再落 P1 可靠续跑；性能、策略注册和全库代次切换分别验收，避免以大重构延后处理当前假运行问题。
