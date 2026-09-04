using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Core.Interfaces;

public interface IGraphIndexingService
{
    Task UpdateGraphAsync(
        FileRecord file,
        string markdownContent,
        IEnumerable<ChunkRecord> chunks,
        CancellationToken ct = default,
        Func<string, string?>? resolveLink = null);

    Task RemoveAsync(string filePath, CancellationToken ct = default);
}
