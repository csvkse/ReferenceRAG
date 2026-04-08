using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ObsidianRAG.Core.Interfaces;
using ObsidianRAG.Core.Models;
using ObsidianRAG.Core.Services;
using ObsidianRAG.Storage;

namespace ObsidianRAG.CLI;

/// <summary>
/// Obsidian RAG CLI 工具 - 支持多源文件夹
/// </summary>
class Program
{
    private static ObsidianRagConfig? _config;
    private static ConfigManager? _configManager;

    static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("Obsidian RAG Knowledge Base CLI - 支持多源文件夹");

        // 全局选项
        var configOption = new Option<string?>("--config", "配置文件路径");
        rootCommand.AddGlobalOption(configOption);

        // ==================== 源管理命令 ====================
        var sourceCommand = new Command("source", "源文件夹管理");
        
        // source list
        var sourceListCommand = new Command("list", "列出所有源");
        sourceListCommand.SetHandler(ctx => 
        {
            var configPath = ctx.ParseResult.GetValueForOption(configOption);
            HandleSourceList(configPath);
        });
        
        // source add
        var sourceAddCommand = new Command("add", "添加源文件夹");
        var addPathArg = new Argument<string>("path", "文件夹路径");
        var addNameOption = new Option<string?>("--name", "源名称");
        addNameOption.AddAlias("-n");
        var addTypeOption = new Option<string>("--type", () => "markdown", "源类型: obsidian, markdown, documents, code, custom");
        addTypeOption.AddAlias("-t");
        var addPatternOption = new Option<string[]>("--pattern", () => new[] { "*.md" }, "文件匹配模式");
        addPatternOption.AddAlias("-p");
        var addRecursiveOption = new Option<bool>("--recursive", () => true, "是否递归");
        addRecursiveOption.AddAlias("-r");
        
        sourceAddCommand.AddArgument(addPathArg);
        sourceAddCommand.AddOption(addNameOption);
        sourceAddCommand.AddOption(addTypeOption);
        sourceAddCommand.AddOption(addPatternOption);
        sourceAddCommand.AddOption(addRecursiveOption);
        
        sourceAddCommand.SetHandler(ctx =>
        {
            var configPath = ctx.ParseResult.GetValueForOption(configOption);
            var path = ctx.ParseResult.GetValueForArgument(addPathArg);
            var name = ctx.ParseResult.GetValueForOption(addNameOption);
            var type = ctx.ParseResult.GetValueForOption(addTypeOption);
            var patterns = ctx.ParseResult.GetValueForOption(addPatternOption);
            var recursive = ctx.ParseResult.GetValueForOption(addRecursiveOption);
            HandleSourceAdd(configPath, path, name, type, patterns, recursive);
        });
        
        // source remove
        var sourceRemoveCommand = new Command("remove", "移除源文件夹");
        var removeArg = new Argument<string>("name", "源名称或路径");
        sourceRemoveCommand.AddArgument(removeArg);
        sourceRemoveCommand.SetHandler(ctx =>
        {
            var configPath = ctx.ParseResult.GetValueForOption(configOption);
            var name = ctx.ParseResult.GetValueForArgument(removeArg);
            HandleSourceRemove(configPath, name);
        });
        
        // source enable
        var sourceEnableCommand = new Command("enable", "启用源");
        var enableArg = new Argument<string>("name", "源名称或路径");
        sourceEnableCommand.AddArgument(enableArg);
        sourceEnableCommand.SetHandler(ctx =>
        {
            var configPath = ctx.ParseResult.GetValueForOption(configOption);
            var name = ctx.ParseResult.GetValueForArgument(enableArg);
            HandleSourceToggle(configPath, name, true);
        });
        
        // source disable
        var sourceDisableCommand = new Command("disable", "禁用源");
        var disableArg = new Argument<string>("name", "源名称或路径");
        sourceDisableCommand.AddArgument(disableArg);
        sourceDisableCommand.SetHandler(ctx =>
        {
            var configPath = ctx.ParseResult.GetValueForOption(configOption);
            var name = ctx.ParseResult.GetValueForArgument(disableArg);
            HandleSourceToggle(configPath, name, false);
        });
        
        sourceCommand.AddCommand(sourceListCommand);
        sourceCommand.AddCommand(sourceAddCommand);
        sourceCommand.AddCommand(sourceRemoveCommand);
        sourceCommand.AddCommand(sourceEnableCommand);
        sourceCommand.AddCommand(sourceDisableCommand);
        rootCommand.AddCommand(sourceCommand);

        // ==================== 配置命令 ====================
        var configCommand = new Command("config", "配置管理");
        
        var configInitCommand = new Command("init", "初始化配置文件");
        configInitCommand.SetHandler(ctx =>
        {
            var configPath = ctx.ParseResult.GetValueForOption(configOption);
            HandleConfigInit(configPath);
        });
        
        var configShowCommand = new Command("show", "显示当前配置");
        configShowCommand.SetHandler(ctx =>
        {
            var configPath = ctx.ParseResult.GetValueForOption(configOption);
            HandleConfigShow(configPath);
        });
        
        configCommand.AddCommand(configInitCommand);
        configCommand.AddCommand(configShowCommand);
        rootCommand.AddCommand(configCommand);

        // ==================== 索引命令 ====================
        var indexCommand = new Command("index", "索引文件");
        var indexPathOption = new Option<string[]>("--path", "要索引的源名称或路径（可多个）");
        indexPathOption.AddAlias("-p");
        var indexForceOption = new Option<bool>("--force", () => false, "强制重新索引");
        indexForceOption.AddAlias("-f");
        var indexVerboseOption = new Option<bool>("--verbose", () => false, "详细输出");
        indexVerboseOption.AddAlias("-v");
        
        indexCommand.AddOption(indexPathOption);
        indexCommand.AddOption(indexForceOption);
        indexCommand.AddOption(indexVerboseOption);
        
        indexCommand.SetHandler(async ctx =>
        {
            var configPath = ctx.ParseResult.GetValueForOption(configOption);
            var paths = ctx.ParseResult.GetValueForOption(indexPathOption);
            var force = ctx.ParseResult.GetValueForOption(indexForceOption);
            var verbose = ctx.ParseResult.GetValueForOption(indexVerboseOption);
            await HandleIndexCommand(configPath, paths, force, verbose);
        });
        rootCommand.AddCommand(indexCommand);

        // ==================== 查询命令 ====================
        var queryCommand = new Command("query", "查询知识库");
        var queryArg = new Argument<string>("query", "查询文本");
        var queryModeOption = new Option<string>("--mode", () => "standard", "查询模式: quick, standard, deep");
        queryModeOption.AddAlias("-m");
        var queryTopKOption = new Option<int>("--top-k", () => 10, "返回结果数量");
        queryTopKOption.AddAlias("-k");
        var querySourceOption = new Option<string[]>("--source", "限定搜索的源");
        querySourceOption.AddAlias("-s");
        
        queryCommand.AddArgument(queryArg);
        queryCommand.AddOption(queryModeOption);
        queryCommand.AddOption(queryTopKOption);
        queryCommand.AddOption(querySourceOption);
        
        queryCommand.SetHandler(async ctx =>
        {
            var configPath = ctx.ParseResult.GetValueForOption(configOption);
            var query = ctx.ParseResult.GetValueForArgument(queryArg);
            var mode = ctx.ParseResult.GetValueForOption(queryModeOption);
            var topK = ctx.ParseResult.GetValueForOption(queryTopKOption);
            var sources = ctx.ParseResult.GetValueForOption(querySourceOption);
            await HandleQueryCommand(configPath, query, mode, topK, sources);
        });
        rootCommand.AddCommand(queryCommand);

        // ==================== 状态命令 ====================
        var statusCommand = new Command("status", "查看索引状态");
        statusCommand.SetHandler(async ctx =>
        {
            var configPath = ctx.ParseResult.GetValueForOption(configOption);
            await HandleStatusCommand(configPath);
        });
        rootCommand.AddCommand(statusCommand);

        // ==================== 清理命令 ====================
        var cleanCommand = new Command("clean", "清理索引数据");
        var cleanConfirmOption = new Option<bool>("--confirm", () => false, "确认清理");
        var cleanSourceOption = new Option<string?>("--source", "指定清理的源");
        cleanSourceOption.AddAlias("-s");
        
        cleanCommand.AddOption(cleanConfirmOption);
        cleanCommand.AddOption(cleanSourceOption);
        
        cleanCommand.SetHandler(async ctx =>
        {
            var configPath = ctx.ParseResult.GetValueForOption(configOption);
            var confirm = ctx.ParseResult.GetValueForOption(cleanConfirmOption);
            var source = ctx.ParseResult.GetValueForOption(cleanSourceOption);
            await HandleCleanCommand(configPath, confirm, source);
        });
        rootCommand.AddCommand(cleanCommand);

        // ==================== 监控命令 ====================
        var watchCommand = new Command("watch", "监控文件变化并自动索引");
        var watchSourceOption = new Option<string[]>("--source", "监控的源");
        watchSourceOption.AddAlias("-s");
        
        watchCommand.AddOption(watchSourceOption);
        
        watchCommand.SetHandler(async ctx =>
        {
            var configPath = ctx.ParseResult.GetValueForOption(configOption);
            var sources = ctx.ParseResult.GetValueForOption(watchSourceOption);
            await HandleWatchCommand(configPath, sources);
        });
        rootCommand.AddCommand(watchCommand);

        // 添加模型和测试命令
        rootCommand.AddCommand(ObsidianRAG.CLI.Commands.ModelCommands.CreateModelCommand());
        rootCommand.AddCommand(ObsidianRAG.CLI.Commands.TestCommands.CreateTestCommand());

        return await rootCommand.InvokeAsync(args);
    }

    private static ObsidianRagConfig LoadConfig(string? configPath)
    {
        _configManager = new ConfigManager(configPath);
        _config = _configManager.Load();
        return _config;
    }

    // ==================== 源管理 ====================

    private static void HandleSourceList(string? configPath)
    {
        var config = LoadConfig(configPath);
        
        Console.WriteLine("源文件夹列表:\n");
        
        if (config.Sources.Count == 0)
        {
            Console.WriteLine("  (无)");
            Console.WriteLine("\n使用 'source add <path>' 添加源");
            return;
        }

        for (int i = 0; i < config.Sources.Count; i++)
        {
            var source = config.Sources[i];
            var status = source.Enabled ? "✓" : "✗";
            var type = source.Type.ToString();
            
            Console.WriteLine($"  [{i + 1}] {status} {source.Name}");
            Console.WriteLine($"      路径: {source.Path}");
            Console.WriteLine($"      类型: {type}");
            Console.WriteLine($"      模式: {string.Join(", ", source.FilePatterns)}");
            Console.WriteLine($"      递归: {source.Recursive}");
            
            if (Directory.Exists(source.Path))
            {
                var fileCount = CountFiles(source);
                Console.WriteLine($"      文件: {fileCount} 个");
            }
            else
            {
                Console.WriteLine($"      状态: ⚠ 路径不存在");
            }
            
            Console.WriteLine();
        }
    }

    private static void HandleSourceAdd(string? configPath, string path, string? name, string type, string[] patterns, bool recursive)
    {
        if (!Directory.Exists(path))
        {
            Console.WriteLine($"错误: 目录不存在: {path}");
            return;
        }

        var config = LoadConfig(configPath);
        
        var sourceType = type.ToLower() switch
        {
            "obsidian" => SourceType.Obsidian,
            "markdown" => SourceType.Markdown,
            "documents" => SourceType.Documents,
            "code" => SourceType.CodeDocs,
            "custom" => SourceType.Custom,
            _ => SourceType.Markdown
        };

        var excludeDirs = new List<string> { ".git", "node_modules" };
        if (sourceType == SourceType.Obsidian)
        {
            excludeDirs.AddRange(new[] { ".obsidian", ".trash" });
        }

        var source = new SourceFolder
        {
            Path = Path.GetFullPath(path),
            Name = name ?? Path.GetFileName(path) ?? $"Source{config.Sources.Count + 1}",
            Type = sourceType,
            FilePatterns = patterns.ToList(),
            Recursive = recursive,
            ExcludeDirs = excludeDirs
        };

        _configManager!.AddSource(source);
        
        Console.WriteLine($"\n已添加源:");
        Console.WriteLine($"  名称: {source.Name}");
        Console.WriteLine($"  路径: {source.Path}");
        Console.WriteLine($"  类型: {source.Type}");
        Console.WriteLine($"  模式: {string.Join(", ", source.FilePatterns)}");
    }

    private static void HandleSourceRemove(string? configPath, string name)
    {
        LoadConfig(configPath);
        _configManager!.RemoveSource(name);
    }

    private static void HandleSourceToggle(string? configPath, string name, bool enabled)
    {
        LoadConfig(configPath);
        _configManager!.ToggleSource(name, enabled);
    }

    // ==================== 配置管理 ====================

    private static void HandleConfigInit(string? configPath)
    {
        _configManager = new ConfigManager(configPath);
        _configManager.CreateDefaultConfig();
        Console.WriteLine($"配置文件已创建: {_configManager.GetConfigPath()}");
        Console.WriteLine("\n使用以下命令添加源文件夹:");
        Console.WriteLine("  obsidian-rag source add /path/to/folder --name \"我的笔记\"");
    }

    private static void HandleConfigShow(string? configPath)
    {
        var config = LoadConfig(configPath);
        
        Console.WriteLine("当前配置:");
        Console.WriteLine($"  配置文件: {_configManager!.GetConfigPath()}");
        Console.WriteLine($"  数据路径: {config.DataPath}");
        Console.WriteLine($"  模型路径: {config.Embedding.ModelPath}");
        Console.WriteLine($"  模型名称: {config.Embedding.ModelName}");
        Console.WriteLine($"  服务端口: {config.Service.Port}");
        Console.WriteLine($"  源数量: {config.Sources.Count}");
        
        var (valid, errors, warnings) = _configManager.Validate(config);
        
        if (errors.Count > 0)
        {
            Console.WriteLine("\n❌ 配置错误:");
            foreach (var error in errors)
            {
                Console.WriteLine($"  • {error}");
            }
        }
        
        if (warnings.Count > 0)
        {
            Console.WriteLine("\n⚠ 配置警告:");
            foreach (var warning in warnings)
            {
                Console.WriteLine($"  • {warning}");
            }
        }
        
        if (valid)
        {
            Console.WriteLine("\n✅ 配置有效");
        }
    }

    // ==================== 索引 ====================

    private static async Task HandleIndexCommand(string? configPath, string[]? paths, bool force, bool verbose)
    {
        var config = LoadConfig(configPath);
        
        var sources = config.Sources.Where(s => s.Enabled).ToList();
        
        if (paths != null && paths.Length > 0)
        {
            sources = sources.Where(s => 
                paths.Any(p => 
                    s.Name.Equals(p, StringComparison.OrdinalIgnoreCase) ||
                    s.Path.Equals(p, StringComparison.OrdinalIgnoreCase) ||
                    s.Path.StartsWith(p, StringComparison.OrdinalIgnoreCase)
                )
            ).ToList();
        }

        if (sources.Count == 0)
        {
            Console.WriteLine("没有可索引的源");
            Console.WriteLine("使用 'source add <path>' 添加源");
            return;
        }

        Console.WriteLine($"准备索引 {sources.Count} 个源:\n");
        foreach (var s in sources)
        {
            Console.WriteLine($"  • {s.Name} ({s.Path})");
        }
        Console.WriteLine();

        var serviceProvider = CreateServiceProvider(config);
        var chunker = serviceProvider.GetRequiredService<MarkdownChunker>();
        var vectorStore = serviceProvider.GetRequiredService<IVectorStore>();
        var embeddingService = serviceProvider.GetRequiredService<IEmbeddingService>();
        var hashDetector = serviceProvider.GetRequiredService<ContentHashDetector>();

        var totalProcessed = 0;
        var totalSkipped = 0;
        var totalChunks = 0;

        foreach (var source in sources)
        {
            Console.WriteLine($"\n=== 索引源: {source.Name} ===\n");

            if (!Directory.Exists(source.Path))
            {
                Console.WriteLine($"  ⚠ 路径不存在: {source.Path}");
                continue;
            }

            var files = GetFiles(source);
            Console.WriteLine($"  找到 {files.Count} 个文件");

            var processed = 0;
            var skipped = 0;

            foreach (var filePath in files)
            {
                try
                {
                    var content = await File.ReadAllTextAsync(filePath);
                    var contentHash = hashDetector.ComputeFingerprint(content);

                    if (!force)
                    {
                        var existing = await vectorStore.GetFileByPathAsync(filePath);
                        if (existing != null && existing.ContentHash == contentHash)
                        {
                            skipped++;
                            if (verbose) Console.WriteLine($"    跳过: {Path.GetFileName(filePath)}");
                            continue;
                        }
                    }

                    // 先生成分块，以便正确设置 ChunkCount
                    var chunks = chunker.Chunk(content, new FileRecord { Path = filePath }).ToList();
                    foreach (var chunk in chunks)
                    {
                        chunk.Source = source.Name;
                    }

                    var fileRecord = new FileRecord
                    {
                        Id = chunks.FirstOrDefault()?.FileId ?? Guid.NewGuid().ToString(),
                        Path = filePath,
                        Title = Path.GetFileNameWithoutExtension(filePath),
                        ContentHash = contentHash,
                        ParentFolder = Path.GetDirectoryName(filePath) ?? "",
                        Source = source.Name,
                        CreatedAt = File.GetCreationTime(filePath),
                        ModifiedAt = File.GetLastWriteTime(filePath),
                        ChunkCount = chunks.Count,
                        IndexedAt = DateTime.UtcNow
                    };

                    await vectorStore.UpsertFileAsync(fileRecord);

                    // 更新 chunks 的 FileId
                    foreach (var chunk in chunks)
                    {
                        chunk.FileId = fileRecord.Id;
                    }

                    await vectorStore.UpsertChunksAsync(chunks);

                    var vectors = new List<VectorRecord>();
                    foreach (var chunk in chunks)
                    {
                        var embedding = await embeddingService.EncodeAsync(chunk.Content);
                        vectors.Add(new VectorRecord
                        {
                            Id = Guid.NewGuid().ToString(),
                            ChunkId = chunk.Id,
                            FileId = chunk.FileId,  // 修复：正确关联 FileId
                            Vector = embedding,
                            Dimension = embedding.Length,
                            Source = chunk.Source,
                            ModelName = embeddingService.ModelName
                        });
                    }

                    await vectorStore.UpsertVectorsAsync(vectors);

                    processed++;
                    totalChunks += chunks.Count;

                    if (verbose || processed % 10 == 0)
                    {
                        Console.WriteLine($"    ✓ {Path.GetFileName(filePath)} ({chunks.Count} 分段)");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"    ✗ {Path.GetFileName(filePath)}: {ex.Message}");
                }
            }

            Console.WriteLine($"\n  完成: {processed} 处理, {skipped} 跳过");
            totalProcessed += processed;
            totalSkipped += skipped;
        }

        Console.WriteLine($"\n=== 索引完成 ===");
        Console.WriteLine($"  总处理: {totalProcessed}");
        Console.WriteLine($"  总跳过: {totalSkipped}");
        Console.WriteLine($"  总分段: {totalChunks}");
    }

    private static List<string> GetFiles(SourceFolder source)
    {
        var files = new List<string>();
        
        foreach (var pattern in source.FilePatterns)
        {
            var found = Directory.EnumerateFiles(
                source.Path,
                pattern,
                source.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly
            );

            foreach (var file in found)
            {
                var dir = Path.GetDirectoryName(file)?.ToLower() ?? "";
                var excluded = source.ExcludeDirs.Any(d => dir.Contains(d.ToLower()));
                
                var fileName = Path.GetFileName(file);
                var fileExcluded = source.ExcludeFiles.Any(p => 
                    p.StartsWith("*") && fileName.EndsWith(p.Substring(1)) ||
                    p.EndsWith("*") && fileName.StartsWith(p.Substring(0, p.Length - 1)) ||
                    fileName.Equals(p, StringComparison.OrdinalIgnoreCase)
                );

                if (!excluded && !fileExcluded && !files.Contains(file))
                {
                    files.Add(file);
                }
            }
        }

        return files;
    }

    private static int CountFiles(SourceFolder source)
    {
        try { return GetFiles(source).Count; }
        catch { return 0; }
    }

    // ==================== 查询 ====================

    private static async Task HandleQueryCommand(string? configPath, string query, string mode, int topK, string[]? sources)
    {
        var config = LoadConfig(configPath);
        Console.WriteLine($"查询: {query}");
        Console.WriteLine($"模式: {mode}, TopK: {topK}");
        
        if (sources != null && sources.Length > 0)
        {
            Console.WriteLine($"限定源: {string.Join(", ", sources)}");
        }

        var serviceProvider = CreateServiceProvider(config);
        var searchService = serviceProvider.GetRequiredService<ISearchService>();

        var queryMode = mode.ToLower() switch
        {
            "quick" => QueryMode.Quick,
            "deep" => QueryMode.Deep,
            _ => QueryMode.Standard
        };

        var request = new AIQueryRequest
        {
            Query = query,
            Mode = queryMode,
            TopK = topK,
            Sources = sources?.ToList() ?? new List<string>()
        };

        try
        {
            var response = await searchService.SearchAsync(request);

            Console.WriteLine($"\n找到 {response.Chunks.Count} 个结果:\n");

            foreach (var chunk in response.Chunks)
            {
                Console.WriteLine($"--- [{chunk.RefId}] 分数: {chunk.Score:F4} ---");
                Console.WriteLine($"源: {chunk.Source ?? "未知"}");
                Console.WriteLine($"文件: {chunk.FilePath}");
                if (!string.IsNullOrEmpty(chunk.HeadingPath))
                {
                    Console.WriteLine($"章节: {chunk.HeadingPath}");
                }
                Console.WriteLine($"行号: {chunk.StartLine}-{chunk.EndLine}");
                Console.WriteLine($"内容: {chunk.Content.Substring(0, Math.Min(200, chunk.Content.Length))}...");
                Console.WriteLine();
            }

            if (!string.IsNullOrEmpty(response.Suggestion))
            {
                Console.WriteLine($"建议: {response.Suggestion}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"查询错误: {ex.Message}");
        }
    }

    // ==================== 状态 ====================

    private static async Task HandleStatusCommand(string? configPath)
    {
        var config = LoadConfig(configPath);
        var serviceProvider = CreateServiceProvider(config);
        var vectorStore = serviceProvider.GetRequiredService<IVectorStore>();

        var files = await vectorStore.GetAllFilesAsync();
        var fileList = files.ToList();

        Console.WriteLine("索引状态:\n");
        Console.WriteLine($"  数据路径: {config.DataPath}");
        Console.WriteLine($"  总文件数: {fileList.Count}");

        var bySource = fileList.GroupBy(f => f.Source ?? "未知");
        Console.WriteLine("\n  按源统计:");
        foreach (var group in bySource)
        {
            Console.WriteLine($"    • {group.Key}: {group.Count()} 文件");
        }

        if (fileList.Count > 0)
        {
            var oldest = fileList.Min(f => f.IndexedAt);
            var newest = fileList.Max(f => f.IndexedAt);
            
            Console.WriteLine($"\n  最早索引: {oldest:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($"  最新索引: {newest:yyyy-MM-dd HH:mm:ss}");
        }
    }

    // ==================== 清理 ====================

    private static async Task HandleCleanCommand(string? configPath, bool confirm, string? source)
    {
        if (!confirm)
        {
            Console.WriteLine("请使用 --confirm 选项确认清理操作");
            return;
        }

        var config = LoadConfig(configPath);
        var serviceProvider = CreateServiceProvider(config);
        var vectorStore = serviceProvider.GetRequiredService<IVectorStore>();

        var files = await vectorStore.GetAllFilesAsync();
        var fileList = files.ToList();

        if (!string.IsNullOrEmpty(source))
        {
            fileList = fileList.Where(f => f.Source == source).ToList();
        }

        foreach (var file in fileList)
        {
            await vectorStore.DeleteFileAsync(file.Id);
        }

        Console.WriteLine($"已清理 {fileList.Count} 个文件的索引数据");
    }

    // ==================== 监控 ====================

    private static async Task HandleWatchCommand(string? configPath, string[]? sources)
    {
        var config = LoadConfig(configPath);
        
        var watchSources = config.Sources.Where(s => s.Enabled).ToList();
        if (sources != null && sources.Length > 0)
        {
            watchSources = watchSources.Where(s => 
                sources.Any(p => s.Name.Equals(p, StringComparison.OrdinalIgnoreCase))
            ).ToList();
        }

        if (watchSources.Count == 0)
        {
            Console.WriteLine("没有可监控的源");
            return;
        }

        Console.WriteLine($"监控 {watchSources.Count} 个源:");
        foreach (var s in watchSources)
        {
            Console.WriteLine($"  • {s.Name} ({s.Path})");
        }
        Console.WriteLine("\n按 Ctrl+C 停止监控...\n");

        var detectors = new List<FileChangeDetector>();

        foreach (var source in watchSources)
        {
            var detector = new FileChangeDetector(source.Path);
            
            detector.FileChanged += (sender, e) =>
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{source.Name}] 变更: {Path.GetFileName(e.FilePath)}");
            };

            detector.FileDeleted += (sender, e) =>
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{source.Name}] 删除: {Path.GetFileName(e.FilePath)}");
            };

            detector.FileRenamed += (sender, e) =>
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{source.Name}] 重命名: {Path.GetFileName(e.OldFilePath)} -> {Path.GetFileName(e.FilePath)}");
            };

            detectors.Add(detector);
        }

        var tcs = new TaskCompletionSource();
        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            tcs.SetResult();
        };

        await tcs.Task;

        foreach (var detector in detectors)
        {
            detector.Dispose();
        }

        Console.WriteLine("\n监控已停止");
    }

    // ==================== 辅助方法 ====================

    private static IServiceProvider CreateServiceProvider(ObsidianRagConfig config)
    {
        var services = new ServiceCollection();
        
        // 添加日志
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Warning));
        
        services.AddSingleton(config);
        services.AddSingleton<ITokenizer, SimpleTokenizer>();
        services.AddSingleton<ITextEnhancer, TextEnhancer>();
        services.AddSingleton<IVectorStore>(sp => new JsonVectorStore(config.DataPath));
        services.AddSingleton<IEmbeddingService>(sp =>
            new EmbeddingService(new EmbeddingOptions
            {
                ModelPath = config.Embedding.ModelPath,
                ModelName = config.Embedding.ModelName,
                UseCuda = config.Embedding.UseCuda,
                CudaDeviceId = config.Embedding.CudaDeviceId,
                CudaLibraryPath = config.Embedding.CudaLibraryPath,
                MaxSequenceLength = config.Embedding.MaxSequenceLength,
                BatchSize = config.Embedding.BatchSize
            }));
        services.AddScoped<ISearchService, SearchService>();
        services.AddSingleton<MarkdownChunker>();
        services.AddSingleton<ContentHashDetector>();

        return services.BuildServiceProvider();
    }
}
