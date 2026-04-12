<#
.SYNOPSIS
    Control ObsidianRAG Windows Service
.DESCRIPTION
    Start, stop, restart, or check status of the ObsidianRAG service.
.PARAMETER Action
    Action to perform: start, stop, restart, status
.EXAMPLE
    .\servicectl.ps1 status
    .\servicectl.ps1 stop
    .\servicectl.ps1 restart
#>

param(
    [Parameter(Position = 0)]
    [ValidateSet("start", "stop", "restart", "status")]
    [string]$Action = "status"
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ServiceName = "ObsidianRAG"

# Check if running as Administrator for start/stop/restart
$needsAdmin = $Action -in @("start", "stop", "restart")
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if ($needsAdmin -and -not $isAdmin) {
    Write-Host "Requesting administrator privileges..." -ForegroundColor Yellow
    $scriptPath = $MyInvocation.MyCommand.Path
    Start-Process powershell.exe -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -NoExit -File `"$scriptPath`" $Action" -Wait
    exit 0
}

# Get service
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $service) {
    Write-Host "Service '$ServiceName' is not installed." -ForegroundColor Yellow
    exit 1
}

switch ($Action) {
    "status" {
        $service = Get-Service -Name $ServiceName
        Write-Host "Service: $ServiceName"
        Write-Host "Status: $($service.Status)"
        Write-Host "Start Type: $($service.StartType)"

        if ($service.Status -eq "Running") {
            # Get process info
            $proc = Get-Process -Name "dotnet" -ErrorAction SilentlyContinue | Where-Object {
                $_.CommandLine -like "*ObsidianRAG*" -or $_.Path -like "*ObsidianRAG*"
            } | Select-Object -First 1
            if ($proc) {
                Write-Host "Process ID: $($proc.Id)"
                Write-Host "Memory: $([math]::Round($proc.WorkingSet64 / 1MB, 2)) MB"
            }
        }
    }

    "start" {
        if ($service.Status -eq "Running") {
            Write-Host "Service is already running." -ForegroundColor Yellow
        } else {
            Write-Host "Starting service..." -ForegroundColor Green
            Start-Service -Name $ServiceName
            Start-Sleep -Seconds 2
            $service = Get-Service -Name $ServiceName
            Write-Host "Service status: $($service.Status)" -ForegroundColor Green
        }
    }

    "stop" {
        if ($service.Status -eq "Stopped") {
            Write-Host "Service is already stopped." -ForegroundColor Yellow
        } else {
            Write-Host "Stopping service..." -ForegroundColor Yellow
            Stop-Service -Name $ServiceName -Force
            Start-Sleep -Seconds 2
            $service = Get-Service -Name $ServiceName
            Write-Host "Service status: $($service.Status)" -ForegroundColor Green
        }
    }

    "restart" {
        Write-Host "Restarting service..." -ForegroundColor Green
        if ($service.Status -eq "Running") {
            Stop-Service -Name $ServiceName -Force
            Start-Sleep -Seconds 2
        }
        Start-Service -Name $ServiceName
        Start-Sleep -Seconds 3
        $service = Get-Service -Name $ServiceName
        Write-Host "Service status: $($service.Status)" -ForegroundColor Green
    }
}
