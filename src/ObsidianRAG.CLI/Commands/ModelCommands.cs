using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ObsidianRAG.Core.Interfaces;
using ObsidianRAG.Core.Models;
using ObsidianRAG.Core.Services;
using ObsidianRAG.Storage;

namespace ObsidianRAG.CLI.Commands;

/// <summary>
/// 模型管理命令
/// </summary>
public static class ModelCommands
{
    public static Command CreateModelCommand()
    {
        var modelCommand = new Command("model", "模型管理");

        // model list
        var listCommand = new Command("list", "列出所有可用模型");
        listCommand.SetHandler(async ctx =>
        {
            await HandleModelList();
        });

        // model current
        var currentCommand = new Command("current", "显示当前使用的模型");
        currentCommand.SetHandler(async ctx =>
        {
            await HandleModelCurrent();
        });

        // model switch
        var switchCommand = new Command("switch", "切换模型");
        var switchArg = new Argument<string>("name", "模型名称");
        switchCommand.AddArgument(switchArg);
        switchCommand.SetHandler(async ctx =>
        {
            var name = ctx.ParseResult.GetValueForArgument(switchArg);
            await HandleModelSwitch(name);
        });

        // model download
        var downloadCommand = new Command("download", "下载模型");
        var downloadArg = new Argument<string>("name", "模型名称");
        downloadCommand.AddArgument(downloadArg);
        downloadCommand.SetHandler(async ctx =>
        {
            var name = ctx.ParseResult.GetValueForArgument(downloadArg);
            await HandleModelDownload(name);
        });

        // model quantize
        var quantizeCommand = new Command("quantize", "量化模型");
        var quantizeArg = new Argument<string>("name", "模型名称");
        var quantizeTypeOption = new Option<string>("--type", () => "fp16", "量化类型: fp16, int8");
        quantizeCommand.AddArgument(quantizeArg);
        quantizeCommand.AddOption(quantizeTypeOption);
        quantizeCommand.SetHandler(async ctx =>
        {
            var name = ctx.ParseResult.GetValueForArgument(quantizeArg);
            var type = ctx.ParseResult.GetValueForOption(quantizeTypeOption);
            await HandleModelQuantize(name, type);
        });

        // model delete
        var deleteCommand = new Command("delete", "删除模型");
        var deleteArg = new Argument<string>("name", "模型名称");
        deleteCommand.AddArgument(deleteArg);
        deleteCommand.SetHandler(async ctx =>
        {
            var name = ctx.ParseResult.GetValueForArgument(deleteArg);
            await HandleModelDelete(name);
        });

        // model recommend
        var recommendCommand = new Command("recommend", "获取模型推荐");
        var langOption = new Option<string?>("--lang", "语言偏好: zh, en");
        var gpuOption = new Option<bool>("--gpu", () => true, "是否优先GPU模型");
        recommendCommand.AddOption(langOption);
        recommendCommand.AddOption(gpuOption);
        recommendCommand.SetHandler(async ctx =>
        {
            var lang = ctx.ParseResult.GetValueForOption(langOption);
            var gpu = ctx.ParseResult.GetValueForOption(gpuOption);
            await HandleModelRecommend(lang, gpu);
        });

        modelCommand.AddCommand(listCommand);
        modelCommand.AddCommand(currentCommand);
        modelCommand.AddCommand(switchCommand);
        modelCommand.AddCommand(downloadCommand);
        modelCommand.AddCommand(quantizeCommand);
        modelCommand.AddCommand(deleteCommand);
        modelCommand.AddCommand(recommendCommand);

        return modelCommand;
    }

    private static async Task HandleModelList()
    {
        var modelsPath = Path.Combine(Environment.CurrentDirectory, "models");
        var configPath = Path.Combine(Environment.CurrentDirectory, "obsidian-rag.json");
        
        var manager = new ModelManager(modelsPath, configPath);
        var models = await manager.GetAvailableModelsAsync();

        Console.WriteLine("\n可用模型:\n");
        Console.WriteLine("{0,-30} {1,-10} {2,-10} {3,-15} {4,-10}", 
            "名称", "维度", "状态", "大小", "评分");
        Console.WriteLine(new string('-', 80));

        foreach (var model in models.OrderByDescending(m => m.BenchmarkScore ?? 0))
        {
            var status = model.IsDownloaded ? "✓ 已下载" : "✗ 未下载";
            var size = model.ModelSizeBytes > 0 
                ? $"{model.ModelSizeBytes / 1024.0 / 1024.0:F1} MB" 
                : "-";
            var score = model.BenchmarkScore.HasValue 
                ? $"{model.BenchmarkScore.Value * 100:F0}%" 
                : "-";
            var quant = model.IsQuantized ? $" ({model.QuantizationType})" : "";

            Console.WriteLine("{0,-30} {1,-10} {2,-10} {3,-15} {4,-10}",
                model.Name + quant,
                model.Dimension,
                status,
                size,
                score);
        }

        Console.WriteLine($"\n共 {models.Count} 个模型");
    }

    private static async Task HandleModelCurrent()
    {
        var configPath = Path.Combine(Environment.CurrentDirectory, "obsidian-rag.json");
        
        if (File.Exists(configPath))
        {
            var json = await File.ReadAllTextAsync(configPath);
            var config = System.Text.Json.JsonSerializer.Deserialize<ObsidianRagConfig>(json);
            
            if (config != null)
            {
                Console.WriteLine($"\n当前模型: {config.Embedding.ModelName}");
                Console.WriteLine($"模型路径: {config.Embedding.ModelPath}");
                Console.WriteLine($"GPU加速: {(config.Embedding.UseCuda ? "启用" : "禁用")}");
            }
        }
        else
        {
            Console.WriteLine("配置文件不存在");
        }
    }

    private static async Task HandleModelSwitch(string name)
    {
        var modelsPath = Path.Combine(Environment.CurrentDirectory, "models");
        var configPath = Path.Combine(Environment.CurrentDirectory, "obsidian-rag.json");
        
        var manager = new ModelManager(modelsPath, configPath);
        var success = await manager.SwitchModelAsync(name);

        if (success)
        {
            Console.WriteLine($"\n✓ 已切换到模型: {name}");
            Console.WriteLine("重启服务以应用更改");
        }
        else
        {
            Console.WriteLine($"\n✗ 切换失败: {name}");
        }
    }

    private static async Task HandleModelDownload(string name)
    {
        var modelsPath = Path.Combine(Environment.CurrentDirectory, "models");
        var configPath = Path.Combine(Environment.CurrentDirectory, "obsidian-rag.json");
        
        var manager = new ModelManager(modelsPath, configPath);
        
        Console.WriteLine($"\n开始下载模型: {name}");
        
        var progress = new Progress<float>(p =>
        {
            Console.Write($"\r下载进度: {p:F1}%");
        });

        var success = await manager.DownloadModelAsync(name, progress);

        if (success)
        {
            Console.WriteLine("\n✓ 下载完成");
        }
        else
        {
            Console.WriteLine("\n✗ 下载失败");
        }
    }

    private static async Task HandleModelQuantize(string name, string type)
    {
        var modelsPath = Path.Combine(Environment.CurrentDirectory, "models");
        var configPath = Path.Combine(Environment.CurrentDirectory, "obsidian-rag.json");
        
        var manager = new ModelManager(modelsPath, configPath);
        
        Console.WriteLine($"\n开始量化模型: {name} -> {type}");
        
        var success = await manager.QuantizeModelAsync(name, type);

        if (success)
        {
            Console.WriteLine("✓ 量化完成");
        }
        else
        {
            Console.WriteLine("✗ 量化失败");
        }
    }

    private static async Task HandleModelDelete(string name)
    {
        var modelsPath = Path.Combine(Environment.CurrentDirectory, "models");
        var configPath = Path.Combine(Environment.CurrentDirectory, "obsidian-rag.json");
        
        var manager = new ModelManager(modelsPath, configPath);
        
        Console.WriteLine($"\n确认删除模型: {name}? (y/n)");
        var confirm = Console.ReadLine();
        
        if (confirm?.ToLower() == "y")
        {
            var success = await manager.DeleteModelAsync(name);
            Console.WriteLine(success ? "✓ 已删除" : "✗ 删除失败");
        }
        else
        {
            Console.WriteLine("已取消");
        }
    }

    private static async Task HandleModelRecommend(string? language, bool preferGpu)
    {
        var modelsPath = Path.Combine(Environment.CurrentDirectory, "models");
        var configPath = Path.Combine(Environment.CurrentDirectory, "obsidian-rag.json");
        
        var manager = new ModelManager(modelsPath, configPath);
        var recommended = await manager.GetRecommendedModelsAsync(language, preferGpu);

        Console.WriteLine($"\n推荐模型 (语言: {language ?? "全部"}, GPU: {preferGpu}):\n");

        foreach (var model in recommended)
        {
            var status = model.IsDownloaded ? "✓" : "✗";
            Console.WriteLine($"  {status} {model.DisplayName}");
            Console.WriteLine($"      名称: {model.Name}");
            Console.WriteLine($"      维度: {model.Dimension}");
            Console.WriteLine($"      评分: {model.BenchmarkScore * 100:F0}%");
            Console.WriteLine($"      描述: {model.Description}");
            Console.WriteLine();
        }
    }
}

/// <summary>
/// 测试命令
/// </summary>
public static class TestCommands
{
    public static Command CreateTestCommand()
    {
        var testCommand = new Command("test", "测试工具");

        // test query
        var queryCommand = new Command("query", "查询效果测试");
        var interactiveOption = new Option<bool>("--interactive", () => false, "交互模式");
        queryCommand.AddOption(interactiveOption);
        queryCommand.SetHandler(async ctx =>
        {
            var interactive = ctx.ParseResult.GetValueForOption(interactiveOption);
            await HandleTestQuery(interactive);
        });

        // test benchmark
        var benchmarkCommand = new Command("benchmark", "基准测试");
        var iterationsOption = new Option<int>("--iterations", () => 10, "测试次数");
        benchmarkCommand.AddOption(iterationsOption);
        benchmarkCommand.SetHandler(async ctx =>
        {
            var iterations = ctx.ParseResult.GetValueForOption(iterationsOption);
            await HandleTestBenchmark(iterations);
        });

        // test vector
        var vectorCommand = new Command("vector", "向量生成测试");
        var textOption = new Option<string>("--text", () => "测试文本", "测试文本");
        vectorCommand.AddOption(textOption);
        vectorCommand.SetHandler(async ctx =>
        {
            var text = ctx.ParseResult.GetValueForOption(textOption);
            await HandleTestVector(text);
        });

        testCommand.AddCommand(queryCommand);
        testCommand.AddCommand(benchmarkCommand);
        testCommand.AddCommand(vectorCommand);

        return testCommand;
    }

    private static async Task HandleTestQuery(bool interactive)
    {
        Console.WriteLine("\n=== 查询效果测试 ===\n");

        var configPath = Path.Combine(Environment.CurrentDirectory, "obsidian-rag.json");
        var config = await LoadConfigAsync(configPath);
        var serviceProvider = CreateServiceProvider(config);

        var searchService = serviceProvider.GetRequiredService<ISearchService>();
        var embeddingService = serviceProvider.GetRequiredService<IEmbeddingService>();
        var vectorStore = serviceProvider.GetRequiredService<IVectorStore>();

        var testService = new QueryTestService(searchService, embeddingService, vectorStore);

        if (interactive)
        {
            await testService.RunInteractiveTestAsync();
        }
        else
        {
            var suite = QueryTestService.GetDefaultTestSuite();
            Console.WriteLine($"运行测试套件: {suite.Name}");
            Console.WriteLine($"测试用例: {suite.TestCases.Count} 个\n");

            var results = await testService.RunTestSuiteAsync(suite);
            var report = testService.GenerateReport(results);

            Console.WriteLine(report);

            // 保存报告
            var reportPath = Path.Combine(Environment.CurrentDirectory, "query-test-report.md");
            await File.WriteAllTextAsync(reportPath, report);
            Console.WriteLine($"\n报告已保存: {reportPath}");
        }
    }

    private static async Task HandleTestBenchmark(int iterations)
    {
        Console.WriteLine($"\n=== 基准测试 ({iterations} 次) ===\n");

        var configPath = Path.Combine(Environment.CurrentDirectory, "obsidian-rag.json");
        var config = await LoadConfigAsync(configPath);
        var serviceProvider = CreateServiceProvider(config);

        var searchService = serviceProvider.GetRequiredService<ISearchService>();
        var embeddingService = serviceProvider.GetRequiredService<IEmbeddingService>();

        // 测试向量生成性能
        Console.WriteLine("1. 向量生成性能测试");
        var testTexts = Enumerable.Range(0, 10).Select(i => $"测试文本 {i}，用于测试向量生成性能。").ToList();
        
        var embedTimes = new List<long>();
        for (int i = 0; i < iterations; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await embeddingService.EncodeBatchAsync(testTexts);
            sw.Stop();
            embedTimes.Add(sw.ElapsedMilliseconds);
        }

        Console.WriteLine($"   平均: {embedTimes.Average():F1}ms");
        Console.WriteLine($"   最小: {embedTimes.Min()}ms");
        Console.WriteLine($"   最大: {embedTimes.Max()}ms");
        Console.WriteLine($"   P95: {embedTimes.OrderBy(x => x).Skip((int)(iterations * 0.95)).First()}ms");

        // 测试查询性能
        Console.WriteLine("\n2. 查询性能测试");
        var queryTimes = new List<long>();
        var testQueries = new[] { "如何配置？", "核心功能", "系统架构", "部署方法", "使用指南" };

        for (int i = 0; i < iterations; i++)
        {
            var query = testQueries[i % testQueries.Length];
            var sw = System.Diagnostics.Stopwatch.StartNew();
            
            var request = new AIQueryRequest { Query = query, TopK = 10 };
            await searchService.SearchAsync(request);
            
            sw.Stop();
            queryTimes.Add(sw.ElapsedMilliseconds);
        }

        Console.WriteLine($"   平均: {queryTimes.Average():F1}ms");
        Console.WriteLine($"   最小: {queryTimes.Min()}ms");
        Console.WriteLine($"   最大: {queryTimes.Max()}ms");
        Console.WriteLine($"   P95: {queryTimes.OrderBy(x => x).Skip((int)(iterations * 0.95)).First()}ms");

        Console.WriteLine("\n基准测试完成");
    }

    private static async Task HandleTestVector(string text)
    {
        Console.WriteLine($"\n=== 向量生成测试 ===\n");
        Console.WriteLine($"文本: {text}\n");

        var configPath = Path.Combine(Environment.CurrentDirectory, "obsidian-rag.json");
        var config = await LoadConfigAsync(configPath);
        var serviceProvider = CreateServiceProvider(config);
        var embeddingService = serviceProvider.GetRequiredService<IEmbeddingService>();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var vector = await embeddingService.EncodeAsync(text);
        sw.Stop();

        Console.WriteLine($"向量维度: {vector.Length}");
        Console.WriteLine($"生成耗时: {sw.ElapsedMilliseconds}ms");
        Console.WriteLine($"模型名称: {embeddingService.ModelName}");
        Console.WriteLine($"\n前10维: [{string.Join(", ", vector.Take(10).Select(v => v.ToString("F4")))} ...]");

        // 验证归一化
        var norm = Math.Sqrt(vector.Sum(v => v * v));
        Console.WriteLine($"向量范数: {norm:F6} (应接近1.0)");
    }

    private static async Task<ObsidianRagConfig> LoadConfigAsync(string configPath)
    {
        if (File.Exists(configPath))
        {
            var json = await File.ReadAllTextAsync(configPath);
            return System.Text.Json.JsonSerializer.Deserialize<ObsidianRagConfig>(json) ?? new ObsidianRagConfig();
        }
        return new ObsidianRagConfig();
    }

    private static IServiceProvider CreateServiceProvider(ObsidianRagConfig config)
    {
        var services = new ServiceCollection();
        
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
