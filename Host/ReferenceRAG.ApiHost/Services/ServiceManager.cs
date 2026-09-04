using System.Diagnostics;
using System.Net;
using System.Runtime.Versioning;
using System.ServiceProcess;
using System.Text.Json;

using Serilog;

namespace WebApiWindowsService;

/// <summary>
/// Windows 服务管理器 - 支持通过参数自动注册/卸载服务
/// </summary>
public static class ServiceManager
{

    #region 入口依赖

    #region 依赖属性
    /// <summary>
    /// 暴露 Services 集合供 Controller 使用
    /// </summary>
    public static IServiceProvider? Services { get; private set; }

    /// <summary>
    /// 是否运行在服务环境
    /// </summary>
    public static bool IsService { get; private set; }

    private static ServiceConfig _serviceConfig { get; set; }

    private static bool _manualRestartRequested = false;

    #endregion

    #region 依赖方法


    /// <summary>
    /// 手动触发重启（供 Controller 或其他业务代码调用）
    /// </summary>
    public static void TriggerRestart()
    {
        Log.Information("收到手动重启服务请求...");
        _manualRestartRequested = true; // 标记为手动请求重启

        if (Services != null)
        {
            // 获取应用的生命周期管理器
            var appLifetime = Services.GetService<IHostApplicationLifetime>();
            if (appLifetime != null)
            {
                Log.Information("正在通知 Kestrel 优雅停止...");
                // 这行代码会让当前的 app.Run() 正常结束，从而进入 AppLaunch 的 finally 块
                appLifetime.StopApplication();
                return;
            }
        }

        // 兜底方案：如果没拿到服务容器，直接硬重启
        Log.Warning("无法获取 IHostApplicationLifetime，采取直接重启...");
        DoAutoRestart(_serviceConfig?.Name ?? "WebApiWindowsService");
    }

    public static void AppLaunch(string[] args, WebApplicationBuilder builder, WebApplication app)
    {
        #region 启动服务和程序（支援自动重启）
        ServiceManager.Services = app.Services;
        try
        {
            app.Run();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "服务启动失败");
            throw;
        }
        finally
        {
            // 修改这里：增加手动重启标志的判断
            if (_manualRestartRequested || ShouldAutoRestart(builder.Configuration))
            {
                DoAutoRestart(_serviceConfig.Name);
            }
            else
            {
                Log.CloseAndFlush();
            }
        }
        #endregion
    }

    /// <summary>
    /// 自动重启 (改为同步方法)
    /// </summary>
    public static void DoAutoRestart(string serviceName)
    {
        Log.Information("准备重启服务: {ServiceName}", serviceName);

        try
        {
            if (OperatingSystem.IsWindows())
                RestartWindowsServiceSync(serviceName);
            else if (OperatingSystem.IsLinux())
                RestartLinuxServiceSync(serviceName);
            else
                Log.Error("不支持的操作系统");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "重启失败: {Message}", ex.Message);
            Log.CloseAndFlush();
        }
    }

    private static void RestartWindowsServiceSync(string serviceName)
    {
        // 使用 PowerShell 后台延时启动
        var psScript = $"Start-Sleep -Seconds 3; Restart-Service -Name '{serviceName}' -Force";

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -WindowStyle Hidden -Command \"{psScript}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        });

        Log.Information("已触发 Windows PowerShell 后台重启进程，当前服务即将彻底退出");

        // 关键步骤：同步刷入日志，然后主动告诉操作系统正常退出
        Log.CloseAndFlush();
        Environment.Exit(0);
    }

    private static void RestartLinuxServiceSync(string serviceName)
    {
        var script = $"sleep 3 && systemctl restart {serviceName}";
        Process.Start(new ProcessStartInfo
        {
            FileName = "/bin/bash",
            Arguments = $"-c \"nohup bash -c '{script}' > /dev/null 2>&1 &\"",
            UseShellExecute = false,
            CreateNoWindow = true
        });

        Log.Information("已委托 Linux shell 执行 restart，当前服务即将彻底退出");
        Log.CloseAndFlush();
        Environment.Exit(0);
    }

    /// <summary>
    /// 是否应该自动重启
    /// </summary>
    public static bool ShouldAutoRestart(IConfiguration configuration)
    {
        var value = configuration["Service:AutoRestart"];
        var serviceRestart = configuration["Service:AutoRestartInService"];
        
        Log.Information("自动重启检查: IsService={IsService}, UserInteractive={UserInteractive}", 
            IsService, Environment.UserInteractive);
        Log.Information("自动重启配置: Service:AutoRestart = {Value}, Service:AutoRestartInService = {ServiceRestart}", 
            value ?? "(null)", serviceRestart ?? "(null)");

        // 检查全局禁用
        if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
        {
            Log.Information("全局配置禁用自动重启");
            return false;
        }

        // 服务环境：默认启用
        if (IsService)
        {
            if (string.Equals(serviceRestart, "false", StringComparison.OrdinalIgnoreCase))
            {
                Log.Information("服务模式但配置了禁用自动重启");
                return false;
            }
            Log.Information("服务模式，启用自动重启");
            return true;
        }

        //// 控制台模式：默认禁用，可通过 Service:AutoRestart=true 启用
        //if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
        //{
        //    Log.Information("控制台模式，配置启用自动重启");
        //    return true;
        //}

        Log.Information("控制台模式，默认不自动重启");
        return false;
    }

    public static void ConfigureLogging(WebApplicationBuilder builder)
    {
        var workDir = builder.Configuration["Work:Directory"];
        workDir = string.IsNullOrWhiteSpace(workDir) ? AppContext.BaseDirectory : workDir;
        Log.Information("工作区目录: {WorkDir}", workDir);

        var logPath = Path.Combine(workDir, "Logs");
        if (!Directory.Exists(logPath))
        {
            Directory.CreateDirectory(logPath);
        }

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                shared: true,
                path: @$"{logPath}\app_.log",
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}"
            )
            .CreateLogger();

        builder.Host.UseSerilog();
        Log.Information("-----------程序调用-----------");
    }

    public static bool ConfigureService(string[] args, WebApplicationBuilder builder)
    {
        _serviceConfig = new ServiceManager.ServiceConfig
        {
            Name = builder.Configuration["Service:Name"] ?? AppDomain.CurrentDomain.FriendlyName,
            DisplayName = builder.Configuration["Service:DisplayName"] ?? "Windows Service",
            Description = builder.Configuration["Service:Description"] ?? "Windows System Service"
        };

        // Windows 平台检查服务命令
        if (OperatingSystem.IsWindows() && ServiceManager.TryHandleServiceCommand(args, _serviceConfig))
        {
            Log.CloseAndFlush();
            Environment.Exit(0);
            return true;
        }

        // Windows: 检查父进程是否是 services.exe
        if (OperatingSystem.IsWindows())
        {
            builder.Host.UseWindowsService();
            IsService = IsRunningAsService();  // ← 改用正确的检测方法
        }
        else if (OperatingSystem.IsLinux())
        {
            builder.Host.UseSystemd();
            IsService = Environment.GetEnvironmentVariable("INVOCATION_ID") != null;
        }

        return IsService;
    }

    #region 服务环境检测

    /// <summary>
    /// 检测是否作为 Windows 服务运行
    /// </summary>
    public static bool IsRunningAsService()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            // 方法 1：检查命令行参数（API 重启时传递）
            var cmdArgs = Environment.GetCommandLineArgs();
            if (cmdArgs.Any(a => a.Equals("--service-mode", StringComparison.OrdinalIgnoreCase)))
            {
                Log.Information("服务检测：命令行参数确认 (--service-mode)");
                return true;
            }

            // 方法 2：检查环境变量
            var serviceMode = Environment.GetEnvironmentVariable("REFERENCERAG_SERVICE_MODE");
            if (!string.IsNullOrEmpty(serviceMode) && serviceMode.Equals("1", StringComparison.OrdinalIgnoreCase))
            {
                Log.Information("服务检测：环境变量确认 (REFERENCERAG_SERVICE_MODE=1)");
                return true;
            }

            // 方法 3：通过 WMI 查询当前进程是否注册为服务
            var currentProcessId = Environment.ProcessId;
            using var searcher = new System.Management.ManagementObjectSearcher(
                $"SELECT Name FROM Win32_Service WHERE ProcessId = {currentProcessId}");
            using var results = searcher.Get();

            foreach (System.Management.ManagementObject result in results)
            {
                var serviceName = result["Name"]?.ToString();
                if (!string.IsNullOrEmpty(serviceName))
                {
                    Log.Information("WMI 检测确认：当前进程是服务 '{ServiceName}'", serviceName);
                    return true;
                }
            }

            // 方法 4：检查父进程是否为 services.exe
            var currentProcess = Process.GetCurrentProcess();
            var parentProcessId = GetParentProcessId(currentProcess.Id);

            if (parentProcessId > 0)
            {
                var parentProcess = Process.GetProcessById(parentProcessId);
                var isServiceParent = parentProcess.ProcessName.Equals("services", StringComparison.OrdinalIgnoreCase) ||
                                      parentProcess.ProcessName.Equals("services.exe", StringComparison.OrdinalIgnoreCase);
                parentProcess.Dispose();
                
                if (isServiceParent)
                {
                    Log.Information("父进程检测确认：services.exe 是父进程");
                    return true;
                }
            }

            // 无法确认为服务
            Log.Information("服务检测结果：非服务环境 (UserInteractive={UserInteractive})", 
                Environment.UserInteractive);
            return false;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "服务检测异常，使用备用判断");
            return false;
        }
    }

    /// <summary>
    /// 获取父进程 ID（跨平台兼容）
    /// </summary>
    private static int GetParentProcessId(int processId)
    {
        // 方法 1：使用 P/Invoke (Windows NT 4.0+)
        try
        {
            var query = $"SELECT ParentProcessId FROM Win32_Process WHERE ProcessId = {processId}";
            using var searcher = new System.Management.ManagementObjectSearcher(query);
            using var results = searcher.Get();

            foreach (System.Management.ManagementObject result in results)
            {
                return Convert.ToInt32(result["ParentProcessId"]);
            }
        }
        catch { }

        return -1;
    }

    #endregion
    #endregion

    #endregion


    #region 核心服务依赖类
    /// <summary>
    /// 日志查询选项
    /// </summary>
    public class LogOptions
    {
        public int Lines { get; set; } = 50;
        public string? Filter { get; set; }
        public bool Tail { get; set; }
    }

    /// <summary>
    /// 服务配置
    /// </summary>
    public class ServiceConfig
    {
        public string Name { get; set; } = "WebApiWindowsService";
        public string DisplayName { get; set; } = "Web API Windows Service";
        public string Description { get; set; } = "Web API as Windows Service";
        public string WorkDir { get; set; } = "";
    } 
    #endregion

    #region 核心功能-服务配置
    /// <summary>
    /// 解析命令行参数并执行相应操作
    /// </summary>
    /// <returns>true 表示已处理服务操作，应退出程序；false 表示正常运行</returns>
    public static bool TryHandleServiceCommand(string[] args, ServiceConfig config)
    {
        if (args.Length == 0)
            return false;

        var command = args[0].ToLowerInvariant();

        // help 和 status 命令不需要管理员权限
        switch (command)
        {
            case "--help":
            case "-h":
            case "help":
                ShowHelp();
                return true;

            case "--status":
            case "status":
                ShowStatus(config);
                return true;

            case "--logs":
            case "logs":
                ShowLogs(config, args);
                return true;
        }

        // 其他命令需要管理员权限
        if (!IsAdministrator())
        {
            Console.WriteLine("错误: 请以管理员身份运行此程序");
            Console.WriteLine("右键点击命令提示符，选择'以管理员身份运行'");
            return true;
        }

        switch (command)
        {
            case "--install":
            case "-i":
            case "install":
                InstallService(config);
                return true;

            case "--uninstall":
            case "-u":
            case "uninstall":
                UninstallService(config);
                return true;

            case "--start":
            case "start":
                StartService(config);
                return true;

            case "--stop":
            case "stop":
                StopService(config);
                return true;

            default:
                Console.WriteLine($"未知命令: {command}");
                ShowHelp();
                return true;
        }
    }

    /// <summary>
    /// 安装服务
    /// </summary>
    public static void InstallService(ServiceConfig config)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            Console.WriteLine("错误: 无法获取程序路径");
            return;
        }

        // 检查服务是否已存在
        if (ServiceExists(config.Name))
        {
            Console.WriteLine($"服务 '{config.Name}' 已存在，请先卸载");
            return;
        }

        Console.WriteLine($"正在安装服务 '{config.Name}'...");

        // 构建 binPath，可选设置工作目录
        string binPath;
        if (!string.IsNullOrEmpty(config.WorkDir))
        {
            // 工作目录通过键值对传递，服务启动时会使用指定的工作目录
            binPath = $"\"{exePath}\" --workdir \"{config.WorkDir}\"";
        }
        else
        {
            binPath = $"\"{exePath}\"";
        }

        // 使用 sc create 命令安装服务
        // binPath= 后面必须有空格
        var arguments = $"create \"{config.Name}\" binPath= {binPath} DisplayName= \"{config.DisplayName}\" start= auto";

        var result = RunScCommand(arguments);
        if (result.Success)
        {
            // 设置服务描述
            RunScCommand($"description \"{config.Name}\" \"{config.Description}\"");

            // 配置失败后自动重启
            ConfigureFailureActions(config.Name);

            Console.WriteLine($"服务 '{config.Name}' 安装成功！");
            Console.WriteLine($"可执行文件路径: {exePath}");
            Console.WriteLine();
            Console.WriteLine("后续操作:");
            Console.WriteLine($"  启动服务: {exePath} --start");
            Console.WriteLine($"  或使用: sc start \"{config.Name}\"");

            // 启动服务
            StartService(config);
        }
        else
        {
            Console.WriteLine($"服务安装失败: {result.Output}");
        }
    }

    /// <summary>
    /// 卸载服务
    /// </summary>
    public static void UninstallService(ServiceConfig config)
    {
        if (!ServiceExists(config.Name))
        {
            Console.WriteLine($"服务 '{config.Name}' 不存在");
            return;
        }

        // 先停止服务
        var service = ServiceController.GetServices().FirstOrDefault(s => s.ServiceName == config.Name);
        if (service != null && service.Status == ServiceControllerStatus.Running)
        {
            Console.WriteLine("正在停止服务...");
            try
            {
                service.Stop();
                service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"停止服务失败: {ex.Message}");
            }
        }

        Console.WriteLine($"正在卸载服务 '{config.Name}'...");
        var result = RunScCommand($"delete \"{config.Name}\"");

        if (result.Success)
        {
            Console.WriteLine($"服务 '{config.Name}' 卸载成功！");
        }
        else
        {
            Console.WriteLine($"服务卸载失败: {result.Output}");
        }
    }

    /// <summary>
    /// 启动服务
    /// </summary>
    public static void StartService(ServiceConfig config)
    {
        if (!ServiceExists(config.Name))
        {
            Console.WriteLine($"服务 '{config.Name}' 不存在，请先安装");
            return;
        }

        Console.WriteLine($"正在启动服务 '{config.Name}'...");
        var result = RunScCommand($"start \"{config.Name}\"");

        if (result.Success)
        {
            Console.WriteLine("服务启动成功！");
        }
        else
        {
            Console.WriteLine($"服务启动失败: {result.Output}");
        }
    }

    /// <summary>
    /// 停止服务
    /// </summary>
    public static void StopService(ServiceConfig config)
    {
        if (!ServiceExists(config.Name))
        {
            Console.WriteLine($"服务 '{config.Name}' 不存在");
            return;
        }

        Console.WriteLine($"正在停止服务 '{config.Name}'...");
        var result = RunScCommand($"stop \"{config.Name}\"");

        if (result.Success)
        {
            Console.WriteLine("服务停止成功！");
        }
        else
        {
            Console.WriteLine($"服务停止失败: {result.Output}");
        }
    }
    

    /// <summary>
    /// 配置失败后自动重启
    /// </summary>
    private static void ConfigureFailureActions(string serviceName)
    {
        // reset=86400: 24小时后重置失败计数
        // actions=restart/5000/restart/5000/restart/5000: 失败后5秒重启，最多3次
        var result = RunScCommand($"failure \"{serviceName}\" reset= 86400 actions= restart/5000/restart/5000/restart/5000");
        if (result.Success)
        {
            Console.WriteLine("已配置失败后自动重启 (失败后5秒重启，最多3次，24小时后重置)");
        }
        else
        {
            Console.WriteLine($"配置失败恢复失败: {result.Output}");
        }
    }

    /// <summary>
    /// 显示服务状态
    /// </summary>
    public static void ShowStatus(ServiceConfig config)
    {
        if (!ServiceExists(config.Name))
        {
            Console.WriteLine($"服务 '{config.Name}' 未安装");
            return;
        }

        var service = ServiceController.GetServices().FirstOrDefault(s => s.ServiceName == config.Name);
        if (service != null)
        {
            Console.WriteLine($"服务名称: {config.Name}");
            Console.WriteLine($"显示名称: {config.DisplayName}");
            Console.WriteLine($"当前状态: {GetStatusText(service.Status)}");
            Console.WriteLine($"可执行路径: {GetServicePath(config.Name)}");
        }
    }

    /// <summary>
    /// 显示帮助信息
    /// </summary>
    public static void ShowHelp()
    {
        var exeName = AppDomain.CurrentDomain.FriendlyName;
        Console.WriteLine($"用法: {exeName} <命令>");
        Console.WriteLine();
        Console.WriteLine("命令:");
        Console.WriteLine("  --install, -i, install    安装为 Windows 服务");
        Console.WriteLine("  --uninstall, -u, uninstall 卸载 Windows 服务");
        Console.WriteLine("  --start, start            启动服务");
        Console.WriteLine("  --stop, stop              停止服务");
        Console.WriteLine("  --status, status          查看服务状态");
        Console.WriteLine("  --logs, logs              查看日志 (默认最近50行)");
        Console.WriteLine("  --help, -h, help          显示帮助信息");
        Console.WriteLine();
        Console.WriteLine("日志选项:");
        Console.WriteLine("  -n, --lines N             显示最近 N 行 (默认 50)");
        Console.WriteLine("  -f, --filter TEXT         过滤包含指定文本的日志");
        Console.WriteLine("  -t, --tail                实时跟踪日志 (Ctrl+C 退出)");
        Console.WriteLine();
        Console.WriteLine("不带参数运行时，将作为普通应用程序或服务运行");
        Console.WriteLine();
        Console.WriteLine("示例:");
        Console.WriteLine($"  {exeName} --install    # 安装服务");
        Console.WriteLine($"  {exeName} --start      # 启动服务");
        Console.WriteLine($"  {exeName} --status     # 查看状态");
        Console.WriteLine($"  {exeName} --logs       # 查看最近日志");
        Console.WriteLine($"  {exeName} --logs -n 100 # 查看最近100行");
        Console.WriteLine($"  {exeName} --logs -f Error # 过滤Error关键字");
        Console.WriteLine($"  {exeName} --logs --tail # 实时跟踪日志");
        Console.WriteLine($"  {exeName} --stop       # 停止服务");
        Console.WriteLine($"  {exeName} --uninstall  # 卸载服务");
    } 
    #endregion

    #region 辅助方法

    private static bool IsAdministrator()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    private static bool ServiceExists(string serviceName)
    {
        return ServiceController.GetServices().Any(s => s.ServiceName == serviceName);
    }

    private static (bool Success, string Output) RunScCommand(string arguments)
    {
        try
        {
            var processInfo = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(processInfo);
            if (process == null)
                return (false, "无法启动 sc.exe");

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            // sc.exe 返回 0 表示成功，但有些情况下 [SC] 开头的输出也算成功
            var success = process.ExitCode == 0 || output.Contains("[SC] OpenService SUCCESS") || output.Contains("[SC] CreateService SUCCESS");

            return (success, string.IsNullOrEmpty(output) ? error : output);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static string GetStatusText(ServiceControllerStatus status)
    {
        return status switch
        {
            ServiceControllerStatus.Running => "正在运行",
            ServiceControllerStatus.Stopped => "已停止",
            ServiceControllerStatus.StartPending => "正在启动",
            ServiceControllerStatus.StopPending => "正在停止",
            ServiceControllerStatus.Paused => "已暂停",
            ServiceControllerStatus.PausePending => "正在暂停",
            ServiceControllerStatus.ContinuePending => "正在继续",
            _ => status.ToString()
        };
    }

    private static string? GetServicePath(string serviceName)
    {
        try
        {
            using var regKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
            return regKey?.GetValue("ImagePath")?.ToString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 显示日志文件内容
    /// </summary>
    public static void ShowLogs(ServiceConfig config, string[] args)
    {
        // 使用与 Program.cs 相同的日志目录逻辑
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        var logDir = Path.Combine(exeDir, "logs");
        var logPath = Path.Combine(logDir, "app_"+DateTime.Now.ToString("yyyy-MM-dd") + ".log");

        Console.WriteLine($"日志目录: {logDir}");

        if (!Directory.Exists(logDir))
        {
            Console.WriteLine($"日志目录不存在: {logDir}");
            Console.WriteLine("提示: 服务运行后才会生成日志文件");
            return;
        }

        // 解析参数
        var options = ParseLogOptions(args);

        try
        {
            if (options.Tail)
            {
                // 实时跟踪当日日志
                Console.WriteLine($"正在跟踪日志文件: {logPath}");
                Console.WriteLine("按 Ctrl+C 退出");
                Console.WriteLine(new string('-', 50));

                if (!File.Exists(logPath))
                {
                    Console.WriteLine("等待日志文件创建...");
                }

                using var fileStream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fileStream);

                // 先读取现有内容
                var existingContent = reader.ReadToEnd();
                if (!string.IsNullOrEmpty(existingContent))
                {
                    Console.Write(existingContent);
                }

                // 然后跟踪新内容
                while (true)
                {
                    var line = reader.ReadLine();
                    if (line == null)
                    {
                        Thread.Sleep(100);
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(options.Filter) &&
                        !line.Contains(options.Filter, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    Console.WriteLine(line);
                }
            }
            else
            {
                // 读取所有日志文件或指定文件
                var files = Directory.GetFiles(logDir, "*.log")
                    .OrderByDescending(f => f)
                    .ToList();

                if (files.Count == 0)
                {
                    Console.WriteLine("没有找到日志文件");
                    return;
                }

                var allLines = new List<string>();

                foreach (var file in files)
                {
                    // 使用共享读模式，允许与 Serilog 的独占写入共存
                    string[] lines;
                    try
                    {
                        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var reader = new StreamReader(fs);
                        var content = reader.ReadToEnd();
                        lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    }
                    catch (IOException)
                    {
                        // 文件被占用时跳过
                        continue;
                    }

                    var filtered = lines.AsEnumerable();

                    if (!string.IsNullOrWhiteSpace(options.Filter))
                    {
                        filtered = filtered.Where(l => l.Contains(options.Filter, StringComparison.OrdinalIgnoreCase));
                    }

                    allLines.AddRange(filtered);

                    // 达到目标行数后跳出循环
                    if (allLines.Count >= options.Lines)
                    {
                        break;
                    }
                }

                var lastLines = allLines.TakeLast(options.Lines).ToList();

                if (lastLines.Count == 0)
                {
                    Console.WriteLine("没有匹配的日志");
                    return;
                }

                Console.WriteLine($"共 {lastLines.Count} 条日志记录:");
                Console.WriteLine(new string('-', 50));
                foreach (var line in lastLines)
                {
                    Console.WriteLine(line);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"读取日志失败: {ex.Message}");
        }
    }

    private static LogOptions ParseLogOptions(string[] args)
    {
        var options = new LogOptions();

        for (int i = 1; i < args.Length; i++)
        {
            var arg = args[i].ToLowerInvariant();

            if (arg == "--tail" || arg == "-t")
            {
                options.Tail = true;
            }
            else if (arg == "--lines" || arg == "-n")
            {
                if (i + 1 < args.Length && int.TryParse(args[i + 1], out var lines))
                {
                    options.Lines = lines;
                    i++;
                }
            }
            else if (arg == "--filter" || arg == "-f")
            {
                if (i + 1 < args.Length)
                {
                    options.Filter = args[i + 1];
                    i++;
                }
            }
            else if (!arg.StartsWith("-"))
            {
                // 尝试作为行数解析
                if (int.TryParse(arg, out var lines))
                {
                    options.Lines = lines;
                }
            }
        }

        return options;
    }

    #endregion
}
