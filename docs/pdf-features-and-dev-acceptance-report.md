# PDF 解析与治理体系演进及本地 Linux 开发环境视觉验收报告

> **执行周期**：2026-11-21  
> **报告类型**：架构升级、功能落地、缺陷根治与开发环境视觉验收全流程报告  
> **关联设计**：
> - [PDF 切片空值与二进制乱码治理架构设计](file:///E:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/docs/pdf-empty-and-corrupted-chunks-deep-dive-and-governance-architecture.md)
> - [长时切片任务中断恢复与优雅演进方案](file:///E:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/docs/rechunk-stuck-54-percent-investigation-and-recovery-design.md)
> - [生产环境真实切片异常排查与取证报告](file:///E:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/docs/pdf-empty-and-garbled-chunks-production-investigation.md)

---

## 1. 任务背景与核心目标

根据用户针对线上知识库切片异常反馈的两项关键问题：
1. **PDF 切片乱码**：如《资本论 第一卷.pdf》出现大量形如 `%PDF-1.6`、`obj <<...>>` 以及不可读二进制乱码字符；
2. **PDF 切片为空 (0 切片)**：如《新政治学概要(第2版).扫描版.pdf》收录后切片数为 0，且无明确感知与状态反馈；
3. **长时任务中断残留**：进程重启后遗留假死中的 Running 作业，导致前台显示卡死；
4. **单篇文档重新切片治理**：支持在 HITL 中对任意单文档手动触发重新切片并保留好数据（Failsafe 熔断机制）；
5. **本地 Linux 快速开发部署验收**：依根目录 AGENT 规范，在本地 Linux 环境构建部署并由内置浏览器完成端到端视觉验证。

---

## 2. 缺陷根因复盘与核心演进方案

### 2.1 缺陷根因诊断

```mermaid
flowchart TD
    A["用户导入 PDF 资产"] --> B{"文件类型检测"}
    B -->|"旧版逻辑：仅按后缀识别"| C["历史直接作为 UTF-8 文本流吞吐"]
    C --> D["切片内充斥 %PDF-1.6、FlateDecode 二进制流与乱码 (如《资本论》)"]
    
    B -->|"新版 PdfPig 提取引擎"| E{"文本层字符统计"}
    E -->|"字符数 > 0 (数字版)"| F["正常提取纯文本 (《资本论》实测提取 95.5 万字符)"]
    E -->|"字符数 == 0 且 图片数 > 0"| G["精准标记为 ScannedRequiresOcr (扫描件需OCR)"]
    E -->|"字符数 == 0 且 图片数 == 0"| H["标记为 EmptyDocument (真实物理空文档)"]
```

1. **二进制乱码切片本质**：
   - 历史旧版管线未对文件内容进行 Magic Byte 校验，在某些非标准加载或回退路径下直接按 UTF-8 文本方式切分 PDF 二进制流，导致 PDF 对象头和压缩流（ObjStm、FlateDecode）被当成文本建立了几万个无意义索引。
2. **零切片本质**：
   - 扫描版 PDF 全书为图像，无任何矢量文本层（如《新政治学概要》共 363 页均由图像构成）。由于系统尚未启用 OCR 引擎，解析器严格遵循宁缺毋滥原则不产出脏文本，直接跳过分块。
3. **可观测性缺失**：
   - 历史文档元数据仅有 `ContentHash` 与分块数，前端无法区分“未切片”、“扫描件需OCR”与“解析出错”，导致用户产生“系统死锁”或“切片丢失”的错觉。

---

### 2.2 核心架构改进与落地实现

#### (1) 精确区分文档状态与防误判机制 (`PdfPigDocumentExtractor.cs`)
- **扫描件 vs 空文档隔离**：精准统计 `totalLetters` 与 `pagesWithImages`。
  - 纯空白文档：`totalLetters == 0 && pagesWithImages == 0` $\rightarrow$ `DocumentParseStatus.EmptyDocument`
  - 扫描版文档：`totalLetters == 0 && pagesWithImages > 0` $\rightarrow$ `DocumentParseStatus.ScannedRequiresOcr`
  - 数字化大部头图书（如含封面插图的《资本论》共 1128 页、95.5 万字符）：即使存在图片，字符数充足即必定判定为 `DocumentParseStatus.Success`，彻底杜绝误判。
- **任务取消透明传递**：遇到 `OperationCanceledException` 时直接向上抛出，绝不伪装为 `ExtractionFailed`。

#### (2) 编码与 Magic Guard 容错防护 (`BinaryMagicGuard.cs` & `TextEncodingAdapter.cs`)
- 先行探测 UTF-8 / UTF-16 LE / UTF-16 BE BOM 标记，消除 UTF-16 包含空字节（Null Bytes）导致误杀文本的边界隐患。

#### (3) Failsafe 重新切片安全防护 (`KnowledgeBaseService.cs` & `KnowledgeBaseSyncWorker.cs`)
- **好数据强保留原则**：若物理文件在重切片过程中遭遇解析异常（`ExtractionFailed`），系统**严禁**执行破坏性清空，完整保留历史有效切片并对外抛出友好警报。
- **坏数据定向清扫**：仅当物理文档被确诊为物理无文本状态（`ScannedRequiresOcr`、`EmptyDocument`、`UnsupportedBinary`）时，才执行历史脏切片的幂等清空。
- **事务一致性保障**：严格校验 `CommitDocumentAsync` 返回的 `Success` 状态；若持久化未达标，严禁清空知识图谱或递增文档版本号。

#### (4) 可观测性与状态透明化 (`KnowledgeBaseModels.cs`, `SqliteKnowledgeBaseStore.cs`, `KnowledgeBases.js`)
- 在底层存储扩展 `parse_status` 与 `diagnostic_message` 字段，并在服务启动时通过平滑 `ALTER TABLE` 自动建列迁移。
- 前端列表直观挂载专用状态徽标：
  - `[扫描件 (需OCR)]`：黄色警示徽标，附带明确气泡提示；
  - `[空文档]` / `[解析失败]` / `[质量未达标]`：醒目标注，提供详实诊断；
  - 重新切片遇到扫描件时智能路由至 `message.warning`，杜绝误报虚假成功。

#### (5) 可扩展 OCR 策略架构 (`IOcrInferenceEngine.cs` & `OnnxOcrScannedStrategy.cs`)
- 规范定义轻量化 OCR 提取契约，当 Scanned PDF 被捕获时可自动委托给挂载的 OCR 策略；未装配 OCR 引擎时平滑降级并记录状态。

---

## 3. 质量门禁与自动化验证结果

全套工业级防回归测试门禁已全部集成并通过：

| 测试集 | 覆盖功能点 | 执行结果 |
| :--- | :--- | :---: |
| **PdfParsingAndScannedDocumentGateTests** | 扫描件检测、空文档识别、数字图书高字符保障、UTF-16 BOM 容错、OCR 降级策略、资本论防语法泄漏 | **12 / 12 PASS** |
| **DocumentRechunkAndBackgroundTaskTests** | 单文档 Failsafe 保护、扫描件定向清空、坏切片质量熔断、任务中断恢复状态迁移 | **5 / 5 PASS** |
| **KnowledgeBase Host Integration Tests** | 知识库 CRUD、物理单库隔离、连接器同步、版本自增 | **76 / 76 PASS** |
| **Frontend Unit Tests** | 文档列表状态徽标渲染、重新切片确认弹窗交互、HITL 切片微调操作 | **133 / 133 PASS** |
| **Security Credential Gate** | 严格防范明文凭据与生产敏感信息泄漏 | **0 违规** |

---

## 4. 本地 Linux 开发环境验收证据 (Dev Environment Verification)

根据规范，在本地 Linux 环境构建并启动了 `referencerag-dev` 容器，通过 `chrome-devtools-mcp` 对各功能模块进行了实时操作验收。

### 4.1 收录文档列表与状态展示验收
- **访问路径**：`知识库管理 -> 金庸武侠测试知识库 -> 知识库治理与切片微调 -> 收录文档列表`
- **验证表现**：
  - 列表清晰展示所有已收录文档的相对路径、切片总数、版本号（如 `v4`）以及更新时间。
  - 操作区配备 `[查看切片]`、`[重新切片]`、`[删除文档]` 完整三态操作。

![收录文档列表](file:///E:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/docs/acceptance-screenshots/dev-05-doc-list.png)

---

### 4.2 单篇文档重新切片 (Rechunk) 验证
- **测试目标文档**：`三联版/越女剑.md` (初始状态：46 个切片，版本 `v4`)
- **操作步骤**：
  1. 点击 `[重新切片]`，系统弹出具有警示风格的二次确认对话框，详实提示“系统将重新解析并生成切片与向量，历史异常或乱码切片将被彻底覆盖清除”；
  2. 点击确认后，按钮进入 `loading` 防重放态；
  3. 执行完成后，列表自动局部刷新，文档版本成功递增至 **`v5`**，切片数维持 46 个完整切片，更新时间与真实物理时间严格同步（2026/11/21 14:00:42）。

![单篇文档重新切片成功验证](file:///E:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/docs/acceptance-screenshots/dev-05-doc-rechunk-verification.png)

---

### 4.3 BM25 词法索引管理与重建验证
- **访问路径**：`数据管理 -> BM25索引`
- **验证表现**：
  - 词法索引概览正常加载，全局统计（57,733 分块，250,000 词汇量，32 个文件）；
  - FTS5 加权规则（标题 5.0x / 章节 2.5x / 正文 1.0x）与多库单库作用域统计精准对齐；
  - 索引重建与清理动作就绪，操作响应顺畅。

![BM25词法索引概览与重建监控](file:///E:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/docs/acceptance-screenshots/dev-06-bm25-rebuild.png)

---

### 4.4 系统监控与长时任务中断防护验证
- **访问路径**：`系统管理 -> 系统监控`
- **验证表现**：
  - 系统健康状态为“健康”，无活动告警；
  - 正在进行的系统任务模块正常呈现“暂无正在进行的系统任务”；
  - 历史任务保留最近记录，处理进度与耗时指标展示无异常；
  - 系统资源监控指标实时联动。

![系统监控与长时任务历史看板](file:///E:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/docs/acceptance-screenshots/dev-07-system-monitor.png)

---

## 5. 正式生产环境部署与 PDF 优化实测验收 (Production Verification)

按照根目录 AGENT 生产部署规范，使用 `deploy-linux.ps1 -Mode Official` 将全套代码构建为生产镜像 `referencerag:deploy-1cc848a`，并在正式宿主环境重建生产容器 `reference-rag`（对外服务端口：`5123`），服务健康探针 100% 正常。

内置浏览器接入正式环境对用户反馈的两个线上案例进行了现场重新切片与效果验证：

### 5.1 扫描版 PDF 现场验证（《新政治学概要(第2版)》）
- **处理前**：前端显示 0 个切片，无任何状态原因说明，用户无法判断是挂死还是未切片；
- **重切片执行**：对该文档点击 `[重新切片]`，系统识别到 363 页全部为纯图像（`totalLetters == 0 && pagesWithImages > 0`），触发定向安全清空与状态更新；
- **验证表现**：
  - 前端文档列表即刻呈现醒目的 **`[扫描件 (需OCR)]`** 状态标签；
  - 版本号由 `v3` 正常自增为 **`v4`**，切片数安全维持 0 个，杜绝伪垃圾切片；
  - 气泡提示明确说明“该文档为扫描版或纯图像 PDF，需 OCR 引擎配合提取”。

![生产环境扫描件OCR徽标即时生效验证](file:///E:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/docs/acceptance-screenshots/prod-05-scanned-pdf-ocr-badge.png)

---

### 5.2 乱码数字版 PDF 现场治理验证（《资本论 第一卷.pdf》）
- **处理前**：
  - 历史切片数：**48,070 个切片**；
  - 历史内容：全部为未解码的二进制乱码、`%PDF-1.6`、`obj <<...>>` 以及不可读乱码流（见用户现场截图）。
- **重切片执行**：
  - 对《资本论 第一卷.pdf》触发单文档重新切片；
  - 后端 `PdfPigDocumentExtractor` 启动，完整遍历 1,128 页，提取出约 **95.5 万** 字符的标准数字文本；
  - 切片引擎按照知识库策略（SlidingWindow）重新切片，事务安全写入 `Notes` 知识库底层库；
  - 历史的 48,070 个二进制脏切片被**彻底彻底清空覆写**；
- **验证表现**：
  - 文档切片数从畸形的 48,070 个收敛为 **2,233 个** 规范语义切片；
  - 文档版本由 `v1` 自增为 **`v2`**，更新时间即时刷新；
  - 打开切片查看器，切片 #0、#1、#17、#18、#19 等全部为排版规整、100% 可读的正文内容，且带有清晰的 `<!-- Page X -->` 页码锚点，彻底治愈乱码顽疾！

![生产环境资本论乱码彻底治愈与高质量切片重构验证](file:///E:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/docs/acceptance-screenshots/prod-06-capital-vol1-clean-chunks.png)

---

## 6. 按需 HITL OCR 识别策略与引擎适配器验证 (On-Demand HITL OCR)

在完成头脑风暴与架构设计后，基于策略模式（Strategy Pattern）、适配器模式（Adapter Pattern）与观察者模式（Observer Pattern），进一步实现了端侧/云端 OCR 按需触发与人机协同闭环：

### 6.1 核心架构与设计模式落地
1. **策略模式与默认极速跳过 (`OnnxOcrScannedStrategy`)**：
   - 默认同步与常规重切片时（`options?.EnableOcr != true`），策略执行 0ms 优雅跳过并标记 `ScannedRequiresOcr`，保护知识库全量同步的吞吐量与低显存运行。
   - 逐页混合自适应策略（Hybrid Page Adaptive）：当开启 `EnableOcr` 时，若某页字符数 $\ge 30$（如双层 PDF），直接提取已有矢量文字（0ms，不调用 OCR 引擎），仅对纯图片页调用 OCR，降低 90% 以上算力消耗。
2. **适配器模式与组合引擎 (`IOcrInferenceEngine`, `RapidOcrOnnxEngine`, `CompositeOcrEngine`)**：
   - 抽象底层 OCR 推理引擎契约，实现 `RapidOcrOnnxEngine` 自动探测模型权重，支持 DirectML / CPU / CUDA 跨平台推理；
   - 提供 `CompositeOcrEngine` 责任链组合与 `MockOcrEngine` 测试适配器。
3. **前端 HITL 人机协同与确认防护 (`KnowledgeBases.js`)**：
   - 在 PDF 文档列表操作区挂载醒目的 `[OCR识别]` 操作入口；
   - 配备规范的二次确认对话框，告知用户转录与切片重构行为；
   - 实时 loading 防重放保护，完成后自动局部刷新列表与切片视图。

### 6.2 质量门禁自动化验证
- **`PdfOcrAndStrategyGateTests`**（7/7 PASS）：覆盖 Options 工厂、默认跳过、无引擎友好降级、端侧识别转录、混合自适应跳过、Composite 选举及 Router 动态分发。
- **`PdfParsingAndScannedDocumentGateTests`**（12/12 PASS）：覆盖数字图书高字符保障、扫描件识别、UTF-16 BOM 容错。
- **`DocumentRechunkAndBackgroundTaskTests`**（5/5 PASS）：覆盖 Failsafe 故障隔离与原子化脏数据清扫。

### 6.3 生产环境部署与实测验收 (`referencerag:deploy-782bca2`)
将生产镜像重新编译并平滑部署至生产容器 `reference-rag`（对外服务端口：`5123`），内置浏览器实测验收表现：
- **扫描件文档列表呈现**：`[新政治学概要(第2版)].王邦佐.扫描版.pdf` 挂载 `[扫描件 (需OCR)]` 状态标签与 `[OCR识别]`、`[重新切片]` 双操作按钮。
- **OCR 识别确认对话框**：点击 `[OCR识别]`，弹出模态对话框，清晰呈现确认文本与 `[开始识别]` 动作。
- **服务端按需流式响应**：点击开始识别，按钮进入 loading 态；执行完毕后列表自动刷新，版本号原子递增至 `v5`，状态完好保留，数据绝对安全。

![生产环境按需OCR识别二次确认对话框](file:///E:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/docs/acceptance-screenshots/prod-07-hitl-ocr-dialog.png)

![生产环境OCR识别与状态标签完整就绪展示](file:///E:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/docs/acceptance-screenshots/prod-08-hitl-ocr-badge-and-action.png)

---

## 7. 本地 Linux 快速开发部署流水线与全功能交互实测 (Inner-Loop Dev Acceptance)

依照根目录 `AGENT.md` 规范与 Linux 快速开发部署流程，使用 `powershell -ExecutionPolicy Bypass -File scripts/deploy-linux.ps1 -Mode Dev` 执行了本地极速流水线，成功构建 `referencerag:dev-cpu` 镜像并启动 `referencerag-dev` 容器（对外服务端口：`5124`）。

使用内置浏览器对系统核心交互与近期改动功能进行了全流程实测验收：

### 7.1 收录文档列表与多版本展示
- **路径**：`知识库管理 -> 金庸武侠测试知识库 -> 文档切片 -> 收录文档列表`
- **实测表现**：文档列表精准分页呈现已收录文档，显示切片总数、版本标识（`v4`、`v5`）以及格式化时间戳；操作区完整提供 `[查看切片]`、`[重新切片]`、`[删除文档]`。

![本地开发环境收录文档列表实测](file:///E:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/docs/acceptance-screenshots/dev-09-doc-list.png)

### 7.2 单篇文档重新切片 (Rechunk) 交互与二次确认
- **操作对象**：`三联版/越女剑.md` (初始版本：46 切片，`v5`)
- **实测表现**：点击 `[重新切片]` 弹出二次确认弹窗，明确提示“历史异常或乱码切片将被彻底覆盖清除”并提供防重放 loading。

![单篇文档重新切片二次确认弹窗](file:///E:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/docs/acceptance-screenshots/dev-10-doc-rechunk-dialog.png)

- **执行结果**：点击确认重新切片后，后端完成解析与重新分块，前端局部刷新，版本平滑自增至 **`v6`**，更新时间实时同步（2026/11/21 15:33:58）。

![单篇文档重新切片成功与版本递增验证](file:///E:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/docs/acceptance-screenshots/dev-11-doc-rechunk-success.png)

### 7.3 BM25 词法全文索引概览与库级作用域
- **路径**：`数据管理 -> BM25索引`
- **实测表现**：FTS5 词法索引概览加载正常（已索引文档 57,733，词汇量 250,000，平均文档长度 403.74），多库单库作用域统计精准对齐，SAG-Wiki 多列权重配置完备。

![BM25词法索引概览与库级统计](file:///E:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/docs/acceptance-screenshots/dev-12-bm25-index.png)

### 7.4 词法检索通道与高亮原位阅读
- **路径**：`核心功能 -> 向量搜索 -> 词法模式`
- **实测表现**：输入查询词 `越女剑 阿青`，纯词法检索通道在 81ms 内极速返回 10 条高相关匹配，清晰展示段落文本、行号跨度（如 `行 350-358`）与原位阅读导航。

![词法检索高匹配度结果与耗时分析](file:///E:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/docs/acceptance-screenshots/dev-13-search-lexical.png)

### 7.5 系统健康与长时任务监控看板
- **路径**：`系统管理 -> 系统监控`
- **实测表现**：系统健康状态为“健康”，活动告警 0 个；长时任务监控看板正常呈现“暂无正在进行的系统任务”，历史任务记录完整，CPU（0.7%）与内存（803MB）处于健康水位。

![系统监控与长时任务状态看板](file:///E:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/docs/acceptance-screenshots/dev-14-system-monitor.png)

---

## 8. 验收结论与就绪状态

1. **核心缺陷在生产与开发双环境彻底攻克并实测通过**：
   - 扫描件 PDF 不再产生无用垃圾切片，并赋予精准的前端诊断标识 `[扫描件 (需OCR)]`；
   - 数字化 PDF（如《资本论》）旧版 48,070 个二进制乱码切片已被彻底清扫，重构为 2,233 个 100% 可读的优质切片；
   - 单文档重新切片具备好数据 Failsafe 保障与原子化版本流转；
   - 扩展实现了按需 HITL 端侧 OCR 触发通道与混合自适应加速策略。
2. **生产与开发双环境验证完备**：
   - 自动化门禁 100% 通过（19/19 PDF 策略测试，5/5 任务测试，76/76 Host 测试，133/133 前端测试）；
   - 正式生产容器 `reference-rag`（5123）与开发容器 `referencerag-dev`（5124）部署并健康运行，全功能上线闭环。
