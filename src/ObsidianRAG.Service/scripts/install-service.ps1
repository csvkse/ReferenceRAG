# ObsidianRAG Service Install Script (Windows)
# Run as Administrator

param(
    [string]$ServiceName = "ObsidianRAG",
    [int]$Port = 5000
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

# Check admin privileges
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "ERROR: Administrator privileges required" -ForegroundColor Red
    Write-Host "Please run PowerShell as Administrator and try again" -ForegroundColor Yellow
    exit 1
}

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ServiceDir = Split-Path -Parent $ScriptDir
$Executable = Join-Path $ServiceDir "ObsidianRAG.Service.exe"

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
$result = sc.exe create $ServiceName binPath= $binPath start= auto DisplayName= "ObsidianRAG Service"
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

# Set ASPNETCORE_URLS
$envValue = "ASPNETCORE_URLS=http://0.0.0.0:$Port"
$existingEnv = (Get-ItemProperty -Path "Registry::$regPath" -Name "Environment" -ErrorAction SilentlyContinue).Environment
if ($existingEnv) {
    $envArray = @($existingEnv) + $envValue
} else {
    $envArray = @($envValue)
}
Set-ItemProperty -Path "Registry::$regPath" -Name "Environment" -Value $envArray -Type MultiString
Write-Host "Listening port: $Port"

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
} catch {
    Write-Warning "Service created but failed to start: $_"
    Write-Host "Check logs: $ServiceDir\logs\"
    exit 1
}
