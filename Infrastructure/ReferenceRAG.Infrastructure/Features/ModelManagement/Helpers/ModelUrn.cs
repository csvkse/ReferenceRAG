using System;
using System.Linq;

namespace ReferenceRAG.Core.Features.ModelManagement.Helpers;

/// <summary>
/// 全域统一模型资源定位符 (Uniform Resource Name)
/// 规范格式：
///   本地模型：local:{modelName} (例如 local:bge-small-zh-v1.5)
///   服务商模型：provider:{providerId}:{modelId} (例如 provider:prov_sf:deepseek-ai/DeepSeek-V3)
/// </summary>
public sealed class ModelUrn : IEquatable<ModelUrn>
{
    public const string LocalPrefix = "local:";
    public const string ProviderPrefix = "provider:";

    public bool IsLocal => !string.IsNullOrEmpty(ModelName) && string.IsNullOrEmpty(ProviderId);
    public bool IsProvider => !string.IsNullOrEmpty(ProviderId) && !string.IsNullOrEmpty(ModelId);
    public bool IsEmpty => string.IsNullOrEmpty(ModelName) && string.IsNullOrEmpty(ProviderId);

    /// <summary>本地模型名称（仅对 local 生效）</summary>
    public string ModelName { get; } = string.Empty;

    /// <summary>服务商唯一标识（仅对 provider 生效，例如 prov_sf）</summary>
    public string ProviderId { get; } = string.Empty;

    /// <summary>服务商内模型标识（仅对 provider 生效，例如 deepseek-ai/DeepSeek-V3）</summary>
    public string ModelId { get; } = string.Empty;

    private readonly string _rawUrn;

    private ModelUrn(string modelName, string providerId, string modelId, string rawUrn)
    {
        ModelName = modelName ?? string.Empty;
        ProviderId = providerId ?? string.Empty;
        ModelId = modelId ?? string.Empty;
        _rawUrn = rawUrn ?? string.Empty;
    }

    /// <summary>创建本地模型 URN</summary>
    public static ModelUrn ForLocal(string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName)) return Empty;
        var clean = modelName.Trim();
        if (clean.StartsWith(LocalPrefix, StringComparison.OrdinalIgnoreCase))
            clean = clean.Substring(LocalPrefix.Length);
        return new ModelUrn(clean, string.Empty, string.Empty, $"{LocalPrefix}{clean}");
    }

    /// <summary>创建服务商模型 URN</summary>
    public static ModelUrn ForProvider(string providerId, string modelId)
    {
        if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(modelId)) return Empty;
        var pId = providerId.Trim();
        var mId = modelId.Trim();
        return new ModelUrn(string.Empty, pId, mId, $"{ProviderPrefix}{pId}:{mId}");
    }

    /// <summary>从任意字符串安全解析为 ModelUrn（兼容旧版名称无感迁移）</summary>
    public static ModelUrn Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return Empty;
        var s = input.Trim();

        // 1. 标准 provider: 前缀
        if (s.StartsWith(ProviderPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var content = s.Substring(ProviderPrefix.Length);
            var firstColon = content.IndexOf(':');
            if (firstColon > 0)
            {
                var provId = content.Substring(0, firstColon);
                var modId = content.Substring(firstColon + 1);
                return new ModelUrn(string.Empty, provId, modId, s);
            }
        }

        // 2. 标准 local: 前缀
        if (s.StartsWith(LocalPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var modelName = s.Substring(LocalPrefix.Length);
            return new ModelUrn(modelName, string.Empty, string.Empty, s);
        }

        // 3. 兼容历史知识库 prov_ 前缀格式 (例如 prov_prov_sf_BAAI/bge-m3)
        if (s.StartsWith("prov_", StringComparison.OrdinalIgnoreCase))
        {
            var parts = s.Split('_', 3);
            if (parts.Length == 3)
            {
                var provId = $"prov_{parts[1]}";
                var modId = parts[2];
                return new ModelUrn(string.Empty, provId, modId, $"{ProviderPrefix}{provId}:{modId}");
            }
        }

        // 4. 默认兜底为本地模型名称
        return new ModelUrn(s, string.Empty, string.Empty, $"{LocalPrefix}{s}");
    }

    public static ModelUrn Empty { get; } = new(string.Empty, string.Empty, string.Empty, string.Empty);

    public override string ToString() => _rawUrn;

    public bool Equals(ModelUrn? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return string.Equals(_rawUrn, other._rawUrn, StringComparison.OrdinalIgnoreCase);
    }

    public override bool Equals(object? obj) => Equals(obj as ModelUrn);

    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(_rawUrn);

    public static bool operator ==(ModelUrn? left, ModelUrn? right) => Equals(left, right);
    public static bool operator !=(ModelUrn? left, ModelUrn? right) => !Equals(left, right);
}
