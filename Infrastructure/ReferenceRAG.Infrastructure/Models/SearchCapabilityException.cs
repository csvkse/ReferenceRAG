namespace ReferenceRAG.Core.Models;

/// <summary>Known, actionable search capability or index-coverage failure.</summary>
public sealed class SearchCapabilityException : InvalidOperationException
{
    public SearchCapabilityException(string code, string message, IReadOnlyCollection<string>? sources = null)
        : base(message)
    {
        Code = code;
        Sources = sources ?? Array.Empty<string>();
    }

    public string Code { get; }
    public IReadOnlyCollection<string> Sources { get; }
}
