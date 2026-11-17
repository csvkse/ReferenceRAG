<#
.SYNOPSIS
    ReferenceRAG - Git 客户端门禁与版本钩子安装脚本
.DESCRIPTION
    配置本地 Git core.hooksPath 指向 .githooks 目录，启用 pre-commit 安全扫描与 pre-push 版本守门员。
#>

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Split-Path -Parent $ScriptDir
Set-Location $RepoRoot

Write-Host "Configuring Git core.hooksPath to .githooks..." -ForegroundColor Cyan
git config core.hooksPath .githooks

if ($LASTEXITCODE -eq 0) {
    Write-Host "[SUCCESS] Git hooks registered successfully!" -ForegroundColor Green
    Write-Host "  1. pre-commit : 自动运行 scripts/security-scan.ps1 安全与敏感信息门禁" -ForegroundColor Green
    Write-Host "  2. pre-push   : 自动检测版本标签推送并联动 MSBuild 动态版本注入守卫" -ForegroundColor Green
} else {
    Write-Error "Failed to set git config core.hooksPath."
}
