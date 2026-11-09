using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Service.Services;

namespace ReferenceRAG.Business.Features.KnowledgeBase.Services;

/// <summary>Maps trusted source grants to individual files owned by enabled local connectors.</summary>
internal sealed class KnowledgeBaseFileAuthorization
{
    private readonly ConfigManager _configManager;
    private readonly HashSet<string> _allowedSources;
    private readonly Dictionary<string, ConnectorDefinition> _connectors;

    public KnowledgeBaseFileAuthorization(ConfigManager configManager, IEnumerable<string> allowedSources,
        IEnumerable<ConnectorDefinition> connectors)
    {
        _configManager = configManager;
        _allowedSources = allowedSources.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _connectors = connectors.Where(c => c.Enabled).ToDictionary(c => c.Id, StringComparer.Ordinal);
    }

    public bool Allows(string connectorId, string sourceKey, string? locator)
    {
        if (!_connectors.TryGetValue(connectorId, out var connector) || string.IsNullOrWhiteSpace(sourceKey) || string.IsNullOrWhiteSpace(locator)) return false;
        try
        {
            var target = Path.GetFullPath(connector.TargetPath);
            string path;
            if (connector.Kind.Equals("singlefile", StringComparison.OrdinalIgnoreCase) ||
                connector.Kind.Equals("file", StringComparison.OrdinalIgnoreCase))
            {
                var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (!string.Equals(Path.GetFileName(target), sourceKey, comparison)) return false;
                path = target;
            }
            else if (connector.Kind.Equals("folder", StringComparison.OrdinalIgnoreCase) ||
                     connector.Kind.Equals("localfolder", StringComparison.OrdinalIgnoreCase))
            {
                if (Path.IsPathRooted(sourceKey)) return false;
                path = Path.GetFullPath(Path.Combine(target, sourceKey));
                var relative = Path.GetRelativePath(target, path);
                if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    return false;
            }
            else return false;

            var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!string.Equals(path, Path.GetFullPath(locator), pathComparison)) return false;

            // Most-specific configured root wins, including disabled and ungranted nested sources.
            var source = IndexService.FindLexicalSource(path, _configManager.Load().Sources);
            return source != null && source.Enabled && _allowedSources.Contains(source.Name) &&
                File.Exists(path) && IndexService.IsLexicalFileAllowed(path, source);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
