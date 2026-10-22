namespace ReferenceRAG.ApiHost.Authentication;

/// <summary>
/// 密钥解析策略接口（策略模式）
/// </summary>
public interface IKeyResolutionStrategy
{
    /// <summary>
    /// 优先级序号（数值越小优先级越高，1 为最高优先级）
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// 策略标识来源
    /// </summary>
    KeyResolutionSource Source { get; }

    /// <summary>
    /// 尝试提取密钥
    /// </summary>
    /// <returns>若本策略存在非空有效密钥返回密钥字符串，否则返回 null</returns>
    string? TryResolveKey();
}
