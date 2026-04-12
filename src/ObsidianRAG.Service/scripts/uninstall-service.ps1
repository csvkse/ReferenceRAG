# ObsidianRAG Service Uninstall Script (Windows)
# Run as Administrator

param([string]$ServiceName = "ObsidianRAG")

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

# Check admin privileges and auto-elevate if needed
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "Requesting administrator privileges..." -ForegroundColor Yellow
    $scriptPath = $MyInvocation.MyCommand.Path
    $args = @("-ServiceName", $ServiceName)
    Start-Process powershell.exe -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -NoExit -File `"$scriptPath`" $($args -join ' ')" -Wait
    exit 0
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
