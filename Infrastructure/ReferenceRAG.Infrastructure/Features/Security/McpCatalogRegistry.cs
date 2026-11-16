using System;
using System.Collections.Generic;
using System.Linq;

namespace ReferenceRAG.Core.Services;

/// <summary>
/// MCP 工具单项元数据（叶子节点）。
///
/// 除权限所需的 <paramref name="RiskLevel"/> / <paramref name="ModuleCode"/> 外，
/// 这里同时是**工具提示词（协议 description）的唯一事实源**：
/// <paramref name="Description"/> 是用途，<paramref name="WhenToUse"/> 说明何时该用，
/// <paramref name="UseInstead"/> 说明什么情况下改用哪个邻居工具，
/// <paramref name="Limits"/> 写清数量上限与截断行为。
/// SDK 侧通过 Metadata["DescriptionOverride"] 读取合成结果，避免与 [Description] 特性维护两份。
/// </summary>
public sealed record McpToolCatalogItem(
    string Name,
    string Description,
    string RiskLevel, // "Read", "Execute", "Dangerous"
    string ModuleCode, // 归属的业务模块代号，如 "search", "files", "sources", "index", "system"
    string? WhenToUse = null,
    string? UseInstead = null,
    string? Limits = null
)
{
    /// <summary>合成后的协议描述：用途 + 何时用 + 用邻居 + 上限。</summary>
    public string ComposePrompt()
    {
        var parts = new[] { Description, WhenToUse, UseInstead, Limits };
        return string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }
}

/// <summary>
/// 业务功能模块与 MCP 二级树状目录（分类节点，组合模式 Composite Pattern）
/// 统一管理业务领域模块、协议支持标记（API/MCP）及旗下工具集，支持前端统一联动授权与权限决断
/// </summary>
public sealed record McpCategoryCatalog(
    string Key,
    string Name,
    string Description,
    List<McpToolCatalogItem> Tools,
    string ModuleCode,
    bool SupportsApi = true,
    bool SupportsMcp = true
);

/// <summary>
/// 业务模块与 MCP 工具统一目录注册中心（单例事实来源 Single Source of Truth）
/// 对齐 WebToolForLinux 架构规范：
/// 1. 按业务领域功能模块划分父节点；
/// 2. 同时标明 REST API 与 MCP 协议支持属性；
/// 3. 所有 MCP 工具明确挂载于对应业务模块下，实现按模块统一授权与级联权限决断。
/// </summary>
public static class McpCatalogRegistry
{
    private static readonly List<McpCategoryCatalog> Categories = new()
    {
        new McpCategoryCatalog(
            "chat",
            "💬 对话与大模型问答 (Chat)",
            "大模型对话生成、流式会话交互与多轮上下文管理（REST: /v1/chat/completions, /api/MafChat）",
            new List<McpToolCatalogItem>(),
            ModuleCode: "chat",
            SupportsApi: true,
            SupportsMcp: false
        ),

        new McpCategoryCatalog(
            "search",
            "🔍 知识检索与语义嵌入 (Search)",
            "语义搜索、混合搜索、重排、图谱遍历与文本向量化（REST: /api/AIQuery, /api/Search, /v1/embeddings; MCP: 搜索工具）",
            new List<McpToolCatalogItem>
            {
                new("SemanticSearch", "在知识库中执行向量语义搜索，基于自然语言查找相关文档片段。",
                    "Read", "search",
                    WhenToUse: "适合问「是什么 / 为什么 / 怎么理解」这类概念性或描述性问题。",
                    UseInstead: "要精确匹配错误码、函数名、配置项请改用 lexical_search；既要语义又要精确命中改用 hybrid_search；已在明确知识库内检索用 knowledge_base_search。",
                    Limits: "topK 默认 5，上限 50（超出按 50 处理）；返回切片正文按 500 字截断。"),
                new("KnowledgeBaseDrillDown", "展开某个知识库切片的完整正文（按 chunkId，或按 documentId + 序号批量取）。",
                    "Read", "search",
                    WhenToUse: "检索结果正文被截断、需要看完整片段时使用。",
                    UseInstead: "只想确认某 chunkId 属于哪个文件用 get_document_info；想看整个文件用 read_note_content。",
                    Limits: "documentId 模式的 limit 默认 20。"),
                new("LexicalSearch", "在知识库中执行免向量词法搜索，按关键词字面匹配。",
                    "Read", "search",
                    WhenToUse: "要在知识库里查找精确词汇、错误码、API 名、函数名、配置键等字面命中时使用（返回知识库切片，不读源码文件）。",
                    UseInstead: "要到源码或文件正文里搜（按文件路径/目录范围）改用 search_file_content；自然语言问题用 semantic_search；两者都要用 hybrid_search。",
                    Limits: "topK 默认 5，上限 50；返回切片正文按 500 字截断。"),
                new("KnowledgeBaseSearch", "在指定知识库中执行跨库多路召回与 RRF 融合检索。",
                    "Read", "search",
                    WhenToUse: "已确认目标知识库 ID、要在指定库里搜索内容时使用（例如只在 Notes 库里搜）。",
                    UseInstead: "不指定库、要在全部已启用库中检索改用 semantic_search / hybrid_search / lexical_search。",
                    Limits: "topK 默认 5，上限 50（超出按 50 处理）；结果超 40 万字符直接拒绝并提示收窄。"),
                new("HybridSearch", "混合搜索：BM25 关键词精确匹配 + 向量语义召回，双路 RRF 融合。",
                    "Read", "search",
                    WhenToUse: "既要精确匹配又要语义理解时使用（问题里既有专有名词又有概念描述）；纯概念用 semantic_search，纯字面用 lexical_search。",
                    UseInstead: "纯概念问题用 semantic_search（更省一次词法开销）；纯字面匹配用 lexical_search。",
                    Limits: "topK 默认 5，上限 50；返回切片正文按 500 字截断。"),
                new("RerankResults", "对给定候选文档列表按与查询的相关性做深度重排并打分。",
                    "Read", "search",
                    WhenToUse: "已经有候选文档、需要二次精排（比如检索结果太多或排序可疑）时使用。",
                    UseInstead: "还没有候选时先用 semantic_search / hybrid_search 拿结果。",
                    Limits: "topK 默认 5；单次调用有 8000 字符级开销，候选过多时先裁剪。"),
                new("EmbedText", "将单段文本转换为向量表示，返回维度、范数与部分分量预览。",
                    "Read", "search",
                    WhenToUse: "需要文本向量本身（做相似度外的自定义计算或诊断嵌入模型）时使用。",
                    UseInstead: "只是比较两段文本相似度请用 calculate_similarity（内部一次完成，无需自己算）；批量转换用 embed_batch。",
                    Limits: "单段上限 8000 字符，超限返回 too_large。"),
                new("CalculateSimilarity", "计算两段文本的语义余弦相似度，返回 0–1 分数与文字解释。",
                    "Read", "search",
                    WhenToUse: "判断两段文本语义是否接近、做去重或匹配校验时使用。",
                    UseInstead: "需要向量本身用 embed_text；需要检索候选用 semantic_search。",
                    Limits: "两段文本各自上限 8000 字符。"),
                new("EmbedBatch", "批量将多段文本转换为向量表示。",
                    "Read", "search",
                    WhenToUse: "一次要处理多条文本、需要向量本身时使用。",
                    UseInstead: "只有一条请用 embed_text；只要相似度请用 calculate_similarity。",
                    Limits: "单条上限 8000 字符；批量条数建议不超过 32。"),
                new("GraphSearch", "从给定文件节点出发遍历 wiki-link 关系网，返回直接链接、反向链接与关联文档。",
                    "Read", "search",
                    WhenToUse: "已经知道文件路径，想沿链接关系发现相关笔记时使用。",
                    UseInstead: "只记得标题、不知道路径请先用 graph_search_by_title 找到节点再遍历。",
                    Limits: "depth 1–3，默认 1；depth 越大结果指数膨胀。"),
                new("GraphSearchByTitle", "在知识图谱中按标题关键词模糊检索节点。",
                    "Read", "search",
                    WhenToUse: "知道笔记标题片段、需要拿到节点 ID 或路径时使用。",
                    UseInstead: "已经有节点路径要做关系遍历请用 graph_search。",
                    Limits: "limit 默认 10。")
            },
            ModuleCode: "search",
            SupportsApi: true,
            SupportsMcp: true
        ),

        new McpCategoryCatalog(
            "files",
            "📄 文件系统与文本阅读 (Files)",
            "知识源物理文件浏览、流式行阅读、Markdown 大纲与 Agent 正则/文本检索（REST: /api/FileSystem; MCP: 文件探索工具）",
            new List<McpToolCatalogItem>
            {
                new("GetFileTree", "获取指定数据源或子目录下的文件与目录层级树。",
                    "Read", "files",
                    WhenToUse: "需要从头浏览某个数据源/目录的结构、定位笔记位置时使用。",
                    UseInstead: "已知文件名模式要找文件用 find_files_by_pattern；要按内容检索用 search_file_content。",
                    Limits: "单次返回当前目录一层（条目最多 500）；深目录请逐层传 subPath。"),
                new("ReadNoteContent", "流式按行范围读取指定笔记的正文。",
                    "Read", "files",
                    WhenToUse: "已知文件路径、要按行号范围读取正文（如第 10 到 20 行）时使用；大文件分段读可避免 Token 爆仓。",
                    UseInstead: "只要大纲标题用 get_note_outline；要按内容搜文件用 search_file_content；要拿知识库切片用 knowledge_base_drill_down。",
                    Limits: "endLine 默认 200，传 -1 或大值读至末尾，单次最大 2MB。"),
                new("GetNoteOutline", "解析 Markdown 大纲（H1–H6 标题树与行号）、Frontmatter 与分块索引状态。",
                    "Read", "files",
                    WhenToUse: "要看这篇文档的大纲、标题层级有哪些（H1–H6 与行号），或先了解章节结构再决定读哪一段时使用。",
                    UseInstead: "已经知道行范围要读正文请用 read_note_content。",
                    Limits: "单文件；返回标题树，不返回正文。"),
                new("SearchFileContent", "在知识库或源码工程中执行关键字/正则内容检索，返回带行号的匹配上下文。",
                    "Read", "files",
                    WhenToUse: "在文件正文里精确查找函数、变量、配置项、错误码、TODO 时使用（类似 ripgrep）。",
                    UseInstead: "只找文件路径不读内容用 find_files_by_pattern；要语义相似片段用 semantic_search。",
                    Limits: "maxMatches 默认 50、上限 500（超出按 500）；contextLines 0–5。"),
                new("FindFilesByPattern", "按通配符模式定位数据源中的文件路径（零内容读取）。",
                    "Read", "files",
                    WhenToUse: "知道文件名或扩展名模式（如 *.md、*architecture*.cs），只要路径列表时使用。",
                    UseInstead: "要在文件内容里搜关键词用 search_file_content；要看目录层级用 get_file_tree。",
                    Limits: "maxResults 默认 100、上限 500（超出按 500，响应回显生效值）。")
            },
            ModuleCode: "files",
            SupportsApi: true,
            SupportsMcp: true
        ),

        new McpCategoryCatalog(
            "sources",
            "📁 知识源配置管理 (Sources)",
            "知识源路径配置、启停开关、可用性探测与宏观统计信息（REST: /api/Sources; MCP: 数据源管理工具）",
            new List<McpToolCatalogItem>
            {
                new("ListSources", "列出所有已配置的知识库数据源及其索引统计（计数取自各库 kb.db 物理事实源）。",
                    "Read", "sources",
                    WhenToUse: "第一次接触这个服务时先调它：拿到有哪些数据源、每个源多少文件多少分块；所有文件类工具的 sourceName 都必须来自这里。需要知道有哪些源、各自规模时使用。",
                    UseInstead: "只要知识库清单（不要源路径）用 knowledge_base_list；要看某个源的详情用 get_source_info。",
                    Limits: "每个源回显文件数与分块数；scoped 时不属于目标库的源计数为 0。"),
                new("KnowledgeBaseList", "列出已注册的物理隔离知识库（ID、名称、配置代次、绑定的嵌入规格）。",
                    "Read", "sources",
                    WhenToUse: "需要确认系统里有几个知识库、各自叫什么，或需要知识库 ID 去限定检索范围时使用。",
                    UseInstead: "需要数据源路径或文件计数用 list_sources。",
                    Limits: "无参。"),
                new("GetSourceInfo", "获取指定数据源的路径状态、文档/分块计数、物理文件数与总大小。",
                    "Read", "sources",
                    WhenToUse: "需要确认某个源还在不在、有多大、索引到哪了时使用。",
                    UseInstead: "只想看所有源的概览用 list_sources；要看索引健康度用 get_index_status。",
                    Limits: "单源；sourceName 必须来自 list_sources。"),
                new("GetSystemStats", "获取整体系统统计：配置概览、Embedding 模型参数、搜索与重排配置、按库汇总的索引统计。",
                    "Read", "sources",
                    WhenToUse: "排查配置问题或确认当前用的是哪个模型/参数时使用。",
                    UseInstead: "只要逐库分块/向量数字用 get_index_status；只要服务版本用 get_service_info。",
                    Limits: "无参；结果含配置快照，可能较大。")
            },
            ModuleCode: "sources",
            SupportsApi: true,
            SupportsMcp: true
        ),

        new McpCategoryCatalog(
            "index",
            "⚡ 索引与分块维护 (Index)",
            "向量分块切片维护、BM25 倒排索引状态与增量/全量构建（REST: /api/Index, /api/BM25Index; MCP: 索引状态工具）",
            new List<McpToolCatalogItem>
            {
                new("GetIndexStatus", "获取当前索引状态，按物理知识库逐库统计分块数、文档数、向量数。",
                    "Read", "index",
                    WhenToUse: "确认索引是否建完，或检索结果为空、需要排查索引问题时使用。",
                    UseInstead: "要看具体文件清单用 list_files；要看系统配置用 get_system_stats。",
                    Limits: "无参；30 秒缓存，首调较慢。"),
                new("GetDocumentInfo", "按切片 ID 反查它属于哪个文件：返回内容片段、行号与所属文件路径。",
                    "Read", "index",
                    WhenToUse: "已知 chunkId、需要定位它来自哪个文件/哪几行时使用。",
                    UseInstead: "要看完整正文用 knowledge_base_drill_down；要列文件清单用 list_files。",
                    Limits: "必须同时传 knowledgeBaseId；正文按 500 字截断。"),
                new("ListFiles", "列出知识库中已索引的文件（文档 ID、路径、切片数），支持 connectorId 精确过滤或 pathKeyword 路径子串过滤（可叠加）。",
                    "Read", "index",
                    WhenToUse: "需要列出某个知识库或连接器下有哪些文件（文件名、切片数）时使用；要的是知识库清单本身请用 knowledge_base_list。",
                    UseInstead: "要看知识库清单本身（ID/名称）用 knowledge_base_list；要看物理目录树用 get_file_tree（读磁盘）；要按内容检索用 search_file_content。",
                    Limits: "limit 默认 20；未知 connectorId 直接报 connector_not_found；响应回显 appliedFilters/totalMatched/truncated。")
            },
            ModuleCode: "index",
            SupportsApi: true,
            SupportsMcp: true
        ),

        new McpCategoryCatalog(
            "models",
            "🧠 模型调度与管理 (Models)",
            "本地与云端模型运行时状态、显存驻留、探针基准测试与动态热重载（REST: /api/Models, /api/Settings）",
            new List<McpToolCatalogItem>(),
            ModuleCode: "models",
            SupportsApi: true,
            SupportsMcp: false
        ),

        new McpCategoryCatalog(
            "system",
            "🛠 系统监控与运维 (System)",
            "系统健康检查、硬件加速感知、操作日志与链路诊断（REST: /api/System, /api/Logs; MCP: 连通性测试）",
            new List<McpToolCatalogItem>
            {
                new("Ping", "测试 MCP 通道双向连通性，返回 pong。",
                    "Read", "system",
                    WhenToUse: "只是确认服务是否活着、通道是否通时使用（最快的一次往返）。",
                    UseInstead: "要看版本与服务信息用 get_service_info；要看模型与配置用 get_system_stats。",
                    Limits: "无参，无外部依赖。"),
                new("GetServiceInfo", "获取 ReferenceRAG MCP 服务的名称、版本、传输方式与端点。",
                    "Read", "system",
                    WhenToUse: "需要确认服务版本或接入端点时使用。",
                    UseInstead: "只是探活请用 ping（更轻）；要看模型配置用 get_system_stats。",
                    Limits: "无参。")
            },
            ModuleCode: "system",
            SupportsApi: true,
            SupportsMcp: true
        )
    };

    private static readonly Dictionary<string, string> ToolToModuleMap = new(StringComparer.OrdinalIgnoreCase);

    static McpCatalogRegistry()
    {
        foreach (var category in Categories)
        {
            foreach (var tool in category.Tools)
            {
                ToolToModuleMap[tool.Name] = tool.ModuleCode;
            }
        }
    }

    /// <summary>
    /// 获取全量分类与工具元数据清单
    /// </summary>
    public static IReadOnlyList<McpCategoryCatalog> GetCatalog() => Categories;

    private static readonly Dictionary<string, McpToolCatalogItem> ToolItemMap =
        Categories.SelectMany(c => c.Tools).ToDictionary(t => Key(t.Name), StringComparer.Ordinal);

    /// <summary>归一化工具名（目录用 PascalCase，协议用 snake_case，比较前必须抹平）。</summary>
    private static string Key(string value) =>
        string.Concat(value.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    /// <summary>
    /// 反查工具的提示词（用途 + 何时用 + 用邻居 + 上限）。
    /// 供 SDK 在构建 <c>tools/list</c> 时作为 description 的唯一事实源，
    /// 避免与 <c>[Description]</c> 特性维护两份互相漂移的文本。
    /// </summary>
    public static string? GetPrompt(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName)) return null;
        return ToolItemMap.TryGetValue(Key(toolName), out var item) ? item.ComposePrompt() : null;
    }

    /// <summary>
    /// 跨工具硬约束：写入协议的 <c>initialize.instructions</c>。
    /// 只放「所有工具都要遵守」的规则，不复述单个工具的用途。
    /// </summary>
    public const string ServerInstructions =
        "这是一个知识库检索与文件阅读服务。请遵守以下顺序与约束：\n" +
        "1. 不确定有哪些数据源或知识库时，先调用 list_sources 或 knowledge_base_list 拿到合法名称/ID；不要凭空猜 sourceName 或 knowledgeBaseId。\n" +
        "2. 检索四选一：概念性问题用 semantic_search；精确词（错误码/函数名/配置项）用 lexical_search；两者都需要用 hybrid_search；已确定知识库范围用 knowledge_base_search。\n" +
        "2b. 若同一关键词既可指知识库切片也可指源码文件（如错误码、函数名），先判断来源：要读已索引的笔记/文档用 lexical_search，要在源码工程里按文件搜用 search_file_content。\n" +
        "3. 检索返回的每一条都带 chunkId + knowledgeBaseId。要读完整正文用 knowledge_base_drill_down；要确认片段来自哪个文件用 get_document_info（必须带 knowledgeBaseId）。\n" +
        "4. 所有文件路径必须来自 list_sources / get_file_tree 给出的根目录之下；绝对路径或越界路径会被沙箱拒绝（errorCode=path_denied）。\n" +
        "5. 每个工具的 topK / limit / maxMatches 都有上限（见各自描述）。需要更多结果时请收窄过滤条件（知识库、connectorId、pathKeyword），不要单纯加大数量。\n" +
        "6. 失败信封统一带 errorCode（not_found / invalid_argument / too_large / path_denied / forbidden 等），请按码决定重试还是放弃，不要只读文本。";

    /// <summary>
    /// 根据 MCP 工具名称反查其所属的业务功能模块代号（如 "semantic_search" -> "search", "get_file_tree" -> "files"）
    /// </summary>
    public static string? GetModuleByToolName(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName)) return null;
        return ToolToModuleMap.TryGetValue(toolName, out var module) ? module : null;
    }

    /// <summary>
    /// 反查工具的风险等级（"Read" / "Execute" / "Dangerous"），供协议 annotations 使用。
    /// </summary>
    public static string? GetRiskLevel(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName)) return null;

        // 目录用 PascalCase（get_file_tree），协议工具名是 snake_case（get_file_tree）：
        // 归一化后比较，避免两套命名互相查不到。
        static string Key(string value) =>
            string.Concat(value.Where(char.IsLetterOrDigit)).ToLowerInvariant();

        var target = Key(toolName);
        foreach (var category in Categories)
        {
            foreach (var tool in category.Tools)
            {
                if (Key(tool.Name) == target) return tool.RiskLevel;
            }
        }
        return null;
    }

    /// <summary>
    /// 检查指定工具名称是否为系统内已知工具
    /// </summary>
    public static bool IsKnownTool(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName)) return false;
        return ToolToModuleMap.ContainsKey(toolName);
    }
}
