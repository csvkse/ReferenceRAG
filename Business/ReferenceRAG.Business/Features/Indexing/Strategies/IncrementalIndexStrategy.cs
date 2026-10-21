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
/// 增量索引策略：全量扫描/增量哈希比对、精准分块、四重防护文件清理、GPU大批次推理与BM25/图谱构建。
/// </summary>
public class IncrementalIndexStrategy : IIndexExecutionStrategy
{
    public string Name => "Incremental";

    public bool CanHandle(IndexRequest request) => !request.VectorOnly && !request.Force && !request.Rechunk;

    public async Task<IndexExecutionResult> ExecuteAsync(
        IndexExecutionContext context,
        IndexJobStateMachine stateMachine,
        CancellationToken ct)
    {
        var request = context.Request;
        var config = context.ConfigManager.Load();
        var sw = Stopwatch.StartNew();

        // ── 1. 扫描阶段 (Scanning) ──
        stateMachine.TransitionTo(IndexJobPhase.Scanning, "扫描源文件夹及文件变动");

        var sources = request.Sources?.Count > 0
            ? config.Sources.Where(s => request.Sources.Contains(s.Name)).ToList()
            : config.Sources.Where(s => s.Enabled).ToList();

        var allFiles = request.Files?.ToList() ?? new List<string>();
        if (request.Files is null)
        {
            foreach (var source in sources)
            {
                var normalizedSourcePath = context.StorageAdapter.NormalizePath(source.Path);
                if (!Directory.Exists(normalizedSourcePath) && !Directory.Exists(source.Path))
                {
                    context.Logger.LogWarning("源目录不存在: {Path}", source.Path);
                    continue;
                }

                var scanDir = Directory.Exists(normalizedSourcePath) ? normalizedSourcePath : source.Path;
                try
                {
                    var enumOptions = new EnumerationOptions
                    {
                        IgnoreInaccessible = true,
                        RecurseSubdirectories = true,
                        MatchCasing = MatchCasing.CaseInsensitive
                    };
                    var patterns = (source.FilePatterns?.Count > 0) ? source.FilePatterns : new List<string> { "*.md", "*.txt" };
                    var excludeDirs = source.ExcludeDirs ?? new List<string>();

                    var files = Directory.EnumerateFiles(scanDir, "*", enumOptions)
                        .Where(f => patterns.Any(p => MatchesPattern(f, p)))
                        .Where(f => !excludeDirs.Any(d => f.Contains(d, StringComparison.OrdinalIgnoreCase)));
                    allFiles.AddRange(files);
                }
                catch (Exception ex)
                {
                    context.Logger.LogWarning(ex, "扫描目录失败: {Dir}", scanDir);
                }
            }
        }

        allFiles = allFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        stateMachine.TotalFiles = allFiles.Count;

        // ── 2. 准备阶段 (Preparing) ──
        stateMachine.TransitionTo(IndexJobPhase.Preparing, $"准备文件与分块 (0/{allFiles.Count})");

        var errors = new ConcurrentBag<string>();
        var skippedCount = 0;
        var errorsCount = 0;
        var prepCount = 0;

        const int maxPrepParallelism = 8;
        using var prepSemaphore = new SemaphoreSlim(maxPrepParallelism);

        var prepTasks = allFiles.Select(async file =>
        {
            if (ct.IsCancellationRequested) return null;
            if (!context.Guard.TryAcquire(file))
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
                context.ActiveFiles.TryAdd(file, 0);

                prepared = await context.Pipeline.PrepareAsync(file, sources, request.Force, ct);
                if (prepared is not null)
                {
                    context.HeldFiles.TryAdd(file, 0);
                    // 确保持久化父文件记录，避免后续并发场景偶发外键丢失
                    await context.StorageAdapter.EnsureFileRecordExistsAsync(prepared.FileRecord, ct);
                }
                else
                {
                    Interlocked.Increment(ref skippedCount);
                }

                var currentPrep = Interlocked.Increment(ref prepCount);
                stateMachine.ReportPreparingProgress(file, currentPrep, allFiles.Count);
                return prepared;
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref skippedCount);
                return null;
            }
            catch (Exception ex)
            {
                errors.Add($"{file}: {ex.Message}");
                Interlocked.Increment(ref errorsCount);
                context.Logger.LogWarning(ex, "Phase1 准备失败: {File}", file);
                return null;
            }
            finally
            {
                if (enteredSemaphore)
                    prepSemaphore.Release();
                context.ActiveFiles.TryRemove(file, out _);
                if (prepared is null)
                    context.Guard.Release(file);
            }
        }).ToList();

        var prepResults = await Task.WhenAll(prepTasks);
        var contexts = prepResults.Where(c => c != null).ToList()!;
        stateMachine.SkippedFiles = skippedCount;
        stateMachine.ErrorsCount = errorsCount;

        // ── 2.5 磁盘已删文件安全清理 ──
        if (request.Files is null && !ct.IsCancellationRequested)
        {
            var diskFilesSet = new HashSet<string>(allFiles, StringComparer.OrdinalIgnoreCase);
            var activeFilePaths = new HashSet<string>(contexts.Select(c => c!.FileRecord.Path), StringComparer.OrdinalIgnoreCase);
            var sourceNames = sources.Select(s => s.Name).ToList();

            await context.StorageAdapter.CleanDeletedFilesSafeAsync(sourceNames, diskFilesSet, activeFilePaths, ct);
        }

        // ── 3. 向量推理阶段 (Embedding) ──
        var allChunks = contexts.SelectMany(c => c!.Chunks).ToList();
        var totalVectorsCount = 0;
        var totalChunksCount = allChunks.Count;
        stateMachine.TotalChunks = totalChunksCount;

        if (allChunks.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var batchSize = Math.Max(1, config.Embedding.BatchSize);
            var totalBatches = (int)Math.Ceiling((double)allChunks.Count / batchSize);

            stateMachine.TransitionTo(IndexJobPhase.Embedding, $"向量推理准备 (共 {totalChunksCount} 分块, {totalBatches} 批次)");

            using var indexingPipeline = new IndexingPipeline(
                context.EmbeddingService,
                context.VectorStore,
                batchSize: batchSize,
                storageAdapter: context.StorageAdapter);

            // 监听流水线进度，实时推送至状态机与 SignalR 广播
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
                throw new InvalidOperationException($"向量管道失败: {pipelineResult.ErrorMessage}");
            }

            totalVectorsCount = pipelineResult.TotalVectors;
            stateMachine.TotalVectors = totalVectorsCount;
        }

        // ── 4. 收尾后处理阶段 (Finalizing) ──
        stateMachine.TransitionTo(IndexJobPhase.Finalizing, $"收尾后处理 (0/{contexts.Count})");

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
                await context.Pipeline.FinalizeAsync(
                    ctx!,
                    filenameMap,
                    ct,
                    updateGraph: true,
                    updateBm25: true);

                var count = Interlocked.Increment(ref processedCount);
                stateMachine.ReportFinalizingProgress(ctx!.FileRecord.FileName, count, contexts.Count);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                errors.Add($"{ctx!.FileRecord.Path}: {ex.Message}");
                Interlocked.Increment(ref errorsCount);
                context.Logger.LogWarning(ex, "Phase3 收尾失败: {File}", ctx!.FileRecord.Path);
            }
            finally
            {
                finSemaphore.Release();
                context.Guard.Release(ctx!.FileRecord.Path);
                context.HeldFiles.TryRemove(ctx.FileRecord.Path, out _);
            }
        }).ToList();

        await Task.WhenAll(finalizeTasks);

        // 自愈收尾：自动清理全库历史遗留的任何孤立向量，确保存储与统计 100% 绝对一致
        try
        {
            await context.VectorStore.CleanupOrphanChunkVectorsAsync(ct);
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(ex, "收尾自愈孤立向量清理提示: {Message}", ex.Message);
        }

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

    private static bool MatchesPattern(string filePath, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        var p = pattern.Trim();
        if (p == "*" || p == "*.*") return true;

        var fileName = Path.GetFileName(filePath);
        if (p.StartsWith("*."))
        {
            var ext = p[1..];
            return fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase);
        }
        if (p.StartsWith('.'))
        {
            return fileName.EndsWith(p, StringComparison.OrdinalIgnoreCase);
        }
        if (p.Contains('*') || p.Contains('?'))
        {
            return System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(p, fileName, ignoreCase: true);
        }
        return fileName.Equals(p, StringComparison.OrdinalIgnoreCase) ||
               filePath.Contains(p, StringComparison.OrdinalIgnoreCase);
    }
}
