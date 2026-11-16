using System.Text.Json;

namespace ReferenceRAG.Host.Tests;

/// <summary>
/// MCP 工具返回值解析辅助：区分「业务成功（纯 JSON）」与「协议失败信封」。
/// 失败信封形如 <c>{"content":[{"type":"text","text":"{...success:false}"}],"isError":true}</c>。
/// </summary>
public sealed class McpToolResult
{
    private McpToolResult(bool isError, string text, JsonElement body)
    {
        IsError = isError;
        Text = text;
        Body = body;
    }

    public bool IsError { get; }
    public string Text { get; }
    public JsonElement Body { get; }

    public static McpToolResult Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("isError", out var isError) && isError.ValueKind == JsonValueKind.True)
        {
            var text = root.GetProperty("content").EnumerateArray()
                .First().GetProperty("text").GetString() ?? string.Empty;
            using var inner = JsonDocument.Parse(text);
            return new McpToolResult(true, text, inner.RootElement.Clone());
        }

        // 部分旧式返回值没有信封，直接就是业务 JSON
        return new McpToolResult(false, json, root.Clone());
    }
}
