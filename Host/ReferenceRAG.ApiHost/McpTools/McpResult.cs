using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReferenceRAG.Service.McpTools;

/// <summary>
/// MCP 工具结果信封（Adapter + State Machine）。
///
/// 背景：McpHelper 的适配层把工具返回的字符串当成 <c>CallToolResult</c> 反序列化，
/// 因此有两个必须遵守的协议约束（均已生产实测）：
///
/// 1. 工具 JSON 顶层 **不能** 出现 <c>content</c>（会与协议保留字段冲突，结果被整段吞成空数组）。
///    <c>read_note_content</c> 曾因此完全失效。
/// 2. 业务失败必须返回 <c>{"content":[...],"isError":true}</c> 才是协议层可判别的失败；
///    否则客户端只能靠解析 JSON 文本猜测。
///
/// 成功的工具结果仍然是"纯业务 JSON"，但会经过保留字段清洗（见 <see cref="Sanitize"/>）。
/// </summary>
public static class McpResult
{
    /// <summary>CallToolResult 的保留字段：出现在工具顶层 JSON 时会被协议层覆盖。</summary>
    public static readonly IReadOnlyList<string> ReservedTopLevelKeys = new[]
    {
        "content", "result", "isError", "structuredContent", "_meta", "meta"
    };

    private static readonly JsonSerializerOptions PrettyOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // 中文不转义：\uXXXX 会把同样内容膨胀数倍，直接吃模型上下文预算。
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>成功：返回业务 JSON（已移除非法的顶层保留字段）。</summary>
    public static string Ok(object payload) => Sanitize(JsonSerializer.Serialize(payload, PrettyOptions));

    /// <summary>成功：已经是完整 JSON 文本时使用。</summary>
    public static string OkRaw(string json) => Sanitize(json);

    /// <summary>失败：返回协议可判别的 <c>isError:true</c> 信封。</summary>
    public static string Fail(string? error, string? errorCode = null)
    {
        var body = new JsonObject
        {
            ["success"] = false,
            ["error"] = error ?? "未知错误"
        };
        if (!string.IsNullOrWhiteSpace(errorCode)) body["errorCode"] = errorCode;

        var envelope = new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = body.ToJsonString(PrettyOptions)
                }
            },
            ["isError"] = true
        };
        return envelope.ToJsonString(PrettyOptions);
    }

    /// <summary>
    /// 把工具自己的顶层保留字段挪到 <c>payload</c> 下，避免被协议层覆盖。
    /// 这里只做一次轻量重写：绝大多数工具不会命中，命中即为契约违规。
    /// </summary>
    public static string Sanitize(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return json;

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch
        {
            // 非 JSON 文本（少数工具直接返回 Markdown），保持原样
            return json;
        }

        if (node is not JsonObject obj) return json;

        var displaced = new JsonObject();
        foreach (var key in ReservedTopLevelKeys)
        {
            if (!obj.TryGetPropertyValue(key, out var value)) continue;
            obj.Remove(key);
            displaced[key] = value?.DeepClone();
        }

        if (displaced.Count > 0)
        {
            obj["payload"] = displaced;
        }

        return obj.ToJsonString(PrettyOptions);
    }
}
