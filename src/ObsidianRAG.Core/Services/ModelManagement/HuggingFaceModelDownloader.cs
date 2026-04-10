using System.Net.Http;
using System.Net.Http.Headers;
using System.Diagnostics;
using ObsidianRAG.Core.Models;

namespace ObsidianRAG.Core.Services;

/// <summary>
/// HuggingFace 模型下载器 - 支持直接下载 ONNX 或下载后自动转换
/// </summary>
internal class HuggingFaceModelDownloader : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _hfToken;
    private bool _disposed;

    public HuggingFaceModelDownloader(string? hfToken = null)
    {
        _httpClient = new HttpClient();
        _hfToken = hfToken ?? Environment.GetEnvironmentVariable("HF_TOKEN") ?? "";

        if (!string.IsNullOrEmpty(_hfToken))
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _hfToken);
        }
    }

    /// <summary>
    /// 下载模型文件
    /// </summary>
    public async Task DownloadAsync(
        string modelId,
        string targetDir,
        IProgress<float>? progress = null,
        CancellationToken cancellationToken = default,
        string? onnxFormat = null,
        string? onnxVariantPath = null)
    {
        Directory.CreateDirectory(targetDir);

        // 获取模型文件列表
        var files = await GetModelFilesAsync(modelId, cancellationToken);
        if (files == null || files.Count == 0)
        {
            throw new InvalidOperationException($"无法获取模型 {modelId} 的文件列表，请检查网络连接或模型名称是否正确");
        }

        // 检查是否有 ONNX 模型文件
        var onnxFiles = files.Where(f => f.Path.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase)).ToList();

        if (onnxFiles.Count > 0)
        {
            // 方案1: 直接下载已有的 ONNX 模型
            Console.WriteLine($"[Downloader] 检测到 ONNX 文件，直接下载模式");
            await DownloadOnnxModelAsync(modelId, targetDir, files, progress, cancellationToken, onnxVariantPath);
        }
        else
        {
            // 方案2: 下载 PyTorch 模型并转换
            Console.WriteLine($"[Downloader] 未检测到 ONNX 文件，将下载 PyTorch 模型并自动转换");
            await DownloadAndConvertAsync(modelId, targetDir, files, progress, cancellationToken, onnxFormat);
        }

        progress?.Report(100);
        Console.WriteLine($"[Downloader] 模型下载完成: {modelId}");
    }

    /// <summary>
    /// 获取模型可用的 ONNX 变体列表（向后兼容）
    /// </summary>
    [Obsolete("Use GetDownloadOptionsAsync instead")]
    public async Task<List<OnnxVariant>> GetOnnxVariantsAsync(
        string modelId,
        CancellationToken cancellationToken = default)
    {
        var options = await GetDownloadOptionsAsync(modelId, cancellationToken);
        return options.AllOptions.Select(o => new OnnxVariant
        {
            Path = o.Path,
            DisplayName = o.DisplayName,
            Description = o.Description,
            Size = o.Size,
            IsQuantized = o.IsQuantized,
            TargetPlatform = o.TargetPlatform,
            IsRecommended = o.IsRecommended
        }).ToList();
    }

    /// <summary>
    /// 获取模型的下载选项
    /// </summary>
    public async Task<ModelDownloadOptions> GetDownloadOptionsAsync(
        string modelId,
        CancellationToken cancellationToken = default)
    {
        var result = new ModelDownloadOptions { ModelName = modelId };

        try
        {
            var files = await GetModelFilesAsync(modelId, cancellationToken);

            // 查找所有 ONNX 文件
            var onnxFiles = files.Where(f =>
                f.Path.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase)).ToList();

            // 查找所有外部数据文件（.onnx.data 或 .onnx_data 两种命名约定）
            var dataFiles = files.Where(f =>
                f.Path.EndsWith(".onnx.data", StringComparison.OrdinalIgnoreCase) ||
                f.Path.EndsWith(".onnx_data", StringComparison.OrdinalIgnoreCase)).ToList();

            if (onnxFiles.Count == 0)
            {
                // 没有 ONNX 文件，需要转换
                result.HasOnnx = false;
                result.NeedsConversion = true;

                // 估算模型大小（PyTorch 文件）
                result.EstimatedSize = files
                    .Where(f => f.Path.EndsWith(".bin") || f.Path.EndsWith(".safetensors"))
                    .Sum(f => f.Size);
            }
            else
            {
                result.HasOnnx = true;
                result.NeedsConversion = false;

                // 分离根目录和子目录的文件
                var rootOnnxFiles = onnxFiles.Where(f => !f.Path.Contains("/")).ToList();
                var subfolderOnnxFiles = onnxFiles.Where(f => f.Path.Contains("/")).ToList();

                // 处理根目录文件
                foreach (var file in rootOnnxFiles)
                {
                    var option = CreateFileOption(file, dataFiles, false);
                    result.RootOptions.Add(option);
                }

                // 处理子目录文件
                foreach (var file in subfolderOnnxFiles)
                {
                    var option = CreateFileOption(file, dataFiles, true);
                    result.SubfolderOptions.Add(option);
                }

                // 合并所有选项
                result.AllOptions = result.RootOptions.Concat(result.SubfolderOptions).ToList();

                // 确定推荐选项
                DetermineRecommendedOption(result);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Downloader] 获取下载选项失败: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// 创建文件选项
    /// </summary>
    private OnnxFileOption CreateFileOption(HuggingFaceFile file, List<HuggingFaceFile> dataFiles, bool isInSubfolder)
    {
        var fileName = System.IO.Path.GetFileName(file.Path);
        var option = new OnnxFileOption
        {
            Path = file.Path,
            Size = file.Size,
            IsInSubfolder = isInSubfolder,
            IsQuantized = false,
            HasExternalData = false
        };

        // 检查是否有配套的外部数据文件（支持 .onnx.data 和 .onnx_data 两种命名约定）
        // HuggingFace 使用: model.onnx 的外部数据文件名为 model.onnx_data
        var dataPathDotData = file.Path + ".data";   // e.g. onnx/model.onnx -> onnx/model.onnx.data
        var baseWithoutOnnx = file.Path.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase)
            ? file.Path[..^5]                         // 去掉 ".onnx"
            : file.Path;
        var dataPathUnderscore = baseWithoutOnnx + ".onnx_data";  // e.g. onnx/model.onnx -> onnx/model.onnx_data
        var dataPaths = new[] { dataPathDotData, dataPathUnderscore };
        var dataFile = dataFiles.FirstOrDefault(d => dataPaths.Contains(d.Path, StringComparer.OrdinalIgnoreCase));
        if (dataFile != null)
        {
            option.HasExternalData = true;
            option.ExternalDataPath = dataFile.Path;
            option.Size += dataFile.Size;
        }

        // 解析文件名推断类型
        var lowerName = fileName.ToLowerInvariant();

        if (fileName == "model.onnx")
        {
            option.DisplayName = option.HasExternalData ? "标准版本 (外部格式)" : "标准版本 (嵌入式)";
            option.Description = option.HasExternalData
                ? "外部数据格式，支持大模型，下载后可转换为嵌入式"
                : "嵌入式格式，单个文件，兼容性最好";
            option.IsRecommended = !option.HasExternalData; // 嵌入式优先推荐
        }
        else if (lowerName.Contains("qint8") || lowerName.Contains("int8"))
        {
            option.IsQuantized = true;
            option.DisplayName = "INT8 量化";

            if (lowerName.Contains("arm64"))
            {
                option.TargetPlatform = "arm64";
                option.DisplayName += " (ARM64)";
                option.Description = "针对 ARM64 优化的 INT8 量化模型";
            }
            else if (lowerName.Contains("avx512"))
            {
                option.TargetPlatform = "avx512";
                option.DisplayName += " (AVX512)";
                option.Description = "针对 AVX512 优化的 INT8 量化模型";
            }
            else if (lowerName.Contains("avx2"))
            {
                option.TargetPlatform = "avx2";
                option.DisplayName += " (AVX2)";
                option.Description = "针对 AVX2 优化的 INT8 量化模型";
            }
            else
            {
                option.Description = "INT8 量化模型，体积更小，速度更快";
            }
        }
        else if (lowerName.Contains("quint8"))
        {
            option.IsQuantized = true;
            option.DisplayName = "UINT8 量化";

            if (lowerName.Contains("avx2"))
            {
                option.TargetPlatform = "avx2";
                option.DisplayName += " (AVX2)";
            }
            option.Description = "UINT8 量化模型";
        }
        else if (lowerName.Contains("_o"))
        {
            var match = System.Text.RegularExpressions.Regex.Match(fileName, @"_o(\d)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var level = int.Parse(match.Groups[1].Value);
                option.DisplayName = $"O{level} 优化";
                option.Description = $"优化级别 {level}，推理速度更快";
            }
        }
        else if (lowerName.Contains("fp16") || lowerName.Contains("float16"))
        {
            option.DisplayName = "FP16 版本";
            option.Description = "半精度模型，体积约为一半";
        }
        else
        {
            option.DisplayName = fileName.Replace(".onnx", "").Replace("_", " ");
            option.Description = $"变体: {fileName}";
        }

        // 添加来源信息
        if (isInSubfolder)
        {
            option.Description += " (来自 onnx/ 子目录)";
        }

        return option;
    }

    /// <summary>
    /// 确定推荐选项
    /// </summary>
    private static void DetermineRecommendedOption(ModelDownloadOptions result)
    {
        if (result.AllOptions.Count == 0) return;

        if (result.AllOptions.Count == 1)
        {
            result.AllOptions[0].IsRecommended = true;
            result.RecommendedOption = result.AllOptions[0];
            return;
        }

        // 优先推荐：根目录的嵌入式标准版本
        var embeddedStandard = result.RootOptions.FirstOrDefault(o =>
            System.IO.Path.GetFileName(o.Path) == "model.onnx" && !o.HasExternalData);

        if (embeddedStandard != null)
        {
            embeddedStandard.IsRecommended = true;
            result.RecommendedOption = embeddedStandard;
            return;
        }

        // 次选：子目录的嵌入式标准版本
        var subfolderEmbedded = result.SubfolderOptions.FirstOrDefault(o =>
            System.IO.Path.GetFileName(o.Path) == "model.onnx" && !o.HasExternalData);

        if (subfolderEmbedded != null)
        {
            subfolderEmbedded.IsRecommended = true;
            result.RecommendedOption = subfolderEmbedded;
            return;
        }

        // 再次：根目录的标准版本（即使是外部格式）
        var rootStandard = result.RootOptions.FirstOrDefault(o =>
            System.IO.Path.GetFileName(o.Path) == "model.onnx");

        if (rootStandard != null)
        {
            rootStandard.IsRecommended = true;
            result.RecommendedOption = rootStandard;
            return;
        }

        // 最后：子目录的标准版本
        var subfolderStandard = result.SubfolderOptions.FirstOrDefault(o =>
            System.IO.Path.GetFileName(o.Path) == "model.onnx");

        if (subfolderStandard != null)
        {
            subfolderStandard.IsRecommended = true;
            result.RecommendedOption = subfolderStandard;
        }
    }

    /// <summary>
    /// 解析 ONNX 文件变体信息
    /// </summary>
    private static OnnxVariant? ParseOnnxVariant(HuggingFaceFile file)
    {
        var fileName = System.IO.Path.GetFileName(file.Path);
        var displayName = "ONNX 模型";
        var description = "";
        var isQuantized = false;
        var targetPlatform = "";
        var relativeSize = file.Size; // 如果有精确大小则使用，否则为 0

        // 解析文件名推断变体类型
        var lowerName = fileName.ToLowerInvariant();

        if (fileName == "model.onnx")
        {
            displayName = "标准版本";
            description = "标准 ONNX 模型，兼容性最好";
        }
        else if (lowerName.Contains("qint8") || lowerName.Contains("int8") || lowerName.Contains("quantized"))
        {
            isQuantized = true;
            displayName = "INT8 量化";
            // 量化版本通常是标准版本的 ~1/4 到 ~1/2 大小
            relativeSize = relativeSize > 0 ? relativeSize : 1; // 标记为较小

            if (lowerName.Contains("arm64"))
            {
                targetPlatform = "arm64";
                displayName += " (ARM64)";
                description = "针对 ARM64 优化的 INT8 量化模型";
            }
            else if (lowerName.Contains("avx512"))
            {
                targetPlatform = "avx512";
                displayName += " (AVX512)";
                description = "针对 AVX512 优化的 INT8 量化模型";
            }
            else if (lowerName.Contains("avx2"))
            {
                targetPlatform = "avx2";
                displayName += " (AVX2)";
                description = "针对 AVX2 优化的 INT8 量化模型";
            }
            else
            {
                description = "INT8 量化模型，体积更小，速度更快";
            }
        }
        else if (lowerName.Contains("quint8"))
        {
            isQuantized = true;
            displayName = "UINT8 量化";
            relativeSize = relativeSize > 0 ? relativeSize : 1;

            if (lowerName.Contains("avx2"))
            {
                targetPlatform = "avx2";
                displayName += " (AVX2)";
                description = "针对 AVX2 优化的 UINT8 量化模型";
            }
            else
            {
                description = "UINT8 量化模型";
            }
        }
        else if (lowerName.Contains("_o"))
        {
            // 优化级别 O1-O4
            var match = System.Text.RegularExpressions.Regex.Match(fileName, @"_o(\d)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var level = int.Parse(match.Groups[1].Value);
                displayName = $"O{level} 优化";
                description = $"优化级别 {level}，推理速度更快";
                // O4 通常是最小的（包含更多优化）
                relativeSize = relativeSize > 0 ? relativeSize : 4 - level;
            }
        }
        else if (lowerName.Contains("fp16") || lowerName.Contains("float16"))
        {
            displayName = "FP16 版本";
            description = "半精度模型，体积约为一半";
            relativeSize = relativeSize > 0 ? relativeSize : 2;
        }
        else
        {
            // 其他情况，使用文件名
            displayName = fileName.Replace(".onnx", "").Replace("_", " ");
            description = $"变体: {fileName}";
        }

        return new OnnxVariant
        {
            Path = file.Path,
            DisplayName = displayName,
            Description = description,
            Size = relativeSize,
            IsQuantized = isQuantized,
            TargetPlatform = string.IsNullOrEmpty(targetPlatform) ? null : targetPlatform,
            IsRecommended = false
        };
    }

    /// <summary>
    /// 方案1: 直接下载已有 ONNX 模型
    /// </summary>
    private async Task DownloadOnnxModelAsync(
        string modelId,
        string targetDir,
        List<HuggingFaceFile> files,
        IProgress<float>? progress,
        CancellationToken cancellationToken,
        string? onnxVariantPath = null)
    {
        // 对于预转换的 ONNX 模型，只下载必要文件，跳过 PyTorch 模型文件

        // 如果指定了变体路径，直接使用指定的变体
        List<HuggingFaceFile> onnxFiles;
        bool needMoveFromSubfolder = false;

        if (!string.IsNullOrEmpty(onnxVariantPath))
        {
            Console.WriteLine($"[Downloader] 使用指定的 ONNX 变体: {onnxVariantPath}");

            // 查找指定变体的主文件和可能的 .data 文件
            var variantFile = files.FirstOrDefault(f => f.Path == onnxVariantPath);
            if (variantFile == null)
            {
                throw new InvalidOperationException($"指定的 ONNX 变体不存在: {onnxVariantPath}");
            }

            // 查找配套的 .data 文件（如果有，支持 .onnx.data 和 .onnx_data 两种命名）
            var dataPathDotData = onnxVariantPath + ".data";
            var baseWithoutOnnx = onnxVariantPath.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase)
                ? onnxVariantPath[..^5]
                : onnxVariantPath;
            var dataPathUnderscore = baseWithoutOnnx + ".onnx_data";
            var dataFile = files.FirstOrDefault(f => f.Path == dataPathDotData || f.Path == dataPathUnderscore);

            onnxFiles = new List<HuggingFaceFile> { variantFile };
            if (dataFile != null)
            {
                onnxFiles.Add(dataFile);
            }

            // 如果变体在子目录中，需要移动到根目录
            if (onnxVariantPath.Contains("/"))
            {
                needMoveFromSubfolder = true;
            }
        }
        else
        {
            // 未指定变体，自动选择最佳 ONNX 文件
            // ONNX 文件处理：优先根目录的 model.onnx，避免重复下载 onnx/ 子目录
            var rootOnnxFiles = files.Where(f =>
                (f.Path == "model.onnx" || f.Path == "model.onnx.data" || f.Path == "model.onnx_data") ||
                (!f.Path.Contains("/") && f.Path.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase)) ||
                (!f.Path.Contains("/") && f.Path.EndsWith(".onnx.data", StringComparison.OrdinalIgnoreCase)) ||
                (!f.Path.Contains("/") && f.Path.EndsWith(".onnx_data", StringComparison.OrdinalIgnoreCase))).ToList();

            var subfolderOnnxFiles = files.Where(f =>
                f.Path.StartsWith("onnx/", StringComparison.OrdinalIgnoreCase) &&
                (f.Path.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase) ||
                 f.Path.EndsWith(".onnx.data", StringComparison.OrdinalIgnoreCase) ||
                 f.Path.EndsWith(".onnx_data", StringComparison.OrdinalIgnoreCase))).ToList();

            // 如果根目录有 ONNX 文件，使用根目录的；否则使用子目录的
            if (rootOnnxFiles.Any(f => f.Path == "model.onnx"))
            {
                onnxFiles = rootOnnxFiles;
                Console.WriteLine("[Downloader] 使用根目录的 ONNX 文件");
            }
            else if (subfolderOnnxFiles.Any(f => f.Path == "onnx/model.onnx"))
            {
                // 使用子目录的 ONNX 文件，下载后移动到根目录
                onnxFiles = subfolderOnnxFiles;
                needMoveFromSubfolder = true;
                Console.WriteLine("[Downloader] 使用 onnx/ 子目录的 ONNX 文件（将移动到根目录）");
            }
            else
            {
                onnxFiles = rootOnnxFiles.Any() ? rootOnnxFiles : subfolderOnnxFiles;

                // 如果有多个变体且未指定，选择标准的 model.onnx 或最小的非量化版本
                if (onnxFiles.Count > 2)
                {
                    var standardOnnx = onnxFiles.FirstOrDefault(f =>
                        System.IO.Path.GetFileName(f.Path) == "model.onnx");
                    if (standardOnnx != null)
                    {
                        var dataFile = onnxFiles.FirstOrDefault(f =>
                            f.Path == "model.onnx.data");
                        onnxFiles = dataFile != null
                            ? new List<HuggingFaceFile> { standardOnnx, dataFile }
                            : new List<HuggingFaceFile> { standardOnnx };
                        Console.WriteLine("[Downloader] 自动选择标准 ONNX 版本");
                    }
                }
            }
        }

        var tokenizerFiles = files.Where(f =>
            f.Path.EndsWith("tokenizer.json", StringComparison.OrdinalIgnoreCase) ||
            f.Path.EndsWith("tokenizer_config.json", StringComparison.OrdinalIgnoreCase) ||
            f.Path.EndsWith("vocab.txt", StringComparison.OrdinalIgnoreCase) ||
            f.Path.EndsWith("special_tokens_map.json", StringComparison.OrdinalIgnoreCase) ||
            f.Path.EndsWith("tokenizer.model", StringComparison.OrdinalIgnoreCase) ||
            f.Path.EndsWith("sentencepiece.bpe.model", StringComparison.OrdinalIgnoreCase) ||
            f.Path.EndsWith("bpe.vocab", StringComparison.OrdinalIgnoreCase)).ToList();

        var configFiles = files.Where(f =>
            f.Path.EndsWith("config.json", StringComparison.OrdinalIgnoreCase) ||
            f.Path.EndsWith("sentence_bert_config.json", StringComparison.OrdinalIgnoreCase) ||
            f.Path.EndsWith("config_sentence_transformers.json", StringComparison.OrdinalIgnoreCase) ||
            f.Path.EndsWith("modules.json", StringComparison.OrdinalIgnoreCase) ||
            f.Path == "1_Pooling/config.json").ToList();

        // 合并需要下载的文件
        var filesToDownload = onnxFiles.Concat(tokenizerFiles).Concat(configFiles).Distinct().ToList();

        // 过滤掉不需要的文件
        var skippedFiles = files.Where(f => !filesToDownload.Contains(f)).ToList();
        if (skippedFiles.Count > 0)
        {
            var skippedSize = skippedFiles.Sum(f => f.Size) / 1024.0 / 1024.0;
            Console.WriteLine($"[Downloader] 跳过不需要的文件 ({skippedFiles.Count} 个, 共 {skippedSize:F2} MB):");
            foreach (var f in skippedFiles.Take(5))
            {
                Console.WriteLine($"  - {f.Path}");
            }
            if (skippedFiles.Count > 5)
            {
                Console.WriteLine($"  ... 等 {skippedFiles.Count - 5} 个文件");
            }
        }

        var totalSize = filesToDownload.Sum(f => f.Size);
        var downloadedSize = 0L;

        foreach (var file in filesToDownload)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var filePath = Path.Combine(targetDir, file.Path);
            var fileDir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(fileDir))
            {
                Directory.CreateDirectory(fileDir);
            }

            var url = $"https://huggingface.co/{modelId}/resolve/main/{file.Path}";
            Console.WriteLine($"[Downloader] 下载: {file.Path} ({file.Size / 1024.0 / 1024.0:F2} MB)");

            await DownloadFileAsync(url, filePath, downloadedSize, totalSize, progress, cancellationToken);
            downloadedSize += file.Size;
        }

        // 如果 ONNX 文件在子目录或使用非标准文件名，需要移动/重命名
        if (needMoveFromSubfolder)
        {
            // 查找下载的 ONNX 主文件（非 .data 文件）
            var mainOnnxFile = onnxFiles.FirstOrDefault(f =>
                !f.Path.EndsWith(".data", StringComparison.OrdinalIgnoreCase));

            if (mainOnnxFile != null)
            {
                var sourceOnnxPath = Path.Combine(targetDir, mainOnnxFile.Path);
                var rootOnnxPath = Path.Combine(targetDir, "model.onnx");

                if (File.Exists(sourceOnnxPath) && sourceOnnxPath != rootOnnxPath)
                {
                    Console.WriteLine($"[Downloader] 移动 ONNX 文件到根目录: {mainOnnxFile.Path} -> model.onnx");
                    if (File.Exists(rootOnnxPath))
                    {
                        File.Delete(rootOnnxPath);
                    }

                    // 确保目标目录存在
                    var rootDir = Path.GetDirectoryName(rootOnnxPath);
                    if (!string.IsNullOrEmpty(rootDir) && !Directory.Exists(rootDir))
                    {
                        Directory.CreateDirectory(rootDir);
                    }

                    File.Move(sourceOnnxPath, rootOnnxPath);
                }

                // 移动配套的 .data 文件（如果存在，支持 .onnx.data 和 .onnx_data 两种命名）
                var mainBase = mainOnnxFile.Path.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase)
                    ? mainOnnxFile.Path[..^5]
                    : mainOnnxFile.Path;
                var dataFile = onnxFiles.FirstOrDefault(f =>
                    f.Path == mainOnnxFile.Path + ".data" ||
                    f.Path == mainBase + ".onnx_data");

                if (dataFile != null)
                {
                    var sourceDataPath = Path.Combine(targetDir, dataFile.Path);
                    var rootDataPath = Path.Combine(targetDir, "model.onnx.data");

                    if (File.Exists(sourceDataPath) && sourceDataPath != rootDataPath)
                    {
                        Console.WriteLine($"[Downloader] 移动外部数据文件: {dataFile.Path} -> model.onnx.data");
                        if (File.Exists(rootDataPath))
                        {
                            File.Delete(rootDataPath);
                        }
                        File.Move(sourceDataPath, rootDataPath);
                    }
                }
            }

            // 删除空的子目录
            try
            {
                var onnxDir = Path.Combine(targetDir, "onnx");
                if (Directory.Exists(onnxDir) && !Directory.EnumerateFileSystemEntries(onnxDir).Any())
                {
                    Directory.Delete(onnxDir);
                    Console.WriteLine("[Downloader] 已删除空的 onnx 子目录");
                }
            }
            catch { }
        }

        Console.WriteLine($"[Downloader] 预转换 ONNX 模型下载完成");
    }

    /// <summary>
    /// 方案2: 下载 PyTorch 模型并转换为 ONNX
    /// </summary>
    private async Task DownloadAndConvertAsync(
        string modelId,
        string targetDir,
        List<HuggingFaceFile> files,
        IProgress<float>? progress,
        CancellationToken cancellationToken,
        string? onnxFormat = null)
    {
        // 只下载必要的文件
        var requiredFiles = new[] { "pytorch_model.bin", "model.safetensors", "config.json", 
            "tokenizer.json", "tokenizer_config.json", "vocab.txt", "special_tokens_map.json",
            "tokenizer.model", "sentencepiece.bpe.model", "bpe.vocab" };

        var filesToDownload = files.Where(f => 
            requiredFiles.Contains(Path.GetFileName(f.Path), StringComparer.OrdinalIgnoreCase) ||
            f.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
            f.Path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ||
            f.Path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) ||
            f.Path.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase) ||
            f.Path.EndsWith(".model", StringComparison.OrdinalIgnoreCase)
        ).ToList();

        // 计算总大小
        var totalSize = filesToDownload.Sum(f => f.Size);
        var downloadedSize = 0L;

        // 下载必要文件
        foreach (var file in filesToDownload)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var filePath = Path.Combine(targetDir, file.Path);
            var fileDir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(fileDir))
            {
                Directory.CreateDirectory(fileDir);
            }

            var url = $"https://huggingface.co/{modelId}/resolve/main/{file.Path}";
            Console.WriteLine($"[Downloader] 下载: {file.Path} ({file.Size / 1024.0 / 1024.0:F2} MB)");

            await DownloadFileAsync(url, filePath, downloadedSize, totalSize, progress, cancellationToken);
            downloadedSize += file.Size;
        }

        // 检查是否已有 ONNX 文件（可能是之前转换的）
        var onnxPath = Path.Combine(targetDir, "model.onnx");
        if (File.Exists(onnxPath))
        {
            Console.WriteLine("[Downloader] ONNX 文件已存在，跳过转换");
            return;
        }

        // 计算模型大小，判断是否需要使用 external 格式
        // ONNX 嵌入式格式有 ~2GB 限制，经验估算 ONNX 文件约为 PyTorch 的 1.5-2 倍
        var modelSize = filesToDownload
            .Where(f => f.Path.EndsWith(".bin") || f.Path.EndsWith(".safetensors"))
            .Sum(f => f.Size);
        
        // 阈值: 1.5GB (预留缓冲空间)
        const long EMBEDDED_SIZE_LIMIT = 1610612736; // 1.5 * 1024 * 1024 * 1024
        
        // 自动判断格式：如果指定了格式用指定格式；否则根据大小自动判断
        var actualFormat = onnxFormat;
        if (string.IsNullOrEmpty(actualFormat))
        {
            if (modelSize > EMBEDDED_SIZE_LIMIT)
            {
                actualFormat = "external";
                Console.WriteLine($"[Downloader] 检测到模型大小 {modelSize / 1024.0 / 1024.0 / 1024.0:F2} GB > 1.5 GB，自动使用 external 格式");
            }
            else
            {
                actualFormat = "embedded";
                Console.WriteLine($"[Downloader] 模型大小 {modelSize / 1024.0 / 1024.0:F2} MB，使用 embedded 格式");
            }
        }

        // 执行 ONNX 转换
        Console.WriteLine($"[Downloader] 开始 ONNX 转换 (格式: {actualFormat})...");
        progress?.Report(90);

        var success = await ConvertToOnnxAsync(targetDir, onnxPath, actualFormat, null, cancellationToken);
        
        // 如果 embedded 格式转换失败，尝试 external 格式
        if (!success && actualFormat == "embedded")
        {
            Console.WriteLine("[Downloader] Embedded 格式转换失败，尝试 external 格式...");
            
            // 清理之前可能的残留文件
            var failedOnnxPath = Path.Combine(targetDir, "model.onnx");
            if (File.Exists(failedOnnxPath))
            {
                try { File.Delete(failedOnnxPath); } catch { }
            }
            var failedDataPath = Path.Combine(targetDir, "model.onnx.data");
            if (File.Exists(failedDataPath))
            {
                try { File.Delete(failedDataPath); } catch { }
            }
            
            success = await ConvertToOnnxAsync(targetDir, onnxPath, "external", null, cancellationToken);
            if (success)
            {
                actualFormat = "external";
                Console.WriteLine("[Downloader] External 格式转换成功");
            }
        }
        
        if (!success)
        {
            Console.WriteLine("[Downloader] ONNX 转换失败，尝试下载预转换版本...");
            
            // 尝试从 optimum 导出的模型仓库下载
            var optimumModelId = $"optimum/{modelId.Split('/').Last()}";
            var optimumOnnxDownloaded = await TryDownloadOptimumOnnxAsync(optimumModelId, targetDir, cancellationToken);
            
            if (!optimumOnnxDownloaded)
            {
                throw new InvalidOperationException(
                    $"模型转换失败。\n\n" +
                    $"可能原因：\n" +
                    $"1. 模型体积较大，嵌入式格式受限\n" +
                    $"2. Python 环境缺少必要的库\n\n" +
                    $"建议解决方案：\n" +
                    $"1. 使用 external 格式重试（支持大模型）\n" +
                    $"2. 确保已安装 Python 和依赖：pip install torch transformers optimum onnx\n\n" +
                    $"手动转换命令：\n" +
                    $"optimum-cli export onnx --model {modelId} --dtype float16 {targetDir}");
            }
            else
            {
                // 预转换模型下载成功，清理原始 PyTorch 文件
                CleanupPyTorchFiles(targetDir);
            }
        }
        else
        {
            Console.WriteLine($"[Downloader] ONNX 转换完成 (格式: {actualFormat})");

            // 清理原始 PyTorch 模型文件，节省磁盘空间
            CleanupPyTorchFiles(targetDir);
        }
    }

    /// <summary>
    /// 清理 PyTorch 模型文件（ONNX 转换成功后调用）
    /// </summary>
    private static void CleanupPyTorchFiles(string targetDir)
    {
        // 需要删除的文件列表
        var filesToDelete = new[]
        {
            "pytorch_model.bin",
            "model.safetensors"
        };

        long savedSpace = 0;
        foreach (var fileName in filesToDelete)
        {
            var filePath = Path.Combine(targetDir, fileName);
            if (File.Exists(filePath))
            {
                try
                {
                    var fileSize = new FileInfo(filePath).Length;
                    File.Delete(filePath);
                    savedSpace += fileSize;
                    Console.WriteLine($"[Downloader] 已清理: {fileName} ({fileSize / 1024.0 / 1024.0:F2} MB)");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Downloader] 清理 {fileName} 失败: {ex.Message}");
                }
            }
        }

        if (savedSpace > 0)
        {
            Console.WriteLine($"[Downloader] 共释放空间: {savedSpace / 1024.0 / 1024.0:F2} MB");
        }
    }

    /// <summary>
    /// 尝试从 optimum 仓库下载预转换的 ONNX 模型
    /// </summary>
    private async Task<bool> TryDownloadOptimumOnnxAsync(string optimumModelId, string targetDir, CancellationToken cancellationToken)
    {
        try
        {
            Console.WriteLine($"[Downloader] 尝试从 {optimumModelId} 下载预转换的 ONNX 模型...");
            
            var files = await GetModelFilesAsync(optimumModelId, cancellationToken);
            if (files.Count == 0) return false;

            var onnxFile = files.FirstOrDefault(f => f.Path == "model.onnx");
            if (onnxFile == null) return false;

            var url = $"https://huggingface.co/{optimumModelId}/resolve/main/model.onnx";
            var targetPath = Path.Combine(targetDir, "model.onnx");

            await DownloadFileAsync(url, targetPath, 0, onnxFile.Size, null, cancellationToken);
            
            Console.WriteLine("[Downloader] 预转换 ONNX 模型下载成功");
            return true;
        }
        catch
        {
            Console.WriteLine($"[Downloader] 预转换模型下载失败: {optimumModelId}");
            return false;
        }
    }

    /// <summary>
    /// 使用 Python 转换模型为 ONNX
    /// </summary>
    public async Task<bool> ConvertToOnnxAsync(
        string modelDir, 
        string outputPath, 
        string format = "embedded",
        IProgress<float>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // 检查 Python 是否可用
        if (!IsPythonAvailable())
        {
            Console.WriteLine("[Downloader] Python 不可用，跳过自动转换");
            return false;
        }

        // 生成转换脚本
        var scriptContent = GetConversionScript(format);
        var scriptPath = Path.Combine(Path.GetTempPath(), $"convert_onnx_{Guid.NewGuid():N}.py");

        try
        {
            await File.WriteAllTextAsync(scriptPath, scriptContent, cancellationToken);
            progress?.Report(10);

            // Validate format parameter against whitelist to prevent command injection
            var allowedFormats = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "embedded", "external" };
            if (!allowedFormats.Contains(format))
            {
                Console.WriteLine($"[Downloader] Invalid format '{format}', must be 'embedded' or 'external'");
                return false;
            }

            // Sanitize path parameters: reject paths containing characters that could escape quotes
            if (modelDir.Contains('"') || outputPath.Contains('"') || modelDir.Contains('\0') || outputPath.Contains('\0'))
            {
                Console.WriteLine("[Downloader] Invalid path characters detected");
                return false;
            }

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
            progress?.Report(20);

            using var process = new Process { StartInfo = startInfo };
            
            var outputBuilder = new System.Text.StringBuilder();
            var errorBuilder = new System.Text.StringBuilder();

            process.OutputDataReceived += (s, e) => 
            { 
                if (e.Data != null) 
                {
                    Console.WriteLine($"[Python] {e.Data}");
                    outputBuilder.AppendLine(e.Data);
                }
            };
            process.ErrorDataReceived += (s, e) => 
            { 
                if (e.Data != null) 
                {
                    Console.WriteLine($"[Python Error] {e.Data}");
                    errorBuilder.AppendLine(e.Data);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode == 0 && File.Exists(outputPath))
            {
                Console.WriteLine("[Downloader] ONNX 转换成功");
                return true;
            }
            else
            {
                Console.WriteLine($"[Downloader] ONNX 转换失败 (ExitCode: {process.ExitCode})");
                return false;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Downloader] ONNX 转换异常: {ex.Message}");
            return false;
        }
        finally
        {
            if (File.Exists(scriptPath))
            {
                try { File.Delete(scriptPath); } catch { }
            }
        }
    }

    /// <summary>
    /// 检查 Python 是否可用
    /// </summary>
    private static bool IsPythonAvailable()
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "python",
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            process?.WaitForExit(5000);
            return process?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 获取转换脚本内容
    /// </summary>
    private static string GetConversionScript(string format = "embedded")
    {
        // 使用普通原始字符串，避免插值字符串中花括号转义问题
        return """
import argparse
import sys
import os
import shutil

# Fix Windows encoding issues
if sys.platform == 'win32':
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
    sys.stderr.reconfigure(encoding='utf-8', errors='replace')

def convert_onnx_format(model_dir, output_path, format):
    # Convert between ONNX embedded and external formats using onnx library
    import onnx
    from onnx import numpy_helper

    src_onnx = os.path.join(model_dir, "model.onnx")
    print(f"Loading existing ONNX model from: {src_onnx}")
    onnx_model = onnx.load(src_onnx, load_external_data=True)

    if format == 'external':
        print("Converting to external format (model.onnx + model.onnx.data)...")
        onnx.save(
            onnx_model,
            output_path,
            save_as_external_data=True,
            all_tensors_to_one_file=True,
            location="model.onnx.data",
            size_threshold=0,
        )
    else:  # embedded
        print("Converting to embedded format (single file)...")
        # 关键：必须显式清除所有 tensor 的 ExternalDataDescriptor，
        # 否则 onnx.save 可能保留外部数据引用而非内联数据
        for init in onnx_model.graph.initializer:
            if init.HasField('data_location') or len(init.external_data) > 0:
                # 将外部数据内联为 raw_data
                # numpy_helper.to_array 返回的 ndarray 已保持正确的 dtype
                np_array = numpy_helper.to_array(init)
                init.ClearField('external_data')
                init.ClearField('data_location')
                init.raw_data = np_array.tobytes()
        onnx.save(onnx_model, output_path)
        # 清理可能残留的 .data 文件
        data_file = output_path + ".data"
        if os.path.exists(data_file):
            print(f"Removing residual external data file after embedded conversion: {data_file}")
            os.remove(data_file)

    return True

def export_with_optimum(model_dir, output_path, format):
    # Use optimum library for ONNX export (most reliable for embedded format)
    try:
        from optimum.onnxruntime import ORTModelForFeatureExtraction
        from transformers import AutoTokenizer
    except ImportError:
        return False

    model = ORTModelForFeatureExtraction.from_pretrained(model_dir, export=True)
    tokenizer = AutoTokenizer.from_pretrained(model_dir)

    # optimum exports to a directory; we save then move
    temp_dir = output_path + "_optimum_temp"
    os.makedirs(temp_dir, exist_ok=True)
    try:
        model.save_pretrained(temp_dir)
        tokenizer.save_pretrained(temp_dir)

        # Move the exported ONNX file to output_path
        src_onnx = os.path.join(temp_dir, "model.onnx")
        src_data = os.path.join(temp_dir, "model.onnx.data")

        # For embedded: remove any pre-existing .data file in target dir first
        target_data = output_path + ".data"
        if format == 'embedded':
            if os.path.exists(target_data):
                os.remove(target_data)
                print("Removed pre-existing external data file for embedded format")
            shutil.move(src_onnx, output_path)
            # If optimum also produced a .data file in temp, merge it
            if os.path.exists(src_data):
                import onnx
                from onnx import numpy_helper
                merged = onnx.load(output_path, load_external_data=True)
                # 清除外部数据引用，强制内联
                for init in merged.graph.initializer:
                    if init.HasField('data_location') or len(init.external_data) > 0:
                        np_array = numpy_helper.to_array(init)
                        init.ClearField('external_data')
                        init.ClearField('data_location')
                        init.raw_data = np_array.tobytes()
                onnx.save(merged, output_path)
                os.remove(src_data)
                print("Merged external data into embedded ONNX file")
        else:
            shutil.move(src_onnx, output_path)
            if os.path.exists(src_data):
                shutil.move(src_data, target_data)

        return True
    finally:
        if os.path.exists(temp_dir):
            shutil.rmtree(temp_dir, ignore_errors=True)

def export_with_torch(model_dir, output_path, format):
    # Fallback: torch.onnx.export with post-processing for embedded format
    import torch
    from transformers import AutoModel, AutoTokenizer

    print(f"Loading model from: {model_dir}")
    model = AutoModel.from_pretrained(model_dir)
    tokenizer = AutoTokenizer.from_pretrained(model_dir)
    model.eval()

    dummy_input = tokenizer("test input", return_tensors="pt", padding=True, truncation=True)

    print(f"Exporting ONNX to: {output_path}")
    print(f"Format: {format}")

    # Always export with external data first (most reliable)
    torch.onnx.export(
        model,
        (dummy_input["input_ids"], dummy_input["attention_mask"]),
        output_path,
        input_names=["input_ids", "attention_mask"],
        output_names=["last_hidden_state"],
        dynamic_axes={
            "input_ids": {0: "batch_size", 1: "sequence_length"},
            "attention_mask": {0: "batch_size", 1: "sequence_length"},
            "last_hidden_state": {0: "batch_size", 1: "sequence_length"}
        },
        opset_version=14,
        do_constant_folding=True,
        export_params=True,
    )

    # Post-process: if target is embedded but external data was produced, merge
    data_file = output_path + ".data"
    if format == 'embedded' and os.path.exists(data_file):
        print("Merging external data into single embedded file...")
        import onnx
        from onnx import numpy_helper
        merged = onnx.load(output_path, load_external_data=True)
        # 清除外部数据引用，强制内联
        for init in merged.graph.initializer:
            if init.HasField('data_location') or len(init.external_data) > 0:
                np_array = numpy_helper.to_array(init)
                init.ClearField('external_data')
                init.ClearField('data_location')
                init.raw_data = np_array.tobytes()
        onnx.save(merged, output_path)
        os.remove(data_file)
        # 检查是否还有残留的 .data 文件
        if os.path.exists(data_file):
            print(f"Warning: .data file still exists after embedded save, removing: {data_file}")
            os.remove(data_file)
        print("Merged external data into embedded format successfully")
    elif format == 'external' and not os.path.exists(data_file):
        # Already embedded, force external by saving with external_data_to
        print("Converting to external format...")
        import onnx
        tmp_output = output_path + ".tmp"
        os.rename(output_path, tmp_output)
        onnx_model = onnx.load(tmp_output)
        onnx.save(
            onnx_model,
            output_path,
            save_as_external_data=True,
            all_tensors_to_one_file=True,
            location="model.onnx.data",
            size_threshold=0,
        )
        os.remove(tmp_output)

    return True

def main():
    parser = argparse.ArgumentParser(description='Convert PyTorch model to ONNX')
    parser.add_argument('--model_dir', required=True, help='Path to the model directory')
    parser.add_argument('--output', required=True, help='Output ONNX file path')
    parser.add_argument('--format', default='embedded', choices=['embedded', 'external'], help='ONNX format')
    args = parser.parse_args()

    # Check if model is already ONNX (no PyTorch weights) - use direct format conversion
    has_pytorch = os.path.exists(os.path.join(args.model_dir, "pytorch_model.bin")) or \
                  os.path.exists(os.path.join(args.model_dir, "model.safetensors"))
    has_onnx = os.path.exists(os.path.join(args.model_dir, "model.onnx"))

    if not has_pytorch and has_onnx:
        # Already ONNX, just convert between embedded/external formats
        print("Model is already ONNX (no PyTorch weights), converting format directly...")
        convert_onnx_format(args.model_dir, args.output, args.format)
    else:
        # Strategy 1: Try optimum (most reliable for embedded format)
        print(f"Attempting ONNX export with optimum...")
        if export_with_optimum(args.model_dir, args.output, args.format):
            # Verify export
            try:
                import onnx
                onnx_model = onnx.load(args.output, load_external_data=False)
                onnx.checker.check_model(onnx_model)
            except Exception as e:
                print(f"Warning: ONNX verification failed: {e}")
        else:
            print("optimum not available, falling back to torch.onnx.export...")
            export_with_torch(args.model_dir, args.output, args.format)

    # Report results
    file_size = os.path.getsize(args.output) / (1024 * 1024)
    print(f"ONNX export completed successfully! Size: {file_size:.2f} MB")

    data_file = args.output + ".data"
    if os.path.exists(data_file):
        data_size = os.path.getsize(data_file) / (1024 * 1024)
        print(f"External data file: {data_size:.2f} MB")
        print("Note: Using external data format (model.onnx + model.onnx.data)")
    else:
        print("Note: Using embedded format (single file)")

if __name__ == "__main__":
    main()
""";
    }

    /// <summary>
    /// 获取模型的文件列表
    /// </summary>
    public async Task<List<HuggingFaceFile>> GetModelFilesAsync(
        string modelId,
        CancellationToken cancellationToken = default)
    {
        var files = new List<HuggingFaceFile>();

        try
        {
            var apiUrl = $"https://huggingface.co/api/models/{modelId}";
            var response = await _httpClient.GetAsync(apiUrl, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var modelInfo = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json);
                
                if (modelInfo.TryGetProperty("siblings", out var siblings))
                {
                    foreach (var sibling in siblings.EnumerateArray())
                    {
                        if (sibling.TryGetProperty("rfilename", out var rfilename))
                        {
                            var size = 0L;
                            if (sibling.TryGetProperty("size", out var sizeElement))
                            {
                                size = sizeElement.GetInt64();
                            }

                            files.Add(new HuggingFaceFile
                            {
                                Path = rfilename.GetString() ?? "",
                                Size = size
                            });
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Downloader] API request failed: {ex.Message}");
        }

        return files;
    }

    private async Task DownloadFileAsync(
        string url,
        string targetPath,
        long downloadedSize,
        long totalSize,
        IProgress<float>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

        var buffer = new byte[8192];
        long totalRead = 0;
        int bytesRead;

        while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            totalRead += bytesRead;

            if (totalSize > 0)
            {
                var overallProgress = (float)(downloadedSize + totalRead) / totalSize * 100;
                progress?.Report(Math.Min(overallProgress, 99)); // 保留1%给最终确认
            }
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _httpClient.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// HuggingFace 文件信息
/// </summary>
public class HuggingFaceFile
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
}
