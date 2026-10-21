namespace ReferenceRAG.Business.Features.Chat.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// 视觉模型生命周期与配对状态
/// </summary>
public enum VisionModelStatus
{
    /// <summary>纯文本模型，不具备多模态能力</summary>
    TextOnly,

    /// <summary>模型架构支持视觉（VLM），但缺少必要的独立组件（如 GGUF 缺少 mmproj 或 ONNX 缺少 vision.onnx）</summary>
    Unpaired,

    /// <summary>视觉组件已完整配对就绪，支持图片上传与视觉多模态理解</summary>
    PairedReady,

    /// <summary>多模态引擎运行中</summary>
    Active,

    /// <summary>安全降级至纯文本模式</summary>
    Degraded
}

/// <summary>
/// 视觉能力评估与组件匹配结果
/// </summary>
public record VisionMatchingResult(
    bool SupportsVision,
    VisionModelStatus Status,
    string? MatchedProjectorPath,
    string ModelFamily,
    string Description,
    string? GuidanceMessage)
{
    public static VisionMatchingResult TextOnly(string modelName, string? reason = null) =>
        new(false, VisionModelStatus.TextOnly, null, "Text", "纯语言模型",
            reason ?? $"当前模型【{modelName}】为纯语言模型（LLM），未挂载多模态视觉投影器（mmproj），无法直接识别图像。如需使用图片识别，请切换为多模态视觉模型（如 Qwen2-VL）或云端 API。");

    public static VisionMatchingResult Unpaired(string modelName, string missingComponent, string guidance) =>
        new(false, VisionModelStatus.Unpaired, null, "Multimodal", $"视觉架构但缺少 {missingComponent}", guidance);

    public static VisionMatchingResult Paired(string modelName, string? projectorPath, string description) =>
        new(true, VisionModelStatus.PairedReady, projectorPath, "Multimodal", description, "已成功挂载视觉组件，多模态推理就绪。");
}

/// <summary>
/// 视觉能力匹配策略接口（Strategy Pattern）
/// </summary>
public interface IVisionCapabilityMatchingStrategy
{
    string BackendType { get; }
    VisionMatchingResult Match(string modelName, string? modelPath);
}

/// <summary>
/// 本地 GGUF 视觉投影器（mmproj）扫描与亲和度匹配策略
/// </summary>
public class GgufVisionMatchingStrategy : IVisionCapabilityMatchingStrategy
{
    public string BackendType => "LocalGguf";

    public VisionMatchingResult Match(string modelName, string? modelPath)
    {
        var safeName = modelName ?? string.Empty;
        var dir = ResolveDirectory(modelPath);

        // 1. 若目录不存在，根据命名规则判定
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            return IsLikelyVisionModel(safeName)
                ? VisionMatchingResult.Unpaired(safeName, "mmproj-*.gguf", $"检测到该模型【{safeName}】为视觉多模态架构，但未找到模型存放目录。请确保模型与配套的 mmproj 投影器已下载。")
                : VisionMatchingResult.TextOnly(safeName);
        }

        // 2. 扫描同目录或上级模型目录下的 mmproj 文件
        var mmprojFiles = Directory.GetFiles(dir, "*mmproj*.gguf", SearchOption.TopDirectoryOnly).ToList();
        if (mmprojFiles.Count == 0 && Directory.GetParent(dir) is { Exists: true } parent)
        {
            mmprojFiles.AddRange(Directory.GetFiles(parent.FullName, "*mmproj*.gguf", SearchOption.TopDirectoryOnly));
        }

        // 3. 亲和度匹配算法（Affinity Scoring）
        if (mmprojFiles.Count > 0)
        {
            var bestMatch = PickBestMmproj(safeName, mmprojFiles);
            if (bestMatch != null)
            {
                var fileName = Path.GetFileName(bestMatch);
                return VisionMatchingResult.Paired(safeName, bestMatch, $"已自动匹配并挂载配套 GGUF 视觉投影器: {fileName}");
            }
        }

        // 4. 未找到匹配的 mmproj 投影器
        if (IsLikelyVisionModel(safeName))
        {
            return VisionMatchingResult.Unpaired(
                safeName,
                "mmproj-*.gguf",
                $"检测到模型【{safeName}】为多模态架构，但在当前模型目录下未找到配套的 mmproj 视觉投影器文件（如 mmproj-F16.gguf）。请下载对应的投影器放入模型目录以激活视觉识别。");
        }

        return VisionMatchingResult.TextOnly(safeName);
    }

    private static string? ResolveDirectory(string? modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath)) return null;
        if (Directory.Exists(modelPath)) return modelPath;
        if (File.Exists(modelPath)) return Path.GetDirectoryName(modelPath);
        return null;
    }

    private static bool IsLikelyVisionModel(string name)
    {
        return Regex.IsMatch(name, @"(?i)(?:-vl\b|_vl\b|vision|llava|minicpm-v|qwen.*vl|qwen3\.5|gemma-?4|omni|多模态|视觉)");
    }

    private static string? PickBestMmproj(string modelName, IList<string> candidates)
    {
        if (candidates.Count == 0) return null;
        if (candidates.Count == 1) return candidates[0];

        // 提取模型家族关键词（如 qwen, llava, phi, gemma, minicpm）
        var familyKeywords = new[] { "qwen3.5", "qwen2.5", "qwen2", "qwen", "llava", "phi", "gemma", "minicpm" };
        var matchedFamily = familyKeywords.FirstOrDefault(k => modelName.Contains(k, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(matchedFamily))
        {
            var familyMatch = candidates.FirstOrDefault(c => Path.GetFileName(c).Contains(matchedFamily, StringComparison.OrdinalIgnoreCase));
            if (familyMatch != null) return familyMatch;
        }

        // 兜底返回第一个
        return candidates[0];
    }
}

/// <summary>
/// 本地 ONNX 多计算图拓扑探测策略
/// </summary>
public class OnnxVisionMatchingStrategy : IVisionCapabilityMatchingStrategy
{
    public string BackendType => "LocalOnnxGenAi";

    public VisionMatchingResult Match(string modelName, string? modelPath)
    {
        var safeName = modelName ?? string.Empty;
        var dir = ResolveDirectory(modelPath);

        if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
        {
            // 1. 探测视觉子计算图文件 (vision.onnx / vision_encoder*.onnx)
            var hasVisionGraph = Directory.GetFiles(dir, "*vision*.onnx", SearchOption.TopDirectoryOnly).Length > 0 ||
                                 Directory.GetFiles(dir, "*image_features*.onnx", SearchOption.TopDirectoryOnly).Length > 0;

            // 2. 探测 genai_config.json 中的视觉配置
            var configPath = Path.Combine(dir, "genai_config.json");
            var hasVisionConfig = false;
            if (File.Exists(configPath))
            {
                try
                {
                    var text = File.ReadAllText(configPath);
                    hasVisionConfig = text.Contains("\"vision\"", StringComparison.OrdinalIgnoreCase) ||
                                      text.Contains("phi3v", StringComparison.OrdinalIgnoreCase) ||
                                      text.Contains("qwen2_vl", StringComparison.OrdinalIgnoreCase);
                }
                catch { }
            }

            if (hasVisionGraph || hasVisionConfig)
            {
                return VisionMatchingResult.Paired(safeName, dir, "ONNX 视觉计算图组件完整，已就绪支持 DirectML 多模态推理。");
            }
        }

        if (Regex.IsMatch(safeName, @"(?i)(?:vision|phi-3.5-vision|qwen.*vl|omni)"))
        {
            return VisionMatchingResult.Unpaired(
                safeName,
                "vision.onnx",
                $"检测到 ONNX 视觉模型配置【{safeName}】，但在模型目录中未检测到完整的 vision_encoder.onnx 视觉计算图文件。");
        }

        return VisionMatchingResult.TextOnly(safeName, $"当前 ONNX 模型【{safeName}】为纯语言模型，无视觉编码器计算图。");
    }

    private static string? ResolveDirectory(string? modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath)) return null;
        if (Directory.Exists(modelPath)) return modelPath;
        if (File.Exists(modelPath)) return Path.GetDirectoryName(modelPath);
        return null;
    }
}

/// <summary>
/// 外部云端 API 模型能力字典与元数据匹配策略
/// </summary>
public class ExternalApiVisionMatchingStrategy : IVisionCapabilityMatchingStrategy
{
    public string BackendType => "OpenAiApi";

    private static readonly Regex KnownVisionApiRegex = new(
        @"(?i)(?:gpt-4o|gpt-4-turbo|gpt-4-vision|gemini|claude-3|qwen.*vl|qwen.*vision|glm-4v|deepseek-vl|internvl|yi-vl)",
        RegexOptions.Compiled);

    public VisionMatchingResult Match(string modelName, string? modelPath)
    {
        var safeName = modelName ?? string.Empty;
        if (KnownVisionApiRegex.IsMatch(safeName))
        {
            return VisionMatchingResult.Paired(safeName, null, "云端大模型 API 原生支持视觉多模态图像输入。");
        }

        return VisionMatchingResult.TextOnly(safeName, $"当前线上模型【{safeName}】为纯文本模型，不支持多模态图像识别。如需使用图片识别，请切换为 gpt-4o、qwen-vl-max 等多模态模型。");
    }
}

/// <summary>
/// 复合视觉能力匹配器（Facade / Composite Pattern）
/// 统一管理并调度三端匹配策略，提供高并发线程安全的缓存探测。
/// </summary>
public class CompositeVisionCapabilityMatcher
{
    private readonly Dictionary<string, IVisionCapabilityMatchingStrategy> _strategies = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, VisionMatchingResult> _cache = new(StringComparer.OrdinalIgnoreCase);

    public CompositeVisionCapabilityMatcher(IEnumerable<IVisionCapabilityMatchingStrategy>? strategies = null)
    {
        var list = strategies ?? [
            new GgufVisionMatchingStrategy(),
            new OnnxVisionMatchingStrategy(),
            new ExternalApiVisionMatchingStrategy()
        ];

        foreach (var s in list)
        {
            _strategies[s.BackendType] = s;
        }
    }

    public VisionMatchingResult Match(string backendType, string modelName, string? modelPath)
    {
        var cacheKey = $"{backendType}::{modelName}::{modelPath}";
        return _cache.GetOrAdd(cacheKey, _ =>
        {
            if (_strategies.TryGetValue(backendType, out var strategy))
            {
                return strategy.Match(modelName, modelPath);
            }

            // 兜底退回 GGUF 或纯文本
            if (_strategies.TryGetValue("LocalGguf", out var gguf))
            {
                return gguf.Match(modelName, modelPath);
            }

            return VisionMatchingResult.TextOnly(modelName);
        });
    }

    public void InvalidateCache()
    {
        _cache.Clear();
    }
}
