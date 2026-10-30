using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Infrastructure.Features.FileSystem;
using Xunit;

namespace ReferenceRAG.Host.Tests;

public class FileSearchAndReaderTests : IDisposable
{
    private readonly string _testRoot;

    public FileSearchAndReaderTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "referencerag_search_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testRoot))
            {
                Directory.Delete(_testRoot, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public void ExactSubstringStrategy_MatchesCorrectly()
    {
        var strategy = new ExactSubstringStrategy("ApiKey", caseSensitive: false);
        var line = "public class apikeypermissionmatrix".AsSpan();

        var matched = strategy.MatchesLine(line, out var index, out var length);

        Assert.True(matched);
        Assert.Equal(13, index);
        Assert.Equal(6, length);
    }

    [Fact]
    public void ExactSubstringStrategy_CaseSensitiveHonored()
    {
        var sensitive = new ExactSubstringStrategy("ApiKey", caseSensitive: true);
        var insensitive = new ExactSubstringStrategy("ApiKey", caseSensitive: false);
        var line = "const apikey = 123;".AsSpan();

        Assert.False(sensitive.MatchesLine(line, out _, out _));
        Assert.True(insensitive.MatchesLine(line, out var idx, out var len));
        Assert.Equal(6, idx);
        Assert.Equal(6, len);
    }

    [Fact]
    public void NonBacktrackingRegexStrategy_LinearAndSafe()
    {
        var strategy = new NonBacktrackingRegexStrategy(@"TODO:\s*(?<task>.*)", caseSensitive: false);
        var line = "// todo: implement search engine".AsSpan();

        var matched = strategy.MatchesLine(line, out var index, out var length);

        Assert.True(matched);
        Assert.Equal(3, index);
    }

    [Fact]
    public void ContextHunkStateMachine_MergesNearbyMatchesWithoutDuplicateLines()
    {
        // contextLines = 1 (before = 1, after = 1)
        var fsm = new ContextHunkStateMachine(1);

        // Lines:
        // 1: intro
        // 2: header
        // 3: MATCH_1
        // 4: middle
        // 5: MATCH_2
        // 6: footer
        // 7: extra
        fsm.FeedLine(1, "intro", isMatch: false);
        fsm.FeedLine(2, "header", isMatch: false);
        fsm.FeedLine(3, "MATCH_1", isMatch: true, matchIndex: 0, matchLength: 7);
        fsm.FeedLine(4, "middle", isMatch: false);
        fsm.FeedLine(5, "MATCH_2", isMatch: true, matchIndex: 0, matchLength: 7);
        fsm.FeedLine(6, "footer", isMatch: false);
        fsm.FeedLine(7, "extra", isMatch: false);

        var hunks = fsm.Finish();

        // 预期合并为一个 Hunk，覆盖第 2 至第 6 行，无重复行
        Assert.Single(hunks);
        var hunk = hunks[0];
        Assert.Equal(2, hunk.StartLine);
        Assert.Equal(6, hunk.EndLine);
        Assert.Equal(5, hunk.Lines.Count);

        var lineNumbers = hunk.Lines.Select(l => l.LineNumber).ToList();
        Assert.Equal(new[] { 2, 3, 4, 5, 6 }, lineNumbers);

        Assert.True(hunk.Lines[1].IsMatch); // Line 3
        Assert.True(hunk.Lines[3].IsMatch); // Line 5
    }

    [Fact]
    public async Task ManagedFileSearchEngine_SkipsBinaryAndScansContent()
    {
        var sourceDir = Path.Combine(_testRoot, "notes");
        Directory.CreateDirectory(sourceDir);

        // 1. 纯文本文件（含目标关键词）
        var textFile = Path.Combine(sourceDir, "guide.md");
        await File.WriteAllTextAsync(textFile, """
            # ReferenceRAG 指南
            欢迎使用系统。
            重要配置: ApiKeyPermissionMatrix 是核心类。
            结束。
            """, Encoding.UTF8);

        // 2. 二进制文件（前 512 字节包含 \0，哪怕内部有关键词也必须跳过）
        var binFile = Path.Combine(sourceDir, "dump.bin");
        var binBytes = new byte[1024];
        binBytes[10] = 0; // NUL 字节
        var secret = Encoding.UTF8.GetBytes("ApiKeyPermissionMatrix");
        Array.Copy(secret, 0, binBytes, 20, secret.Length);
        await File.WriteAllBytesAsync(binFile, binBytes);

        // 3. 黑名单目录（.git）内部的文件必须被浅层剪枝跳过
        var gitDir = Path.Combine(sourceDir, ".git");
        Directory.CreateDirectory(gitDir);
        await File.WriteAllTextAsync(Path.Combine(gitDir, "ignored.txt"), "ApiKeyPermissionMatrix in git", Encoding.UTF8);

        // 构造配置
        var cfg = new ObsidianRagConfig();
        cfg.Sources.Add(new SourceFolder { Name = "notes", Path = sourceDir, Enabled = true });
        var configMgr = new ConfigManager(initialConfig: cfg, customConfigPath: Path.Combine(_testRoot, "config.json"));
        configMgr.Save(cfg);

        var engine = new ManagedFileSearchEngine(configMgr);

        var query = new FileSearchQuery
        {
            Pattern = "ApiKeyPermissionMatrix",
            IsRegex = false,
            CaseSensitive = false,
            ContextLines = 1,
            MaxMatches = 50
        };

        var result = await engine.SearchAsync(query);

        Assert.True(result.Success);
        Assert.Equal(1, result.TotalMatches);
        Assert.Single(result.Files);

        var matchGroup = result.Files[0];
        Assert.Equal("notes", matchGroup.SourceName);
        Assert.Equal("guide.md", matchGroup.RelativePath);
        Assert.Single(matchGroup.Hunks);

        var matchLine = matchGroup.Hunks[0].Lines.FirstOrDefault(l => l.IsMatch);
        Assert.NotNull(matchLine);
        Assert.Equal(3, matchLine.LineNumber);
        Assert.Contains("ApiKeyPermissionMatrix", matchLine.Text);
    }

    [Fact]
    public async Task FileReaderService_IgnoresHeadersInsideCodeBlocks()
    {
        var mdFile = Path.Combine(_testRoot, "doc.md");
        await File.WriteAllTextAsync(mdFile, """
            ---
            title: "架构设计"
            author: "agent"
            ---

            # 第一章 概述

            这是概述内容。

            ```csharp
            // # 这里是代码块注释，绝不能误判为 H1 标题
            var a = 1;
            ```

            ## 1.1 细节剖析
            细节描述。
            """, Encoding.UTF8);

        var service = new FileReaderService();

        var meta = await service.GetMetadataAsync(mdFile);

        Assert.True(meta.IsMarkdown);
        Assert.Equal("架构设计", meta.Frontmatter["title"]);
        Assert.Equal("agent", meta.Frontmatter["author"]);

        // 标题应该只有两个：# 第一章 概述 (H1) 和 ## 1.1 细节剖析 (H2)
        Assert.Equal(2, meta.Headings.Count);
        Assert.Equal("第一章 概述", meta.Headings[0].Title);
        Assert.Equal(1, meta.Headings[0].Level);

        Assert.Equal("1.1 细节剖析", meta.Headings[1].Title);
        Assert.Equal(2, meta.Headings[1].Level);

        // 测试行范围读取
        var range = await service.ReadLineRangeAsync(mdFile, startLine: 6, endLine: 8);
        Assert.Equal(6, range.StartLine);
        Assert.Equal(8, range.EndLine);
        Assert.Equal(3, range.ReturnedLines);
        Assert.Contains("# 第一章 概述", range.Content);
    }
}
