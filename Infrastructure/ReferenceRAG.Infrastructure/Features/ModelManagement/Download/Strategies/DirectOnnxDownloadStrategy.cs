using ReferenceRAG.Core.Models;
using ReferenceRAG.Infrastructure.Features.ModelManagement.Download.Adapters;
using ReferenceRAG.Infrastructure.Features.ModelManagement.Download.StateMachine;

namespace ReferenceRAG.Infrastructure.Features.ModelManagement.Download.Strategies;

/// <summary>
/// 方案 1: 直接下载仓库预置的 ONNX 模型
/// 精准选择单一 ONNX 变体，彻底根治多变体全量下载缺陷
/// </summary>
public class DirectOnnxDownloadStrategy : IModelDownloadStrategy
{
    public string StrategyName => "DirectOnnx";
    public int Priority => 10;

    public bool CanHandle(ModelManifest manifest, DownloadContext context)
    {
        if (context.TargetFormat?.Equals("gguf", StringComparison.OrdinalIgnoreCase) == true)
            return false;
        if (context.OnnxVariantPath?.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) == true)
            return false;
        return manifest.HasOnnx;
    }

    public async Task ExecuteAsync(
        ModelManifest manifest,
        DownloadContext context,
        DownloadStateMachine stateMachine,
        CancellationToken cancellationToken = default)
    {
        var targetDir = context.TargetDir;
        Directory.CreateDirectory(targetDir);

        var (onnxFiles, needMoveFromSubfolder) = SelectTargetOnnxFiles(manifest, context.OnnxVariantPath);

        // 收集必备的配套配置文件与词表文件
        var configFiles = manifest.Files.Where(f =>
            f.Path.EndsWith("tokenizer.json", StringComparison.OrdinalIgnoreCase) ||
            f.Path.EndsWith("tokenizer_config.json", StringComparison.OrdinalIgnoreCase) ||
            f.Path.EndsWith("vocab.txt", StringComparison.OrdinalIgnoreCase) ||
            f.Path.EndsWith("special_tokens_map.json", StringComparison.OrdinalIgnoreCase) ||
            f.Path.EndsWith("tokenizer.model", StringComparison.OrdinalIgnoreCase) ||
            f.Path.EndsWith("sentencepiece.bpe.model", StringComparison.OrdinalIgnoreCase) ||
            f.Path.EndsWith("config.json", StringComparison.OrdinalIgnoreCase) ||
            f.Path.EndsWith("modules.json", StringComparison.OrdinalIgnoreCase) ||
            f.Path.EndsWith("sentence_bert_config.json", StringComparison.OrdinalIgnoreCase) ||
            f.Path.Equals("1_Pooling/config.json", StringComparison.OrdinalIgnoreCase)
        ).ToList();

        var filesToDownload = onnxFiles.Concat(configFiles).DistinctBy(f => f.Path).ToList();

        // 刷新总传输大小
        var totalBytes = filesToDownload.Sum(f => f.Size);
        stateMachine.SetTotalBytes(totalBytes);
        stateMachine.TransitionTo(DownloadStage.DownloadingFiles, $"开始下载 ONNX 模型 (共 {filesToDownload.Count} 个文件，预计 {totalBytes / 1024.0 / 1024.0:F1} MB)");

        for (int i = 0; i < filesToDownload.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = filesToDownload[i];
            var targetFilePath = Path.Combine(targetDir, file.Path);

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

        // 校验与子目录文件规整
        stateMachine.TransitionTo(DownloadStage.VerifyingFiles, "校验 ONNX 模型文件并归整目录结构...");

        var rootOnnx = Path.Combine(targetDir, "model.onnx");
        if (!File.Exists(rootOnnx))
        {
            // 检查子目录下是否有 model.onnx
            var foundOnnx = Directory.GetFiles(targetDir, "*.onnx", SearchOption.AllDirectories)
                .OrderBy(p => p.Contains("model.onnx", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .FirstOrDefault();

            if (foundOnnx != null)
            {
                var srcDir = Path.GetDirectoryName(foundOnnx)!;
                if (!string.Equals(srcDir, targetDir, StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(foundOnnx, rootOnnx, true);

                    // 联动迁移外部数据
                    var subData = Directory.GetFiles(srcDir, "*.onnx_data", SearchOption.TopDirectoryOnly)
                        .Concat(Directory.GetFiles(srcDir, "*.onnx.data", SearchOption.TopDirectoryOnly))
                        .FirstOrDefault();
                    if (subData != null)
                    {
                        File.Copy(subData, Path.Combine(targetDir, "model.onnx_data"), true);
                    }
                }
            }
        }

        if (!File.Exists(rootOnnx))
        {
            throw new InvalidOperationException("模型下载不完整: 目录中未找到有效的 model.onnx 文件");
        }

        stateMachine.SetCompleted("ONNX 模型下载与校验完成");
    }

    private static (List<ModelFileItem> Files, bool NeedMove) SelectTargetOnnxFiles(ModelManifest manifest, string? requestedVariant)
    {
        var allOnnxFiles = manifest.Files.Where(f => f.IsOnnx).ToList();
        if (allOnnxFiles.Count == 0)
        {
            throw new InvalidOperationException("清单中未包含任何 .onnx 模型文件");
        }

        // 1. 如果用户明确指定了变体
        if (!string.IsNullOrWhiteSpace(requestedVariant))
        {
            var matched = allOnnxFiles.FirstOrDefault(f =>
                f.Path.Equals(requestedVariant, StringComparison.OrdinalIgnoreCase) ||
                f.FileName.Equals(requestedVariant, StringComparison.OrdinalIgnoreCase));

            if (matched == null)
            {
                throw new InvalidOperationException($"指定的 ONNX 变体 '{requestedVariant}' 在模型仓库中不存在");
            }

            var result = new List<ModelFileItem> { matched };
            AddCompanionDataFile(manifest, matched, result);
            return (result, matched.Path.Contains('/'));
        }

        // 2. 根目录优先查找 model.onnx
        var rootModelOnnx = allOnnxFiles.FirstOrDefault(f => f.Path.Equals("model.onnx", StringComparison.OrdinalIgnoreCase));
        if (rootModelOnnx != null)
        {
            var result = new List<ModelFileItem> { rootModelOnnx };
            AddCompanionDataFile(manifest, rootModelOnnx, result);
            return (result, false);
        }

        // 3. 子目录查找 onnx/model.onnx (核心修复：只取单一目标，绝不把全部变体加入)
        var subModelOnnx = allOnnxFiles.FirstOrDefault(f => f.Path.Equals("onnx/model.onnx", StringComparison.OrdinalIgnoreCase));
        if (subModelOnnx != null)
        {
            var result = new List<ModelFileItem> { subModelOnnx };
            AddCompanionDataFile(manifest, subModelOnnx, result);
            return (result, true);
        }

        // 4. 其它单文件候选：按标准命名优先级提取单一文件
        var fallback = allOnnxFiles.OrderBy(f => f.Path.Contains("int8") || f.Path.Contains("q4") ? 1 : 0)
                                   .ThenBy(f => f.Size > 0 ? f.Size : long.MaxValue)
                                   .First();

        var fallbackResult = new List<ModelFileItem> { fallback };
        AddCompanionDataFile(manifest, fallback, fallbackResult);
        return (fallbackResult, fallback.Path.Contains('/'));
    }

    private static void AddCompanionDataFile(ModelManifest manifest, ModelFileItem onnxItem, List<ModelFileItem> targetList)
    {
        var dotData = onnxItem.Path + ".data";
        var basePath = onnxItem.Path.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase)
            ? onnxItem.Path[..^5]
            : onnxItem.Path;
        var underData = basePath + ".onnx_data";

        var companion = manifest.Files.FirstOrDefault(f =>
            f.Path.Equals(dotData, StringComparison.OrdinalIgnoreCase) ||
            f.Path.Equals(underData, StringComparison.OrdinalIgnoreCase));

        if (companion != null && !targetList.Contains(companion))
        {
            targetList.Add(companion);
        }
    }
}
