using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ReferenceRAG.Service.McpTools;

/// <summary>
/// 参数校验失败（宿主侧），统一映射为 <c>invalid_argument</c> 信封。
/// 与 SDK 侧 <c>McpToolArgumentException</c> 语义相同，区别是宿主能给出可读的纠正建议。
/// </summary>
public sealed class McpInputArgumentException : Exception
{
    public McpInputArgumentException(string parameter, string message) : base(message)
    {
        Parameter = parameter;
    }

    public string Parameter { get; }
}

/// <summary>
/// MCP 工具入参适配器（Adapter）：把协议侧「宽松但同义」的入参收敛成同一种语义。
///
/// 存在的理由：此前同类参数在不同工具上行为不一致——<c>knowledgeBaseIds</c> 在
/// semantic/lexical/hybrid 接受逗号分隔，在 knowledge_base_search 却直接拒绝；
/// 空数组在一处表示「全部库」、在另一处表示「没有库」。调用方无法预测，模型只能靠试错。
/// 现在所有工具都经这一个入口解析，行为只有一种。
/// </summary>
public static class McpInputAdapters
{
    /// <summary>单段文本向量化上限（实测 40,000 字符耗时 27.6 秒，会拖垮 Agent 超时预算）。</summary>
    public const int MaxEmbedChars = 8000;

    /// <summary>批量向量化单次条数上限（100 条约 2.2 秒；400 条约 9.4 秒且无任何提示）。</summary>
    public const int MaxEmbedBatchItems = 100;

    /// <summary>重排单次候选条数上限（远远超过实际精排预算的候选没有意义）。</summary>
    public const int MaxRerankDocuments = 100;

    /// <summary>
    /// 解析知识库 ID 列表。三种等价写法：JSON 数组字符串、逗号分隔、原生数组（SDK 已转成字符串）。
    /// 空/"[]" 一律表示「全部已启用库」，由调用方决定是否解释为 all。
    /// 非法内容抛 <see cref="McpInputArgumentException"/>，不再把原始串当成库 ID 去检索。
    /// </summary>
    public static List<string> ParseKnowledgeBaseIds(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new List<string>();

        var text = raw.Trim();
        if (text == "[]") return new List<string>();
        if (text == "null") return new List<string>();

        if (text.StartsWith('['))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<List<string>>(text);
                return parsed?.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToList()
                       ?? new List<string>();
            }
            catch (JsonException)
            {
                throw new McpInputArgumentException(
                    "knowledgeBaseIds",
                    $"knowledgeBaseIds 必须是 JSON 数组字符串（如 [\"kb_a\",\"kb_b\"]）或逗号分隔列表，收到: {Truncate(text)}");
            }
        }

        // 逗号分隔容错：与另三个检索工具保持一致，避免同一参数在不同工具上被拒。
        return text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                   .Where(x => x.Length > 0)
                   .ToList();
    }

    /// <summary>
    /// 解析 JSON 数组字符串形式的文本列表（<c>texts</c> / <c>documents</c>）。
    /// 非法内容给可自纠的提示，不再泄漏 <c>Path: $ | LineNumber…</c> 这类序列化器内部信息。
    /// </summary>
    public static List<string> ParseStringArray(string? raw, string parameter)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new List<string>();

        try
        {
            return JsonSerializer.Deserialize<List<string>>(raw) ?? new List<string>();
        }
        catch (JsonException)
        {
            throw new McpInputArgumentException(
                parameter,
                $"{parameter} 必须是 JSON 数组字符串（如 [\"文本1\",\"文本2\"]），收到: {Truncate(raw)}");
        }
    }

    /// <summary>
    /// 整型入参边界：低于下限直接拒绝（0/-1 属于调用方笔误，静默夹取会掩盖错误），
    /// 高于上限按上限处理并回显生效值（与 topK 的既有语义一致）。
    /// </summary>
    public static (int Requested, int Effective) ClampInt(string parameter, int requested, int min, int max)
    {
        if (requested < min)
        {
            throw new McpInputArgumentException(
                parameter,
                $"{parameter} 必须 ≥ {min}，收到 {requested}。需要更多结果时请收窄过滤条件，而不是传 0 或负数。");
        }

        return (requested, requested > max ? max : requested);
    }

    /// <summary>把可空整型的「未传」与「传了非法值」区分开：未传走默认，传 0/-1 走拒绝。</summary>
    public static (int Requested, int Effective) ClampIntNullable(string parameter, int? requested, int fallback, int min, int max)
        => requested is null ? (fallback, fallback) : ClampInt(parameter, requested.Value, min, max);

    /// <summary>单条文本长度校验，供 calculate_similarity / rerank / embed 共用。</summary>
    public static void EnsureTextLength(string parameter, string? text)
    {
        if (text is null) return;
        if (text.Length > MaxEmbedChars)
        {
            throw new McpInputArgumentException(
                parameter,
                $"{parameter} 过长：{text.Length} 字符，上限 {MaxEmbedChars}。请分段后多次调用。");
        }
    }

    /// <summary>集合条数校验，供 embed_batch / rerank_results 共用。</summary>
    public static void EnsureItemCount(string parameter, int count, int max)
    {
        if (count > max)
        {
            throw new McpInputArgumentException(
                parameter,
                $"{parameter} 条数过多：{count} 条，上限 {max}。请分批调用。");
        }
    }

    private static string Truncate(string text) =>
        text.Length <= 60 ? text : text[..60] + "…";
}
