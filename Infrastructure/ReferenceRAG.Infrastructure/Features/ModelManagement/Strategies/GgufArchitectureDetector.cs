using System.Text;

namespace ReferenceRAG.Core.Features.ModelManagement.Strategies;

/// <summary>
/// GGUF 模型架构与模型家族特征轻量级检测器
/// </summary>
public static class GgufArchitectureDetector
{
    /// <summary>
    /// 检测 GGUF 模型的架构阵营（causal 代表自回归因果大模型，bert 代表带分类头的编码器模型）
    /// </summary>
    public static string DetectArchitecture(string filePath, string modelName = "")
    {
        var combined = $"{modelName} {Path.GetFileName(filePath)}".ToLowerInvariant();

        // 1. 基于模型名称与文件名的毫秒级匹配
        if (combined.Contains("qwen") ||
            combined.Contains("llama") ||
            combined.Contains("gemma") ||
            combined.Contains("mistral") ||
            combined.Contains("minicpm") ||
            combined.Contains("glm") ||
            combined.Contains("internlm"))
        {
            return "causal";
        }

        if (combined.Contains("bert") ||
            combined.Contains("roberta") ||
            combined.Contains("bge-reranker-base") ||
            combined.Contains("bge-reranker-large"))
        {
            return "bert";
        }

        // 2. 浅层二进制头探针（读取前 64KB 元数据区）
        try
        {
            if (File.Exists(filePath))
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var buffer = new byte[Math.Min(65536, (int)fs.Length)];
                var read = fs.Read(buffer, 0, buffer.Length);
                var text = Encoding.ASCII.GetString(buffer, 0, read);

                if (text.Contains("qwen2") || text.Contains("llama") || text.Contains("gemma") || text.Contains("mistral"))
                {
                    return "causal";
                }

                if (text.Contains("bert") || text.Contains("roberta"))
                {
                    return "bert";
                }
            }
        }
        catch { }

        // 现代开源大模型重排器（如 Qwen3-Reranker）主流为因果自回归架构
        return "causal";
    }
}
