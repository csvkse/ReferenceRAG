using ReferenceRAG.Core.Models;
using ReferenceRAG.Infrastructure.Features.ModelManagement.Download.Adapters;
using ReferenceRAG.Infrastructure.Features.ModelManagement.Download.StateMachine;

namespace ReferenceRAG.Infrastructure.Features.ModelManagement.Download.Strategies;

/// <summary>
/// 方案 2: 直接下载 GGUF 格式模型（LLamaSharp 原生加载，零格式转换）
/// </summary>
public class DirectGgufDownloadStrategy : IModelDownloadStrategy
{
    public string StrategyName => "DirectGguf";
    public int Priority => 15;

    public bool CanHandle(ModelManifest manifest, DownloadContext context)
    {
        if (context.TargetFormat?.Equals("onnx", StringComparison.OrdinalIgnoreCase) == true)
            return false;
        if (context.OnnxVariantPath?.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase) == true)
            return false;
        return manifest.HasGguf;
    }

    public async Task ExecuteAsync(
        ModelManifest manifest,
        DownloadContext context,
        DownloadStateMachine stateMachine,
        CancellationToken cancellationToken = default)
    {
        var targetDir = context.TargetDir;
        Directory.CreateDirectory(targetDir);

        var ggufFiles = manifest.GetGgufFiles();
        if (ggufFiles.Count == 0)
        {
            throw new InvalidOperationException($"在模型 {manifest.ModelId} 中未发现 .gguf 文件");
        }

        ModelFileItem targetGguf;
        if (!string.IsNullOrWhiteSpace(context.OnnxVariantPath))
        {
            targetGguf = ggufFiles.FirstOrDefault(f =>
                f.Path.Equals(context.OnnxVariantPath, StringComparison.OrdinalIgnoreCase) ||
                f.FileName.Equals(context.OnnxVariantPath, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"指定的 GGUF 变体不存在: {context.OnnxVariantPath}");
        }
        else
        {
            targetGguf = SelectBestGgufFile(ggufFiles);
        }

        var auxiliaryFiles = manifest.Files.Where(f =>
            f.Path.EndsWith("tokenizer.json", StringComparison.OrdinalIgnoreCase) ||
            f.Path.EndsWith("tokenizer_config.json", StringComparison.OrdinalIgnoreCase) ||
            f.Path.EndsWith("vocab.txt", StringComparison.OrdinalIgnoreCase) ||
            f.Path.EndsWith("special_tokens_map.json", StringComparison.OrdinalIgnoreCase) ||
            f.Path.EndsWith("config.json", StringComparison.OrdinalIgnoreCase)
        ).ToList();

        var filesToDownload = new List<ModelFileItem> { targetGguf };
        filesToDownload.AddRange(auxiliaryFiles);
        filesToDownload = filesToDownload.DistinctBy(f => f.Path).ToList();

        var totalBytes = filesToDownload.Sum(f => f.Size);
        stateMachine.SetTotalBytes(totalBytes);
        stateMachine.TransitionTo(DownloadStage.DownloadingFiles, $"开始下载 GGUF 模型: {targetGguf.FileName} (预计 {totalBytes / 1024.0 / 1024.0:F1} MB)");

        for (int i = 0; i < filesToDownload.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = filesToDownload[i];
            var targetFilePath = Path.Combine(targetDir, file.FileName);

            long fileDownloaded = 0;
            await context.SourceAdapter.DownloadFileAsync(
                manifest.ModelId,
                file.Path,
                targetFilePath,
                (chunkSize, totalFileRead) =>
                {
                    fileDownloaded = totalFileRead;
                    stateMachine.UpdateFileProgress(
                        file.FileName,
                        i + 1,
                        filesToDownload.Count,
                        fileDownloaded,
                        file.Size > 0 ? file.Size : fileDownloaded,
                        chunkSize);
                },
                cancellationToken);
        }

        stateMachine.TransitionTo(DownloadStage.VerifyingFiles, "校验 GGUF 模型文件...");
        var downloadedGguf = Path.Combine(targetDir, targetGguf.FileName);
        if (!File.Exists(downloadedGguf) || new FileInfo(downloadedGguf).Length == 0)
        {
            throw new InvalidOperationException($"GGUF 文件下载不完整: {downloadedGguf}");
        }

        stateMachine.SetCompleted($"GGUF 模型下载完成: {targetGguf.FileName}");
    }

    private static ModelFileItem SelectBestGgufFile(List<ModelFileItem> files)
    {
        var priorities = new[] { "q4_k_m", "q4_k_s", "q4_0", "q5_k_m", "q5_k_s", "q5_0", "q8_0", "q4_1", "f16" };
        foreach (var p in priorities)
        {
            var matched = files.FirstOrDefault(f => f.Path.Contains(p, StringComparison.OrdinalIgnoreCase));
            if (matched != null) return matched;
        }

        return files.OrderBy(f => f.Size > 0 ? f.Size : long.MaxValue).First();
    }
}
