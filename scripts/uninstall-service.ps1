#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Uninstall ObsidianRAG Windows Service
.DESCRIPTION
    Stops and removes the ObsidianRAG Windows Service.
.PARAMETER Force
    Skip confirmation prompt
.EXAMPLE
    .\uninstall-service.ps1
    .\uninstall-service.ps1 -Force
#>

param(
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$ServiceName = "ObsidianRAG"

# Check if running as Administrator
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Error "This script must be run as Administrator. Please restart PowerShell as admin."
    exit 1
}

# Check if service exists
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $service) {
    Write-Host "Service '$ServiceName' is not installed." -ForegroundColor Yellow
    exit 0
}

# Confirmation
if (-not $Force) {
    Write-Host "This will stop and remove the service '$ServiceName'." -ForegroundColor Yellow
    $response = Read-Host "Continue? (y/N)"
    if ($response -ne "y" -and $response -ne "Y") {
        Write-Host "Cancelled."
        exit 0
    }
}

# Stop service if running
if ($service.Status -eq "Running") {
    Write-Host "Stopping service..." -ForegroundColor Yellow
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue

    # Wait for service to stop
    $timeout = 30
    $elapsed = 0
    while ($service.Status -ne "Stopped" -and $elapsed -lt $timeout) {
        Start-Sleep -Seconds 1
        $elapsed++
        $service = Get-Service -Name $ServiceName
    }

    if ($service.Status -eq "Running") {
        Write-Warning "Service did not stop within $timeout seconds. Forcing..."
    }
}

# Find NSSM path
$nssm = $null
$commonPaths = @(
    "C:\Program Files\nssm\nssm.exe",
    "C:\Program Files (x86)\nssm\nssm.exe",
    "$env:ProgramFiles\nssm\nssm.exe",
    "$env:LOCALAPPDATA\nssm\nssm.exe"
)
foreach ($path in $commonPaths) {
    if (Test-Path $path) {
        $nssm = $path
        break
    }
}
if (-not $nssm) {
    $nssm = Get-Command nssm.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source
}

if ($nssm) {
    Write-Host "Removing service using NSSM..." -ForegroundColor Yellow
    & $nssm remove $ServiceName /confirm 2>$null
}

# Also try native sc delete
$scResult = sc.exe delete $ServiceName 2>&1
if ($LASTEXITCODE -eq 0) {
    Write-Host "Service '$ServiceName' has been removed." -ForegroundColor Green
} else {
    Write-Warning "SC delete returned: $scResult"
}

# Refresh service list
Start-Sleep -Seconds 1
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $service) {
    Write-Host ""
    Write-Host "Service uninstalled successfully." -ForegroundColor Green
} else {
    Write-Warning "Service may still exist. Status: $($service.Status)"
}
