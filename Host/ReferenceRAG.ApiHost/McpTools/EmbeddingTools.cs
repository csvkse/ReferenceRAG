using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ReferenceRAG.Core.Interfaces;

namespace ReferenceRAG.Service.McpTools;

/// <summary>
/// 向量化相关工具（归属 search 业务模块）
/// </summary>
[McpServerToolType]
public class EmbeddingTools
{
    /// <summary>单条向量化字符上限（实测 40,000 字符耗时 27.6 秒，会拖垮 Agent 超时预算）。</summary>
    private const int MaxEmbedChars = McpInputAdapters.MaxEmbedChars;

    private readonly IServiceProvider _serviceProvider;

    public EmbeddingTools(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <summary>
    /// 将文本转换为向量表示（Embedding）。
    /// 在需要理解文本语义表示或进行相似度计算时使用。
    /// </summary>
    /// <param name="text">待向量化的文本（必填）</param>
    [McpServerTool(Title = "文本向量化")]
    [Description("将文本转换为向量表示（Embedding）。用于获取文本的语义向量、计算文本相似度、或进行向量化相关的分析。返回向量维度和向量范数信息。")]
    public async Task<string> EmbedText(
        [Description("待向量化的文本（必填）。可以是任何需要理解语义的文本，如句子、段落、问题等")] string text)
    {
        using var scope = _serviceProvider.CreateScope();
        if (!McpPermissionGuard.CheckPermission(scope.ServiceProvider, nameof(EmbedText), out var permErr))
        {
            return McpResult.Fail(permErr, McpErrorCodes.Forbidden);
        }
        var embeddingService = scope.ServiceProvider.GetRequiredService<IEmbeddingService>();

        // 边界钳制：实测 40,000 字符输入耗时 27.6 秒且无任何提示，Agent 侧必超时。
        if (text.Length > MaxEmbedChars)
            return McpResult.Fail($"文本过长：{text.Length} 字符，上限 {MaxEmbedChars}。请分段后多次调用。", McpErrorCodes.TooLarge);

        try
        {
            var vector = await embeddingService.EncodeAsync(text);
            var dimension = vector.Length;

            return McpResult.Ok(new
            {
                success = true,
                text = text.Length > 100 ? text[..100] + "..." : text,
                dimension = dimension,
                modelName = embeddingService.ModelName,
                vectorPreview = vector.Take(10).ToArray(),
                vectorNorm = Math.Sqrt(vector.Sum(v => v * v))
            });
        }
        catch (Exception ex)
        {
            return McpResult.FailException(ex);
        }
    }

    /// <summary>
    /// 计算两段文本的语义相似度。
    /// 在需要比较两个文本是否相关或相似时使用。
    /// </summary>
    /// <param name="text1">第一段文本（必填）</param>
    /// <param name="text2">第二段文本（必填）</param>
    [McpServerTool(Title = "文本相似度")]
    [Description("计算两段文本的语义相似度（余弦相似度）。用于判断两个文本在语义上是否相似。返回0-1之间的相似度分数和文字解释。")]
    public async Task<string> CalculateSimilarity(
        [Description("第一段文本（必填）")] string text1,
        [Description("第二段文本（必填）")] string text2)
    {
        using var scope = _serviceProvider.CreateScope();
        if (!McpPermissionGuard.CheckPermission(scope.ServiceProvider, nameof(CalculateSimilarity), out var permErr))
        {
            return McpResult.Fail(permErr, McpErrorCodes.Forbidden);
        }
        var embeddingService = scope.ServiceProvider.GetRequiredService<IEmbeddingService>();

        // 与 embed_text 同一条上限：此前 9000 字符可绕过检查，实测 6 秒且无提示。
        if (text1?.Length > MaxEmbedChars || text2?.Length > MaxEmbedChars)
        {
            var (longer, len) = (text1?.Length ?? 0) >= (text2?.Length ?? 0)
                ? ("text1", text1?.Length ?? 0)
                : ("text2", text2?.Length ?? 0);
            return McpResult.Fail($"{longer} 过长：{len} 字符，上限 {MaxEmbedChars}。请分段或缩短后重试。", McpErrorCodes.TooLarge);
        }

        try
        {
            var vector1 = await embeddingService.EncodeAsync(text1);
            var vector2 = await embeddingService.EncodeAsync(text2);

            var similarity = CosineSimilarity(vector1, vector2);

            return McpResult.Ok(new
            {
                success = true,
                similarity = similarity,
                text1Preview = text1.Length > 50 ? text1[..50] + "..." : text1,
                text2Preview = text2.Length > 50 ? text2[..50] + "..." : text2,
                interpretation = similarity switch
                {
                    > 0.9f => "高度相似",
                    > 0.7f => "较为相似",
                    > 0.5f => "有一定相似性",
                    > 0.3f => "相似性较低",
                    _ => "几乎不相关"
                }
            });
        }
        catch (Exception ex)
        {
            return McpResult.FailException(ex);
        }
    }

    /// <summary>
    /// 批量向量化多段文本。
    /// 在需要一次处理多个文本时使用，提高效率。
    /// </summary>
    /// <param name="texts">待向量化的文本列表（必填，JSON数组格式）</param>
    [McpServerTool(Title = "批量向量化")]
    [Description("批量向量化多段文本。用于一次性处理多个文本，提高效率。输入为 JSON 数组字符串，如[\"文本1\",\"文本2\",\"文本3\"]。")]
    public async Task<string> EmbedBatch(
        [Description("待向量化的文本列表（必填，最多 100 条），须为 JSON 数组字符串，如[\"文本1\",\"文本2\"]；单条上限 8000 字符")] string texts)
    {
        using var scope = _serviceProvider.CreateScope();
        if (!McpPermissionGuard.CheckPermission(scope.ServiceProvider, nameof(EmbedBatch), out var permErr))
        {
            return McpResult.Fail(permErr, McpErrorCodes.Forbidden);
        }
        var embeddingService = scope.ServiceProvider.GetRequiredService<IEmbeddingService>();

        List<string> textList;
        try
        {
            textList = McpInputAdapters.ParseStringArray(texts, nameof(texts));
            McpInputAdapters.EnsureItemCount(nameof(texts), textList.Count, McpInputAdapters.MaxEmbedBatchItems);
        }
        catch (McpInputArgumentException ex)
        {
            return McpResult.Fail(ex.Message, McpErrorCodes.InvalidArgument);
        }

        try
        {
            var oversize = textList.FirstOrDefault(t => t.Length > MaxEmbedChars);
            if (oversize != null)
                return McpResult.Fail(
                    $"存在超长文本（{oversize.Length} 字符），单条上限 {MaxEmbedChars}。请分段后重试。", McpErrorCodes.TooLarge);

            var results = new List<object>();

            foreach (var text in textList)
            {
                var vector = await embeddingService.EncodeAsync(text);
                results.Add(new
                {
                    text = text.Length > 50 ? text[..50] + "..." : text,
                    dimension = vector.Length,
                    vectorNorm = Math.Sqrt(vector.Sum(v => v * v))
                });
            }

            return McpResult.Ok(new
            {
                success = true,
                count = results.Count,
                modelName = embeddingService.ModelName,
                results = results
            });
        }
        catch (Exception ex)
        {
            return McpResult.FailException(ex);
        }
    }

    private static float CosineSimilarity(float[] vector1, float[] vector2)
    {
        if (vector1.Length != vector2.Length)
            throw new ArgumentException("Vectors must have the same dimension");

        float dotProduct = 0;
        float norm1 = 0;
        float norm2 = 0;

        for (int i = 0; i < vector1.Length; i++)
        {
            dotProduct += vector1[i] * vector2[i];
            norm1 += vector1[i] * vector1[i];
            norm2 += vector2[i] * vector2[i];
        }

        if (norm1 == 0 || norm2 == 0)
            return 0;

        return dotProduct / (float)(Math.Sqrt(norm1) * Math.Sqrt(norm2));
    }
}
