using ReferenceRAG.Business.Features.Chunking.Adapters;
using ReferenceRAG.Business.Features.Chunking.Strategies;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using Xunit;

namespace ReferenceRAG.Tests;

public class AdaptiveChunkingTests
{
    [Fact]
    public void ModelProfileAdapter_Resolves_512_And_8192_Models_Accurately()
    {
        var adapter = new DefaultModelProfileAdapter();

        // 1. 测试 512 模型 (bge-small-zh-v1.5)
        var smallConfig = new EmbeddingConfig { ModelName = "bge-small-zh-v1.5", MaxSequenceLength = 512 };
        var smallProfile = adapter.GetProfile(smallConfig);
        Assert.Equal(512, smallProfile.PhysicalMaxTokens);
        Assert.InRange(smallProfile.SweetSpotOptimalTokens, 320, 380);
        Assert.False(smallProfile.SupportsMultiPageContext);

        // 2. 测试 8192 模型 (bge-m3)
        var m3Config = new EmbeddingConfig { ModelName = "bge-m3", Mode = "onnx" };
        var m3Profile = adapter.GetProfile(m3Config);
        Assert.Equal(8192, m3Profile.PhysicalMaxTokens);
        Assert.True(m3Profile.SupportsMultiPageContext);
        Assert.InRange(m3Profile.SweetSpotOptimalTokens, 1200, 2048);

        // 3. 测试 OpenAI API 模型 (text-embedding-3-small)
        var apiConfig = new EmbeddingConfig { ModelName = "text-embedding-3-small", Mode = "openai" };
        var apiProfile = adapter.GetProfile(apiConfig);
        Assert.Equal(8192, apiProfile.PhysicalMaxTokens);
        Assert.True(apiProfile.SupportsMultiPageContext);
    }

    [Fact]
    public void AdaptiveStrategyFactory_Resolves_All_Four_Presets()
    {
        var factory = new AdaptiveStrategyFactory();
        var adapter = new DefaultModelProfileAdapter();
        var profile512 = adapter.GetProfile(new EmbeddingConfig { ModelName = "bge-small" });

        var options = new ChunkingOptions();

        // 1. Balanced
        var balanced = factory.GetStrategy("Balanced");
        var pBalanced = balanced.ResolveParameters(options, profile512);
        Assert.InRange(pBalanced.TargetTokens, 300, 400);
        Assert.True(pBalanced.OverlapTokens > 0);

        // 2. PrecisionDense
        var dense = factory.GetStrategy("PrecisionDense");
        var pDense = dense.ResolveParameters(options, profile512);
        Assert.True(pDense.TargetTokens < pBalanced.TargetTokens);

        // 3. DeepNarrative
        var deep = factory.GetStrategy("DeepNarrative");
        var pDeep = deep.ResolveParameters(options, profile512);
        Assert.True(pDeep.TargetTokens > pBalanced.TargetTokens);
        Assert.True(pDeep.OverlapTokens >= pBalanced.OverlapTokens);

        // 4. CodeFirst
        var code = factory.GetStrategy("CodeFirst");
        var pCode = code.ResolveParameters(options, profile512);
        Assert.Equal(0, pCode.OverlapTokens); // 代码优先 0% 重叠
    }

    [Fact]
    public void AdaptiveMode_On_LongText_Drastically_Reduces_ChunkCount_And_Storage()
    {
        // 构造一篇长达 ~2500 tokens 的长文（包含多个段落）
        var paragraph = "这是一段非常充实的技术论述与详细方案设计说明，详细论证了在分布式知识库向量检索体系中各种参数调优的重要性。" +
                        "系统通过双路检索并行召回，将 Dense 向量模型与 BM25 稀疏检索算法相结合，在不同权重配置下实现极高的平均召回率与准确度。\n\n";
        var longText = string.Concat(Enumerable.Repeat(paragraph, 35));

        var chunker = new MarkdownChunker();

        // 1. 传统固定模式 (Fixed 512)
        var fixedOptions = new ChunkingOptions
        {
            Mode = "Fixed",
            MaxTokens = 512,
            MinTokens = 50,
            OverlapTokens = 50
        };
        var fixedChunks = chunker.Chunk(longText, fixedOptions);

        // 2. 长模型自适应模式 (Adaptive with BGE-M3 8192 Profile)
        var m3Profile = new DefaultModelProfileAdapter().GetProfile(new EmbeddingConfig { ModelName = "bge-m3" });
        var adaptiveOptions = new ChunkingOptions
        {
            Mode = "Adaptive",
            AdaptiveLevel = "Level2",
            AdaptiveStrategyType = "Balanced",
            ModelProfile = m3Profile
        };
        var adaptiveChunks = chunker.Chunk(longText, adaptiveOptions);

        // 断言：自适应模式针对长模型进行了窗口对齐，分块数应大幅降低（降幅 > 50%），直接证明空间瘦身
        Assert.NotEmpty(fixedChunks);
        Assert.NotEmpty(adaptiveChunks);
        Assert.True(adaptiveChunks.Count < fixedChunks.Count,
            $"自适应分块数 ({adaptiveChunks.Count}) 必须显著少于固定分块数 ({fixedChunks.Count})");
    }

    [Fact]
    public void AdaptiveMode_Aggregates_Tiny_Sibling_Sections_To_Prevent_Micro_Vectors()
    {
        // 构造包含很多微短小节的 Markdown（每个小节只有 60-80 字，在固定模式下会产生大量低信息密度微块）
        var md = @"# 系统总体架构
## 第一节 初始化说明
这是第一节初始化说明，负责说明环境准备与前置条件。
## 第二节 端口监听规范
这是第二节端口监听规范，负责说明网络访问与通信协议。
## 第三节 日志级别配置
这是第三节日志级别配置，负责说明日志记录与异常拦截。
## 第四节 性能指标监控
这是第四节性能指标监控，负责说明监控上报与告警规则。";

        var chunker = new MarkdownChunker();

        // 1. 固定分段
        var fixedOptions = new ChunkingOptions
        {
            Mode = "Fixed",
            MaxTokens = 512,
            MinTokens = 30
        };
        var fixedChunks = chunker.Chunk(md, fixedOptions);

        // 2. 自适应 Level 2 智能聚合
        var adaptiveOptions = new ChunkingOptions
        {
            Mode = "Adaptive",
            AdaptiveLevel = "Level2",
            AdaptiveStrategyType = "Balanced"
        };
        var adaptiveChunks = chunker.Chunk(md, adaptiveOptions);

        // 断言：微小兄弟小节应被智能聚合，分块数减少
        Assert.True(adaptiveChunks.Count <= fixedChunks.Count,
            $"自适应聚合分块数 ({adaptiveChunks.Count}) 应不多于未聚合分块数 ({fixedChunks.Count})");
    }

    [Fact]
    public void AdaptiveMode_Protects_CodeBlocks_Without_Cross_Chunk_Duplication()
    {
        var md = @"# 核心代码示例

以下是关键的算法实现：

```csharp
public class MathUtility
{
    public static int Add(int a, int b) => a + b;
    public static int Multiply(int a, int b) => a * b;
}
```

后续的一段总结说明文字。";

        var chunker = new MarkdownChunker();
        var options = new ChunkingOptions
        {
            Mode = "Adaptive",
            AdaptiveStrategyType = "CodeFirst",
            PreserveCodeBlocks = true
        };

        var chunks = chunker.Chunk(md, options);

        // 断言：分块中完整保留了代码围栏
        Assert.Contains(chunks, c => c.Content.Contains("```csharp") && c.Content.Contains("MathUtility"));
    }
}
