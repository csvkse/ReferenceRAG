using Microsoft.AspNetCore.Http;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;

namespace ReferenceRAG.Service.Services;

internal sealed class HttpRequestSourceScopeAccessor : IRequestSourceScopeAccessor
{
    private readonly IHttpContextAccessor _context;
    public HttpRequestSourceScopeAccessor(IHttpContextAccessor context) => _context = context;
    public IReadOnlyList<string>? GetAllowedSources() => ApiKeyPermissionMatrix.GetEffectiveAllowedSources(
        _context.HttpContext?.Items["CurrentApiKey"] as ApiKeyEntity);
}
