namespace ReferenceRAG.Core.Interfaces;

/// <summary>
/// GPU 显存信息
/// </summary>
public class GpuMemoryInfo
{
    public int DeviceId { get; set; }
    public long FreeMB { get; set; }
    public long UsedMB { get; set; }
    public long TotalMB { get; set; }
    public int GpuUtilization { get; set; }
    public int Temperature { get; set; }
    public DateTime Timestamp { get; set; }

    public double UsagePercent => TotalMB > 0 ? (double)UsedMB / TotalMB * 100 : 0;

    /// <summary>
    /// 创建未知状态的显存信息
    /// </summary>
    public static GpuMemoryInfo Unknown(int deviceId = 0) => new()
    {
        DeviceId = deviceId,
        FreeMB = -1,
        UsedMB = -1,
        TotalMB = -1,
        GpuUtilization = -1,
        Temperature = -1,
        Timestamp = DateTime.UtcNow
    };
}

/// <summary>
/// Session 状态
/// </summary>
public class SessionState
{
    public string Name { get; set; } = "";
    public int DeviceId { get; set; }
    public bool IsActive { get; set; }
    public int ActiveInferenceCount { get; set; }
    public DateTime LastActivityTime { get; set; }
    public TimeSpan IdleTime => DateTime.UtcNow - LastActivityTime;
    public bool HasPendingShrink { get; set; }
}

/// <summary>
/// GPU 显存管理器接口
/// </summary>
public interface IGpuMemoryManager
{
    /// <summary>
    /// 注册 ONNX Session
    /// </summary>
    /// <param name="name">Session 名称标识</param>
    /// <param name="getSession">获取 Session 的委托（弱引用）</param>
    /// <param name="deviceId">GPU 设备 ID</param>
    void Register(string name, Func<Microsoft.ML.OnnxRuntime.InferenceSession?> getSession, int deviceId = 0);

    /// <summary>
    /// 注销 Session
    /// </summary>
    void Unregister(string name);

    /// <summary>
    /// 进入推理（增加活动计数）
    /// </summary>
    void EnterInference(string name);

    /// <summary>
    /// 退出推理（减少计数，可能触发延迟的释放）
    /// </summary>
    void ExitInference(string name);

    /// <summary>
    /// 获取显存信息
    /// </summary>
    GpuMemoryInfo GetMemoryInfo(int deviceId = 0);

    /// <summary>
    /// 手动触发释放
    /// </summary>
    Task ShrinkAsync(string? name = null);

    /// <summary>
    /// 获取所有注册的 Session 状态
    /// </summary>
    IReadOnlyDictionary<string, SessionState> GetSessionStates();
}
