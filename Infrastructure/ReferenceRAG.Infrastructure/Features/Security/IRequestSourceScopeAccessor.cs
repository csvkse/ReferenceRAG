namespace ReferenceRAG.Core.Interfaces;

/// <summary>Returns the current request's source grants; null represents unrestricted access.</summary>
public interface IRequestSourceScopeAccessor
{
    IReadOnlyList<string>? GetAllowedSources();
}
