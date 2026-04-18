<#
.SYNOPSIS
    Stop ReferenceRAG Service
.DESCRIPTION
    Stop the ReferenceRAG Windows Service.
#>

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ServiceName = "ReferenceRAG"

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Stop ReferenceRAG Service" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Check if running as Administrator
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host "Requesting administrator privileges..." -ForegroundColor Yellow
    $scriptPath = $MyInvocation.MyCommand.Path
    Start-Process powershell.exe -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -NoExit -File `"$scriptPath`"" -Wait
    exit 0
}

# Get service
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $service) {
    Write-Host "Service '$ServiceName' is not installed." -ForegroundColor Red
    Write-Host ""
    Write-Host "To install, run: .\install-service.ps1" -ForegroundColor Green
    exit 1
}

# Check if already stopped
if ($service.Status -eq "Stopped") {
    Write-Host "Service is already stopped." -ForegroundColor Yellow
    Write-Host ""
    cmd /c pause
    exit 0
}

# Stop service
Write-Host "Stopping service..." -ForegroundColor Yellow
Stop-Service -Name $ServiceName -Force

# Wait for service to stop
$timeout = 30
$elapsed = 0
while ((Get-Service -Name $ServiceName).Status -ne "Stopped" -and $elapsed -lt $timeout) {
    Start-Sleep -Milliseconds 500
    $elapsed += 0.5
}

$service = Get-Service -Name $ServiceName
Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Service:      $ServiceName"
Write-Host "Status:       " -NoNewline
if ($service.Status -eq "Stopped") {
    Write-Host "Stopped" -ForegroundColor Green
} else {
    Write-Host "$($service.Status)" -ForegroundColor Red
}
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
cmd /c pause
