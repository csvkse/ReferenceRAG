using System;

namespace ReferenceRAG.ApiHost.Authentication;

/// <summary>
/// 环境变量密钥解析策略（次高优先级 Priority = 2）
/// 按顺序检查 REFERENCERAG_API_KEY、API_KEY、AUTH_TOKEN
/// </summary>
public sealed class EnvironmentVariableKeyResolutionStrategy : IKeyResolutionStrategy
{
    private static readonly string[] CandidateVariableNames = new[]
    {
        "REFERENCERAG_API_KEY",
        "API_KEY",
        "AUTH_TOKEN"
    };

    private readonly Func<string, string?> _envAccessor;

    public int Priority => 2;
    public KeyResolutionSource Source => KeyResolutionSource.EnvironmentVariable;

    public EnvironmentVariableKeyResolutionStrategy(Func<string, string?>? envAccessor = null)
    {
        _envAccessor = envAccessor ?? Environment.GetEnvironmentVariable;
    }

    public string? TryResolveKey()
    {
        foreach (var varName in CandidateVariableNames)
        {
            var val = _envAccessor(varName)?.Trim();
            if (!string.IsNullOrEmpty(val))
            {
                return val;
            }
        }

        return null;
    }
}
