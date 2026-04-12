<#
.SYNOPSIS
    Check ObsidianRAG Service Status
.DESCRIPTION
    Display detailed status information about the ObsidianRAG service.
#>

$ErrorActionPreference = "Continue"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ServiceName = "ObsidianRAG"

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  ObsidianRAG Service Status" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Get service
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $service) {
    Write-Host "Service '$ServiceName' is not installed." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "To install, run: .\install-service.ps1" -ForegroundColor Green
    cmd /c pause
    exit 0
}

# Basic status
Write-Host "Service Name: $ServiceName"
Write-Host "Status:       " -NoNewline
switch ($service.Status) {
    "Running"  { Write-Host "Running" -ForegroundColor Green }
    "Stopped"  { Write-Host "Stopped" -ForegroundColor Red }
    "Paused"   { Write-Host "Paused" -ForegroundColor Yellow }
    default    { Write-Host $service.Status -ForegroundColor Yellow }
}
Write-Host "Start Type:   $($service.StartType)"
Write-Host "Can Pause:    $($service.CanPauseAndContinue)"
Write-Host ""

# Get registry info
$regPath = "HKLM\SYSTEM\CurrentControlSet\Services\$ServiceName"
try {
    $workingDir = (Get-ItemProperty -Path "Registry::$regPath" -Name "WorkingDirectory" -ErrorAction SilentlyContinue).WorkingDirectory
    $envVars = (Get-ItemProperty -Path "Registry::$regPath" -Name "Environment" -ErrorAction SilentlyContinue).Environment

    if ($workingDir) {
        Write-Host "Working Dir:  $workingDir"
    }
    if ($envVars) {
        Write-Host "Environment:"
        foreach ($env in $envVars) {
            Write-Host "  $env" -ForegroundColor Gray
        }
    }
} catch {}

Write-Host ""

# Process info if running
if ($service.Status -eq "Running") {
    Write-Host "----------------------------------------" -ForegroundColor DarkGray
    Write-Host "Process Information" -ForegroundColor Cyan
    Write-Host "----------------------------------------" -ForegroundColor DarkGray

    # Find the process
    $exePath = if ($workingDir) { Join-Path $workingDir "ObsidianRAG.Service.exe" } else { $null }

    $procs = Get-Process -Name "ObsidianRAG.Service" -ErrorAction SilentlyContinue
    if (-not $procs) {
        # Try dotnet process
        $procs = Get-Process -Name "dotnet" -ErrorAction SilentlyContinue | Where-Object {
            $_.Path -like "*ObsidianRAG*"
        }
    }

    if ($procs) {
        $proc = $procs | Select-Object -First 1
        Write-Host "Process ID:   $($proc.Id)"
        Write-Host "Memory (MB):  $([math]::Round($proc.WorkingSet64 / 1MB, 2))"
        if ($proc.TotalProcessorTime) {
            Write-Host "CPU Time:     $($proc.TotalProcessorTime.ToString('hh\:mm\:ss'))"
        }
        if ($proc.StartTime) {
            Write-Host "Start Time:   $($proc.StartTime)"
        }
        if ($proc.Path) {
            Write-Host "Path:         $($proc.Path)"
        }
    } else {
        Write-Host "Process not found" -ForegroundColor Yellow
    }
    Write-Host ""

    # Check if API is responding
    Write-Host "----------------------------------------" -ForegroundColor DarkGray
    Write-Host "API Health Check" -ForegroundColor Cyan
    Write-Host "----------------------------------------" -ForegroundColor DarkGray

    $port = 5000
    if ($envVars) {
        $urlEnv = $envVars | Where-Object { $_ -like "ASPNETCORE_URLS*" } | Select-Object -First 1
        if ($urlEnv -match ":(\d+)") {
            $port = $matches[1]
        }
    }

    try {
        $response = Invoke-WebRequest -Uri "http://localhost:$port/health" -TimeoutSec 5 -UseBasicParsing -ErrorAction Stop
        Write-Host "Health:       OK" -ForegroundColor Green
        Write-Host "API URL:      http://localhost:$port"
    } catch {
        Write-Host "Health:       Not responding" -ForegroundColor Yellow
        Write-Host "API URL:      http://localhost:$port"
        Write-Host "Error:       $($_.Exception.Message)" -ForegroundColor Gray
    }
    Write-Host ""
}

# Recent logs
if ($workingDir) {
    $logDir = Join-Path $workingDir "logs"
    if (Test-Path $logDir) {
        $logFile = Get-ChildItem -Path $logDir -Filter "*.log" -ErrorAction SilentlyContinue |
                   Sort-Object LastWriteTime -Descending |
                   Select-Object -First 1

        if ($logFile) {
            Write-Host "----------------------------------------" -ForegroundColor DarkGray
            Write-Host "Recent Logs (last 10 lines)" -ForegroundColor Cyan
            Write-Host "----------------------------------------" -ForegroundColor DarkGray
            Write-Host "Log File:     $($logFile.Name)"
            Write-Host ""
            Get-Content $logFile.FullName -Tail 10 | ForEach-Object {
                if ($_ -match "error|Error|ERROR|fail|Fail|FAIL") {
                    Write-Host $_ -ForegroundColor Red
                } elseif ($_ -match "warn|Warn|WARN") {
                    Write-Host $_ -ForegroundColor Yellow
                } else {
                    Write-Host $_
                }
            }
            Write-Host ""
        }
    }
}

Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
cmd /c pause
