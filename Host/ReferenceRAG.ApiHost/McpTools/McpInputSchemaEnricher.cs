using System.ComponentModel;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using McpHelper.McpTools;
using ModelContextProtocol.Server;

namespace ReferenceRAG.Service.McpTools;

/// <summary>
/// MCP 工具 inputSchema 补齐器。
///
/// 背景：McpHelper.Core 的 SSE 适配层只读取 <see cref="McpServerToolAttribute"/> 标记与
/// <see cref="DescriptionAttribute"/> 文本，**不反射参数生成 JSON Schema**，
/// 导致 tools/list 返回的每个工具都是 `{"type":"object","properties":{}}`——
/// 客户端据此认为工具不接受任何参数，模型无法得知必填项与默认值（MCP 规范要求
/// inputSchema 必须是合法的 JSON Schema，无参工具应写 additionalProperties:false）。
///
/// 做法：注册完成后就地改写 <see cref="McpToolDescriptor.InputSchema"/>（该属性公开可写，
/// 且描述符在 GetTools/GetAllTools 之间是同一实例），不改第三方包。
/// </summary>
public static partial class McpInputSchemaEnricher
{
    [GeneratedRegex(@"(?<!^)(?=[A-Z])")]
    private static partial Regex PascalToSnakeRegex();

    /// <summary>方法名 → 虚线/下划线名（与 McpHelper 的 DeriveToolName 一致）。</summary>
    public static string ToSnakeCase(string name) =>
        PascalToSnakeRegex().Replace(name, "_").ToLowerInvariant();

    /// <summary>
    /// 扫描程序集内所有 <see cref="McpServerToolTypeAttribute"/> 声明的工具方法，为已注册的描述符补齐 schema。
    /// 未被注册的类型不会造成副作用（只是找不到同名描述符）。
    /// </summary>
    public static McpSchemaEnrichResult Enrich(IMcpToolRegistry registry, Assembly? assembly = null)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var methodMap = BuildMethodMap(assembly ?? typeof(McpInputSchemaEnricher).Assembly);
        var enriched = new List<string>();
        var missing = new List<string>();

        foreach (var (name, descriptor) in registry.GetAllTools())
        {
            if (methodMap.TryGetValue(name, out var method))
            {
                descriptor.InputSchema = BuildSchema(method);
                enriched.Add(name);
            }
            else
            {
                missing.Add(name);
            }
        }

        return new McpSchemaEnrichResult(enriched, missing);
    }

    private static Dictionary<string, MethodInfo> BuildMethodMap(Assembly assembly)
    {
        var map = new Dictionary<string, MethodInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in assembly.GetTypes())
        {
            if (type.GetCustomAttribute<McpServerToolTypeAttribute>() is null) continue;
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
            {
                if (method.GetCustomAttribute<McpServerToolAttribute>() is null) continue;
                var name = ToSnakeCase(method.Name);
                map.TryAdd(name, method);
            }
        }
        return map;
    }

    /// <summary>
    /// 由方法签名生成 JSON Schema。无参（或仅含框架注入参数）时输出
    /// <c>{"type":"object","additionalProperties":false}</c>，符合 MCP 规范对无参工具的要求。
    /// </summary>
    public static JsonObject BuildSchema(MethodInfo method)
    {
        var properties = new JsonObject();
        var required = new JsonArray();

        foreach (var p in method.GetParameters())
        {
            if (IsFrameworkInjected(p.ParameterType)) continue;

            var property = new JsonObject { ["type"] = MapJsonType(p.ParameterType) };
            var description = p.GetCustomAttribute<DescriptionAttribute>()?.Description;
            if (!string.IsNullOrWhiteSpace(description)) property["description"] = description;

            var arrayItemType = GetArrayItemType(p.ParameterType);
            if (arrayItemType is not null)
            {
                property["items"] = new JsonObject { ["type"] = MapJsonType(arrayItemType) };
            }

            if (p.HasDefaultValue && p.DefaultValue is not null and not DBNull)
            {
                property["default"] = JsonValue.Create(p.DefaultValue);
            }
            else if (!p.HasDefaultValue)
            {
                required.Add(p.Name);
            }

            properties[p.Name!] = property;
        }

        if (properties.Count == 0)
        {
            return new JsonObject { ["type"] = "object", ["additionalProperties"] = false };
        }

        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Count > 0) schema["required"] = required;
        return schema;
    }

    private static bool IsFrameworkInjected(Type type) =>
        type == typeof(CancellationToken) ||
        type == typeof(IServiceProvider) ||
        type == typeof(McpServer);

    private static Type? GetArrayItemType(Type type)
    {
        if (type == typeof(string)) return null;
        if (type.IsArray) return type.GetElementType();
        var generic = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)
            ? type.GetGenericArguments()[0] : null;
        return generic;
    }

    private static string MapJsonType(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (underlying.IsArray) return "array";
        if (underlying == typeof(string) || underlying == typeof(char) || underlying.IsEnum) return "string";
        if (underlying == typeof(bool)) return "boolean";
        if (underlying == typeof(int) || underlying == typeof(long) || underlying == typeof(short)
            || underlying == typeof(byte) || underlying == typeof(uint) || underlying == typeof(ulong))
            return "integer";
        if (underlying == typeof(double) || underlying == typeof(float) || underlying == typeof(decimal))
            return "number";
        return underlying.IsGenericType && underlying.GetGenericTypeDefinition() == typeof(List<>)
            ? "array" : "string";
    }
}

/// <summary>补齐结果：已补齐的工具名与未找到方法映射的工具名（后者需人工排查）。</summary>
public sealed record McpSchemaEnrichResult(
    IReadOnlyList<string> Enriched,
    IReadOnlyList<string> MissingMethodMapping)
{
    public int EnrichedCount => Enriched.Count;
    public int MissingCount => MissingMethodMapping.Count;
}
