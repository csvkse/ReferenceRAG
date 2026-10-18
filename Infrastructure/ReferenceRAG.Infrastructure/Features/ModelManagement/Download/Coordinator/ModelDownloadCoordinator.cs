using ReferenceRAG.Core.Models;
using ReferenceRAG.Infrastructure.Features.ModelManagement.Download.Adapters;
using ReferenceRAG.Infrastructure.Features.ModelManagement.Download.StateMachine;
using ReferenceRAG.Infrastructure.Features.ModelManagement.Download.Strategies;

namespace ReferenceRAG.Infrastructure.Features.ModelManagement.Download.Coordinator;

/// <summary>
/// 模型下载与准备协调中心 (Coordinator)
/// 统筹模型源适配器 (Adapter)、策略匹配 (Strategy) 与生命周期状态机 (StateMachine)
/// </summary>
public class ModelDownloadCoordinator
{
    private readonly List<IModelDownloadStrategy> _strategies = new();
    private readonly Func<IModelSourceAdapter> _defaultAdapterFactory;

    public ModelDownloadCoordinator(
        IEnumerable<IModelDownloadStrategy>? strategies = null,
        Func<IModelSourceAdapter>? defaultAdapterFactory = null)
    {
        _defaultAdapterFactory = defaultAdapterFactory ?? (() => new HuggingFaceSourceAdapter());

        if (strategies != null)
        {
            _strategies.AddRange(strategies.OrderBy(s => s.Priority));
        }
        else
        {
            _strategies.Add(new DirectOnnxDownloadStrategy());
            _strategies.Add(new DirectGgufDownloadStrategy());
            _strategies.Add(new CommunityMirrorDownloadStrategy());
            _strategies.Add(new PyTorchExportDownloadStrategy());
            _strategies.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        }
    }

    /// <summary>
    /// 开始下载并准备模型
    /// </summary>
    public async Task DownloadModelAsync(
        string modelId,
        string targetDir,
        DownloadStateMachine stateMachine,
        IModelSourceAdapter? customAdapter = null,
        string? targetFormat = null,
        string? onnxVariantPath = null,
        CancellationToken cancellationToken = default,
        string? repository = null)
    {
        using var adapter = customAdapter ?? _defaultAdapterFactory();

        try
        {
            var repoToFetch = !string.IsNullOrWhiteSpace(repository) ? repository : modelId;
            stateMachine.TransitionTo(DownloadStage.ResolvingManifest, $"正在连接模型源 [{adapter.ActiveEndpoint}]，获取文件清单 ({repoToFetch})...");

            var manifest = await adapter.GetManifestAsync(repoToFetch, cancellationToken);
            if (manifest == null || manifest.Files.Count == 0)
            {
                throw new InvalidOperationException($"无法获取模型 '{repoToFetch}' 的文件列表，请检查网络连接或模型名称是否正确");
            }

            var context = new DownloadContext
            {
                ModelName = modelId,
                TargetDir = targetDir,
                SourceAdapter = adapter,
                TargetFormat = targetFormat,
                OnnxVariantPath = onnxVariantPath,
                SourceRepository = repoToFetch
            };

            // 依据清单智能选取首选执行策略
            var strategy = _strategies.FirstOrDefault(s => s.CanHandle(manifest, context));
            if (strategy == null)
            {
                throw new InvalidOperationException($"未找到可用于模型 '{repoToFetch}' 的下载或导出策略");
            }

            Console.WriteLine($"[DownloadCoordinator] 模型 [{repoToFetch}] 匹配下载策略: {strategy.StrategyName}");

            await strategy.ExecuteAsync(manifest, context, stateMachine, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            stateMachine.SetCancelled();
            throw;
        }
        catch (Exception ex)
        {
            var errCode = ParseErrorCode(ex);
            stateMachine.SetFailed(ex.Message, errCode);
            throw;
        }
    }

    /// <summary>
    /// 探查并获取模型可用的下载选项（包含 ONNX 多变体与 GGUF，支持两者全量聚合）
    /// </summary>
    public async Task<ModelDownloadOptions> GetDownloadOptionsAsync(
        string modelId,
        IModelSourceAdapter? customAdapter = null,
        CancellationToken cancellationToken = default)
    {
        using var adapter = customAdapter ?? _defaultAdapterFactory();
        var result = new ModelDownloadOptions { ModelName = modelId };

        try
        {
            var manifest = await adapter.GetManifestAsync(modelId, cancellationToken);

            var onnxFiles = manifest.GetOnnxFiles();
            var ggufFiles = manifest.GetGgufFiles();
            var dataFiles = manifest.Files.Where(f => f.IsExternalData).ToList();

            // 1. 扫描主仓库中直接包含的 ONNX 与 GGUF 文件
            if (onnxFiles.Count > 0)
            {
                result.HasOnnx = true;
                foreach (var onnxFile in onnxFiles)
                {
                    var opt = CreateOnnxOption(onnxFile, dataFiles);
                    opt.Repository = modelId;
                    result.OnnxOptions.Add(opt);
                    result.AllOptions.Add(opt);
                    if (opt.IsInSubfolder) result.SubfolderOptions.Add(opt);
                    else result.RootOptions.Add(opt);
                }
            }

            if (ggufFiles.Count > 0)
            {
                result.HasGguf = true;
                foreach (var ggufFile in ggufFiles)
                {
                    var opt = CreateGgufOption(ggufFile);
                    opt.Repository = modelId;
                    result.GgufOptions.Add(opt);
                    result.AllOptions.Add(opt);
                    if (opt.IsInSubfolder) result.SubfolderOptions.Add(opt);
                    else result.RootOptions.Add(opt);
                }
            }

            // 2. 若缺少 GGUF 或缺少 ONNX，通过社区候选源分别补充
            if (!result.HasGguf || !result.HasOnnx)
            {
                var parts = modelId.Split('/');
                var repoName = parts.Last();
                var org = parts.Length > 1 ? parts[0] : "";

                var candidates = new List<string>();
                if (!string.IsNullOrEmpty(org))
                {
                    candidates.Add($"{modelId}-GGUF");
                }
                candidates.Add($"onnx-community/{repoName}");
                candidates.Add($"Xenova/{repoName}");
                candidates.Add($"second-state/{repoName}-GGUF");
                candidates.Add($"bartowski/{repoName}-GGUF");
                candidates.Add($"QuantFactory/{repoName}-GGUF");

                var validCandidates = candidates
                    .Where(c => !c.Equals(modelId, StringComparison.OrdinalIgnoreCase))
                    .Distinct()
                    .ToList();

                var lockObj = new object();
                using var semaphore = new SemaphoreSlim(3, 3);
                using var ctsCombined = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                var probeTasks = validCandidates.Select(async candidate =>
                {
                    try
                    {
                        await semaphore.WaitAsync(ctsCombined.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    try
                    {
                        if (ctsCombined.IsCancellationRequested) return;
                        lock (lockObj)
                        {
                            if (result.HasGguf && result.HasOnnx) return;
                        }

                        using var singleCts = new CancellationTokenSource(TimeSpan.FromSeconds(3.5));
                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ctsCombined.Token, singleCts.Token);

                        var communityManifest = await adapter.GetManifestAsync(candidate, linked.Token);
                        if (communityManifest != null)
                        {
                            lock (lockObj)
                            {
                                if (!result.HasGguf && communityManifest.HasGguf)
                                {
                                    result.HasGguf = true;
                                    var communityGguf = communityManifest.GetGgufFiles();
                                    foreach (var file in communityGguf)
                                    {
                                        var opt = CreateGgufOption(file);
                                        opt.Repository = candidate;
                                        opt.Description += $" (来自社区免转源 {candidate})";
                                        result.GgufOptions.Add(opt);
                                        result.AllOptions.Add(opt);
                                        if (opt.IsInSubfolder) result.SubfolderOptions.Add(opt);
                                        else result.RootOptions.Add(opt);
                                    }
                                }

                                if (!result.HasOnnx && communityManifest.HasOnnx)
                                {
                                    result.HasOnnx = true;
                                    var communityOnnx = communityManifest.GetOnnxFiles();
                                    var communityData = communityManifest.Files.Where(f => f.IsExternalData).ToList();
                                    foreach (var file in communityOnnx)
                                    {
                                        var opt = CreateOnnxOption(file, communityData);
                                        opt.Repository = candidate;
                                        opt.Description += $" (来自社区免转源 {candidate})";
                                        result.OnnxOptions.Add(opt);
                                        result.AllOptions.Add(opt);
                                        if (opt.IsInSubfolder) result.SubfolderOptions.Add(opt);
                                        else result.RootOptions.Add(opt);
                                    }
                                }

                                if (result.HasGguf && result.HasOnnx)
                                {
                                    try { ctsCombined.Cancel(); } catch { }
                                }
                            }
                        }
                    }
                    catch
                    {
                        // 单个候选源超时或失败忽略
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }).ToList();

                try
                {
                    await Task.WhenAll(probeTasks);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // 提前探测到所有目标格式引发的短路
                }
            }

            if (result.HasGguf || result.HasOnnx)
            {
                result.NeedsConversion = false;
                DetermineRecommendedOption(result);
            }
            else
            {
                result.HasOnnx = false;
                result.HasGguf = false;
                result.NeedsConversion = true;
                result.EstimatedSize = manifest.Files.Sum(f => f.Size);
            }

            return result;
        }
        catch (Exception ex)
        {
            var msg = ex.Message.ToLowerInvariant();
            if (msg.Contains("401") || msg.Contains("403") || msg.Contains("unauthorized") || msg.Contains("gated"))
            {
                result.IsGatedOrUnauthorized = true;
            }
            result.NeedsConversion = true;
            return result;
        }
    }

    private static OnnxFileOption CreateOnnxOption(ModelFileItem file, List<ModelFileItem> dataFiles)
    {
        var dotData = file.Path + ".data";
        var basePath = file.Path.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase) ? file.Path[..^5] : file.Path;
        var underData = basePath + ".onnx_data";
        var companion = dataFiles.FirstOrDefault(f => f.Path == dotData || f.Path == underData);

        var isInSubfolder = file.Path.Contains('/');
        var fileName = file.FileName;
        var lower = fileName.ToLowerInvariant();

        var opt = new OnnxFileOption
        {
            Path = file.Path,
            DisplayName = fileName,
            Format = "onnx",
            Size = file.Size + (companion?.Size ?? 0),
            HasExternalData = companion != null,
            ExternalDataPath = companion?.Path,
            IsInSubfolder = isInSubfolder,
            IsQuantized = lower.Contains("int8") || lower.Contains("uint8") || lower.Contains("q4") || lower.Contains("bnb4") || lower.Contains("quant")
        };

        if (fileName.Equals("model.onnx", StringComparison.OrdinalIgnoreCase))
        {
            opt.DisplayName = "标准版本 (FP32)";
            opt.Description = "未量化的原始精度模型，最佳精度与最大兼容性";
        }
        else if (lower.Contains("fp16"))
        {
            opt.DisplayName = "半精度版本 (FP16)";
            opt.Description = "16位半精度浮点模型，显存与体积减半";
        }
        else if (lower.Contains("int8") || lower.Contains("q4"))
        {
            opt.DisplayName = $"量化版本 ({fileName})";
            opt.Description = "针对 CPU/NPU 优化的高速低内存版本";
        }

        return opt;
    }

    private static OnnxFileOption CreateGgufOption(ModelFileItem file)
    {
        var fileName = file.FileName;
        var match = System.Text.RegularExpressions.Regex.Match(fileName.ToLowerInvariant(), @"(q[0-9]_[a-z0-9_]+|f16|f32|bf16)");
        var quant = match.Success ? match.Groups[1].Value.ToUpperInvariant() : "GGUF";

        return new OnnxFileOption
        {
            Path = file.Path,
            DisplayName = $"GGUF {quant}",
            Format = "gguf",
            Description = $"GGUF 量化模型 ({quant})，支持 LLamaSharp 原生加载与 GPU Offload",
            Size = file.Size,
            IsQuantized = true,
            IsInSubfolder = file.Path.Contains('/'),
            IsRecommended = quant.Contains("Q4_K") || quant.Contains("Q4_0")
        };
    }

    private static void DetermineRecommendedOption(ModelDownloadOptions options)
    {
        if (options.AllOptions.Count == 0) return;

        // 优先推荐：已标记推荐 > GGUF Q4量化 > ONNX model.onnx
        var std = options.AllOptions.FirstOrDefault(o => o.IsRecommended)
               ?? options.AllOptions.FirstOrDefault(o => o.Format == "gguf" && (o.DisplayName.Contains("Q4") || o.DisplayName.Contains("Q5")))
               ?? options.AllOptions.FirstOrDefault(o => o.Path.EndsWith("model.onnx", StringComparison.OrdinalIgnoreCase) && !o.HasExternalData)
               ?? options.AllOptions.FirstOrDefault(o => o.Path.EndsWith("model.onnx", StringComparison.OrdinalIgnoreCase))
               ?? options.AllOptions[0];

        foreach (var opt in options.AllOptions)
        {
            opt.IsRecommended = (opt == std);
        }
        options.RecommendedOption = std;
    }

    private static DownloadErrorCode ParseErrorCode(Exception ex)
    {
        if (ex is UnauthorizedAccessException) return DownloadErrorCode.AuthorizationRequired;
        if (ex is KeyNotFoundException) return DownloadErrorCode.ModelNotFound;
        if (ex is HttpRequestException) return DownloadErrorCode.NetworkError;
        var msg = ex.Message.ToLowerInvariant();
        if (msg.Contains("python")) return DownloadErrorCode.PythonNotFound;
        if (msg.Contains("optimum") || msg.Contains("transformers")) return DownloadErrorCode.DependenciesMissing;
        if (msg.Contains("完整") || msg.Contains("incomplete")) return DownloadErrorCode.FileIncomplete;
        return DownloadErrorCode.Unknown;
    }
}
