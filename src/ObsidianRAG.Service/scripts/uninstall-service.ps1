# ObsidianRAG 服务卸载脚本 (Windows)
# 用法: 以管理员身份运行

param([string]$ServiceName = "ObsidianRAG")

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

# 检查管理员权限
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "错误: 需要管理员权限运行此脚本" -ForegroundColor Red
    Write-Host "请右键点击 PowerShell，选择'以管理员身份运行'，然后重新执行此脚本" -ForegroundColor Yellow
    exit 1
}

$existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existingService) {
    Write-Host "正在停止服务..."
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    Write-Host "正在删除服务..."
    sc.exe delete $ServiceName | Out-Null
    Write-Host "服务已卸载" -ForegroundColor Green
} else {
    Write-Host "服务不存在" -ForegroundColor Yellow
}
