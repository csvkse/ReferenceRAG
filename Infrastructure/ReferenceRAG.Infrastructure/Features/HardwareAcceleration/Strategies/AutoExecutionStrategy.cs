using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using ReferenceRAG.Core.Features.HardwareAcceleration.Detection;
using ReferenceRAG.Core.Features.HardwareAcceleration.Models;

namespace ReferenceRAG.Core.Features.HardwareAcceleration.Strategies;

/// <summary>
/// 硬件自适应聚合策略（优先级 999）
/// 负责在多个候选硬件执行策略之间进行自动化协商与多级平滑降级（Graceful Degradation）
/// </summary>
public class AutoExecutionStrategy : IExecutionStrategy
{
    private readonly IHardwareDetector _detector;
    private readonly List<IExecutionStrategy> _strategies;
    private readonly ILogger<AutoExecutionStrategy>? _logger;

    private IExecutionStrategy? _activeStrategy;
    private StrategyValidationResult? _lastValidationResult;
    private readonly object _lock = new();

    public string Name => _activeStrategy?.Name ?? "Auto";
    public int Priority => 999;

    public AutoExecutionStrategy(
        IHardwareDetector detector,
        IEnumerable<IExecutionStrategy> strategies,
        ILogger<AutoExecutionStrategy>? logger = null)
    {
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
        // 排除自身，按优先级降序排序
        _strategies = (strategies ?? Enumerable.Empty<IExecutionStrategy>())
            .Where(s => s is not AutoExecutionStrategy)
            .OrderByDescending(s => s.Priority)
            .ToList();
        _logger = logger;
    }

    public bool IsSupported(HardwareContext context) => true;

    /// <summary>
    /// 获取当前生效的执行策略（若尚未协商则就绪默认 CPU 策略）
    /// </summary>
    public IExecutionStrategy GetActiveStrategy()
    {
        return _activeStrategy ?? _strategies.FirstOrDefault(s => s is CpuStrategy) ?? new CpuStrategy();
    }

    /// <summary>
    /// 获取上一次协商与校验结果
    /// </summary>
    public StrategyValidationResult? GetLastResult() => _lastValidationResult;

    /// <summary>
    /// 协商并准备最优执行策略
    /// </summary>
    public async Task<StrategyValidationResult> ValidateAndPrepareAsync(
        HardwareContext context,
        int deviceId = 0,
        string? configuredPath = null)
    {
        lock (_lock)
        {
            if (_activeStrategy != null && _lastValidationResult != null && _lastValidationResult.IsSuccess)
            {
                return _lastValidationResult;
            }
        }

        var hardwareContext = context ?? _detector.GetContext();
        _logger?.LogInformation("[AutoExecutionStrategy] 开始硬件加速策略协商，发现设备数={Count}, 主GPU={Primary}",
            hardwareContext.GpuDevices.Count,
            hardwareContext.PrimaryGpu?.Name ?? "None");

        foreach (var strategy in _strategies)
        {
            if (!strategy.IsSupported(hardwareContext))
            {
                _logger?.LogDebug("[AutoExecutionStrategy] 策略 {Name} 不支持当前环境，跳过", strategy.Name);
                continue;
            }

            _logger?.LogInformation("[AutoExecutionStrategy] 尝试评估策略: {Name} (优先级 {Priority})", strategy.Name, strategy.Priority);
            var result = await strategy.ValidateAndPrepareAsync(hardwareContext, deviceId, configuredPath);

            if (result.IsSuccess)
            {
                lock (_lock)
                {
                    _activeStrategy = strategy;
                    _lastValidationResult = result;
                }

                _logger?.LogInformation("[AutoExecutionStrategy] 策略协商成功，选定: {StrategyName}, 描述: {Message}",
                    strategy.Name, result.Message);
                return result;
            }

            _logger?.LogWarning("[AutoExecutionStrategy] 策略 {Name} 准备失败 ({Error})，触发平滑降级",
                strategy.Name, result.ErrorMessage);
        }

        // 最终兜底 CPU
        var cpuStrategy = _strategies.FirstOrDefault(s => s is CpuStrategy) ?? new CpuStrategy();
        var fallbackResult = await cpuStrategy.ValidateAndPrepareAsync(hardwareContext, deviceId, configuredPath);

        lock (_lock)
        {
            _activeStrategy = cpuStrategy;
            _lastValidationResult = fallbackResult;
        }

        return fallbackResult;
    }

    public void ConfigureOnnxSession(SessionOptions options, int deviceId = 0)
    {
        var active = GetActiveStrategy();
        active.ConfigureOnnxSession(options, deviceId);
    }
}
