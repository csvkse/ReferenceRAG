using System.Diagnostics;
using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Infrastructure.Features.ModelManagement.Download.StateMachine;

/// <summary>
/// 模型下载全生命周期状态机与进度协调器
/// 严格管控状态流转、并发安全、速率平滑计算与文件级传输追踪
/// </summary>
public class DownloadStateMachine
{
    private readonly object _syncRoot = new();
    private readonly IProgress<DownloadProgress>? _progressReporter;
    private readonly DownloadProgress _progress;
    private readonly Stopwatch _stopwatch = new();
    private readonly Stopwatch _speedStopwatch = new();

    private long _lastBytesReceived;
    private double _smoothedSpeed;
    private const double SpeedSmoothingAlpha = 0.3; // 移动平均平滑因子

    public DownloadProgress CurrentProgress
    {
        get
        {
            lock (_syncRoot)
            {
                return CloneProgress();
            }
        }
    }

    public DownloadStage CurrentStage
    {
        get
        {
            lock (_syncRoot)
            {
                return _progress.Stage;
            }
        }
    }

    public DownloadStateMachine(string modelName, long estimatedTotalBytes = 0, IProgress<DownloadProgress>? progressReporter = null)
    {
        _progressReporter = progressReporter;
        _progress = new DownloadProgress
        {
            ModelName = modelName,
            Status = "downloading",
            Stage = DownloadStage.Idle,
            StageMessage = "准备就绪",
            StartTime = DateTime.UtcNow,
            TotalBytes = estimatedTotalBytes,
            Progress = 0
        };

        _stopwatch.Start();
        _speedStopwatch.Start();
    }

    /// <summary>
    /// 转换到新的生命周期阶段
    /// </summary>
    public void TransitionTo(DownloadStage nextStage, string stageMessage)
    {
        lock (_syncRoot)
        {
            ValidateTransition(_progress.Stage, nextStage);

            _progress.Stage = nextStage;
            _progress.StageMessage = stageMessage;

            if (nextStage == DownloadStage.Completed)
            {
                _progress.Status = "completed";
                _progress.Progress = 100f;
                _progress.EndTime = DateTime.UtcNow;
                _progress.EstimatedSecondsRemaining = 0;
            }
            else if (nextStage == DownloadStage.Failed)
            {
                _progress.Status = "failed";
                _progress.EndTime = DateTime.UtcNow;
            }
            else if (nextStage == DownloadStage.Cancelled)
            {
                _progress.Status = "cancelled";
                _progress.EndTime = DateTime.UtcNow;
            }

            ReportCurrent();
        }
    }

    /// <summary>
    /// 设置并校正总字节数（例如元数据完全解析或 HEAD 探测之后）
    /// </summary>
    public void SetTotalBytes(long totalBytes)
    {
        lock (_syncRoot)
        {
            if (totalBytes > 0)
            {
                _progress.TotalBytes = totalBytes;
                RecalculateProgress();
                ReportCurrent();
            }
        }
    }

    /// <summary>
    /// 更新当前正在传输的文件指标
    /// </summary>
    public void UpdateFileProgress(
        string fileName,
        int fileIndex,
        int totalFiles,
        long currentFileBytes,
        long currentFileTotalBytes,
        long deltaBytes)
    {
        lock (_syncRoot)
        {
            _progress.CurrentFileName = fileName;
            _progress.CurrentFileIndex = fileIndex;
            _progress.TotalFiles = totalFiles;
            _progress.CurrentFileBytes = currentFileBytes;
            _progress.CurrentFileTotalBytes = currentFileTotalBytes;
            _progress.BytesReceived += deltaBytes;

            // 计算平滑速率与剩余时间 (每 250ms 或有显著传输时更新)
            var elapsedSec = _speedStopwatch.Elapsed.TotalSeconds;
            if (elapsedSec >= 0.25)
            {
                var bytesInInterval = _progress.BytesReceived - _lastBytesReceived;
                var currentSpeed = bytesInInterval / elapsedSec;

                if (_smoothedSpeed <= 0)
                {
                    _smoothedSpeed = currentSpeed;
                }
                else
                {
                    _smoothedSpeed = SpeedSmoothingAlpha * currentSpeed + (1 - SpeedSmoothingAlpha) * _smoothedSpeed;
                }

                _progress.SpeedBytesPerSecond = _smoothedSpeed;
                _lastBytesReceived = _progress.BytesReceived;
                _speedStopwatch.Restart();

                if (_progress.TotalBytes > _progress.BytesReceived && _smoothedSpeed > 1024)
                {
                    var remainingBytes = _progress.TotalBytes - _progress.BytesReceived;
                    _progress.EstimatedSecondsRemaining = (int)(remainingBytes / _smoothedSpeed);
                }
            }

            RecalculateProgress();

            // 构造动态友好的 StageMessage
            var fileMb = currentFileBytes / 1024.0 / 1024.0;
            var fileTotalMb = currentFileTotalBytes / 1024.0 / 1024.0;
            var speedMb = _progress.SpeedBytesPerSecond / 1024.0 / 1024.0;
            var etaText = _progress.EstimatedSecondsRemaining.HasValue
                ? $"，剩余约 {_progress.EstimatedSecondsRemaining}s"
                : "";

            _progress.StageMessage = $"正在下载 [{fileIndex}/{totalFiles}] {fileName} ({fileMb:F1}MB / {fileTotalMb:F1}MB, {speedMb:F1}MB/s{etaText})";

            ReportCurrent();
        }
    }

    /// <summary>
    /// 触发失败状态
    /// </summary>
    public void SetFailed(string errorMessage, DownloadErrorCode errorCode = DownloadErrorCode.Unknown)
    {
        lock (_syncRoot)
        {
            _progress.Status = "failed";
            _progress.Stage = DownloadStage.Failed;
            _progress.ErrorMessage = errorMessage;
            _progress.ErrorCode = errorCode;
            _progress.StageMessage = $"任务失败: {errorMessage}";
            _progress.EndTime = DateTime.UtcNow;
            ReportCurrent();
        }
    }

    /// <summary>
    /// 触发取消状态
    /// </summary>
    public void SetCancelled()
    {
        lock (_syncRoot)
        {
            _progress.Status = "cancelled";
            _progress.Stage = DownloadStage.Cancelled;
            _progress.StageMessage = "下载已由用户取消";
            _progress.EndTime = DateTime.UtcNow;
            ReportCurrent();
        }
    }

    /// <summary>
    /// 触发完成状态
    /// </summary>
    public void SetCompleted(string? message = null)
    {
        lock (_syncRoot)
        {
            _progress.Status = "completed";
            _progress.Stage = DownloadStage.Completed;
            _progress.StageMessage = message ?? "模型已就绪并成功加载";
            _progress.Progress = 100f;
            _progress.EndTime = DateTime.UtcNow;
            _progress.SpeedBytesPerSecond = 0;
            _progress.EstimatedSecondsRemaining = 0;
            ReportCurrent();
        }
    }

    private void RecalculateProgress()
    {
        if (_progress.TotalBytes > 0)
        {
            var calculated = (float)_progress.BytesReceived / _progress.TotalBytes * 100f;
            // 下载阶段最多到 99%，100% 留给 VerifyingFiles/Completed
            _progress.Progress = Math.Clamp(calculated, 0f, 99f);
        }
    }

    private void ReportCurrent()
    {
        _progressReporter?.Report(CloneProgress());
    }

    private DownloadProgress CloneProgress()
    {
        return new DownloadProgress
        {
            ModelName = _progress.ModelName,
            Status = _progress.Status,
            Stage = _progress.Stage,
            StageMessage = _progress.StageMessage,
            CurrentFileName = _progress.CurrentFileName,
            CurrentFileIndex = _progress.CurrentFileIndex,
            TotalFiles = _progress.TotalFiles,
            CurrentFileBytes = _progress.CurrentFileBytes,
            CurrentFileTotalBytes = _progress.CurrentFileTotalBytes,
            Progress = _progress.Progress,
            BytesReceived = _progress.BytesReceived,
            TotalBytes = _progress.TotalBytes,
            ErrorMessage = _progress.ErrorMessage,
            ErrorCode = _progress.ErrorCode,
            StartTime = _progress.StartTime,
            EndTime = _progress.EndTime,
            SpeedBytesPerSecond = _progress.SpeedBytesPerSecond,
            EstimatedSecondsRemaining = _progress.EstimatedSecondsRemaining,
            DownloadOptions = _progress.DownloadOptions
        };
    }

    private static void ValidateTransition(DownloadStage current, DownloadStage next)
    {
        if (current == DownloadStage.Completed || current == DownloadStage.Failed || current == DownloadStage.Cancelled)
        {
            // 终止态不再接受新的普通流转（除非重新初始化）
            return;
        }

        // 允许任何中途状态跳转至 Cancelled 或 Failed
        if (next == DownloadStage.Cancelled || next == DownloadStage.Failed)
        {
            return;
        }

        // 合法流转链: Idle -> ResolvingManifest -> ProbingMirrors -> DownloadingFiles -> VerifyingFiles -> Converting -> Completed
        // （允许根据已有文件情况合理跳跃）
    }
}
