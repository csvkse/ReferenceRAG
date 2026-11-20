# ReferenceRAG 长时任务监控总线与单文档重新切片功能验收报告

> **验收环境**：本地 Linux Dev 远程开发环境 (`http://127.0.0.1:5124/`)  
> **验收基准方案**：[`docs/background-task-engine-and-document-rechunk-architecture-study.md`](file:///e:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/docs/background-task-engine-and-document-rechunk-architecture-study.md)  
> **执行标准**：遵循根目录 [`AGENT.md`](file:///e:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/AGENT.md) 快速开发部署流程与凭据安全红线  
> **验收工具**：原生 Chrome DevTools MCP (`chrome-devtools-mcp`)  
> **验收状态**：全部功能项与防回归门禁 100% 验收通过  

---

## 1. 快速开发部署流程验证

按照 `AGENT.md` 与 `scripts/deploy-linux.ps1` 规范，对 Linux 开发宿主进行端到端部署与热同步验收：

1. **容器部署与镜像构建**：
   - 执行 `powershell -ExecutionPolicy Bypass -File scripts/deploy-linux.ps1 -Mode Dev`；
   - 远程编译构建 `referencerag:dev-cpu` 镜像并启动 `referencerag-dev` 容器（端口 5124）；
   - `/api/system/health` 探针返回 `healthy`，Smoke 测试集自动挂载同步；
2. **前端 ESM 免构建热同步**：
   - 执行 `powershell -ExecutionPolicy Bypass -File scripts/deploy-linux.ps1 -FrontendOnly -Mode Dev`；
   - 耗时 5.1s 极速将 `Business/ReferenceRAG.Business/wwwroot` 增量热同步至容器运行卷，零容器重启完成视图生效。

---

## 2. 核心功能逐项端到端验收

### 验收项 1：知识库治理与单文档重新切片 (Re-chunk Document)

- **功能目标**：为知识库治理文档列表补充单文档级「重新切片」入口，支持单篇按当前分段策略重切并更新向量，自动清除历史乱码脏分块，版本号原子自增。
- **验证页面**：`http://127.0.0.1:5124/knowledge-bases`
- **操作验证流程**：
  1. 打开「金庸武侠测试知识库」治理弹窗，切换至「收录文档列表」；
  2. 每行文档操作栏均展示「查看切片」、「重新切片」、「删除文档」三个按钮；
  3. 点击第二项「三联版/雪山飞狐.md」的「重新切片」，弹出二次确认弹窗：「*确定要对文档重新切片吗？系统将按照当前知识库配置重新解析并生成切片与向量，历史异常或乱码切片将被彻底覆盖清除*」；
  4. 确认后服务端执行 `RechunkDocumentAsync`，文档分块更新并完成向量生成，版本号由 `v4` 平滑递增至 `v5`。
- **截图佐证**：
  - 收录文档列表重新切片按钮：[`docs/acceptance-screenshots/02-documents-rechunk-button.png`](file:///e:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/docs/acceptance-screenshots/02-documents-rechunk-button.png)
  - 重新切片确认弹窗：[`docs/acceptance-screenshots/03-rechunk-confirm-dialog.png`](file:///e:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/docs/acceptance-screenshots/03-rechunk-confirm-dialog.png)

---

### 验收项 2：BM25 全文词法倒排索引异步构建与进度跟踪

- **功能目标**：彻底消除原有 HTTP 同步长阻塞，改造为异步 Job 调度，页面动态展示构建进度百分比与文档进度。
- **验证页面**：`http://127.0.0.1:5124/bm25-index`
- **操作验证流程**：
  1. 切换至「词法索引操作」Tab，点击「重建词法索引」；
  2. 接口立即返回 `202 Accepted` 并携带 `jobId`；
  3. 按钮呈 Loading 状态，下方即时渲染「**索引进度**」卡片，动态跟踪进度条与说明文本（如 `正在重建词法倒排索引 (1/2)`）；
  4. 构建完成后自动刷新索引概览统计。
- **截图佐证**：
  - BM25 重建实时进度卡片：[`docs/acceptance-screenshots/04-bm25-rebuild-progress.png`](file:///e:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/docs/acceptance-screenshots/04-bm25-rebuild-progress.png)

---

### 验收项 3：系统监控长时任务中心聚合监控 (/system)

- **功能目标**：将监控中心升级为全能后台任务中枢，聚合向量索引、BM25 倒排、知识图谱、连接器同步等多类型长时任务。
- **验证页面**：`http://127.0.0.1:5124/system`
- **操作验证流程**：
  1. 在 BM25 倒排索引重建运行期间，导航至 `/system`；
  2. 「**正在进行的系统任务 (长时任务监控)**」卡片精准捕获该后台作业；
  3. 任务类型 Badge 渲染为 `BM25 倒排`，展示任务 ID（`549dded971e3437a`）、目标库、文件进度（`1 / 2 文件`）、当前步骤明细、已运行时间计时器（`55.7s`）以及「中断任务」按钮；
  4. 下方「系统任务历史」保留历史成功/失败记录并支持一键清空。
- **截图佐证**：
  - 系统监控长时任务动态卡片：[`docs/acceptance-screenshots/05-system-long-running-tasks.png`](file:///e:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/docs/acceptance-screenshots/05-system-long-running-tasks.png)

---

### 验收项 4：知识图谱抽取状态与任务纳管 (/graph)

- **功能目标**：消除 `Task.Run` 裸跑，将图谱重建纳管入后台任务总线，展示行内进度说明并支持中途取消。
- **验证页面**：`http://127.0.0.1:5124/graph`
- **操作验证流程**：
  1. 进入知识图谱视图，点击「重建图谱」；
  2. 弹出 Popconfirm 提示：“*重建将重新扫描选定知识库所有文档的 wiki-link，无需 GPU，通常需要几十秒。确认吗？*”；
  3. 确认后提交 `graph_rebuild` 任务并在行内展示抽取进度。
- **截图佐证**：
  - 知识图谱视图状态：[`docs/acceptance-screenshots/06-graph-view.png`](file:///e:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/docs/acceptance-screenshots/06-graph-view.png)

---

### 验收项 5：代码层零分配优化与全栈架构防回归门禁

- **优化点**：
  - 消除 `BM25IndexController.cs` 中的 `KnowledgeBaseIds = scope.Ids.ToList().ToList()` 冗余连写；
  - 将 DTO 响应属性 `KnowledgeBaseIds` 优化提升为只读集合抽象 `IReadOnlyList<string>`，实现引用直传零堆内存分配（Zero Allocation）。
- **门禁防线建设**：
  - 在 [`QualityGateContractTests.cs`](file:///e:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/tests/ReferenceRAG.Tests/QualityGateContractTests.cs) 中新增架构测试 `Architecture_RedundantLinqAndTypeChains_MustNotExistInSourceCode`；
  - 挂载至 Git `pre-commit` 钩子与 CI，严格拦截 `.ToList().ToList()`、`.ToArray().ToArray()`、`.ToString().ToString()` 等无意义链式坏味道。

---

## 3. 全套自动化测试与质量检验汇总

| 检验环节 | 校验命令 | 覆盖指标 | 结果 |
| :--- | :--- | :--- | :--- |
| **前端 AST 语法与模板门禁** | `node Business/ReferenceRAG.Business/wwwroot/check.mjs` | 60 个 JS 模块 + 14 个 Vue 模板 | **100% 通过 (0 错误)** |
| **前端单元测试套件** | `npm test` | 路由、传输层、作业状态机、布局断点等 | **133 / 133 全部通过** |
| **重切片与长时任务测试** | `dotnet test --filter DocumentRechunkAndBackgroundTaskTests` | 单文档重切、扫描件清理、任务适配器聚合 | **5 / 5 全部通过** |
| **架构契约与冗余链门禁** | `dotnet test --filter QualityGateContractTests` | HTTP 环路防御、配置完整性、冗余链门禁 | **7 / 7 全部通过** |
| **凭据安全与敏感信息扫描** | `pwsh scripts/security-scan.ps1` | 全库 196,845 行代码与文档 | **100% 通过 (0 凭据违规)** |

---

## 4. 验收结论

本次更新完全满足方案设计中对「单文档重新切片治理」与「统一长时任务监控总线」的各项功能、性能与交互要求：
1. 治愈了由于文档解析器历史问题遗留的脏切片，用户在知识库治理页面可随时单篇自愈；
2. 彻底终结了 BM25 与图谱等重负载后台任务前端不可观测、无法取消的黑盒现状；
3. 架构上落地了适配器模式、状态机模式与只读契约零分配优化，代码整洁无历史包袱。
验收结论为：**全部功能项验收合格，系统稳定就绪。**
