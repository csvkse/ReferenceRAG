using Microsoft.Extensions.DependencyInjection;
using ReferenceRAG.Core.Features.HardwareAcceleration.Detection;
using ReferenceRAG.Core.Features.HardwareAcceleration.Injection;
using ReferenceRAG.Core.Features.HardwareAcceleration.Portable;
using ReferenceRAG.Core.Features.HardwareAcceleration.Scanning;
using ReferenceRAG.Core.Features.HardwareAcceleration.Strategies;

namespace ReferenceRAG.Core.Features.HardwareAcceleration;

/// <summary>
/// 硬件加速依赖注入扩展
/// </summary>
public static class HardwareAccelerationServiceExtensions
{
    public static IServiceCollection AddHardwareAcceleration(this IServiceCollection services)
    {
        services.AddSingleton<IEnvironmentInjector, Win32EnvironmentInjector>();
        services.AddSingleton<IHardwareDetector, HardwareDetector>();
        services.AddSingleton<ICudaEnvironmentScanner, CudaEnvironmentScanner>();
        services.AddSingleton<IPortableCudaManager, PortableCudaManager>();

        // 注册具体硬件策略
        services.AddSingleton<IExecutionStrategy, NativeCudaStrategy>();
        services.AddSingleton<IExecutionStrategy, PortableCudaStrategy>();
        services.AddSingleton<IExecutionStrategy, DirectMlStrategy>();
        services.AddSingleton<IExecutionStrategy, CpuStrategy>();

        // 注册聚合自适应策略
        services.AddSingleton<AutoExecutionStrategy>();

        // 注册动态 GPU Offload 计算器
        services.AddSingleton<IGpuOffloadCalculator, GpuOffloadCalculator>();

        return services;
    }
}
