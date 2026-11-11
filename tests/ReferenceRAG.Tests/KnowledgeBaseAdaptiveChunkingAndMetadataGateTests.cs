using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Infrastructure.Features.KnowledgeBase.Storage;
using Xunit;

namespace ReferenceRAG.Tests;

/// <summary>
/// 目标 1 & 2 质量门禁：自适应分块、元数据全量暴露 (Locator/StartLine/EndLine/HeadingPath) 与 ChunkingSpec 独立配置
/// </summary>
public class KnowledgeBaseAdaptiveChunkingAndMetadataGateTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _catalogPath;
    private readonly CatalogStore _catalog;

    public KnowledgeBaseAdaptiveChunkingAndMetadataGateTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "RefRAG_ChunkMetaGate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _catalogPath = Path.Combine(_tempDir, "catalog.db");
        _catalog = new CatalogStore(_catalogPath);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public async Task Gate1_MetadataExpansion_RecallItem_ShouldSerializeLocatorAndLineRanges()
    {
        var item = new RecallItem(
            ChunkId: "chunk_001",
            DocumentId: "doc_001",
            SourceKey: "src/main.rs",
            Text: "fn main() { println!(\"Hello\"); }",
            Score: 0.95,
            ScoreKind: ScoreKind.BM25,
            DocumentVersion: 1,
            ConnectorId: "conn_git",
            Locator: "file:///repo/src/main.rs",
            StartLine: 10,
            EndLine: 25,
            HeadingPath: "Chapter 1 > Rust Entry"
        );

        var json = JsonSerializer.Serialize(item);

        // 验证 Locator 严禁包含 [JsonIgnore]，必须完整序列化
        Assert.Contains("\"Locator\":\"file:///repo/src/main.rs\"", json);
        Assert.Contains("\"StartLine\":10", json);
        Assert.Contains("\"EndLine\":25", json);
        // 验证反序列化对称性与 JSON 字段完整保留
        var deserialized = JsonSerializer.Deserialize<RecallItem>(json);
        Assert.NotNull(deserialized);
        Assert.Equal("file:///repo/src/main.rs", deserialized.Locator);
        Assert.Equal(10, deserialized.StartLine);
        Assert.Equal(25, deserialized.EndLine);
        Assert.Equal("Chapter 1 > Rust Entry", deserialized.HeadingPath);
    }

    [Fact]
    public async Task Gate2_SqliteKnowledgeBaseStore_ShouldPersistAndRecallExpandedMetadata()
    {
        var kbDbPath = Path.Combine(_tempDir, "kb_meta.db");
        var store = new SqliteKnowledgeBaseStore("kb_meta", kbDbPath);
        await store.InitializeAsync();

        var mdContent = @"# Title Section

First paragraph of information.

```csharp
public class AgenticWorker
{
    public void Work() => Console.WriteLine(""Running"");
}
```

Final closing summary.";

        var spec = new ChunkingSpec(
            Id: "spec_code_test",
            Mode: "Adaptive",
            StrategyType: "CodeFirst",
            MaxTokens: 256,
            MinTokens: 50,
            PreserveCodeBlocks: true
        );

        var doc = await store.CreateDocumentAsync(
            connectorId: "conn_docs",
            sourceKey: "agentic_guide.md",
            title: "Agentic Guide",
            content: mdContent,
            chunkingSpec: spec
        );

        Assert.NotNull(doc);
        Assert.True(doc.ChunkCount >= 1);

        // 检索并验证词法召回附带行号与章节
        var recall = await store.RecallAsync(new RecallRequest(
            Query: "AgenticWorker",
            TopK: 5,
            UseLexicalTokenizer: false
        ));

        Assert.NotEmpty(recall.Items);
        var topHit = recall.Items[0];
        Assert.Contains("AgenticWorker", topHit.Text);
        Assert.True(topHit.StartLine >= 1);
        Assert.True(topHit.EndLine >= topHit.StartLine);
        Assert.NotNull(topHit.Locator);
        Assert.Contains("manual://conn_docs", topHit.Locator);
    }

    [Fact]
    public async Task Gate3_AdaptiveChunking_CodeFirst_ShouldPreserveCodeBlockIntegrity()
    {
        var kbDbPath = Path.Combine(_tempDir, "kb_code.db");
        var store = new SqliteKnowledgeBaseStore("kb_code", kbDbPath);
        await store.InitializeAsync();

        var codeBlock = "```csharp\n" + string.Join("\n", System.Linq.Enumerable.Range(1, 20).Select(i => $"int line_{i} = {i};")) + "\n```";
        var fullDoc = "# Header\n\nIntro text\n\n" + codeBlock + "\n\nTrailing paragraph.";

        var codeSpec = new ChunkingSpec(
            Id: "spec_chunk_code_first",
            Mode: "Adaptive",
            StrategyType: "CodeFirst",
            MaxTokens: 512,
            MinTokens: 50,
            PreserveCodeBlocks: true
        );

        var doc = await store.CreateDocumentAsync("conn_code", "CodeDoc.cs", "CodeDoc", fullDoc, codeSpec);
        var (chunks, total) = await store.GetChunksAsync(doc.Id, 1, 50);

        Assert.True(total >= 1);
        // 代码块整体不被割裂
        var codeChunk = System.Linq.Enumerable.FirstOrDefault(chunks, c => c.Text.Contains("line_1") && c.Text.Contains("line_20"));
        Assert.NotNull(codeChunk);
        Assert.Contains("```csharp", codeChunk.Text);
        Assert.Contains("```", codeChunk.Text);
    }

    [Fact]
    public async Task Gate4_ChunkingSpec_CatalogCrud_ShouldOperateCorrectly()
    {
        await _catalog.InitializeAsync();

        // 默认预置规格应存在
        var defaultSpec = await _catalog.GetChunkingSpecAsync("spec_chunk_default");
        Assert.NotNull(defaultSpec);
        Assert.Equal("Adaptive", defaultSpec.Mode);
        Assert.Equal("Balanced", defaultSpec.StrategyType);

        var codeFirstSpec = await _catalog.GetChunkingSpecAsync("spec_chunk_code_first");
        Assert.NotNull(codeFirstSpec);
        Assert.True(codeFirstSpec.PreserveCodeBlocks);

        // 新增独立自定义规格
        var customSpec = new ChunkingSpec(
            Id: "spec_custom_kb_01",
            Mode: "Adaptive",
            StrategyType: "PrecisionDense",
            MaxTokens: 256,
            MinTokens: 32,
            OverlapTokens: 32,
            PreserveTables: true
        );

        await _catalog.SaveChunkingSpecAsync(customSpec);
        var retrieved = await _catalog.GetChunkingSpecAsync("spec_custom_kb_01");
        Assert.NotNull(retrieved);
        Assert.Equal(256, retrieved.MaxTokens);
        Assert.Equal("PrecisionDense", retrieved.StrategyType);

        var allSpecs = await _catalog.ListChunkingSpecsAsync();
        Assert.Contains(allSpecs, s => s.Id == "spec_custom_kb_01");

        // 删除
        var deleted = await _catalog.DeleteChunkingSpecAsync("spec_custom_kb_01");
        Assert.True(deleted);
        var nonExistent = await _catalog.GetChunkingSpecAsync("spec_custom_kb_01");
        Assert.Null(nonExistent);
    }
}
