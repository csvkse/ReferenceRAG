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

    /// <summary>失败：输出协议信封，SDK 据此设置 <c>isError=true</c>。</summary>
    public static string Fail(string? error, string? errorCode = null)
    {
        var body = new JsonObject
        {
            ["success"] = false,
            ["error"] = error ?? "未知错误"
        };
        if (!string.IsNullOrWhiteSpace(errorCode)) body["errorCode"] = errorCode;

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
                ["errorCode"] = errorCode ?? "error"
            }
        }.ToJsonString(PrettyOptions);
    }
}
