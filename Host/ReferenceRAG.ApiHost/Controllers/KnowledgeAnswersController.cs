using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Business.Features.KnowledgeBase.Services;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Service.Services;

namespace ReferenceRAG.Service.Controllers;

/// <summary>
/// 知识库单轨有据问答控制器 (POST /api/knowledge-answers)
/// 严格遵循单轨检索优先设计：检索未命中或证据不足时立即短路退出，0 LLM Token 生成消耗。
/// </summary>
[ApiController]
[Route("api/knowledge-answers")]
public class KnowledgeAnswersController : ControllerBase
{
    private readonly IKnowledgeBaseSearchService _searchService;
    private readonly MafChatService? _chatService;
    private readonly ILogger<KnowledgeAnswersController>? _logger;

    private static readonly Regex CitationRegex = new(@"\[(\d+)\]", RegexOptions.Compiled);

    public KnowledgeAnswersController(
        IKnowledgeBaseSearchService searchService,
        MafChatService? chatService = null,
        ILogger<KnowledgeAnswersController>? logger = null)
    {
        _searchService = searchService ?? throw new ArgumentNullException(nameof(searchService));
        _chatService = chatService;
        _logger = logger;
    }

    [HttpPost]
    public async Task<ActionResult<KnowledgeAnswerResponse>> Answer(
        [FromBody] KnowledgeAnswerRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Question))
        {
            return BadRequest(new { message = "用户问题 question 不能为空" });
        }

        var key = ControllerContext.HttpContext?.Items["CurrentApiKey"] as ApiKeyEntity;
        var allowedKbs = ApiKeyPermissionMatrix.GetEffectiveAllowedKnowledgeBases(key);
        if (allowedKbs != null && request.KnowledgeBaseIds is { Count: > 0 })
        {
            var allowedSet = new HashSet<string>(allowedKbs, StringComparer.OrdinalIgnoreCase);
            foreach (var id in request.KnowledgeBaseIds)
            {
                if (!allowedSet.Contains(id))
                {
                    return StatusCode(403, new { message = $"无权访问指定的知识库 '{id}' 或该知识库未在授权列表中。" });
                }
            }
        }

        var allowedSources = ApiKeyPermissionMatrix.GetEffectiveAllowedSources(key);
        var caller = new SearchCaller(
            ClientId: key?.Id ?? "anonymous",
            IsAdmin: key == null || string.Equals(key.SourceScopeMode, "all", StringComparison.OrdinalIgnoreCase),
            AuthorizedSourceNames: allowedSources,
            AuthorizedKnowledgeBaseIds: allowedKbs,
            AuthorizedTools: ApiKeyPermissionMatrix.GetEffectiveAllowedTools(key)
        );

        // 1. 优先执行单轨证据检索
        var searchReq = new KnowledgeBaseSearchRequest(
            Query: request.Question,
            Mode: string.IsNullOrWhiteSpace(request.Mode) ? "auto" : request.Mode,
            KnowledgeBaseIds: request.KnowledgeBaseIds,
            TopK: request.TopK > 0 ? request.TopK : 5,
            AllowDegrade: request.AllowDegrade
        )
        {
            Caller = caller,
            AuthorizedSourceNames = allowedSources
        };

        KnowledgeBaseSearchResponse searchRes;
        try
        {
            searchRes = await _searchService.SearchAsync(searchReq, ct);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(403, new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(503, new { message = ex.Message });
        }

        // 2. 质检与短路守卫：无匹配或证据不足时立即短路退出，杜绝空跑 LLM
        if (searchRes.Outcome == "no_match" || searchRes.Hits.Count == 0)
        {
            return Ok(new KnowledgeAnswerResponse(
                Status: "no_match",
                Answer: "未检索到与该问题匹配的知识库证据。",
                Citations: Array.Empty<AnswerCitation>(),
                Completeness: searchRes.Completeness,
                TraceId: searchRes.TraceId
            ));
        }

        if (searchRes.Outcome == "insufficient_evidence")
        {
            return Ok(new KnowledgeAnswerResponse(
                Status: "insufficient_evidence",
                Answer: "知识库现有证据相关度不足，无法支撑可靠有据问答。",
                Citations: Array.Empty<AnswerCitation>(),
                Completeness: searchRes.Completeness,
                TraceId: searchRes.TraceId
            ));
        }

        // 3. 构建短 ID 引用映射表与提示词上下文
        var availableCitations = new List<AnswerCitation>();
        var contextSb = new StringBuilder();
        for (int i = 0; i < searchRes.Hits.Count; i++)
        {
            var hit = searchRes.Hits[i];
            var shortId = $"[{i + 1}]";
            availableCitations.Add(new AnswerCitation(
                ShortId: shortId,
                CitationRef: hit.CitationRef,
                SourceKey: hit.SourceKey,
                StartLine: hit.StartLine,
                EndLine: hit.EndLine
            ));

            contextSb.AppendLine($"{shortId} 文件: {hit.SourceKey} (L{hit.StartLine}-L{hit.EndLine})");
            if (!string.IsNullOrWhiteSpace(hit.HeadingPath))
            {
                contextSb.AppendLine($"标题大纲: {hit.HeadingPath}");
            }
            contextSb.AppendLine($"正文摘录:\n{hit.Excerpt}\n");
        }

        // 4. 若聊天后端不可用，降级返回带引用的结构化证据摘要
        if (_chatService == null)
        {
            return Ok(new KnowledgeAnswerResponse(
                Status: "answered",
                Answer: $"基于知识库证据召回 {availableCitations.Count} 条切片：\n\n" + contextSb,
                Citations: availableCitations,
                Completeness: searchRes.Completeness,
                TraceId: searchRes.TraceId
            ));
        }

        // 5. 执行严谨有据的大模型问答生成
        var systemPrompt = "你是一个严谨的知识库问答助手。请严格仅依据给定的编号参考资料回答用户问题。\n" +
                           "规则：\n" +
                           "1. 回答中的关键事实与结论处必须显式标注引用的编号（例如 [1] 或 [2]）；\n" +
                           "2. 若资料中未包含回答所需的事实，请明确指出资料未涉及，严禁主观编造；\n" +
                           "3. 保持回答客观、精炼、结构清晰。";

        var userPrompt = $"【参考资料】\n{contextSb}\n【用户问题】\n{request.Question}";

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, userPrompt)
        };

        var options = new ChatOptions
        {
            Tools = Array.Empty<AITool>(),
            Temperature = 0.2f
        };

        string answer;
        try
        {
            answer = await _chatService.CompleteAsync(messages, options, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[KnowledgeAnswersController] 生成回答异常");
            return StatusCode(503, new { message = $"回答生成失败: {ex.Message}" });
        }

        // 6. 提取模型实际引用的编号，过滤未知引用
        var citedIndices = new HashSet<int>();
        foreach (Match match in CitationRegex.Matches(answer))
        {
            if (int.TryParse(match.Groups[1].Value, out var idx) && idx >= 1 && idx <= availableCitations.Count)
            {
                citedIndices.Add(idx);
            }
        }

        var usedCitations = citedIndices.Count > 0
            ? availableCitations.Where((_, idx) => citedIndices.Contains(idx + 1)).ToList()
            : availableCitations;

        return Ok(new KnowledgeAnswerResponse(
            Status: "answered",
            Answer: answer,
            Citations: usedCitations,
            Completeness: searchRes.Completeness,
            TraceId: searchRes.TraceId
        ));
    }
}
