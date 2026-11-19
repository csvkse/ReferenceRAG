using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Core.Features.Search.Fusion;

namespace ReferenceRAG.Business.Features.KnowledgeBase.Services;

/// <summary>
/// 结构化版本化证据引用契约 (CitationRef)
/// 覆盖知识库、文档版本与切片正文位置，支持重切片与版本漂移检验。
/// </summary>
public sealed record CitationRef(
    [property: JsonPropertyName("knowledgeBaseId")] string KnowledgeBaseId,
    [property: JsonPropertyName("documentId")] string DocumentId,
    [property: JsonPropertyName("chunkId")] string ChunkId,
    [property: JsonPropertyName("documentVersion")] long DocumentVersion,
    [property: JsonPropertyName("digest")] string Digest)
{
    public static string ComputeDigest(string chunkId, string text, int startLine, int endLine)
    {
        var input = $"{chunkId}:{startLine}:{endLine}:{text.Trim()}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..16];
    }
}

/// <summary>
/// 单条可用受权证据命中项
/// </summary>
public sealed record KnowledgeEvidenceHit(
    [property: JsonPropertyName("citationRef")] CitationRef CitationRef,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("sourceKey")] string SourceKey,
    [property: JsonPropertyName("startLine")] int StartLine,
    [property: JsonPropertyName("endLine")] int EndLine,
    [property: JsonPropertyName("excerpt")] string Excerpt,
    [property: JsonPropertyName("score")] double Score,
    [property: JsonPropertyName("channels")] IReadOnlyList<string> Channels,
    [property: JsonPropertyName("knowledgeBaseId")] string KnowledgeBaseId,
    [property: JsonPropertyName("scoreKind")] ScoreKind ScoreKind = ScoreKind.RRF,
    [property: JsonPropertyName("isFloor")] bool IsFloor = false,
    [property: JsonPropertyName("coverage")] double? Coverage = null,
    [property: JsonPropertyName("headingPath")] string? HeadingPath = null)
{
    [JsonPropertyName("chunkId")]
    public string ChunkId => CitationRef.ChunkId;

    [JsonPropertyName("documentId")]
    public string DocumentId => CitationRef.DocumentId;

    [JsonPropertyName("documentVersion")]
    public long DocumentVersion => CitationRef.DocumentVersion;

    public RecallItem ToRecallItem()
    {
        RecallChannel ch = RecallChannel.None;
        if (Channels != null)
        {
            foreach (var c in Channels)
            {
                if (string.Equals(c, "lexical", StringComparison.OrdinalIgnoreCase)) ch |= RecallChannel.Lexical;
                if (string.Equals(c, "vector", StringComparison.OrdinalIgnoreCase)) ch |= RecallChannel.Vector;
            }
        }
        if (ch == RecallChannel.None) ch = RecallChannel.Lexical;

        return new RecallItem(
            ChunkId: CitationRef.ChunkId,
            DocumentId: CitationRef.DocumentId,
            SourceKey: SourceKey,
            Text: Excerpt,
            Score: Score,
            ScoreKind: ScoreKind,
            DocumentVersion: CitationRef.DocumentVersion,
            ConnectorId: null,
            Locator: null,
            StartLine: StartLine,
            EndLine: EndLine,
            HeadingPath: HeadingPath,
            Coverage: Coverage,
            IsFloor: IsFloor,
            Channels: ch,
            KnowledgeBaseId: KnowledgeBaseId
        );
    }
}

/// <summary>
/// 单个知识库执行情况记录
/// </summary>
public sealed record KnowledgeBaseExecutionRecord(
    [property: JsonPropertyName("knowledgeBaseId")] string KnowledgeBaseId,
    [property: JsonPropertyName("plannedChannels")] IReadOnlyList<string> PlannedChannels,
    [property: JsonPropertyName("completedChannels")] IReadOnlyList<string> CompletedChannels,
    [property: JsonPropertyName("status")] string Status, // "success", "partial", "failed", "skipped"
    [property: JsonPropertyName("reasonCode")] string? ReasonCode = null);

/// <summary>
/// 可信服务端调用者上下文
/// </summary>
public sealed record SearchCaller(
    string ClientId,
    bool IsAdmin = false,
    IReadOnlyList<string>? AuthorizedSourceNames = null,
    IReadOnlyList<string>? AuthorizedKnowledgeBaseIds = null,
    IReadOnlyList<string>? AuthorizedTools = null);

/// <summary>
/// 统一知识库单轨检索请求 (KnowledgeBaseSearchRequest)
/// </summary>
public sealed record KnowledgeBaseSearchRequest(
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("mode")] string Mode = "auto",
    [property: JsonPropertyName("knowledgeBaseIds")] IReadOnlyList<string>? KnowledgeBaseIds = null,
    [property: JsonPropertyName("connectorId")] string? ConnectorId = null,
    [property: JsonPropertyName("pathPrefix")] string? PathPrefix = null,
    [property: JsonPropertyName("topK")] int TopK = 5,
    [property: JsonPropertyName("allowDegrade")] bool AllowDegrade = false,
    [property: JsonPropertyName("folders")] IReadOnlyList<string>? Folders = null,
    [property: JsonPropertyName("queryVector")] float[]? QueryVector = null,
    [property: JsonPropertyName("useLexicalTokenizer")] bool UseLexicalTokenizer = false)
{
    [JsonIgnore]
    public SearchCaller? Caller { get; init; }

    [JsonIgnore]
    public IReadOnlyList<string>? AuthorizedSourceNames { get; init; }

    public static implicit operator KnowledgeBaseSearchRequest(CrossKnowledgeBaseSearchRequest old) => new(
        Query: old.Query,
        Mode: old.UseLexicalTokenizer ? "lexical" : "auto",
        KnowledgeBaseIds: (old.KnowledgeBaseIds != null && old.KnowledgeBaseIds.Count == 0) ? null : old.KnowledgeBaseIds,
        TopK: old.TopK,
        AllowDegrade: old.AllowDegrade,
        Folders: old.Folders,
        QueryVector: old.QueryVector,
        UseLexicalTokenizer: old.UseLexicalTokenizer
    )
    {
        AuthorizedSourceNames = old.AuthorizedSourceNames
    };
}

/// <summary>
/// 统一知识库单轨检索响应 (KnowledgeBaseSearchResponse)
/// </summary>
public sealed record KnowledgeBaseSearchResponse(
    [property: JsonPropertyName("outcome")] string Outcome, // "matched", "no_match", "insufficient_evidence"
    [property: JsonPropertyName("completeness")] string Completeness, // "complete", "partial"
    [property: JsonPropertyName("requestedMode")] string RequestedMode,
    [property: JsonPropertyName("hits")] IReadOnlyList<KnowledgeEvidenceHit> Hits,
    [property: JsonPropertyName("executions")] IReadOnlyList<KnowledgeBaseExecutionRecord> Executions,
    [property: JsonPropertyName("traceId")] string? TraceId = null,
    [property: JsonPropertyName("elapsedMs")] long ElapsedMs = 0,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string>? Warnings = null,
    [property: JsonPropertyName("truncated")] bool Truncated = false)
{
    // 兼容字段：平滑支撑依赖旧契约内部组件
    [JsonPropertyName("items")]
    public IReadOnlyList<RecallItem> Items => Hits.Select(h => h.ToRecallItem()).ToList();

    [JsonPropertyName("query")]
    public string Query { get; init; } = "";

    [JsonPropertyName("successfulKnowledgeBaseIds")]
    public IReadOnlyList<string> SuccessfulKnowledgeBaseIds =>
        Executions.Where(e => e.Status is "success" or "partial").Select(e => e.KnowledgeBaseId).ToList();

    [JsonPropertyName("failedKnowledgeBaseIds")]
    public IReadOnlyList<string> FailedKnowledgeBaseIds =>
        Executions.Where(e => e.Status == "failed").Select(e => e.KnowledgeBaseId).ToList();

    [JsonPropertyName("partialSuccess")]
    public bool PartialSuccess => Completeness == "partial" && Hits.Count > 0;

    [JsonPropertyName("scoreKind")]
    public ScoreKind ScoreKind => ScoreKind.RRF;

    [JsonPropertyName("elapsed")]
    public TimeSpan Elapsed => TimeSpan.FromMilliseconds(ElapsedMs);

    [JsonPropertyName("noMatch")]
    public bool NoMatch => Outcome != "matched";

    [JsonPropertyName("noMatchReason")]
    public string? NoMatchReason => Executions.FirstOrDefault(e => !string.IsNullOrEmpty(e.ReasonCode))?.ReasonCode;

    [JsonPropertyName("usedRecallFloor")]
    public bool UsedRecallFloor => Hits.Any(h => h.IsFloor);

    [JsonPropertyName("timingChain")]
    public ReferenceRAG.Core.Tracing.TimingChain? TimingChain { get; init; }

    [JsonPropertyName("earlyPruned")]
    public bool EarlyPruned { get; init; } = false;

    [JsonPropertyName("degraded")]
    public bool Degraded { get; init; } = false;

    [JsonPropertyName("degradationReason")]
    public string? DegradationReason { get; init; } = null;

    [JsonPropertyName("indexDegraded")]
    public bool IndexDegraded { get; init; } = false;

    [JsonPropertyName("kbChannels")]
    public IReadOnlyDictionary<string, string> KbChannels =>
        Executions.ToDictionary(e => e.KnowledgeBaseId, e => e.CompletedChannels.Count > 1 ? "hybrid" : (e.CompletedChannels.FirstOrDefault() ?? "none"));

    public static implicit operator CrossKnowledgeBaseSearchResponse(KnowledgeBaseSearchResponse r) => new(
        Query: r.Query,
        Items: r.Items,
        SuccessfulKnowledgeBaseIds: r.SuccessfulKnowledgeBaseIds,
        FailedKnowledgeBaseIds: r.FailedKnowledgeBaseIds,
        PartialSuccess: r.PartialSuccess,
        ScoreKind: r.ScoreKind,
        Elapsed: r.Elapsed,
        EarlyPruned: r.EarlyPruned,
        Degraded: r.Degraded,
        DegradationReason: r.DegradationReason,
        IndexDegraded: r.IndexDegraded,
        TimingChain: r.TimingChain,
        UsedRecallFloor: r.UsedRecallFloor,
        KbChannels: r.KbChannels,
        NoMatch: r.NoMatch,
        NoMatchReason: r.NoMatchReason
    );
}

/// <summary>
/// 证据批量回读请求
/// </summary>
public sealed record EvidenceReadRequest(
    [property: JsonPropertyName("citations")] IReadOnlyList<CitationRef> Citations);

/// <summary>
/// 单项核验证据正文
/// </summary>
public sealed record VerifiedEvidenceItem(
    [property: JsonPropertyName("citation")] CitationRef Citation,
    [property: JsonPropertyName("status")] string Status, // "ok", "stale_reference", "not_found", "forbidden"
    [property: JsonPropertyName("text")] string? Text = null,
    [property: JsonPropertyName("startLine")] int StartLine = 0,
    [property: JsonPropertyName("endLine")] int EndLine = 0,
    [property: JsonPropertyName("headingPath")] string? HeadingPath = null,
    [property: JsonPropertyName("sourceKey")] string? SourceKey = null);

/// <summary>
/// 证据批量回读响应
/// </summary>
public sealed record EvidenceReadResponse(
    [property: JsonPropertyName("items")] IReadOnlyList<VerifiedEvidenceItem> Items);

/// <summary>
/// 知识库有据问答请求
/// </summary>
public sealed record KnowledgeAnswerRequest(
    [property: JsonPropertyName("question")] string Question,
    [property: JsonPropertyName("knowledgeBaseIds")] IReadOnlyList<string>? KnowledgeBaseIds = null,
    [property: JsonPropertyName("mode")] string Mode = "auto",
    [property: JsonPropertyName("topK")] int TopK = 5,
    [property: JsonPropertyName("allowDegrade")] bool AllowDegrade = false);

/// <summary>
/// 问答短 ID 引用映射项
/// </summary>
public sealed record AnswerCitation(
    [property: JsonPropertyName("shortId")] string ShortId,
    [property: JsonPropertyName("citationRef")] CitationRef CitationRef,
    [property: JsonPropertyName("sourceKey")] string SourceKey,
    [property: JsonPropertyName("startLine")] int StartLine,
    [property: JsonPropertyName("endLine")] int EndLine);

/// <summary>
/// 知识库有据问答响应
/// </summary>
public sealed record KnowledgeAnswerResponse(
    [property: JsonPropertyName("status")] string Status, // "answered", "insufficient_evidence", "no_match"
    [property: JsonPropertyName("answer")] string? Answer,
    [property: JsonPropertyName("citations")] IReadOnlyList<AnswerCitation> Citations,
    [property: JsonPropertyName("completeness")] string Completeness,
    [property: JsonPropertyName("usage")] TokenUsage? Usage = null,
    [property: JsonPropertyName("traceId")] string? TraceId = null);

/// <summary>
/// Token 计量统计
/// </summary>
public sealed record TokenUsage(
    [property: JsonPropertyName("promptTokens")] int PromptTokens,
    [property: JsonPropertyName("completionTokens")] int CompletionTokens,
    [property: JsonPropertyName("totalTokens")] int TotalTokens);

/// <summary>
/// 知识库状态概览响应
/// </summary>
public sealed record KnowledgeBaseSearchStatusResponse(
    [property: JsonPropertyName("accessibleKnowledgeBases")] IReadOnlyList<KnowledgeBaseStatusSummary> AccessibleKnowledgeBases,
    [property: JsonPropertyName("recentJobs")] IReadOnlyList<JobDefinition> RecentJobs,
    [property: JsonPropertyName("timestamp")] DateTime Timestamp);

/// <summary>
/// 单个知识库状态摘要
/// </summary>
public sealed record KnowledgeBaseStatusSummary(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("isPureLexical")] bool IsPureLexical,
    [property: JsonPropertyName("embeddingSpecId")] string EmbeddingSpecId,
    [property: JsonPropertyName("documentCount")] int DocumentCount,
    [property: JsonPropertyName("chunkCount")] int ChunkCount,
    [property: JsonPropertyName("lexicalReady")] bool LexicalReady);
