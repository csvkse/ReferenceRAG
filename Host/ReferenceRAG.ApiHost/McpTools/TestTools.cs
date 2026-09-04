using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace ReferenceRAG.Service.McpTools;

/// <summary>
/// 测试和诊断工具
/// </summary>
[McpServerToolType]
public class TestTools
{
    [McpServerTool, Description("测试 MCP 连通性，返回 pong")]
    public static string Ping()
    {
        return JsonSerializer.Serialize(new
        {
            status = "ok",
            message = "pong",
            timestamp = DateTime.UtcNow.ToString("O")
        });
    }

    [McpServerTool, Description("获取 ReferenceRAG MCP 服务信息")]
    public static string GetServiceInfo()
    {
        return JsonSerializer.Serialize(new
        {
            name = "ReferenceRAG-MCP",
            version = "1.0.0",
            transport = "SSE",
            endpoint = "/api/mcp",
            description = "ReferenceRAG MCP Server - 提供 RAG 检索、向量化等功能"
        });
    }
}
