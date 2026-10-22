using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReferenceRAG.ApiHost.Features.Logging.Models;
using ReferenceRAG.ApiHost.Features.Logging.Services;
using ReferenceRAG.ApiHost.Features.Logging.Strategies;
using Xunit;

namespace ReferenceRAG.Host.Tests;

public sealed class LoggingServiceTests : IDisposable
{
    private readonly string _tempLogDir;

    public LoggingServiceTests()
    {
        _tempLogDir = Path.Combine(Path.GetTempPath(), "rag-log-unit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempLogDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempLogDir))
            {
                Directory.Delete(_tempLogDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup failure
        }
    }

    [Theory]
    [InlineData("app_20261022.log", true)]
    [InlineData("reference-rag-2026-10-22.log", true)]
    [InlineData("debug.txt", true)]
    [InlineData("log.1.txt", true)]
    [InlineData("../secret.txt", false)]
    [InlineData("../../etc/passwd", false)]
    [InlineData("..\\..\\windows\\win.ini", false)]
    [InlineData("sub/folder/app.log", false)]
    [InlineData("sub\\folder\\app.log", false)]
    [InlineData("malicious.exe", false)]
    [InlineData("script.sh", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void ValidateFileName_GuardsAgainstPathTraversalAndInvalidExtensions(string? fileName, bool expectedValid)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new[] { new System.Collections.Generic.KeyValuePair<string, string?>("LogsPath", _tempLogDir) })
            .Build();

        var service = new LogFileService(config, NullLogger<LogFileService>.Instance);

        var isValid = service.ValidateFileName(fileName, out var safePath);

        Assert.Equal(expectedValid, isValid);
        if (expectedValid)
        {
            Assert.StartsWith(_tempLogDir, safePath, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.Empty(safePath);
        }
    }

    [Fact]
    public async Task FastReverseSeekTailStrategy_ReturnsExactTailLines()
    {
        var filePath = Path.Combine(_tempLogDir, "app_test_reverse.log");
        var lines = Enumerable.Range(1, 10).Select(i => $"Line {i:D2} log message content").ToArray();
        await File.WriteAllLinesAsync(filePath, lines, Encoding.UTF8);

        var strategy = new FastReverseSeekTailStrategy();
        var (content, isTruncated) = await strategy.ReadTailAsync(filePath, 3);

        Assert.False(isTruncated);
        var returnedLines = content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, returnedLines.Length);
        Assert.Equal("Line 08 log message content", returnedLines[0]);
        Assert.Equal("Line 09 log message content", returnedLines[1]);
        Assert.Equal("Line 10 log message content", returnedLines[2]);
    }

    [Fact]
    public async Task FastReverseSeekTailStrategy_WhenRequestedLinesExceedTotal_ReturnsAllLines()
    {
        var filePath = Path.Combine(_tempLogDir, "app_test_all.log");
        var lines = new[] { "Alpha", "Beta", "Gamma" };
        await File.WriteAllLinesAsync(filePath, lines, Encoding.UTF8);

        var strategy = new FastReverseSeekTailStrategy();
        var (content, isTruncated) = await strategy.ReadTailAsync(filePath, 50);

        Assert.False(isTruncated);
        var returnedLines = content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, returnedLines.Length);
        Assert.Equal("Alpha", returnedLines[0]);
        Assert.Equal("Beta", returnedLines[1]);
        Assert.Equal("Gamma", returnedLines[2]);
    }

    [Fact]
    public async Task FastReverseSeekTailStrategy_EmptyFile_ReturnsEmpty()
    {
        var filePath = Path.Combine(_tempLogDir, "app_empty.log");
        await File.WriteAllBytesAsync(filePath, Array.Empty<byte>());

        var strategy = new FastReverseSeekTailStrategy();
        var (content, isTruncated) = await strategy.ReadTailAsync(filePath, 100);

        Assert.Empty(content);
        Assert.False(isTruncated);
    }

    [Fact]
    public async Task LogFileService_GetLogFiles_ReturnsSortedListWithActiveFlag()
    {
        var file1 = Path.Combine(_tempLogDir, "app_old.log");
        var file2 = Path.Combine(_tempLogDir, "app_today.log");

        await File.WriteAllTextAsync(file1, "Old log");
        await File.WriteAllTextAsync(file2, "Today log");

        File.SetLastWriteTime(file1, DateTime.Today.AddDays(-2));
        File.SetLastWriteTime(file2, DateTime.Now);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new[] { new System.Collections.Generic.KeyValuePair<string, string?>("LogsPath", _tempLogDir) })
            .Build();

        var service = new LogFileService(config, NullLogger<LogFileService>.Instance);
        var files = service.GetLogFiles();

        Assert.Equal(2, files.Count);
        Assert.Equal("app_today.log", files[0].Name);
        Assert.True(files[0].IsActiveToday);
        Assert.Equal("app_old.log", files[1].Name);
        Assert.False(files[1].IsActiveToday);
    }

    [Fact]
    public async Task OperationLogStore_RecordAndQueryWithPagingAndFiltering()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new[] { new System.Collections.Generic.KeyValuePair<string, string?>("Work:Directory", _tempLogDir) })
            .Build();

        var serviceProvider = new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider();
        var store = new OperationLogStore(config, serviceProvider, NullLogger<OperationLogStore>.Instance);

        await store.RecordAsync(new OperationLogEntry
        {
            Action = "RebuildIndex",
            TargetType = "Source",
            TargetName = "MainDocs",
            Detail = "全量重建向量索引",
            ClientIp = "127.0.0.1",
            Success = true
        });

        await store.RecordAsync(new OperationLogEntry
        {
            Action = "SwitchModel",
            TargetType = "Model",
            TargetName = "bge-m3",
            Detail = "切换当前默认嵌入模型",
            ClientIp = "192.168.1.50",
            Success = true
        });

        await store.RecordAsync(new OperationLogEntry
        {
            Action = "SaveSettings",
            TargetType = "Settings",
            TargetName = "SystemConfig",
            Detail = "更新系统日志级别与加速参数",
            ClientIp = "127.0.0.1",
            Success = false
        });

        // 1. 无过滤查询全部
        var all = await store.QueryAsync(new OperationLogQuery { Page = 1, PageSize = 10 });
        Assert.Equal(3, all.Total);
        Assert.Equal(3, all.Items.Count);

        // 2. 关键字查询 "bge"
        var bgeQuery = await store.QueryAsync(new OperationLogQuery { Keyword = "bge" });
        Assert.Equal(1, bgeQuery.Total);
        Assert.Equal("SwitchModel", bgeQuery.Items[0].Action);

        // 3. 动作过滤 "RebuildIndex"
        var actionQuery = await store.QueryAsync(new OperationLogQuery { Action = "RebuildIndex" });
        Assert.Equal(1, actionQuery.Total);
        Assert.Equal("MainDocs", actionQuery.Items[0].TargetName);

        // 4. 分页测试 (pageSize = 2)
        var page1 = await store.QueryAsync(new OperationLogQuery { Page = 1, PageSize = 2 });
        Assert.Equal(3, page1.Total);
        Assert.Equal(2, page1.Items.Count);

        var page2 = await store.QueryAsync(new OperationLogQuery { Page = 2, PageSize = 2 });
        Assert.Equal(3, page2.Total);
        Assert.Single(page2.Items);
    }
}
