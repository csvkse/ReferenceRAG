#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Install ObsidianRAG as a Windows Service using NSSM
.DESCRIPTION
    Installs the ObsidianRAG service with automatic startup configuration.
    Requires NSSM (Non-Sucking Service Manager) to be installed or available in PATH.
.PARAMETER ServicePath
    Path to the ObsidianRAG.Service.dll (default: same directory as script)
.PARAMETER ServiceUser
    User to run the service under (default: NT AUTHORITY\LocalService)
.PARAMETER NSSMPath
    Path to nssm.exe if not in PATH
.EXAMPLE
    .\install-service.ps1
    .\install-service.ps1 -ServicePath "C:\Program Files\ObsidianRAG" -ServiceUser "DOMAIN\user"
#>

param(
    [string]$ServicePath = $PSScriptRoot,
    [string]$ServiceUser = "NT AUTHORITY\LocalService",
    [string]$NSSMPath = $null
)

$ErrorActionPreference = "Stop"
$ServiceName = "ObsidianRAG"
$DisplayName = "ObsidianRAG Knowledge Base Service"
$Description = "ObsidianRAG Knowledge Base Service - Vector search and RAG API for Obsidian notes"
$DllPath = Join-Path $ServicePath "ObsidianRAG.Service.dll"

# Check if running as Administrator
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Error "This script must be run as Administrator. Please restart PowerShell as admin."
    exit 1
}

# Find NSSM
function Find-NSSM {
    param([string]$CustomPath)

    if ($CustomPath -and (Test-Path $CustomPath)) {
        return $CustomPath
    }

    # Check common locations
    $commonPaths = @(
        "C:\Program Files\nssm\nssm.exe",
        "C:\Program Files (x86)\nssm\nssm.exe",
        "$env:ProgramFiles\nssm\nssm.exe",
        "$env:LOCALAPPDATA\nssm\nssm.exe"
    )

    foreach ($path in $commonPaths) {
        if (Test-Path $path) {
            return $path
        }
    }

    # Check if nssm is in PATH
    $nssmInPath = Get-Command nssm.exe -ErrorAction SilentlyContinue
    if ($nssmInPath) {
        return $nssmInPath.Source
    }

    return $null
}

# Check if service already exists
$existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existingService) {
    Write-Warning "Service '$ServiceName' already exists. Uninstalling first..."
    & "$PSScriptRoot\uninstall-service.ps1" -Force
}

# Verify DLL exists
if (-not (Test-Path $DllPath)) {
    Write-Error "ObsidianRAG.Service.dll not found at: $DllPath"
    Write-Error "Please build the project first: dotnet build -c Release"
    exit 1
}

# Find NSSM
$nssm = Find-NSSM -CustomPath $NSSMPath
if (-not $nssm) {
    Write-Host "NSSM not found. Please install NSSM first or specify -NSSMPath" -ForegroundColor Yellow
    Write-Host "Download from: https://github.com抽取/nssm/releases" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Or install via Chocolatey: choco install nssm" -ForegroundColor Cyan
    exit 1
}

Write-Host "Using NSSM from: $nssm" -ForegroundColor Cyan

# Create service
Write-Host "Installing service '$ServiceName'..." -ForegroundColor Green

& $nssm install $ServiceName "dotnet" "`"$DllPath`""

if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to install service using NSSM"
    exit 1
}

# Configure service
& $nssm set $ServiceName DisplayName $DisplayName
& $nssm set $ServiceName Description $Description
& $nssm set $ServiceName Start SERVICE_AUTO_START
& $nssm set $ServiceName AppStdout (Join-Path $ServicePath "logs\service.log")
& $nssm set $ServiceName AppStderr (Join-Path $ServicePath "logs\service-error.log")
& $nssm set $ServiceName AppRestartDelay 5000
& $nssm set $ServiceName AppPriority NORMAL

# Set service user if specified and not default
if ($ServiceUser -ne "NT AUTHORITY\LocalService") {
    & $nssm set $ServiceName ObjectName $ServiceUser
}

# Configure environment
$env:ASPNETCORE_ENVIRONMENT = "Production"
& $nssm set $ServiceName AppEnvironmentExtra "ASPNETCORE_ENVIRONMENT=Production"

# Create logs directory if not exists
$logsDir = Join-Path $ServicePath "logs"
if (-not (Test-Path $logsDir)) {
    New-Item -ItemType Directory -Path $logsDir | Out-Null
}

# Start the service
Write-Host "Starting service..." -ForegroundColor Green
Start-Service -Name $ServiceName

# Wait a moment and check status
Start-Sleep -Seconds 2
$service = Get-Service -Name $ServiceName

if ($service.Status -eq "Running") {
    Write-Host ""
    Write-Host "Service '$ServiceName' installed and running successfully!" -ForegroundColor Green
    Write-Host "  Status: $($service.Status)"
    Write-Host "  Display Name: $DisplayName"
    Write-Host "  Service User: $ServiceUser"
    Write-Host "  Working Directory: $ServicePath"
    Write-Host ""
    Write-Host "Use './servicectl.ps1 status' to check service status" -ForegroundColor Cyan
    Write-Host "Use './servicectl.ps1 stop' to stop the service" -ForegroundColor Cyan
    Write-Host "Use './servicectl.ps1 restart' to restart the service" -ForegroundColor Cyan
} else {
    Write-Warning "Service installed but status is: $($service.Status)"
    Write-Host "Check logs at: $logsDir\service-error.log" -ForegroundColor Yellow
}
