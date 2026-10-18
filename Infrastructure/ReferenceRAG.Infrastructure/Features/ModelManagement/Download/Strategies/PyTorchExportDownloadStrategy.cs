using System.Diagnostics;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Infrastructure.Features.ModelManagement.Download.Adapters;
using ReferenceRAG.Infrastructure.Features.ModelManagement.Download.StateMachine;

namespace ReferenceRAG.Infrastructure.Features.ModelManagement.Download.Strategies;

/// <summary>
/// 方案 4: 下载 PyTorch 原始权重并在本地通过 Python/Optimum 执行 ONNX 导出
/// </summary>
public class PyTorchExportDownloadStrategy : IModelDownloadStrategy
{
    public string StrategyName => "PyTorchExport";
    public int Priority => 30;

    public bool CanHandle(ModelManifest manifest, DownloadContext context)
    {
        // 含有 safetensors 或 bin 原始权重
        return manifest.Files.Any(f =>
            f.Path.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase) ||
            f.Path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase));
    }

    public async Task ExecuteAsync(
        ModelManifest manifest,
        DownloadContext context,
        DownloadStateMachine stateMachine,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsPythonAvailable && !IsPythonAvailable())
        {
            throw new InvalidOperationException(
                $"模型 '{manifest.ModelId}' 仅包含 PyTorch 原始权重，未检测到预转换的 ONNX 或 GGUF 格式。\n" +
                $"系统未检测到 Python 运行环境，无法在本地执行 PyTorch 模型导出。\n\n" +
                $"建议解决方案：\n" +
                $"1. 优先选择列表已适配 ONNX/GGUF 的模型；\n" +
                $"2. 或在终端执行: optimum-cli export onnx --model {manifest.ModelId} {context.TargetDir}");
        }

        var targetDir = context.TargetDir;
        Directory.CreateDirectory(targetDir);

        var requiredFiles = new[]
        {
            "pytorch_model.bin", "model.safetensors", "config.json",
            "tokenizer.json", "tokenizer_config.json", "vocab.txt", "special_tokens_map.json",
            "tokenizer.model", "sentencepiece.bpe.model", "bpe.vocab"
        };

        var filesToDownload = manifest.Files.Where(f =>
            requiredFiles.Contains(f.FileName, StringComparer.OrdinalIgnoreCase) ||
            f.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
            f.Path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ||
            f.Path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) ||
            f.Path.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase) ||
            f.Path.EndsWith(".model", StringComparison.OrdinalIgnoreCase)
        ).DistinctBy(f => f.Path).ToList();

        var totalBytes = filesToDownload.Sum(f => f.Size);
        stateMachine.SetTotalBytes(totalBytes);
        stateMachine.TransitionTo(DownloadStage.DownloadingFiles, $"正在下载 PyTorch 模型权重 (共 {filesToDownload.Count} 个文件，预计 {totalBytes / 1024.0 / 1024.0:F1} MB)");

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

        var onnxPath = Path.Combine(targetDir, "model.onnx");
        if (File.Exists(onnxPath))
        {
            stateMachine.SetCompleted("ONNX 模型已存在，无需转换");
            return;
        }

        stateMachine.TransitionTo(DownloadStage.Converting, "执行本地 PyTorch 模型导出与 ONNX 转换...");

        var format = context.TargetFormat ?? (totalBytes > 1610612736 ? "external" : "embedded");
        var success = await ExecutePythonExportAsync(targetDir, onnxPath, format, context.SourceAdapter.ActiveEndpoint, cancellationToken);

        if (!success)
        {
            throw new InvalidOperationException($"PyTorch 模型导出 ONNX 失败，请检查 Python 及 optimum, transformers 依赖");
        }

        stateMachine.TransitionTo(DownloadStage.VerifyingFiles, "校验导出的 ONNX 文件...");
        if (!File.Exists(onnxPath) || new FileInfo(onnxPath).Length == 0)
        {
            throw new InvalidOperationException("ONNX 导出完成但未找到有效的 model.onnx 文件");
        }

        stateMachine.SetCompleted("ONNX 模型成功导出并就绪");
    }

    private static bool IsPythonAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "python",
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(5000);
            return p?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> ExecutePythonExportAsync(
        string modelDir,
        string outputPath,
        string format,
        string hfEndpoint,
        CancellationToken cancellationToken)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"convert_onnx_{Guid.NewGuid():N}.py");
        try
        {
            await File.WriteAllTextAsync(scriptPath, GetConversionScript(), System.Text.Encoding.UTF8, cancellationToken);

            var startInfo = new ProcessStartInfo
            {
                FileName = "python",
                Arguments = $"\"{scriptPath}\" --model_dir \"{modelDir}\" --output \"{outputPath}\" --format {format}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = modelDir,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };
            startInfo.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            startInfo.EnvironmentVariables["HF_ENDPOINT"] = hfEndpoint;

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            var errorBuilder = new System.Text.StringBuilder();
            process.ErrorDataReceived += (s, e) => { if (e.Data != null) errorBuilder.AppendLine(e.Data); };
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0 && File.Exists(outputPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PyTorchExport] 导出异常: {ex.Message}");
            return false;
        }
        finally
        {
            try { if (File.Exists(scriptPath)) File.Delete(scriptPath); } catch { }
        }
    }

    private static string GetConversionScript()
    {
        return """
import argparse, sys, os, shutil
if sys.platform == 'win32':
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
    sys.stderr.reconfigure(encoding='utf-8', errors='replace')

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--model_dir', required=True)
    parser.add_argument('--output', required=True)
    parser.add_argument('--format', default='embedded')
    args = parser.parse_args()

    # 优先尝试 optimum 导出
    import subprocess
    cmd = [
        "optimum-cli", "export", "onnx",
        "--model", args.model_dir,
        os.path.dirname(args.output)
    ]
    res = subprocess.run(cmd)
    if res.returncode == 0 and os.path.exists(args.output):
        print("Optimum export completed successfully.")
        return

    # 兜底使用 torch.onnx 导出
    import torch
    from transformers import AutoTokenizer, AutoModel, AutoModelForSequenceClassification
    import json
    
    config_file = os.path.join(args.model_dir, "config.json")
    is_reranker = False
    if os.path.exists(config_file):
        with open(config_file, 'r', encoding='utf-8') as f:
            c = json.load(f)
            archs = c.get("architectures", [])
            if any("SequenceClassification" in a for a in archs):
                is_reranker = True

    tokenizer = AutoTokenizer.from_pretrained(args.model_dir)
    if is_reranker:
        model = AutoModelForSequenceClassification.from_pretrained(args.model_dir)
        dummy = tokenizer("query", "doc", return_tensors="pt")
        output_names = ["logits"]
    else:
        model = AutoModel.from_pretrained(args.model_dir)
        dummy = tokenizer("query", return_tensors="pt")
        output_names = ["last_hidden_state"]

    inputs = (dummy["input_ids"], dummy["attention_mask"])
    input_names = ["input_ids", "attention_mask"]
    if "token_type_ids" in dummy:
        inputs += (dummy["token_type_ids"],)
        input_names.append("token_type_ids")

    dynamic_axes = {k: {0: "batch", 1: "seq"} for k in input_names}
    dynamic_axes[output_names[0]] = {0: "batch"}

    torch.onnx.export(
        model, inputs, args.output,
        input_names=input_names,
        output_names=output_names,
        dynamic_axes=dynamic_axes,
        opset_version=14,
        do_constant_folding=True
    )
    print("TorchScript ONNX export succeeded.")

if __name__ == "__main__":
    main()
""";
    }
}
