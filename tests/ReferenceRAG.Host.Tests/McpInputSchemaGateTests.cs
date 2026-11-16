using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using McpHelper.McpTools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Service.McpTools;
using Xunit;

namespace ReferenceRAG.Host.Tests;

/// <summary>
/// G-MCP-1 / G-MCP-3 门禁：MCP tools/list 契约合规。
///
/// 背景：McpHelper.Core 的 SSE 适配层只读 [Description] 文本、不反射参数，
/// 导致每个工具的 inputSchema 都是空 properties —— 客户端据此认为工具无参数，
/// 模型无法得知必填字段。本门禁锁定：所有已注册工具都必须带合法 schema，
/// 且目录（权限矩阵）与实际注册工具集一致。
/// </summary>
public sealed class McpInputSchemaGateTests
{
    private static IMcpToolRegistry BuildRegistry()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        McpHelper.Extensions.McpToolRegistryExtensions.AddMcpToolRegistry(services, registry =>
        {
            registry.RegisterLocalTool<RagSearchTools>();
            registry.RegisterLocalTool<EmbeddingTools>();
            registry.RegisterLocalTool<KnowledgeFileTools>();
            registry.RegisterLocalTool<GraphSearchTools>();
            registry.RegisterLocalTool<IndexStatusTools>();
            registry.RegisterLocalTool<SourceManagementTools>();
            registry.RegisterLocalTool<KnowledgeBaseMcpTools>();
            registry.RegisterLocalTool<TestTools>();
        });
        var sp = services.BuildServiceProvider();
        var registry = (IMcpToolRegistry)sp.GetService(
            typeof(McpToolRegistry).Assembly.GetTypes().First(t => t.Name == "IMcpToolRegistry"))!;

        McpInputSchemaEnricher.Enrich(registry);
        return registry;
    }

    [Fact]
    public void EveryRegisteredTool_HasValidInputSchema()
    {
        var registry = BuildRegistry();
        var tools = registry.GetAllTools();
        Assert.NotEmpty(tools);

        var invalid = new List<string>();
        foreach (var (name, descriptor) in tools)
        {
            var schema = descriptor.InputSchema;
            if (schema is null)
            {
                invalid.Add($"{name}: schema=null");
                continue;
            }

            var type = schema["type"]?.GetValue<string>();
            if (type != "object")
            {
                invalid.Add($"{name}: type='{type}'");
                continue;
            }

            var properties = schema["properties"] as System.Text.Json.Nodes.JsonObject;
            if (properties is null)
            {
                // 无参工具的合规形态：显式 additionalProperties:false（MCP 规范推荐写法）
                if (schema["additionalProperties"]?.GetValue<bool>() != false)
                {
                    invalid.Add($"{name}: no properties and additionalProperties not false");
                }
                continue;
            }

            // 无参工具必须显式声明 additionalProperties:false（MCP 规范对无参工具的要求）
            if (properties.Count == 0 && schema["additionalProperties"]?.GetValue<bool>() != false)
            {
                invalid.Add($"{name}: empty properties but additionalProperties not false");
            }
        }

        Assert.True(invalid.Count == 0,
            "以下工具的 inputSchema 不合规：" + string.Join("; ", invalid));
    }

    [Fact]
    public void ToolsWithRequiredParameters_DeclareThemInRequiredArray()
    {
        var registry = BuildRegistry();
        var mismatched = new List<string>();

        foreach (var (name, descriptor) in registry.GetAllTools())
        {
            var method = FindMethodForTool(name);
            if (method is null) continue;

            var nonDefaultParams = method.GetParameters()
                .Where(p => !p.HasDefaultValue
                            && p.ParameterType != typeof(CancellationToken)
                            && p.ParameterType != typeof(IServiceProvider)
                            && p.ParameterType != typeof(ModelContextProtocol.Server.McpServer))
                .Select(p => p.Name!)
                .ToList();

            var required = (descriptor.InputSchema?["required"] as System.Text.Json.Nodes.JsonArray)?
                .Select(n => n!.GetValue<string>()).ToList() ?? new List<string>();

            if (!nonDefaultParams.OrderBy(x => x).SequenceEqual(required.OrderBy(x => x)))
            {
                mismatched.Add($"{name}: expected=[{string.Join(",", nonDefaultParams)}] actual=[{string.Join(",", required)}]");
            }
        }

        Assert.True(mismatched.Count == 0,
            "required 数组与方法签名不一致：" + string.Join("; ", mismatched));
    }

    [Fact]
    public void CatalogToolNames_MatchRegisteredTools()
    {
        var registry = BuildRegistry();
        var registered = registry.GetAllTools().Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var catalogNames = ReferenceRAG.Core.Services.McpCatalogRegistry.GetCatalog()
            .SelectMany(c => c.Tools)
            .Select(t => McpInputSchemaEnricher.ToSnakeCase(t.Name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missingInRegistry = catalogNames.Where(n => !registered.Contains(n)).ToList();
        var missingInCatalog = registered.Where(n => !catalogNames.Contains(n)).ToList();

        Assert.True(missingInRegistry.Count == 0,
            "目录声明但未注册（幽灵工具）：" + string.Join(", ", missingInRegistry));
        Assert.True(missingInCatalog.Count == 0,
            "已注册但目录缺失（权限矩阵无法授权）：" + string.Join(", ", missingInCatalog));
    }

    [Fact]
    public void ToSnakeCase_MatchesMcpHelperNaming()
    {
        Assert.Equal("semantic_search", McpInputSchemaEnricher.ToSnakeCase("SemanticSearch"));
        Assert.Equal("knowledge_base_drill_down", McpInputSchemaEnricher.ToSnakeCase("KnowledgeBaseDrillDown"));
        Assert.Equal("ping", McpInputSchemaEnricher.ToSnakeCase("Ping"));
        Assert.Equal("graph_search_by_title", McpInputSchemaEnricher.ToSnakeCase("GraphSearchByTitle"));
    }

    [Theory]
    [InlineData("list_sources", "knowledgeBaseIds")]
    [InlineData("list_files", "knowledgeBaseIds")]
    [InlineData("get_document_info", "knowledgeBaseId")]
    public void IndexAndSourceTools_ExposeKnowledgeBaseScope(string toolName, string parameter)
    {
        // v2 门禁：这些工具曾只能读旧单库，Agent 无法按库过滤或定位 KB 切片。
        var registry = BuildRegistry();
        var descriptor = registry.GetAllTools()[toolName];
        var props = descriptor.InputSchema!["properties"] as System.Text.Json.Nodes.JsonObject;
        Assert.NotNull(props);
        Assert.True(props!.ContainsKey(parameter), $"{toolName} 缺少 {parameter} 参数");
        Assert.Equal("string", props[parameter]!["type"]?.GetValue<string>());
    }

    private static MethodInfo? FindMethodForTool(string snakeName)
    {
        var assembly = typeof(McpInputSchemaEnricher).Assembly;
        foreach (var type in assembly.GetTypes())
        {
            if (type.GetCustomAttribute<ModelContextProtocol.Server.McpServerToolTypeAttribute>() is null) continue;
            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
            {
                if (m.GetCustomAttribute<ModelContextProtocol.Server.McpServerToolAttribute>() is null) continue;
                if (McpInputSchemaEnricher.ToSnakeCase(m.Name) == snakeName) return m;
            }
        }
        return null;
    }
}
