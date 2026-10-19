using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Features.HardwareAcceleration.Injection;
using ReferenceRAG.Core.Features.HardwareAcceleration.Scanning;
using ReferenceRAG.Core.Helpers;
using LLama.Native;

namespace ReferenceRAG.Core.Features.HardwareAcceleration;

/// <summary>
/// LLamaSharp (llama.cpp) 原生运行库及 CUDA 12 硬件加速引导器
/// 负责在 LLamaSharp 首次加载底层 C++ 动态库之前，完成环境探测、依赖注入与加载策略配置
/// </summary>
public static class LlamaNativeInitializer
{
    private static readonly object _lock = new();
    private static bool _initialized;
    private static bool _cudaConfigured;
    private static readonly ILogger _logger = StaticLogger.GetLogger("LlamaNativeInitializer");

    #region Win32 P/Invoke

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryW(string lpLibFileName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr AddDllDirectory(string NewDirectory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetDefaultDllDirectories(uint DirectoryFlags);

    private const uint LOAD_LIBRARY_SEARCH_DEFAULT_DIRS = 0x00001000;
    private const uint LOAD_LIBRARY_SEARCH_USER_DIRS = 0x00000400;

    #endregion

    /// <summary>
    /// 确保 LLamaSharp 原生运行库在任何 Native API 调用前完成最优配置
    /// </summary>
    /// <param name="preferCuda">是否优先使用 CUDA GPU 加速</param>
    /// <returns>是否已成功配置 CUDA 运行环境</returns>
    public static bool EnsureInitialized(bool preferCuda = true)
    {
        if (_initialized)
        {
            return _cudaConfigured;
        }

        lock (_lock)
        {
            if (_initialized)
            {
                return _cudaConfigured;
            }

            try
            {
                if (NativeLibraryConfig.LLama.LibraryHasLoaded)
                {
                    _logger.LogWarning("[LlamaNative] LLama 原生运行库已预先加载，无法重新调整底层配置 (已加载策略已锁定)");
                    _initialized = true;
                    return _cudaConfigured;
                }

                var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

                if (!preferCuda)
                {
                    NativeLibraryConfig.All.WithCuda(false);
                    _logger.LogInformation("[LlamaNative] preferCuda=false，已配置纯 CPU 运行模式");
                    _initialized = true;
                    return false;
                }

                // 1. 扫描并注入 CUDA 12 运行环境 (PyTorch、Python虚拟环境、CUDA Toolkit 或本地便携包)
                var injector = new Win32EnvironmentInjector();
                var scanner = new CudaEnvironmentScanner(injector);
                var scanResult = scanner.ScanAndInject();

                if (!scanResult.Found)
                {
                    var vulkanCandidateDirs = isWindows ? new[]
                    {
                        Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "vulkan"),
                        Path.Combine(Directory.GetCurrentDirectory(), "runtimes", "win-x64", "native", "vulkan"),
                        Path.Combine(AppContext.BaseDirectory, "vulkan"),
                        Path.Combine(Directory.GetCurrentDirectory(), "vulkan")
                    } : new[]
                    {
                        Path.Combine(AppContext.BaseDirectory, "runtimes", "linux-x64", "native", "vulkan"),
                        Path.Combine(Directory.GetCurrentDirectory(), "runtimes", "linux-x64", "native", "vulkan"),
                        Path.Combine(AppContext.BaseDirectory, "vulkan"),
                        Path.Combine(Directory.GetCurrentDirectory(), "vulkan")
                    };

                    string libName = isWindows ? "llama.dll" : "libllama.so";
                    string? activeVulkanDir = vulkanCandidateDirs.FirstOrDefault(d => Directory.Exists(d) && File.Exists(Path.Combine(d, libName)));
                    if (activeVulkanDir != null)
                    {
                        _logger.LogInformation("[LlamaNative] 未检测到 CUDA 12，已激活 Vulkan GPU 加速运行目录: {Dir}", activeVulkanDir);
                        if (isWindows)
                        {
                            try { AddDllDirectory(activeVulkanDir); } catch { }
                        }
                        injector.InjectDirectories(new[] { activeVulkanDir });
                    }
                    else
                    {
                        _logger.LogInformation("[LlamaNative] 未检测到 CUDA 12 依赖库，自动使用 CPU / 自适应回退模式");
                    }

                    NativeLibraryConfig.All.WithCuda(false).WithAutoFallback(true);
                    _initialized = true;
                    return false;
                }

                _logger.LogInformation("[LlamaNative] 成功探查并匹配到 CUDA 12 依赖路径: {Dir}", scanResult.MainDllDirectory);

                if (isWindows)
                {
                    // 2. 预热载入 CUDA 基础动态库，防止 ggml-cuda.dll 延迟加载时触发 Win32 126 模块缺失
                    if (scanResult.MainDllDirectory != null)
                    {
                        try { AddDllDirectory(scanResult.MainDllDirectory); } catch { }

                        var cudart = Path.Combine(scanResult.MainDllDirectory, "cudart64_12.dll");
                        if (File.Exists(cudart)) LoadLibraryW(cudart);

                        var cublas = Path.Combine(scanResult.MainDllDirectory, "cublas64_12.dll");
                        if (File.Exists(cublas)) LoadLibraryW(cublas);

                        var cublasLt = Path.Combine(scanResult.MainDllDirectory, "cublasLt64_12.dll");
                        if (File.Exists(cublasLt)) LoadLibraryW(cublasLt);
                    }

                    // 3. 探查并预热加载 LLamaSharp.Backend.Cuda12.Windows 中的 ggml-base.dll 与 ggml.dll
                    var candidateNativeDirs = new[]
                    {
                        Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "cuda12"),
                        Path.Combine(Directory.GetCurrentDirectory(), "runtimes", "win-x64", "native", "cuda12"),
                        Path.Combine(AppContext.BaseDirectory, "cuda12"),
                        Path.Combine(Directory.GetCurrentDirectory(), "cuda12")
                    };

                    string? activeCuda12Dir = null;
                    foreach (var dir in candidateNativeDirs)
                    {
                        if (Directory.Exists(dir) && File.Exists(Path.Combine(dir, "llama.dll")))
                        {
                            activeCuda12Dir = dir;
                            break;
                        }
                    }

                    if (activeCuda12Dir != null)
                    {
                        _logger.LogInformation("[LlamaNative] 激活 LLamaSharp CUDA 12 运行目录: {Dir}", activeCuda12Dir);
                        try { AddDllDirectory(activeCuda12Dir); } catch { }
                        injector.InjectDirectories(new[] { activeCuda12Dir });

                        var ggmlBase = Path.Combine(activeCuda12Dir, "ggml-base.dll");
                        if (File.Exists(ggmlBase)) LoadLibraryW(ggmlBase);

                        var ggml = Path.Combine(activeCuda12Dir, "ggml.dll");
                        if (File.Exists(ggml)) LoadLibraryW(ggml);
                    }
                }
                else
                {
                    // 探查并激活 LLamaSharp.Backend.Cuda12.Linux 中的 libllama.so
                    var candidateLinuxDirs = new[]
                    {
                        Path.Combine(AppContext.BaseDirectory, "runtimes", "linux-x64", "native", "cuda12"),
                        Path.Combine(Directory.GetCurrentDirectory(), "runtimes", "linux-x64", "native", "cuda12"),
                        Path.Combine(AppContext.BaseDirectory, "cuda12"),
                        Path.Combine(Directory.GetCurrentDirectory(), "cuda12")
                    };

                    string? activeCuda12LinuxDir = candidateLinuxDirs.FirstOrDefault(d => Directory.Exists(d) && File.Exists(Path.Combine(d, "libllama.so")));
                    if (activeCuda12LinuxDir != null)
                    {
                        _logger.LogInformation("[LlamaNative] 激活 Linux LLamaSharp CUDA 12 运行目录: {Dir}", activeCuda12LinuxDir);
                        injector.InjectDirectories(new[] { activeCuda12LinuxDir });
                    }
                }

                // 4. 配置 LLamaSharp 原生策略
                // SkipCheck(true): 解决无系统级 CUDA Toolkit (仅显卡驱动+第三方依赖) 时 CudaMajorVersion 返回 -1 静默回退 CPU 的深层根因
                // 注意: LLamaSharp 规则要求 SkipCheck(true) 时 WithAutoFallback 必须为 false。
                NativeLibraryConfig.All
                    .WithCuda(true)
                    .WithAutoFallback(false)
                    .SkipCheck(true)
                    .WithLogCallback((level, msg) =>
                    {
                        if (level == LLamaLogLevel.Error)
                            _logger.LogError("[LLamaNative] {Message}", msg);
                        else if (level == LLamaLogLevel.Warning)
                            _logger.LogWarning("[LLamaNative] {Message}", msg);
                        else
                            _logger.LogDebug("[LLamaNative] {Message}", msg);
                    });

                // 使用 DryRun 验证 CUDA 是否真正能成功载入；若失败可在调用任何 Native API 前安全改配回 CPU
                var dryRunSuccess = NativeLibraryConfig.All.DryRun(out var llamaLib, out var mtmdLib);
                if (dryRunSuccess)
                {
                    _cudaConfigured = true;
                    _logger.LogInformation("[LlamaNative] LLamaSharp CUDA 12 硬件加速已成功激活: {Lib}", llamaLib);
                }
                else
                {
                    _logger.LogWarning("[LlamaNative] LLamaSharp CUDA 原生库 DryRun 失败，平滑降级至 CPU 运行");
                    NativeLibraryConfig.All
                        .WithCuda(false)
                        .WithAutoFallback(true)
                        .SkipCheck(false);
                    _cudaConfigured = false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[LlamaNative] 硬件加速环境初始化发生异常，保留默认配置");
            }
            finally
            {
                _initialized = true;
            }

            return _cudaConfigured;
        }
    }
}
