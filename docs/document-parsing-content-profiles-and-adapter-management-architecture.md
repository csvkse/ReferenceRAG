# 多格式解析、内容画像与动态适配器管理架构

> 版本：v1.1；统一审核日期：2026-10-31（Asia/Hong_Kong）。
> 跨专题状态/版本词典、核心契约与阶段顺序以[统一契约与实施计划](unified-architecture-contracts-and-implementation-plan.md)为准，本专题负责处理算法与适配管理细节。
> 状态：可行性调研与目标设计；没有实现业务代码、安装解析库或接入外部解析服务。
> 上位约束：[知识库隔离与多模型执行架构](per-source-data-isolation-and-multi-model-dynamic-lifecycle-architecture.md)。允许破坏性重构，不保留旧接口与历史索引格式。
> 本专题负责处理层契约；知识库身份、权限、模型预算、IndexEpoch 和任务发布规则沿用总规范。上游库的能力不等于本项目已经具备，也不保证采用后一定提升召回或降低耗时。
> 编码端点身份与免重建切换遵循[Query/Document 双路由专题](embedding-compatibility-and-query-document-routing-architecture.md)：处理计划记录真实编码实现，逻辑空间不以 Provider/URL 标识。

## 1. 可行性结论与功能边界

可行，且当前系统有可复用的分块算法、结构适配器、策略工厂、准入网关和遥测。关键不是新增一个“支持所有文件”的巨型适配器，而是把以下职责分别扩展：

| 层次 | 回答的问题 | 示例 |
| :--- | :--- | :--- |
| 来源连接器 | 内容来自哪里、如何同步 | 文件夹、单文件、网页、Git |
| 物理格式识别/解析 | 字节如何解码成有结构的文档 | TXT、Markdown、DOCX、PDF、XLSX、CSV |
| 内容画像/结构识别 | 这段内容遵循什么组织方式 | 小说章节、合同条款、技术说明、表格区域 |
| 分块策略 | 哪些语义单元一起检索、怎样满足 token 上限 | 章节/场景、条款/定义、表头/行组、父子块 |
| 图谱投影/抽取 | 哪些结构或事实成为带证据的节点和边 | 章节顺序、条款引用、人物提及、主体义务 |

“小说”“合同”不是物理文件类型，“表格”既可能是 XLSX，也可能是 DOCX/PDF 中的区域、CSV 或 TXT 中的内容结构。同一文档可以同时含合同条款与附件表格，不能强制整份文件只有一个内容类型。

首版建议原生 .NET 处理 TXT/Markdown/CSV，建立小说/合同/表格画像、管理页与可版本化路由。DOCX/XLSX/文本 PDF 逐项接入候选库；复杂扫描 PDF、OCR 和布局重建使用可选隔离 worker/service。任何仅声明在 manifest 中、依赖尚未就绪的格式不显示为“已支持”。

“动态添加”明确覆盖：
1. 运行中新增、发布识别规则、内容画像和切片配置，使用已安装的实现能力。
2. 运行中注册新的外部适配器服务，完成探测和验证后供新任务使用。
3. 后续安装新的受信任代码适配器包，在独立 worker 中启用；不向主服务任意加载 DLL 或执行脚本。

这三类入口统一展示在“解析与切片适配器”管理模块，但按能力和部署状态区分；新增 JSON 规则不能凭空获得 PDF OCR 或 Word 解码能力。

## 2. 当前项目可复用资产与真实欠缺

包声明核对范围为 [Directory.Packages.props](../Directory.Packages.props)、Infrastructure 与 Business 的 csproj；未执行 restore 或原生解析测试。

| 源码/依赖 | 已确认能力 | 复用方式及边界 |
| :--- | :--- | :--- |
| [MarkdownChunker](../Business/ReferenceRAG.Business/Features/Chunking/Services/MarkdownChunker.cs) 与 [IMarkdownChunker](../Infrastructure/ReferenceRAG.Infrastructure/Features/Chunking/Interfaces/IMarkdownChunker.cs) | Markdig、章节拆分、连续结构保护；接口是 string → List | 拆成 MarkdownParser 与结构切分算法；不能把任意二进制文件当 Markdown 读 |
| Markdig 1.3.2 | 已在 Infrastructure 引用，MarkdownChunker 使用 | 作为 Markdown AST 基础，保留来源映射；不承担 DOCX/PDF 解码 |
| [AdaptiveStrategyFactory](../Business/ReferenceRAG.Business/Features/Chunking/Strategies/AdaptiveStrategyFactory.cs) | Balanced、PrecisionDense、DeepNarrative、CodeFirst；默认回落 Balanced | 复用参数策略；未知策略在目标系统发布时校验失败，不能静默换策略 |
| [DeepNarrativeAdaptiveStrategy](../Business/ReferenceRAG.Business/Features/Chunking/Strategies/DeepNarrativeAdaptiveStrategy.cs) | 根据模型上下文调整 token 与 overlap | 不是小说章节、场景或人物识别器；源码注释中的节省比例不是经过本轮验证的收益 |
| [TableStructureAdapter](../Business/ReferenceRAG.Business/Features/Chunking/ContinuousElements/Adapters/TableStructureAdapter.cs) | 识别 Markdown 表头/分隔行 | 保留为 Markdown 表格转换器，不视为 Excel 或 PDF 表格解析器 |
| [TableContinuousChunkingStrategy](../Business/ReferenceRAG.Business/Features/Chunking/ContinuousElements/Strategies/TableContinuousChunkingStrategy.cs) | 表头继承、行组切片、数据行不重叠 | 算法可复用；现实现可扩大 effectiveMaxTokens、单超长行独立输出，新契约必须服从嵌入硬上限 |
| [连续结构工厂](../Business/ReferenceRAG.Business/Features/Chunking/ContinuousElements/Strategies/ContinuousChunkingStrategyFactory.cs) | Table/Code/List 策略，允许构造注入 | 复用注册思路；当前 Dictionary 和 DI 单例不是运行中持久化的插件管理 |
| [ChunkingExecutionStateMachine](../Business/ReferenceRAG.Business/Features/Chunking/StateMachine/ChunkingExecutionStateMachine.cs) | 阶段与耗时记录 | 可复用遥测；当前 TransitionTo 直接赋状态，不等于已具备合法迁移守卫或任务恢复 |
| [ChunkGatekeeperService](../Business/ReferenceRAG.Business/Features/Indexing/Services/ChunkGatekeeperService.cs) | 结构准入、吸附和噪声丢弃策略 | 按画像调整过滤；不能把金额 0、空值、短条款或章节标题当作低信息噪声统一丢弃 |
| [TextEnhancer](../Business/ReferenceRAG.Business/Features/Chunking/Services/TextEnhancer.cs) | 原文与标题、路径、标签增强分离 | 复用 Render/EmbeddingView 思路；生成摘要不能覆盖原文或成为无证据事实 |
| [FileIndexPipeline](../Business/ReferenceRAG.Business/Features/Indexing/Services/FileIndexPipeline.cs) | SafeReadFileAsync 使用 StreamReader.ReadToEndAsync，哈希、分块、嵌入 | 读取改为格式解析入口；当前是全量文本读取，不具备通用文件解析与有界大文档能力 |
| [ChunkRecord](../Infrastructure/ReferenceRAG.Infrastructure/Models/ChunkRecord.cs) | Content/EnhancedContent、行列、HeadingPath、类型 | 扩展成结构引用与多种 SourceAnchor；不能用伪造行号代替 PDF 坐标或工作表单元格 |
| [GraphIndexingService](../Business/ReferenceRAG.Business/Features/Graph/Services/GraphIndexingService.cs) | 文档/标题、标签、WikiLink 图谱 | 复用写入与 provenance 基础；不是小说人物关系或合同义务知识抽取 |
| Microsoft.ML.Tokenizers、Tokenizers.HuggingFace、BERTTokenizers、jieba.NET | 仓库已有声明；已有模型 token 计数接口 | 按实际 EmbeddingSpec 选择 tokenizer；分词器不等于文件解析器 |
| [索引流水线](../Business/ReferenceRAG.Business/Features/Indexing/Services/IndexingPipeline.cs)、[索引状态机](../Business/ReferenceRAG.Business/Features/Indexing/StateMachine/IndexJobStateMachine.cs) | 有界 Channel、处理阶段与事件 | 继续使用，新增处理快照、字节/token 配额、失败恢复与发布栅栏 |

本轮未在项目直接包引用/集中版本声明中发现 PdfPig、DocumentFormat.OpenXml、ExcelDataReader、CsvHelper、NPOI、Docling、Tika、Tesseract。它们是候选，不作为现有已安装能力。工作机上的文档解析技能也不等于项目运行时依赖。

[既有自适应分段文档](adaptive-chunking-architecture-and-embedding-optimization-design.md)可供算法背景参考；其中固定收益、最佳窗口比例和向后兼容设计不作为本专题验收要求。

## 3. 主流方案与库对比

以下是官方能力摘要及本项目判断，完整链接见第 12 节，不依据第三方跑分直接宣称本项目收益。

| 方案 | 官方可确认的做法 | 借鉴与限制 |
| :--- | :--- | :--- |
| Docling [R1][R2] | 统一文档模型；层次切块后按 tokenizer 分拆/合并，支持 metadata contextualization | 借鉴结构 IR 与 token 二次约束；可用隔离服务集成，不能让全项目依赖其专有 IR |
| Unstructured [R3][R4] | partition 先识别 Title/Table 等元素，再 chunk；by_title 与 TableChunk | 借鉴解析/切块分离与元素保护；其字符预算不能直接当模型 token 预算 |
| Apache Tika [R5] | 多格式内容检测、元数据/文本抽取 | 可选覆盖长尾格式；不是领域切片或高质量表格布局的保证，需 JVM/service 与输出映射 |
| Azure Document Intelligence Layout [R6] | 段落、表格单元格、跨行跨列与位置等布局输出 | 可选云解析；需保留原始结构而非只取 Markdown，并受出站权限/配额约束；不能假定所有格式有相同表格分析能力 |
| LlamaIndex Node Parsers [R7] | 标题/表格/层次等多种 node parser，可使用父子节点结构 | 借鉴父子块与按内容选择；不为复用 Python API 重写现有 .NET 系统 |
| Microsoft GraphRAG [R8] | 文档 → TextUnits → 实体/关系 → 图增强/摘要等分离阶段 | 借鉴图谱抽取与 chunk 独立、证据可回溯；不为首版引入社区报告和全套图算法 |

### 3.1 .NET 原生候选与部署成本

| 候选 | 适合解决的问题 | 局限与接入决定 |
| :--- | :--- | :--- |
| PdfPig [R9] | 文本型 PDF 的文字、位置与布局分析 | 需读序重建；不能直接信任 page.Text 顺序；扫描件仍需 OCR。优先做文本 PDF spike |
| DocumentFormat.OpenXml [R10] | DOCX 段落/表格/样式/编号与 OOXML 内容 | 不是排版渲染引擎，无可靠分页；旧 DOC 不是 DOCX。合同必须额外解析编号、修订与表格结构 |
| ExcelDataReader [R11] | XLS/XLSX 等工作表读取、行级遍历 | 优先 reader，避免 AsDataSet 全量装载；公式/显示格式/合并单元格的可获得程度按锁定版本验证 |
| CsvHelper [R12] | CSV 引号、换行、分隔符、文化/类型规则 | 不用 Split(',')；不能仅据 .txt 扩展名排除 CSV 内容。逐行读取，限制列/单元格大小 |
| NPOI [R13] | Office 格式读取，作为 Excel/Word 方案的替代候选 | 不与多个功能重叠库同时默认引入；先比较复杂样例、内存及许可/发布要求 |
| Tesseract [R14] | 可选本地 OCR 引擎 | 需要图像渲染、语言数据及坐标转换，不解决所有表格/阅读顺序；在 worker 中运行并限制资源 |
| Docling/Unstructured/Tika service | 复杂格式、布局与长尾覆盖 | 可动态注册服务适配器；需要部署、健康检查、版本锁定、输出验证与错误归一化 |

建议路线：先复用 Markdig/已有切块能力，补 TXT/CSV；再按实际样本选 OpenXml、ExcelDataReader、PdfPig；复杂布局选择一个服务后端验证，不同时默认部署所有转换框架。格式库、OCR 模型、字体/词典和原生依赖许可证分别核对，发布时固定版本；本轮不安装和升级。

## 4. 总体处理管道与设计模式

~~~mermaid
flowchart LR
    Source["Connector: 原始字节与来源身份"] --> Sniff["FormatDetector: 物理格式/编码"]
    Sniff --> Parse["DocumentParser: 结构 IR"]
    Parse --> Detect["ContentProfiler: 文档与区域画像"]
    Detect --> Plan["ProcessingPlanner: 固定修订与预算"]
    Plan --> Chunk["ChunkStrategy: 结构切片"]
    Chunk --> Views["内容视图/Token校验"]
    Plan --> Graph["GraphProjector: 结构/事实"]
    Views --> Validate["来源映射与完整性检查"]
    Graph --> Validate
    Validate --> Stage["暂存与文档版本发布"]
    Stage --> Index["全文/向量/图谱读取"]
~~~

内容画像可以在少量原始文本上提供初选提示，但最终基于解析 IR 与整篇结构验证。表格、条款等区域可覆盖文档默认画像。工作流固定为上述阶段，不建设任意脚本 DAG 编辑器。

| 关注点 | 模式 | 实际作用与限制 |
| :--- | :--- | :--- |
| 不同文件/布局引擎 | Adapter + Factory | Parser 输出共同 IR；业务不引用 PdfPig/OpenXml 类型 |
| 内容画像与块级选择 | Strategy + 有序规则 | 可解释选择小说/条款/表格策略，不只调整 token 大小 |
| 多格式候选识别 | 有界探测链 | 返回候选与证据，集中解决冲突；不执行“第一个 true 永远赢”的隐藏路由 |
| 不同结构元素 | Composite | Document/Section/Table 等树与块流，不把表格强行转无结构文本 |
| 图谱投影 | Strategy | 结构图与领域事实独立版本化，共用 provenance |
| 配置执行 | Immutable Plan + Registry Snapshot | 在途任务固定 adapter/profile 版本，发布不影响运行中任务 |
| 控制生命周期 | State Machine | 发布/排空/退役与处理作业分别管理；状态机不替代锁和事务 |
| 校验、预算、遥测 | Decorator | 所有扩展走统一配额与来源校验，不自行写库或加载模型 |

## 5. 核心契约与三类内容策略

### 5.1 格式识别与路由

FormatDetector 综合扩展名、可信传输 MIME、文件签名/容器内容、编码与有限文本抽样。DOCX/XLSX 同为 ZIP 容器，要有限读取内部标识后区分；不能只认 ZIP 头。扩展名和内容矛盾返回诊断，不自动将二进制降成 TXT。

识别结果包含候选 Mime/Parser、证据、支持程度、采样位置与 ambiguous 标记；启发式分数不是概率。首版禁用默认 LLM 分类；需要时作为可选受预算分类器，保存模型/提示版本和出站策略。

确定性优先级：
1. 文档显式覆盖；
2. 连接器范围规则；
3. 知识库 ProcessingProfile 路由；
4. 系统默认。
同层规则使用显式 priority 与稳定 ID；相同优先级且同时命中的互斥规则在发布/执行时返回冲突。强制 Parser 仍须兼容实际字节与 IR 能力；强制内容画像可应用于兼容 IR，但显示警告与匹配证据。

低确定性默认进入通用结构画像并标 needsReview；若知识库采用 StrictClassification，则暂停文档等待处理。仅为管理者提供人工修正，不把所有疑似文件都阻塞全库。

### 5.2 统一 IR、来源锚点与内容视图

| 契约 | 必要信息 |
| :--- | :--- |
| SourceEnvelope | kbId、DocumentId、ConnectorId、SourceKey、SourceRevision、原始字节哈希、文件名、实际媒体类型与可重读受限内容句柄 |
| ParsedArtifact | ArtifactId、IRSchemaVersion、ParserId/Revision、输入哈希、完整性状态、规范文本哈希、块树与块序 |
| DocumentBlock | BlockId、ParentId、Kind、Order、TextRef、SourceAnchors、语言与可选结构数据；Kind 包含 Paragraph/Heading/List/Clause/Table/Cell 等 |
| SourceAnchor | 文本原始字节/行列、PDF 页与坐标、DOCX part/段落/运行位置、工作表/单元格范围；注明坐标系与精度，允许一块多锚点 |
| ContentProfileResult | 文档标签 + 区域画像、候选证据、规则版本、人工覆盖与不确定性 |
| ProcessingPlan | Parser/Profiler/Chunker/Renderer/GraphProjector 语义修订、逻辑嵌入角色契约/tokenizer 语义、预算/允许实现约束；物理端点在每次 EmbeddingAttemptEnvelope 中记录，不因兼容端点替代改变内容计划 |
| ChunkArtifact | EvidenceText、EmbeddingText、RetrievalText、BlockRefs/精确源区间、ParentChunkRef、Continuation/Window 信息、实际 token 计数与诊断 |
| GraphAssertion | Subject/Predicate/Object、类型与限定信息、EvidenceRefs、提取器/规则修订、来源类型与置信信息、文档/代次 |

EvidenceText 是抽取后可追溯到来源的正文视图，不等同于原始字节；EmbeddingText 可以加章节/列头/定义上下文；RetrievalText 用于全文与展示。生成摘要标 synthetic 并保留支撑来源，不能冒充直接引文。OCR 文本带识别器版本与不确定性，不能声称与图像逐字一致。

IR 保留稳定块锚点、读取顺序、表头/数据区/脚注/合并单元格、可选跨页映射。不会用一个超大 JSON 数组一次性装载整篇；块树索引与文本可暂存到磁盘，按区域/页/行组读取。Markdown 仅是展示/导出视图，不能作为所有格式唯一中间表示。

从清洗文本回到原文需要变换映射；去页眉、连字恢复、空白合并不得修改“原始位置”而不记录。不能定位时标 Approximate/Unavailable，UI 不展示虚构页码或字符区间。

~~~csharp
// 目标契约示意；值对象、持久化实现与 API 版本在实施阶段定义。
public interface IFormatDetector
{
    ValueTask<FormatProbeResult> ProbeAsync(ProbeInput input, CancellationToken ct);
}
public interface IDocumentParser
{
    IAsyncEnumerable<ParseEvent> ParseAsync(
        SourceEnvelope source, ParseOptions options, CancellationToken ct);
}
public interface IContentProfiler
{
    ValueTask<ContentProfileResult> ClassifyAsync(
        ParsedArtifactHandle artifact, ProfileRules rules, CancellationToken ct);
}
public interface IChunkStrategy
{
    IAsyncEnumerable<ChunkArtifact> ChunkAsync(
        ParsedArtifactHandle artifact, ProcessingPlan plan, CancellationToken ct);
}
public interface IGraphProjector
{
    IAsyncEnumerable<GraphAssertion> ProjectAsync(
        ParsedArtifactHandle artifact, ChunkMapHandle chunks,
        GraphProjectionPlan plan, CancellationToken ct);
}
~~~

ParseEvent 为 Header/Block/Diagnostic/Completed；Completed 包含清单与校验摘要。流结束但没有 Completed 是失败，不是空文档成功。外部 adapter 无权直接提供任意数据库路径或执行 SQL；宿主负责暂存、预算、版本与发布。

### 5.3 小说画像：章节、场景与检索粒度

- 识别“第 X 章/卷”、英文 Chapter、独立章节标题和场景分隔；规则可配置，避免将正文里的“第三章规定”误识别成章节。序列与层级也是证据，不只匹配单行正则。
- 按章节 → 场景 → 段落/对话组切块；对话发言标签与正文尽量保留，遇超长单元按句子和 token 再切分。
- 默认生成叶子检索块与父章节引用；不默认给整章和所有父层都生成向量，以免重复命中和索引膨胀。
- 章节/角色名单可以作为来源支持的上下文；人物实体抽取、别名归并与跨章关系是独立图谱功能，不能由“相邻出现”推导“朋友/敌人”。
- 图谱首版生成 Document/Chapter/Scene 的 contains、next、mentions；同名人物以作品/文档范围分开，跨文档合并显式配置。
- 长文策略扩大 token 可能稀释细节，章级问答和局部事件问答分别评估，不能把块数减少当作召回提升。

### 5.4 合同画像：条款、定义、附件与限定信息

- 保留原编号及完整条款路径，识别条/款/项、定义、附件和交叉引用。DOCX 编号需解析 numbering/style，而非仅依赖可见文本正则。
- 短条款可以独立为证据块；重要否定、例外、期限、金额、币种和条件不得被噪声过滤或摘要省略。
- 采用条款叶子块 + 上级条文/定义引用；超长条款可分段，每段携带条款路径和 continuation，检索后有界扩展定义与例外。
- 区分主合同/附件/修订版本，图谱引用保留文档版本；不能将同编号不同合同条款合为一个节点。
- 第一阶段结构图生成 Clause、DefinedTerm、Attachment 与 refers_to；Party/Obligation/Condition/Exception 等语义事实后续独立验证，附主体、时间、模态、否定及证据。
- DOCX 修订选择 final/original/含修订必须显式记录；扫描签章图像不自动推断签署主体或效力。
- 这里是信息检索与证据建模，不依靠自动抽取结果替代对原条款的阅读。

### 5.5 表格画像：物理格式与数据区域分离

- XLSX、CSV、Markdown/PDF 表格统一为 Table/Column/Row/Cell 结构；一张工作表可有多个区域，不假设首行就是唯一表头。
- 保存多行表头、列路径、单元格原值/显示文本/类型、单位、空值、合并范围、脚注和坐标；缺失信息标 Unknown，不猜测填满。
- 按行组切片并带表名、列头与单位；需要时按列组处理超宽行，重复行键并记录横向窗口覆盖，不能直接输出超长行等待截断。
- 区分重复表头与数据行：EmbeddingText 可重复列头，数据证据不重复；跨页表格只有经规则确认才拼接，保留原页映射。
- 公式不执行外部链接、宏或自动重算；使用已保存的计算结果时标明缓存状态，无法获得可信值则保留公式和警告。日期、百分比、货币及前导零按列/文化策略处理。
- 图谱默认只投影表/列/关键行实体和显式键关系，防止每个单元格两两连边。跨表 join 需明确键规则与范围，不用列名相同自动合并。
- 数值求和、筛选与范围比较不能只靠向量相似度。后续可增加有类型约束的 TableQueryAdapter，与语义召回互补；首版不允许模型直接生成任意 SQL。

## 6. 动态注册、发布与执行生命周期

### 6.1 三种动态扩展路径

| 级别 | 用户能动态增加什么 | 生效方式 | 不能解决的事情 |
| :--- | :--- | :--- | :--- |
| L0：声明式配置 | TXT 章节/条款识别、表头别名、分隔规则、块大小/保护/上下文配置 | 管理页保存草稿 → 校验 → 样本预览 → 发布不可变修订 → 绑定新任务 | 不执行用户 C#/Python/JS，不新增未知二进制解码器 |
| L1：服务适配器 | 自托管 Docling/Tika/OCR 或自研 parser/chunker/graph service | 注册受信任 endpoint → 协议/能力握手 → 样例验证 → 激活 | 不能在服务端能力缺失时仅靠声明支持某格式 |
| L2：代码包适配器 | 新的 .NET parser、检测器、切块或投影算法 | 管理者导入固定依赖包 → 独立 worker 校验 → 自检 → 新版本 worker 激活 → 旧 worker 排空 | 首版不支持主进程任意 DLL 热载入、在线编译脚本或未受控系统命令 |

L0 是首个交付版本的完整功能，不仅提供代码注册点。L1 接着交付；L2 在协议与资源隔离成熟后实现。主服务可不停机激活新 worker，但初始化/自检未完成前新能力不可选；不是“包复制完成即可调用”。

AssemblyLoadContext/AssemblyDependencyResolver 可在受信任 worker 内处理依赖冲突，不是安全沙箱；.NET 官方明确不支持安全地把不可信代码放入可信进程 [R15]。分进程提供故障/资源控制基础，若要接收不可信第三方代码，必须另做 OS 权限/容器隔离；首版只接受管理员审查的代码包。

### 6.2 注册表、能力与版本契约

AdapterDefinition 由稳定 AdapterId 标识；每个 AdapterRevision 不可变，包含：
- Kind：format-detector / parser / content-profiler / chunker / graph-projector / renderer。
- ImplementationRef：内置类型、服务部署修订或代码包哈希；配置 schema 与默认值。
- 接受/输出的 MediaTypes、IRSchemaVersion 范围、RequiredFeatures 与 ProducedFeatures。
- 语言/格式限制、流式/页范围/表格/坐标/OCR 能力、是否产生 synthetic 内容。
- 资源档位、最大输入/输出、deadline、需要的模型/凭据、是否访问网络。
- 依赖、许可证标识、平台架构与引擎/模型实际版本，允许的降级策略。

管理者填写的能力须通过验证样例/自检，不仅信任服务返回值。一个服务可能同时暴露多个 stage 的 AdapterRevision；只启用 parser 不自动启用其图谱抽取。

Registry 定义/发布记录保存在 Catalog，实际处理绑定按 kb.db 的 EffectiveConfigRevision 读取。作业在 Probing 前固定 registry/规则/处理绑定快照，先生成固定 parser/profiler 阶段计划；Profiling 后在此快照内封存语义 ProcessingPlan。嵌入执行端点另按批次路由/准入取得并记录 AttemptEnvelope，必须满足计划的角色输入与预算；换兼容端点不改 PlanHash，改变语义能力需新计划。现有 DI 容器不动态增删服务，稳定工厂与 worker/client 池管理运行对象。

服务协议建议使用宿主定义的 HTTP JSON 控制面与有界 NDJSON 事件流，首版仅一种协议：
- capabilities/health 返回 protocolVersion、implementationDigest、IR schema/features 与操作列表。
- parse 接受限量原始内容上传或一次性授权的 ArtifactHandle；不可传任意本地路径给服务读取。
- profile/chunk/project 接受已授权的 IR/ChunkMap 内容流或只读短期句柄；返回 stage 对应 schema，服务不可直接写本地库。
- 每次调用携带 JobId、AttemptId、PlanHash、输入摘要、deadline 和幂等键；有 sequence、诊断与最终 Completed 校验。
- 同一固定修订若返回不同实现摘要，宿主拒绝执行并要求注册新修订，不让远端热更新绕过重建判断。
- 取消使用关联调用 ID，断流后的结果不发布；重试受预算及阶段幂等规则限制，不重放已发布文档版本。
- 服务地址由管理者预注册，传输遵守 local_only/出站策略，认证使用 CredentialRef，不允许普通请求任意指定 URL。

### 6.3 两类状态机

发布状态：
~~~mermaid
stateDiagram-v2
    [*] --> Draft
    Draft --> Validating
    Validating --> Validated
    Validating --> ValidationFailed
    ValidationFailed --> Draft
    Validated --> Active
    Active --> Draining
    Draining --> Retired
    Draining --> DrainFailed
    DrainFailed --> Draining
    Retired --> [*]
~~~

修订内容不可编辑；草稿修改生成新的候选哈希，验证只对该哈希有效。激活通过 Catalog ExpectedRevision 条件更新，随后替换运行快照。Health 单独作为 Healthy/Degraded/Unavailable，不把临时断网自动解释成配置退役。

普通禁用先列出画像、路由、知识库、待执行/在途任务依赖，有引用则要求改绑定。强制禁用阻止新任务，记录 AdapterUnavailable 并取消/排空，已有索引只读查询不因解析器退役立即消失；需要重新处理时必须重新选择可用实现。保留处理证据元数据，不强制永久保存所有历史插件二进制。

处理作业的 JobStatus 使用统一枚举 Queued/Running/WaitingForInput/Cancelling/Succeeded/PartiallySucceeded/Failed/Cancelled；处理阶段是 Running 下的 OperationPhase：Probing → Parsing → Profiling → Planning → Chunking → Rendering → Embedding/Projecting → Validating → Publishing。按 RequiredOutputs 选择或跳过阶段并记录原因；Completed 只是外部阶段流结束事件。ClassificationReview 使用 WaitingForInput + WaitReason，释放租约；重启 Interrupted 只记录在 Attempt，再按发布事实恢复 Job。

Chunking 与 GraphProjection 的顺序在有证据引用时固定：先得到可校验 ChunkMap，再投影图；并行实现必须能在 publish 前解析所有证据映射。不以状态机日志代替 durable checkpoint；实际发布事实与状态按总规范对账。

回退必须分类：
- UnknownFormat、EncryptedDocument、CorruptContainer 不能当 TXT “尽量读”。
- UnsupportedStructure 可在配置允许且来源映射完整时回退通用文本块，标明 lostFeatures。
- Parser/OCR 超时、worker 崩溃是失败或待重试，不悄悄输出空文档覆盖旧文档。
- 图谱可选阶段失败时，文本/向量可发布，Graph 能力为 Pending，JobStatus=PartiallySucceeded，并记录持久化补偿任务或人工重试动作；严格画像缺必需图则不发布该文档。任何情况下不保留指向失效 chunks 的旧关系，不把“空图”冒充阶段成功。

## 7. 管理模块、API 与交互流程

新增“解析与切片适配器”导航入口，不与模型下载页混为一个列表，内部提供：
| 页面 | 主要内容与操作 |
| :--- | :--- |
| 适配器目录 | 按 stage、物理格式、内容画像、部署位置、能力、健康/发布状态筛选；添加配置、注册服务，后续导入包 |
| 内容画像与路由 | 小说/合同/表格/通用模板，规则优先级、匹配证据、段级覆盖和回退设置 |
| 处理配置 | parser → profile → chunker → renderer → graph 的固定版本组合；token、出处、预算与出站限制校验 |
| 预览与评测 | 原文件/结构树/切片/表格/来源锚点/图谱证据并列检查，前后配置对比 |
| 发布与依赖 | 草稿、验证报告、激活范围、受影响文档、重处理任务、禁用排空和错误 |

### 7.1 用户操作示例

添加“中文小说 TXT”：
1. 从通用文本模板复制，选择已安装 TextParser。
2. 新增章节识别规则、章节/场景策略和标题上下文，不需要部署新代码。
3. 样本预览展示误命中行、边界、token、原文覆盖与章节层级。
4. 发布画像修订并绑定指定知识库/连接器；确认是否仅用于新内容或创建重处理任务。
5. 任务详情显示 actual profile revision，旧文档未处理前显示 ProcessingPending/MixedProcessingRevisions。

添加“合同 DOCX”：
1. 先确认 OpenXmlParser 已安装且支持编号/表格/选定修订视图。
2. 使用合同画像，配置条款/附件、定义引用及字段保留规则。
3. 在预览中验证“除非/但/不”、金额、期限没有被切掉，目录页不被当作正文条款。
4. 若解析能力缺失，页面提供依赖说明，不让用户用切片配置弥补丢失的正文或编号。

添加“新的专有文件格式”：
1. 注册实现该格式的服务或安装受信任代码包。
2. 能力握手、自检与格式/损坏样本测试通过后发布。
3. 再组合内容画像、切片和图谱策略；该流程证明动态新增实现，而不仅是勾选扩展名。

### 7.2 预览、发布与试运行

预览默认不入正式索引，创建独立 PreviewJob，使用相同解析/调度/出站校验。样本需明确获准读取；预览产物受 kbId 权限保护并设置 TTL。大文档采用标记清楚的样本范围，页面不得将样本通过显示为全文验证通过。

预览必须展示：选择路径与匹配证据、Parser/Plan 版本、文档/区域画像、来源坐标、丢失结构、解析/切块警告、token 分布、覆盖缺口、重复上下文、预计向量数与图实体数。用户能覆盖识别结果并重跑；不能只看“切出了多少块”。

发布与绑定分开：发布使实现/配置可用，不自动修改已有知识库；绑定保存期望修订并可创建 Reparse/Rechunk/ReprojectGraph 任务。只修改显示名不重建；实际影响数据的更改列出影响范围与停顿能力。灰度以指定库/连接器/文档范围执行，不引入无限配置继承。

禁用/升级不自动回滚索引。可把默认绑定指回另一个已验证配置，但已有数据必须按实际处理版本显示并按需重处理；路由版本切换不等于旧输出已恢复。

### 7.3 目标 API

| API | 核心语义 |
| :--- | :--- |
| GET /api/processing/adapters | 目录、能力、部署、健康；只返回调用者可见信息 |
| POST /api/processing/adapters | 注册内置能力配置或受信任服务；稳定 AdapterId |
| POST /api/processing/adapters/{id}/revisions | 新不可变修订草稿、schema、implementationRef |
| POST /api/processing/revisions/{id}/validate | 有界异步验证，返回 202 + jobId |
| POST /api/processing/revisions/{id}/activate | ExpectedRevision、验证报告哈希与依赖检查 |
| POST /api/processing/adapters/{id}/operations | disable/retire，force、依赖预览和排空结果 |
| POST /api/processing/profiles | 画像/规则/策略组合，独立版本 |
| POST /api/processing/previews | 文档/样本/候选计划，返回 PreviewJob |
| POST /api/knowledge-bases/{id}/processing-binding | 期望画像/路由修订、生效范围与重处理选项 |
| POST /api/knowledge-bases/{id}/jobs | 新增 reparse/refreshVectors/refreshRetrievalView/reprojectGraph，与整库 rechunk/reembed 明确区分 |

API 是目标契约，不要求保留当前 Settings 字段。管理、预览源文档、发布/安装分别鉴权；管理适配器不代表有权查看所有文档。

配置接口使用结构化 schema，不暴露“任意脚本”和 shell 命令框。首版规则编辑采用有限谓词、行模式、字段映射、层级锚点和已知策略选项；受控正则需超时与输入限制，NonBacktracking 不是对恶意规则的安全边界 [R16]。

## 8. 指纹、索引版本与总体架构衔接

### 8.1 从单一切块配置改为处理配置

知识库不再假定所有文档用一套 ChunkingSpec。KnowledgeBase 绑定 ProcessingProfileRevision，其中包含文档/区域路由与默认策略；每份文档记录解析后真正执行的 ProcessingPlanHash。EmbeddingSpec 仍为知识库唯一向量空间，不能让某个 adapter 悄悄换嵌入模型。

ProcessingFingerprint 至少覆盖：原始字节哈希与来源修订、Parser 实现/模型/配置/IR 版本、清洗与映射、画像规则、块级路由、Chunker/Renderer 参数、tokenizer 与嵌入预算、GraphProjector/schema/提示版本。展示名、健康状态与密钥值不进入内容指纹。

逻辑语义指纹与执行溯源分开：ProcessingPlan 固定语义配方、tokenizer 语义与预算；每次向量执行在 EmbeddingAttemptEnvelope 记录实际 Endpoint/EncodingImplementationRevision、路由/角色/准入/cohort。兼容端点替换不使 Parser/Chunker 缓存失效、不重算旧向量；新向量缓存按实际实现隔离。最终检索结果还必须绑定每库 ReadRevision，避免局部刷新后命中旧结果。

为了避免每次更改都全量重做，按阶段分别缓存：
| 变化 | 必须失效/执行 | 可复用 |
| :--- | :--- | :--- |
| Parser/OCR/解码/读序/清洗改变 | Reparse，再执行受影响下游 | 原始受控快照 |
| 内容画像/边界规则/Chunker 改变 | Rechunk 与相关向量/图证据更新 | 已验证 ParsedArtifact |
| 仅 EmbeddingText renderer 改变 | 同一嵌入空间下 RefreshVectors 刷新受影响块 | EvidenceText、原 IR、FTS 与未改变的结构图 |
| 仅 RetrievalText/展示渲染改变 | RefreshRetrievalView 更新派生展示/FTS；若仅 UI 样式则不重建 | 未改变的证据、向量和图；三种视图分别记录哈希 |
| EvidenceText/来源映射改变 | 按影响选择 Reparse/Rechunk，重新验证全部证据与关联图 | 仅可证明不受影响的上游产物，不能按纯展示修改处理 |
| GraphProjector/schema 改变 | ReprojectGraph | 原 IR/chunks/vector；改变引用粒度时重新验证 ChunkMap |
| 逻辑嵌入模型/角色配方/tokenizer 语义/预算变化 | 检查是否需要 Rechunk，再按空间变化执行 Reembed | 解析产物，符合预算时可保留 chunks |
| 仅更换已通过准入的 Query/Document 执行端点 | 不自动重解析/重切片/重编码；未来执行记录真实实现与 cohort | 现有 IR、chunks、向量、结构图；按兼容专题治理路由/缓存/准入 |
| 仅路由规则改变 | 对匹配结果/计划发生变化的文档重处理 | 未受影响文档与缓存 |
| 仅目录显示名改变 | 不重处理 | 全部处理产物 |

Reparse 需要原始输入；Rechunk 需要对应 ParsedArtifact；只有纯文本缓存时无法重新恢复丢失的页/单元格结构。默认保存规范 IR 与 provenance，原始快照按库策略保留并受配额/权限/清理治理；快照不存在且来源不可访问时返回 SourceUnavailable，不伪造可重解析能力。

### 8.2 代次、局部更新与发布

IndexEpoch 仍是知识库写入栅栏；ProcessingBinding 切换时推进 epoch，旧计划不能以新绑定身份提交。普通文档局部重处理使用 DocumentVersion/ArtifactRevision 条件更新，Chunk 引用包含 kbId、ContentGeneration、DocumentId、DocumentVersion、chunkId。

ContentGeneration 用于整库 Rechunk/Reingest 的全局失效；局部重处理发布新的 DocumentVersion，旧文档引用失效。图的输出版本使用 GraphArtifactRevision，投影规则定义另用 GraphProjectionSpecRevision；只改图输出不使直接正文引用失效。所有正式发布/删除/失效事务同时推进 ReadRevision，活动 cohort/引用数随向量实际可见性同事务维护。

向量仍须关联本库 EmbeddingSpecHash；处理版本混合但嵌入空间一致时允许按每文档实际 manifest 检索，并显示 mixedProcessingRevisions。不能把“旧画像输出”误报成“向量空间不一致”；反之也不能靠处理版本标签掩盖模型不匹配。

明确区分：Reembed 指整库切换/重建嵌入空间，推进库级 VectorGeneration 并失效该库旧向量；RefreshVectors 指同一 EmbeddingSpecHash 下刷新指定文档/块，仅推进该文档 VectorArtifactRevision，不推进库级 VectorGeneration，不清空未受影响向量。上下文渲染只改变模型输入内容、不改变全库 query/document 指令与模型配置时才适用 RefreshVectors；后者改变即需新 EmbeddingSpec 和整库 Reembed。向量读取同时检查库级空间与每文档活动向量修订，发布指针原子切换。

RefreshRetrievalView 使用独立 RetrievalViewRevision 事务更新全文索引/派生展示；不修改 EvidenceText 或 ChunkMap，也不使原文引用失效。若 renderer 同时改变多个视图，则按各视图哈希并集执行刷新；管理预览列出实际影响，不能把所有渲染变化都归为重算向量。

大文档采用 staging 表/受控产物逐批写入，最后以短事务发布 DocumentVersion/ArtifactRevision 指针和可查询标志；查询只读已发布版本。后台阶段未完成时不让部分块、旧图边和新向量混用。此机制是单文档原子发布，不要求保留历史索引格式或整库双版本在线迁移。

Catalog 记录作业/Attempt/期望配置，kb.db 保存 EffectiveConfigRevision、manifest、发布指针、ReadRevision、实际 cohort 与 checkpoint；事件在事实提交后发送。崩溃先核对 LastAppliedOperation/JobId/AttemptId/PlanHash/IndexEpoch 与正式发布清单，再恢复或终结，不能仅因 Catalog 状态滞后重复发布。GC 仅回收无正式/读快照/恢复任务引用且过保留期的产物。

## 9. 召回质量、性能与失败风险

### 9.1 质量收益的前提

向量侧：识别结构、避免列头和数据分离、保留条款例外、提供章节上下文，有机会提高证据命中；但过大块、过多前缀、错误分类、OCR 误字和重复父子向量也会降低质量。

图谱侧：结构节点可改善导航，语义实体/关系必须另行抽取与消歧。切片更好不等于自动获得真实知识图；错误关系可能放大召回噪声。图抽取采用 profile-specific schema，强制 EvidenceRefs，限制节点/边数量；模型输出未知关系类型或缺证据时拒绝或隔离。

父子检索采用“叶子召回 → 分数合并 → 有界父级/邻居扩展”，父文本无向量也可按引用取回。扩展需相同授权、文档版本和上下文预算；防止图谱跨文档/跨库绕过权限。生成摘要作为单独检索视图时标 synthetic，回答引用仍回原证据。

### 9.2 有界处理与安全

- 先有限格式探测，再执行一个明确计划；不对每个文件尝试全部 parser 或运行全部画像模型。
- 抽样可以头/中/尾组合，整篇结构切分仍检查后续区域；对非 seek 流先用受控 spool，不无限缓存。
- 建议起点：探测不超过 256 KiB，预览默认最多 10 页/2 MiB 文本，parser worker 默认并发 1，单任务输出事件缓冲 8 MiB；这些是可调初值，不能证明整文件峰值内存。
- 额外配置原始大小、解压字节/条目、像素/页数、表格行列/单元格、IR块数、总输出与临时盘配额；超限返回 ResourceLimitExceeded，避免 ZIP 解压、超大共享字符串和 OCR 像素耗尽资源。
- 只读解析，不执行宏、文档链接、外部实体或嵌入脚本。来源内容不得改变系统提示或工具权限。worker 崩溃隔离，取消无法中断原生调用时终止该专用 worker 并回收暂存。
- worker 是资源隔离单元，OS 权限才是安全边界；不声称仅分进程即能运行任意不可信插件。
- OCR/布局模型/语义分类和图抽取使用总规范模型路由与预算。外部本地服务的常驻模型需声明设备资源并纳入预算/资源组，无法探测时按保守独占预留，不能成为调度旁路。
- 有界 token 硬校验在 EmbeddingText 加入表头、标题、定义之后执行。超长表头/行/条款通过列组/子句分片处理；不能动态扩大模型硬上限或静默删尾。
- Preview、缓存和 raw spool 同样受 local_only、凭据与文档读取权限约束；跨库缓存不共享私有产物。
- 同一文档先固定输入快照或校验流读取前后版本/哈希；源正在修改时重试并留诊断，不把拼接了两个版本的流当成稳定正文。

### 9.3 评测设计

每类内容准备带标注的样例与查询，按文档/作品/合同族划分开发与测试集，避免同源切片泄漏。用户私有文本不用于公开评测或传外部服务，除非已有明确出站配置。

| 层面 | 指标与测试方式 |
| :--- | :--- |
| 格式解析 | 字符/正文覆盖、读序、表格行列/合并准确性、来源锚点误差、OCR CER/WER、失败/部分输出率 |
| 画像识别 | Novel/Contract/Table/Generic 分类精确率/召回率、歧义率、人工覆盖率；测试同扩展名不同内容 |
| 切片 | 章节/条款边界 F1、表头继承率、证据覆盖、超 token 比例、重复 token/索引膨胀、父子映射正确性 |
| 向量召回 | Evidence Recall@K、MRR/nDCG、局部事实与全章问题分组、Rerank 后准确性 |
| 图谱召回 | 证据支持率、实体/关系精确率与召回率、否定/条件/时间保留、错误合并率、图扩展后的证据命中 |
| 系统性能 | 页/秒、MiB/秒、p95 处理耗时、峰值 RAM/临时盘/显存、向量数、模型调用/token/费用 |
| 动态管理 | 发布原子性、依赖禁用、在途版本固定、worker 替换、断流/取消/恢复与协议不匹配 |

先做消融：现有 Markdown/通用切片基线 → 新 parser/IR → 内容画像结构切片 → 上下文视图 → 图抽取/父子扩展；固定相同 EmbeddingSpec、检索预算和 reranker。不能同时换模型和切片后把全部提升归因于适配器。

每类数据设置准入标准并在首次基线后固定；本轮不预设“召回提升 X%”。硬约束是无未声明内容丢失、无未经处理的 token 超限、无越权、无错误证据引用；质量与吞吐目标依据实际语料和硬件确定。

## 10. 工作包归属与唯一实施顺序

执行顺序与退出条件唯一采用[统一计划 G0–G9](unified-architecture-contracts-and-implementation-plan.md)。以下 A 编号仅保留专题范围，不再是另一套排期：

| 工作包 | 统一交付门与边界 |
| :--- | :--- |
| A0：样本/IR/锚点/token 技术验证 | G0；不把 spike 当正式产物 |
| A1：文本/内容画像与 L0 管理 | G1 先交付通用文本基础，G3 交付完整画像/预览/发布/绑定与局部刷新；G3 依赖 G2 的模型/预算基础 |
| A2：Office/文本 PDF | G6，逐库逐样本验收，不同时强制安装全部候选库 |
| A3：服务/OCR | G6，L1 协议、真实服务、能力管理、资源/断流/取消一并交付 |
| A4：领域图/父子与结构检索 | G5 有界扩展、G7 所选领域事实/质量集；TableQuery 不默认成为必需依赖 |
| A5：worker 代码包 | G8；只有此门通过才能宣传动态代码适配器 |

G3 当期必须有可操作的 L0 管理和预览，G6 当期必须有服务注册界面/API；不能统一推迟至全协议入口阶段。尚未实现的扩展不提供空壳可用按钮，所有 Job/状态/恢复按统一计划执行。

### 10.1 最小行为验收

1. 三个 .txt 分别含小说、合同、表格；同一文件中合同+附件表格按区域处理，扩展名不决定内容画像。
2. 含“第一章规定”的普通段落不误切；无章标题小说可回落通用结构并保留诊断。
3. 合同“但不包括/除非”与金额/期限保留，父条款/定义扩展有界且可定位。
4. XLSX 多区域、多行表头、跨页 PDF 表格与超长单行不会静默错列或截断。
5. 加密/损坏/扫描无文字文件分类明确；parser 流未 Completed 不覆盖正式文档。
6. 人工改画像后保存新版本，仅指定范围创建任务；在途任务固定旧计划。
7. 路由重复优先级、能力不匹配、IR schema 不支持、远端实现摘要漂移被拒绝。
8. 旧计划提交、旧文档版本提交和过期图证据都被栅栏/条件更新阻止。
9. 用户停用在用适配器能看到依赖，强制操作也不悬空已有索引读取。
10. 预览不入正式索引，过期产物可回收；原文、向量、图预览权限一致。
11. 外部服务超时/断流/worker 退出/磁盘配额耗尽可恢复，任务不伪成功。
12. 新插件的真实实现完整走到检索与引用；仅添加 manifest 不算支持新格式。

优先扩展现有 MarkdownChunkerTests、AdaptiveChunkingTests、ContinuousElementChunkingTests、IngestionNoiseFilteringTests、FileIndexPipelineTests 与 GraphStoreTests。新增契约与真实组件行为测试，不能只匹配源码字符串证明动态管理或召回提升。

## 11. 决策记录与待验证问题

确定采用：格式与内容画像分离；统一结构 IR；按文档与区域选择策略；独立图投影；在线配置/服务/worker 包三层扩展；不可变计划与按阶段指纹；有界预览和有证据的发布。

不采用：扩展名直接绑定领域类型、所有格式转纯文本后丢弃结构、默认每文档 LLM 分类、父子全部向量化、按语义相近自动推断关系、主进程任意插件热载入、自动无限 fallback、为“动态”重建通用脚本平台。

待 A0/A2 实测：具体库对中文/混排/复杂表格/扫描件的质量与资源；DOCX 编号/修订和 PDF 读序；实际 tokenizer 与增强前缀后的 token 上限；长文 IR 存储与有限发布；部署中的服务版本可固定程度。这里的 adapter/strategy/state machine 是控制职责与变化点的手段，本身不构成质量或性能收益证明。

## 12. 官方资料与核对范围

调研日期为 2026-10-31。只依据官方项目/文档能力选型，本文是设计判断；链接内容与具体依赖 API/许可证在实施锁定版本时再次核对。

- [R1 Docling Document](https://docling-project.github.io/docling/concepts/docling_document/)：共同文档表示与结构。
- [R2 Docling Chunking](https://docling-project.github.io/docling/concepts/chunking/)：层次/混合切块、tokenizer 与上下文视图。
- [R3 Unstructured Partitioning](https://docs.unstructured.io/open-source/core-functionality/partitioning)：格式识别与元素抽取。
- [R4 Unstructured Chunking](https://docs.unstructured.io/open-source/core-functionality/chunking)：分区后切片、标题与表格策略。
- [R5 Apache Tika](https://tika.apache.org/)：多格式检测与抽取。
- [R6 Azure Document Intelligence Layout](https://learn.microsoft.com/azure/ai-services/document-intelligence/prebuilt/layout?view=doc-intel-4.0.0)：布局、表格和来源坐标。
- [R7 LlamaIndex Node Parser Modules](https://developers.llamaindex.ai/python/framework/module_guides/loading/node_parsers/modules/)：可组合节点解析与层次切片。
- [R8 GraphRAG Indexing Dataflow](https://microsoft.github.io/graphrag/index/default_dataflow/)：TextUnit 与实体/关系抽取分层。
- [R9 PdfPig](https://github.com/UglyToad/PdfPig)：.NET PDF 文字与布局，阅读顺序注意事项。
- [R10 Open XML SDK](https://github.com/dotnet/Open-XML-SDK)：OOXML .NET 基础。
- [R11 ExcelDataReader](https://github.com/ExcelDataReader/ExcelDataReader)：工作表 reader 与全量 DataSet 的区别。
- [R12 CsvHelper](https://joshclose.github.io/CsvHelper/)：.NET CSV 读取。
- [R13 NPOI](https://github.com/nissl-lab/npoi)：Office 读取的替代候选。
- [R14 Tesseract](https://github.com/tesseract-ocr/tesseract)：OCR 引擎与部署入口。
- [R15 .NET Plugin Tutorial](https://learn.microsoft.com/en-us/dotnet/core/tutorials/creating-app-with-plugin-support)：插件依赖加载及可信进程边界。
- [R16 .NET Regex Best Practices](https://learn.microsoft.com/dotnet/standard/base-types/best-practices)：超时/NonBacktracking 与可信规则约束。
