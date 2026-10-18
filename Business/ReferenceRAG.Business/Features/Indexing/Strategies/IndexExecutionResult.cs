namespace ReferenceRAG.Business.Features.Indexing.Strategies;

/// <summary>
/// 索引策略执行结果
/// </summary>
public class IndexExecutionResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public int TotalFiles { get; set; }
    public int ProcessedFiles { get; set; }
    public int SkippedFiles { get; set; }
    public int ErrorsCount { get; set; }
    public int TotalChunks { get; set; }
    public int TotalVectors { get; set; }
    public List<string> Errors { get; set; } = new();

    public static IndexExecutionResult Ok(
        int totalFiles,
        int processedFiles,
        int skippedFiles,
        int totalChunks,
        int totalVectors,
        List<string>? errors = null) => new()
    {
        Success = true,
        TotalFiles = totalFiles,
        ProcessedFiles = processedFiles,
        SkippedFiles = skippedFiles,
        TotalChunks = totalChunks,
        TotalVectors = totalVectors,
        Errors = errors ?? new List<string>()
    };

    public static IndexExecutionResult Fail(string error, List<string>? errors = null) => new()
    {
        Success = false,
        ErrorMessage = error,
        Errors = errors ?? new List<string> { error }
    };
}
