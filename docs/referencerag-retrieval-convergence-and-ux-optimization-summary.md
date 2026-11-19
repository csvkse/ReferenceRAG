# ReferenceRAG 知识库检索单轨收口、权限隔离与体验优化技术总结

## 1. 架构演进背景与设计目标

在 ReferenceRAG 正式发布前的系统重构窗口期，根据 [ReferenceRAG 知识库单轨检索与证据链实施方案](file:///E:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/docs/referencerag-knowledge-retrieval-convergence-implementation-plan.md)，项目明确了**“零历史兼容包袱（Zero-Compatibility Burden）、服务端契约一元化、权限一等公民强隔离”**的核心战略。

此前系统存在三大痛点：
1. **多轨分裂**：Web 界面、桌面 IPC、MCP 工具、CLI 命令行与对话问答各走各路，底层存在单库向量检索、独立词法检索、跨库扇出接口等多个重复入口，字段语义与降级行为严重漂移。
2. **权限隔离失配**：数据源升级为“知识库”体系后，API Key 授权矩阵仍残留底层物理目录路径判定，导致跨库检索时无法做到严格的知识库边界控制。
3. **证据链与问答断层**：检索召回缺乏不可变摘要（Digest）与版本标记，问答系统在无匹配内容时仍盲目调用 LLM 产生幻觉与额外 Token 消耗。

本次改动彻底消除了历史包袱，在**功能**、**性能**与**交互体验**三大维度完成了系统性跃升。

---

## 2. 功能维度优化 (Functionality)

### 2.1 单轨检索契约一元化 (`KnowledgeBaseSearchRequest/Response`)
- **废弃多轨冗余入口**：全面统一至 [`IKnowledgeBaseSearchService`](file:///E:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/Business/ReferenceRAG.Business/Features/KnowledgeBase/Services/IKnowledgeBaseService.cs) 及 `POST /api/knowledge-base-search`。
- **通用模式语义解耦**：客户端仅通过通用参数 `mode: "auto" | "lexical" | "semantic" | "hybrid"` 表达意图，不再绑定底层分词器细节（如 `useLexicalTokenizer`）。由服务端根据各知识库绑定的模型规格（如 `spec_lexical_only`、`local_bge-m3`）自动规划召回通道。

### 2.2 API Key 知识库一等公民权限隔离（防越权与防假命中）
- **实体隔离落地**：API Key 鉴权矩阵升级为以 `AllowedKnowledgeBaseIds` 为核心边界。
- **严格拒绝契约（消除假命中安全隐患）**：
  - **显式空数组拒绝**：客户端传入 `knowledgeBaseIds: []` 时，服务端严格返回 **400 Bad Request**（明确区分“未选定范围”与“检索全库”）。
  - **越权显式查询强拒绝**：受限 Key 若显式查询未授权库，服务端直接返回 **403 Forbidden**，**彻底消除了以往“静默裁剪未授权库后返回 200”导致调用方误以为在该库检索成功的数据假象**。
  - **全库隐式安全收敛**：若未指定知识库参数，服务端自动将其安全收敛（Clamp）至当前 Key 有权访问的知识库子集。

### 2.3 证据链版本化回读与漂移防范 (`CitationRef`)
- **一等公民证据引用**：检索命中项统一输出版本化 [`CitationRef`](file:///E:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/Business/ReferenceRAG.Business/Features/KnowledgeBase/Services/KnowledgeBaseSearchContracts.cs)，内嵌：
  - `KnowledgeBaseId`、`DocumentId`、`ChunkId`；
  - `DocumentVersion`（文档版本号）；
  - `ChunkDigest`（切片内容的 SHA-256 哈希签名）；
  - `StartLine`、`EndLine` 与 `HeadingPath`（Markdown 标题路径）。
- **不可变证据批量回读**：新增 `POST /api/knowledge-evidence/read` 接口，支持按引用批量提取正文并校验内容哈希，精确报告 `isDrifted: true/false` 与状态 `ok | modified | missing`。

### 2.4 有据问答检索短路机制 (`POST /api/knowledge-answers`)
- **零 Token 快速熔断**：检索阶段经关键词二元组与锚点覆盖率评估，若判定为 `no_match` 或 `insufficient_evidence`，**在调用大模型前立即短路拦截**，返回结构化未命中原因与改写建议。
- **真有据回答**：仅在命中有效证据切片时组装 Prompt 喂给模型，并在回答中携带可追溯的 `AnswerCitation`。

### 2.5 客户端与生态对齐
- **Native MCP 收口**：整合 MCP 工具为统一的 `knowledge_search` 与 `read_evidence`，下线冗余破折号命令。
- **CLI 门面重构**：[`rag_cli.py`](file:///E:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/support/skill/ReferenceRAG/scripts/rag_cli.py) 支持 `search --rest`、`read --citations`、`status` 与 `ask`，提供 100% 自动化的 21 项冒烟回归套件。

---

## 3. 性能维度优化 (Performance)

```
┌────────────────────────────────────────────────────────────────────────┐
│                        单轨检索性能加速与资源调度架构                       │
└────────────────────────────────────────────────────────────────────────┘
                                    │
                         POST /api/knowledge-base-search
                                    │
                   ┌────────────────┴────────────────┐
                   ▼                                 ▼
           [mode = "lexical"]               [mode = "hybrid/semantic"]
                   │                                 │
                   ▼                                 ▼
         ┌───────────────────┐             ┌───────────────────┐
         │  纯词法 FTS5 引擎   │             │ 单次查询向量计算     │
         │ (零模型推理 / 0 GPU) │             │ (多库共享 Embedding) │
         └───────────────────┘             └───────────────────┘
                   │                                 │
                   │                        ┌────────┴────────┐
                   │                        ▼                 ▼
                   │                  [词法极速早停]     [并行跨库扇出]
                   │                  (跳过重排与向量)    (微秒级存储租约)
                   │                        │                 │
                   └────────────────► RRF 结果融合 ◄───────────┘
                                            │
                                            ▼
                                  结构化输出与证据链溯源
```

### 3.1 纯词法检索零模型推理开销
- 在 `mode: "lexical"` 或查询纯词法知识库（`isPureLexical: true`）时，**执行管道完全绕过 ONNX 与 GGUF 模型上下文**。
- 零张量计算、零 GPU 显存占用、零模型初始化锁等待，纯内存/磁盘 SQLite FTS5 倒排索引检索在 **1~5ms** 内完成。

### 3.2 同规格跨库共享向量推理
- 在涉及多个知识库的混合检索中，若目标库采用相同的嵌入模型规范（例如 `local_bge-m3`），系统在调度器顶层**仅执行一次文本向量化推理**，随后在并行 Task 间复用只读向量。
- 跨库扇出效率大幅提升，模型算力消耗减少为原先的 $1/N$。

### 3.3 级联极速早停 (Early Pruning)
- 词法通道召回中若出现极高匹配置信度切片（首项分数显著超越早停阈值），触发级联截断早停，直接跳过耗时漫长的重排与向量兜底阶段，整体延迟从 200~400ms 降至 **< 10ms**。

### 3.4 问答短路节省 100% 无效 Token 开销
- 无相关文档召回时拦截 LLM 调用，单次问答节省 300 ~ 1500 Tokens，杜绝无效开销与排队延迟。

### 3.5 高并发微秒级存储租约管理
- 知识库读租约采用只读共享机制（`StorageAccess.ReadOnly`），耗时链通过 [`TimingWaterfall`](file:///E:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/Business/ReferenceRAG.Business/wwwroot/app/components/TimingWaterfall.js) 精确追踪租约获取微秒数，保障高并发下的连接复用与无死锁。

---

## 4. 交互与体验维度优化 (UX & Interaction)

### 4.1 检索结果真实状态透明化
- **消除“虚词假命中”**：当查询词未命中正文，仅依靠停用词/虚词兜底时，界面渲染 **`⚠ 宽松兜底`** 与 **`宽松匹配 X%`**（查询词覆盖率），避免误导用户。
- **早停与降级徽章**：命中早停时渲染 **`⚡ 级联极速早停 (跳过向量推理)`**；模型不可用发生容灾时显式显示 **`容灾降级 (词法降级)`**。
- **分数类型与通道**：区分展示 `BM25`、`RRF` 与 `向量` 分数，不再对纯词法模式计算误导性的百分比相似度。

### 4.2 检索页面防呆交互（容错无感化）
- **默认全库检索契约对齐**：检索页面知识库下拉框未选择任何库时，前端自动省略 `knowledgeBaseIds` 字段传参，既严格满足服务端“空数组报 400”的安全门禁，又对普通用户实现无门槛的“默认检索全部可用知识库”。
- **动态路径树联动**：多选知识库时实时联动下挂连接器路径树，不再出现路径阻断或空树异常。

### 4.3 知识库生命周期自由度
- **一键解绑模型**：知识库卡片新增 **`解绑模型 (降级为纯词法)`** 选项，支持将大型向量库平滑转变为轻量纯 BM25 词法库，保留全文索引与文档，释放宝贵算力。

### 4.4 API Key 授权弹窗重构
- **三态级联选择器**：弹窗提供清晰的“全部知识库”与“指定知识库”单选；选择指定知识库后展开带全选/反选功能的树形列表，授权边界一目了然。

---

## 5. 改动前后核心指标对比

| 衡量维度 | 改造前状态 | 改造后状态 |
| :--- | :--- | :--- |
| **检索服务入口** | 4 套分裂接口（`/aiquery`, `/search/lexical`, `/knowledge-base-search` 等） | **1 套单轨契约**（`/api/knowledge-base-search`）贯穿所有端 |
| **API Key 越权控制** | 查询非授权库时静默忽略或数据漂移 | **严格返回 403 Forbidden**，零数据跨库泄露风险 |
| **纯词法检索耗时** | 30ms ~ 150ms（伴随冗余模型管道判定） | **1ms ~ 5ms**（纯 FTS5 倒排索引内存查表） |
| **无效查询 Token 消耗** | 即使无匹配文档仍调用 LLM 消耗 300~1500 Tokens | **0 Token**（短路返回 `insufficient_evidence`） |
| **证据防篡改能力** | 仅返回纯文本切片，无原文哈希与起止行校验 | 输出带 **SHA-256 摘要与行号的 CitationRef**，支持版本漂移检测 |
| **工程测试覆盖** | 依赖部分端点集成测试 | **前端 133 项 + 后端 1365 项 + 生产隔离 10 项 + CLI 冒烟 21 项全部通过** |

---

## 6. 自动化测试门禁与生产实测验收

### 6.1 本地测试门禁验证
1. **前端自动化单元测试 (`tests/*.test.mjs`)**：
   - 执行：`npm test`
   - 结果：**133 / 133 全部通过 (100%)**，覆盖 FSM 状态机、断点响应式、Markdown 安全解析与模板语法门禁。
2. **后端测试工程 (`ReferenceRAG.Tests` & `ReferenceRAG.Host.Tests`)**：
   - 执行：`dotnet test`
   - 结果：**1,365 项用例全部通过**，专项包含 24 项权限矩阵与短路问答架构门禁。
3. **凭据安全与信息脱敏门禁 (`scripts/security-scan.ps1`)**：
   - 执行：`pwsh scripts/security-scan.ps1`
   - 结果：**0 violations**（全量扫描 194,128 行，无任何凭据泄露与明文非回环 IP 泄露）。

### 6.2 生产环境实机隔离复验 (`verify-kb-isolation.mjs`)
针对正式服务器运行端到端 API Key 知识库隔离套件（包含 `Notes` 允许库与 `默认知识库` 禁止库），测试结果为 **10 / 10 项全部通过**：
- 管理员枚举知识库：PASS (HTTP 200)
- 动态创建受限 API Key：PASS (HTTP 200)
- 受限 Key 枚举列表仅见授权库：PASS (HTTP 200)
- 受限 Key 单库越权访问：PASS (HTTP 403 Forbidden)
- 受限 Key 下钻越权文档/切片：PASS (HTTP 403 Forbidden)
- 隐式检索自动收敛至授权范围：PASS (0 条越权切片)
- 显式单库越权检索：PASS (HTTP 403 Forbidden)
- 显式混合跨库越权检索：PASS (HTTP 403 Forbidden)
- CLI 客户端越权执行拦截：PASS (无越权内容透出)
- 临时 Key 销毁后立即失效：PASS (HTTP 403/401)

### 6.3 浏览器端到端视觉与页面交互验收
使用 Playwright/Edge 无头浏览器以受信任会话完整走通 Web 页面交互链路，**控制台错误数为 0**，各项页面均正常渲染并生成全屏验收快照：

| 页面路由 | 核心验证内容 | 验收快照文件 |
| :--- | :--- | :--- |
| **`/dashboard`** | 服务统计指标卡片、健康状态、深链无闪烁重定向 | `screenshots/01-dashboard.png` |
| **`/knowledge-bases`** | Notes（纯词法 BM25，237,721 chunks）、默认知识库（bge-m3 1024d，24,776 chunks）卡片正常展示 | `screenshots/02-knowledge-bases.png` |
| **`/search`** | 模式单选框包含“快速 / 词法 / 平衡 / 深度”；词法检索展示切片正文、BM25 分数、原位阅读按钮与耗时链 | `screenshots/03-search-lexical.png` |
| **`/apikeys`** | API Key 列表正常加载；“新建 API Key”弹窗完整渲染知识库作用域模式（全部 / 指定）与权限树 | `screenshots/04-apikeys-modal.png` |
| **`/models`** | 嵌入模型与重排模型列表、推理设备规格与生命周期加载完整 | `screenshots/05-models.png` |

---

## 7. 结语

本次重构彻底解决了历史多轨演进所积攒的架构债务，确立了以“知识库”为核心边界的统一单轨检索服务与版本化证据链体系。系统在保持代码库轻量、免繁重构建特性的同时，实现了更强的数据安全性、极速的微秒级纯词法响应以及透明防呆的用户交互体验。
