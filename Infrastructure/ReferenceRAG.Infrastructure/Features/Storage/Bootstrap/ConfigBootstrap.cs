namespace ReferenceRAG.Infrastructure.Features.Storage.Bootstrap;

using System;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Helpers;

/// <summary>
/// 配置文件引导入口门面
/// 统一对外暴露 EnsureInitialized 静态方法，确保程序启动或服务加载前配置文件健康就绪
/// </summary>
public static class ConfigBootstrap
{
    private static readonly object _syncLock = new();
    private static readonly ILogger _logger = StaticLogger.GetLogger("ConfigBootstrap");
    private static bool _initialized = false;
    private static ConfigBootstrapReport? _lastReport = null;
    private static string? _activeConfigPath = null;

    /// <summary>
    /// 获取当前生效的主配置文件绝对物理路径
    /// </summary>
    public static string ActiveConfigPath => _activeConfigPath ?? ConfigPathResolver.ResolveConfigPath();

    /// <summary>
    /// 重置引导状态（主要用于测试）
    /// </summary>
    public static void Reset()
    {
        lock (_syncLock)
        {
            _initialized = false;
            _lastReport = null;
            _activeConfigPath = null;
        }
    }

    /// <summary>
    /// 获取上一次引导执行报告
    /// </summary>
    public static ConfigBootstrapReport? LastReport => _lastReport;

    /// <summary>
    /// 确保配置文件就绪（不存在则自动创建，损坏则自愈，旧版本则自动迁移）
    /// 必须在 WebApplication.CreateBuilder 之前或首次加载配置时调用
    /// </summary>
    public static ConfigBootstrapReport EnsureInitialized(
        string? customConfigPath = null,
        IConfigEnvironmentAdapter? customAdapter = null,
        bool forceRecheck = false)
    {
        lock (_syncLock)
        {
            if (_initialized && !forceRecheck && customConfigPath == null && customAdapter == null)
            {
                return _lastReport ?? new ConfigBootstrapReport(
                    ConfigLifecycleState.Ready,
                    ConfigLifecycleState.Ready,
                    false, false, false, null, null, null);
            }

            var adapter = customAdapter ?? new LocalConfigEnvironmentAdapter(customConfigPath: customConfigPath);
            var stateMachine = new ConfigLifecycleStateMachine(adapter);
            var report = stateMachine.Run();

            if (report.WasSeeded)
            {
                _logger.LogInformation("[ConfigBootstrap] 配置文件不存在，已通过策略 [{Strategy}] 自动创建: {ConfigPath}",
                    report.UsedSeedStrategy, adapter.ConfigPath);
            }

            if (report.WasHealed)
            {
                _logger.LogWarning("[ConfigBootstrap] 检测到损坏的配置文件，已安全备份至: {BackupPath}，并已自愈重建",
                    report.BackupPath);
            }

            if (report.WasMigrated)
            {
                _logger.LogInformation("[ConfigBootstrap] 存量配置文件结构已自动执行版本迁移升级: {ConfigPath}",
                    adapter.ConfigPath);
            }

            if (report.FinalState == ConfigLifecycleState.Failed)
            {
                _logger.LogError("[ConfigBootstrap] 配置文件引导失败: {Error}", report.ErrorMessage);
            }

            _lastReport = report;
            if (adapter is LocalConfigEnvironmentAdapter localAdapter &&
                string.Equals(Path.GetFullPath(localAdapter.BaseDirectory), Path.GetFullPath(Directory.GetCurrentDirectory()), StringComparison.OrdinalIgnoreCase))
            {
                _activeConfigPath = adapter.ConfigPath;
            }
            else
            {
                _activeConfigPath = null;
            }
            _initialized = (report.FinalState == ConfigLifecycleState.Ready);
            return report;
        }
    }
}
