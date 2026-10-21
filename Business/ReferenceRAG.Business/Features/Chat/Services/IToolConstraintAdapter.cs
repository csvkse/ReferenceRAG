namespace ReferenceRAG.Business.Features.Chat.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

/// <summary>
/// 目标后端架构
/// </summary>
public enum ToolConstraintBackend
{
    /// <summary>
    /// 本地 GGUF / llama.cpp (GBNF 语法树)
    /// </summary>
    LocalGguf,

    /// <summary>
    /// 本地 ONNX / ONNX Runtime GenAI (JSON Schema / llguidance)
    /// </summary>
    LocalOnnx,

    /// <summary>
    /// 外部云端 API (OpenAI strict: true / response_format)
    /// </summary>
    ExternalApi
}

/// <summary>
/// 工具约束适配器接口（适配器模式 Adapter Pattern）
/// 屏蔽底层异构引擎在结构化输出、语法剪枝与模式约束上的格式差异。
/// </summary>
public interface IToolConstraintAdapter
{
    ToolConstraintBackend Backend { get; }

    /// <summary>
    /// 将跨平台的通用 AITool 契约集合适配为底层专有的语法约束指令
    /// </summary>
    object? AdaptConstraint(IList<AITool>? tools, string mode = "ToolCallOrText");
}

/// <summary>
/// GGUF / llama.cpp GBNF 语法适配器
/// 负责动态生成上下文无关文法（CFG），在 Token 采样层强制闭合标签与合法 JSON 结构。
/// </summary>
public class LlamaGbnfConstraintAdapter : IToolConstraintAdapter
{
    public ToolConstraintBackend Backend => ToolConstraintBackend.LocalGguf;

    public object? AdaptConstraint(IList<AITool>? tools, string mode = "ToolCallOrText")
    {
        if (tools == null || tools.Count == 0) return null;

        var funcs = tools.OfType<AIFunction>().ToList();
        if (funcs.Count == 0) return null;

        var sb = new StringBuilder();
        var funcNames = string.Join(" | ", funcs.Select(f => $"\"{f.Name}\""));

        if (mode == "StrictToolCallOnly")
        {
            sb.AppendLine("root ::= tool-call");
        }
        else
        {
            // 关键逻辑设计：联合文法（Union Grammar），允许模型在工具调用与自然文本间分支，防止死锁
            sb.AppendLine("root ::= tool-call | text-response");
        }

        sb.AppendLine("tool-call ::= \"<tool_call>\\n\" ws json-obj ws \"</tool_call>\"");
        sb.AppendLine($"tool-name ::= {funcNames}");
        sb.AppendLine("json-obj ::= \"{\\n\" ws \"\\\"name\\\":\\\"\" tool-name \"\\\",\\n\" ws \"\\\"arguments\\\":\\\"\" ws json-args ws \"\\\"\\n}\"");
        sb.AppendLine("json-args ::= \"{\\\"\" [^\"\\n]* \"\\\"}\" | \"{}\"");
        sb.AppendLine("text-response ::= [^<]+");
        sb.AppendLine("ws ::= [ \\t\\n]*");

        return sb.ToString();
    }
}

/// <summary>
/// ONNX Runtime GenAI Schema 适配器
/// 动态生成 OGA 与 llguidance 原生支持的 JSON Schema 字符串
/// </summary>
public class OnnxSchemaConstraintAdapter : IToolConstraintAdapter
{
    public ToolConstraintBackend Backend => ToolConstraintBackend.LocalOnnx;

    public object? AdaptConstraint(IList<AITool>? tools, string mode = "ToolCallOrText")
    {
        if (tools == null || tools.Count == 0) return null;

        var funcs = tools.OfType<AIFunction>().ToList();
        if (funcs.Count == 0) return null;

        var schemas = new JsonArray();
        foreach (var func in funcs)
        {
            var node = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["name"] = new JsonObject { ["type"] = "string", ["const"] = func.Name },
                    ["arguments"] = JsonSerializer.SerializeToNode(func.JsonSchema) ?? new JsonObject()
                },
                ["required"] = new JsonArray { "name", "arguments" }
            };
            schemas.Add(node);
        }

        var root = new JsonObject
        {
            ["$schema"] = "http://json-schema.org/draft-07/schema#",
            ["anyOf"] = schemas
        };

        return root.ToJsonString();
    }
}

/// <summary>
/// 外部 API 严格模式适配器
/// 适配 OpenAI 兼容标准接口的 "strict: true" 与 structured outputs 配置
/// </summary>
public class ExternalApiStrictConstraintAdapter : IToolConstraintAdapter
{
    public ToolConstraintBackend Backend => ToolConstraintBackend.ExternalApi;

    public object? AdaptConstraint(IList<AITool>? tools, string mode = "ToolCallOrText")
    {
        if (tools == null || tools.Count == 0) return null;

        var options = new ChatOptions
        {
            Tools = [.. tools]
        };

        // 服务端严格结构化标记（可在序列化为 HTTP Payload 时注入 strict: true）
        options.AdditionalProperties ??= new();
        options.AdditionalProperties["strict_tools"] = true;

        return options;
    }
}
