# ObsidianRAG 服务安装脚本 (Windows)
# 用法: 以管理员身份运行

param(
    [string]$ServiceName = "ObsidianRAG",
    [int]$Port = 5000
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ServiceDir = Split-Path -Parent $ScriptDir
$Executable = Join-Path $ServiceDir "ObsidianRAG.Service.exe"

if (-not (Test-Path $Executable)) {
    Write-Error "找不到可执行文件: $Executable"
    exit 1
}

# 检查服务是否存在
$existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existingService) {
    Write-Host "服务已存在，正在停止..."
    Stop-Service -Name $ServiceName -Force
    sc.exe delete $ServiceName
    Start-Sleep -Seconds 2
}

# 创建服务
$binPath = "`"$Executable`""
sc.exe create $ServiceName binPath= $binPath start= auto DisplayName= "ObsidianRAG Service"

# 设置服务恢复选项
sc.exe failure $ServiceName reset= 86400 actions= restart/10000/restart/20000/restart/30000

# 设置环境变量
[Environment]::SetEnvironmentVariable("ASPNETCORE_URLS", "http://0.0.0.0:$Port", "Machine")

# 启动服务
Start-Service -Name $ServiceName

Write-Host "服务已安装并启动"
Write-Host "状态: Get-Service $ServiceName"
Write-Host "日志: Get-EventLog -LogName Application -Source $ServiceName"
