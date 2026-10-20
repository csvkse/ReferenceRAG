namespace ReferenceRAG.Business.Features.Chat.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

/// <summary>
/// 本地大模型通用工具调用辅助类（支持 Qwen2.5 / DeepSeek / Llama3 / Phi / ChatML 协议）
/// 负责将 AITool 序列化注入 System Prompt，以及解析与拦截模型输出的 &lt;tool_call&gt; 结构。
/// </summary>
public static class ChatToolCallingHelper
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// 将 AITool 集合格式化为 ChatML / Nous-Hermes / Qwen2.5 官方标准 XML &lt;tools&gt; 描述块
    /// </summary>
    public static string FormatToolsPrompt(IList<AITool>? tools)
    {
        if (tools == null || tools.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.Append("\n# Tools\n\n");
        sb.Append("You may call one or more functions to assist with the user query. You are provided with function signatures within <tools></tools> XML tags:\n");
        sb.Append("<tools>\n");

        foreach (var tool in tools)
        {
            if (tool is AIFunction func)
            {
                var toolDef = new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = func.Name,
                        ["description"] = func.Description ?? string.Empty,
                        ["parameters"] = JsonSerializer.SerializeToNode(func.JsonSchema, _jsonOptions) ?? new JsonObject()
                    }
                };
                sb.Append(toolDef.ToJsonString(_jsonOptions)).Append('\n');
            }
        }

        sb.Append("</tools>\n\n");
        sb.Append("For each function call, return a json object with function name and arguments within <tool_call></tool_call> XML tags:\n");
        sb.Append("<tool_call>\n");
        sb.Append("{\"name\": <function-name>, \"arguments\": <args-json-object>}\n");
        sb.Append("</tool_call>\n\n");
        sb.Append("IMPORTANT INSTRUCTIONS:\n");
        sb.Append("1. When a user query requires searching knowledge, reading notes, or retrieving system index status, call the appropriate tool IMMEDIATELY using <tool_call> tags. Do not explain the tools to the user unless explicitly asked.\n");
        sb.Append("2. After receiving tool results (<tool_response>), you MUST synthesize the findings and answer the user in clear, polite, natural human language (Markdown format). Even if the user directly inputted a tool name or command (such as 'GetIndexStatus' or '调用: GetIndexStatus'), present the information helpfully in conversational text.\n");
        sb.Append("3. NEVER return raw JSON objects, JSON code blocks, or machine-readable dictionaries as your final response to the user.\n");
        sb.Append("4. NEVER output XML tags like <tool_call> or <tool_response> in your final answer to the user.\n");

        return sb.ToString();
    }

    /// <summary>
    /// 构建 ChatML 完整提示词（注入工具说明与历史对话，包含 Tool 返回结果的无缝对接，并执行严格的上下文预算保护与智能截断）
    /// </summary>
    public static string BuildChatMlPrompt(
        IList<ChatMessage> messages,
        IList<AITool>? tools = null,
        int maxContextTokens = 0,
        int maxOutputTokens = 1024)
    {
        // 1. 预算规划（若未指定 maxContextTokens，设定 8192 作为安全默认上限）
        int effectiveCtx = maxContextTokens > 0 ? maxContextTokens : 8192;
        int maxPromptTokens = Math.Max(512, effectiveCtx - Math.Clamp(maxOutputTokens, 128, 2048) - 64);

        // 字符预算折算：中文/英文混合保守折算（1 token ≈ 1.5 字符）
        int maxPromptChars = Math.Max(1024, (int)(maxPromptTokens * 1.5));
        int maxToolOutputChars = Math.Min(6000, Math.Max(1200, maxPromptChars / 2));
        int maxUserMessageChars = Math.Min(8000, Math.Max(1500, (int)(maxPromptChars * 0.75)));

        // 2. 格式化 System Prompt（包含 Tools 声明），System 部分拥有最高优先级
        string systemText = string.Empty;
        var nonSystemMessages = new List<ChatMessage>();
        bool hasExplicitSystem = false;

        foreach (var msg in messages)
        {
            if (msg.Role == ChatRole.System)
            {
                hasExplicitSystem = true;
                systemText = msg.Text ?? string.Empty;
            }
            else
            {
                nonSystemMessages.Add(msg);
            }
        }

        if (tools != null && tools.Count > 0)
        {
            var toolsPrompt = FormatToolsPrompt(tools);
            systemText = hasExplicitSystem ? $"{systemText}\n{toolsPrompt}" : $"你是 ReferenceRAG 智能助手。{toolsPrompt}";
        }
        else if (!hasExplicitSystem && string.IsNullOrWhiteSpace(systemText))
        {
            systemText = "你是 ReferenceRAG 智能助手。";
        }

        var systemBlock = $"<|im_start|>system\n{systemText}\n<|im_end|>\n";
        const string assistantTrigger = "<|im_start|>assistant\n";

        // 3. 计算留给非系统消息（历史与最新输入）的剩余字符预算
        int remainingChars = maxPromptChars - systemBlock.Length - assistantTrigger.Length;
        if (remainingChars < 512) remainingChars = 512;

        // 4. 从后往前组装消息（优先保留最新提问与工具返回结果）
        var formattedBlocks = new List<string>();
        int usedChars = 0;
        bool droppedEarlyMessages = false;

        for (int i = nonSystemMessages.Count - 1; i >= 0; i--)
        {
            var msg = nonSystemMessages[i];
            string blockText = FormatSingleMessage(msg, maxToolOutputChars, maxUserMessageChars);

            if (usedChars + blockText.Length <= remainingChars)
            {
                formattedBlocks.Add(blockText);
                usedChars += blockText.Length;
            }
            else
            {
                // 如果这是最新的一轮消息（i == nonSystemMessages.Count - 1），必须截断保留
                if (formattedBlocks.Count == 0)
                {
                    int budgetForLatest = Math.Max(256, remainingChars);
                    if (blockText.Length > budgetForLatest)
                    {
                        blockText = TruncateSingleBlock(msg.Role, blockText, budgetForLatest);
                    }
                    formattedBlocks.Add(blockText);
                    usedChars += blockText.Length;
                }
                else
                {
                    // 较早的历史轮次，舍弃并标记
                    droppedEarlyMessages = true;
                }
            }
        }

        // 5. 按照时间正序还原消息流
        formattedBlocks.Reverse();

        var sb = new StringBuilder();
        sb.Append(systemBlock);

        if (droppedEarlyMessages)
        {
            sb.Append("<|im_start|>system\n[提示：由于上下文窗口限制，已自动压缩并省略较早的若干轮历史对话]\n<|im_end|>\n");
        }

        foreach (var block in formattedBlocks)
        {
            sb.Append(block);
        }

        sb.Append(assistantTrigger);
        return sb.ToString();
    }

    private static string FormatSingleMessage(ChatMessage msg, int maxToolChars, int maxUserChars)
    {
        if (msg.Role == ChatRole.User)
        {
            var text = msg.Text ?? string.Empty;
            var dataContents = msg.Contents.OfType<DataContent>().ToList();
            var uriContents = msg.Contents.OfType<UriContent>().ToList();
            int totalImages = dataContents.Count + uriContents.Count;

            if (totalImages > 0)
            {
                var imgNotes = new List<string>();
                for (int i = 0; i < dataContents.Count; i++)
                {
                    var dc = dataContents[i];
                    imgNotes.Add($"[附带图像 #{i + 1} ({dc.MediaType ?? "image/jpeg"}), 本地文本引擎未挂载视觉投影器，已优先基于文本与知识库为您解答]");
                }
                for (int i = 0; i < uriContents.Count; i++)
                {
                    var uc = uriContents[i];
                    imgNotes.Add($"[附带图像 #{dataContents.Count + i + 1} ({uc.Uri}), 本地文本引擎未挂载视觉投影器，已优先基于文本与知识库为您解答]");
                }
                var notesBlock = string.Join("\n", imgNotes);
                text = string.IsNullOrWhiteSpace(text) ? notesBlock : $"{notesBlock}\n{text}";
            }

            if (text.Length > maxUserChars)
            {
                int cut = FindNaturalBreakpoint(text, maxUserChars);
                text = text.Substring(0, cut) + "\n... [用户输入内容超长已安全截断]";
                text = RepairTruncatedMarkdownAndXml(text);
            }

            return $"<|im_start|>user\n{text}\n<|im_end|>\n";
        }

        if (msg.Role == ChatRole.Assistant)
        {
            var text = msg.Text ?? string.Empty;
            var toolCalls = msg.Contents.OfType<FunctionCallContent>().ToList();
            if (toolCalls.Count > 0)
            {
                var tcSb = new StringBuilder();
                if (!string.IsNullOrWhiteSpace(text)) tcSb.AppendLine(text);
                foreach (var tc in toolCalls)
                {
                    var argsJson = tc.Arguments != null ? JsonSerializer.Serialize(tc.Arguments, _jsonOptions) : "{}";
                    tcSb.AppendLine($"<tool_call>\n{{\"name\": \"{tc.Name}\", \"arguments\": {argsJson}}}\n</tool_call>");
                }
                text = tcSb.ToString().TrimEnd();
            }

            return $"<|im_start|>assistant\n{text}\n<|im_end|>\n";
        }

        if (msg.Role == ChatRole.Tool)
        {
            var text = msg.Text ?? string.Empty;
            var funcResults = msg.Contents.OfType<FunctionResultContent>().ToList();
            if (funcResults.Count > 0)
            {
                var resSb = new StringBuilder();
                foreach (var fr in funcResults)
                {
                    var resStr = fr.Result?.ToString() ?? string.Empty;
                    if (resStr.Length > maxToolChars)
                    {
                        int cut = FindNaturalBreakpoint(resStr, maxToolChars);
                        resStr = resStr.Substring(0, cut) + "\n... [检索结果正文过长已自动截断以保护上下文]";
                        resStr = RepairTruncatedMarkdownAndXml(resStr);
                    }
                    resSb.AppendLine($"<tool_response>\n{resStr}\n</tool_response>");
                }
                text = resSb.ToString().TrimEnd();
            }
            else
            {
                if (text.Length > maxToolChars)
                {
                    int cut = FindNaturalBreakpoint(text, maxToolChars);
                    text = text.Substring(0, cut) + "\n... [检索结果正文过长已自动截断以保护上下文]";
                    text = RepairTruncatedMarkdownAndXml(text);
                }
                text = $"<tool_response>\n{text}\n</tool_response>";
            }

            return $"<|im_start|>user\n{text}\n<|im_end|>\n";
        }

        return $"<|im_start|>user\n{msg.Text ?? string.Empty}\n<|im_end|>\n";
    }

    private static string TruncateSingleBlock(ChatRole role, string blockText, int budgetChars)
    {
        string roleTag = role == ChatRole.Tool || role == ChatRole.User ? "user" : "assistant";
        string prefix = $"<|im_start|>{roleTag}\n";
        string suffix = "\n<|im_end|>\n";

        int overhead = prefix.Length + suffix.Length;
        int innerBudget = Math.Max(64, budgetChars - overhead);

        string inner = blockText;
        if (inner.StartsWith(prefix)) inner = inner.Substring(prefix.Length);
        if (inner.EndsWith(suffix)) inner = inner.Substring(0, inner.Length - suffix.Length);

        if (inner.Length > innerBudget)
        {
            int cut = FindNaturalBreakpoint(inner, innerBudget);
            inner = inner.Substring(0, cut) + "\n... [内容超长已截断]";
            inner = RepairTruncatedMarkdownAndXml(inner);
        }

        return $"{prefix}{inner}{suffix}";
    }

    /// <summary>
    /// 在语义边界（换行、句末标点）搜寻最佳截断点，避免在词句中间机械截断
    /// </summary>
    public static int FindNaturalBreakpoint(string text, int targetIndex, int lookbackRange = 150)
    {
        if (string.IsNullOrEmpty(text) || targetIndex >= text.Length) return text?.Length ?? 0;
        if (targetIndex <= 0) return 0;

        int minIndex = Math.Max(0, targetIndex - lookbackRange);
        char[] priorityDelimiters = ['\n', '。', '！', '？', ';', '；', '.'];

        for (int i = targetIndex; i >= minIndex; i--)
        {
            char c = text[i];
            if (Array.IndexOf(priorityDelimiters, c) >= 0)
            {
                return i + 1;
            }
        }

        int spaceIdx = text.LastIndexOf(' ', targetIndex, lookbackRange);
        if (spaceIdx >= minIndex)
        {
            return spaceIdx + 1;
        }

        return targetIndex;
    }

    /// <summary>
    /// 自愈修复截断导致的未配对 Markdown 代码块、标签与 XML 结构，防止前端排版崩溃或非法语法逃逸
    /// </summary>
    public static string RepairTruncatedMarkdownAndXml(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var sb = new StringBuilder(text);

        // 1. 代码块 ``` 奇偶自愈
        int codeBlockMatches = System.Text.RegularExpressions.Regex.Matches(text, "```").Count;
        if (codeBlockMatches % 2 != 0)
        {
            sb.Append("\n```\n");
        }

        // 2. <tool_call> 标签闭合自愈
        int tcOpen = System.Text.RegularExpressions.Regex.Matches(text, "<tool_call>", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count;
        int tcClose = System.Text.RegularExpressions.Regex.Matches(text, "</tool_call>", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count;
        if (tcOpen > tcClose)
        {
            sb.Append("\n</tool_call>\n");
        }

        // 3. <tool_response> 标签闭合自愈
        int trOpen = System.Text.RegularExpressions.Regex.Matches(text, "<tool_response>", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count;
        int trClose = System.Text.RegularExpressions.Regex.Matches(text, "</tool_response>", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count;
        if (trOpen > trClose)
        {
            sb.Append("\n</tool_response>\n");
        }

        return sb.ToString();
    }

    /// <summary>
    /// 从生成文本中提取潜在的工具调用（支持 &lt;tool_call&gt;、[TOOL_CALLS] 与 Markdown json 语法）
    /// </summary>
    public static (bool Success, string? Name, IDictionary<string, object?>? Arguments, string RemainingText) TryParseToolCall(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return (false, null, null, text);

        // 1. 尝试匹配 <tool_call>...</tool_call>
        int startIdx = text.IndexOf("<tool_call>", StringComparison.OrdinalIgnoreCase);
        if (startIdx >= 0)
        {
            int endIdx = text.IndexOf("</tool_call>", startIdx, StringComparison.OrdinalIgnoreCase);
            if (endIdx > startIdx)
            {
                string jsonBlock = text.Substring(startIdx + 11, endIdx - (startIdx + 11)).Trim();
                string prefix = text.Substring(0, startIdx).Trim();
                string suffix = text.Substring(endIdx + 12).Trim();
                string remaining = string.IsNullOrWhiteSpace(prefix) ? suffix : $"{prefix}\n{suffix}".Trim();

                if (TryParseFunctionJson(jsonBlock, out var name, out var args))
                {
                    return (true, name, args, remaining);
                }
            }
        }

        // 2. 尝试匹配 [TOOL_CALLS] [...] 格式（Mistral / Phi 格式）
        int mistralIdx = text.IndexOf("[TOOL_CALLS]", StringComparison.OrdinalIgnoreCase);
        if (mistralIdx >= 0)
        {
            string jsonBlock = text.Substring(mistralIdx + 12).Trim();
            if (TryParseFunctionJson(jsonBlock, out var name, out var args))
            {
                string remaining = text.Substring(0, mistralIdx).Trim();
                return (true, name, args, remaining);
            }
        }

        return (false, null, null, text);
    }

    private static bool TryParseFunctionJson(string jsonStr, out string? name, out IDictionary<string, object?>? arguments)
    {
        name = null;
        arguments = null;

        try
        {
            // 去除 markdown 代码块包裹 ```json ... ```
            jsonStr = jsonStr.Trim();
            if (jsonStr.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
            {
                jsonStr = jsonStr.Substring(7);
            }
            if (jsonStr.StartsWith("```"))
            {
                jsonStr = jsonStr.Substring(3);
            }
            if (jsonStr.EndsWith("```"))
            {
                jsonStr = jsonStr.Substring(0, jsonStr.Length - 3);
            }
            jsonStr = jsonStr.Trim();

            // 若包裹在数组中 [{ "name": ... }]
            if (jsonStr.StartsWith("[") && jsonStr.EndsWith("]"))
            {
                var arrayNode = JsonNode.Parse(jsonStr)?.AsArray();
                if (arrayNode != null && arrayNode.Count > 0 && arrayNode[0] is JsonObject firstObj)
                {
                    return ExtractFromJsonObject(firstObj, out name, out arguments);
                }
            }

            var node = JsonNode.Parse(jsonStr)?.AsObject();
            if (node != null)
            {
                return ExtractFromJsonObject(node, out name, out arguments);
            }
        }
        catch
        {
            // 忽略非合法 JSON
        }

        return false;
    }

    private static bool ExtractFromJsonObject(JsonObject obj, out string? name, out IDictionary<string, object?>? arguments)
    {
        name = null;
        arguments = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        // 标准字段提取: name / function / arguments / parameters
        if (obj.TryGetPropertyValue("name", out var nameVal) && nameVal != null)
        {
            name = nameVal.GetValue<string>();
        }
        else if (obj.TryGetPropertyValue("function", out var funcNode) && funcNode is JsonObject funcObj && funcObj.TryGetPropertyValue("name", out var fNameVal))
        {
            name = fNameVal?.GetValue<string>();
            if (funcObj.TryGetPropertyValue("arguments", out var fArgsNode))
            {
                obj = funcObj;
            }
        }

        if (string.IsNullOrWhiteSpace(name)) return false;

        JsonNode? argsNode = null;
        if (obj.TryGetPropertyValue("arguments", out var aNode)) argsNode = aNode;
        else if (obj.TryGetPropertyValue("parameters", out var pNode)) argsNode = pNode;

        if (argsNode is JsonObject argsObj)
        {
            foreach (var kvp in argsObj)
            {
                arguments[kvp.Key] = kvp.Value?.ToString();
            }
        }
        else if (argsNode != null)
        {
            var rawStr = argsNode.ToString();
            try
            {
                var parsedObj = JsonNode.Parse(rawStr)?.AsObject();
                if (parsedObj != null)
                {
                    foreach (var kvp in parsedObj)
                    {
                        arguments[kvp.Key] = kvp.Value?.ToString();
                    }
                }
            }
            catch
            {
                arguments["input"] = rawStr;
            }
        }

        return true;
    }

    /// <summary>
    /// 对助手回复进行后处理过滤与自愈清洗（剥离幻觉伪词元如 &lt;nlink...&gt;，若小模型输出了裸 JSON 状态结构则自动规整为清晰的 Markdown 自然语言）
    /// </summary>
    public static string SanitizeAssistantResponse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        // 1. 清理 hallucinated 畸形分词标记（如 <nlinkcount, <nlink..., <unk>, <pad>）
        var cleaned = System.Text.RegularExpressions.Regex.Replace(text, @"<\s*nlink\w*[^>]*>?", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"<\s*(?:unk|pad)\s*>", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // 2. 检查是否输出了裸 JSON 状态结构（常见于小参数模型对 GetIndexStatus 的过度格式模仿）
        var trimmed = cleaned.Trim();
        string candidateJson = trimmed;
        if (candidateJson.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
        {
            candidateJson = candidateJson.Substring(7);
            if (candidateJson.EndsWith("```")) candidateJson = candidateJson.Substring(0, candidateJson.Length - 3);
            candidateJson = candidateJson.Trim();
        }
        else if (candidateJson.StartsWith("```") && candidateJson.EndsWith("```"))
        {
            candidateJson = candidateJson.Substring(3, candidateJson.Length - 6).Trim();
        }

        if (candidateJson.StartsWith("{") && (candidateJson.Contains("status", StringComparison.OrdinalIgnoreCase) || candidateJson.Contains("BM25") || candidateJson.Contains("分块") || candidateJson.Contains("向量")))
        {
            try
            {
                using var doc = JsonDocument.Parse(candidateJson);
                var root = doc.RootElement;
                JsonElement statusElem = root;
                if (root.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.Object)
                {
                    statusElem = s;
                }

                int? vectorCount = null;
                int? bm25Count = null;

                foreach (var prop in statusElem.EnumerateObject())
                {
                    var pName = prop.Name;
                    if (pName.Contains("向量") || pName.Contains("分块") || pName.Contains("vector", StringComparison.OrdinalIgnoreCase))
                    {
                        if (prop.Value.TryGetInt32(out var v)) vectorCount = v;
                        else if (int.TryParse(prop.Value.GetString(), out var v2)) vectorCount = v2;
                    }
                    else if (pName.Contains("BM25") || pName.Contains("文档") || pName.Contains("doc", StringComparison.OrdinalIgnoreCase))
                    {
                        if (prop.Value.TryGetInt32(out var b)) bm25Count = b;
                        else if (int.TryParse(prop.Value.GetString(), out var b2)) bm25Count = b2;
                    }
                }

                if (vectorCount.HasValue || bm25Count.HasValue)
                {
                    return $"知识库当前已成功构建索引：包含 **{vectorCount ?? 0}** 个向量分块和 **{bm25Count ?? 0}** 个 BM25 检索文档。";
                }
            }
            catch
            {
                // 若 JSON 畸形（如截断缺失闭合括号），尝试通过正则提取数值并自愈
                var bmMatch = System.Text.RegularExpressions.Regex.Match(candidateJson, @"BM25[^\d]*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                var vecMatch = System.Text.RegularExpressions.Regex.Match(candidateJson, @"(?:向量|分块)[^\d]*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                if (bmMatch.Success || vecMatch.Success)
                {
                    var bmVal = bmMatch.Success ? bmMatch.Groups[1].Value : "0";
                    var vecVal = vecMatch.Success ? vecMatch.Groups[1].Value : "0";
                    return $"知识库当前已成功构建索引：包含 **{vecVal}** 个向量分块和 **{bmVal}** 个 BM25 检索文档。";
                }
            }
        }

        return cleaned;
    }
}
