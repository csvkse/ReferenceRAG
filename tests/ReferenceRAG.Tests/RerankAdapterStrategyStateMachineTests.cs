using ReferenceRAG.Core.Features.ModelManagement.Adapters;
using ReferenceRAG.Core.Features.ModelManagement.StateMachine;
using ReferenceRAG.Core.Features.ModelManagement.Strategies;
using ReferenceRAG.Core.Models;
using Xunit;

namespace ReferenceRAG.Tests;

public class RerankAdapterStrategyStateMachineTests
{
    [Fact]
    public void ArchitectureDetector_AccuratelyClassifies_CausalVsBert()
    {
        // 1. Causal LLM family
        Assert.Equal("causal", GgufArchitectureDetector.DetectArchitecture(@"C:\models\Qwen3-Reranker-0.6B.Q4_0.gguf", "Qwen3-Reranker-0.6B"));
        Assert.Equal("causal", GgufArchitectureDetector.DetectArchitecture(@"C:\models\Llama-3-8B-Rerank.gguf", "Llama-3-Reranker"));
        Assert.Equal("causal", GgufArchitectureDetector.DetectArchitecture(@"C:\models\Gemma-2-9b-Rerank.gguf", "gemma-rerank"));
        Assert.Equal("causal", GgufArchitectureDetector.DetectArchitecture(@"C:\models\MiniCPM-Reranker.gguf", "minicpm"));

        // 2. BERT classification family
        Assert.Equal("bert", GgufArchitectureDetector.DetectArchitecture(@"C:\models\bge-reranker-large.gguf", "bge-reranker-large"));
        Assert.Equal("bert", GgufArchitectureDetector.DetectArchitecture(@"C:\models\bert-base-rerank.gguf", "bert-base"));
    }

    [Fact]
    public void StateMachine_TransitionsThroughNormalLifecycle()
    {
        var sm = new RerankExecutionContextStateMachine();
        Assert.Equal(RerankExecutionState.Idle, sm.CurrentState);

        var transitions = new List<(RerankExecutionState From, RerankExecutionState To)>();
        sm.StateChanged += (_, e) => transitions.Add((e.From, e.To));

        sm.TransitionTo(RerankExecutionState.PrefixIngested, "Prefill Query");
        Assert.Equal(RerankExecutionState.PrefixIngested, sm.CurrentState);

        sm.TransitionTo(RerankExecutionState.BatchDecoding, "Batch docs");
        Assert.Equal(RerankExecutionState.BatchDecoding, sm.CurrentState);

        sm.TransitionTo(RerankExecutionState.Scoring, "Extract Logits");
        Assert.Equal(RerankExecutionState.Scoring, sm.CurrentState);

        sm.TransitionTo(RerankExecutionState.Idle, "Complete");
        Assert.Equal(RerankExecutionState.Idle, sm.CurrentState);

        Assert.Equal(4, transitions.Count);
        Assert.Equal(RerankExecutionState.Idle, transitions[0].From);
        Assert.Equal(RerankExecutionState.PrefixIngested, transitions[0].To);
    }

    [Fact]
    public void StateMachine_AnomaliesTrigger_AutomaticDegradedTransition()
    {
        var sm = new RerankExecutionContextStateMachine();
        var degradedTriggered = false;
        sm.DegradedTriggered += (_, _) => degradedTriggered = true;

        // First anomaly - does not degrade yet
        var degraded1 = sm.RecordAnomalyScore("First NaN", threshold: 2);
        Assert.False(degraded1);
        Assert.Equal(1, sm.ConsecutiveNanCount);
        Assert.Equal(RerankExecutionState.Idle, sm.CurrentState);

        // Second anomaly - reaches threshold -> triggers Degraded
        var degraded2 = sm.RecordAnomalyScore("Second NaN", threshold: 2);
        Assert.True(degraded2);
        Assert.Equal(2, sm.ConsecutiveNanCount);
        Assert.Equal(RerankExecutionState.Degraded, sm.CurrentState);
        Assert.True(degradedTriggered);
        Assert.Equal(1, sm.DegradedExecutions);

        // Reset clears anomalies
        sm.Reset();
        Assert.Equal(RerankExecutionState.Idle, sm.CurrentState);
        Assert.Equal(0, sm.ConsecutiveNanCount);
    }

    [Fact]
    public void StateMachine_GenerateFallbackDocuments_YieldsValidOrderedScores()
    {
        var docs = new[] { "Doc A", "Doc B", "Doc C", "Doc D" };
        var results = RerankExecutionContextStateMachine.GenerateFallbackDocuments(docs);

        Assert.Equal(4, results.Count);
        for (int i = 0; i < results.Count; i++)
        {
            Assert.Equal(i, results[i].Index);
            Assert.Equal(docs[i], results[i].Document);
            Assert.True(results[i].RelevanceScore >= 0.1 && results[i].RelevanceScore <= 0.99);
            if (i > 0)
            {
                Assert.True(results[i - 1].RelevanceScore > results[i].RelevanceScore,
                    "Higher ranked initial candidates should maintain higher fallback scores");
            }
        }
    }

    [Fact]
    public async Task LlamaCausalLogitRerankAdapter_SimulationMode_RerankBatchAsync_Succeeds()
    {
        var dummyPath = Path.Combine(Path.GetTempPath(), $"dummy_causal_{Guid.NewGuid():N}.gguf");
        using var adapter = new LlamaCausalLogitRerankAdapter(dummyPath, "Qwen3-Reranker-0.6B");

        await adapter.InitializeAsync();
        Assert.True(adapter.IsSimulationMode);
        Assert.Equal("llama-causal", adapter.BackendType);

        var query = "ReferenceRAG 性能优化";
        var documents = new[]
        {
            "无关文档内容：今天天气很好",
            "相关文档内容：ReferenceRAG 性能优化与状态机实践指南",
            "半相关文档：RAG 检索增强系统概览"
        };

        var batchResult = await adapter.RerankBatchAsync(query, documents);
        Assert.Equal(query, batchResult.Query);
        Assert.Equal(3, batchResult.Documents.Count);
        Assert.True(batchResult.DurationMs >= 0);

        // The document containing the query string should score higher
        Assert.Contains("ReferenceRAG 性能优化", batchResult.Documents[0].Document);
        Assert.True(batchResult.Documents[0].RelevanceScore > batchResult.Documents[2].RelevanceScore);

        // Single rerank
        var singleScore = await adapter.RerankAsync(query, "包含 ReferenceRAG 性能优化 的内容");
        Assert.True(singleScore > 0.8);
    }

    [Fact]
    public async Task SafeFallbackRerankAdapter_AlwaysSucceedsWithOrderPreserved()
    {
        using var fallback = new SafeFallbackRerankAdapter("path/to/missing.gguf", "bad-model", "Test error");
        await fallback.InitializeAsync();
        Assert.True(fallback.IsLoaded);
        Assert.Equal("fallback", fallback.BackendType);

        var query = "test";
        var docs = new[] { "Doc 1", "Doc 2", "Doc 3" };
        var res = await fallback.RerankBatchAsync(query, docs);

        Assert.Equal(3, res.Documents.Count);
        Assert.Equal(0, res.DurationMs);
        Assert.True(res.Documents[0].RelevanceScore > res.Documents[1].RelevanceScore);
    }

    [Fact]
    public async Task StrategyFactory_RoutesGgufCausal_ToLlamaCausalLogitRerankAdapter()
    {
        var dummyCausalPath = Path.Combine(Path.GetTempPath(), $"Qwen3-Reranker-0.6B_{Guid.NewGuid():N}.gguf");
        var strategy = new GgufBackendStrategy();

        var adapter = await strategy.CreateRerankAdapterAsync(
            dummyCausalPath,
            "Qwen3-Reranker-0.6B",
            new RerankConfig { UseCuda = false });

        Assert.NotNull(adapter);
        Assert.Equal("llama-causal", adapter.BackendType);
        Assert.IsType<LlamaCausalLogitRerankAdapter>(adapter);

        var res = await adapter.RerankBatchAsync("测试查询", new[] { "候选文档一", "候选文档二" });
        Assert.Equal(2, res.Documents.Count);
    }
}
