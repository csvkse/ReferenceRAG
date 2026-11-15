using System.Collections.Generic;
using System.Linq;
using ReferenceRAG.Core.Features.ModelManagement.Helpers;
using ReferenceRAG.Core.Services;

namespace ReferenceRAG.Core.Features.ModelManagement.Services;

/// <summary>
/// 模型展示名覆盖注册表（策略模式 + 单一权威注册表）。
///
/// 背景：系统中同一模型有两类称谓——<see cref="ModelInfo.Name"/>（规范 ID，身份键）
/// 与 <see cref="ModelInfo.DisplayName"/>（展示名）。历史上展示名既被扫描逻辑推导
/// （本地目录名），又被误用为身份键（EmbeddingSpec.ModelName），导致命名体验差且
/// 配置漂移误报。本注册表把「用户自定义展示名」收敛为显式、集中、可管理的一等公民。
///
/// 设计原则：
/// 1. 以 <see cref="ModelUrn"/> 为键——身份与展示彻底解耦，本地/在线模型共用一套 API；
/// 2. 只影响 UI 呈现，绝不参与身份比较（EmbeddingSpec.ModelName / 漂移判定不受影响）；
/// 3. 持久化于 ObsidianRagConfig.ModelDisplayNames，缺省为空字典即现状行为（纯加法）。
///
/// 业界对照：vLLM 的 --served-model-name 将「API 可见名称」与底层模型路径分离，
/// 支持随时修改且不影响模型加载；MLflow Model Registry 以 name + aliases + tags
/// 承载注册元数据。二者共同原则：展示名是可随时变更的轻量元数据。
/// </summary>
public sealed class ModelDisplayNameRegistry
{
    /// <summary>展示名最大长度（防 UI 撑爆与存储滥用）。</summary>
    public const int MaxDisplayNameLength = 64;

    private readonly ConfigManager _configManager;
    private readonly object _gate = new();

    public ModelDisplayNameRegistry(ConfigManager configManager)
    {
        _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
    }

    /// <summary>
    /// 当前全部覆盖项快照（键为 ModelUrn 字符串形式，如 "local:bge-m3"）。
    /// </summary>
    public Dictionary<string, string> Snapshot()
    {
        lock (_gate)
        {
            return new Dictionary<string, string>(LoadOverrides());
        }
    }

    /// <summary>
    /// 解析某模型的最终展示名（策略优先级：用户覆盖 → 系统推导值）。
    /// </summary>
    /// <param name="urn">模型身份</param>
    /// <param name="fallback">系统推导名（DisplayName / Name / 目录名）</param>
    public string Resolve(ModelUrn urn, string? fallback)
    {
        if (urn.IsEmpty) return fallback ?? string.Empty;

        lock (_gate)
        {
            if (LoadOverrides().TryGetValue(urn.ToString(), out var custom) &&
                !string.IsNullOrWhiteSpace(custom))
            {
                return custom;
            }
        }

        return fallback ?? string.Empty;
    }

    /// <summary>
    /// 判断某模型是否已被用户自定义展示名。
    /// </summary>
    public bool HasOverride(ModelUrn urn)
    {
        if (urn.IsEmpty) return false;
        lock (_gate)
        {
            return LoadOverrides().ContainsKey(urn.ToString());
        }
    }

    /// <summary>
    /// 设置展示名覆盖（含校验：非空、长度上限、无控制字符）。
    /// </summary>
    /// <returns>成功返回 true；失败时 <paramref name="error"/> 说明原因（中文，供 UI 直接展示）</returns>
    public bool TrySet(ModelUrn urn, string? displayName, out string? error)
    {
        error = null;
        if (urn.IsEmpty)
        {
            error = "模型身份无效";
            return false;
        }

        var trimmed = (displayName ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            error = "模型名称不能为空";
            return false;
        }

        if (trimmed.Length > MaxDisplayNameLength)
        {
            error = $"模型名称过长（最多 {MaxDisplayNameLength} 字符）";
            return false;
        }

        if (trimmed.Any(char.IsControl))
        {
            error = "模型名称不能包含控制字符";
            return false;
        }

        lock (_gate)
        {
            var config = _configManager.Load();
            var overrides = config.ModelDisplayNames ?? new Dictionary<string, string>();
            overrides[urn.ToString()] = trimmed;
            config.ModelDisplayNames = overrides;
            _configManager.Save(config);
        }

        return true;
    }

    /// <summary>
    /// 清除展示名覆盖，回退到系统推导名。
    /// </summary>
    public bool Reset(ModelUrn urn)
    {
        if (urn.IsEmpty) return false;

        lock (_gate)
        {
            var config = _configManager.Load();
            var overrides = config.ModelDisplayNames;
            if (overrides == null || !overrides.Remove(urn.ToString())) return false;

            config.ModelDisplayNames = overrides;
            _configManager.Save(config);
            return true;
        }
    }

    /// <summary>
    /// 清理已不存在模型的孤儿覆盖条目（模型被删除后调用）。
    /// </summary>
    /// <param name="existsPredicate">判断某 ModelUrn 是否仍存在</param>
    /// <returns>被清理的条目数</returns>
    public int PruneOrphans(ModelUrnExistsPredicate existsPredicate)
    {
        ArgumentNullException.ThrowIfNull(existsPredicate);

        lock (_gate)
        {
            var config = _configManager.Load();
            var overrides = config.ModelDisplayNames;
            if (overrides == null || overrides.Count == 0) return 0;

            var orphans = overrides.Keys
                .Where(key => !existsPredicate(ModelUrn.Parse(key)))
                .ToList();

            foreach (var key in orphans)
            {
                overrides.Remove(key);
            }

            if (orphans.Count > 0)
            {
                config.ModelDisplayNames = overrides;
                _configManager.Save(config);
            }

            return orphans.Count;
        }
    }

    private Dictionary<string, string> LoadOverrides()
    {
        var config = _configManager.Load();
        var overrides = config.ModelDisplayNames;
        if (overrides == null || overrides.Count == 0) return new Dictionary<string, string>();

        // 容错：跳过空键/空值条目，避免损坏配置导致名称解析异常
        return overrides
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
    }
}

/// <summary>判断 ModelUrn 是否仍存在的谓词（供孤儿清理使用）。</summary>
public delegate bool ModelUrnExistsPredicate(ModelUrn urn);
