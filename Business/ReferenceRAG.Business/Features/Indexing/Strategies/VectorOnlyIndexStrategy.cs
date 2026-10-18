using Microsoft.Extensions.Logging;
using ReferenceRAG.Business.Features.Indexing.StateMachine;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Core.Services.Graph;
using ReferenceRAG.Service.Services;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace ReferenceRAG.Business.Features.Indexing.Strategies;

/// <summary>
/// 仅重推向量策略（VectorOnly）：用于模型切换后的快速向量重算，不破坏现有分块结构、BM25与图谱数据。
/// </summary>
public class VectorOnlyIndexStrategy : IIndexExecutionStrategy
{
    public string Name => "VectorOnly";

    public bool CanHandle(IndexRequest request) => request.VectorOnly;

    public async Task<IndexExecutionResult> ExecuteAsync(
        IndexExecutionContext context,
        IndexJobStateMachine stateMachine,
        CancellationToken ct)
    {
        var request = context.Request;
        var config = context.ConfigManager.Load();
        var sw = Stopwatch.StartNew();

        stateMachine.TransitionTo(IndexJobPhase.Scanning, "扫描需要重构向量的文件");

        var sources = request.Sources?.Count > 0
            ? config.Sources.Where(s => request.Sources.Contains(s.Name)).ToList()
            : config.Sources.Where(s => s.Enabled).ToList();

        var sourceNames = sources.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 获取已有数据库中的文件记录进行向量重建
        var allDbFiles = (await context.VectorStore.GetAllFilesAsync(ct))
            .Where(f => sourceNames.Count == 0 || (f.Source != null && sourceNames.Contains(f.Source)))
            .ToList();

        if (request.Files?.Count > 0)
        {
            var targetFiles = new HashSet<string>(request.Files, StringComparer.OrdinalIgnoreCase);
            allDbFiles = allDbFiles.Where(f => targetFiles.Contains(f.Path)).ToList();
        }

        stateMachine.TotalFiles = allDbFiles.Count;
        if (allDbFiles.Count == 0)
        {
            stateMachine.ReportCompleted(sw.Elapsed);
            return IndexExecutionResult.Ok(0, 0, 0, 0, 0);
        }

        // ── 1. 准备存量分块 (Preparing) ──
        stateMachine.TransitionTo(IndexJobPhase.Preparing, $"加载存量分块 (0/{allDbFiles.Count})");

        var errors = new ConcurrentBag<string>();
        var skippedCount = 0;
        var errorsCount = 0;
        var prepCount = 0;

        const int maxPrepParallelism = 8;
        using var prepSemaphore = new SemaphoreSlim(maxPrepParallelism);

        var prepTasks = allDbFiles.Select(async dbFile =>
        {
            if (ct.IsCancellationRequested) return null;
            if (!context.Guard.TryAcquire(dbFile.Path))
            {
                Interlocked.Increment(ref skippedCount);
                return null;
            }

            FileProcessContext? prepared = null;
            var enteredSemaphore = false;
            try
            {
                await prepSemaphore.WaitAsync(ct);
                enteredSemaphore = true;
                context.ActiveFiles.TryAdd(dbFile.Path, 0);

                prepared = await context.Pipeline.PrepareVectorOnlyAsync(dbFile.Path, ct);
                if (prepared is not null)
                {
                    context.HeldFiles.TryAdd(dbFile.Path, 0);
                }
                else
                {
                    Interlocked.Increment(ref skippedCount);
                }

                var current = Interlocked.Increment(ref prepCount);
                stateMachine.ReportPreparingProgress(dbFile.Path, current, allDbFiles.Count);
                return prepared;
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref skippedCount);
                return null;
            }
            catch (Exception ex)
            {
                errors.Add($"{dbFile.Path}: {ex.Message}");
                Interlocked.Increment(ref errorsCount);
                context.Logger.LogWarning(ex, "VectorOnly 准备失败: {File}", dbFile.Path);
                return null;
            }
            finally
            {
                if (enteredSemaphore) prepSemaphore.Release();
                context.ActiveFiles.TryRemove(dbFile.Path, out _);
                if (prepared is null) context.Guard.Release(dbFile.Path);
            }
        }).ToList();

        var prepResults = await Task.WhenAll(prepTasks);
        var contexts = prepResults.Where(c => c != null).ToList()!;
        stateMachine.SkippedFiles = skippedCount;
        stateMachine.ErrorsCount = errorsCount;

        // ── 2. 向量推理 (Embedding) ──
        var allChunks = contexts.SelectMany(c => c!.Chunks).ToList();
        var totalVectorsCount = 0;
        var totalChunksCount = allChunks.Count;
        stateMachine.TotalChunks = totalChunksCount;

        if (allChunks.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var batchSize = Math.Max(1, config.Embedding.BatchSize);
            var totalBatches = (int)Math.Ceiling((double)allChunks.Count / batchSize);

            stateMachine.TransitionTo(IndexJobPhase.Embedding, $"向量重算推理 (共 {totalChunksCount} 分块, {totalBatches} 批次)");

            using var indexingPipeline = new IndexingPipeline(
                context.EmbeddingService,
                context.VectorStore,
                batchSize: batchSize,
                storageAdapter: context.StorageAdapter);

            indexingPipeline.Progress += (_, e) =>
            {
                if (e.Stage == "Embed")
                {
                    stateMachine.ReportEmbeddingProgress(
                        e.BatchIndex + 1,
                        totalBatches,
                        Math.Min(totalChunksCount, (e.BatchIndex + 1) * batchSize),
                        totalChunksCount);
                }
                else if (e.Stage == "Write")
                {
                    stateMachine.TotalVectors = e.Processed;
                }
            };

            var embeddingInputs = contexts
                .Where(c => c!.EmbeddingInputs != null)
                .SelectMany(c => c!.EmbeddingInputs!)
                .ToDictionary(kv => kv.Key, kv => kv.Value);

            var pipelineResult = await indexingPipeline.ExecuteAsync(
                allChunks,
                "batch",
                ct,
                embeddingInputs: embeddingInputs.Count > 0 ? embeddingInputs : null);

            if (!pipelineResult.Success)
            {
                throw new InvalidOperationException($"向量重构失败: {pipelineResult.ErrorMessage}");
            }

            totalVectorsCount = pipelineResult.TotalVectors;
            stateMachine.TotalVectors = totalVectorsCount;
        }

        // ── 3. 状态收尾 (Finalizing) ──
        stateMachine.TransitionTo(IndexJobPhase.Finalizing, $"状态完成收尾 (0/{contexts.Count})");

        var allIndexedFiles = await context.VectorStore.GetAllFilesAsync(ct);
        var filenameMap = GraphFilenameMapper.BuildFilenameMap(allIndexedFiles);

        const int maxFinalizeParallelism = 4;
        using var finSemaphore = new SemaphoreSlim(maxFinalizeParallelism);

        var processedCount = 0;
        var finalizeTasks = contexts.Select(async ctx =>
        {
            if (ct.IsCancellationRequested) return;
            await finSemaphore.WaitAsync(ct);
            try
            {
                // VectorOnly 时仅更新状态，不改 BM25 / 图谱
                await context.Pipeline.FinalizeAsync(
                    ctx!,
                    filenameMap,
                    ct,
                    updateGraph: false,
                    updateBm25: false);

                var count = Interlocked.Increment(ref processedCount);
                stateMachine.ReportFinalizingProgress(ctx!.FileRecord.FileName, count, contexts.Count);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                errors.Add($"{ctx!.FileRecord.Path}: {ex.Message}");
                Interlocked.Increment(ref errorsCount);
                context.Logger.LogWarning(ex, "VectorOnly 收尾失败: {File}", ctx!.FileRecord.Path);
            }
            finally
            {
                finSemaphore.Release();
                context.Guard.Release(ctx!.FileRecord.Path);
                context.HeldFiles.TryRemove(ctx.FileRecord.Path, out _);
            }
        }).ToList();

        await Task.WhenAll(finalizeTasks);

        sw.Stop();
        stateMachine.ProcessedFiles = processedCount;
        stateMachine.ReportCompleted(sw.Elapsed);

        return IndexExecutionResult.Ok(
            stateMachine.TotalFiles,
            processedCount,
            skippedCount,
            totalChunksCount,
            totalVectorsCount,
            errors.ToList());
    }
}
