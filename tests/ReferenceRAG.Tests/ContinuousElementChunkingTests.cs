using ReferenceRAG.Core.Helpers;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;

namespace ReferenceRAG.Tests;

public class ContinuousElementChunkingTests
{
    private readonly MarkdownChunker _chunker;

    public ContinuousElementChunkingTests()
    {
        _chunker = new MarkdownChunker();
    }

    [Fact]
    public void Chunk_OversizedTable_SplitsWithHeaderCarryoverAndZeroRowOverlap()
    {
        // 构建 60 行超长表格，预期远超 80 tokens 的预算
        var header = "| 用户ID | 姓名 | 职务角色 | 权限范围 | 状态 |\n|:---|:---|:---|:---|:---:|";
        var dataRows = Enumerable.Range(1, 60)
            .Select(i => $"| {1000 + i} | 用户{i} | 架构专家{i} | 系统全域管理与核心审计 | 正常 |")
            .ToList();

        var tableContent = header + "\n" + string.Join("\n", dataRows);
        var markdown = "# 系统权限清单\n\n" + tableContent + "\n\n正文结尾说明。";

        var options = new ChunkingOptions
        {
            MaxTokens = 90,
            MinTokens = 10,
            OverlapTokens = 20,
            PreserveTables = true,
            PreserveHeadings = true
        };

        var chunks = _chunker.Chunk(markdown, options);

        Assert.NotEmpty(chunks);

        var tableChunks = chunks.Where(c => c.ChunkType == ChunkType.Table).ToList();
        Assert.True(tableChunks.Count >= 3, $"预期至少切分为 3 个表格切片，实际切分: {tableChunks.Count}");

        // 断言 1: 每一个表格切片都必须拥有完整的表头和分隔线（表头传承 Header Carryover）
        foreach (var chunk in tableChunks)
        {
            Assert.Contains("| 用户ID | 姓名 | 职务角色 | 权限范围 | 状态 |", chunk.Content);
            Assert.Contains("|:---|:---|:---|:---|:---:|", chunk.Content);
        }

        // 断言 2: 表格数据行零重叠（Zero Row Overlap），任何数据行只出现在一个切片中
        var seenRows = new HashSet<string>();
        foreach (var chunk in tableChunks)
        {
            var lines = chunk.Content.Split('\n');
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("| 10")) // 数据行特征
                {
                    Assert.DoesNotContain(trimmed, seenRows);
                    seenRows.Add(trimmed);
                }
            }
        }

        // 所有 60 行数据必须全部被涵盖，既无丢失也无重复
        Assert.Equal(60, seenRows.Count);
    }

    [Fact]
    public void Chunk_TableWithPrecedingContext_CarriesPrecedingContextToSubsequentChunks()
    {
        var intro = "*表 1-1：2026 年度系统权限矩阵*";
        var header = "| 角色 | 操作权限 |\n|:---|:---|";
        var dataRows = Enumerable.Range(1, 40)
            .Select(i => $"| 角色_{i} | 允许执行第_{i}_号高危运维指令与发布操作 |")
            .ToList();

        var markdown = $"{intro}\n{header}\n{string.Join("\n", dataRows)}";

        var options = new ChunkingOptions
        {
            MaxTokens = 70,
            MinTokens = 10,
            PreserveTables = true
        };

        var chunks = _chunker.Chunk(markdown, options);
        var tableChunks = chunks.Where(c => c.ChunkType == ChunkType.Table).ToList();

        Assert.True(tableChunks.Count >= 2);

        // 首块应包含原始导语
        Assert.Contains(intro, tableChunks[0].Content);

        // 后续块应包含传承导语带 (续) 标记
        Assert.Contains($"{intro} (续)", tableChunks[1].Content);
    }

    [Fact]
    public void Chunk_NormalTableWithinMaxTokens_EmittedAsSingleTableChunk()
    {
        var markdown = "# 配置列表\n\n| 参数 | 默认值 | 说明 |\n|---|---|---|\n| port | 8080 | 监听端口 |\n| timeout | 30 | 超时时间 |\n\n后续正文。";

        var options = new ChunkingOptions
        {
            MaxTokens = 200,
            PreserveTables = true
        };

        var chunks = _chunker.Chunk(markdown, options);

        var tableChunk = chunks.FirstOrDefault(c => c.ChunkType == ChunkType.Table);
        Assert.NotNull(tableChunk);
        Assert.Contains("| port | 8080 | 监听端口 |", tableChunk.Content);
        Assert.Contains("| timeout | 30 | 超时时间 |", tableChunk.Content);
    }

    [Fact]
    public void Chunk_InlineBase64Image_ReplacedWithCleanPlaceholder()
    {
        // 模拟 1000 字符的内联 Base64 图片
        var fakeBase64 = "data:image/png;base64," + new string('A', 800) + "==";
        var markdown = $"# 图文文档\n\n请参见下图结构：\n\n![架构图]({fakeBase64})\n\n正文描述。";

        var chunks = _chunker.Chunk(markdown, new ChunkingOptions { MaxTokens = 200 });

        Assert.NotEmpty(chunks);

        // 断言原始 Base64 编码被替换为占位符，未泄露进切片
        Assert.DoesNotContain(new string('A', 100), string.Join("\n", chunks.Select(c => c.Content)));
        Assert.Contains(chunks, c => c.Content.Contains("[内联图片数据: 大小约"));
    }

    [Fact]
    public void Chunk_SurrogatePairText_DoesNotSplitSurrogatePair()
    {
        // 连续由 Emoji 构成的超长文本，触发强制单句切分
        var emojis = string.Concat(Enumerable.Repeat("🎉🚀💎🔥🌟🌈", 100)); // 包含多组 UTF-16 高低代理对
        var markdown = $"# 极端字符测试\n\n{emojis}";

        var chunks = _chunker.Chunk(markdown, new ChunkingOptions { MaxTokens = 30 });

        Assert.NotEmpty(chunks);

        // 验证每一个切片内部的字符都不存在单独孤立的高代理或低代理项
        foreach (var chunk in chunks)
        {
            var text = chunk.Content;
            for (int i = 0; i < text.Length; i++)
            {
                if (char.IsHighSurrogate(text[i]))
                {
                    Assert.True(i + 1 < text.Length, "高代理项后缺少字符！");
                    Assert.True(char.IsLowSurrogate(text[i + 1]), "高代理项后不是合法的低代理项！");
                    i++; // 跳过成对的低代理项
                }
                else
                {
                    Assert.False(char.IsLowSurrogate(text[i]), "发现孤立的低代理项！");
                }
            }
        }
    }
}
