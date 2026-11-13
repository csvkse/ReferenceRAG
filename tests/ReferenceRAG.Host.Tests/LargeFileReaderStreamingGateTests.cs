using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using ReferenceRAG.ApiHost.Controllers;
using ReferenceRAG.Infrastructure.Features.FileSystem;
using Xunit;

namespace ReferenceRAG.Host.Tests;

public sealed class LargeFileReaderStreamingGateTests : IDisposable
{
    private readonly string _testDir;

    public LargeFileReaderStreamingGateTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "referencerag_largefile_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, true);
            }
        }
        catch
        {
            // 忽略临时清理异常
        }
    }

    [Fact]
    public async Task FileReaderService_CanStreamWindow_FromLargeFile_WithO1Memory()
    {
        // 构造一个 50,000 行的大文本文件 (约 3MB)
        var filePath = Path.Combine(_testDir, "large_corpus.txt");
        await using (var sw = new StreamWriter(new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None), Encoding.UTF8))
        {
            for (int i = 1; i <= 50000; i++)
            {
                await sw.WriteLineAsync($"Line {i}: This is structured corpus content line for large file streaming benchmarks.");
            }
        }

        var readerService = new FileReaderService();

        // 验证任意深度的滑动窗口读取 (例如读取 25000 ~ 25199 行，共 200 行)
        var swWatch = System.Diagnostics.Stopwatch.StartNew();
        var result = await readerService.ReadLineRangeAsync(filePath, startLine: 25000, endLine: 25199);
        swWatch.Stop();

        Assert.Equal(200, result.ReturnedLines);
        Assert.Equal(25000, result.StartLine);
        Assert.Equal(25199, result.EndLine);
        Assert.True(result.HasMore);
        Assert.Equal(25200, result.NextStartLine);
        Assert.StartsWith("Line 25000:", result.Lines[0]);
        Assert.StartsWith("Line 25199:", result.Lines[^1]);

        // 微秒级断言：在本地流式快进跳行应极速完成 (< 1500ms)
        Assert.True(swWatch.ElapsedMilliseconds < 1500, $"Reading 200 lines from 50k lines took {swWatch.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task FileReaderService_GetMetadata_ExtractsOutlineAndTotalLines_Streamingly()
    {
        // 构造包含 TOC 标题、Frontmatter、代码块和 20,000 行文本的 Markdown 文件
        var filePath = Path.Combine(_testDir, "large_book.md");
        await using (var sw = new StreamWriter(new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None), Encoding.UTF8))
        {
            await sw.WriteLineAsync("---");
            await sw.WriteLineAsync("title: 大文件流式阅读专著");
            await sw.WriteLineAsync("author: 金庸测试集");
            await sw.WriteLineAsync("---");
            await sw.WriteLineAsync("# 第一章 风起云涌");
            for (int i = 1; i <= 5000; i++)
            {
                await sw.WriteLineAsync($"正文段落 {i}: 云海茫茫，剑气纵横。");
            }
            await sw.WriteLineAsync("```csharp");
            await sw.WriteLineAsync("# 这是代码块里的伪注释标题，不应该被识别为 TOC");
            await sw.WriteLineAsync("```");
            await sw.WriteLineAsync("## 第二节 剑拔弩张");
            for (int i = 1; i <= 8000; i++)
            {
                await sw.WriteLineAsync($"正文段落 {i}: 双方对峙，空气骤然凝聚。");
            }
            await sw.WriteLineAsync("### 第三目 尘埃落定");
            for (int i = 1; i <= 5000; i++)
            {
                await sw.WriteLineAsync($"正文段落 {i}: 终究归于平静。");
            }
        }

        var readerService = new FileReaderService();
        var meta = await readerService.GetMetadataAsync(filePath);

        Assert.True(meta.IsMarkdown);
        Assert.False(meta.IsBinary);
        Assert.True(meta.TotalLines > 18000, $"TotalLines was {meta.TotalLines}");
        Assert.True(meta.SizeBytes > 500 * 1024, $"SizeBytes was {meta.SizeBytes}");
        Assert.Equal("大文件流式阅读专著", meta.Frontmatter["title"]);

        // 断言识别出正确的 3 个层级大纲，且忽略代码块内部的 #
        Assert.Equal(3, meta.Headings.Count);
        Assert.Equal(1, meta.Headings[0].Level);
        Assert.Equal("第一章 风起云涌", meta.Headings[0].Title);
        Assert.Equal(2, meta.Headings[1].Level);
        Assert.Equal("第二节 剑拔弩张", meta.Headings[1].Title);
        Assert.Equal(3, meta.Headings[2].Level);
        Assert.Equal("第三目 尘埃落定", meta.Headings[2].Title);
    }

    [Fact]
    public async Task FileSystemController_ReadText_RelaxesTo5MB_AndRejectsAboveWithSuggestStream()
    {
        var controller = new FileSystemController(
            NullLogger<FileSystemController>.Instance,
            new FileReaderService(),
            null
        );

        // 1. 测试 3MB 文件 (符合放宽后的 5MB 限制，允许全量载入)
        var threeMbFile = Path.Combine(_testDir, "three_mb.txt");
        await using (var sw = new StreamWriter(new FileStream(threeMbFile, FileMode.Create, FileAccess.Write, FileShare.None), Encoding.UTF8))
        {
            var line = new string('A', 1000);
            for (int i = 0; i < 3000; i++)
            {
                await sw.WriteLineAsync(line);
            }
        }

        var threeMbResult = await controller.ReadText(threeMbFile);
        Assert.IsType<OkObjectResult>(threeMbResult);

        // 2. 测试 6MB 文件 (超过 5MB 限制，返回 isTooLarge 和 suggestStream)
        var sixMbFile = Path.Combine(_testDir, "six_mb.txt");
        await using (var sw = new StreamWriter(new FileStream(sixMbFile, FileMode.Create, FileAccess.Write, FileShare.None), Encoding.UTF8))
        {
            var line = new string('B', 1000);
            for (int i = 0; i < 6200; i++)
            {
                await sw.WriteLineAsync(line);
            }
        }

        var sixMbResult = await controller.ReadText(sixMbFile);
        var badReq = Assert.IsType<BadRequestObjectResult>(sixMbResult);
        Assert.NotNull(badReq.Value);

        var json = System.Text.Json.JsonSerializer.Serialize(badReq.Value);
        Assert.Contains("isTooLarge", json);
        Assert.Contains("suggestStream", json);
    }
}
