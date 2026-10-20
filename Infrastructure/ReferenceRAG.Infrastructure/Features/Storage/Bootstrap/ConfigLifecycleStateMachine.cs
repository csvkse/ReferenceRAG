namespace ReferenceRAG.Infrastructure.Features.Storage.Bootstrap;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using ReferenceRAG.Infrastructure.Features.Storage.Migrations;

/// <summary>
/// 引导执行报告
/// </summary>
public record ConfigBootstrapReport(
    ConfigLifecycleState InitialState,
    ConfigLifecycleState FinalState,
    bool WasSeeded,
    bool WasHealed,
    bool WasMigrated,
    string? UsedSeedStrategy,
    string? BackupPath,
    string? ErrorMessage);

/// <summary>
/// 配置文件生命周期状态机
/// 负责驱动配置文件的探测、校验、自愈备份、种子生成与平滑迁移
/// </summary>
public class ConfigLifecycleStateMachine
{
    private readonly IConfigEnvironmentAdapter _adapter;
    private readonly List<IConfigSeedStrategy> _seedStrategies;
    private ConfigLifecycleState _currentState = ConfigLifecycleState.Uninitialized;

    public ConfigLifecycleState CurrentState => _currentState;

    public ConfigLifecycleStateMachine(
        IConfigEnvironmentAdapter adapter,
        IEnumerable<IConfigSeedStrategy>? seedStrategies = null)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _seedStrategies = (seedStrategies ??
        [
            new TemplateFileSeedStrategy(),
            new FactoryDefaultSeedStrategy()
        ]).OrderBy(s => s.Priority).ToList();
    }

    /// <summary>
    /// 运行状态机直至终止状态（Ready 或 Failed）
    /// </summary>
    public ConfigBootstrapReport Run()
    {
        _currentState = ConfigLifecycleState.Checking;
        var initialState = _currentState;
        bool wasSeeded = false;
        bool wasHealed = false;
        bool wasMigrated = false;
        string? usedStrategy = null;
        string? backupPath = null;
        string? errorMessage = null;

        while (_currentState != ConfigLifecycleState.Ready && _currentState != ConfigLifecycleState.Failed)
        {
            switch (_currentState)
            {
                case ConfigLifecycleState.Checking:
                    if (!_adapter.FileExists(_adapter.ConfigPath))
                    {
                        _currentState = ConfigLifecycleState.Missing;
                    }
                    else
                    {
                        _currentState = ConfigLifecycleState.Validating;
                    }
                    break;

                case ConfigLifecycleState.Missing:
                    _currentState = ConfigLifecycleState.Seeding;
                    break;

                case ConfigLifecycleState.Seeding:
                    try
                    {
                        var strategy = _seedStrategies.FirstOrDefault(s => s.CanSeed(_adapter))
                                       ?? new FactoryDefaultSeedStrategy();

                        usedStrategy = strategy.Name;
                        var content = strategy.GenerateConfigJson(_adapter);
                        _adapter.WriteAtomic(_adapter.ConfigPath, content);
                        wasSeeded = true;
                        _currentState = ConfigLifecycleState.Ready;
                    }
                    catch (Exception ex)
                    {
                        errorMessage = $"配置种子生成失败: {ex.Message}";
                        _currentState = ConfigLifecycleState.Failed;
                    }
                    break;

                case ConfigLifecycleState.Validating:
                    if (_adapter.GetFileSize(_adapter.ConfigPath) < 5)
                    {
                        _currentState = ConfigLifecycleState.Corrupted;
                        break;
                    }

                    try
                    {
                        var text = _adapter.ReadAllText(_adapter.ConfigPath);
                        var node = JsonNode.Parse(text)?.AsObject();
                        if (node == null)
                        {
                            _currentState = ConfigLifecycleState.Corrupted;
                            break;
                        }

                        // 检查是否需要版本迁移（存在旧 Chat 节点或版本低于 2）
                        bool hasLegacyChat = node.ContainsKey("Chat");
                        int schemaVersion = 1;
                        if (node["ReferenceRAG"] is JsonObject ragObj && ragObj.TryGetPropertyValue("schemaVersion", out var verNode))
                        {
                            schemaVersion = verNode?.GetValue<int>() ?? 1;
                        }

                        if (hasLegacyChat || schemaVersion < 2)
                        {
                            _currentState = ConfigLifecycleState.Migrating;
                        }
                        else
                        {
                            _currentState = ConfigLifecycleState.Ready;
                        }
                    }
                    catch
                    {
                        // JSON 语法解析崩溃，属于损坏文件
                        _currentState = ConfigLifecycleState.Corrupted;
                    }
                    break;

                case ConfigLifecycleState.Corrupted:
                    _currentState = ConfigLifecycleState.Healing;
                    break;

                case ConfigLifecycleState.Healing:
                    try
                    {
                        backupPath = $"{_adapter.ConfigPath}.corrupted.{DateTime.UtcNow:yyyyMMddHHmmss}.bak";
                        _adapter.Move(_adapter.ConfigPath, backupPath, overwrite: true);
                        wasHealed = true;
                        _currentState = ConfigLifecycleState.Seeding;
                    }
                    catch (Exception ex)
                    {
                        errorMessage = $"自愈备份损坏文件失败: {ex.Message}";
                        _currentState = ConfigLifecycleState.Failed;
                    }
                    break;

                case ConfigLifecycleState.Migrating:
                    try
                    {
                        ConfigMigrationRunner.ApplyMigrations(_adapter.ConfigPath);
                        wasMigrated = true;
                        _currentState = ConfigLifecycleState.Ready;
                    }
                    catch (Exception ex)
                    {
                        errorMessage = $"执行配置版本迁移失败: {ex.Message}";
                        _currentState = ConfigLifecycleState.Failed;
                    }
                    break;

                default:
                    errorMessage = $"未知的状态机状态: {_currentState}";
                    _currentState = ConfigLifecycleState.Failed;
                    break;
            }
        }

        return new ConfigBootstrapReport(
            InitialState: initialState,
            FinalState: _currentState,
            WasSeeded: wasSeeded,
            WasHealed: wasHealed,
            WasMigrated: wasMigrated,
            UsedSeedStrategy: usedStrategy,
            BackupPath: backupPath,
            ErrorMessage: errorMessage);
    }
}
