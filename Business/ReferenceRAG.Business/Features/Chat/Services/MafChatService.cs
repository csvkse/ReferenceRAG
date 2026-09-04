using System.ClientModel;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Service.Services;

public record SseEvent(string Type, string? Delta = null, string? Message = null);

public class MafChatService
{
    private readonly IChatClient _client;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _systemPrompt;
    private readonly string _localBaseUrl;
    private readonly AITool[] _tools;
    private readonly ConcurrentDictionary<string, List<ChatMessage>> _sessions = new();
    private readonly ILogger<MafChatService> _logger;

    private static readonly JsonSerializerOptions _jsonOpts = new() { PropertyNameCaseInsensitive = true };

    public MafChatService(
        IConfiguration config,
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        ILogger<MafChatService> logger)
    {
        _scopeFactory = scopeFactory;
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        var section = config.GetSection("Chat");
        var endpoint = section["Endpoint"] ?? "https://api.openai.com/v1";
        var apiKey = section["ApiKey"] ?? "placeholder";
        var model = section["Model"] ?? "gpt-4o-mini";
        _systemPrompt = section["SystemPrompt"] ?? "你是 ReferenceRAG 智能助手。";

        var port = config.GetSection("ReferenceRAG:Service")["port"] ?? "7897";
        _localBaseUrl = $"http://localhost:{port}/api";

        _tools =
        [
            AIFunctionFactory.Create(SearchKnowledgeAsync),
            AIFunctionFactory.Create(DrillDownChunkAsync),
            AIFunctionFactory.Create(BM25SearchAsync),
            AIFunctionFactory.Create(SearchNoteByTitleAsync),
            AIFunctionFactory.Create(GetFileStructureAsync),
            AIFunctionFactory.Create(ReadFileContentAsync),
            AIFunctionFactory.Create(GetIndexStatusAsync)
        ];

        var rawClient = new OpenAIClient(
            new ApiKeyCredential(apiKey),
            new OpenAIClientOptions { Endpoint = new Uri(endpoint) })
            .GetChatClient(model)
            .AsIChatClient();

        _client = rawClient
            .AsBuilder()
            .UseFunctionInvocation()
            .Build();
    }

    // ── 工具定义 ──────────────────────────────────────────────────────

    [Description("语义搜索知识库，返回相关笔记片段及来源元数据（文件路径、行号、refId）。适用于通用知识查询，建议扩展查询词以提高召回率。")]
    private async Task<string> SearchKnowledgeAsync(
        [Description("查询词，建议补充同义词、中英双语、相关术语以提高召回率")] string query,
        [Description("返回数量，默认 10")] int topK = 10)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var searchService = scope.ServiceProvider.GetRequiredService<ISearchService>();
            var request = new AIQueryRequest
            {
                Query = query,
                TopK = topK,
                Mode = QueryMode.HybridRerank,
                ContextWindow = 1
            };
            var response = await searchService.SearchAsync(request);

            if (response.Chunks.Count == 0)
                return "知识库中未找到相关内容。";

            var sb = new StringBuilder();
            sb.AppendLine($"## 语义检索结果（共 {response.Chunks.Count} 条，耗时 {response.Stats.DurationMs}ms）");
            sb.AppendLine();

            for (int i = 0; i < response.Chunks.Count; i++)
            {
                var c = response.Chunks[i];
                var title = c.Title ?? Path.GetFileNameWithoutExtension(c.FilePath);
                var heading = string.IsNullOrEmpty(c.HeadingPath) ? "" : $" > {c.HeadingPath}";
                sb.AppendLine($"[{i + 1}] 《{title}》{heading}");
                sb.AppendLine($"    文件: {c.FilePath}（行 {c.StartLine}-{c.EndLine}，refId: {c.RefId}，score: {c.Score:F3}）");
                sb.AppendLine("    ---");
                sb.AppendLine(c.Content);
                sb.AppendLine();
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SearchKnowledge 失败: {Query}", query);
            return $"搜索失败：{ex.Message}";
        }
    }

    [Description("按 refId 展开某个检索结果的完整上下文，适用于 SearchKnowledge 返回内容被截断时。")]
    private async Task<string> DrillDownChunkAsync(
        [Description("来自 SearchKnowledge 结果的 refId")] string refId,
        [Description("原始查询词")] string query,
        [Description("上下文扩展窗口大小，默认 2")] int expandContext = 2)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var searchService = scope.ServiceProvider.GetRequiredService<ISearchService>();
            var response = await searchService.DrillDownAsync(new DrillDownRequest
            {
                Query = query,
                RefIds = [refId],
                ExpandContext = expandContext
            });
            return string.IsNullOrWhiteSpace(response.FullContext)
                ? "未能展开该片段内容。"
                : $"## 展开内容（refId: {refId}）\n\n{response.FullContext}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DrillDown 失败: {RefId}", refId);
            return $"展开失败：{ex.Message}";
        }
    }

    [Description("BM25 关键词精确搜索，适用于错误码、API 名称、变量名、精确术语等场景。")]
    private async Task<string> BM25SearchAsync(
        [Description("精确关键词")] string keyword,
        [Description("返回数量，默认 10")] int topK = 10)
    {
        try
        {
            var http = _httpClientFactory.CreateClient("Rag.LocalApi");
            var url = $"{_localBaseUrl}/bm25index/search?query={Uri.EscapeDataString(keyword)}&topK={topK}";
            var res = await http.GetFromJsonAsync<JsonElement>(url, _jsonOpts);

            var results = res.TryGetProperty("results", out var r) ? r : res.GetProperty("Results");
            var sb = new StringBuilder();
            sb.AppendLine($"## BM25 精确检索（词：{keyword}）");
            sb.AppendLine();
            int idx = 1;
            foreach (var item in results.EnumerateArray())
            {
                var content = GetStr(item, "content", "Content");
                var score = GetDouble(item, "score", "Score");
                sb.AppendLine($"[{idx++}] score: {score:F2}");
                sb.AppendLine(content);
                sb.AppendLine();
            }
            return idx == 1 ? $"未找到关键词「{keyword}」的匹配内容。" : sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BM25Search 失败: {Keyword}", keyword);
            return $"BM25 搜索失败：{ex.Message}";
        }
    }

    [Description("按笔记标题关键词查找知识图谱节点，返回节点 ID 和标题，可用于后续 GetFileStructure 或 ReadFileContent。")]
    private async Task<string> SearchNoteByTitleAsync(
        [Description("笔记标题关键词")] string titleQuery,
        [Description("返回数量，默认 10")] int limit = 10)
    {
        try
        {
            var http = _httpClientFactory.CreateClient("Rag.LocalApi");
            var url = $"{_localBaseUrl}/graph/search?q={Uri.EscapeDataString(titleQuery)}&limit={limit}";
            var json = await http.GetStringAsync(url);
            var nodes = JsonSerializer.Deserialize<JsonElement[]>(json, _jsonOpts) ?? [];

            if (nodes.Length == 0)
                return $"未找到标题含「{titleQuery}」的笔记节点。";

            var sb = new StringBuilder();
            sb.AppendLine($"## 图谱节点搜索（标题：{titleQuery}）");
            sb.AppendLine();
            foreach (var node in nodes)
            {
                var id = GetStr(node, "id", "Id");
                var title = GetStr(node, "title", "Title");
                var type = GetStr(node, "type", "Type");
                sb.AppendLine($"- 《{title}》  类型: {type}");
                sb.AppendLine($"  ID: {id}");
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SearchNoteByTitle 失败: {Query}", titleQuery);
            return $"图谱搜索失败：{ex.Message}";
        }
    }

    [Description("获取一批文件的章节目录结构（标题 + 行号范围），不含正文，用于规划后续精准读取。文件路径用逗号分隔，单次最多 20 个。")]
    private async Task<string> GetFileStructureAsync(
        [Description("文件绝对路径列表，多个路径用英文逗号分隔")] string filePaths)
    {
        try
        {
            var paths = filePaths.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var http = _httpClientFactory.CreateClient("Rag.LocalApi");
            var body = new StringContent(JsonSerializer.Serialize(new { paths }), Encoding.UTF8, "application/json");
            var resp = await (await http.PostAsync($"{_localBaseUrl}/sources/files/info", body)).Content
                .ReadFromJsonAsync<JsonElement>(_jsonOpts);

            var results = resp.TryGetProperty("results", out var r) ? r : resp.GetProperty("Results");
            var sb = new StringBuilder();
            sb.AppendLine("## 文件章节结构");
            sb.AppendLine();
            foreach (var item in results.EnumerateArray())
            {
                var path = GetStr(item, "path", "Path");
                var title = GetStr(item, "title", "Title");
                var err = GetStr(item, "error", "Error");
                if (!string.IsNullOrEmpty(err)) { sb.AppendLine($"- {path}: 错误 {err}"); continue; }
                sb.AppendLine($"### {(string.IsNullOrEmpty(title) ? path : title)}");
                sb.AppendLine($"路径: {path}");
                var sections = item.TryGetProperty("sections", out var s) ? s : item.GetProperty("Sections");
                foreach (var sec in sections.EnumerateArray())
                {
                    var heading = GetStr(sec, "headingPath", "HeadingPath");
                    var start = GetInt(sec, "startLine", "StartLine");
                    var end = GetInt(sec, "endLine", "EndLine");
                    sb.AppendLine($"  - {heading}（行 {start}-{end}）");
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetFileStructure 失败");
            return $"获取文件结构失败：{ex.Message}";
        }
    }

    [Description("按行范围精准读取文件内容。startLine=0 且 endLine=0 时读取全文。建议先用 GetFileStructure 确认目标章节行号再调用。")]
    private async Task<string> ReadFileContentAsync(
        [Description("文件绝对路径")] string filePath,
        [Description("起始行，0 表示从头")] int startLine = 0,
        [Description("结束行，0 表示到末尾")] int endLine = 0)
    {
        try
        {
            var http = _httpClientFactory.CreateClient("Rag.LocalApi");
            var body = new StringContent(
                JsonSerializer.Serialize(new { items = new[] { new { path = filePath, startLine, endLine } } }),
                Encoding.UTF8, "application/json");
            var resp = await (await http.PostAsync($"{_localBaseUrl}/sources/file/lines", body)).Content
                .ReadFromJsonAsync<JsonElement>(_jsonOpts);

            var results = resp.TryGetProperty("results", out var r) ? r : resp.GetProperty("Results");
            var sb = new StringBuilder();
            foreach (var item in results.EnumerateArray())
            {
                var err = GetStr(item, "error", "Error");
                if (!string.IsNullOrEmpty(err)) return $"读取失败：{err}";
                var chunks = item.TryGetProperty("chunks", out var c) ? c : item.GetProperty("Chunks");
                foreach (var chunk in chunks.EnumerateArray())
                    sb.AppendLine(GetStr(chunk, "content", "Content"));
            }
            return sb.Length == 0 ? "未读取到内容。" : sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ReadFileContent 失败: {Path}", filePath);
            return $"读取文件失败：{ex.Message}";
        }
    }

    [Description("获取知识库当前索引状态：向量分块总数、BM25 文档总数。")]
    private async Task<string> GetIndexStatusAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var vectorStore = scope.ServiceProvider.GetRequiredService<IVectorStore>();
            var bm25Store = scope.ServiceProvider.GetRequiredService<IBM25Store>();
            var vectorStats = await vectorStore.GetVectorStatsAsync();
            var bm25Stats = await bm25Store.GetStatsAsync();
            return $"向量索引：{vectorStats.Sum(v => v.VectorCount)} 个分块；BM25 索引：{bm25Stats.TotalDocuments} 个文档";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetIndexStatus 失败");
            return $"获取状态失败：{ex.Message}";
        }
    }

    // ── 会话管理 ──────────────────────────────────────────────────────

    public string CreateSession()
    {
        var sessionId = Guid.NewGuid().ToString("N");
        _sessions[sessionId] = [new ChatMessage(ChatRole.System, _systemPrompt)];
        return sessionId;
    }

    public bool DeleteSession(string sessionId) => _sessions.TryRemove(sessionId, out _);

    public IReadOnlyList<string> GetToolDescriptions()
    {
        return _tools.Select(t =>
        {
            var func = t as AIFunction;
            return func != null ? $"{func.Name}（{func.Description?.Split('。')[0] ?? ""}）" : "";
        }).Where(s => !string.IsNullOrEmpty(s)).ToList();
    }

    public async IAsyncEnumerable<SseEvent> StreamAsync(
        string sessionId,
        string userMessage,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var history))
        {
            yield return new SseEvent("error", Message: "会话不存在，请创建新会话");
            yield return new SseEvent("done");
            yield break;
        }

        history.Add(new ChatMessage(ChatRole.User, userMessage));
        var options = new ChatOptions { Tools = [.. _tools] };
        var responseText = new StringBuilder();

        await foreach (var update in _client.GetStreamingResponseAsync(history, options, ct))
        {
            if (!string.IsNullOrEmpty(update.Text))
            {
                responseText.Append(update.Text);
                yield return new SseEvent("text", Delta: update.Text);
            }
        }

        if (responseText.Length > 0)
            history.Add(new ChatMessage(ChatRole.Assistant, responseText.ToString()));

        yield return new SseEvent("done");
    }

    // ── 辅助方法 ──────────────────────────────────────────────────────

    private static string GetStr(JsonElement e, string camel, string pascal)
        => (e.TryGetProperty(camel, out var v) ? v : e.TryGetProperty(pascal, out v) ? v : default).GetString() ?? "";

    private static double GetDouble(JsonElement e, string camel, string pascal)
        => (e.TryGetProperty(camel, out var v) ? v : e.TryGetProperty(pascal, out v) ? v : default).GetDouble();

    private static int GetInt(JsonElement e, string camel, string pascal)
        => (e.TryGetProperty(camel, out var v) ? v : e.TryGetProperty(pascal, out v) ? v : default).GetInt32();
}
