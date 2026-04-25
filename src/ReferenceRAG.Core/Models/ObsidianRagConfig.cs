namespace ReferenceRAG.Core.Models;

/// <summary>
/// ReferenceRAG 配置
/// </summary>
public class ObsidianRagConfig
{
    /// <summary>
    /// 数据存储路径
    /// </summary>
    public string DataPath { get; set; } = "data";

    /// <summary>
    /// 模型根目录（统一管理所有模型：嵌入式和重排）
    /// </summary>
    public string ModelsRootPath { get; set; } = "models";

    /// <summary>
    /// 源文件夹列表（支持多个）
    /// </summary>
    public List<SourceFolder> Sources { get; set; } = new();

    /// <summary>
    /// 向量模型配置
    /// </summary>
    public EmbeddingConfig Embedding { get; set; } = new();

    /// <summary>
    /// 分段配置
    /// </summary>
    public ChunkingConfig Chunking { get; set; } = new();

    /// <summary>
    /// 搜索配置
    /// </summary>
    public SearchConfig Search { get; set; } = new();

    /// <summary>
    /// 服务配置
    /// </summary>
    public ServiceConfig Service { get; set; } = new();

    /// <summary>
    /// 重排模型配置
    /// </summary>
    public RerankConfig Rerank { get; set; } = new();

    /// <summary>
    /// 索引配置
    /// </summary>
    public IndexingConfig Indexing { get; set; } = new();

}

/// <summary>
/// 源文件夹配置
/// </summary>
public class SourceFolder
{
    /// <summary>
    /// 文件夹路径
    /// </summary>
    public string Path { get; set; } = "";

    /// <summary>
    /// 显示名称（用于区分不同来源）
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// 是否启用
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 文件匹配模式
    /// </summary>
    public List<string> FilePatterns { get; set; } = new() { "*.md" };

    /// <summary>
    /// 是否递归索引
    /// </summary>
    public bool Recursive { get; set; } = true;

    /// <summary>
    /// 排除的目录
    /// </summary>
    public List<string> ExcludeDirs { get; set; } = new()
    {
        ".git",
        "node_modules",
        ".trash"
    };

    /// <summary>
    /// 排除的文件模式
    /// </summary>
    public List<string> ExcludeFiles { get; set; } = new()
    {
        "*.tmp",
        "*.bak",
        "~*"
    };

    /// <summary>
    /// 文件类型（用于分类）
    /// </summary>
    public SourceType Type { get; set; } = SourceType.Markdown;

    /// <summary>
    /// 自定义标签（用于过滤）
    /// </summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>
    /// 优先级（数字越大优先级越高）
    /// </summary>
    public int Priority { get; set; } = 0;
}

/// <summary>
/// 源类型
/// </summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum SourceType
{
    /// <summary>
    /// Obsidian 笔记库
    /// </summary>
    Obsidian,

    /// <summary>
    /// 普通 Markdown 文件
    /// </summary>
    Markdown,

    /// <summary>
    /// 文档目录
    /// </summary>
    Documents,

    /// <summary>
    /// 代码文档
    /// </summary>
    CodeDocs,

    /// <summary>
    /// 自定义
    /// </summary>
    Custom
}

/// <summary>
/// 向量模型配置
/// </summary>
public class EmbeddingConfig
{
    /// <summary>
    /// 模型路径
    /// </summary>
    public string ModelPath { get; set; } = "";

    /// <summary>
    /// 模型名称
    /// </summary>
    public string ModelName { get; set; } = "bge-small-zh-v1.5";

    /// <summary>
    /// 是否使用 CUDA
    /// </summary>
    public bool UseCuda { get; set; } = false;

    /// <summary>
    /// CUDA 设备 ID
    /// </summary>
    public int CudaDeviceId { get; set; } = 0;

    /// <summary>
    /// CUDA 库路径（可选，用于指定 CUDA DLL 所在目录）
    /// </summary>
    public string? CudaLibraryPath { get; set; }

    /// <summary>
    /// 最大序列长度
    /// </summary>
    public int MaxSequenceLength { get; set; } = 512;

    /// <summary>
    /// 批处理大小（RTX 4060 推荐 64-128）
    /// </summary>
    public int BatchSize { get; set; } = 32;

}

/// <summary>
/// 分段配置
/// </summary>
public class ChunkingConfig
{
    /// <summary>
    /// 最大 Token 数
    /// </summary>
    public int MaxTokens { get; set; } = 512;

    /// <summary>
    /// 最小 Token 数
    /// </summary>
    public int MinTokens { get; set; } = 50;

    /// <summary>
    /// 重叠 Token 数
    /// </summary>
    public int OverlapTokens { get; set; } = 50;

    /// <summary>
    /// 是否保留标题
    /// </summary>
    public bool PreserveHeadings { get; set; } = true;

    /// <summary>
    /// 是否保留代码块
    /// </summary>
    public bool PreserveCodeBlocks { get; set; } = true;
}

/// <summary>
/// 搜索配置
/// </summary>
public class SearchConfig
{
    /// <summary>
    /// 默认返回数量
    /// </summary>
    public int DefaultTopK { get; set; } = 10;

    /// <summary>
    /// 上下文窗口大小
    /// </summary>
    public int ContextWindow { get; set; } = 1;

    /// <summary>
    /// 相似度阈值
    /// </summary>
    public float SimilarityThreshold { get; set; } = 0.5f;

    /// <summary>
    /// 是否启用 MMR 多样性
    /// </summary>
    public bool EnableMmr { get; set; } = true;

    /// <summary>
    /// MMR lambda 参数
    /// </summary>
    public float MmrLambda { get; set; } = 0.7f;

    /// <summary>
    /// 默认搜索的源（空=全部）
    /// </summary>
    public List<string> DefaultSources { get; set; } = new();

    /// <summary>
    /// BM25 提供者类型：fts5
    /// </summary>
    public string BM25Provider { get; set; } = "fts5";

    /// <summary>
    /// 是否启用知识图谱扩展召回（基于 wiki-link 邻居节点）
    /// </summary>
    public bool EnableGraphExpansion { get; set; } = false;

    /// <summary>
    /// 图扩展遍历深度（1-2，默认 1）
    /// </summary>
    public int GraphExpansionDepth { get; set; } = 1;

    /// <summary>
    /// 每个结果最多扩展的邻居节点数（默认 3）
    /// </summary>
    public int GraphExpansionMaxNodes { get; set; } = 3;
}

/// <summary>
/// 服务配置
/// </summary>
public class ServiceConfig
{
    /// <summary>
    /// 监听端口
    /// </summary>
    public int Port { get; set; } = 5000;

    /// <summary>
    /// 是否允许来自局域网/外部网络的访问。
    /// true → 绑定 0.0.0.0（全网可访问）；false → 仅绑定 localhost（默认）。
    /// 修改后需要重启服务生效。
    /// </summary>
    public bool AllowNetworkAccess { get; set; } = false;

    /// <summary>
    /// 是否启用 CORS
    /// </summary>
    public bool EnableCors { get; set; } = true;

    /// <summary>
    /// 是否启用 Swagger
    /// </summary>
    public bool EnableSwagger { get; set; } = true;

    /// <summary>
    /// 日志级别
    /// </summary>
    public string LogLevel { get; set; } = "Information";

    /// <summary>
    /// API Key 用于认证（为空则不启用认证）
    /// </summary>
    public string? ApiKey { get; set; }
}

/// <summary>
/// 重排模型配置
/// </summary>
public class RerankConfig
{
    /// <summary>
    /// 是否启用重排功能
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// 模型名称
    /// </summary>
    public string ModelName { get; set; } = "bge-reranker-base";

    /// <summary>
    /// 当前使用的重排模型
    /// </summary>
    public string? CurrentModel { get; set; }

    /// <summary>
    /// 模型路径（相对于 ModelsRootPath）
    /// </summary>
    public string? ModelPath { get; set; }

    /// <summary>
    /// 是否使用 CUDA
    /// </summary>
    public bool UseCuda { get; set; } = false;

    /// <summary>
    /// CUDA 设备 ID
    /// </summary>
    public int CudaDeviceId { get; set; } = 0;

    /// <summary>
    /// 重排后返回的最大文档数
    /// </summary>
    public int TopN { get; set; } = 10;

    /// <summary>
    /// 召回倍数（候选文档数 = TopN * RecallFactor）
    /// 推荐 3-5 倍，确保召回足够的候选
    /// </summary>
    public int RecallFactor { get; set; } = 3;

    /// <summary>
    /// 是否在 Hybrid 模式下自动启用重排
    /// true: Hybrid 模式自动进行两阶段搜索
    /// false: 仅当 QueryMode.HybridRerank 时才重排
    /// </summary>
    public bool AutoRerankInHybrid { get; set; } = true;

    /// <summary>
    /// 重排分数阈值（低于此阈值的文档将被过滤）
    /// 0 表示不过滤
    /// </summary>
    public float ScoreThreshold { get; set; } = 0.0f;
}

/// <summary>
/// 索引配置
/// </summary>
public class IndexingConfig
{
    /// <summary>
    /// 是否启用自动索引
    /// </summary>
    public bool AutoIndexEnabled { get; set; } = true;

    /// <summary>
    /// 防抖延迟（毫秒）
    /// 用于合并短时间内多次文件变动事件
    /// </summary>
    public int DebounceMs { get; set; } = 500;

    /// <summary>
    /// 启动时是否同步索引
    /// </summary>
    public bool SyncOnStartup { get; set; } = true;

    /// <summary>
    /// 文件处理最大重试次数
    /// </summary>
    public int MaxFileRetries { get; set; } = 3;

    /// <summary>
    /// 上下文窗口大小（用于搜索结果的上下文展示）
    /// </summary>
    public int ContextWindowSize { get; set; } = 1;
}
