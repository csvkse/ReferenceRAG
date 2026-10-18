using ReferenceRAG.Core.Features.ModelManagement.StateMachine;
using ReferenceRAG.Core.Interfaces;

namespace ReferenceRAG.Core.Features.ModelManagement.Adapters;

/// <summary>
/// 安全平滑降级重排适配器（当底层模型损坏、OOM 或缺少算子时提供零崩溃保底排序）
/// </summary>
public sealed class SafeFallbackRerankAdapter : IRerankBackendAdapter
{
    private readonly string _modelPath;
    private readonly string _modelName;
    private readonly string _reason;

    public string BackendType => "fallback";
    public string ModelPath => _modelPath;
    public string ModelName => _modelName;
    public bool IsLoaded => true;
    public string Reason => _reason;

    public SafeFallbackRerankAdapter(string modelPath, string modelName, string reason = "Fallback mode")
    {
        _modelPath = modelPath;
        _modelName = modelName;
        _reason = reason;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<double> RerankAsync(string query, string document, CancellationToken cancellationToken = default) =>
        Task.FromResult(0.75);

    public Task<RerankResult> RerankBatchAsync(string query, IEnumerable<string> documents, CancellationToken cancellationToken = default)
    {
        var docList = documents as IReadOnlyList<string> ?? documents.ToList();
        var result = new RerankResult
        {
            Query = query,
            DurationMs = 0,
            Documents = RerankExecutionContextStateMachine.GenerateFallbackDocuments(docList)
        };
        return Task.FromResult(result);
    }

    public void Dispose() { }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
