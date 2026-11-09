using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Core.Interfaces;

/// <summary>Immutable caller-authorized scope. An empty source set grants no access.</summary>
public sealed class LexicalSearchScope
{
    private readonly HashSet<string> _sources;
    private readonly string[] _folders;
    private readonly bool _invalidFolderScope;
    private readonly HashSet<string> _tags;
    private readonly Dictionary<string, string[]>? _sourceRoots;
    private readonly Func<FileRecord, bool>? _fileAllowed;

    public LexicalSearchScope(IEnumerable<string> sources, IEnumerable<string>? folders = null,
        IEnumerable<string>? tags = null, DateTime? start = null, DateTime? end = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? sourceRoots = null,
        Func<FileRecord, bool>? fileAllowed = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _sources = sources.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var requestedFolders = folders?.ToArray() ?? Array.Empty<string>();
        _folders = requestedFolders.Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => f.Replace('\\', '/').TrimEnd('/')).ToArray() ?? Array.Empty<string>();
        _invalidFolderScope = requestedFolders.Length > 0 && _folders.Length == 0;
        _tags = tags?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new(StringComparer.OrdinalIgnoreCase);
        _fileAllowed = fileAllowed;
        if (sourceRoots != null)
            _sourceRoots = sourceRoots.ToDictionary(pair => pair.Key,
                pair => pair.Value.Where(root => !string.IsNullOrWhiteSpace(root))
                    .Select(root => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))).ToArray(),
                StringComparer.OrdinalIgnoreCase);
        Start = start;
        End = end;
    }

    public DateTime? Start { get; }
    public DateTime? End { get; }

    public bool Allows(FileRecord file)
    {
        if (string.IsNullOrEmpty(file.Source) || !_sources.Contains(file.Source)) return false;
        if (_fileAllowed != null && !_fileAllowed(file)) return false;
        if (_invalidFolderScope) return false;
        if (_sourceRoots != null)
        {
            if (!_sourceRoots.TryGetValue(file.Source, out var roots)) return false;
            var fullPath = Path.GetFullPath(file.Path);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!roots.Any(root => fullPath.Equals(root, comparison) || fullPath.StartsWith(
                Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, comparison))) return false;
        }
        var path = file.Path.Replace('\\', '/');
        if (_folders.Length > 0 && !_folders.Any(folder =>
            path.Equals(folder, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase))) return false;
        if (_tags.Count > 0 && (file.Tags == null || !file.Tags.Any(_tags.Contains))) return false;
        if (Start.HasValue && (!file.ModifiedAt.HasValue || file.ModifiedAt.Value < Start.Value)) return false;
        if (End.HasValue && (!file.ModifiedAt.HasValue || file.ModifiedAt.Value > End.Value)) return false;
        return true;
    }
}
