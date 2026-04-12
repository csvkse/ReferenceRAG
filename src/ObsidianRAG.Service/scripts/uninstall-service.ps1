# ObsidianRAG 服务卸载脚本 (Windows)
# 用法: 以管理员身份运行

param([string]$ServiceName = "ObsidianRAG")

$ErrorActionPreference = "Stop"

$existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existingService) {
    Stop-Service -Name $ServiceName -Force
    sc.exe delete $ServiceName
    Write-Host "服务已卸载"
} else {
    Write-Host "服务不存在"
}
