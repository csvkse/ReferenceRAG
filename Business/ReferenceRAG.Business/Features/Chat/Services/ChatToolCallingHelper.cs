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
    public static string FormatToolsPrompt(IList<AITool>? tools, bool supportsVision = false)
    {
        if (tools == null || tools.Count == 0) return string.Empty;

        var sb = new StringBuilder();

        if (!supportsVision)
        {
            sb.Append("\n## 内置专业能力画像 (Capability Manifest)\n");
            sb.Append("你是 ReferenceRAG 智能助手，已就绪并内置挂载以下专业工具：\n");
            foreach (var tool in tools)
            {
                if (tool is AIFunction func)
                {
                    sb.Append($"- **`{func.Name}`**：{func.Description}\n");
                }
            }
            sb.Append("【能力自知准则】：当用户询问你有什么工具、具备什么能力、或要求介绍当前功能时，请直接依据上述已挂载工具如实回答，切勿声称“没有内置知识库工具”或臆造第三方未连接服务。\n\n");
        }

        sb.Append("# Tools\n\n");
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

        if (!supportsVision)
        {
            sb.Append("# Two-Phase Tool Execution Protocol:\n");
            sb.Append("Phase 1 (Tool Call): When a user query requires searching knowledge, reading notes, retrieving system index status, or introducing/listing available tools (e.g. \"介绍当前工具\", \"有哪些工具\"), call the appropriate tool IMMEDIATELY using <tool_call> tags. Do not explain the tools to the user unless explicitly asked.\n");
            sb.Append("Example:\n");
            sb.Append("User: 查询知识库状态\n");
            sb.Append("Assistant: <tool_call>\n");
            sb.Append("{\"name\": \"GetIndexStatus\", \"arguments\": {}}\n");
            sb.Append("</tool_call>\n\n");
            sb.Append("Example 2:\n");
            sb.Append("User: 介绍当前工具\n");
            sb.Append("Assistant: <tool_call>\n");
            sb.Append("{\"name\": \"GetAvailableTools\", \"arguments\": {}}\n");
            sb.Append("</tool_call>\n\n");
            sb.Append("Phase 2 (Final Answer): After receiving tool results (<tool_response>), synthesize the findings and answer the user in clear, polite, natural human language (Markdown format).\n");
            sb.Append("CRITICAL RULES FOR PHASE 2:\n");
            sb.Append("1. Base your answer STRICTLY on the tool results provided in <tool_response>.\n");
            sb.Append("2. If the tool response indicates no matching content was found (\"未找到相关内容\" or \"未检索到\"), directly and politely inform the user that no relevant notes/documents were found in the knowledge base, and suggest more specific business/technical keywords.\n");
            sb.Append("3. NEVER assume the user is just saying hello or testing command syntax.\n");
            sb.Append("4. NEVER lecture or educate the user about command syntax, formats, or how to use \"搜索: xxx\".\n");
            sb.Append("5. If the user asked to introduce or list tools, directly present the available tools and their capabilities based on the tool results in clean Markdown.\n");
            sb.Append("6. Deliver the search conclusions and document summaries directly in Markdown.\n");
        }

        return sb.ToString();
    }

    /// <summary>
    /// 构建 ChatML 完整提示词（注入工具说明与历史对话，包含 Tool 返回结果的无缝对接，并执行严格的上下文预算保护与智能截断）
    /// </summary>
    public static string BuildChatMlPrompt(
        IList<ChatMessage> messages,
        IList<AITool>? tools = null,
        int maxContextTokens = 0,
        int maxOutputTokens = 4096,
        string? modelName = null,
        bool supportsVision = false)
    {
        bool isPhi = !string.IsNullOrWhiteSpace(modelName) && modelName.Contains("phi", StringComparison.OrdinalIgnoreCase);
        bool isReasoningModel = !string.IsNullOrWhiteSpace(modelName) &&
            (modelName.Contains("qwen3.5", StringComparison.OrdinalIgnoreCase) ||
             modelName.Contains("qwen35", StringComparison.OrdinalIgnoreCase) ||
             modelName.Contains("qwen3_5", StringComparison.OrdinalIgnoreCase) ||
             modelName.Contains("deepseek-r1", StringComparison.OrdinalIgnoreCase) ||
             modelName.Contains("deepseek_r1", StringComparison.OrdinalIgnoreCase) ||
             modelName.Contains("r1-distill", StringComparison.OrdinalIgnoreCase));

        // 1. 预算规划（若未指定 maxContextTokens，设定 8192 作为安全默认上限）
        int effectiveCtx = maxContextTokens > 0 ? maxContextTokens : 8192;
        int maxPromptTokens = Math.Max(512, effectiveCtx - Math.Clamp(maxOutputTokens, 128, 8192) - 64);

        // 字符预算折算：中文/英文混合保守折算（1 token ≈ 1.5 字符）
        int maxPromptChars = Math.Max(1024, (int)(maxPromptTokens * 1.5));
        int maxToolOutputChars = Math.Min(6000, Math.Max(1200, maxPromptChars / 2));
        int maxUserMessageChars = Math.Min(8000, Math.Max(1500, (int)(maxPromptChars * 0.75)));

        // 2. 格式化 System Prompt（包含 Tools 声明），System 部分拥有最高优先级
        string systemText = string.Empty;
        var nonSystemMessages = new List<ChatMessage>();
        bool hasExplicitSystem = false;
        bool hasImages = false;

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
                if (msg.Contents.Any(c => c is DataContent or UriContent))
                {
                    hasImages = true;
                }
            }
        }

        // 对视觉多模态模型（如 Qwen2-VL / Qwen3.5-VL 等），官方预训练及微调模版严格使用 "You are a helpful assistant."
        // 若使用偏离分布的中文默认系统词，会导致视觉投影器指令坍塌直接触发 EOS 截断
        string defaultSystem = supportsVision ? "You are a helpful assistant." : "你是 ReferenceRAG 智能助手。";
        if (hasExplicitSystem && supportsVision && (systemText == "你是 ReferenceRAG 智能助手。" || string.IsNullOrWhiteSpace(systemText)))
        {
            systemText = defaultSystem;
        }

        // 当包含多模态图像时，避免注入纯文本知识库检索工具 Schema（# Tools），
        // 视觉模型的 Cross-Attention 与 M-RoPE 在同时接收 XML 工具声明与高维图像投影时会发生注意力坍塌并立即产生 EOS 截断。
        if (tools != null && tools.Count > 0 && (!supportsVision || !hasImages))
        {
            var toolsPrompt = FormatToolsPrompt(tools, supportsVision);
            systemText = hasExplicitSystem ? $"{systemText}\n{toolsPrompt}" : $"{defaultSystem}\n{toolsPrompt}";
        }
        else if (!hasExplicitSystem && string.IsNullOrWhiteSpace(systemText))
        {
            systemText = defaultSystem;
        }

        var systemBlock = isPhi
            ? $"<|system|>\n{systemText}<|end|>\n"
            : $"<|im_start|>system\n{systemText}<|im_end|>\n";
        var assistantTrigger = isPhi
            ? "<|assistant|>\n"
            : (isReasoningModel
                ? "<|im_start|>assistant\n<think>\n"
                : "<|im_start|>assistant\n");

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
            string blockText = FormatSingleMessage(msg, maxToolOutputChars, maxUserMessageChars, isPhi, supportsVision);

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

    /// <summary>
    /// 剥离历史助手回复中注入的 UI 装饰性徽章（如 '> 🔧 **已执行工具**: ...' 与 '> 🔧 **已调用工具**: ...'），
    /// 消除小模型在多轮会话中的少样本格式模仿偏置（In-Context Format Drift），确保历史上下文协议纯净。
    /// </summary>
    public static string StripDecorativeBadges(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var cleaned = System.Text.RegularExpressions.Regex.Replace(
            text,
            @"^>\s*🔧\s*\*\*已(?:执行|调用)工具\*\*:[^\n]*(?:\r?\n)*",
            "",
            System.Text.RegularExpressions.RegexOptions.Multiline);
        return cleaned.TrimStart();
    }

    /// <summary>
    /// 剥离历史助理消息中的思维链标签（<think>...</think> 与 <thought>...</thought>），
    /// 确保历史会话送回模型 Prompt 时保持纯净，防止思维链 In-Context 少样本强化及上下文空间无谓浪费。
    /// </summary>
    public static string StripThoughtBlocks(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        // 1. 移除成对闭合的 <think>...</think> 或 <thought>...</thought>
        var cleaned = System.Text.RegularExpressions.Regex.Replace(
            text,
            @"<\s*(?:think|thought)\b[^>]*>[\s\S]*?<\s*/(?:think|thought)\s*>",
            "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // 2. 移除可能残余的未闭合 <think> 标签开头至末尾
        cleaned = System.Text.RegularExpressions.Regex.Replace(
            cleaned,
            @"<\s*(?:think|thought)\b[^>]*>[\s\S]*$",
            "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return cleaned.Trim();
    }

    private static string FormatSingleMessage(ChatMessage msg, int maxToolChars, int maxUserChars, bool isPhi = false, bool supportsVision = false)
    {
        if (msg.Role == ChatRole.User)
        {
            var text = msg.Text ?? string.Empty;
            var dataContents = msg.Contents.OfType<DataContent>().ToList();
            var uriContents = msg.Contents.OfType<UriContent>().ToList();
            int totalImages = dataContents.Count + uriContents.Count;

            if (totalImages > 0)
            {
                if (supportsVision)
                {
                    // 具备视觉多模态能力时，按 llama.cpp MTMD 规范注入媒体标记 <__media__>
                    var markers = string.Concat(Enumerable.Repeat("<__media__>\n", totalImages));
                    text = string.IsNullOrWhiteSpace(text) ? markers.TrimEnd() : $"{markers}{text}";
                }
                else
                {
                    // 纯文本引擎安全降级：保留图像元数据注解，提示纯文本模型已安全降级
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
            }

            if (text.Length > maxUserChars)
            {
                int cut = FindNaturalBreakpoint(text, maxUserChars);
                text = text.Substring(0, cut) + "\n... [用户输入内容超长已安全截断]";
                text = RepairTruncatedMarkdownAndXml(text);
            }

            return isPhi ? $"<|user|>\n{text}\n<|end|>\n" : $"<|im_start|>user\n{text}\n<|im_end|>\n";
        }

        if (msg.Role == ChatRole.Assistant)
        {
            var text = StripDecorativeBadges(msg.Text ?? string.Empty);
            text = StripThoughtBlocks(text);
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

            return isPhi ? $"<|assistant|>\n{text}\n<|end|>\n" : $"<|im_start|>assistant\n{text}\n<|im_end|>\n";
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

            return isPhi ? $"<|user|>\n{text}\n<|end|>\n" : $"<|im_start|>user\n{text}\n<|im_end|>\n";
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
            else
            {
                // 容错：端侧小模型（如 Qwen2.5/3.5 0.8B/2B）输出 <tool_call> 后常直接命中 <|im_end|> 导致缺失 </tool_call>
                string jsonBlock = text.Substring(startIdx + 11).Trim();
                string prefix = text.Substring(0, startIdx).Trim();
                jsonBlock = RepairTruncatedMarkdownAndXml(jsonBlock);

                if (TryParseFunctionJson(jsonBlock, out var name, out var args))
                {
                    return (true, name, args, prefix);
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

        // 3. 容错匹配：Markdown 代码块包裹、裸 JSON 对象或内嵌 <tool_call> 标签（小参数模型常见）
        if (text.Contains("\"name\"", StringComparison.OrdinalIgnoreCase) || text.Contains("<tool_call>", StringComparison.OrdinalIgnoreCase))
        {
            if (TryParseFunctionJson(text, out var name, out var args) && !string.IsNullOrWhiteSpace(name))
            {
                return (true, name, args, string.Empty);
            }
        }

        // 4. 容错匹配：伪代码调用指令（如 '调用: GetIndexStatus' 或 'call GetIndexStatus()'）
        var pseudoMatch = System.Text.RegularExpressions.Regex.Match(text.Trim(), @"^(?:调用|call|执行)[:：\s]+([A-Za-z0-9_]+)(?:\(\))?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (pseudoMatch.Success)
        {
            var pName = pseudoMatch.Groups[1].Value;
            return (true, pName, new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase), string.Empty);
        }

        return (false, null, null, text);
    }

    private static bool TryParseFunctionJson(string jsonStr, out string? name, out IDictionary<string, object?>? arguments)
    {
        name = null;
        arguments = null;

        try
        {
            // 去除 markdown 代码块包裹 ```json ... ``` 及可能残留的 XML 标签
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
            jsonStr = jsonStr.Replace("<tool_call>", "", StringComparison.OrdinalIgnoreCase)
                             .Replace("</tool_call>", "", StringComparison.OrdinalIgnoreCase)
                             .Trim();

            // 若包含花括号，提取最外层有效 JSON 区间
            int firstBrace = jsonStr.IndexOf('{');
            int lastBrace = jsonStr.LastIndexOf('}');
            if (firstBrace >= 0 && lastBrace > firstBrace)
            {
                jsonStr = jsonStr.Substring(firstBrace, lastBrace - firstBrace + 1);
            }
            else if (firstBrace >= 0 && lastBrace < firstBrace)
            {
                jsonStr = jsonStr.Substring(firstBrace) + "}";
            }

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

        // 3. 清理泄露的裸 <tool_call> 标签残余（若模型把工具调用漏出到最终回复中，自愈规整为结构化友好文本）
        if (cleaned.Contains("<tool_call>", StringComparison.OrdinalIgnoreCase))
        {
            var parsed = TryParseToolCall(cleaned);
            if (parsed.Success && !string.IsNullOrEmpty(parsed.Name))
            {
                var argsSummary = parsed.Arguments != null && parsed.Arguments.Count > 0
                    ? string.Join(", ", parsed.Arguments.Select(kv => $"{kv.Key}: \"{kv.Value}\""))
                    : string.Empty;
                var display = $"> 🔧 **已调用工具**: `{parsed.Name}`({argsSummary})";
                cleaned = string.IsNullOrWhiteSpace(parsed.RemainingText)
                    ? display
                    : $"{display}\n\n{parsed.RemainingText}";
            }
            else
            {
                cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"<\s*/?\s*tool_call\s*>", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
            }
        }

        return cleaned;
    }

    /// <summary>
    /// 检测用户提问是否为明确的工具调用指令（如 'GetIndexStatus'、'搜索: xxx'、'SearchKnowledge 搜索: xxx'），
    /// 实施确定性意图分发，保障端侧小模型（&lt;1B）100% 避免幻觉跳过工具执行。
    /// </summary>
    public static bool TryDetectDirectToolCall(
        string userText,
        IList<AITool>? tools,
        out string? functionName,
        out IDictionary<string, object?>? arguments)
    {
        functionName = null;
        arguments = null;

        if (string.IsNullOrWhiteSpace(userText) || tools == null || tools.Count == 0)
            return false;

        var availableFuncs = tools.OfType<AIFunction>().ToList();
        if (availableFuncs.Count == 0) return false;

        var trimmed = userText.Trim();

        // 1. 去除常见命令前缀: '调用: ', '调用：', 'call ', '/call ', '/', 'tool_call: ', 'tool: '
        string cleanCommand = trimmed;
        if (cleanCommand.StartsWith("调用:", StringComparison.OrdinalIgnoreCase) ||
            cleanCommand.StartsWith("调用：", StringComparison.OrdinalIgnoreCase))
        {
            cleanCommand = cleanCommand.Substring(3).Trim();
        }
        else if (cleanCommand.StartsWith("tool_call:", StringComparison.OrdinalIgnoreCase) ||
                 cleanCommand.StartsWith("tool_call：", StringComparison.OrdinalIgnoreCase))
        {
            cleanCommand = cleanCommand.Substring(10).Trim();
        }
        else if (cleanCommand.StartsWith("tool_call ", StringComparison.OrdinalIgnoreCase))
        {
            cleanCommand = cleanCommand.Substring(10).Trim();
        }
        else if (cleanCommand.StartsWith("tool:", StringComparison.OrdinalIgnoreCase) ||
                 cleanCommand.StartsWith("tool：", StringComparison.OrdinalIgnoreCase))
        {
            cleanCommand = cleanCommand.Substring(5).Trim();
        }
        else if (cleanCommand.StartsWith("tool ", StringComparison.OrdinalIgnoreCase))
        {
            cleanCommand = cleanCommand.Substring(5).Trim();
        }
        else if (cleanCommand.StartsWith("/call ", StringComparison.OrdinalIgnoreCase))
        {
            cleanCommand = cleanCommand.Substring(6).Trim();
        }
        else if (cleanCommand.StartsWith("call ", StringComparison.OrdinalIgnoreCase))
        {
            cleanCommand = cleanCommand.Substring(5).Trim();
        }
        else if (cleanCommand.StartsWith("/"))
        {
            cleanCommand = cleanCommand.Substring(1).Trim();
        }

        // 1.1 容错结构化 XML / Markdown / JSON 标签包裹的指令
        if (cleanCommand.StartsWith("<tool_call>", StringComparison.OrdinalIgnoreCase) ||
            cleanCommand.StartsWith("{", StringComparison.OrdinalIgnoreCase) ||
            cleanCommand.StartsWith("```", StringComparison.OrdinalIgnoreCase))
        {
            var parsed = TryParseToolCall(cleanCommand);
            if (parsed.Success && !string.IsNullOrEmpty(parsed.Name))
            {
                var matched = availableFuncs.FirstOrDefault(f =>
                    string.Equals(f.Name, parsed.Name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(f.Name + "Async", parsed.Name, StringComparison.OrdinalIgnoreCase) ||
                    (f.Name.EndsWith("Async") && string.Equals(f.Name[..^5], parsed.Name, StringComparison.OrdinalIgnoreCase)));

                if (matched != null)
                {
                    functionName = matched.Name;
                    arguments = parsed.Arguments ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    return true;
                }
            }
        }

        // 2. 检查是否为完全匹配函数名（如 'GetIndexStatus'、'GetIndexStatusAsync'、'GetFileStructure'）
        var matchedExact = availableFuncs.FirstOrDefault(f =>
            string.Equals(f.Name, cleanCommand, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(f.Name + "Async", cleanCommand, StringComparison.OrdinalIgnoreCase) ||
            (f.Name.EndsWith("Async") && string.Equals(f.Name[..^5], cleanCommand, StringComparison.OrdinalIgnoreCase)));

        if (matchedExact != null)
        {
            functionName = matchedExact.Name;
            arguments = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            return true;
        }

        // 3. 检查中文自然语言直白状态指令: "查询知识库状态" / "获取知识库状态" / "知识库状态" / "索引状态"
        if (cleanCommand.Contains("索引状态") || cleanCommand.Contains("知识库状态") || cleanCommand.Contains("查询状态") || cleanCommand.Contains("有多少笔记") || cleanCommand.Contains("有多少文件"))
        {
            var statusFunc = availableFuncs.FirstOrDefault(f =>
                f.Name.Contains("GetIndexStatus", StringComparison.OrdinalIgnoreCase));
            if (statusFunc != null)
            {
                functionName = statusFunc.Name;
                arguments = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                return true;
            }
        }

        // 4. 检查目录结构指令: "获取目录结构" / "查看文件结构" / "文件树" / "包含哪些文件" / "有哪些文件"
        if (cleanCommand.Contains("目录结构") || cleanCommand.Contains("文件树") || cleanCommand.Contains("文件结构") ||
            cleanCommand.Contains("有哪些文件") || cleanCommand.Contains("包含哪些文件") || cleanCommand.Contains("文件列表") || cleanCommand.Contains("笔记列表"))
        {
            var structFunc = availableFuncs.FirstOrDefault(f =>
                f.Name.Contains("GetFileStructure", StringComparison.OrdinalIgnoreCase));
            if (structFunc != null)
            {
                functionName = structFunc.Name;
                arguments = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                return true;
            }
        }

        // 4.1 检查工具与能力清单指令: "介绍当前工具" / "有哪些工具" / "可用工具" / "你有什么工具" / "工具列表" / "挂载工具"
        if (cleanCommand.Contains("当前工具") || cleanCommand.Contains("可用工具") || cleanCommand.Contains("有哪些工具") ||
            cleanCommand.Contains("你有什么工具") || cleanCommand.Contains("介绍工具") || cleanCommand.Contains("介绍当前工具") ||
            cleanCommand.Contains("工具列表") || cleanCommand.Contains("支持哪些工具") || cleanCommand.Contains("挂载哪些工具") ||
            cleanCommand.Contains("挂载了哪些工具") || cleanCommand.Contains("挂载工具") || cleanCommand.Contains("系统工具") ||
            cleanCommand.Contains("内置工具") || cleanCommand.Contains("查看工具") || cleanCommand.Contains("能调用什么工具") ||
            cleanCommand.Contains("能使用什么工具") || cleanCommand.Contains("当前有什么工具"))
        {
            var toolsFunc = availableFuncs.FirstOrDefault(f =>
                f.Name.Contains("GetAvailableTools", StringComparison.OrdinalIgnoreCase) ||
                f.Name.Contains("ListTools", StringComparison.OrdinalIgnoreCase));
            if (toolsFunc != null)
            {
                functionName = toolsFunc.Name;
                arguments = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                return true;
            }
        }

        // 5. 检查搜索指令与多轮追问 (包含无冒号中文自然口语，按最长前缀优先匹配):
        var searchFunc = availableFuncs.FirstOrDefault(f =>
            f.Name.Contains("SearchKnowledge", StringComparison.OrdinalIgnoreCase) ||
            f.Name.Contains("BM25Search", StringComparison.OrdinalIgnoreCase));

        if (searchFunc != null)
        {
            string? query = null;
            string targetFuncName = searchFunc.Name;

            // 模式 A: 指定了明确搜索工具名前缀 "SearchKnowledge 搜索: xxx" 或 "BM25Search: xxx"
            if (cleanCommand.StartsWith("SearchKnowledge", StringComparison.OrdinalIgnoreCase))
            {
                var remainder = cleanCommand.Substring(15).Trim();
                query = remainder.TrimStart(':', '：', '搜', '索', ' ').Trim();
                targetFuncName = "SearchKnowledge";
            }
            else if (cleanCommand.StartsWith("BM25Search", StringComparison.OrdinalIgnoreCase) ||
                     cleanCommand.StartsWith("BM25:", StringComparison.OrdinalIgnoreCase) ||
                     cleanCommand.StartsWith("BM25：", StringComparison.OrdinalIgnoreCase))
            {
                var remainder = cleanCommand.StartsWith("BM25Search", StringComparison.OrdinalIgnoreCase)
                    ? cleanCommand.Substring(10).Trim()
                    : cleanCommand.Substring(5).Trim();
                query = remainder.TrimStart(':', '：', '搜', '索', ' ').Trim();
                targetFuncName = "BM25Search";
            }
            else
            {
                string[] searchPrefixes =
                [
                    "搜索知识库中:", "搜索知识库中：", "搜索知识库中",
                    "搜索知识库关于:", "搜索知识库关于：", "搜索知识库关于",
                    "搜索知识库:", "搜索知识库：", "搜索知识库",
                    "检索知识库中:", "检索知识库中：", "检索知识库中",
                    "检索知识库关于:", "检索知识库关于：", "检索知识库关于",
                    "检索知识库:", "检索知识库：", "检索知识库",
                    "查询知识库关于:", "查询知识库关于：", "查询知识库关于",
                    "查询知识库:", "查询知识库：", "查询知识库",
                    "知识库搜索:", "知识库搜索：", "知识库搜索",
                    "知识库检索:", "知识库检索：", "知识库检索",
                    "在知识库中搜索:", "在知识库中搜索：", "在知识库中搜索",
                    "在知识库中查找:", "在知识库中查找：", "在知识库中查找",
                    "从知识库中检索:", "从知识库中检索：", "从知识库中检索",
                    "从知识库检索:", "从知识库检索：", "从知识库检索",
                    "从知识库搜索:", "从知识库搜索：", "从知识库搜索",
                    "再查一下关于:", "再查一下关于：", "再查一下关于",
                    "再查一下:", "再查一下：", "再查一下",
                    "再搜索一下关于:", "再搜索一下关于：", "再搜索一下关于",
                    "再搜索一下:", "再搜索一下：", "再搜索一下",
                    "再检索一下关于:", "再检索一下关于：", "再检索一下关于",
                    "再检索一下:", "再检索一下：", "再检索一下",
                    "再找找关于:", "再找找关于：", "再找找关于",
                    "再找找:", "再找找：", "再找找",
                    "再搜关于:", "再搜关于：", "再搜关于",
                    "再搜一下:", "再搜一下：", "再搜一下",
                    "再查关于:", "再查关于：", "再查关于",
                    "继续搜索:", "继续搜索：", "继续搜索",
                    "继续检索:", "继续检索：", "继续检索",
                    "帮我查一下关于:", "帮我查一下关于：", "帮我查一下关于",
                    "帮我查一下:", "帮我查一下：", "帮我查一下",
                    "帮我查关于:", "帮我查关于：", "帮我查关于",
                    "帮我查:", "帮我查：", "帮我查",
                    "帮我搜索关于:", "帮我搜索关于：", "帮我搜索关于",
                    "帮我搜索:", "帮我搜索：", "帮我搜索",
                    "帮我检索关于:", "帮我检索关于：", "帮我检索关于",
                    "帮我检索:", "帮我检索：", "帮我检索",
                    "查一下关于:", "查一下关于：", "查一下关于",
                    "查一下:", "查一下：", "查一下",
                    "搜索关于:", "搜索关于：", "搜索关于",
                    "检索关于:", "检索关于：", "检索关于",
                    "搜索:", "搜索：", "搜索 ",
                    "检索:", "检索：", "检索 ",
                    "查询:", "查询：", "查询 ",
                    "查找:", "查找：", "查找 "
                ];

                foreach (var p in searchPrefixes)
                {
                    if (cleanCommand.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                    {
                        query = cleanCommand.Substring(p.Length).Trim();
                        targetFuncName = "SearchKnowledge";
                        break;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(query))
            {
                // 将多行输入规范化为单行有效关键词，并去除多余空白
                query = string.Join(" ", query.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Trim();
                var matchedExactFunc = availableFuncs.FirstOrDefault(f =>
                    string.Equals(f.Name, targetFuncName, StringComparison.OrdinalIgnoreCase));
                functionName = matchedExactFunc != null ? matchedExactFunc.Name : targetFuncName;
                arguments = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["query"] = query,
                    ["keyword"] = query,
                    ["topK"] = 5
                };
                return true;
            }
        }

        return false;
    }
}
