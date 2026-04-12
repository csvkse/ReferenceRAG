# ObsidianRAG Service Uninstall Script (Windows)
# Run as Administrator

param([string]$ServiceName = "ObsidianRAG")

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

# Check admin privileges
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "ERROR: Administrator privileges required" -ForegroundColor Red
    Write-Host "Please run PowerShell as Administrator and try again" -ForegroundColor Yellow
    exit 1
}

$existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existingService) {
    Write-Host "Stopping service..."
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    Write-Host "Removing service..."
    sc.exe delete $ServiceName | Out-Null
    Write-Host "Service uninstalled" -ForegroundColor Green
} else {
    Write-Host "Service not found" -ForegroundColor Yellow
}
