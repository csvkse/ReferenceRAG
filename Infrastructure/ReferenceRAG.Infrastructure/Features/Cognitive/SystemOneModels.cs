using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ReferenceRAG.Infrastructure.Features.Cognitive;

/// <summary>
/// TypeSafe AI System One 问题类型定义
/// </summary>
public enum SystemOneQuestionType
{
    /// <summary>是非/布尔判断，返回 0.0 ~ 1.0 的真值概率</summary>
    Noul,
    /// <summary>多项选择，返回每个选项的校准概率分布</summary>
    Choice,
    /// <summary>分级打分，返回连续分值与档位概率</summary>
    Score
}

/// <summary>
/// TypeSafe AI System One 强类型问题定义
/// </summary>
public sealed class SystemOneQuestion
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "noul"; // "noul" | "choice" | "score"

    [JsonPropertyName("instructions")]
    public string Instructions { get; set; } = string.Empty;

    [JsonPropertyName("criteria")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Criteria { get; set; }

    public static SystemOneQuestion Noul(string instructions) => new()
    {
        Type = "noul",
        Instructions = instructions
    };

    public static SystemOneQuestion Choice(string instructions, IDictionary<string, string> criteria) => new()
    {
        Type = "choice",
        Instructions = instructions,
        Criteria = criteria
    };

    public static SystemOneQuestion Score(string instructions, IEnumerable<string> orderedCriteria) => new()
    {
        Type = "score",
        Instructions = instructions,
        Criteria = orderedCriteria
    };
}

/// <summary>
/// System One 请求体
/// </summary>
public sealed class SystemOneRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "jev-latest";

    [JsonPropertyName("state")]
    public object State { get; set; } = string.Empty;

    [JsonPropertyName("questions")]
    public Dictionary<string, SystemOneQuestion> Questions { get; set; } = new();
}

/// <summary>
/// System One 单题回答数据
/// </summary>
public sealed class SystemOneAnswer
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    // Noul 结果
    [JsonPropertyName("noul")]
    public double? Noul { get; set; }

    // Choice 结果
    [JsonPropertyName("choice")]
    public string? Choice { get; set; }

    [JsonPropertyName("confidence")]
    public double? Confidence { get; set; }

    [JsonPropertyName("probabilities")]
    public Dictionary<string, double>? Probabilities { get; set; }

    // Score 结果
    [JsonPropertyName("score")]
    public double? Score { get; set; }

    [JsonPropertyName("level")]
    public string? Level { get; set; }
}

/// <summary>
/// Token 开销统计
/// </summary>
public sealed class SystemOneUsage
{
    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; set; }

    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; set; }
}

/// <summary>
/// System One 响应载荷
/// </summary>
public sealed class SystemOneResponse
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("answers")]
    public Dictionary<string, SystemOneAnswer> Answers { get; set; } = new();

    [JsonPropertyName("usage")]
    public SystemOneUsage? Usage { get; set; }

    [JsonPropertyName("detail")]
    public string? Detail { get; set; }
}
