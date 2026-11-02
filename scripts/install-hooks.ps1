<#
.SYNOPSIS
    ReferenceRAG - Git Pre-commit 安全门禁钩子安装脚本
.DESCRIPTION
    配置本地 Git core.hooksPath 指向 .githooks 目录。
#>

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Split-Path -Parent $ScriptDir
Set-Location $RepoRoot

Write-Host "Configuring Git core.hooksPath to .githooks..." -ForegroundColor Cyan
git config core.hooksPath .githooks

if ($LASTEXITCODE -eq 0) {
    Write-Host "[SUCCESS] Pre-commit security hook registered successfully!" -ForegroundColor Green
    Write-Host "All git commits will now automatically run scripts/security-scan.ps1 -StagedOnly." -ForegroundColor Green
} else {
    Write-Error "Failed to set git config core.hooksPath."
}
