using ReferenceRAG.Core.Models;
using ReferenceRAG.Infrastructure.Features.ModelManagement.Download.Adapters;
using ReferenceRAG.Infrastructure.Features.ModelManagement.Download.StateMachine;

namespace ReferenceRAG.Infrastructure.Features.ModelManagement.Download.Strategies;

/// <summary>
/// 方案 3: 社区镜像源探测与下载策略 (onnx-community / Xenova / optimum)
/// 针对官方仅含 PyTorch 权重的模型，自动寻找社区转换完成的免转 ONNX 模型
/// </summary>
public class CommunityMirrorDownloadStrategy : IModelDownloadStrategy
{
    private readonly DirectOnnxDownloadStrategy _onnxStrategy = new();
    private readonly DirectGgufDownloadStrategy _ggufStrategy = new();

    public string StrategyName => "CommunityMirror";
    public int Priority => 20;

    public bool CanHandle(ModelManifest manifest, DownloadContext context)
    {
        // 1. 如果显式指定了外部仓库且与当前清单不同
        if (!string.IsNullOrEmpty(context.SourceRepository) &&
            !context.SourceRepository.Equals(manifest.ModelId, StringComparison.OrdinalIgnoreCase))
            return true;

        // 2. 如果请求特定格式但当前清单不满足
        if (context.TargetFormat?.Equals("gguf", StringComparison.OrdinalIgnoreCase) == true && !manifest.HasGguf)
            return true;
        if (context.TargetFormat?.Equals("onnx", StringComparison.OrdinalIgnoreCase) == true && !manifest.HasOnnx)
            return true;

        // 3. 如果请求特定变体文件但在当前清单中找不到
        if (!string.IsNullOrWhiteSpace(context.OnnxVariantPath) &&
            !manifest.Files.Any(f => f.Path.Equals(context.OnnxVariantPath, StringComparison.OrdinalIgnoreCase) ||
                                     f.FileName.Equals(context.OnnxVariantPath, StringComparison.OrdinalIgnoreCase)))
            return true;

        // 4. 官方原生既不含 ONNX 也不含 GGUF 时触发探查
        return !manifest.HasOnnx && !manifest.HasGguf;
    }

    public async Task ExecuteAsync(
        ModelManifest manifest,
        DownloadContext context,
        DownloadStateMachine stateMachine,
        CancellationToken cancellationToken = default)
    {
        stateMachine.TransitionTo(DownloadStage.ProbingMirrors, "正在探测免转镜像源 (GGUF / onnx-community / Xenova)...");

        var parts = manifest.ModelId.Split('/');
        var repoName = parts.Last();
        var org = parts.Length > 1 ? parts[0] : "";

        // 优先探测官方或社区 GGUF 仓库，其次探测 onnx-community 与 Xenova
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(context.SourceRepository) &&
            !candidates.Contains(context.SourceRepository, StringComparer.OrdinalIgnoreCase))
        {
            candidates.Add(context.SourceRepository);
        }

        if (!string.IsNullOrEmpty(org))
        {
            candidates.Add($"{manifest.ModelId}-GGUF");
        }
        candidates.Add($"onnx-community/{repoName}");
        candidates.Add($"Xenova/{repoName}");
        candidates.Add($"second-state/{repoName}-GGUF");
        candidates.Add($"bartowski/{repoName}-GGUF");
        candidates.Add($"QuantFactory/{repoName}-GGUF");
        candidates.Add($"optimum/{repoName}");

        bool preferGguf = context.TargetFormat?.Equals("gguf", StringComparison.OrdinalIgnoreCase) == true
                       || context.OnnxVariantPath?.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) == true;
        bool preferOnnx = context.TargetFormat?.Equals("onnx", StringComparison.OrdinalIgnoreCase) == true
                       || context.OnnxVariantPath?.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase) == true;

        foreach (var candidate in candidates)
        {
            if (candidate.Equals(manifest.ModelId, StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrEmpty(context.SourceRepository)) continue;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                stateMachine.TransitionTo(DownloadStage.ProbingMirrors, $"正在尝试探测免转镜像: {candidate} ...");

                var mirrorManifest = await context.SourceAdapter.GetManifestAsync(candidate, cancellationToken);
                if (mirrorManifest != null)
                {
                    if (preferGguf)
                    {
                        if (mirrorManifest.HasGguf)
                        {
                            Console.WriteLine($"[CommunityMirror] 命中社区 GGUF 镜像: [{candidate}]");
                            stateMachine.TransitionTo(DownloadStage.ResolvingManifest, $"命中预置 GGUF 镜像 [{candidate}]，切换免转换直接下载");
                            await _ggufStrategy.ExecuteAsync(mirrorManifest, context, stateMachine, cancellationToken);
                            return;
                        }
                    }
                    else if (preferOnnx)
                    {
                        if (mirrorManifest.HasOnnx)
                        {
                            Console.WriteLine($"[CommunityMirror] 命中社区 ONNX 镜像: [{candidate}]");
                            stateMachine.TransitionTo(DownloadStage.ResolvingManifest, $"命中预置 ONNX 镜像 [{candidate}]，切换免转换直接下载");
                            await _onnxStrategy.ExecuteAsync(mirrorManifest, context, stateMachine, cancellationToken);
                            return;
                        }
                    }
                    else
                    {
                        if (mirrorManifest.HasGguf)
                        {
                            Console.WriteLine($"[CommunityMirror] 命中社区 GGUF 镜像: [{candidate}]");
                            stateMachine.TransitionTo(DownloadStage.ResolvingManifest, $"命中预置 GGUF 镜像 [{candidate}]，切换免转换直接下载");
                            await _ggufStrategy.ExecuteAsync(mirrorManifest, context, stateMachine, cancellationToken);
                            return;
                        }
                        else if (mirrorManifest.HasOnnx)
                        {
                            Console.WriteLine($"[CommunityMirror] 命中社区 ONNX 镜像: [{candidate}]");
                            stateMachine.TransitionTo(DownloadStage.ResolvingManifest, $"命中预置 ONNX 镜像 [{candidate}]，切换免转换直接下载");
                            await _onnxStrategy.ExecuteAsync(mirrorManifest, context, stateMachine, cancellationToken);
                            return;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 忽略候选不存在（404）或权限错误，继续探查下一个候选
                Console.WriteLine($"[CommunityMirror] 候选镜像 {candidate} 不适用: {ex.Message}");
            }
        }

        throw new InvalidOperationException($"在所有免转镜像候选源中未发现 '{repoName}' 的预转换 ONNX 或 GGUF 模型");
    }
}
