# ObsidianRAG 服务安装脚本 (Windows)
# 用法: 以管理员身份运行

param(
    [string]$ServiceName = "ObsidianRAG",
    [int]$Port = 5000
)

$ErrorActionPreference = "Stop"

# 检查管理员权限
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "错误: 需要管理员权限运行此脚本" -ForegroundColor Red
    Write-Host "请右键点击 PowerShell，选择'以管理员身份运行'，然后重新执行此脚本" -ForegroundColor Yellow
    exit 1
}

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ServiceDir = Split-Path -Parent $ScriptDir
$Executable = Join-Path $ServiceDir "ObsidianRAG.Service.exe"

if (-not (Test-Path $Executable)) {
    Write-Error "找不到可执行文件: $Executable"
    exit 1
}

Write-Host "服务目录: $ServiceDir"
Write-Host "可执行文件: $Executable"

# 检查服务是否存在
$existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existingService) {
    Write-Host "服务已存在，正在停止并删除..."
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

# 创建服务
$binPath = "`"$Executable`""
$result = sc.exe create $ServiceName binPath= $binPath start= auto DisplayName= "ObsidianRAG Service"
if ($LASTEXITCODE -ne 0) {
    Write-Error "创建服务失败: $result"
    exit 1
}
Write-Host "[SC] CreateService SUCCESS"

# 设置服务恢复选项
sc.exe failure $ServiceName reset= 86400 actions= restart/10000/restart/20000/restart/30000 | Out-Null

# 设置服务环境变量（通过注册表）
$regPath = "HKLM\SYSTEM\CurrentControlSet\Services\$ServiceName"
$envValue = "ASPNETCORE_URLS=http://0.0.0.0:$Port"
# 追加到现有环境变量
$existingEnv = (Get-ItemProperty -Path "Registry::$regPath" -Name "Environment" -ErrorAction SilentlyContinue).Environment
if ($existingEnv) {
    $envArray = @($existingEnv) + $envValue
} else {
    $envArray = @($envValue)
}
Set-ItemProperty -Path "Registry::$regPath" -Name "Environment" -Value $envArray -Type MultiString
Write-Host "设置监听端口: $Port"

# 设置服务描述
sc.exe description $ServiceName "Obsidian RAG 向量检索服务" | Out-Null

# 启动服务
Write-Host "正在启动服务..."
try {
    Start-Service -Name $ServiceName
    Write-Host ""
    Write-Host "========================================" -ForegroundColor Green
    Write-Host "服务已成功安装并启动" -ForegroundColor Green
    Write-Host "========================================" -ForegroundColor Green
    Write-Host ""
    Write-Host "访问地址: http://localhost:$Port"
    Write-Host "查看状态: Get-Service $ServiceName"
    Write-Host "查看日志: Get-Content (Join-Path `"$ServiceDir`" 'logs\*.log')"
} catch {
    Write-Warning "服务创建成功但启动失败: $_"
    Write-Host "请检查日志文件: $ServiceDir\logs\"
}
