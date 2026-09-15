namespace ReferenceRAG.Core.Interfaces;

/// <summary>Counts text tokens with the tokenizer used by the active embedding endpoint.</summary>
public interface IEmbeddingTokenCounter
{
    Task<int> CountTokensAsync(string text, CancellationToken cancellationToken = default);
}
