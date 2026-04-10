using System.Threading.Channels;
using ObsidianRAG.Core.Interfaces;
using ObsidianRAG.Core.Models;

namespace ObsidianRAG.Core.Services;

/// <summary>
/// 索引管道 - 三阶段流水线处理
/// Stage 1: Tokenize (CPU 并行)
/// Stage 2: GPU 推理
/// Stage 3: 写入存储
/// </summary>
public class IndexingPipeline : IDisposable
{
    private readonly IEmbeddingService _embeddingService;
    private readonly IVectorStore _vectorStore;
    private readonly int _batchSize;
    private readonly int _maxConcurrency;
    private readonly Channel<ChunkBatch> _tokenizeChannel;
    private readonly Channel<EmbeddedBatch> _embedChannel;
    private readonly CancellationTokenSource _cts;
    private bool _disposed;

    public event EventHandler<IndexingProgressEventArgs>? Progress;

    public IndexingPipeline(
        IEmbeddingService embeddingService,
        IVectorStore vectorStore,
        int batchSize = 64,
        int maxConcurrency = 3)
    {
        _embeddingService = embeddingService;
        _vectorStore = vectorStore;
        _batchSize = batchSize;
        _maxConcurrency = maxConcurrency;

        // 创建通道（有界，防止内存爆炸）
        _tokenizeChannel = Channel.CreateBounded<ChunkBatch>(
            new BoundedChannelOptions(maxConcurrency) { FullMode = BoundedChannelFullMode.Wait });

        _embedChannel = Channel.CreateBounded<EmbeddedBatch>(
            new BoundedChannelOptions(maxConcurrency) { FullMode = BoundedChannelFullMode.Wait });

        _cts = new CancellationTokenSource();
    }

    /// <summary>
    /// 执行索引管道
    /// </summary>
    public async Task<IndexingResult> ExecuteAsync(
        IEnumerable<ChunkRecord> chunks,
        string source,
        CancellationToken cancellationToken = default)
    {
        var chunkList = chunks.ToList();
        if (chunkList.Count == 0)
        {
            return new IndexingResult { TotalChunks = 0, TotalVectors = 0 };
        }

        var result = new IndexingResult
        {
            TotalChunks = chunkList.Count,
            StartTime = DateTime.UtcNow
        };

        var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);

        try
        {
            // 启动三个阶段
            var tokenizeTask = RunTokenizeStageAsync(chunkList, source, combinedCts.Token);
            var embedTask = RunEmbedStageAsync(combinedCts.Token);
            var writeTask = RunWriteStageAsync(result, combinedCts.Token);

            // 等待所有阶段完成，任何一个阶段异常都应取消其他阶段
            var allTasks = new[] { tokenizeTask, embedTask, writeTask };
            while (allTasks.Any(t => !t.IsCompleted))
            {
                var completedTask = await Task.WhenAny(allTasks.Where(t => !t.IsCompleted));
                if (completedTask.IsFaulted)
                {
                    // Cancel remaining stages when one fails
                    combinedCts.Cancel();
                    Console.WriteLine($"Pipeline stage failed: {completedTask.Exception?.GetBaseException().Message}");
                    try { await completedTask; } catch { } // Observe the exception
                    break;
                }
            }

            // Await all tasks to propagate exceptions
            try
            {
                await Task.WhenAll(tokenizeTask, embedTask, writeTask);
            }
            catch (AggregateException ae)
            {
                throw ae.InnerException ?? ae;
            }

            result.EndTime = DateTime.UtcNow;
            result.Duration = result.EndTime.Value - result.StartTime;
            result.Success = true;
        }
        catch (OperationCanceledException)
        {
            result.Success = false;
            result.ErrorMessage = "Cancelled";
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    /// <summary>
    /// Stage 1: Tokenize (CPU 并行)
    /// </summary>
    private async Task RunTokenizeStageAsync(
        List<ChunkRecord> chunks,
        string source,
        CancellationToken cancellationToken)
    {
        try
        {
            // 分批处理
            var batches = chunks.Chunk(_batchSize).ToList();

            // 小批量顺序处理，大批量并行
            if (batches.Count < 4)
            {
                for (int i = 0; i < batches.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var batch = batches[i];
                    await ProcessTokenizeBatch(batch, source, i, batch.Length, chunks.Count, cancellationToken);
                }
            }
            else
            {
                var options = new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 4)
                };

                await Task.Run(() =>
                {
                    Parallel.For(0, batches.Count, options, i =>
                    {
                        var batch = batches[i];
                        ProcessTokenizeBatchSync(batch, source, i, chunks.Count, cancellationToken);
                    });
                }, cancellationToken);
            }
        }
        finally
        {
            _tokenizeChannel.Writer.Complete();
        }
    }

    private async Task ProcessTokenizeBatch(
        ChunkRecord[] batch,
        string source,
        int batchIndex,
        int batchSize,
        int totalCount,
        CancellationToken cancellationToken)
    {
        // 准备数据（这里只是分组，实际 tokenize 在 EmbeddingService 中）
        var chunkBatch = new ChunkBatch
        {
            Chunks = batch.ToList(),
            Source = source,
            BatchIndex = batchIndex
        };

        await _tokenizeChannel.Writer.WriteAsync(chunkBatch, cancellationToken);

        Progress?.Invoke(this, new IndexingProgressEventArgs
        {
            Stage = "Tokenize",
            Processed = (batchIndex + 1) * batchSize,
            Total = totalCount
        });
    }

    private void ProcessTokenizeBatchSync(
        ChunkRecord[] batch,
        string source,
        int batchIndex,
        int totalCount,
        CancellationToken cancellationToken)
    {
        var chunkBatch = new ChunkBatch
        {
            Chunks = batch.ToList(),
            Source = source,
            BatchIndex = batchIndex
        };

        // Use Wait with cancellation instead of TryWrite to ensure data is not silently lost
        // when the bounded channel is full
        while (!_tokenizeChannel.Writer.TryWrite(chunkBatch))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Thread.Sleep(10);
        }
    }

    /// <summary>
    /// Stage 2: GPU 推理
    /// </summary>
    private async Task RunEmbedStageAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var chunkBatch in _tokenizeChannel.Reader.ReadAllAsync(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var texts = chunkBatch.Chunks.Select(c => c.Content).ToList();
                var vectors = await _embeddingService.EncodeBatchAsync(texts, EmbeddingMode.Document, cancellationToken);

                var embeddedBatch = new EmbeddedBatch
                {
                    Chunks = chunkBatch.Chunks,
                    Vectors = vectors.ToList(),
                    Source = chunkBatch.Source,
                    BatchIndex = chunkBatch.BatchIndex
                };

                await _embedChannel.Writer.WriteAsync(embeddedBatch, cancellationToken);

                Progress?.Invoke(this, new IndexingProgressEventArgs
                {
                    Stage = "Embed",
                    BatchIndex = chunkBatch.BatchIndex
                });
            }
        }
        finally
        {
            _embedChannel.Writer.Complete();
        }
    }

    /// <summary>
    /// Stage 3: 写入存储
    /// </summary>
    private async Task RunWriteStageAsync(IndexingResult result, CancellationToken cancellationToken)
    {
        var totalVectors = 0;

        await foreach (var embeddedBatch in _embedChannel.Reader.ReadAllAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var records = new List<VectorRecord>();
            for (int i = 0; i < embeddedBatch.Chunks.Count; i++)
            {
                var chunk = embeddedBatch.Chunks[i];
                records.Add(new VectorRecord
                {
                    Id = Guid.NewGuid().ToString(),
                    ChunkId = chunk.Id,
                    FileId = chunk.FileId,  // 修复：正确关联 FileId
                    Vector = embeddedBatch.Vectors[i],
                    Dimension = embeddedBatch.Vectors[i].Length,
                    Source = chunk.Source,
                    ModelName = _embeddingService.ModelName
                });
            }

            // 批量写入
            await _vectorStore.UpsertVectorsAsync(records, cancellationToken);
            await _vectorStore.UpsertChunksAsync(embeddedBatch.Chunks, cancellationToken);

            totalVectors += records.Count;

            Progress?.Invoke(this, new IndexingProgressEventArgs
            {
                Stage = "Write",
                BatchIndex = embeddedBatch.BatchIndex,
                Processed = totalVectors
            });
        }

        result.TotalVectors = totalVectors;
    }

    /// <summary>
    /// 取消管道
    /// </summary>
    public void Cancel()
    {
        _cts.Cancel();
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _cts.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// 分段批次
/// </summary>
internal class ChunkBatch
{
    public List<ChunkRecord> Chunks { get; set; } = new();
    public string Source { get; set; } = "";
    public int BatchIndex { get; set; }
}

/// <summary>
/// 嵌入批次
/// </summary>
internal class EmbeddedBatch
{
    public List<ChunkRecord> Chunks { get; set; } = new();
    public List<float[]> Vectors { get; set; } = new();
    public string Source { get; set; } = "";
    public int BatchIndex { get; set; }
}

/// <summary>
/// 索引进度事件参数
/// </summary>
public class IndexingProgressEventArgs : EventArgs
{
    public string Stage { get; set; } = "";
    public int BatchIndex { get; set; }
    public int Processed { get; set; }
    public int Total { get; set; }
}

/// <summary>
/// 索引结果
/// </summary>
public class IndexingResult
{
    public bool Success { get; set; }
    public int TotalChunks { get; set; }
    public int TotalVectors { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public TimeSpan Duration { get; set; }
    public string? ErrorMessage { get; set; }
}
