namespace ReferenceRAG.Service.McpTools;

/// <summary>
/// MCP 工具失败信封的错误码常量集。
///
/// 门禁依据（G-D1）：客户端应能按 <c>errorCode</c> 分支处理，而不是解析中文文本。
/// 全部 <see cref="McpResult.Fail"/> 调用必须显式给出其中一个常量，
/// <c>McpResult.Fail(string?, string)</code> 的 errorCode 参数因此是非可空必填。
/// </summary>
public static class McpErrorCodes
{
    public const string InternalError = "internal_error";
    public const string InvalidArgument = "invalid_argument";
    public const string Forbidden = "forbidden";
    public const string ServiceUnavailable = "service_unavailable";

    public const string NotFound = "not_found";
    public const string SourceNotFound = "source_not_found";
    public const string ConnectorNotFound = "connector_not_found";
    public const string KnowledgeBaseNotFound = "knowledge_base_not_found";
    public const string ChunkNotFound = "chunk_not_found";
    public const string DocumentNotFound = "document_not_found";

    public const string PathDenied = "path_denied";
    public const string TooLarge = "too_large";
    public const string SearchFailed = "search_failed";
    public const string KnowledgeBaseUnavailable = "knowledge_base_unavailable";
    public const string NoKnowledgeBase = "no_knowledge_base";
}
