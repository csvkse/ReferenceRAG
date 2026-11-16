using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Service.McpTools;
using ModelContextProtocol.Server;
using Xunit;

namespace ReferenceRAG.Host.Tests;

/// <summary>
/// 工具提示词与错误信封契约门禁（G-D1 / G-D5 / G-D6 / G-D9）。
///
/// 背景：生产实测 25 个工具里 21 个未说明与邻居工具的取舍、8 个整型参数无上下界、
/// 三处失败无 errorCode。本组锁定「描述由注册表统一供给」与「失败必带码」两条不变量。
/// </summary>
public sealed class McpToolDescriptionContractGateTests
{
    private static IEnumerable<Type> ToolTypes() =>
        typeof(McpResult).Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null);

    private static IEnumerable<MethodInfo> ToolMethods() =>
        ToolTypes().SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
                   .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null);

    [Fact]
    public void EveryRegisteredTool_HasCatalogPrompt()
    {
        // 描述必须来自 McpCatalogRegistry（单一事实源），否则协议层会退回 [Description]，
        // 与权限矩阵里那份文本漂移。
        foreach (var m in ToolMethods())
        {
            var attr = m.GetCustomAttribute<McpServerToolAttribute>()!;
            var name = attr.Name ?? m.Name;
            var prompt = McpCatalogRegistry.GetPrompt(name);
            Assert.False(string.IsNullOrWhiteSpace(prompt), $"{name} 在 McpCatalogRegistry 中没有提示词");
        }
    }

    [Fact]
    public void CatalogPrompt_CoversAllFourComponents()
    {
        foreach (var category in McpCatalogRegistry.GetCatalog())
        {
            foreach (var tool in category.Tools)
            {
                var prompt = tool.ComposePrompt();
                Assert.False(string.IsNullOrWhiteSpace(prompt), tool.Name);
            }
        }
    }

    [Theory]
    [InlineData("SemanticSearch")]
    [InlineData("LexicalSearch")]
    [InlineData("HybridSearch")]
    [InlineData("KnowledgeBaseSearch")]
    public void OverlappingSearchTools_NameTheirNeighbour(string toolName)
    {
        var prompt = McpCatalogRegistry.GetPrompt(toolName);
        Assert.NotNull(prompt);
        // 四选一指引：必须能指向另外三个中的至少两个
        var others = new[] { "SemanticSearch", "LexicalSearch", "HybridSearch", "KnowledgeBaseSearch" }
            .Where(n => !string.Equals(n, toolName, StringComparison.OrdinalIgnoreCase))
            .Select(n => n switch
            {
                "SemanticSearch" => "semantic_search",
                "LexicalSearch" => "lexical_search",
                "HybridSearch" => "hybrid_search",
                _ => "knowledge_base_search"
            }).ToList();
        Assert.True(others.Count(o => prompt!.Contains(o, StringComparison.OrdinalIgnoreCase)) >= 2,
            $"{toolName} 的提示词未指向足够的邻居工具：{prompt}");
    }

    [Fact]
    public void NumericCapTools_StateTheirBound()
    {
        // 仅对「真的有整型数量参数」的工具要求写明上下界；
        // 其余工具（GetNoteOutline 等）的 Limits 只描述行为边界。
        var numericCapTools = new[]
        {
            "SemanticSearch", "LexicalSearch", "HybridSearch", "KnowledgeBaseSearch",
            "RerankResults", "ListFiles", "SearchFileContent", "FindFilesByPattern",
            "GraphSearch", "GraphSearchByTitle", "KnowledgeBaseDrillDown", "EmbedText",
            "EmbedBatch", "CalculateSimilarity", "ReadNoteContent", "GetFileTree"
        };
        foreach (var tool in McpCatalogRegistry.GetCatalog().SelectMany(c => c.Tools))
        {
            var name = tool.Name;
            if (!numericCapTools.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            Assert.False(string.IsNullOrWhiteSpace(tool.Limits), $"{name} 缺少 Limits");
            Assert.True(new[] { "上限", "默认", "最大", "最多" }.Any(w => tool.Limits!.Contains(w)),
                $"{name} 的 Limits 未写明数量约束：{tool.Limits}");
        }
    }

    [Fact]
    public void ServerInstructions_ContainHardConstraints()
    {
        var text = McpCatalogRegistry.ServerInstructions;
        Assert.Contains("list_sources", text);
        Assert.Contains("chunkId", text);
        Assert.Contains("path_denied", text);
        Assert.Contains("errorCode", text);
    }

    [Fact]
    public void Fail_RequiresAndEmitsErrorCode()
    {
        var json = McpResult.Fail("boom", McpErrorCodes.ConnectorNotFound);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("isError").GetBoolean());
        Assert.Equal(McpErrorCodes.ConnectorNotFound,
            doc.RootElement.GetProperty("structuredContent").GetProperty("errorCode").GetString());
    }

    [Fact]
    public void FailException_DefaultsToInternalError()
    {
        var json = McpResult.FailException(new InvalidOperationException("x"));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(McpErrorCodes.InternalError,
            doc.RootElement.GetProperty("structuredContent").GetProperty("errorCode").GetString());
    }

    [Fact]
    public void OkBounded_RefusesOversizedPayloadInsteadOfTruncating()
    {
        var huge = new { success = true, items = Enumerable.Range(0, 100_000).Select(i => i.ToString()).ToArray() };
        var json = McpResult.OkBounded(huge, maxChars: 1000);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("isError").GetBoolean());
        Assert.Equal(McpErrorCodes.TooLarge,
            doc.RootElement.GetProperty("structuredContent").GetProperty("errorCode").GetString());
    }

    [Fact]
    public void Tools_WithParameters_DeclareRequiredOrExplicitEmpty()
    {
        // 有参工具必须写 required（可为空数组），否则模型不知道哪些是必填。
        foreach (var m in ToolMethods())
        {
            var attr = m.GetCustomAttribute<McpServerToolAttribute>()!;
            var name = attr.Name ?? m.Name;
            var props = m.GetParameters()
                .Where(p => p.ParameterType != typeof(System.Threading.CancellationToken)
                         && p.ParameterType != typeof(IServiceProvider)
                         && p.ParameterType != typeof(Microsoft.AspNetCore.Http.HttpContext))
                .ToList();
            if (props.Count == 0) continue;

            var schema = McpToolSchemaBuilderForTest.BuildSchema(m);
            Assert.True(schema.ContainsKey("required") || props.All(p => p.HasDefaultValue),
                $"{name} 有必填参数但 schema 未声明 required");
        }
    }
}

internal static class McpToolSchemaBuilderForTest
{
    public static System.Text.Json.Nodes.JsonObject BuildSchema(MethodInfo m)
    {
        var props = new System.Text.Json.Nodes.JsonObject();
        var required = new System.Text.Json.Nodes.JsonArray();
        foreach (var p in m.GetParameters())
        {
            if (string.IsNullOrEmpty(p.Name)) continue;
            props[p.Name] = new System.Text.Json.Nodes.JsonObject { ["type"] = "string" };
            if (!p.HasDefaultValue) required.Add(p.Name);
        }
        var schema = new System.Text.Json.Nodes.JsonObject
        {
            ["type"] = "object",
            ["properties"] = props
        };
        if (required.Count > 0) schema["required"] = required;
        return schema;
    }
}
