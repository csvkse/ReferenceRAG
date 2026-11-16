using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReferenceRAG.Service.McpTools;

/// <summary>
/// 工具结果输出助手（SDK 2.1.0 起，信封语义由 <c>McpHelper</c> 的类型化结果负责）。
///
/// 历史背景（已由 SDK 修复）：旧适配层把工具返回字符串直接当 <c>CallToolResult</c> 反序列化，
/// 于是「工具顶层叫 content」会被吞成空数组、失败也无法被协议层识别。
/// SDK 修复后，<c>McpToolResult</c> 只在 content 为数组或带 isError 时才视为信封，
/// 其余一律当业务负载并同时填 structuredContent —— 因此这里不再需要清洗保留字段。
/// </summary>
public static class McpResult
{
    private static readonly JsonSerializerOptions PrettyOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // 中文不转义：\uXXXX 会把同样内容膨胀数倍，直接吃模型上下文预算。
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>成功：业务 JSON（SDK 会把它作为 text 块并填入 structuredContent）。</summary>
    public static string Ok(object payload) => JsonSerializer.Serialize(payload, PrettyOptions);

    /// <summary>成功：已经是完整 JSON 文本时使用。</summary>
    public static string OkRaw(string json) => json;

    /// <summary>
    /// 失败：输出协议信封，SDK 据此设置 <c>isError=true</c>。
    /// <paramref name="errorCode"/> 必填（取值见 <see cref="McpErrorCodes"/>）：
    /// 客户端要按码分支处理，缺码就得解析中文文本。
    /// </summary>
    public static string Fail(string? error, string errorCode)
    {
        var body = new JsonObject
        {
            ["success"] = false,
            ["error"] = error ?? "未知错误",
            ["errorCode"] = errorCode
        };

        return new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = body.ToJsonString(PrettyOptions)
                }
            },
            ["isError"] = true,
            ["structuredContent"] = new JsonObject
            {
                ["success"] = false,
                ["errorCode"] = errorCode
            }
        }.ToJsonString(PrettyOptions);
    }

    /// <summary>包装未知异常：内部错误码 + 原始消息（便于模型判断是否可自纠）。</summary>
    public static string FailException(Exception ex, string errorCode = McpErrorCodes.InternalError) =>
        Fail(ex.Message, errorCode);

    /// <summary>业务 JSON 的体积预算：超出则拒绝而不是截断，避免单次调用吃掉模型上下文。</summary>
    public const int MaxPayloadChars = 400_000;

    /// <summary>
    /// 超出预算时返回 <c>too_large</c> 失败信封（附收窄建议），而不是静默截断。
    /// 静默截断会让模型误以为已经拿到全部结果；拒绝则促使它收窄过滤条件。
    /// </summary>
    public static string OkBounded(object payload, int maxChars = MaxPayloadChars)
    {
        var json = Ok(payload);
        if (json.Length <= maxChars) return json;

        return Fail(
            $"结果体积 {json.Length} 字符，超过上限 {maxChars}。请收窄过滤条件（更小的 topK/limit、更精确的关键词或 pathKeyword）后重试。",
            McpErrorCodes.TooLarge);
    }
}
