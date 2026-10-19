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
using ReferenceRAG.Core.Services;

namespace ReferenceRAG.Service.Services;

public record SseEvent(string Type, string? Delta = null, string? Message = null);

public class MafChatService
{
    private volatile IChatClient? _client;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private volatile string _systemPrompt;
    private volatile string _model = "gpt-4o-mini";
    private readonly AITool[] _tools;
    private readonly ConcurrentDictionary<string, ChatSession> _sessions = new();
    private readonly ILogger<MafChatService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly int _maxSessions;
    private readonly int _maxMessagesPerSession;
    private readonly TimeSpan _sessionIdleTimeout;
    private readonly HttpMessageHandler? _httpMessageHandler;

    public IChatClient? Client => _client;
    public bool IsConfigured => _client != null;
    public string CurrentModel => _model;
    public string SystemPrompt => _systemPrompt;
    public AITool[] Tools => _tools;

    public MafChatService(
        IConfiguration config,
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        ILogger<MafChatService> logger,
        TimeProvider? timeProvider = null,
        HttpMessageHandler? httpMessageHandler = null)
    {
        _scopeFactory = scopeFactory;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _httpMessageHandler = httpMessageHandler;

        var section = config.GetSection("Chat");
        var endpoint = section["Endpoint"] ?? "https://api.openai.com/v1";
        var apiKey = section["ApiKey"];
        var model = section["Model"] ?? "gpt-4o-mini";
        _model = model;
        _systemPrompt = section["SystemPrompt"] ?? "你是 ReferenceRAG 智能助手。";
        _maxSessions = Math.Max(1, section.GetValue("MaxSessions", 100));
        _maxMessagesPerSession = Math.Max(3, section.GetValue("MaxMessagesPerSession", 50));
        _sessionIdleTimeout = TimeSpan.FromMinutes(Math.Max(1, section.GetValue("SessionIdleMinutes", 60)));

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

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("聊天 API Key 未配置；聊天接口将在调用时返回配置错误");
        }
        else
        {
            _client = BuildChatClient(endpoint, apiKey, model);
        }
    }

    private IChatClient? BuildChatClient(string endpoint, string? apiKey, string model)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return null;

        var sanitizingHandler = _httpMessageHandler != null
            ? new OpenAiToolCallSanitizingHandler(_httpMessageHandler)
            : new OpenAiToolCallSanitizingHandler();
        var httpClient = new HttpClient(sanitizingHandler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = new Uri(endpoint),
            Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(httpClient)
        };

        var rawClient = new OpenAIClient(new ApiKeyCredential(apiKey), clientOptions)
            .GetChatClient(model)
            .AsIChatClient();

        return rawClient
            .AsBuilder()
            .UseFunctionInvocation()
            .Build();
    }

    /// <summary>
    /// 热更新 AI 聊天配置（无需重启即时生效）
    /// </summary>
    public void UpdateConfiguration(string? endpoint, string? apiKey, string? model, string? systemPrompt)
    {
        var ep = string.IsNullOrWhiteSpace(endpoint) ? "https://api.openai.com/v1" : endpoint.Trim();
        var mdl = string.IsNullOrWhiteSpace(model) ? "gpt-4o-mini" : model.Trim();
        var key = apiKey?.Trim();

        _logger.LogInformation("正在热更新 AI 聊天配置: Endpoint={Endpoint}, Model={Model}, HasKey={HasKey}",
            ep, mdl, !string.IsNullOrWhiteSpace(key));

        _model = mdl;
        _systemPrompt = string.IsNullOrWhiteSpace(systemPrompt) ? "你是 ReferenceRAG 智能助手。" : systemPrompt.Trim();

        if (string.IsNullOrWhiteSpace(key))
        {
            _client = null;
            _logger.LogWarning("AI 聊天 API Key 已清空，聊天接口将提示配置");
            return;
        }

        try
        {
            var newClient = BuildChatClient(ep, key, mdl);
            _client = newClient;
            _logger.LogInformation("AI 聊天客户端热更新成功，立即生效");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "热更新 AI 聊天客户端失败");
            throw;
        }
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
            if (string.IsNullOrWhiteSpace(keyword)) return "搜索关键词不能为空。";
            using var scope = _scopeFactory.CreateScope();
            var bm25Store = scope.ServiceProvider.GetRequiredService<IBM25Store>();
            var vectorStore = scope.ServiceProvider.GetRequiredService<IVectorStore>();
            var configManager = scope.ServiceProvider.GetRequiredService<ConfigManager>();

            var config = configManager.Load();
            var enabledSources = config.Sources
                .Where(s => s.Enabled)
                .Select(s => s.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (config.Sources.Count > 0 && enabledSources.Count == 0)
                return $"未找到关键词「{keyword}」的匹配内容（所有数据源均已停用）。";

            var results = await bm25Store.SearchAsync(keyword, topK * 2);

            var filteredResults = new List<BM25SearchResult>();
            if (config.Sources.Count > 0)
            {
                foreach (var item in results)
                {
                    var chunk = await vectorStore.GetChunkAsync(item.ChunkId);
                    if (chunk != null)
                    {
                        var file = await vectorStore.GetFileAsync(chunk.FileId);
                        if (file != null && !string.IsNullOrEmpty(file.Source) && enabledSources.Contains(file.Source))
                        {
                            filteredResults.Add(item);
                            if (filteredResults.Count >= topK) break;
                        }
                    }
                }
            }
            else
            {
                filteredResults = results.Take(topK).ToList();
            }

            if (filteredResults.Count == 0)
                return $"未找到关键词「{keyword}」的匹配内容。";

            var sb = new StringBuilder();
            sb.AppendLine($"## BM25 精确检索（词：{keyword}）");
            sb.AppendLine();
            int idx = 1;
            foreach (var item in filteredResults)
            {
                sb.AppendLine($"[{idx++}] score: {item.Score:F2}");
                sb.AppendLine(item.Content);
                sb.AppendLine();
            }
            return sb.ToString();
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
            if (string.IsNullOrWhiteSpace(titleQuery)) return "笔记标题关键词不能为空。";
            using var scope = _scopeFactory.CreateScope();
            var graphStore = scope.ServiceProvider.GetRequiredService<IGraphStore>();
            var vectorStore = scope.ServiceProvider.GetRequiredService<IVectorStore>();
            var configManager = scope.ServiceProvider.GetRequiredService<ConfigManager>();

            var config = configManager.Load();
            var enabledSources = config.Sources
                .Where(s => s.Enabled)
                .Select(s => s.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (config.Sources.Count > 0 && enabledSources.Count == 0)
                return $"未找到标题含「{titleQuery}」的笔记节点（所有数据源均已停用）。";

            limit = Math.Clamp(limit, 1, 50);
            var nodes = await graphStore.SearchNodesAsync(titleQuery, limit * 2);

            var filteredNodes = new List<GraphNode>();
            if (config.Sources.Count > 0)
            {
                var paths = nodes.Select(n =>
                {
                    var hashIdx = n.Id.IndexOf('#');
                    return hashIdx >= 0 ? n.Id[..hashIdx] : n.Id;
                }).Distinct().ToList();

                var files = (await vectorStore.GetFilesByPathsAsync(paths))
                    .Where(f => !string.IsNullOrEmpty(f.Source) && enabledSources.Contains(f.Source))
                    .ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);

                foreach (var node in nodes)
                {
                    var hashIdx = node.Id.IndexOf('#');
                    var p = hashIdx >= 0 ? node.Id[..hashIdx] : node.Id;
                    if (files.ContainsKey(p))
                    {
                        filteredNodes.Add(node);
                        if (filteredNodes.Count >= limit) break;
                    }
                }
            }
            else
            {
                filteredNodes = nodes.Take(limit).ToList();
            }

            if (filteredNodes.Count == 0)
                return $"未找到标题含「{titleQuery}」的笔记节点。";

            var sb = new StringBuilder();
            sb.AppendLine($"## 图谱节点搜索（标题：{titleQuery}）");
            sb.AppendLine();
            foreach (var node in filteredNodes)
            {
                sb.AppendLine($"- 《{node.Title}》  类型: {node.Type}");
                sb.AppendLine($"  ID: {node.Id}");
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
            if (paths.Length == 0) return "文件路径列表不能为空。";
            if (paths.Length > 20) return "单次最多查询 20 个文件。";

            using var scope = _scopeFactory.CreateScope();
            var vectorStore = scope.ServiceProvider.GetRequiredService<IVectorStore>();
            var configManager = scope.ServiceProvider.GetRequiredService<ConfigManager>();

            var config = configManager.Load();
            var enabledSources = config.Sources
                .Where(s => s.Enabled)
                .Select(s => s.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var sb = new StringBuilder();
            sb.AppendLine("## 文件章节结构");
            sb.AppendLine();

            foreach (var rawPath in paths)
            {
                if (string.IsNullOrWhiteSpace(rawPath) || rawPath.Contains(".."))
                {
                    sb.AppendLine($"- {rawPath}: 路径无效");
                    continue;
                }

                var normalizedPath = Path.GetFullPath(rawPath);
                var file = await vectorStore.GetFileByPathAsync(normalizedPath)
                           ?? await vectorStore.GetFileByPathAsync(rawPath);

                if (file == null)
                {
                    sb.AppendLine($"- {rawPath}: 文件未被索引");
                    continue;
                }

                if (config.Sources.Count > 0 && (!enabledSources.Contains(file.Source ?? "")))
                {
                    sb.AppendLine($"- {rawPath}: 文件所属数据源已停用");
                    continue;
                }

                var chunks = (await vectorStore.GetChunksByFileAsync(file.Id))
                    .OrderBy(c => c.ChunkOrder)
                    .ToList();

                sb.AppendLine($"### {(string.IsNullOrEmpty(file.Title) ? file.Path : file.Title)}");
                sb.AppendLine($"路径: {file.Path}");
                foreach (var chunk in chunks)
                {
                    sb.AppendLine($"  - {chunk.HeadingPath}（行 {chunk.StartLine}-{chunk.EndLine}）");
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
            if (string.IsNullOrWhiteSpace(filePath) || filePath.Contains(".."))
                return "文件路径无效。";

            if (startLine < 0 || endLine < 0)
                return "startLine / endLine 不能为负数。";

            if (startLine > 0 && endLine > 0 && startLine > endLine)
                return $"startLine ({startLine}) 不能大于 endLine ({endLine})。";

            using var scope = _scopeFactory.CreateScope();
            var vectorStore = scope.ServiceProvider.GetRequiredService<IVectorStore>();
            var configManager = scope.ServiceProvider.GetRequiredService<ConfigManager>();

            var config = configManager.Load();
            var enabledSources = config.Sources
                .Where(s => s.Enabled)
                .Select(s => s.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var normalizedPath = Path.GetFullPath(filePath);
            var file = await vectorStore.GetFileByPathAsync(normalizedPath)
                       ?? await vectorStore.GetFileByPathAsync(filePath);

            if (file == null)
                return "文件未被索引。";

            if (config.Sources.Count > 0 && (!enabledSources.Contains(file.Source ?? "")))
                return "该文件所属数据源已停用，无法读取内容。";

            var allChunks = await vectorStore.GetChunksByFileAsync(file.Id);
            var filtered = allChunks.OrderBy(c => c.ChunkOrder);
            var hasRange = startLine > 0 || endLine > 0;
            if (hasRange)
            {
                var start = startLine > 0 ? startLine : 1;
                var end = endLine > 0 ? endLine : int.MaxValue;
                filtered = filtered.Where(c => c.StartLine <= end && c.EndLine >= start)
                                   .OrderBy(c => c.ChunkOrder);
            }

            var sb = new StringBuilder();
            foreach (var chunk in filtered)
            {
                sb.AppendLine(chunk.Content);
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
        RemoveExpiredSessions();
        while (_sessions.Count >= _maxSessions)
        {
            var oldest = _sessions.MinBy(item => item.Value.LastAccessUtc);
            if (oldest.Key is null || !_sessions.TryRemove(oldest.Key, out _)) break;
        }

        var sessionId = Guid.NewGuid().ToString("N");
        _sessions[sessionId] = new ChatSession([new ChatMessage(ChatRole.System, _systemPrompt)], _timeProvider.GetUtcNow());
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
        var client = _client;
        if (client is null)
        {
            yield return new SseEvent("error", Message: "聊天 API Key 未配置，请在项目设置中配置 Chat ApiKey 后使用");
            yield return new SseEvent("done");
            yield break;
        }

        RemoveExpiredSessions();
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            yield return new SseEvent("error", Message: "会话不存在，请创建新会话");
            yield return new SseEvent("done");
            yield break;
        }

        session.LastAccessUtc = _timeProvider.GetUtcNow();
        List<ChatMessage> history;
        lock (session.Messages)
        {
            session.Messages.Add(new ChatMessage(ChatRole.User, userMessage));
            TrimHistory(session.Messages);
            history = [.. session.Messages];
        }
        var options = new ChatOptions { Tools = [.. _tools] };
        var responseText = new StringBuilder();

        await foreach (var update in client.GetStreamingResponseAsync(history, options, ct))
        {
            if (!string.IsNullOrEmpty(update.Text))
            {
                responseText.Append(update.Text);
                yield return new SseEvent("text", Delta: update.Text);
            }
        }

        if (responseText.Length > 0)
        {
            lock (session.Messages)
            {
                session.Messages.Add(new ChatMessage(ChatRole.Assistant, responseText.ToString()));
                TrimHistory(session.Messages);
                session.LastAccessUtc = _timeProvider.GetUtcNow();
            }
        }

        yield return new SseEvent("done");
    }

    /// <summary>
    /// 检索本地知识库并格式化为 RAG 知识注入提示词（用于 OpenAI 兼容接口 reference-rag 模式）
    /// </summary>
    public async Task<string> RetrieveRagContextAsync(string query, int topK = 5, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(query)) return string.Empty;

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
                return string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine("## 本地知识库参考片段 (Local Knowledge Base Context)");
            sb.AppendLine("以下内容检索自用户的本地知识库。请优先结合这些参考资料准确回答问题，并在引用的观点后标注对应出处，例如 [1] 或《笔记标题》：\n");

            for (int i = 0; i < response.Chunks.Count; i++)
            {
                var c = response.Chunks[i];
                var title = c.Title ?? Path.GetFileNameWithoutExtension(c.FilePath);
                var heading = string.IsNullOrEmpty(c.HeadingPath) ? "" : $" > {c.HeadingPath}";
                sb.AppendLine($"[{i + 1}] 《{title}》{heading} (来源: {c.FilePath})");
                sb.AppendLine("```markdown");
                sb.AppendLine(c.Content);
                sb.AppendLine("```");
                sb.AppendLine();
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RetrieveRagContextAsync 失败: {Query}", query);
            return string.Empty;
        }
    }

    /// <summary>
    /// 无状态多轮流式生成（供 OpenAI 兼容 POST /v1/chat/completions 调用）
    /// </summary>
    public async IAsyncEnumerable<string> StreamCompletionAsync(
        IList<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var client = _client;
        if (client is null)
            throw new InvalidOperationException("Chat API Key 未配置，请在设置中配置 Chat ApiKey 后使用");

        options ??= new ChatOptions { Tools = [.. _tools] };
        await foreach (var update in client.GetStreamingResponseAsync(messages, options, ct))
        {
            if (!string.IsNullOrEmpty(update.Text))
            {
                yield return update.Text;
            }
        }
    }

    /// <summary>
    /// 无状态多轮单次响应生成（供 OpenAI 兼容 POST /v1/chat/completions 调用）
    /// </summary>
    public async Task<string> CompleteAsync(
        IList<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken ct = default)
    {
        var client = _client;
        if (client is null)
            throw new InvalidOperationException("Chat API Key 未配置，请在设置中配置 Chat ApiKey 后使用");

        options ??= new ChatOptions { Tools = [.. _tools] };
        var response = await client.GetResponseAsync(messages, options, ct);
        return response?.Text ?? string.Empty;
    }

    private void RemoveExpiredSessions()
    {
        var cutoff = _timeProvider.GetUtcNow() - _sessionIdleTimeout;
        foreach (var item in _sessions)
            if (item.Value.LastAccessUtc < cutoff)
                _sessions.TryRemove(item.Key, out _);
    }

    internal void TrimHistory(List<ChatMessage> messages)
    {
        var removable = messages.Count - _maxMessagesPerSession;
        if (removable <= 0) return;

        messages.RemoveRange(1, Math.Min(removable, messages.Count - 1));

        // 协议保护：截断后首条非系统消息不能是 Tool 响应，
        // 必须向前丢弃直到遇到首个 User 消息，保证 OpenAI / MAF 会话协议合法性
        while (messages.Count > 1 && messages[1].Role != ChatRole.User)
        {
            messages.RemoveAt(1);
        }
    }

    private sealed class ChatSession(List<ChatMessage> messages, DateTimeOffset lastAccessUtc)
    {
        public List<ChatMessage> Messages { get; } = messages;
        public DateTimeOffset LastAccessUtc { get; set; } = lastAccessUtc;
    }
}
