using System;
using System.Collections.Generic;
using System.Linq;

namespace ReferenceRAG.Core.Services;

/// <summary>
/// MCP 工具单项元数据（叶子节点）
/// </summary>
public sealed record McpToolCatalogItem(
    string Name,
    string Description,
    string RiskLevel, // "Read", "Execute", "Dangerous"
    string ModuleCode // 归属的业务模块代号，如 "search", "files", "sources", "index", "system"
);

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
                new("SemanticSearch", "在知识库中执行向量语义搜索，基于自然语言查找相关文档片段", "Read", "search"),
                new("HybridSearch", "混合搜索（BM25 关键词精确匹配 + 向量双路语义融合检索）", "Read", "search"),
                new("RerankResults", "对搜索候选文档进行深度交叉注意力语义重排与打分", "Read", "search"),
                new("EmbedText", "将单段文本转换为高维向量表示（Embedding）", "Read", "search"),
                new("CalculateSimilarity", "计算两段文本之间的语义余弦相似度分数", "Read", "search"),
                new("EmbedBatch", "批量将多段文本列表转换为向量表示", "Read", "search"),
                new("GraphSearch", "知识图谱关联文档节点拓扑发现与双向关联遍历", "Read", "search"),
                new("GraphSearchByTitle", "在知识图谱中根据标题关键词模糊检索文档节点", "Read", "search")
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
                new("GetFileTree", "获取指定知识源或子目录下的文件与目录层级树", "Read", "files"),
                new("ReadNoteContent", "流式按行范围读取指定笔记文本内容（防 Token 爆仓）", "Read", "files"),
                new("GetNoteOutline", "解析 Markdown 大纲目录树、元数据与向量索引分块状态", "Read", "files"),
                new("SearchFileContent", "Agent 级关键字或正则表达式内容检索（类似 ripgrep/grep）", "Read", "files"),
                new("FindFilesByPattern", "根据通配符模式快速匹配定位知识源中文件路径（类似 fd-find）", "Read", "files")
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
                new("ListSources", "列出系统中所有已配置的知识库数据源及其索引统计", "Read", "sources"),
                new("GetSourceInfo", "获取指定数据源的物理路径、文件数量与详细索引健康度", "Read", "sources"),
                new("GetSystemStats", "获取整体系统宏观配置概览、模型参数与全局统计指标", "Read", "sources")
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
                new("GetIndexStatus", "获取当前向量索引与 BM25 全文索引的状态与统计指标", "Read", "index"),
                new("GetDocumentInfo", "根据分块 ID 查询具体文档片段的内容、行号与元数据", "Read", "index"),
                new("ListFiles", "浏览已入库索引的文件列表及其所属知识源", "Read", "index")
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
                new("Ping", "测试 MCP 通道双向连通性（返回 pong）", "Read", "system"),
                new("GetServiceInfo", "获取 ReferenceRAG 服务版本与系统信息", "Read", "system")
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

    /// <summary>
    /// 根据 MCP 工具名称反查其所属的业务功能模块代号（如 "SemanticSearch" -> "search", "GetFileTree" -> "files"）
    /// </summary>
    public static string? GetModuleByToolName(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName)) return null;
        return ToolToModuleMap.TryGetValue(toolName, out var module) ? module : null;
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
