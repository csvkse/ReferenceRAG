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

        var textGgufFiles = manifest.GetTextGgufFiles();
        var mmprojFiles = manifest.GetMmprojFiles();
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
            targetGguf = SelectBestGgufFile(textGgufFiles.Count > 0 ? textGgufFiles : ggufFiles);
        }

        // 解析并匹配伴生视觉投影器 (mmproj)
        ModelFileItem? companionMmproj = null;
        if (!targetGguf.IsMmproj && mmprojFiles.Count > 0)
        {
            companionMmproj = GgufMultimodalCompanionStrategy.PickBestMmproj(targetGguf.FileName, mmprojFiles);
        }

        var auxiliaryFiles = manifest.Files.Where(f =>
            f.Path.EndsWith("tokenizer.json", StringComparison.OrdinalIgnoreCase) ||
            f.Path.EndsWith("tokenizer_config.json", StringComparison.OrdinalIgnoreCase) ||
            f.Path.EndsWith("vocab.txt", StringComparison.OrdinalIgnoreCase) ||
            f.Path.EndsWith("special_tokens_map.json", StringComparison.OrdinalIgnoreCase) ||
            f.Path.EndsWith("config.json", StringComparison.OrdinalIgnoreCase)
        ).ToList();

        // 组装待下载候选文件清单
        var candidateFiles = new List<ModelFileItem> { targetGguf };
        if (companionMmproj != null)
        {
            candidateFiles.Add(companionMmproj);
        }
        candidateFiles.AddRange(auxiliaryFiles);
        candidateFiles = candidateFiles.DistinctBy(f => f.Path).ToList();

        // 增量自愈检查：若某个文件在本地已完整存在，则跳过传输，支持断点与增量挂载 mmproj
        var filesToDownload = new List<ModelFileItem>();
        foreach (var file in candidateFiles)
        {
            var localPath = Path.Combine(targetDir, file.FileName);
            if (File.Exists(localPath) && new FileInfo(localPath).Length > 0 &&
                (file.Size <= 0 || new FileInfo(localPath).Length == file.Size))
            {
                Console.WriteLine($"[DirectGguf] 本地已存在完整文件，跳过下载: {file.FileName}");
                continue;
            }
            filesToDownload.Add(file);
        }

        if (filesToDownload.Count == 0)
        {
            stateMachine.SetCompleted($"GGUF 模型及组件已在本地就绪: {targetGguf.FileName}" +
                (companionMmproj != null ? $" (已配对视觉投影器: {companionMmproj.FileName})" : ""));
            return;
        }

        var totalBytes = filesToDownload.Sum(f => f.Size);
        stateMachine.SetTotalBytes(totalBytes);

        var startMsg = companionMmproj != null
            ? $"开始下载 GGUF 多模态模型: {targetGguf.FileName} + 视觉投影器 {companionMmproj.FileName} (待下载 {totalBytes / 1024.0 / 1024.0:F1} MB)"
            : $"开始下载 GGUF 模型: {targetGguf.FileName} (待下载 {totalBytes / 1024.0 / 1024.0:F1} MB)";
        stateMachine.TransitionTo(DownloadStage.DownloadingFiles, startMsg);

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

        stateMachine.TransitionTo(DownloadStage.VerifyingFiles, "校验 GGUF 模型文件与视觉组件完整性...");
        var downloadedGguf = Path.Combine(targetDir, targetGguf.FileName);
        if (!File.Exists(downloadedGguf) || new FileInfo(downloadedGguf).Length == 0)
        {
            throw new InvalidOperationException($"GGUF 文件下载不完整: {downloadedGguf}");
        }

        if (companionMmproj != null)
        {
            var downloadedMmproj = Path.Combine(targetDir, companionMmproj.FileName);
            if (!File.Exists(downloadedMmproj) || new FileInfo(downloadedMmproj).Length == 0)
            {
                throw new InvalidOperationException($"伴生视觉投影器文件下载不完整: {downloadedMmproj}");
            }
        }

        var completeMsg = companionMmproj != null
            ? $"GGUF 多模态模型下载完成: {targetGguf.FileName} (已配对视觉投影器: {companionMmproj.FileName})"
            : $"GGUF 模型下载完成: {targetGguf.FileName}";
        stateMachine.SetCompleted(completeMsg);
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
