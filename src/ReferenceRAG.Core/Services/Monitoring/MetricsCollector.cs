using System.Diagnostics;
using System.Collections.Concurrent;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Core.Services;

/// <summary>
/// 有界直方图 - 限制最大样本数，自动淘汰最旧数据
/// </summary>
public class BoundedHistogram
{
    private readonly int _maxSamples;
    private readonly object _lock = new();
    private readonly Queue<long> _samples;
    private long _sum;

    public BoundedHistogram(int maxSamples = 1000)
    {
        _maxSamples = maxSamples;
        _samples = new Queue<long>();
    }

    public void Add(long value)
    {
        lock (_lock)
        {
            if (_samples.Count >= _maxSamples)
            {
                var oldest = _samples.Dequeue();
                _sum -= oldest;
            }

            _samples.Enqueue(value);
            _sum += value;
        }
    }

    public HistogramStatistics GetStatistics()
    {
        lock (_lock)
        {
            var samples = _samples.ToList();

            if (samples.Count == 0)
                return new HistogramStatistics();

            samples.Sort();

            return new HistogramStatistics
            {
                Count = samples.Count,
                Sum = _sum,
                Average = samples.Count > 0 ? samples.Average() : 0,
                Min = samples.Count > 0 ? samples[0] : 0,
                Max = samples.Count > 0 ? samples[^1] : 0,
                P50 = GetPercentile(samples, 50),
                P95 = GetPercentile(samples, 95),
                P99 = GetPercentile(samples, 99)
            };
        }
    }

    private static long GetPercentile(List<long> sortedSamples, int percentile)
    {
        if (sortedSamples.Count == 0) return 0;
        var index = (int)Math.Ceiling(sortedSamples.Count * percentile / 100.0) - 1;
        return sortedSamples[Math.Max(0, index)];
    }

    public void Clear()
    {
        lock (_lock)
        {
            _samples.Clear();
            _sum = 0;
        }
    }
}

/// <summary>
/// 直方图统计信息
/// </summary>
public class HistogramStatistics
{
    public int Count { get; set; }
    public long Sum { get; set; }
    public double Average { get; set; }
    public long Min { get; set; }
    public long Max { get; set; }
    public long P50 { get; set; }
    public long P95 { get; set; }
    public long P99 { get; set; }
}

/// <summary>
/// 监控指标采集服务
/// </summary>
public class MetricsCollector : IDisposable
{
    private readonly IVectorStore _vectorStore;
    private readonly Dictionary<string, MetricValue> _metrics;
    private readonly Dictionary<string, BoundedHistogram> _histograms;
    private readonly int _maxHistogramSamples;
    private readonly object _lock = new();

    // Token 统计相关字段
    private readonly ConcurrentBag<TokenMetrics> _tokenBuffer; // Token 使用记录缓冲区
    private readonly object _tokenStatsLock = new();
    private long _totalQueryTokens;
    private long _totalIndexTokens;
    private DateTime? _lastTokenUpdate;
    private readonly Timer _flushTimer; // 定时持久化计时器
    private readonly string _tokenLogPath; // Token 日志文件路径

    public MetricsCollector(IVectorStore vectorStore, int maxHistogramSamples = 1000)
    {
        _vectorStore = vectorStore;
        _metrics = new Dictionary<string, MetricValue>();
        _histograms = new Dictionary<string, BoundedHistogram>();
        _maxHistogramSamples = maxHistogramSamples;

        // 初始化 Token 统计
        _tokenBuffer = new ConcurrentBag<TokenMetrics>();
        _tokenLogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", "token_metrics.log");
        _totalQueryTokens = 0;
        _totalIndexTokens = 0;

        // 初始化定时器，每 30 秒持久化一次
        // 使用 try-catch 包裹 async void lambda，避免异常导致应用崩溃
        _flushTimer = new Timer(_ =>
        {
            _ = FlushTokenMetricsAsyncSafe();
        }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// 安全的持久化方法，捕获所有异常
    /// </summary>
    private async Task FlushTokenMetricsAsyncSafe()
    {
        try
        {
            await FlushTokenMetricsAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MetricsCollector] 定时持久化异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 采集系统指标
    /// </summary>
    public async Task<SystemMetrics> CollectSystemMetricsAsync()
    {
        var process = Process.GetCurrentProcess();

        var metrics = new SystemMetrics
        {
            Timestamp = DateTime.UtcNow,
            CpuUsagePercent = await GetCpuUsageAsync(),
            MemoryUsageMB = process.WorkingSet64 / 1024 / 1024,
            ThreadCount = process.Threads.Count,
            HandleCount = process.HandleCount,
            Uptime = DateTime.UtcNow - process.StartTime.ToUniversalTime()
        };

        return metrics;
    }

    /// <summary>
    /// 采集索引指标
    /// </summary>
    public async Task<IndexMetrics> CollectIndexMetricsAsync()
    {
        var files = await _vectorStore.GetAllFilesAsync();
        var fileList = files.ToList();

        var metrics = new IndexMetrics
        {
            Timestamp = DateTime.UtcNow,
            TotalFiles = fileList.Count,
            TotalSize = fileList.Sum(f => f.ContentLength),
            TotalChunks = fileList.Sum(f => f.ChunkCount),
            TotalTokens = fileList.Sum(f => f.TotalTokens),
            OldestIndex = fileList.Count > 0 ? fileList.Min(f => f.IndexedAt) : null,
            NewestIndex = fileList.Count > 0 ? fileList.Max(f => f.IndexedAt) : null
        };

        return metrics;
    }

    /// <summary>
    /// 记录查询指标
    /// </summary>
    public void RecordQueryMetric(string query, long durationMs, int resultCount)
    {
        lock (_lock)
        {
            var key = "queries_total";
            if (!_metrics.ContainsKey(key))
            {
                _metrics[key] = new MetricValue { Name = key, Type = MetricType.Counter, Value = 0L };
            }
            _metrics[key].Value = (long)_metrics[key].Value + 1;

            // 使用有界直方图
            GetOrCreateHistogram("query_latency_ms").Add(durationMs);
            GetOrCreateHistogram("query_results_count").Add(resultCount);
        }
    }

    /// <summary>
    /// 获取或创建有界直方图
    /// </summary>
    private BoundedHistogram GetOrCreateHistogram(string name)
    {
        if (!_histograms.TryGetValue(name, out var histogram))
        {
            histogram = new BoundedHistogram(_maxHistogramSamples);
            _histograms[name] = histogram;
        }
        return histogram;
    }

    /// <summary>
    /// 记录索引指标
    /// </summary>
    public void RecordIndexMetric(int filesProcessed, int chunksCreated, long durationMs)
    {
        lock (_lock)
        {
            var key = "index_runs_total";
            if (!_metrics.ContainsKey(key))
            {
                _metrics[key] = new MetricValue { Name = key, Type = MetricType.Counter, Value = 0L };
            }
            _metrics[key].Value = (long)_metrics[key].Value + 1;

            var filesKey = "index_files_processed";
            if (!_metrics.ContainsKey(filesKey))
            {
                _metrics[filesKey] = new MetricValue { Name = filesKey, Type = MetricType.Gauge, Value = filesProcessed };
            }
            else
            {
                _metrics[filesKey].Value = filesProcessed;
            }

            var chunksKey = "index_chunks_created";
            if (!_metrics.ContainsKey(chunksKey))
            {
                _metrics[chunksKey] = new MetricValue { Name = chunksKey, Type = MetricType.Gauge, Value = chunksCreated };
            }
            else
            {
                _metrics[chunksKey].Value = chunksCreated;
            }
        }
    }

    /// <summary>
    /// 获取所有指标
    /// </summary>
    public Dictionary<string, MetricValue> GetAllMetrics()
    {
        lock (_lock)
        {
            return new Dictionary<string, MetricValue>(_metrics);
        }
    }

    /// <summary>
    /// 获取指标摘要
    /// </summary>
    public MetricsSummary GetSummary()
    {
        lock (_lock)
        {
            var summary = new MetricsSummary();

            if (_metrics.TryGetValue("queries_total", out var queries))
            {
                summary.TotalQueries = (long)queries.Value;
            }

            if (_histograms.TryGetValue("query_latency_ms", out var latencyHistogram))
            {
                var stats = latencyHistogram.GetStatistics();
                summary.AvgQueryLatencyMs = stats.Average;
                summary.P95QueryLatencyMs = stats.P95;
                summary.P99QueryLatencyMs = stats.P99;
            }

            if (_histograms.TryGetValue("query_results_count", out var resultsHistogram))
            {
                var stats = resultsHistogram.GetStatistics();
                summary.AvgResultsPerQuery = stats.Average;
            }

            return summary;
        }
    }

    /// <summary>
    /// 清理所有直方图数据（用于重置统计）
    /// </summary>
    public void ClearHistograms()
    {
        lock (_lock)
        {
            foreach (var histogram in _histograms.Values)
            {
                histogram.Clear();
            }
        }
    }

    #region Token 统计功能

    /// <summary>
    /// 记录查询操作的 Token 使用量
    /// </summary>
    /// <param name="count">Token 数量</param>
    /// <param name="modelName">模型名称（可选）</param>
    public void RecordQueryTokens(int count, string? modelName = null)
    {
        var metric = new TokenMetrics
        {
            Timestamp = DateTime.UtcNow,
            Operation = "Query",
            TokenCount = count,
            ModelName = modelName
        };

        _tokenBuffer.Add(metric);

        // 更新统计
        lock (_tokenStatsLock)
        {
            _totalQueryTokens += count;
            _lastTokenUpdate = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// 记录索引操作的 Token 使用量
    /// </summary>
    /// <param name="count">Token 数量</param>
    /// <param name="modelName">模型名称（可选）</param>
    public void RecordIndexTokens(int count, string? modelName = null)
    {
        var metric = new TokenMetrics
        {
            Timestamp = DateTime.UtcNow,
            Operation = "Index",
            TokenCount = count,
            ModelName = modelName
        };

        _tokenBuffer.Add(metric);

        // 更新统计
        lock (_tokenStatsLock)
        {
            _totalIndexTokens += count;
            _lastTokenUpdate = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// 获取 Token 统计信息
    /// </summary>
    /// <returns>Token 统计汇总</returns>
    public TokenStats GetTokenStats()
    {
        lock (_tokenStatsLock)
        {
            return new TokenStats
            {
                TotalQueryTokens = _totalQueryTokens,
                TotalIndexTokens = _totalIndexTokens,
                LastUpdated = _lastTokenUpdate
            };
        }
    }

    /// <summary>
    /// 后台持久化 Token 指标到日志文件
    /// 每 30 秒由定时器自动调用
    /// </summary>
    public async Task FlushTokenMetricsAsync()
    {
        if (_tokenBuffer.IsEmpty) return;

        // 备份缓冲区数据，写入失败时可恢复
        var metricsToFlush = new List<TokenMetrics>();
        var flushFailed = false;

        try
        {
            // 尝试清空缓冲区并获取所有待写入的记录
            while (_tokenBuffer.TryTake(out var metric))
            {
                metricsToFlush.Add(metric);
            }

            if (metricsToFlush.Count == 0) return;

            // 确保日志目录存在
            var logDir = Path.GetDirectoryName(_tokenLogPath);
            if (!string.IsNullOrEmpty(logDir) && !Directory.Exists(logDir))
            {
                Directory.CreateDirectory(logDir);
            }

            // 追加写入日志文件
            var lines = metricsToFlush.Select(m =>
                $"{m.Timestamp:yyyy-MM-dd HH:mm:ss}|{m.Operation}|{m.TokenCount}|{m.ModelName ?? "N/A"}");

            await File.AppendAllLinesAsync(_tokenLogPath, lines);
        }
        catch (Exception ex)
        {
            flushFailed = true;
            // 持久化失败时，将数据放回缓冲区，避免丢失
            foreach (var metric in metricsToFlush)
            {
                _tokenBuffer.Add(metric);
            }
            Console.WriteLine($"[MetricsCollector] Token 指标持久化失败: {ex.Message}，已保留 {metricsToFlush.Count} 条记录在缓冲区");
        }
    }

    #endregion

    /// <summary>
    /// 获取 CPU 使用率
    /// </summary>
    private async Task<double> GetCpuUsageAsync()
    {
        var process = Process.GetCurrentProcess();
        var startTime = DateTime.UtcNow;
        var startCpuUsage = process.TotalProcessorTime;

        await Task.Delay(500);

        var endTime = DateTime.UtcNow;
        var endCpuUsage = process.TotalProcessorTime;

        var cpuUsedMs = (endCpuUsage - startCpuUsage).TotalMilliseconds;
        var totalMsPassed = (endTime - startTime).TotalMilliseconds;
        var cpuUsageTotal = cpuUsedMs / (Environment.ProcessorCount * totalMsPassed);

        return cpuUsageTotal * 100;
    }

    /// <summary>
    /// 释放资源
    /// </summary>
    public void Dispose()
    {
        _flushTimer?.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// 系统指标
/// </summary>
public class SystemMetrics
{
    public DateTime Timestamp { get; set; }
    public double CpuUsagePercent { get; set; }
    public long MemoryUsageMB { get; set; }
    public int ThreadCount { get; set; }
    public int HandleCount { get; set; }
    public TimeSpan Uptime { get; set; }
}

/// <summary>
/// 索引指标
/// </summary>
public class IndexMetrics
{
    public DateTime Timestamp { get; set; }
    public int TotalFiles { get; set; }
    public long TotalSize { get; set; }
    public int TotalChunks { get; set; }
    public long TotalTokens { get; set; }
    public DateTime? OldestIndex { get; set; }
    public DateTime? NewestIndex { get; set; }
}

/// <summary>
/// 指标值
/// </summary>
public class MetricValue
{
    public string Name { get; set; } = string.Empty;
    public MetricType Type { get; set; }
    public object Value { get; set; } = 0;
    public object? Values { get; set; }
}

/// <summary>
/// 指标类型
/// </summary>
public enum MetricType
{
    Counter,
    Gauge,
    Histogram
}

/// <summary>
/// 指标摘要
/// </summary>
public class MetricsSummary
{
    public long TotalQueries { get; set; }
    public double AvgQueryLatencyMs { get; set; }
    public double P95QueryLatencyMs { get; set; }
    public double P99QueryLatencyMs { get; set; }
    public double AvgResultsPerQuery { get; set; }
}
