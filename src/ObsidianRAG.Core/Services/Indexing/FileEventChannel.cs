using System.Threading.Channels;
using ObsidianRAG.Core.Models;

namespace ObsidianRAG.Core.Services.Indexing;

/// <summary>
/// 文件事件共享通道：基于 System.Threading.Channels 的生产者-消费者模式
/// 用于 FileMonitorService（生产者）与 IndexingPipeline（消费者）之间的解耦通信
/// </summary>
public class FileEventChannel : IDisposable
{
    private readonly Channel<FileChangeEvent> _channel;
    private readonly CancellationTokenSource _cts;
    private bool _disposed;

    /// <summary>
    /// 通道容量（超过容量时写入将阻塞）
    /// </summary>
    public int Capacity { get; }

    /// <summary>
    /// 当前通道中的事件数量
    /// </summary>
    public int Count => _channel.Reader.Count;

    /// <summary>
    /// 通道是否已完成
    /// </summary>
    public bool IsCompleted => _channel.Reader.Completion.IsCompleted;

    /// <summary>
    /// 创建文件事件通道
    /// </summary>
    /// <param name="capacity">通道容量，默认 1000</param>
    public FileEventChannel(int capacity = 1000)
    {
        Capacity = capacity;
        _cts = new CancellationTokenSource();
        
        var options = new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,  // 容量满时等待
            SingleReader = false,  // 支持多消费者
            SingleWriter = false   // 支持多生产者
        };
        
        _channel = Channel.CreateBounded<FileChangeEvent>(options);
    }

    /// <summary>
    /// 写入文件变更事件（异步）
    /// </summary>
    /// <param name="fileEvent">文件变更事件</param>
    /// <param name="cancellationToken">取消令牌</param>
    public async ValueTask WriteAsync(FileChangeEvent fileEvent, CancellationToken cancellationToken = default)
    {
        await _channel.Writer.WriteAsync(fileEvent, cancellationToken);
    }

    /// <summary>
    /// 尝试写入文件变更事件（非阻塞）
    /// </summary>
    /// <param name="fileEvent">文件变更事件</param>
    /// <returns>true 表示写入成功</returns>
    public bool TryWrite(FileChangeEvent fileEvent)
    {
        return _channel.Writer.TryWrite(fileEvent);
    }

    /// <summary>
    /// 读取文件变更事件（异步）
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>文件变更事件</returns>
    public async ValueTask<FileChangeEvent> ReadAsync(CancellationToken cancellationToken = default)
    {
        return await _channel.Reader.ReadAsync(cancellationToken);
    }

    /// <summary>
    /// 尝试读取文件变更事件（非阻塞）
    /// </summary>
    /// <param name="fileEvent">输出的文件变更事件</param>
    /// <returns>true 表示读取成功</returns>
    public bool TryRead(out FileChangeEvent? fileEvent)
    {
        return _channel.Reader.TryRead(out fileEvent);
    }

    /// <summary>
    /// 读取所有可用事件（非阻塞批量读取）
    /// </summary>
    /// <returns>事件列表</returns>
    public List<FileChangeEvent> ReadAllAvailable()
    {
        var events = new List<FileChangeEvent>();
        while (_channel.Reader.TryRead(out var fileEvent))
        {
            events.Add(fileEvent);
        }
        return events;
    }

    /// <summary>
    /// 获取读取器（用于 foreach 异步迭代）
    /// </summary>
    public ChannelReader<FileChangeEvent> Reader => _channel.Reader;

    /// <summary>
    /// 获取写入器
    /// </summary>
    public ChannelWriter<FileChangeEvent> Writer => _channel.Writer;

    /// <summary>
    /// 完成通道（不再接受新事件）
    /// </summary>
    public void Complete()
    {
        _channel.Writer.Complete();
    }

    /// <summary>
    /// 取消通道操作
    /// </summary>
    public void Cancel()
    {
        _cts.Cancel();
    }

    /// <summary>
    /// 等待通道处理完成
    /// </summary>
    public async Task WaitToReadAsync(CancellationToken cancellationToken = default)
    {
        await _channel.Reader.WaitToReadAsync(cancellationToken);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _cts.Cancel();
            _channel.Writer.TryComplete();
            _cts.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// 文件变更事件：轻量级事件模型
/// </summary>
public class FileChangeEvent
{
    /// <summary>
    /// 文件路径
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// 旧文件路径（仅 Rename 时有效）
    /// </summary>
    public string? OldFilePath { get; set; }

    /// <summary>
    /// 变更类型
    /// </summary>
    public FileChangeKind ChangeKind { get; set; }

    /// <summary>
    /// 事件时间戳
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// 数据源标识
    /// </summary>
    public string? Source { get; set; }

    /// <summary>
    /// 从 FileChangeEventArgs 转换
    /// </summary>
    public static FileChangeEvent FromEventArgs(FileChangeEventArgs args)
    {
        return new FileChangeEvent
        {
            FilePath = args.FilePath,
            OldFilePath = args.OldFilePath,
            ChangeKind = (FileChangeKind)args.ChangeType,
            Timestamp = args.Timestamp,
            Source = args.Source
        };
    }

    /// <summary>
    /// 转换为 FileChangeEventArgs
    /// </summary>
    public FileChangeEventArgs ToEventArgs()
    {
        return new FileChangeEventArgs
        {
            FilePath = FilePath,
            OldFilePath = OldFilePath,
            ChangeType = (ChangeType)ChangeKind,
            Timestamp = Timestamp,
            Source = Source
        };
    }
}

/// <summary>
/// 文件变更类型枚举：与 ChangeType 保持一致
/// </summary>
public enum FileChangeKind
{
    /// <summary>
    /// 文件创建
    /// </summary>
    Created = 0,

    /// <summary>
    /// 文件修改
    /// </summary>
    Modified = 1,

    /// <summary>
    /// 文件删除
    /// </summary>
    Deleted = 2,

    /// <summary>
    /// 文件重命名
    /// </summary>
    Renamed = 3
}
