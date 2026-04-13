# ReferenceRAG Service Install Script (Windows)
# Run as Administrator

param(
    [string]$ServiceName = "ReferenceRAG",
    [int]$Port = 5000,
    [string]$CudaPath = "",
    [switch]$SkipCuda
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

# Check admin privileges and auto-elevate if needed
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "Requesting administrator privileges..." -ForegroundColor Yellow
    $scriptPath = $MyInvocation.MyCommand.Path
    $args = @("-ServiceName", $ServiceName, "-Port", $Port)
    if ($CudaPath) { $args += @("-CudaPath", $CudaPath) }
    if ($SkipCuda) { $args += "-SkipCuda" }
    Start-Process powershell.exe -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -NoExit -File `"$scriptPath`" $($args -join ' ')" -Wait
    exit 0
}

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ServiceDir = Split-Path -Parent $ScriptDir
$Executable = Join-Path $ServiceDir "ReferenceRAG.Service.exe"

if (-not (Test-Path $Executable)) {
    Write-Error "Executable not found: $Executable"
    exit 1
}

Write-Host "Service directory: $ServiceDir"
Write-Host "Executable: $Executable"

# Check if service exists
$existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existingService) {
    Write-Host "Service exists, stopping and removing..."
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

# Create service
$binPath = '"' + $Executable + '"'
$result = sc.exe create $ServiceName binPath= $binPath start= auto DisplayName= "ReferenceRAG Service"
if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to create service: $result"
    exit 1
}
Write-Host "[SC] CreateService SUCCESS"

# Set service recovery options
sc.exe failure $ServiceName reset= 86400 actions= restart/10000/restart/20000/restart/30000 | Out-Null

# Set service environment variable via registry
$regPath = "HKLM\SYSTEM\CurrentControlSet\Services\$ServiceName"

# Set working directory
Set-ItemProperty -Path "Registry::$regPath" -Name "WorkingDirectory" -Value $ServiceDir -Type ExpandString
Write-Host "Working directory: $ServiceDir"

# Build environment variables array
$envArray = @()

# Set ASPNETCORE_URLS
$envArray += "ASPNETCORE_URLS=http://0.0.0.0:$Port"
# Set service name for restart API detection
$envArray += "REFERENCERAG_SERVICE_NAME=$ServiceName"
Write-Host "Listening port: $Port"

# Detect and add CUDA path
if (-not $SkipCuda) {
    $cudaPaths = @()

    # Parse provided CUDA path(s) - support semicolon or comma separated
    if (-not [string]::IsNullOrEmpty($CudaPath)) {
        $cudaPaths = $CudaPath -split '[;,]' | Where-Object { $_.Trim() } | ForEach-Object { $_.Trim() }
    } else {
        # Auto-detect CUDA path
        $cudaEnv = [Environment]::GetEnvironmentVariable("CUDA_PATH", "Machine")
        if (-not [string]::IsNullOrEmpty($cudaEnv)) {
            $cudaBinPath = Join-Path $cudaEnv "bin"
            if (Test-Path $cudaBinPath) {
                $cudaPaths = @($cudaBinPath)
            }
        }
    }

    # Validate paths exist
    $validCudaPaths = @()
    foreach ($p in $cudaPaths) {
        if (Test-Path $p) {
            $validCudaPaths += $p
            Write-Host "CUDA path verified: $p" -ForegroundColor Green
        } else {
            Write-Host "CUDA path not found: $p" -ForegroundColor Yellow
        }
    }

    if ($validCudaPaths.Count -gt 0) {
        # Build PATH with CUDA paths first
        $cudaPathString = $validCudaPaths -join ';'
        $systemPath = [Environment]::GetEnvironmentVariable("PATH", "Machine")
        $envArray += "PATH=$cudaPathString;$systemPath"
        Write-Host "CUDA enabled with $($validCudaPaths.Count) path(s)" -ForegroundColor Green
    } else {
        Write-Host "CUDA not detected, using CPU mode" -ForegroundColor Yellow
        Write-Host "To enable CUDA, specify -CudaPath parameter (supports multiple paths separated by ; or ,)"
    }
}

# Set environment variables
Set-ItemProperty -Path "Registry::$regPath" -Name "Environment" -Value $envArray -Type MultiString

# Set service description
sc.exe description $ServiceName "Obsidian RAG Vector Search Service" | Out-Null

# Start service
Write-Host "Starting service..."
try {
    Start-Service -Name $ServiceName
    Write-Host ""
    Write-Host "========================================" -ForegroundColor Green
    Write-Host "Service installed and started" -ForegroundColor Green
    Write-Host "========================================" -ForegroundColor Green
    Write-Host ""
    Write-Host "URL: http://localhost:$Port"
    Write-Host "Status: Get-Service $ServiceName"
    $logPath = Join-Path $ServiceDir "logs"
    Write-Host "Logs: $logPath"
    Write-Host ""
    Write-Host "Usage:" -ForegroundColor Cyan
    Write-Host "  .\servicectl.ps1 status   # Check status"
    Write-Host "  .\servicectl.ps1 stop     # Stop service"
    Write-Host "  .\servicectl.ps1 restart  # Restart service"
} catch {
    Write-Warning "Service created but failed to start: $_"
    Write-Host "Check logs: $ServiceDir\logs\"
    exit 1
}
