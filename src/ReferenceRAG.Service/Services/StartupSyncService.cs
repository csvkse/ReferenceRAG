using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;

namespace ReferenceRAG.Service.Services;

/// <summary>
/// 启动同步服务 - 服务启动时检测文件变更并同步
/// </summary>
public class StartupSyncService : IHostedService
{
    private readonly IVectorStore _vectorStore;
    private readonly ConfigManager _configManager;
    private readonly IndexService _indexService;
    private readonly ILogger<StartupSyncService> _logger;

    // 分批处理常量
    private const int BatchSize = 5000;
    private const int BatchDelayMs = 50;

    /// <summary>
    /// 同步完成事件
    /// </summary>
    public event EventHandler<StartupSyncCompletedEventArgs>? SyncCompleted;

    /// <summary>
    /// 同步进度事件
    /// </summary>
    public event EventHandler<StartupSyncProgressEventArgs>? SyncProgress;

    /// <summary>
    /// 同步结果
    /// </summary>
    public StartupSyncResult? LastSyncResult { get; private set; }

    public StartupSyncService(
        IVectorStore vectorStore,
        ConfigManager configManager,
        IndexService indexService,
        ILogger<StartupSyncService> logger)
    {
        _vectorStore = vectorStore;
        _configManager = configManager;
        _indexService = indexService;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("启动同步服务初始化...");

        // 后台执行同步，不阻塞服务启动
        _ = Task.Run(async () =>
        {
            try
            {
                await ExecuteSyncAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "启动同步失败");
            }
        }, cancellationToken);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("启动同步服务停止");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 执行启动同步
    /// </summary>
    private async Task ExecuteSyncAsync(CancellationToken cancellationToken)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = new StartupSyncResult
        {
            StartTime = DateTime.UtcNow
        };

        try
        {
            var config = _configManager.Load();
            var enabledSources = config.Sources.Where(s => s.Enabled).ToList();
            var allSourcePaths = config.Sources.Select(s => PathUtility.NormalizePath(s.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (enabledSources.Count == 0)
            {
                _logger.LogWarning("没有配置启用的源文件夹，跳过启动同步");
                result.Success = true;
                result.Message = "没有配置启用的源文件夹";
                LastSyncResult = result;
                return;
            }

            // 1. 获取向量库中的所有文件记录
            _logger.LogInformation("正在加载向量库文件记录...");
            var storedFilesDict = await LoadSnapshotToHashMapAsync(cancellationToken);

            _logger.LogInformation("向量库中共有 {Count} 个文件记录", storedFilesDict.Count);

            // 2. 清除不存在源的文件数据（配置中已删除的源）
            await CleanOrphanedSourceFilesAsync(storedFilesDict, allSourcePaths, result, cancellationToken);

            // 3. 收集磁盘上的所有文件
            var diskFiles = new HashSet<string>();
            var enabledSourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in enabledSources)
            {
                var normalizedPath = PathUtility.NormalizePath(source.Path);
                enabledSourcePaths.Add(normalizedPath);
                if (!Directory.Exists(normalizedPath))
                {
                    _logger.LogWarning("源目录不存在或无法访问: {Path}", normalizedPath);
                    continue;
                }

                var files = Directory.GetFiles(normalizedPath, "*.*", SearchOption.AllDirectories)
                    .Where(f => source.FilePatterns.Any(p => MatchesPattern(f, p)))
                    .Where(f => !source.ExcludeDirs.Any(d => f.Contains(d)));

                foreach (var file in files)
                {
                    diskFiles.Add(file);
                }
            }

            _logger.LogInformation("磁盘上共有 {Count} 个文件", diskFiles.Count);

            // 4. 删除检测：磁盘已不存在的文件（但保留已禁用源的索引）
            await ProcessDeletedFilesAsync(storedFilesDict, diskFiles, enabledSourcePaths, result, cancellationToken);

            // 5. 新增检测：快照中不存在的文件
            await ProcessNewFilesAsync(storedFilesDict, diskFiles, result, cancellationToken);

            // 6. 修改检测：mtime 晚于 IndexedAt 的文件
            await ProcessModifiedFilesAsync(storedFilesDict, diskFiles, result, cancellationToken);

            // 7. 触发增量索引：新增 + 修改文件
            if (result.NewFiles.Count > 0 || result.ModifiedFiles.Count > 0)
            {
                _logger.LogInformation(
                    "检测到 {NewCount} 个新增文件, {ModCount} 个修改文件，启动增量索引",
                    result.NewFiles.Count, result.ModifiedFiles.Count);

                // 只对有变更文件的源触发索引，避免扫描未变更的源目录
                var changedPaths = result.NewFiles.Concat(result.ModifiedFiles).ToList();
                var affectedSourceNames = enabledSources
                    .Where(s => changedPaths.Any(p =>
                        p.StartsWith(PathUtility.NormalizePath(s.Path), StringComparison.OrdinalIgnoreCase)))
                    .Select(s => s.Name)
                    .ToList();

                if (affectedSourceNames.Count > 0)
                {
                    await _indexService.StartIndexAsync(new IndexRequest
                    {
                        Sources = affectedSourceNames
                    });
                }
            }

            sw.Stop();
            result.EndTime = DateTime.UtcNow;
            result.Duration = sw.Elapsed;
            result.Success = true;

            _logger.LogInformation(
                "启动同步完成: 清理孤立源索引 {OrphanedCount} 个, 删除文件索引 {DeletedCount} 个, 新增 {NewCount} 个文件, 修改 {ModifiedCount} 个文件, 耗时 {Duration}ms",
                result.OrphanedSourceFiles.Count,
                result.DeletedFiles.Count,
                result.NewFiles.Count,
                result.ModifiedFiles.Count,
                sw.ElapsedMilliseconds);

            LastSyncResult = result;
            SyncCompleted?.Invoke(this, new StartupSyncCompletedEventArgs { Result = result });
        }
        catch (Exception ex)
        {
            sw.Stop();
            result.Success = false;
            result.ErrorMessage = ex.Message;
            result.EndTime = DateTime.UtcNow;
            result.Duration = sw.Elapsed;

            _logger.LogError(ex, "启动同步失败");
            LastSyncResult = result;
        }
    }

    /// <summary>
    /// 加载快照到 HashMap（使用流式游标避免全量加载）
    /// </summary>
    private async Task<Dictionary<string, FileRecord>> LoadSnapshotToHashMapAsync(CancellationToken ct)
    {
        var dict = new Dictionary<string, FileRecord>(StringComparer.OrdinalIgnoreCase);
        await foreach (var file in await _vectorStore.StreamAllFilesAsync())
        {
            dict[file.Path] = file;
        }
        return dict;
    }

    /// <summary>
    /// 清理不存在源的文件索引信息（配置中已删除的源）
    /// 注意：仅删除向量库中的文件记录和索引，不删除磁盘上的真实文件
    /// </summary>
    private async Task CleanOrphanedSourceFilesAsync(
        Dictionary<string, FileRecord> storedFilesDict,
        HashSet<string> allConfigSourcePaths,
        StartupSyncResult result,
        CancellationToken cancellationToken)
    {
        // 找出属于不存在源的文件
        var orphanedFiles = storedFilesDict.Values
            .Where(f => !string.IsNullOrEmpty(f.Source))
            .Where(f => !allConfigSourcePaths.Any(sourcePath =>
                f.Path.StartsWith(sourcePath, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (orphanedFiles.Count == 0)
        {
            _logger.LogDebug("没有发现孤立源文件索引");
            return;
        }

        _logger.LogInformation("检测到 {Count} 个属于已删除源的文件索引，开始清理索引信息", orphanedFiles.Count);

        var batchSize = 100;
        var processedCount = 0;

        foreach (var file in orphanedFiles)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            try
            {
                await _vectorStore.DeleteFileAsync(file.Id, cancellationToken);
                result.OrphanedSourceFiles.Add(file.Path);
                processedCount++;

                _logger.LogDebug("已清理孤立源文件索引信息: {FilePath} (源: {Source})", file.Path, file.Source);

                // 批量处理后让出CPU
                if (processedCount % batchSize == 0)
                {
                    await Task.Delay(BatchDelayMs, cancellationToken);
                }

                SyncProgress?.Invoke(this, new StartupSyncProgressEventArgs
                {
                    Phase = "清理孤立源索引",
                    CurrentFile = file.Path,
                    ProcessedCount = processedCount,
                    TotalCount = orphanedFiles.Count
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "清理孤立源文件索引失败: {FilePath}", file.Path);
                result.Errors.Add($"清理孤立源文件索引失败: {file.Path} - {ex.Message}");
            }
        }

        _logger.LogInformation("已清理 {Count} 个孤立源文件的索引信息", result.OrphanedSourceFiles.Count);
    }

    /// <summary>
    /// 处理已删除的文件
    /// </summary>
    private async Task ProcessDeletedFilesAsync(
        Dictionary<string, FileRecord> storedFilesDict,
        HashSet<string> diskFiles,
        HashSet<string> enabledSourcePaths,
        StartupSyncResult result,
        CancellationToken cancellationToken)
    {
        var deletedFiles = storedFilesDict.Keys
            .Where(storedPath => !diskFiles.Contains(storedPath))
            .ToList();

        // 统计因源已禁用而跳过的文件数
        var skippedDueToDisabledSource = 0;

        _logger.LogInformation("检测到 {Count} 个已删除的文件", deletedFiles.Count);

        foreach (var filePath in deletedFiles)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            try
            {
                var fileRecord = storedFilesDict[filePath];

                // 检查文件所属源是否仍启用
                // 如果源已禁用，跳过删除（保留索引以便重新启用时恢复）
                var sourceEnabled = enabledSourcePaths.Any(enabledPath =>
                    filePath.StartsWith(enabledPath, StringComparison.OrdinalIgnoreCase));

                if (!sourceEnabled)
                {
                    // 源已禁用，不删除索引
                    skippedDueToDisabledSource++;
                    _logger.LogDebug("跳过已禁用源的索引删除: {FilePath}", filePath);
                    continue;
                }

                await _vectorStore.DeleteFileAsync(fileRecord.Id, cancellationToken);
                result.DeletedFiles.Add(filePath);

                _logger.LogDebug("已删除文件索引: {FilePath}", filePath);

                SyncProgress?.Invoke(this, new StartupSyncProgressEventArgs
                {
                    Phase = "删除检测",
                    CurrentFile = filePath,
                    ProcessedCount = result.DeletedFiles.Count,
                    TotalCount = deletedFiles.Count
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "删除文件索引失败: {FilePath}", filePath);
                result.Errors.Add($"删除失败: {filePath} - {ex.Message}");
            }
        }

        if (skippedDueToDisabledSource > 0)
        {
            _logger.LogInformation("因源已禁用而跳过删除 {Count} 个文件的索引", skippedDueToDisabledSource);
        }
    }

    /// <summary>
    /// 处理新增的文件
    /// </summary>
    private async Task ProcessNewFilesAsync(
        Dictionary<string, FileRecord> storedFilesDict,
        HashSet<string> diskFiles,
        StartupSyncResult result,
        CancellationToken cancellationToken)
    {
        var newFiles = diskFiles
            .Where(diskPath => !storedFilesDict.ContainsKey(diskPath))
            .ToList();

        _logger.LogInformation("检测到 {Count} 个新增的文件", newFiles.Count);

        var newFileCount = 0;
        foreach (var filePath in newFiles)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            result.NewFiles.Add(filePath);
            newFileCount++;

            // 每处理 batchSize 个文件后让出CPU
            if (newFileCount % BatchSize == 0)
            {
                await Task.Delay(BatchDelayMs, cancellationToken);
            }

            SyncProgress?.Invoke(this, new StartupSyncProgressEventArgs
            {
                Phase = "新增检测",
                CurrentFile = filePath,
                ProcessedCount = result.NewFiles.Count,
                TotalCount = newFiles.Count
            });
        }

        // 如果有新增文件，启动索引任务
        if (newFiles.Count > 0)
        {
            _logger.LogInformation("检测到 {Count} 个新增文件需要索引", newFiles.Count);
        }
    }

    /// <summary>
    /// 处理修改的文件（使用 mtime 粗筛）
    /// </summary>
    private async Task ProcessModifiedFilesAsync(
        Dictionary<string, FileRecord> storedFilesDict,
        HashSet<string> diskFiles,
        StartupSyncResult result,
        CancellationToken cancellationToken)
    {
        var modifiedFiles = new List<string>();

        // 筛选出磁盘上存在且在向量库中也有记录的文件
        var commonFiles = diskFiles.Intersect(storedFilesDict.Keys).ToList();

        foreach (var filePath in commonFiles)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            try
            {
                var fileRecord = storedFilesDict[filePath];
                var diskMtime = File.GetLastWriteTimeUtc(filePath);

                // 使用 mtime 粗筛：磁盘文件的修改时间晚于向量库中的索引时间
                // 添加1秒容差，避免文件系统时间精度问题
                if (diskMtime > fileRecord.IndexedAt.AddSeconds(1))
                {
                    modifiedFiles.Add(filePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "检查文件修改时间失败: {FilePath}", filePath);
            }
        }

        _logger.LogInformation("检测到 {Count} 个修改的文件（基于 mtime 粗筛）", modifiedFiles.Count);

        var modifiedFileCount = 0;
        foreach (var filePath in modifiedFiles)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            result.ModifiedFiles.Add(filePath);
            modifiedFileCount++;

            // 每处理 batchSize 个文件后让出CPU
            if (modifiedFileCount % BatchSize == 0)
            {
                await Task.Delay(BatchDelayMs, cancellationToken);
            }

            SyncProgress?.Invoke(this, new StartupSyncProgressEventArgs
            {
                Phase = "修改检测",
                CurrentFile = filePath,
                ProcessedCount = result.ModifiedFiles.Count,
                TotalCount = modifiedFiles.Count
            });
        }

        // 如果有修改文件，启动索引任务
        if (modifiedFiles.Count > 0)
        {
            _logger.LogInformation("检测到 {Count} 个修改文件需要重新索引", modifiedFiles.Count);
        }
    }

    /// <summary>
    /// 匹配文件模式
    /// </summary>
    private static bool MatchesPattern(string filePath, string pattern)
    {
        if (pattern.StartsWith("*."))
        {
            return filePath.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase);
        }
        return filePath.Contains(pattern, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// 启动同步结果
/// </summary>
public class StartupSyncResult
{
    /// <summary>
    /// 是否成功
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// 开始时间
    /// </summary>
    public DateTime StartTime { get; set; }

    /// <summary>
    /// 结束时间
    /// </summary>
    public DateTime? EndTime { get; set; }

    /// <summary>
    /// 耗时
    /// </summary>
    public TimeSpan Duration { get; set; }

    /// <summary>
    /// 错误消息
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// 消息
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// 已删除的文件列表
    /// </summary>
    public List<string> DeletedFiles { get; set; } = [];

    /// <summary>
    /// 新增的文件列表
    /// </summary>
    public List<string> NewFiles { get; set; } = [];

    /// <summary>
    /// 修改的文件列表
    /// </summary>
    public List<string> ModifiedFiles { get; set; } = [];

    /// <summary>
    /// 孤立源文件索引列表（源已从配置中删除，清理了索引信息但不删除磁盘文件）
    /// </summary>
    public List<string> OrphanedSourceFiles { get; set; } = [];

    /// <summary>
    /// 错误列表
    /// </summary>
    public List<string> Errors { get; set; } = [];

    /// <summary>
    /// 总变更数
    /// </summary>
    public int TotalChanges => OrphanedSourceFiles.Count + DeletedFiles.Count + NewFiles.Count + ModifiedFiles.Count;
}

/// <summary>
/// 启动同步完成事件参数
/// </summary>
public class StartupSyncCompletedEventArgs : EventArgs
{
    public StartupSyncResult Result { get; set; } = new();
}

/// <summary>
/// 启动同步进度事件参数
/// </summary>
public class StartupSyncProgressEventArgs : EventArgs
{
    /// <summary>
    /// 当前阶段
    /// </summary>
    public string Phase { get; set; } = string.Empty;

    /// <summary>
    /// 当前处理的文件
    /// </summary>
    public string CurrentFile { get; set; } = string.Empty;

    /// <summary>
    /// 已处理数量
    /// </summary>
    public int ProcessedCount { get; set; }

    /// <summary>
    /// 总数量
    /// </summary>
    public int TotalCount { get; set; }

    /// <summary>
    /// 进度百分比
    /// </summary>
    public double ProgressPercent => TotalCount > 0 ? (double)ProcessedCount / TotalCount * 100 : 0;
}
