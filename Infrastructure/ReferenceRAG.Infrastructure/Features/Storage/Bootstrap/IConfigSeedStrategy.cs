namespace ReferenceRAG.Infrastructure.Features.Storage.Bootstrap;

/// <summary>
/// 配置文件种子生成策略接口
/// </summary>
public interface IConfigSeedStrategy
{
    /// <summary>
    /// 策略名称
    /// </summary>
    string Name { get; }

    /// <summary>
    /// 优先级（数值越小越优先执行）
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// 是否满足生成条件
    /// </summary>
    bool CanSeed(IConfigEnvironmentAdapter adapter);

    /// <summary>
    /// 生成初始配置 JSON 内容
    /// </summary>
    string GenerateConfigJson(IConfigEnvironmentAdapter adapter);
}
