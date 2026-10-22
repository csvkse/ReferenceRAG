namespace ReferenceRAG.ApiHost.Authentication;

/// <summary>
/// 密钥解析来源类型
/// </summary>
public enum KeyResolutionSource
{
    /// <summary>
    /// 未配置（认证未启用）
    /// </summary>
    None = 0,

    /// <summary>
    /// 配置文件（obsidian_rag_config.json 或 appsettings.json），最高优先级
    /// </summary>
    ConfigFile = 1,

    /// <summary>
    /// 容器或系统环境变量（REFERENCERAG_API_KEY / API_KEY），次高优先级
    /// </summary>
    EnvironmentVariable = 2
}

/// <summary>
/// 访问密钥解析结果
/// </summary>
public sealed record AuthenticationKeyResolutionResult(
    bool IsEnabled,
    string? ApiKey,
    KeyResolutionSource Source,
    string Details
);

/// <summary>
/// 访问密钥仲裁解析器接口
/// </summary>
public interface IAuthenticationKeyResolver
{
    /// <summary>
    /// 按照“配置文件 > 环境变量”严格优先级解析有效访问密钥
    /// </summary>
    AuthenticationKeyResolutionResult ResolveKey();
}
