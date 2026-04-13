<#
.SYNOPSIS
    One-click deploy script for ReferenceRAG Service
.DESCRIPTION
    Stops the Windows service, publishes the application, then restarts the service.
    Supports both Windows Service and Console modes.
.PARAMETER Configuration
    Build configuration (Debug or Release). Default: Release
.PARAMETER ServiceName
    Windows Service name. Default: ReferenceRAG
.PARAMETER Port
    Service listening port. Default: 5000
.PARAMETER SkipService
    Skip service operations (for console mode deployment)
.PARAMETER SkipFrontend
    Skip frontend build
.EXAMPLE
    .\deploy.ps1
    .\deploy.ps1 -Configuration Debug
    .\deploy.ps1 -SkipService
#>

param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$ServiceName = "ReferenceRAG",

    [int]$Port = 5000,

    [switch]$SkipService,

    [switch]$SkipFrontend
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

# Script paths
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# Determine project root based on script location
# Script can be in: resource/scripts/ or src/ReferenceRAG.Service/scripts/
if ($ScriptDir -like "*\resource\scripts*") {
    $ProjectRoot = Split-Path -Parent (Split-Path -Parent $ScriptDir)
} elseif ($ScriptDir -like "*\ReferenceRAG.Service\scripts*") {
    $ProjectRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $ScriptDir))
} else {
    $ProjectRoot = Split-Path -Parent $ScriptDir
}

$ServiceProject = Join-Path $ProjectRoot "src\ReferenceRAG.Service\ReferenceRAG.Service.csproj"
$FrontendDir    = Join-Path $ProjectRoot "dashboard-vue"

# PublishDir is a fallback default; will be overridden if .pubxml is found
$PublishDir = Join-Path $ProjectRoot "src\ReferenceRAG.Service\bin\Publish"

# ---------------------------------------------------------------------------
# Helper: resolve actual publish output directory from .pubxml
# ---------------------------------------------------------------------------
function Resolve-PublishDir {
    $profileDir = Join-Path (Split-Path $ServiceProject) "Properties\PublishProfiles"
    $pubxml = Get-ChildItem -Path $profileDir -Filter "*.pubxml" -ErrorAction SilentlyContinue |
              Select-Object -First 1

    if (-not $pubxml) { return $null }

    try {
        [xml]$prof = Get-Content $pubxml.FullName -Encoding UTF8
        $url = $prof.Project.PropertyGroup.publishUrl
        if (-not $url) { return $null }

        $url = [System.Environment]::ExpandEnvironmentVariables($url)

        if (-not [System.IO.Path]::IsPathRooted($url)) {
            $url = Join-Path (Split-Path $ServiceProject) $url
        }

        return [System.IO.Path]::GetFullPath($url)
    } catch {
        Write-Warning "Failed to parse .pubxml: $_"
        return $null
    }
}

function Write-Step {
    param([string]$Message)
    Write-Host ""
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host " $Message" -ForegroundColor Cyan
    Write-Host "========================================" -ForegroundColor Cyan
}

function Test-Administrator {
    $user      = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($user)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Test-ServiceExists {
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    return $null -ne $service
}

# ---------------------------------------------------------------------------
# Stop service
# ---------------------------------------------------------------------------
function Stop-ReferenceRAGService {
    if (-not (Test-ServiceExists)) {
        Write-Host "Service '$ServiceName' not found, skipping stop." -ForegroundColor Yellow
        return $false
    }

    $service = Get-Service -Name $ServiceName
    if ($service.Status -eq "Stopped") {
        Write-Host "Service '$ServiceName' is already stopped." -ForegroundColor Green
        return $true
    }

    Write-Host "Stopping service '$ServiceName'..." -ForegroundColor Yellow

    try {
        Stop-Service -Name $ServiceName -Force -ErrorAction Stop

        $maxWait = 30
        $waited  = 0
        while ((Get-Service -Name $ServiceName -ErrorAction SilentlyContinue).Status -ne "Stopped" -and $waited -lt $maxWait) {
            Start-Sleep -Seconds 1
            $waited++
            Write-Host "." -NoNewline
        }
        Write-Host ""

        $finalStatus = (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue).Status
        if ($finalStatus -eq "Stopped") {
            Write-Host "Service stopped successfully." -ForegroundColor Green
            return $true
        } else {
            Write-Warning "Service did not stop within $maxWait seconds. Current status: $finalStatus"
            return $false
        }
    } catch {
        Write-Warning "Failed to stop service: $_"
        return $false
    }
}

# ---------------------------------------------------------------------------
# Start service
# ---------------------------------------------------------------------------
function Start-ReferenceRAGService {
    if (-not (Test-ServiceExists)) {
        Write-Host "Service '$ServiceName' not found, skipping start." -ForegroundColor Yellow
        return $false
    }

    $service = Get-Service -Name $ServiceName
    if ($service.Status -eq "Running") {
        Write-Host "Service '$ServiceName' is already running." -ForegroundColor Green
        return $true
    }

    Write-Host "Starting service '$ServiceName'..." -ForegroundColor Yellow

    try {
        Start-Service -Name $ServiceName -ErrorAction Stop

        $maxWait = 30
        $waited  = 0
        while ((Get-Service -Name $ServiceName -ErrorAction SilentlyContinue).Status -ne "Running" -and $waited -lt $maxWait) {
            Start-Sleep -Seconds 1
            $waited++
            Write-Host "." -NoNewline
        }
        Write-Host ""

        $finalStatus = (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue).Status
        if ($finalStatus -eq "Running") {
            Write-Host "Service started successfully." -ForegroundColor Green
            return $true
        } else {
            Write-Warning "Service did not start within $maxWait seconds. Current status: $finalStatus"
            return $false
        }
    } catch {
        Write-Warning "Failed to start service: $_"
        return $false
    }
}

# ---------------------------------------------------------------------------
# Build frontend
# ---------------------------------------------------------------------------
function Build-Frontend {
    if ($SkipFrontend) {
        Write-Host "Skipping frontend build." -ForegroundColor Yellow
        return $true
    }

    if (-not (Test-Path $FrontendDir)) {
        Write-Warning "Frontend directory not found: $FrontendDir"
        return $true
    }

    Write-Host "Building frontend..." -ForegroundColor Yellow

    Push-Location $FrontendDir
    try {
        if (-not (Test-Path "node_modules")) {
            Write-Host "Installing npm dependencies..." -ForegroundColor Yellow
            npm install
            if ($LASTEXITCODE -ne 0) { throw "npm install failed with exit code $LASTEXITCODE" }
        }

        npm run build
        if ($LASTEXITCODE -ne 0) { throw "npm run build failed with exit code $LASTEXITCODE" }

        Write-Host "Frontend build completed." -ForegroundColor Green
        return $true
    } catch {
        Write-Warning "Frontend build failed: $_"
        return $false
    } finally {
        Pop-Location
    }
}

# ---------------------------------------------------------------------------
# Publish service — honours .pubxml when present (matches Visual Studio)
# ---------------------------------------------------------------------------
function Publish-Service {
    Write-Host "Publishing service (Configuration: $Configuration)..." -ForegroundColor Yellow

    $profileDir = Join-Path (Split-Path $ServiceProject) "Properties\PublishProfiles"
    $pubxml     = Get-ChildItem -Path $profileDir -Filter "*.pubxml" -ErrorAction SilentlyContinue |
                  Select-Object -First 1

    if ($pubxml) {
        Write-Host "Using publish profile: $($pubxml.Name)" -ForegroundColor Yellow

        dotnet publish $ServiceProject `
            -c $Configuration `
            /p:PublishProfile=$($pubxml.FullName)
    } else {
        Write-Warning "No .pubxml publish profile found in: $profileDir"
        Write-Warning "Falling back to manual publish parameters..."

        if (Test-Path $PublishDir) {
            Write-Host "Cleaning publish directory..." -ForegroundColor Yellow
            Remove-Item -Path $PublishDir -Recurse -Force
        }

        dotnet publish $ServiceProject `
            -c $Configuration `
            -r win-x64 `
            --self-contained true `
            -o $PublishDir
    }

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE"
    }

    Write-Host "Service published successfully." -ForegroundColor Green
    return $true
}

# ---------------------------------------------------------------------------
# Main deployment
# ---------------------------------------------------------------------------
function Invoke-Deployment {
    $startTime = Get-Date
    $success   = $true

    # Resolve actual publish output directory (may update the module-level variable)
    $resolved = Resolve-PublishDir
    if ($resolved) { $script:PublishDir = $resolved }

    Write-Host ""
    Write-Host "==========================================" -ForegroundColor Magenta
    Write-Host " ReferenceRAG One-Click Deploy"            -ForegroundColor Magenta
    Write-Host "==========================================" -ForegroundColor Magenta
    Write-Host "Configuration: $Configuration"
    Write-Host "Service Name:  $ServiceName"
    Write-Host "Port:          $Port"
    Write-Host "Skip Service:  $SkipService"
    Write-Host "Skip Frontend: $SkipFrontend"
    Write-Host "Publish Dir:   $PublishDir"
    Write-Host ""

    try {
        # Step 1: Stop service
        if (-not $SkipService) {
            Write-Step "Step 1: Stop Service"
            if (-not (Stop-ReferenceRAGService)) {
                Write-Warning "Service stop failed or service not found. Continuing..."
            }
        }

        # Step 2: Build frontend
        Write-Step "Step 2: Build Frontend"
        if (-not (Build-Frontend)) {
            throw "Frontend build failed. Aborting deployment."
        }

        # Step 3: Publish service
        Write-Step "Step 3: Publish Service"
        Publish-Service

        # Step 4: Start service
        if (-not $SkipService) {
            Write-Step "Step 4: Start Service"
            if (-not (Start-ReferenceRAGService)) {
                Write-Warning "Service start failed. Please check service logs."
            }
        }
    } catch {
        Write-Error $_
        $success = $false
    }

    $endTime  = Get-Date
    $duration = $endTime - $startTime

    Write-Host ""
    Write-Host "==========================================" -ForegroundColor $(if ($success) { "Green" } else { "Red" })
    Write-Host " Deployment $(if ($success) { "Completed" } else { "Failed" })" -ForegroundColor $(if ($success) { "Green" } else { "Red" })
    Write-Host " Duration: $($duration.ToString('mm\:ss'))"                      -ForegroundColor $(if ($success) { "Green" } else { "Red" })
    Write-Host "==========================================" -ForegroundColor $(if ($success) { "Green" } else { "Red" })

    if ($success) {
        Write-Host ""
        Write-Host "Service URL: http://localhost:$Port"            -ForegroundColor Cyan
        Write-Host "Dashboard:   http://localhost:$Port/index.html" -ForegroundColor Cyan
        Write-Host "API Docs:    http://localhost:$Port/swagger"    -ForegroundColor Cyan
        Write-Host "Logs:        $PublishDir\logs\"                 -ForegroundColor Cyan
    }

    return $success
}

# ---------------------------------------------------------------------------
# Entry point — re-launch as admin when service operations are needed
# ---------------------------------------------------------------------------
if (-not $SkipService -and -not (Test-Administrator)) {
    Write-Host "Requesting administrator privileges for service operations..." -ForegroundColor Yellow
    $scriptPath = $MyInvocation.MyCommand.Path
    $argList = @("-Configuration", $Configuration, "-ServiceName", $ServiceName, "-Port", $Port)
    if ($SkipFrontend) { $argList += "-SkipFrontend" }

    $process = Start-Process powershell.exe -Verb RunAs `
        -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$scriptPath`" $($argList -join ' ')" `
        -Wait -PassThru
    exit $process.ExitCode
}

try {
    $result = Invoke-Deployment
    exit $(if ($result) { 0 } else { 1 })
} catch {
    Write-Error "Deployment failed: $_"
    exit 1
}