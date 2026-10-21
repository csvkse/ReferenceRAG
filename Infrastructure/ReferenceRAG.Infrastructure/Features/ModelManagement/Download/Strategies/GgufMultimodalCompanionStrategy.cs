namespace ReferenceRAG.Infrastructure.Features.ModelManagement.Download.Strategies;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ReferenceRAG.Infrastructure.Features.ModelManagement.Download.Adapters;

/// <summary>
/// GGUF 视觉模型多模态伴生分析与投影器匹配策略（Strategy Pattern）
/// 负责识别视觉模型、筛选仓库内的 mmproj 投影器，并依据精度优先与亲和度规则推荐最佳配对
/// </summary>
public static class GgufMultimodalCompanionStrategy
{
    private static readonly string[] PrecisionPriorities = new[]
    {
        "f16", "fp16", "bf16", "q8_0", "q6_k", "q5_k_m", "q5_0", "q4_k_m", "q4_0"
    };

    private static readonly string[] FamilyKeywords = new[]
    {
        "qwen3.5", "qwen3.5-vl", "qwen3-vl", "qwen2.5", "qwen2.5-vl", "qwen2-vl", "qwen-vl",
        "llava-1.6", "llava-v1.6", "llava-1.5", "llava-v1.5", "llava",
        "phi-3.5-vision", "phi-3-vision", "phi3v", "gemma-4", "gemma-3", "gemma", "minicpm-v", "omni"
    };

    /// <summary>
    /// 判断文件名是否属于视觉投影器 / 视觉编码器 (mmproj)
    /// </summary>
    public static bool IsMmproj(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        var lower = fileName.ToLowerInvariant();
        return lower.EndsWith(".gguf") && (
            lower.StartsWith("mmproj") ||
            lower.Contains("-mmproj-") ||
            lower.Contains("_mmproj_") ||
            lower.Contains("-mmproj.") ||
            lower.Contains("_mmproj.") ||
            lower.Contains("mmproj"));
    }

    /// <summary>
    /// 判断模型名称或 ID 是否暗示为多模态视觉模型
    /// </summary>
    public static bool IsLikelyVisionModel(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        return Regex.IsMatch(name, @"(?i)(?:-vl\b|_vl\b|vision|llava|minicpm-v|qwen.*vl|qwen3\.5|gemma-?4|omni|多模态|视觉)");
    }

    /// <summary>
    /// 依据模型主名称与候选 mmproj 列表，按精度优先 (F16) 与家族亲和度筛选最佳投影器
    /// </summary>
    public static ModelFileItem? PickBestMmproj(string modelName, IList<ModelFileItem> mmprojFiles)
    {
        if (mmprojFiles == null || mmprojFiles.Count == 0) return null;
        if (mmprojFiles.Count == 1) return mmprojFiles[0];

        var lowerModel = (modelName ?? string.Empty).ToLowerInvariant();

        // 1. 尝试匹配模型家族亲和度
        string? matchedFamily = null;
        foreach (var fam in FamilyKeywords)
        {
            if (lowerModel.Contains(fam))
            {
                matchedFamily = fam;
                break;
            }
        }

        var candidates = mmprojFiles;
        if (!string.IsNullOrEmpty(matchedFamily))
        {
            var familyFiltered = mmprojFiles
                .Where(f => f.FileName.ToLowerInvariant().Contains(matchedFamily))
                .ToList();

            if (familyFiltered.Count > 0)
            {
                candidates = familyFiltered;
            }
        }

        // 2. 精度优先评选 (F16/FP16 优先，防止视觉特征量化失真)
        foreach (var prec in PrecisionPriorities)
        {
            var matchedPrec = candidates.FirstOrDefault(f =>
                f.FileName.ToLowerInvariant().Contains(prec));
            if (matchedPrec != null)
            {
                return matchedPrec;
            }
        }

        // 3. 兜底选取最大尺寸的投影器（通常体积最大的保留了最高的 ViT 精度）
        return candidates.OrderByDescending(f => f.Size).First();
    }

    /// <summary>
    /// 从清单中寻找最适配的主模型伴生投影器
    /// </summary>
    public static ModelFileItem? ResolveCompanionMmproj(string modelName, ModelManifest manifest)
    {
        if (manifest == null || manifest.Files.Count == 0) return null;
        var mmprojs = manifest.GetMmprojFiles();
        return PickBestMmproj(modelName, mmprojs);
    }
}
