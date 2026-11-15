using System.Text.RegularExpressions;

namespace ReferenceRAG.Core.Features.ModelManagement.Services;

/// <summary>
/// 嵌入模型身份规范化器（Model Identity Normalizer）。
///
/// 解决「展示名 vs 规范 ID」混用导致的配置漂移误判：
/// 系统中同一模型存在两种称谓——<see cref="ModelManager.ModelInfo.Name"/>（规范 ID，如 "bge-m3"，
/// 用于 Spec Id 与全局配置）与 <see cref="ModelManager.ModelInfo.DisplayName"/>（展示名，如 "BGE M3"，
/// 仅用于 UI 呈现）。历史上 EmbeddingSpec.ModelName 曾混存两者，使漂移检测的字符串比较恒不相等，
/// 表现为「已对齐的模型被误报为偏离(建议重排)」。
///
/// 业界对照：LangChain 将 model（规范 ID）与 model_provider 作为独立参数而非拼接前缀；
/// Qdrant 在 upsert 时以 models.Document(model=...) 传入结构化模型标识而非展示名。
/// 二者共同原则：身份字段与展示字段必须分离，身份比较必须经过规范化。
/// </summary>
public static partial class ModelIdentity
{
    /// <summary>厂商/后端分隔符（如 "SiliconFlow / BGE M3" 中的 " / "）。</summary>
    private const string ProviderSeparator = " / ";

    // 常见非规范字符：空格、连字符、下划线、点、斜杠统一剥离，仅保留字母数字与小写形式。
    // 例："BGE M3" → "bgem3"，"bge-m3" → "bgem3"，二者相等。
    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonAlphanumericRegex();

    // 版本/量化后缀（如 "-q4_k_m"、"Q4_0"、"-v1.5"）在身份比较时忽略：
    // 同一基座模型的不同量化/微调版本在 Spec 层已由 MaxTokens/Dimension 区分，
    // 但名称主干相同即视为同一模型家族，避免把 "bge-m3" 与 "bge-m3-q4" 误判为偏离。
    [GeneratedRegex(@"[-_]?(q\d+[_a-z0-9]*|v\d+(\.\d+)*)$")]
    private static partial Regex VariantSuffixRegex();

    // 尾部维度括注（如 "BGE-M3 (1024d)"、"bge-m3 (1024 d)"）：维度属于 Spec 字段而非身份，
    // 历史 Spec.ModelName 曾把 "模型 (维度d)" 整体写入，若不剥离会与规范 ID 恒不相等。
    [GeneratedRegex(@"\s*\(\s*dim\s*\d+\s*\)\s*$|\s*\(\s*\d+\s*d\s*\)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex DimensionAnnotationRegex();

    /// <summary>
    /// 规范化模型名为可比较的身份键。
    /// 依次执行：去首尾空白 → 取末段（剥离 "Provider / " 前缀）→ 剥离尾部维度括注 →
    /// 剥离厂商路径前缀（"BAAI/"）→ 转小写 → 剥离量化/版本后缀 → 剔除所有非字母数字字符。
    /// </summary>
    public static string NormalizeKey(string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName)) return string.Empty;

        var value = modelName.Trim();

        // 多段式展示名（"Provider / Model"）取末段，仅比较模型本身
        var sepIndex = value.LastIndexOf(ProviderSeparator, StringComparison.Ordinal);
        if (sepIndex >= 0 && sepIndex + ProviderSeparator.Length < value.Length)
        {
            value = value[(sepIndex + ProviderSeparator.Length)..].Trim();
        }

        // 剥离尾部维度括注（"BGE-M3 (1024d)" → "BGE-M3"）
        value = DimensionAnnotationRegex().Replace(value, string.Empty);

        // 剥离厂商路径前缀（"BAAI/bge-m3" → "bge-m3"）：组织标识不参与身份比较
        var slashIndex = value.LastIndexOf('/');
        if (slashIndex >= 0 && slashIndex + 1 < value.Length)
        {
            value = value[(slashIndex + 1)..].Trim();
        }

        value = value.ToLowerInvariant();

        // 剥离量化/版本后缀（可能多层，如 "bge-m3-q4_k_m-v1.5"）
        for (var i = 0; i < 4; i++)
        {
            var stripped = VariantSuffixRegex().Replace(value, string.Empty);
            if (stripped == value) break;
            value = stripped;
        }

        return NonAlphanumericRegex().Replace(value, string.Empty);
    }

    /// <summary>
    /// 判断两个模型名是否指向同一模型身份。
    /// 空值语义：任一为空视为「无法判定」，返回 false 交由调用方决定（不擅自判等）。
    /// </summary>
    public static bool IsSameModel(string? left, string? right)
    {
        var leftKey = NormalizeKey(left);
        var rightKey = NormalizeKey(right);
        if (leftKey.Length == 0 || rightKey.Length == 0) return false;
        return string.Equals(leftKey, rightKey, StringComparison.Ordinal);
    }
}
