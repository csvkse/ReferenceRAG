using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using McpHelper.McpTools;
using Microsoft.Extensions.DependencyInjection;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Service.McpTools;
using Xunit;

namespace ReferenceRAG.Host.Tests;

/// <summary>
/// G-M1 / G-M2 / G-M7 / G-M8：工具契约面门禁。
///
/// 背景（生产实测）：已声明的 7 个 outputSchema 全部不合规（Dictionary 反射出
/// count/capacity 且标 required、可空字段声明成 string、失败信封不在 schema 内）；
/// 未知参数被静默忽略（knowledgeBaseId 单数被吞后检索了全部库）；
/// 描述里引用了不存在的工具名 kb_list。
/// </summary>
public sealed class McpToolContractSemanticsGateTests
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
        return (IMcpToolRegistry)sp.GetService(
            typeof(McpToolRegistry).Assembly.GetTypes().First(t => t.Name == "IMcpToolRegistry"))!;
    }

    private static IEnumerable<MethodInfo> ToolMethods() =>
        typeof(McpResult).Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<ModelContextProtocol.Server.McpServerToolTypeAttribute>() is not null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .Where(m => m.GetCustomAttribute<ModelContextProtocol.Server.McpServerToolAttribute>() is not null);

    private static string ToolName(MethodInfo m) =>
        m.GetCustomAttribute<ModelContextProtocol.Server.McpServerToolAttribute>()?.Name
        ?? System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(m.Name);

    [Fact]
    public void EveryTool_DeclaresHumanReadableTitle()
    {
        // 协议 title 可选，但客户端 UI 需要它；此前 25 个工具 0 个有 title。
        var missing = ToolMethods()
            .Where(m => string.IsNullOrWhiteSpace(
                m.GetCustomAttribute<ModelContextProtocol.Server.McpServerToolAttribute>()?.Title))
            .Select(ToolName)
            .ToList();

        Assert.True(missing.Count == 0, "以下工具缺 Title：" + string.Join(", ", missing));
    }

    [Fact]
    public void Descriptions_OnlyReferenceExistingToolNames()
    {
        var names = ToolMethods().Select(ToolName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 示例占位符与错误码不是工具名，显式白名单。
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "kb_a", "kb_b", "kb_1", "kb_2", "kbid_1", "docs", "src",
            "too_large", "invalid_argument", "path_denied", "forbidden",
            "not_found", "chunk_not_found", "document_not_found",
            "source_not_found", "connector_not_found", "knowledge_base_not_found",
            "search_failed", "service_unavailable", "internal_error",
            "no_knowledge_base", "knowledge_base_unavailable", "unknown_tool"
        };

        var offenders = new List<string>();
        foreach (var m in ToolMethods())
        {
            var blobs = new List<string>();
            var desc = m.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description;
            if (desc is not null) blobs.Add(desc);
            foreach (var p in m.GetParameters())
            {
                var pd = p.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description;
                if (pd is not null) blobs.Add(pd);
            }

            foreach (var blob in blobs)
            {
                foreach (Match hit in Regex.Matches(blob, @"[a-z0-9]+(?:_[a-z0-9]+)+"))
                {
                    var token = hit.Value;
                    if (!names.Contains(token) && !allowed.Contains(token))
                    {
                        offenders.Add($"{ToolName(m)} → '{token}'");
                    }
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "描述引用了不存在的工具名：" + string.Join("; ", offenders.Distinct()));
    }

    [Fact]
    public void ToolCatalog_PromptBudget_StaysWithinCeiling()
    {
        // 全量 augment 的实测代价是步数 +67%、16.7% 场景回归：描述预算是硬上限，不是目标。
        var prompts = McpCatalogRegistry.GetCatalog()
            .SelectMany(c => c.Tools)
            .Select(t => t.ComposePrompt())
            .ToList();

        var total = prompts.Sum(p => System.Text.Encoding.UTF8.GetByteCount(p));
        Assert.True(total <= 12_000, $"工具描述总字节 {total} 超过 12 KB 上限");
        Assert.True(prompts.Max(p => p.Length) <= 600,
            $"单条描述超过 600 字符：{prompts.OrderByDescending(p => p.Length).First()[..80]}…");
    }

    [Fact]
    public async System.Threading.Tasks.Task UnknownArguments_AreRejected()
    {
        var registry = BuildRegistry();

        var cases = new (string Tool, string BadArg)[]
        {
            ("semantic_search", "topk"),
            ("semantic_search", "knowledgeBaseId"),
            ("list_files", "kbIds"),
            ("find_files_by_pattern", "source"),
            ("get_file_tree", "depth"),
        };

        foreach (var (tool, badArg) in cases)
        {
            var args = new Dictionary<string, JsonNode?> { [badArg] = JsonValue.Create("x") };
            var result = await registry.InvokeToolAsync(tool, args, default);
            var raw = result?.ToJsonString() ?? string.Empty;
            // JsonNode 会把中文转义成 \uXXXX：断言前取文本块原值。
            var text = result?["content"]?[0]?["text"]?.GetValue<string>() ?? raw;

            Assert.True(result?["isError"]?.GetValue<bool>() == true,
                $"{tool} 收到未知参数 '{badArg}' 仍返回成功：{raw}");
            Assert.Contains("schema 未声明的参数", text);
            Assert.Contains("可用参数", text);
        }
    }

    [Fact]
    public void OutputSchemas_AcceptSuccessAndFailureShapes()
    {
        var registry = BuildRegistry();
        var offenders = new List<string>();

        foreach (var (name, descriptor) in registry.GetAllTools())
        {
            var schema = descriptor.OutputSchema;
            if (schema is null) continue;

            // 规定形态：anyOf[成功, 失败]，因此不能再无条件要求成功字段必填。
            if (schema["anyOf"] is not JsonArray variants || variants.Count < 2)
            {
                offenders.Add($"{name}: outputSchema 未同时建模成功与失败形态");
                continue;
            }

            var failureRequired = ((variants[1] as JsonObject)?["required"] as JsonArray)?
                .Select(n => n!.GetValue<string>()).ToList() ?? new List<string>();
            if (!failureRequired.Contains("success") || !failureRequired.Contains("errorCode"))
            {
                offenders.Add($"{name}: 失败形态未要求 success/errorCode");
            }
        }

        Assert.True(offenders.Count == 0, string.Join("; ", offenders));
    }

    [Fact]
    public void OutputSchemas_DoNotReflectDictionaryInternals()
    {
        var registry = BuildRegistry();
        var offenders = new List<string>();

        foreach (var (name, descriptor) in registry.GetAllTools())
        {
            var json = descriptor.OutputSchema?.ToJsonString() ?? string.Empty;
            if (json.Contains("\"capacity\"") || json.Contains("\"comparer\"") || json.Contains("\"values\""))
            {
                offenders.Add(name);
            }
        }

        Assert.True(offenders.Count == 0,
            "以下 outputSchema 仍把 Dictionary 内部结构当成字段：" + string.Join(", ", offenders));
    }
}
