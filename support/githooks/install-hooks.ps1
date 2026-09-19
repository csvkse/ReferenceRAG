<#
.SYNOPSIS
    将 Git Hooks 路径配置为 support/githooks
#>
$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Set-Location $RepoRoot

git config core.hooksPath "support/githooks"
Write-Host "Git hooks path successfully set to: support/githooks" -ForegroundColor Green
