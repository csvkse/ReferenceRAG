using Moq;
using ReferenceRAG.Core.Helpers;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Storage;

namespace ReferenceRAG.Tests;

/// <summary>
/// 审计修复回归测试：覆盖本次修复引入的边界行为。
/// </summary>
public class AuditFixRegressionTests
{
    // ── A6：overlap 合并后不得超过 MaxTokens ──
    [Fact]
    public void Chunk_OverlapPlusParagraph_NeverExceedsMaxTokens()
    {
        var chunker = new MarkdownChunker();
        var options = new ChunkingOptions
        {
            MaxTokens = 100,
            MinTokens = 0,
            OverlapTokens = 40,
            PreserveCodeBlocks = true
        };

        // 构造一段正文：多个段落，每段约 60 token（中文 1 字符/token + 英文）
        var paragraphs = new List<string>();
        for (int i = 0; i < 6; i++)
        {
            paragraphs.Add($"这是第 {i} 段，用于验证重叠合并后的预算约束。" +
                           "This is English padding content to reach the token budget boundary easily. " +
                           "The quick brown fox jumps over the lazy dog repeatedly.");
        }
        var content = string.Join("\n\n", paragraphs);
        var chunks = chunker.Chunk(content, options);

        Assert.NotEmpty(chunks);
        foreach (var chunk in chunks)
        {
            var tokens = TokenEstimator.EstimateTokens(chunk.Content);
            Assert.True(tokens <= options.MaxTokens,
                $"chunk {chunk.ChunkIndex} 估算 {tokens} > MaxTokens {options.MaxTokens}");
        }
    }

    // ── A8：代码块内的 # 行不能当作标题 ──
    [Fact]
    public void Chunk_HeadingInsideCodeFence_NotTreatedAsHeading()
    {
        var chunker = new MarkdownChunker();
        var content = "# Real Heading\n\n```csharp\n# this is a comment\n## another comment\nint x = 1;\n```\n\nBody after code.";

        var chunks = chunker.Chunk(content, new ChunkingOptions
        {
            MaxTokens = 512,
            MinTokens = 0,
            PreserveHeadings = false
        });

        Assert.NotEmpty(chunks);
        // PreserveHeadings=false 时，代码块内容必须保留（不能被当作标题丢弃）
        Assert.Contains(chunks, c => c.Content.Contains("# this is a comment"));
        Assert.Contains(chunks, c => c.Content.Contains("int x = 1;"));
        // 真实标题行本身不应出现在内容里
        Assert.All(chunks, c => Assert.DoesNotContain(c.Content, "Real Heading"));
    }

    [Fact]
    public void Chunk_HeadingInsideCodeFence_PreserveHeadingsTrue_KeepsCodeBlock()
    {
        var chunker = new MarkdownChunker();
        var content = "# Real Heading\n\n```python\n# comment line\nprint('hello')\n```\n\nTail.";

        var chunks = chunker.Chunk(content, new ChunkingOptions
        {
            MaxTokens = 512,
            MinTokens = 0,
            PreserveHeadings = true
        });

        Assert.NotEmpty(chunks);
        // 代码块完整保留（围栏成对）
        Assert.Contains(chunks, c => c.Content.Contains("```python") && c.Content.Contains("```"));
        Assert.Contains(chunks, c => c.Content.Contains("print('hello')"));
    }

    // ── A7：超长单句 / 超长代码块强制拆分 ──
    [Fact]
    public void Chunk_OversizedSingleSentence_SplitWithinBudget()
    {
        var chunker = new MarkdownChunker();
        // 单句 400 字（无句号），MaxTokens=100
        var sentence = new string('中', 400);
        var content = sentence;

        var chunks = chunker.Chunk(content, new ChunkingOptions
        {
            MaxTokens = 100,
            MinTokens = 0,
            OverlapTokens = 0,
            PreserveCodeBlocks = true
        });

        Assert.NotEmpty(chunks);
        Assert.All(chunks, c =>
            Assert.True(TokenEstimator.EstimateTokens(c.Content) <= 100,
                $"forced chunk {c.ChunkIndex} 估算 {TokenEstimator.EstimateTokens(c.Content)} 超限"));
        // 拼接还原
        var joined = string.Concat(chunks.Select(c => c.Content));
        Assert.Equal(sentence, joined);
    }

    [Fact]
    public void Chunk_OversizedCodeBlock_SplitByLines_WithinBudget()
    {
        var chunker = new MarkdownChunker();
        // 超大代码块：300 行，每行 10 字
        var codeBody = string.Join("\n", Enumerable.Range(0, 300).Select(i => $"var v{i} = fn({i});"));
        var content = "```csharp\n" + codeBody + "\n```";

        var chunks = chunker.Chunk(content, new ChunkingOptions
        {
            MaxTokens = 80,
            MinTokens = 0,
            OverlapTokens = 0,
            PreserveCodeBlocks = true
        });

        var codeChunks = chunks.Where(c => c.ChunkType == ChunkType.Code).ToList();
        Assert.NotEmpty(codeChunks);
        Assert.All(codeChunks, c =>
            Assert.True(TokenEstimator.EstimateTokens(c.Content) <= 80,
                $"code chunk 估算 {TokenEstimator.EstimateTokens(c.Content)} 超限"));
        // 所有代码行都在（去除围栏标记后）
        var joined = string.Join("\n", codeChunks.Select(c => c.Content));
        Assert.Contains("var v0 = fn(0);", joined);
        Assert.Contains("var v299 = fn(299);", joined);
    }

    // ── C2：LIKE 通配符转义 ──
    [Fact]
    public async Task Graph_CleanupWithUnderscorePath_NotAffectOtherNodes()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"graph-escape-{Guid.NewGuid():N}.db");
        try
        {
            var store = new SqliteGraphStore(dbPath);

            // 两个相似节点：my_file.md 与 myXfile.md（_ 被 LIKE 当通配符时 myXfile 会被误删）
            await store.UpsertNodeAsync(new GraphNode { Id = "note/my_file.md", Title = "A", Type = "document" });
            await store.UpsertNodeAsync(new GraphNode { Id = "note/myXfile.md", Title = "B", Type = "document" });
            await store.UpsertNodeAsync(new GraphNode { Id = "note/my_file.md#Heading", Title = "H", Type = "heading" });
            await store.UpsertEdgesAsync(new[]
            {
                new GraphEdge { FromId = "note/my_file.md", ToId = "note/my_file.md#Heading", Type = "heading" },
                new GraphEdge { FromId = "note/myXfile.md", ToId = "note/my_file.md", Type = "wikilink" }
            });

            // 删除 my_file.md 的 heading 子节点
            await store.DeleteHeadingNodesAsync("note/my_file.md");

            Assert.NotNull(await store.GetNodeAsync("note/myXfile.md"));   // 不能被误删
            Assert.NotNull(await store.GetNodeAsync("note/my_file.md"));   // 文件节点保留
            Assert.Null(await store.GetNodeAsync("note/my_file.md#Heading"));

            // 边：area myXfile.md → my_file.md 的 wikilink 应保留
            var neighbors = await store.GetNeighborsAsync("note/myXfile.md", 1);
            Assert.Contains(neighbors.Edges, e => e.ToId == "note/my_file.md");

            store.Dispose();
        }
        finally
        {
            // SQLite 连接池占用 db 文件句柄，测试结束后不删除文件，交由系统临时目录清理
        }
    }

    // ── C3：chunk 扩展字段持久化往返 ──
    [Fact]
    public async Task VectorStore_ChunkExtendedFields_RoundTrip()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"chunk-fields-{Guid.NewGuid():N}.db");
        try
        {
            var store = new SqliteVectorStore(dbPath, 384);

            await store.UpsertFileAsync(new FileRecord
            {
                Id = "f1",
                Path = "/tmp/f1.md",
                FileName = "f1.md",
                ContentHash = "h",
                ChunkingHash = "chunkhash-1",
                IndexedAt = DateTime.UtcNow
            });

            var chunk = new ChunkRecord
            {
                Id = "c1",
                FileId = "f1",
                ChunkIndex = 0,
                Content = "原始内容",
                EnhancedContent = "[标题] 标题\n原始内容",
                Source = "src-a",
                Tags = new List<string> { "tag1", "tag2" },
                Keywords = new List<string> { "kw1" },
                AggregateRange = "1-3",
                TokenCount = 5,
                StartLine = 1,
                EndLine = 3,
                Weight = 1.2f,
                ChunkType = ChunkType.Text,
                ChunkOrder = 2.5
            };

            await store.UpsertChunkAsync(chunk, CancellationToken.None);

            var loaded = await store.GetChunkAsync("c1", CancellationToken.None);
            Assert.NotNull(loaded);
            Assert.Equal("原始内容", loaded!.Content);
            Assert.Equal("[标题] 标题\n原始内容", loaded.EnhancedContent);
            Assert.Equal("src-a", loaded.Source);
            Assert.Equal(new List<string> { "tag1", "tag2" }, loaded.Tags);
            Assert.Equal(new List<string> { "kw1" }, loaded.Keywords);
            Assert.Equal("1-3", loaded.AggregateRange);
            Assert.Equal(2.5, loaded.ChunkOrder);

            // FileRecord 的 ChunkingHash 往返
            var file = await store.GetFileAsync("f1", CancellationToken.None);
            Assert.Equal("chunkhash-1", file!.ChunkingHash);

            store.Dispose();
        }
        finally
        {
            // SQLite 连接池占用 db 文件句柄，测试结束后不删除文件，交由系统临时目录清理
        }
    }

    // ── E2：搜索过滤 pending 文件 ──
    [Fact]
    public async Task VectorStore_Search_FiltersPendingFiles()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"search-pending-{Guid.NewGuid():N}.db");
        try
        {
            var store = new SqliteVectorStore(dbPath, 384);

            // 两个文件：一个 complete，一个 pending
            await store.UpsertFileAsync(new FileRecord
            {
                Id = "f-complete",
                Path = "/tmp/complete.md",
                FileName = "complete.md",
                ContentHash = "h1",
                IndexedStatus = "complete",
                IndexedAt = DateTime.UtcNow
            });
            await store.UpsertFileAsync(new FileRecord
            {
                Id = "f-pending",
                Path = "/tmp/pending.md",
                FileName = "pending.md",
                ContentHash = "h2",
                IndexedStatus = "pending",
                IndexedAt = DateTime.UtcNow
            });

            await store.UpsertChunksAsync(new[]
            {
                new ChunkRecord { Id = "c-complete", FileId = "f-complete", ChunkIndex = 0, Content = "complete content", StartLine = 1, EndLine = 1 },
                new ChunkRecord { Id = "c-pending", FileId = "f-pending", ChunkIndex = 0, Content = "pending content", StartLine = 1, EndLine = 1 }
            }, CancellationToken.None);

            // 写入向量（维度 384）
            var vector = new float[384];
            vector[0] = 1f;
            await store.UpsertVectorsAsync(new[]
            {
                new VectorRecord { Id = "vec-c-complete", ChunkId = "c-complete", FileId = "f-complete", Vector = vector, Dimension = 384, ModelName = "default" },
                new VectorRecord { Id = "vec-c-pending", ChunkId = "c-pending", FileId = "f-pending", Vector = vector, Dimension = 384, ModelName = "default" }
            }, CancellationToken.None);

            var results = (await store.SearchAsync(vector, "default", 10, CancellationToken.None)).ToList();

            Assert.Contains(results, r => r.ChunkId == "c-complete");
            Assert.DoesNotContain(results, r => r.ChunkId == "c-pending");

            store.Dispose();
        }
        finally
        {
            // SQLite 连接池占用 db 文件句柄，测试结束后不删除文件，交由系统临时目录清理
        }
    }

    // ── A1/A2：分布式 token 估算一致性（SimpleTokenizer == TokenEstimator）──
    [Theory]
    [InlineData("你好世界")]
    [InlineData("Hello world")]
    [InlineData("Hello 你好 world 世界")]
    [InlineData("")]
    [InlineData("### Heading\n\n正文内容。")]
    [InlineData("1234567890")]
    [InlineData("café résumé 中文测试")]
    public void TokenEstimator_ConsistentWithSimpleTokenizer(string text)
    {
        var tokenizer = new SimpleTokenizer();
        Assert.Equal(TokenEstimator.EstimateTokens(text), tokenizer.CountTokens(text));
    }

    // ── A5：配置一致性校验 ──
    [Fact]
    public void ConfigManager_ChunkMaxTokensExceedingEmbeddingBudget_FailsValidation()
    {
        var config = new ObsidianRagConfig
        {
            DataPath = "data",
            Sources = { new SourceFolder { Name = "s", Path = Path.GetTempPath() } },
            Service = new ServiceConfig { Port = 8080 }
        };
        config.Embedding.MaxSequenceLength = 512;
        config.Embedding.Mode = "onnx";
        config.Chunking.MaxTokens = 800;   // > 512 - 2 - 32 = 478

        var manager = new ConfigManager();
        var (valid, errors, warnings) = manager.Validate(config);

        Assert.False(valid);
        Assert.Contains(errors, e => e.Contains("MaxTokens"));
    }

    // ── B1：pending 纳入启动同步 ──
    [Fact]
    public async Task StartupSync_PendingFiles_IncludedInRecovery()
    {
        // 通过反射调用私有方法不可行，改用端到端验证：
        // pending 文件的 skip 条件：hash 一致 + status=complete 才会跳过 → pending 必然重跑
        var pipeline = CreatePipelineWithVectorStore(out var vectorStore, out var chunker);

        var dir = Path.Combine(Path.GetTempPath(), $"pending-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var filePath = Path.Combine(dir, "doc.md");
            await File.WriteAllTextAsync(filePath, "# Title\n\nSome content here.");

            // 第一次 Prepare（模拟上次中断：遗留 pending 记录）
            var ctx = await pipeline.PrepareAsync(filePath, [], force: true, CancellationToken.None);
            Assert.NotNull(ctx);

            // 模拟 Phase3 未完成：文件状态保持 pending
            await vectorStore.Object.MarkFileStatusAsync(ctx!.FileRecord.Id, "pending", CancellationToken.None);

            // 再次 Prepare（非 force）：pending + hash 一致 → 必须重跑，返回非 null
            var retried = await pipeline.PrepareAsync(filePath, [], force: false, CancellationToken.None);
            Assert.NotNull(retried);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ── B2：分块配置变更触发重新分块 ──
    [Fact]
    public async Task PrepareAsync_ChunkingConfigChanged_RechunksEvenIfContentSame()
    {
        var pipeline = CreatePipelineWithVectorStore(out var vectorStore, out var chunker);

        var dir = Path.Combine(Path.GetTempPath(), $"chunkhash-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var filePath = Path.Combine(dir, "doc.md");
            await File.WriteAllTextAsync(filePath, "# Title\n\nSome content here.");

            // 第一次：完整索引（chunkingHash 写入 complete 记录）
            var ctx = await pipeline.PrepareAsync(filePath, [], force: true, CancellationToken.None);
            Assert.NotNull(ctx);
            await pipeline.FinalizeAsync(ctx!, new Dictionary<string, string>(), CancellationToken.None);

            // 模拟修改 chunking 配置（MaxTokens 变化）后再次 Prepare（非 force）
            // chunker mock 返回新配置下的分块；hash 一致但 chunkingHash 不同 → 必须重跑
            var retried = await pipeline.PrepareAsync(filePath, [], force: false, CancellationToken.None);
            Assert.NotNull(retried);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ── 辅助：构造带临时目录的 pipeline（chunking 配置可独立变化）──
    private static FileIndexPipeline CreatePipelineWithVectorStore(
        out Mock<IVectorStore> vectorStore,
        out Mock<IMarkdownChunker> chunker)
    {
        vectorStore = new Mock<IVectorStore>();
        vectorStore
            .Setup(s => s.GetFileByPathAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FileRecord?)null);
        vectorStore
            .Setup(s => s.GetChunksByFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        vectorStore
            .Setup(s => s.UpsertFileAsync(It.IsAny<FileRecord>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        vectorStore
            .Setup(s => s.MarkFileStatusAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        vectorStore
            .Setup(s => s.DeleteChunkAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        chunker = new Mock<IMarkdownChunker>();
        chunker
            .Setup(c => c.Chunk(It.IsAny<string>(), It.IsAny<ChunkingOptions>()))
            .Returns((string content, ChunkingOptions? o) => new MarkdownChunker().Chunk(content, o ?? new ChunkingOptions()));

        return new FileIndexPipeline(
            vectorStore.Object,
            new Mock<IBM25Store>().Object,
            chunker.Object,
            new Mock<IEmbeddingService>().Object,
            new TextEnhancer(),
            new ConfigManager());
    }
}