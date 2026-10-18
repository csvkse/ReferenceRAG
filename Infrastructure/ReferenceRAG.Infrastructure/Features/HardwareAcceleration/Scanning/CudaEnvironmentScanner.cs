using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using ReferenceRAG.Core.Features.HardwareAcceleration.Injection;
using ReferenceRAG.Core.Features.HardwareAcceleration.Models;

namespace ReferenceRAG.Core.Features.HardwareAcceleration.Scanning;

/// <summary>
/// CUDA 12 运行库环境扫描器
/// 负责在系统、注册表、标准目录以及常见 AI/PyTorch/Conda 环境中搜寻 cublasLt64_12.dll 并注入
/// </summary>
public class CudaEnvironmentScanner : ICudaEnvironmentScanner
{
    private readonly IEnvironmentInjector _injector;
    private readonly ILogger<CudaEnvironmentScanner>? _logger;

    private static readonly string[] RequiredDlls =
    {
        "cublasLt64_12.dll",
        "cublas64_12.dll"
    };

    public CudaEnvironmentScanner(IEnvironmentInjector injector, ILogger<CudaEnvironmentScanner>? logger = null)
    {
        _injector = injector ?? throw new ArgumentNullException(nameof(injector));
        _logger = logger;
    }

    public CudaEnvironmentScanResult ScanAndInject(string? configuredPath = null)
    {
        var scannedLocations = new List<string>();
        var matchedDirectories = new List<string>();

        var candidates = ProbeCandidateDirectoriesInternal(configuredPath, scannedLocations);

        foreach (var dir in candidates)
        {
            if (IsValidCuda12Directory(dir))
            {
                matchedDirectories.Add(dir);
            }
        }

        if (matchedDirectories.Count > 0)
        {
            var injected = _injector.InjectDirectories(matchedDirectories);
            var mainDir = matchedDirectories[0];

            _logger?.LogInformation("[CudaEnvironmentScanner] 成功定位 CUDA 12 运行库: {MainDir}, 共注入 {Count} 个路径",
                mainDir, injected.Count);

            return new CudaEnvironmentScanResult
            {
                Found = true,
                MainDllDirectory = mainDir,
                CudaVersion = "12.x",
                HasRequiredDlls = true,
                InjectedPaths = injected,
                ScannedLocations = scannedLocations
            };
        }

        _logger?.LogWarning("[CudaEnvironmentScanner] 未能在系统或常见 AI 目录中找到 CUDA 12 (cublasLt64_12.dll)");
        return new CudaEnvironmentScanResult
        {
            Found = false,
            HasRequiredDlls = false,
            ScannedLocations = scannedLocations,
            ErrorMessage = "未找到包含 cublasLt64_12.dll 与 cublas64_12.dll 的 CUDA 12 运行目录"
        };
    }

    public IReadOnlyList<string> ProbeCandidateDirectories(string? configuredPath = null)
    {
        var dummy = new List<string>();
        return ProbeCandidateDirectoriesInternal(configuredPath, dummy);
    }

    private List<string> ProbeCandidateDirectoriesInternal(string? configuredPath, List<string> scanned)
    {
        var candidates = new List<string>();
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        // 1. 用户配置路径（最高优先级）
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var split = configuredPath.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var p in split)
            {
                scanned.Add($"Configured: {p}");
                if (Directory.Exists(p)) candidates.Add(p);
            }
        }

        // 2. 本地便携式运行库目录 (./runtimes/cuda)
        var portableDirs = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "runtimes", "cuda"),
            Path.Combine(Directory.GetCurrentDirectory(), "runtimes", "cuda")
        };
        foreach (var pdir in portableDirs)
        {
            scanned.Add($"Portable: {pdir}");
            if (Directory.Exists(pdir)) candidates.Add(pdir);
        }

        if (isWindows)
        {
            // 3. Windows 注册表扫描
            try
            {
                candidates.AddRange(ScanRegistry(scanned));
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "[CudaEnvironmentScanner] 注册表扫描跳过");
            }

            // 4. 环境变量 CUDA_PATH 及系统 PATH
            candidates.AddRange(ScanEnvironmentVariables(scanned));

            // 5. Windows 常用标准安装路径
            candidates.AddRange(ScanStandardProgramFiles(scanned));

            // 6. 常见本地 AI 软件 / PyTorch / Conda 目录探测
            candidates.AddRange(ScanLocalAiSoftwareDirectories(scanned));
        }
        else
        {
            // Linux / 容器标准路径
            var linuxPaths = new[] { "/usr/local/cuda/lib64", "/usr/local/cuda-12/lib64", "/usr/lib/x86_64-linux-gnu" };
            foreach (var lp in linuxPaths)
            {
                scanned.Add($"Linux: {lp}");
                if (Directory.Exists(lp)) candidates.Add(lp);
            }
        }

        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static List<string> ScanRegistry(List<string> scanned)
    {
        var results = new List<string>();
        const string cudaRegPath = @"SOFTWARE\NVIDIA Corporation\GPU Computing Toolkit\CUDA";

        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var subKey = baseKey.OpenSubKey(cudaRegPath);
        if (subKey != null)
        {
            foreach (var subName in subKey.GetSubKeyNames())
            {
                // 仅筛选 v12.* 系列
                if (subName.StartsWith("v12", StringComparison.OrdinalIgnoreCase))
                {
                    using var verKey = subKey.OpenSubKey(subName);
                    var installDir = verKey?.GetValue("InstallDir")?.ToString();
                    if (!string.IsNullOrWhiteSpace(installDir))
                    {
                        var binDir = Path.Combine(installDir, "bin");
                        scanned.Add($"Registry: {binDir}");
                        if (Directory.Exists(binDir)) results.Add(binDir);
                    }
                }
            }
        }

        return results;
    }

    private static List<string> ScanEnvironmentVariables(List<string> scanned)
    {
        var results = new List<string>();

        // 探测 CUDA_PATH_V12_*
        var envVars = Environment.GetEnvironmentVariables();
        foreach (string key in envVars.Keys)
        {
            if (key.StartsWith("CUDA_PATH_V12", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("CUDA_PATH", StringComparison.OrdinalIgnoreCase))
            {
                var val = envVars[key]?.ToString();
                if (!string.IsNullOrWhiteSpace(val))
                {
                    var bin = Path.Combine(val, "bin");
                    scanned.Add($"EnvVar({key}): {bin}");
                    if (Directory.Exists(bin)) results.Add(bin);
                    if (Directory.Exists(val)) results.Add(val);
                }
            }
        }

        // 探测现有 PATH
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var p in pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (p.Contains("CUDA", StringComparison.OrdinalIgnoreCase) ||
                p.Contains("torch", StringComparison.OrdinalIgnoreCase) ||
                p.Contains("TensorRT", StringComparison.OrdinalIgnoreCase))
            {
                scanned.Add($"PathEntry: {p}");
                if (Directory.Exists(p)) results.Add(p);
            }
        }

        return results;
    }

    private static List<string> ScanStandardProgramFiles(List<string> scanned)
    {
        var results = new List<string>();
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrWhiteSpace(programFiles)) return results;

        var toolkitBase = Path.Combine(programFiles, "NVIDIA GPU Computing Toolkit", "CUDA");
        if (Directory.Exists(toolkitBase))
        {
            try
            {
                foreach (var dir in Directory.GetDirectories(toolkitBase, "v12.*"))
                {
                    var bin = Path.Combine(dir, "bin");
                    scanned.Add($"ProgramFiles: {bin}");
                    if (Directory.Exists(bin)) results.Add(bin);
                }
            }
            catch { }
        }

        var cudnnBase = Path.Combine(programFiles, "NVIDIA", "CUDNN");
        if (Directory.Exists(cudnnBase))
        {
            try
            {
                foreach (var dir in Directory.GetDirectories(cudnnBase))
                {
                    var bin = Path.Combine(dir, "bin");
                    scanned.Add($"ProgramFiles(cuDNN): {bin}");
                    if (Directory.Exists(bin)) results.Add(bin);
                }
            }
            catch { }
        }

        return results;
    }

    private static List<string> ScanLocalAiSoftwareDirectories(List<string> scanned)
    {
        var results = new List<string>();

        // 常见本地 AI 根目录列表
        var candidateRoots = new List<string>
        {
            @"E:\AI",
            @"D:\AI",
            @"C:\AI",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "miniconda3"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "anaconda3"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python")
        };

        foreach (var root in candidateRoots)
        {
            if (!Directory.Exists(root)) continue;

            scanned.Add($"LocalAiRoot: {root}");
            try
            {
                // 探测第一级或第二级子目录下的 torch/lib 与 _internal/torch/lib
                ProbeAiSubdirectories(root, results, depth: 0, maxDepth: 3);
            }
            catch { }
        }

        return results;
    }

    private static void ProbeAiSubdirectories(string dir, List<string> results, int depth, int maxDepth)
    {
        if (depth > maxDepth) return;

        // 检查直接包含 torch/lib
        var torchLib = Path.Combine(dir, "torch", "lib");
        if (Directory.Exists(torchLib) && IsValidCuda12Directory(torchLib))
        {
            results.Add(torchLib);
            return;
        }

        var internalTorchLib = Path.Combine(dir, "_internal", "torch", "lib");
        if (Directory.Exists(internalTorchLib) && IsValidCuda12Directory(internalTorchLib))
        {
            results.Add(internalTorchLib);
            return;
        }

        var venvTorchLib = Path.Combine(dir, ".venv", "Lib", "site-packages", "torch", "lib");
        if (Directory.Exists(venvTorchLib) && IsValidCuda12Directory(venvTorchLib))
        {
            results.Add(venvTorchLib);
            return;
        }

        try
        {
            foreach (var sub in Directory.GetDirectories(dir))
            {
                var subName = Path.GetFileName(sub);
                // 避开无意义且庞大的缓存或版本控制目录
                if (subName.StartsWith(".") || subName.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
                    subName.Equals("AppData", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                ProbeAiSubdirectories(sub, results, depth + 1, maxDepth);
            }
        }
        catch { }
    }

    private static bool IsValidCuda12Directory(string dir)
    {
        if (!Directory.Exists(dir)) return false;

        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        if (isWindows)
        {
            foreach (var dll in RequiredDlls)
            {
                var dllPath = Path.Combine(dir, dll);
                if (!File.Exists(dllPath)) return false;
            }
        }
        else
        {
            var hasCublasLt = File.Exists(Path.Combine(dir, "libcublasLt.so.12")) ||
                              Directory.GetFiles(dir, "libcublasLt.so.12*").Length > 0;
            var hasCublas = File.Exists(Path.Combine(dir, "libcublas.so.12")) ||
                            Directory.GetFiles(dir, "libcublas.so.12*").Length > 0;
            if (!hasCublasLt && !hasCublas) return false;
        }

        return true;
    }
}
