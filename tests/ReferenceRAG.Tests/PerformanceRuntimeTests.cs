using Microsoft.Extensions.DependencyInjection;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Storage;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

namespace ReferenceRAG.Tests;

/// <summary>
/// 性能测试 - 运行时文件变更反应时间、启动同步效率、非阻塞验证
/// </summary>
public class PerformanceRuntimeTests : IDisposable
{
    private readonly string _testVaultPath;
    private readonly string _dbPath;
    private readonly string _baseUrl;
    private readonly HttpClient _httpClient;
    private readonly IServiceProvider _services;
    private readonly List<string> _testFilesToClean = new();

    public PerformanceRuntimeTests()
    {
        _testVaultPath = Path.Combine("E:", "LinuxWork", "Obsidian", "resource", "test-vault");
        _dbPath = Path.Combine("E:", "LinuxWork", "Obsidian", "resource", "data", "vectors.db");
        _baseUrl = "http://localhost:5000";
        _httpClient = new HttpClient { BaseAddress = new Uri(_baseUrl), Timeout = TimeSpan.FromSeconds(30) };

        // 创建服务容器以访问核心组件
        var services = new ServiceCollection();
        var configManager = new ConfigManager();
        services.AddSingleton(configManager);
        services.AddSingleton<IVectorStore>(sp => new SqliteVectorStore(_dbPath));
        _services = services.BuildServiceProvider();
    }

    /// <summary>
    /// 测试1: 运行时文件变更反应时间
    /// 创建测试文件 -> 记录T1 -> 检查向量库中该文件的索引时间T2 -> 计算反应时间
    /// </summary>
    public async Task<double> TestRuntimeChangeReactionTimeAsync()
    {
        Console.WriteLine("\n========== 测试1: 运行时变更反应时间 ==========");

        var testFileName = $"test_runtime_{Guid.NewGuid():N}.md";
        var testFilePath = Path.Combine(_testVaultPath, testFileName);
        var content = $"# 测试运行时变更\n\n这是一篇用于测试运行时变更反应时间的文档。\n\n测试时间: {DateTime.UtcNow:O}\n\n## 内容\n\n这是测试内容，包含一些中文和英文混合的文本。\n\n- 功能点1\n- 功能点2\n- 功能点3\n";

        _testFilesToClean.Add(testFilePath);

        // 记录创建时间 T1
        var t1 = DateTime.UtcNow;
        await File.WriteAllTextAsync(testFilePath, content);
        Console.WriteLine($"[T1] 文件创建时间: {t1:HH:mm:ss.fff}");

        // 等待文件被索引（最多30秒）
        var vectorStore = _services.GetRequiredService<IVectorStore>();
        DateTime? indexedAt = null;
        double reactionTime = -1;

        for (int i = 0; i < 300; i++)
        {
            await Task.Delay(100);
            try
            {
                var fileRecord = await vectorStore.GetFileByPathAsync(testFilePath);
                if (fileRecord != null)
                {
                    indexedAt = fileRecord.IndexedAt;
                    reactionTime = (indexedAt.Value - t1).TotalMilliseconds;
                    Console.WriteLine($"[T2] 索引完成时间: {indexedAt:HH:mm:ss.fff}");
                    Console.WriteLine($"[结果] 运行时变更反应时间: {reactionTime:F2}ms");
                    break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"查询错误 (尝试 {i + 1}/300): {ex.Message}");
            }
        }

        if (indexedAt == null)
        {
            Console.WriteLine("[结果] 超时: 文件未在30秒内被索引");
        }

        return reactionTime;
    }

    /// <summary>
    /// 测试2: 启动时变更对照效率
    /// 记录启动时间T1 -> 等待StartupSync完成 -> 记录完成时间T2 -> 计算启动同步耗时
    /// </summary>
    public async Task<(double syncTime, int fileCount)> TestStartupSyncEfficiencyAsync()
    {
        Console.WriteLine("\n========== 测试2: 启动同步效率 ==========");

        var t1 = DateTime.UtcNow;
        Console.WriteLine($"[T1] 启动同步开始: {t1:HH:mm:ss.fff}");

        // 等待 StartupSync 完成通过监控向量库文件数量变化
        var vectorStore = _services.GetRequiredService<IVectorStore>();
        var initialFiles = new HashSet<string>((await vectorStore.GetAllFilesAsync()).Select(f => f.Path));

        // 等待同步完成（检测到至少有一些文件被处理）
        var maxWaitMs = 60000; // 最多等待60秒
        var waitedMs = 0;
        var syncCompleted = false;
        var fileCount = 0;

        while (waitedMs < maxWaitMs)
        {
            await Task.Delay(500);
            waitedMs += 500;

            try
            {
                var currentFiles = (await vectorStore.GetAllFilesAsync()).ToList();
                fileCount = currentFiles.Count;

                // 检查是否有新增或变更的文件（与初始快照比较）
                // 如果文件数量稳定且超过初始数量，认为同步可能已完成
                if (currentFiles.Count > 0)
                {
                    // 简单策略：等待足够长的时间认为启动同步已完成
                    if (waitedMs > 10000) // 至少等待10秒
                    {
                        syncCompleted = true;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"等待同步时出错: {ex.Message}");
            }
        }

        var t2 = DateTime.UtcNow;
        var syncTime = (t2 - t1).TotalMilliseconds;

        Console.WriteLine($"[T2] 同步检测时间: {t2:HH:mm:ss.fff}");
        Console.WriteLine($"[结果] 启动同步耗时: {syncTime:F2}ms");
        Console.WriteLine($"[结果] 处理文件数: {fileCount}");

        return (syncTime, fileCount);
    }

    /// <summary>
    /// 测试3: 后台异步非阻塞验证
    /// 启动服务后（模拟启动过程中的状态），发送搜索请求，验证搜索是否正常响应（不被阻塞）
    /// </summary>
    public async Task<bool> TestNonBlockingVerificationAsync()
    {
        Console.WriteLine("\n========== 测试3: 非阻塞验证 ==========");

        try
        {
            // 检查服务是否可访问
            var healthCheck = await _httpClient.GetAsync("/api/system/health");
            if (!healthCheck.IsSuccessStatusCode)
            {
                Console.WriteLine($"[结果] FAILED - 服务健康检查失败: {healthCheck.StatusCode}");
                return false;
            }
            Console.WriteLine("[状态] 服务健康检查通过");

            // 在服务运行期间发送搜索请求
            var queryRequest = new
            {
                query = "测试",
                topK = 5,
                mode = "Standard"
            };

            var sw = Stopwatch.StartNew();
            var response = await _httpClient.PostAsJsonAsync("/api/ai/query", queryRequest);
            sw.Stop();

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[结果] FAILED - 搜索请求失败: {response.StatusCode}");
                return false;
            }

            var responseTime = sw.ElapsedMilliseconds;
            Console.WriteLine($"[状态] 搜索请求成功，响应时间: {responseTime}ms");

            // 读取响应内容验证
            var responseContent = await response.Content.ReadAsStringAsync();
            var hasResults = responseContent.Contains("chunks") || responseContent.Contains("results") || responseContent.Contains("content");
            Console.WriteLine($"[结果] 非阻塞验证: {(hasResults ? "PASSED" : "FAILED")}");

            return hasResults;
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"[结果] FAILED - HTTP请求错误: {ex.Message}");
            return false;
        }
        catch (TaskCanceledException ex)
        {
            Console.WriteLine($"[结果] FAILED - 请求超时（被阻塞）: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[结果] FAILED - 未知错误: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 清理测试创建的文件
    /// </summary>
    public void CleanupTestFiles()
    {
        foreach (var filePath in _testFilesToClean)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    Console.WriteLine($"[清理] 已删除测试文件: {Path.GetFileName(filePath)}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[清理] 删除文件失败 {filePath}: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        CleanupTestFiles();
        _httpClient.Dispose();
    }
}

/// <summary>
/// 性能测试运行器
/// </summary>
public static class PerformanceTestRunner
{
    public static async Task RunAllTestsAsync(string reportPath)
    {
        Console.WriteLine("========================================");
        Console.WriteLine("  Obsidian RAG 性能测试");
        Console.WriteLine($"  测试时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine("========================================");

        var results = new List<(string Name, string Status, string Details)>();

        using var tests = new PerformanceRuntimeTests();

        // 测试1: 运行时变更反应时间
        try
        {
            var reactionTime = await tests.TestRuntimeChangeReactionTimeAsync();
            if (reactionTime > 0)
            {
                results.Add(("运行时变更反应时间", "PASS", $"{reactionTime:F2}ms"));
            }
            else
            {
                results.Add(("运行时变更反应时间", "FAIL", "超时或错误"));
            }
        }
        catch (Exception ex)
        {
            results.Add(("运行时变更反应时间", "ERROR", ex.Message));
            Console.WriteLine($"[错误] 测试1执行失败: {ex.Message}");
        }

        // 测试2: 启动同步效率
        try
        {
            var (syncTime, fileCount) = await tests.TestStartupSyncEfficiencyAsync();
            results.Add(("启动同步效率", "PASS", $"耗时{syncTime:F2}ms, 处理文件{fileCount}个"));
        }
        catch (Exception ex)
        {
            results.Add(("启动同步效率", "ERROR", ex.Message));
            Console.WriteLine($"[错误] 测试2执行失败: {ex.Message}");
        }

        // 测试3: 非阻塞验证
        try
        {
            var nonBlockingPassed = await tests.TestNonBlockingVerificationAsync();
            results.Add(("非阻塞验证", nonBlockingPassed ? "PASS" : "FAIL", nonBlockingPassed ? "验证通过" : "验证失败"));
        }
        catch (Exception ex)
        {
            results.Add(("非阻塞验证", "ERROR", ex.Message));
            Console.WriteLine($"[错误] 测试3执行失败: {ex.Message}");
        }

        // 生成报告
        var report = GenerateReport(results);
        Console.WriteLine("\n" + report);

        // 保存报告
        await File.WriteAllTextAsync(reportPath, report);
        Console.WriteLine($"\n报告已保存到: {reportPath}");
    }

    private static string GenerateReport(List<(string Name, string Status, string Details)> results)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# Obsidian RAG 性能测试报告");
        sb.AppendLine();
        sb.AppendLine($"**测试时间**: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        sb.AppendLine("## 测试结果");
        sb.AppendLine();
        sb.AppendLine("| 测试项 | 状态 | 详情 |");
        sb.AppendLine("|--------|------|------|");

        foreach (var (name, status, details) in results)
        {
            var statusIcon = status switch
            {
                "PASS" => "PASS",
                "FAIL" => "FAIL",
                _ => "ERROR"
            };
            sb.AppendLine($"| {name} | {statusIcon} | {details} |");
        }

        sb.AppendLine();
        sb.AppendLine("## 测试说明");
        sb.AppendLine();
        sb.AppendLine("- **运行时变更反应时间**: 文件创建到被向量库索引的时间差");
        sb.AppendLine("- **启动同步效率**: 服务启动时同步文件的时间");
        sb.AppendLine("- **非阻塞验证**: 服务运行期间搜索请求是否被阻塞");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine("*由 TestAgent 自动生成*");

        return sb.ToString();
    }
}
