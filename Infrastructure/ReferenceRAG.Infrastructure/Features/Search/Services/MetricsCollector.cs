using System.Diagnostics;
using System.Collections.Concurrent;
using System.IO;
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

    // CPU 采样相关字段 (非阻塞差分采样)
    private readonly object _cpuLock = new();
    private DateTime _lastCpuSampleTime = DateTime.UtcNow;
    private TimeSpan _lastCpuTotalProcessorTime = Process.GetCurrentProcess().TotalProcessorTime;
    private double _lastCpuUsagePercent = 0.0;

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
    /// 采集系统指标（同步无阻塞）
    /// </summary>
    public SystemMetrics CollectSystemMetrics()
    {
        var process = Process.GetCurrentProcess();
        var cpuUsage = GetCpuUsage();
        var memoryUsedBytes = process.WorkingSet64;
        var memoryTotalBytes = GetTotalMemoryBytes(memoryUsedBytes);
        var (diskTotalBytes, diskFreeBytes, diskUsedBytes) = GetStorageMetrics();
        var uptime = DateTime.UtcNow - process.StartTime.ToUniversalTime();

        return new SystemMetrics
        {
            Timestamp = DateTime.UtcNow,
            CpuUsagePercent = cpuUsage,
            MemoryUsageMB = memoryUsedBytes / 1024 / 1024,
            MemoryUsedBytes = memoryUsedBytes,
            MemoryTotalBytes = memoryTotalBytes,
            DiskUsedBytes = diskUsedBytes,
            DiskTotalBytes = diskTotalBytes,
            DiskFreeBytes = diskFreeBytes,
            ThreadCount = process.Threads.Count,
            HandleCount = process.HandleCount,
            Uptime = uptime,
            UptimeSeconds = (long)Math.Max(0, uptime.TotalSeconds)
        };
    }

    /// <summary>
    /// 采集系统指标（异步兼容方法）
    /// </summary>
    public Task<SystemMetrics> CollectSystemMetricsAsync()
    {
        return Task.FromResult(CollectSystemMetrics());
    }

    /// <summary>
    /// 采集索引指标
    /// </summary>
    public async Task<IndexMetrics> CollectIndexMetricsAsync()
    {
        var fileStream = await _vectorStore.StreamAllFilesAsync();

        int totalFiles = 0;
        long totalSize = 0;
        int totalChunks = 0;
        long totalTokens = 0;
        DateTime? oldestIndex = null;
        DateTime? newestIndex = null;

        await foreach (var file in fileStream)
        {
            totalFiles++;
            totalSize += file.ContentLength;
            totalChunks += file.ChunkCount;
            totalTokens += file.TotalTokens;

            if (oldestIndex == null || file.IndexedAt < oldestIndex)
            {
                oldestIndex = file.IndexedAt;
            }

            if (newestIndex == null || file.IndexedAt > newestIndex)
            {
                newestIndex = file.IndexedAt;
            }
        }

        var metrics = new IndexMetrics
        {
            Timestamp = DateTime.UtcNow,
            TotalFiles = totalFiles,
            TotalSize = totalSize,
            TotalChunks = totalChunks,
            TotalTokens = totalTokens,
            OldestIndex = oldestIndex,
            NewestIndex = newestIndex
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
    /// 获取 CPU 使用率（基于滑动窗口差分计算，无阻塞毫秒延迟）
    /// </summary>
    public double GetCpuUsage()
    {
        var now = DateTime.UtcNow;
        var elapsed = now - _lastCpuSampleTime;

        // 若距离上次采样不足 250ms，直接复用缓存值，避免微小时间差导致的抖动与无谓锁竞争
        if (elapsed.TotalMilliseconds < 250)
        {
            return _lastCpuUsagePercent;
        }

        lock (_cpuLock)
        {
            now = DateTime.UtcNow;
            elapsed = now - _lastCpuSampleTime;
            if (elapsed.TotalMilliseconds < 250)
            {
                return _lastCpuUsagePercent;
            }

            try
            {
                var process = Process.GetCurrentProcess();
                var currentTotalTime = process.TotalProcessorTime;
                var cpuUsedMs = (currentTotalTime - _lastCpuTotalProcessorTime).TotalMilliseconds;
                var totalMsPassed = elapsed.TotalMilliseconds;

                if (totalMsPassed > 0)
                {
                    var cpuUsage = (cpuUsedMs / (Environment.ProcessorCount * totalMsPassed)) * 100.0;
                    _lastCpuUsagePercent = Math.Clamp(Math.Round(cpuUsage, 1), 0.0, 100.0);
                    _lastCpuTotalProcessorTime = currentTotalTime;
                    _lastCpuSampleTime = now;
                }
            }
            catch
            {
                // 忽略瞬态进程探测异常，复用上一次采样值
            }

            return _lastCpuUsagePercent;
        }
    }

    /// <summary>
    /// 获取 CPU 使用率（异步兼容包装）
    /// </summary>
    private Task<double> GetCpuUsageAsync()
    {
        return Task.FromResult(GetCpuUsage());
    }

    /// <summary>
    /// 获取系统可用总内存（容器感知与物理机兼容）
    /// </summary>
    public static long GetTotalMemoryBytes(long currentWorkingSetBytes = 0)
    {
        long totalMemoryBytes = 0;

        // 1. .NET 8/9/10 BCL 容器原生感知接口（自动识别 cgroups v1 与 v2 内存上限）
        try
        {
            var gcInfo = GC.GetGCMemoryInfo();
            totalMemoryBytes = gcInfo.TotalAvailableMemoryBytes;
        }
        catch
        {
            // 忽略 GC 接口异常
        }

        // 2. Linux 平台兜底：读取 /proc/meminfo 的 MemTotal
        if (totalMemoryBytes <= 0 && OperatingSystem.IsLinux() && File.Exists("/proc/meminfo"))
        {
            try
            {
                foreach (var line in File.ReadLines("/proc/meminfo"))
                {
                    if (line.StartsWith("MemTotal:", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2 && long.TryParse(parts[1], out var kb))
                        {
                            totalMemoryBytes = kb * 1024L;
                            break;
                        }
                    }
                }
            }
            catch
            {
                // 忽略文件读取异常
            }
        }

        // 3. 兜底策略：若仍无法探测，以当前工作集或 1GB 为下限保护
        if (totalMemoryBytes <= 0)
        {
            totalMemoryBytes = Math.Max(currentWorkingSetBytes, 1024L * 1024L * 1024L);
        }

        return totalMemoryBytes;
    }

    /// <summary>
    /// 解析数据存储所在磁盘或挂载点的容量指标
    /// </summary>
    public static (long totalBytes, long freeBytes, long usedBytes) GetStorageMetrics(string? targetPath = null)
    {
        try
        {
            var resolvedPath = ResolveTargetPath(targetPath);
            DriveInfo? bestMatch = null;
            int bestMatchLength = -1;

            DriveInfo[] drives;
            try
            {
                drives = DriveInfo.GetDrives();
            }
            catch
            {
                drives = Array.Empty<DriveInfo>();
            }

            foreach (var drive in drives)
            {
                try
                {
                    if (!drive.IsReady) continue;
                    var root = drive.RootDirectory.FullName;

                    var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    var normalizedPath = resolvedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                    if (string.IsNullOrEmpty(normalizedRoot))
                    {
                        if (bestMatchLength < 1)
                        {
                            bestMatch = drive;
                            bestMatchLength = 1;
                        }
                    }
                    else if (normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        if (normalizedRoot.Length > bestMatchLength)
                        {
                            bestMatch = drive;
                            bestMatchLength = normalizedRoot.Length;
                        }
                    }
                }
                catch
                {
                    // 忽略无权限或已卸载的驱动器
                }
            }

            if (bestMatch != null)
            {
                var total = bestMatch.TotalSize;
                var free = bestMatch.AvailableFreeSpace;
                var used = Math.Max(0L, total - free);
                return (total, free, used);
            }

            // 兜底：通过 Path.GetPathRoot 构造 DriveInfo
            var pathRoot = Path.GetPathRoot(resolvedPath);
            if (!string.IsNullOrEmpty(pathRoot))
            {
                var fallbackDrive = new DriveInfo(pathRoot);
                if (fallbackDrive.IsReady)
                {
                    var total = fallbackDrive.TotalSize;
                    var free = fallbackDrive.AvailableFreeSpace;
                    var used = Math.Max(0L, total - free);
                    return (total, free, used);
                }
            }
        }
        catch
        {
            // 忽略磁盘探测异常，返回零值保底
        }

        return (0, 0, 0);
    }

    private static string ResolveTargetPath(string? targetPath)
    {
        if (!string.IsNullOrWhiteSpace(targetPath))
        {
            return Path.GetFullPath(targetPath);
        }

        // 依次从环境变量解析配置路径或数据目录
        var explicitConfigPath = Environment.GetEnvironmentVariable("REFERENCERAG_CONFIG_PATH");
        if (!string.IsNullOrWhiteSpace(explicitConfigPath))
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(explicitConfigPath));
            if (!string.IsNullOrEmpty(dir)) return dir;
        }

        var baseDir = Environment.GetEnvironmentVariable("REFERENCERAG_CONTENT_ROOT") ?? Directory.GetCurrentDirectory();
        var dataDir = Environment.GetEnvironmentVariable("REFERENCERAG_DATA_PATH")
                      ?? Environment.GetEnvironmentVariable("DataPath")
                      ?? Environment.GetEnvironmentVariable("OBSIDIAN_RAG_DATA_PATH")
                      ?? Path.Combine(baseDir, "data");

        return Path.IsPathRooted(dataDir) ? Path.GetFullPath(dataDir) : Path.GetFullPath(Path.Combine(baseDir, dataDir));
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

    // 内存指标
    public long MemoryUsageMB { get; set; }
    public long MemoryUsedBytes { get; set; }
    public long MemoryTotalBytes { get; set; }

    // 磁盘指标
    public long DiskUsedBytes { get; set; }
    public long DiskTotalBytes { get; set; }
    public long DiskFreeBytes { get; set; }

    // 运行时与进程指标
    public int ThreadCount { get; set; }
    public int HandleCount { get; set; }
    public TimeSpan Uptime { get; set; }
    public long UptimeSeconds { get; set; }
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
