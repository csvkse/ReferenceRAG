namespace ReferenceRAG.Core.Services;

using System;
using System.IO;
using System.Linq;

/// <summary>
/// ONNX Runtime GenAI (OGA) 物理模型目录与变体路径解析器。
/// 能够自动处理不同格式规范（单文件 .onnx、包含 genai_config.json 的根目录、以及带有 gpu/cpu 分支子目录的官方分发包），
/// 确保原生推理解码引擎始终获得包含 genai_config.json 与权重分词器的有效目录。
/// </summary>
public static class OnnxGenAiPathResolver
{
    /// <summary>
    /// 自适应解析 ONNX GenAI 模型的有效物理目录。
    /// 无论输入是模型文件路径、父级模型包根目录，还是具体的变体子目录，
    /// 均自愈定位到包含 genai_config.json 的物理目录（优先匹配 DirectML / GPU 加速目录）。
    /// </summary>
    public static string? ResolveModelDirectory(string? inputPath)
    {
        if (string.IsNullOrWhiteSpace(inputPath)) return null;

        var candidate = File.Exists(inputPath) ? Path.GetDirectoryName(inputPath) : inputPath;
        if (string.IsNullOrWhiteSpace(candidate) || !Directory.Exists(candidate)) return null;

        // 1. 目标目录自身即为标准的 OGA 目录
        if (File.Exists(Path.Combine(candidate, "genai_config.json")))
        {
            return candidate;
        }

        // 2. 递归查找子目录中的 genai_config.json，优先 GPU/DirectML，其次 CUDA，最后 CPU
        try
        {
            var configFiles = Directory.GetFiles(candidate, "genai_config.json", SearchOption.AllDirectories);
            if (configFiles.Length > 0)
            {
                var bestConfig = configFiles
                    .OrderByDescending(f => f.Contains("gpu", StringComparison.OrdinalIgnoreCase) || f.Contains("directml", StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(f => f.Contains("cuda", StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(f => f.Contains("cpu", StringComparison.OrdinalIgnoreCase))
                    .First();

                return Path.GetDirectoryName(bestConfig);
            }
        }
        catch
        {
            // 忽略扫描时的 I/O 权限异常
        }

        return null;
    }

    /// <summary>
    /// 检查指定路径或其子目录是否包含合法的 ONNX GenAI 运行环境
    /// </summary>
    public static bool IsValidOgaDirectory(string? inputPath)
    {
        var resolved = ResolveModelDirectory(inputPath);
        if (string.IsNullOrEmpty(resolved) || !Directory.Exists(resolved)) return false;

        var hasConfig = File.Exists(Path.Combine(resolved, "genai_config.json"));
        var hasModel = File.Exists(Path.Combine(resolved, "model.onnx")) || Directory.GetFiles(resolved, "*.onnx").Length > 0;
        return hasConfig && hasModel;
    }
}
