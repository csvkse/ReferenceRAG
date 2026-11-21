# PDF 零切片与乱码：生产核验、解析方案及回归门禁

日期：2026-11-21，Asia/Hong_Kong。源码基准：`d11652f`。

本次范围为用户截图对应的 Notes 知识库、指定正式实例的只读 API、两份源 PDF 及相关源码。仅新增本文档，不修改解析代码，不在生产执行重新切片、OCR、清理或部署。服务器地址、凭据和物理源路径不落入文档；原始 PDF 经已有 SSH 凭据只读取得，在本机内存中分析，没有上传第三方服务。

## 1. 核心结论

**截图中的两个问题原因不同。**

- **扫描版《新政治学概要》零切片**：源文件确实没有可提取文本层。当前代码只有 PdfPig 文本提取和“需要 OCR，安全跳过”的策略，没有 OCR 实现。零切片符合当前降级设计，但产品没有持久化、展示具体原因，使用户看到的只是“0 个切片”。
- **《资本论 第一卷》48070 个乱码切片**：API 中保存的是 `%PDF-1.6`、`/Filter/FlateDecode`、对象流与大量替换字符，证明 PDF 容器字节曾被当成正文。记录仍为 11 月 15 日的 v1，而 PDF 路由修复提交在 11 月 20 日。新解析代码不会自动修复数据库中的历史切片。
- **《资本论》不必整本 OCR**：独立引擎读取源文件发现 1128 页有文本层、约 95.5 万字符。应先用现有 PDF 文本解析能力重建，仅对缺失/低质量页面按需 OCR；不应因页面含图片就判为纯扫描件。

问题本质依次为：**文件格式与字符编码边界失守、扫描图像与文本层能力不同、历史数据没有迁移，以及解析结果未形成持久化状态与安全发布协议。** 调整 chunk 大小、换编码或开 GPU 不能恢复压缩流里的正文。

## 2. 现场与原始文件证据

### 2.1 正式 API 核验

2026-11-21 13:25:49–13:25:50，通过已有 Master Key 的请求头鉴权，只读查询文档与切片接口。

| 项目 | 扫描版《新政治学概要》 | 《资本论 第一卷》 |
| --- | --- | --- |
| 文档 ID | `1bff3b80f4ca401ea36093e3bf6b8b64` | `2a1fa2a86b544e18ab973b151bda6b5d` |
| DocumentVersion | 3 | 1 |
| ChunkCount | 0 | 48070 |
| UpdatedAt（本地时间） | 2026-11-21 13:21:35 | 2026-11-15 18:59:14 |
| 抽样查询 | items 为空，total=0 | 前 3 个切片含 PDF 对象/压缩流乱码，Enabled=true、HasVector=false |
| 首片文本特征 | 无 | `%PDF-1.6`、`12691 0 obj`、`/Filter/FlateDecode`、`/Type/ObjStm` |

零切片文档的 ContentHash 与当前失败分支的 `SHA256(SourceKey + ":ScannedRequiresOcr")` 完全吻合；与 EmptyDocument、ExtractionFailed、UnsupportedBinary 的对应标记不匹配。这是解析状态的**旁证**，不是正式的状态存储接口，也不应让前端靠反推 hash 获取原因。

### 2.2 源文件独立解析

使用现有 Python/PyMuPDF 对两份 PDF **遍历全部页面**，统计 `get_text()`、图像资源和字体资源。该结果用于独立核验文本层，不能冒充当前生产 PdfPig 的重切结果或 OCR 准确率。

| 指标 | 扫描版《新政治学概要》 | 《资本论 第一卷》 |
| --- | --- | --- |
| 源文件大小 | 10,636,519 字节 | 46,411,844 字节 |
| 页数 | 363 | 1146 |
| 有可提取文本的页面 | 0 | 1128 |
| 有图像资源的页面 | 362 | 1145 |
| 有字体资源的页面 | 0 | 1128 |
| 逐页去除首尾空白后的字符数之和 | 0 | 955105 |
| 提取文本 U+FFFD 数量 | 0（因为无文本） | 0 |

例如，《资本论》第 574 页可提取 977 字符；第 1、2 页无文本，第 3 页仅 12 字符。**存在图像资源不代表没有文本层；封面/空白页无文本也不代表整本需要 OCR。** 字符数和替换符统计尚不能证明阅读顺序、汉字映射或表格内容全部准确，正式重建仍要检查典型页和检索质量。

路径核验还发现：《资本论》记录的旧 Locator 已不存在，源文件可在当前连接器根目录下的 `Note/` 子层找到，大小与记录一致。当前单文档重切已有类似一级子目录回退，但正式修复仍应验证源身份和 revision，不能跨连接器任意选择同名文件。

### 2.3 历史代码证据

`git show 6436877^:Infrastructure/ReferenceRAG.Infrastructure/Features/KnowledgeBase/Jobs/KnowledgeBaseSyncWorker.cs` 显示旧同步器使用：

```csharp
using (var reader = new StreamReader(stream, Encoding.UTF8))
{
    textContent = await reader.ReadToEndAsync(ct);
}
```

`6436877` 于 2026-11-20 21:13 引入 PDF 解析与门禁。结合 11 月 15 日的 v1 文档和 PDF 原始语法切片，历史二进制直读是有充分证据支持的来源；未取得当时部署程序集，不把历史提交时间等同于精确上线时间。

旧文档 [PDF 架构调研](pdf-parsing-and-scanned-document-architecture-study.md)记录了当时的直读缺陷。本文是针对**当前已经存在 PdfPig/路由/降级代码，但历史污染和 OCR 缺失仍然可见**的补充，不应继续把旧版 StreamReader 描述成当前 sync 的实现。

## 3. 当前系统已有什么、缺什么

| 能力 | 已核实位置 | 复用与限制 |
| --- | --- | --- |
| PdfPig 依赖 | `Directory.Packages.props`、Infrastructure csproj | 已声明并引用 `PdfPig 0.1.16`，无需再次选一个同类库作为 P0 前提 |
| 文件路由 | `Infrastructure/ReferenceRAG.Infrastructure/Features/DocumentExtraction/Services/DocumentExtractorRouter.cs` | 默认 PDF → 文本 → 不支持格式；sync 和单文档重切已调用 |
| 魔数防线 | 同目录 `Guards/BinaryMagicGuard.cs`；`Extractors/PlainTextDocumentExtractor.cs` | `%PDF-`/已知二进制拒绝作为文本；当前只从流头读取有限前缀，不是完整格式识别器 |
| 电子 PDF 提取 | `Extractors/PdfPigDocumentExtractor.cs` | 遍历页面、读取 Letters/Text、添加 Page 注释；未做阅读顺序重建和按页质量裁决 |
| 扫描处理扩展点 | `Domain/IScannedPdfHandlingStrategy.cs` | 有接口，可扩展；签名绑定 PdfPig 文档，必要时补独立页面/源流契约 |
| 扫描默认策略 | `Strategies/GracefulDegradeScannedStrategy.cs` | 实际只返回 ScannedRequiresOcr 和空文本，不是 OCR 引擎 |
| DI 注册 | `DocumentExtractionExtensions.cs`、`KnowledgeBaseStorageExtensions.cs` | 已注册路由与默认降级策略；当前未发现 OCR 实现/模型注册 |
| 噪声检测 | `Infrastructure/ReferenceRAG.Infrastructure/Helpers/NoiseChunkDetector.cs` | PDF 元语法正则、替换符/控制符 2% 阈值；sync、重切及 SeparatorNoiseAdapter 使用 |
| 文本编码 | `Features/Storage/Services/TextEncodingAdapter.cs` | 已拦截已知二进制；只能处理真实文本，不能替代 PDF 解压/字体映射 |
| 写入能力 | `Features/KnowledgeBase/Storage/SqliteKnowledgeBaseStore.cs` | CommitDocument 事务替换 chunks、FTS、词法和向量，含代次/文档版本校验；没有统一来源感知的内容质量准入 |
| 模型运行时 | `Features/ModelManagement/Runtime/ModelRouter.cs`、ModelLease、ONNX 包 | 可复用调度/设备探测思路；现有 embedding/rerank adapter 不是 OCR adapter，也不含 OCR det/rec/字典/预处理 |
| 受管作业 | `Business/ReferenceRAG.Business/Features/KnowledgeBase/Services/KnowledgeBaseJobWorker.cs`、JobRecoveryService | 当前源码已存在，可纳管 OCR/迁移；不要照搬前一轮调研时“尚无受管 worker”的旧结论；持久化续跑能力另行验证 |
| UI | `Business/ReferenceRAG.Business/wwwroot/app/views/KnowledgeBases.js` | 单文档重切读取 message 后统一 success 提示，没有按解析状态分流；文档列表只展示计数/版本等 |

`KnowledgeBaseDocumentRecord` 与 documents 表未发现持久化 ParseStatus、DiagnosticMessage、页数、解析器版本或 OCR 状态字段。当前状态主要存在于提取返回值、重切响应和日志中，刷新文档列表就无法解释为何为零。

## 4. 当前代码仍存在的同类缺陷

### 4.1 非成功解析一律清空，混淆“安全隔离”与“成功替换”

`KnowledgeBaseSyncWorker.ExecuteSyncAsync` 和 `KnowledgeBaseService.RechunkDocumentAsync` 对所有 `Status != Success` 都提交空 IndexBatch，清除历史切片/向量，再删图谱节点。这对**已确认是二进制污染**的数据有清理效果，但对临时解析失败、取消、密码保护或库兼容问题，可能误删之前有效内容。

PdfPig 提取器用通用 `catch(Exception)` 返回 ExtractionFailed，可能把 OperationCanceledException 也转换成解析失败。后续空提交若仍能执行就有破坏旧结果的风险；若 token 已取消，提交可能直接抛异常。不能将“有 catch”当作正确取消协议。

非成功分支还没有检查 `CommitDocumentAsync` 返回的 Success，就继续删图谱、构造版本加一的重切响应。必须以真实提交结果为准，提交失败不得报告已清理/已升级版本。图谱删除与文档提交也不是跨组件原子事务。

### 4.2 扫描判定过粗

当前用整本文档平均 Letters <15 且存在图像，或总 Letters=0 判扫描。问题包括：

- 无文字的空白 PDF 也被判为 ScannedRequiresOcr；现有扫描测试夹具实际上就是空白页，不是真实图片页。
- 混合文档平均值达标就整体成功，扫描页可能静默缺失；稀疏但有效的文字页也可能被误判。
- 字体/CMap 映射异常可以产生“很多字符但不可读”，不在密度判断覆盖内。
- `page.Text` 保留 PDF 内容流顺序，不能保证段落、双栏、空格和表格阅读顺序；这类乱序与 `%PDF` 二进制泄露不是同一个问题。

### 4.3 门禁覆盖不完整，且存在误杀风险

- 修复写入逻辑不会自动更新 11 月 15 日的旧 chunks/FTS。
- `LexicalFileIndexer.IndexAsync` 当前仍直接 `File.ReadAllTextAsync(path)`；它属于另一条索引链，若入口允许 PDF 就可能绕过路由。应测试真实入口和文件过滤，不能仅凭这个局部调用断言当前生产样本由它产生。
- 手工录入、手工增改切片、底层 CommitDocument 等入口需要统一准入。不能只在一个 worker 里检查。
- 正则会匹配合法 PDF 技术文档/代码示例中的 `obj`、`stream`、`FlateDecode`。应结合文件来源、结构与乱码证据判定，不要看到一个关键字就删除整段正常技术内容。
- BinaryMagicGuard 把前缀 NUL 视作二进制，而 TextEncodingAdapter 在 BOM 解码前调用它；UTF-16 普通文本常含 NUL。需要单独门禁保护正常 UTF-16 文本，避免为了挡 PDF 又引入“文本切片为空”。

## 5. 库与主流方案对比

以下基于官方项目/文档，查询日期为 2026-11-21；未实测 OCR 性能，不采用“毫秒每页”“中文极高准确率”“100% 兼容”等未经基准验证的断言。

| 方案 | 适用能力 | 本项目取舍 |
| --- | --- | --- |
| [PdfPig](https://github.com/UglyToad/PdfPig)，Apache-2.0 | .NET PDF 文本、字形坐标与版面分析 | 保留现有库，先改质量判定与阅读顺序；不能识别扫描图片里的文字 |
| [Docnet / PDFium](https://github.com/GowenGit/docnet)，包装层 MIT | .NET 调用 PDFium 提取/页面渲染 | OCR 前的页面渲染候选；需验证原生依赖、RID/架构及上游组件许可；不是单独的 OCR 方案 |
| [RapidOCR](https://github.com/RapidAI/RapidOCR)，Apache-2.0 | OCR 工具链，支持 ONNX 等后端，模型有独立来源/哈希说明 | 优先评估本地 OCR worker；可与现有 ONNX 经验结合，但不能假设 pip 包可直接变成 .NET adapter |
| [Tesseract](https://github.com/tesseract-ocr/tesseract)，Apache-2.0 | 多语言本地 OCR | CPU 基线与候选后端，需语言数据及原生打包；中文书籍质量必须同集评估 |
| [OCRmyPDF](https://ocrmypdf.readthedocs.io/en/latest/cookbook.html) | 为 PDF 增加可检索 OCR 文本层，区分跳过已有文本、重做等模式 | 适合离线预处理成**新文件**后交给 PdfPig；较重，不直接覆盖原文件，依赖清单和许可证单独核验 |
| [Unstructured](https://docs.unstructured.io/open-source/core-functionality/partitioning) | PDF 的 auto/fast/hi_res/ocr_only 路由及结构化 elements | 借鉴电子文本优先、不可提取再 OCR、版面处理策略；整体引入需 Python/模型及服务运维 |
| [Docling](https://github.com/docling-project/docling)，代码 MIT，模型独立许可 | 文档转换、版面/表格、OCR 与统一结构化表示 | 本地 sidecar 候选，适合复杂版面；先小样本对比，避免为两份 PDF 引入整套平台 |

PdfPig 官方明确建议索引/RAG 使用 ContentOrderTextExtractor 或版面分析，而非直接 page.Text：[官方说明](https://github.com/UglyToad/PdfPig)。这能改善拼接和顺序，但不承诺复杂双栏全正确。Unstructured 官方对 PDF 区分可提取文本与 OCR 路径：[策略说明](https://docs.unstructured.io/open-source/core-functionality/partitioning)。RapidOCR 官方列出模型来源与哈希/许可要求：[项目说明](https://github.com/RapidAI/RapidOCR)。

建议选型：**现有 PdfPig 继续处理数字文本；新增可替换 OCR adapter，首轮对比 RapidOCR 与 Tesseract；复杂表格再比较 Docling。** 页面必须完整渲染后识别，不能把 `GetImages()` 返回的任意内嵌图像直接当成整页：PDF 还可能包含裁剪、旋转、遮罩、多个图像块及矢量内容。

本次 PyMuPDF 仅作为已有诊断工具使用，不代表建议直接加入产品依赖；若产品采用它，需另行核查所选版本的授权条件。

## 6. 具体解决方案

### 6.1 P0：先让状态真实，阻止误清空

1. 为 documents 或独立 ingestion_results 表保存：SourceHash、SourceRevision、ParserId/Version、ParserConfigHash、ParseStatus、DiagnosticCode、PageCount、TextPageCount、OcrPageCount、EmptyPageCount、RejectedChunkCount、最后尝试时间、活动文档版本和尝试 ID。源文件 hash 与解析输出 hash 分离，不再把状态编码进 ContentHash。
2. 新增明确结果：RequiresOcr、Empty、Encrypted/PasswordRequired、Unsupported、ExtractionFailed、Cancelled、QualityRejected、PartiallyParsed。列表与详情显示原因及重试条件；HTTP 成功受理不等于文档提取成功。
3. 取消异常直接向外传播。解析失败/超时保留旧活动内容并标记最新尝试失败；确定的无文本扫描件标记 RequiresOcr。若旧内容已经被确认是污染，显式隔离其检索资格；“保留旧版本”不等于继续对外检索旧垃圾。
4. 清理已确认污染是独立、有审计的动作；正常重建先产生合格候选再原子发布。依据真实 CommitResult 返回版本与结果；失败时不删除图谱节点、不报告清理成功。
5. 对被噪声门禁全部剔除的“Success”结果，转为 QualityRejected 并记录拒绝原因；不能宣称解析成功且静默保存零片。

前端分流示例：`RequiresOcr → warning + 配置 OCR/提供文字版`；`ExtractionFailed → error + 诊断编号/重试`；`Empty → 信息提示`；`PartiallyParsed → 缺页计数`；仅合格内容发布后显示成功。

### 6.2 P0：历史脏数据定向迁移

针对《资本论》的建议操作顺序（本次未执行）：

1. 在限定 KB/connector/document 范围生成只读候选清单，标识旧 ParserVersion 缺失、PDF 容器语法、高替换符/控制符等组合证据；人工确认，不按扩展名批量删除。
2. 固定正确的连接器和源身份，验证新路径、文件大小、hash/revision；备份一致的 SQLite 数据，包含 WAL 的正确备份方式。
3. 读取源 PDF，用当前 PdfPig 离线生成候选；核验多页正文、章节、页数覆盖、噪声、阅读顺序及几个检索问题。没有真实提取基准前不承诺新切片数，也不把 48070 当作期望值。
4. 以 ExpectedDocumentVersion 和 IndexEpoch 校验并原子替换 chunks/FTS/词法/向量；并发编辑发生时拒绝过期发布，保留用户修订，不传 0 绕过版本校验。
5. 图谱和内存投影按版本更新，失败可重试；核对旧 chunk_id/FTS 命中已不可见，新内容可检索。空 embedding 模式下 HasVector=false 是正常行为。

扫描版《新政治学概要》不应反复点重切期待非零；没有 OCR 时明确保留 RequiresOcr。OCR 就绪后才对该文档提交候选重建。

### 6.3 P1：按页解析与 OCR，不整本文档二选一

```text
格式识别 → PDF 结构解析 → 每页提取数字文本并评估质量
    ├─ 合格文本页 → 阅读顺序/段落重建
    ├─ 空白页 → EmptyPage（不 OCR）
    ├─ 图像/低质量文本页 → 渲染完整页 → OCR → 质量检查
    └─ 受保护/损坏页 → 明确错误或部分解析结果
             ↓
统一 Page/Block 表示 → 来源感知的质量门禁 → 章节/段落切片 → 原子发布
```

每个文本块保存 pageNumber、bbox、extractionMethod、parserVersion、ocrModelHash；切片保存页码范围及来源块。PDF 的 `行1-1` 不是原始页定位，HTML Page 注释也不能代替结构化页码。

页质量结合非空字符、可见字符、替换符/控制符、布局和词序、有效正文与图像覆盖等信号。阈值需要标注集校准；不能单凭“少于15字”或“有图片”触发 OCR，也不能用“有汉字”保证内容正确。对已有 OCR 层只在质量差时重识别，按区域/块去重，避免原文与 OCR 重复入库。

### 6.4 P1：OCR 的运行约束与复用

- 复用受管作业、取消、日志与模型资源协调；新增 `IPdfPageRenderer`、`IOcrBackend` 和结构化页结果，先单一后端实现，保留替换边界。
- 固定 det/rec/方向模型、字典、输入尺寸、前后处理和模型 hash；CPU 先跑通，按实测再选 CUDA/DirectML/OpenVINO。不能因现有 ORT 包已安装就认为整个 OCR 链已经兼容。
- 按页流式处理，限制文档字节数、页数、渲染像素、并发、内存和临时磁盘；整本复制进 MemoryStream 再整本文字拼接的当前实现需要纳入预算。
- 同步/原生解析和 OCR 可能不响应 token，需要硬超时时放到子进程；仅 WaitAsync 超时不能终止原任务，不能让超时旧 worker 继续发布。
- 按 SourceHash + page + renderer/parser/OCR/config hash 保存检查点，恢复前验证版本；不能复用另一模型/另一版源文件的页结果。失败页面有明确结果，不以总文件数进度掩盖页内停滞。

### 6.5 P2：质量评估与选型门槛

准备中文印刷体、扫描噪点/倾斜、双栏、表格、公式、竖排、英文混排、已有 OCR 层、损坏字体映射和空白页的匿名/可公开夹具。对每个候选测 CER/WER、页块阅读顺序、表格结构、页码溯源，以及端到端检索 Recall@k 和答案来源正确性；同时记录页耗时 P50/P95、峰值内存、失败率与部署体积。

OCR 模型置信度跨引擎不可直接等值比较。先约定基线和可接受误差，再决定 OCR 后端及默认阈值；本次未跑 OCR 对比，不能把架构可复用写成性能或准确率已达标。

## 7. 门禁与避免回归测试

本节为待实现/补齐的验收规格，不代表已经新增测试。

| 门禁 | 输入或故障注入 | 必须验证的可观察行为 |
| --- | --- | --- |
| G-PDF-BOUNDARY-01 | 原始 PDF、改为 .txt 的 PDF、无后缀、短读/不可 seek 流、前置字节或 BOM | 可靠识别或明确拒绝，任何入口不把压缩字节直接当正文；正常文本不漏读流头 |
| G-PDF-DIGITAL-01 | 有中文 ToUnicode 的数字 PDF、双栏及跨页章节 | 提取关键正文，顺序/页码合理，无容器语法；不是只测 Helvetica 英文 |
| G-PDF-SCAN-01 | 真正图片扫描 PDF、空白 PDF、稀疏文字封面、混合文本/扫描页 | 区分 RequiresOcr/Empty/Success/PartiallyParsed，不能整本平均值误判 |
| G-PDF-OCR-01 | 旋转/倾斜、分块图像、透明遮罩、中文页 | 渲染完整页再 OCR，文本与页码/bbox 可追溯，准确率达到预先约定基线 |
| G-PDF-FAILSAFE-01 | 已有有效旧切片，解析器抛异常、超时、密码错误或取消 | 旧有效版本不被空提交清除；最新尝试状态明确；旧污染按隔离策略不可检索 |
| G-PDF-PUBLISH-01 | CommitResult 失败、旧文档版本、旧 epoch、图谱更新失败 | 不虚报成功/版本加一，不提前删图谱；后续对账能收敛 |
| G-PDF-LEGACY-01 | 预置历史 `%PDF/ObjStm` 乱码及 FTS/向量，再执行迁移 | 旧污染不再命中，正文可检索；重跑幂等，保护人工编辑版本 |
| G-PDF-QUALITY-01 | 提取器返回 Success 但所有片段被拒绝 | 持久化 QualityRejected，前端解释原因，不能显示正常成功 |
| G-TEXT-ENCODING-01 | UTF-8/GB18030/UTF-16 LE/BE/BOM 的正常中文文本 | 不因 NUL 启发式误拒绝 UTF-16；PDF 不进入编码回退 |
| G-CONTENT-PROVENANCE-01 | 合法 PDF 技术教程中的 obj/stream 示例与真实容器噪声 | 教程可索引，真实二进制泄露被拦截，不用全局关键字黑名单替代格式路由 |
| G-INGEST-ENTRY-01 | KB sync、单文档重切、LexicalFileIndexer 真实入口、手工录入/修改 | 接受格式和质量政策一致；拒绝不支持输入时给出原因，不绕过准入 |
| G-PDF-UI-01 | 刷新页面后展示扫描需 OCR、解析失败、空文档、部分解析 | 状态来自持久化 API，两页面一致，绿色成功提示不掩盖错误 |
| G-PDF-RESUME-01 | 完成若干页后杀解析子进程，再恢复；恢复时更换源文件/模型 | 不永久 Running、不重复页块、不用旧配置检查点、不接受失效执行者发布 |
| G-PDF-SCALE-01 | 合成千页图文混排及高像素页面 | 预算内完成或明确失败，无无界内存/线程增长，进度可解释 |

现有 `PdfParsingAndScannedDocumentGateTests` 共 7 项，覆盖签名、文本拒绝 PDF、英文电子文本、降级、噪声、编码阻断与混合连接器同步。其扫描夹具是**空白页而不是扫描图像页**，并未覆盖 OCR、中文复杂字形、真实大书、迁移、临时失败保旧和状态刷新。

现有 Host 重切测试通过 mock 返回 ScannedRequiresOcr 验证清空污染；没有区分“确认污染”与“旧内容有效但本次解析失败”。新增测试先表述这两个不同场景，避免一概撤销清理能力。

CI 需要实际接入：`.github/workflows/web-build.yml` 的 Core 过滤仅有 `ContractTests|AuditFixRegressionTests`，不会命中 PdfParsingAndScannedDocumentGateTests；PR paths 也未包含 `tests/**`。应显式加入 PDF 套件并覆盖测试路径；增加 Linux 原生渲染/OCR 和真实子进程崩溃 job，不能仅本地通过后声称持续集成已防回归。

## 8. 实际验证、限制与交付

已运行：

```powershell
dotnet test tests/ReferenceRAG.Tests/ReferenceRAG.Tests.csproj --no-restore --filter FullyQualifiedName~PdfParsingAndScannedDocumentGateTests --verbosity quiet
dotnet test tests/ReferenceRAG.Host.Tests/ReferenceRAG.Host.Tests.csproj --no-restore --filter FullyQualifiedName~DocumentRechunkAndBackgroundTaskTests --verbosity quiet
```

结果：**7/7 和 5/5 通过，无跳过**。构建有现有 obsolete、nullable、平台及测试分析器警告，未顺带修复。生产文档/切片 API、原始 PDF 全页文本层统计、旧版源码和当前路由均已核验。

未执行：生产重切/清理、真实 OCR、候选引擎准确率/性能基准、当前 PdfPig 对两份完整生产 PDF 的离线重建、复杂版面人工逐页验收、完整测试集、新门禁实现或产品代码修复。

实施顺序：**先修状态持久化与失败保旧，定向迁移《资本论》旧乱码；再增加按页 OCR，让扫描版真正可检索；最后优化复杂版面和批处理性能。** 不复用未经确认的旧报告精度/耗时结论，也不将零切片等同于解析器失效。
