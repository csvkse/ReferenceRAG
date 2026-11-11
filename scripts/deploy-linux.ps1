<#
.SYNOPSIS
    ReferenceRAG Remote Linux Deployment & Inner-Loop Pipeline Script
.DESCRIPTION
    Implements a production-grade remote deployment workflow for Linux hosts using:
      - Strategy Pattern: Adaptive code sync (Auto / GitArchive / Direct / FrontendHotSync) & Hardware profiles (CPU, CUDA, Intel, ROCm)
      - Adapter Pattern: Windows OpenSSH execution adapter with ACL-restricted temporary key management
      - State Machine Pattern: Predictable deployment state transitions (Probe -> Sync -> Build -> Deploy -> HealthCheck)
.PARAMETER Mode
    Deployment mode: "Dev" (default, fast JIT build & live volume mount) or "Official" (GHCR prebuilt image pull).
.PARAMETER SyncMethod
    Sync strategy: "Auto" (clean tree -> GitArchive, dirty tree -> Direct), "Direct", or "GitArchive".
.PARAMETER FrontendOnly
    Instant 3-second frontend static hot-patch. Syncs wwwroot directly to running container without restart.
.PARAMETER Hardware
    Hardware acceleration strategy: "cpu", "cuda", "intel", or "rocm". Overrides .envdev setting if specified.
.PARAMETER EnvFile
    Path to environment configuration file. Defaults to "env/.envdev".
.PARAMETER CheckOnly
    Probes remote host connectivity, Docker engine, hardware capabilities, and exits.
.PARAMETER RemoteExec
    Executes an arbitrary shell command on the target host over the configured SSH tunnel.
.PARAMETER SkipBuild
    Skips the container image build step in Dev mode.
#>

[CmdletBinding()]
param(
    [ValidateSet("Dev", "Official")]
    [string]$Mode = "Dev",

    [ValidateSet("Auto", "Direct", "GitArchive")]
    [string]$SyncMethod = "Auto",

    [switch]$FrontendOnly,

    [ValidateSet("", "cpu", "cuda", "intel", "rocm")]
    [string]$Hardware = "",

    [string]$EnvFile = "env/.envdev",

    [switch]$CheckOnly,

    [string]$RemoteExec = "",

    [switch]$SkipBuild,

    [switch]$NoHealthCheck,

    [string]$Image = "",

    [ValidateSet("None", "Smoke", "Core", "Full")]
    [string]$SyncDataset = "None"
)

$ErrorActionPreference = "Stop"

# Helper display functions
function Write-Step([string]$msg) {
    Write-Host "`n====> $msg" -ForegroundColor Cyan
}

function Write-Success([string]$msg) {
    Write-Host " [OK] $msg" -ForegroundColor Green
}

function Write-Warn([string]$msg) {
    Write-Host " [!] $msg" -ForegroundColor Yellow
}

function Write-Fail([string]$msg) {
    Write-Host " [FAIL] $msg" -ForegroundColor Red
}

# Resolve Repository Root
$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepoRoot

# ------------------------------------------------------------------------------
# State 1: Configuration Loading & Parsing (Adapter Pattern)
# ------------------------------------------------------------------------------
Write-Step "State [INIT]: Resolving environment configuration from '$EnvFile'..."

$resolvedEnvPath = Join-Path $RepoRoot $EnvFile
if (-not (Test-Path $resolvedEnvPath)) {
    $templatePath = Join-Path $RepoRoot "env/.envdev.template"
    if (Test-Path $templatePath) {
        Write-Warn "Configuration file '$EnvFile' not found. Creating from template '$templatePath'..."
        Copy-Item $templatePath $resolvedEnvPath -Force
        Write-Warn "Please edit '$EnvFile' with your target Linux host credentials and rerun."
        exit 1
    } else {
        throw "Deployment config '$EnvFile' not found, and template '$templatePath' is missing."
    }
}

# Parse env file (supporting multi-line private keys and comments)
$envMap = @{}
$lines = Get-Content -Path $resolvedEnvPath -Encoding UTF8
$currentKey = $null
$currentVal = ""
$inMultiLine = $false

foreach ($line in $lines) {
    $trimmed = $line.Trim()
    if (-not $inMultiLine) {
        if ($trimmed.StartsWith("#") -or [string]::IsNullOrWhiteSpace($trimmed)) {
            continue
        }
        if ($line -match '^([A-Za-z0-9_]+)=(.*)$') {
            $currentKey = $matches[1]
            $valPart = $matches[2]
            if ($valPart.StartsWith('"') -and -not ($valPart.EndsWith('"') -and $valPart.Length -gt 1)) {
                $inMultiLine = $true
                $currentVal = $valPart.Substring(1) + "`n"
            } else {
                $valClean = $valPart.Trim('"', "'")
                $envMap[$currentKey] = $valClean
                $currentKey = $null
            }
        }
    } else {
        if ($line.EndsWith('"')) {
            $currentVal += $line.Substring(0, $line.Length - 1)
            $envMap[$currentKey] = $currentVal
            $inMultiLine = $false
            $currentKey = $null
            $currentVal = ""
        } else {
            $currentVal += $line + "`n"
        }
    }
}

$deployHost = if ($envMap["DEPLOY_HOST"]) { $envMap["DEPLOY_HOST"] } else { "127.0.0.1" }
$deployPort = if ($envMap["DEPLOY_PORT"]) { [int]$envMap["DEPLOY_PORT"] } else { 22 }
$deployUser = if ($envMap["DEPLOY_USER"]) { $envMap["DEPLOY_USER"] } else { "root" }
$sshKeyContent = $envMap["DEPLOY_SSH_KEY"]
$containerBaseName = if ($envMap["DEPLOY_CONTAINER_NAME"]) { $envMap["DEPLOY_CONTAINER_NAME"] } else { "referencerag" }
$dockerImage = if ($Image) { $Image } elseif ($envMap["DOCKER_IMAGE"]) { $envMap["DOCKER_IMAGE"] } else { "ghcr.io/csvkse/referencerag:latest" }
$prodPort = if ($envMap["WEB_PORT"]) { [int]$envMap["WEB_PORT"] } else { 5123 }
$devPort = if ($envMap["DEV_WEB_PORT"]) { [int]$envMap["DEV_WEB_PORT"] } else { 5124 }
$remoteWorkdir = if ($envMap["DEPLOY_REMOTE_DIR"]) { $envMap["DEPLOY_REMOTE_DIR"] } else { "/opt/referencerag-dev" }
$remoteDataDir = if ($envMap["REMOTE_DATA_DIR"]) { $envMap["REMOTE_DATA_DIR"] } else { "referencerag-data" }
$remoteModelsDir = if ($envMap["REMOTE_MODELS_DIR"]) { $envMap["REMOTE_MODELS_DIR"] } else { "/opt/referencerag/models" }
$remoteSourcesDir = if ($envMap["REMOTE_SOURCES_DIR"]) { $envMap["REMOTE_SOURCES_DIR"] } else { "/opt/referencerag/sources" }
$masterKey = if ($envMap["REFERENCERAG_MASTER_KEY"]) { $envMap["REFERENCERAG_MASTER_KEY"] } else { "" }

# Hardware strategy resolution
$selectedHardware = if ($Hardware) { $Hardware.ToLower() } elseif ($envMap["HARDWARE_ACCELERATION"]) { $envMap["HARDWARE_ACCELERATION"].ToLower() } else { "cpu" }

# Mode-dependent properties
if ($FrontendOnly) {
    $Mode = "Dev"
}

$effectiveContainer = if ($Mode -eq "Dev") { "${containerBaseName}-dev" } else { $containerBaseName }
$effectivePort = if ($Mode -eq "Dev") { $devPort } else { $prodPort }
$targetWebUrl = "http://${deployHost}:${effectivePort}/"

Write-Success "Configuration loaded: Mode=$Mode, Target=$deployUser@$deployHost`:$deployPort, Container=$effectiveContainer, Port=$effectivePort, Hardware=$selectedHardware"

# ------------------------------------------------------------------------------
# SSH Adapter Setup (Adapter Pattern: Windows OpenSSH & ACL Enforcement)
# ------------------------------------------------------------------------------
if ([string]::IsNullOrWhiteSpace($sshKeyContent) -or $sshKeyContent -like "*PASTE_YOUR_OPENSSH*" -or $sshKeyContent -like "*REPLACE_WITH_YOUR_ACTUAL*") {
    throw "DEPLOY_SSH_KEY in '$EnvFile' contains default placeholder. Please provide a valid private key."
}

$tempKeyPath = [System.IO.Path]::GetTempFileName()
try {
    $cleanKey = $sshKeyContent.Replace("\n", "`n").Trim()
    [System.IO.File]::WriteAllText($tempKeyPath, ($cleanKey + "`n"), [System.Text.Encoding]::ASCII)
    
    # Restrict permissions on Windows so OpenSSH doesn't reject the key (UNPROTECTED PRIVATE KEY FILE error)
    & icacls.exe $tempKeyPath /inheritance:r /grant:r "$($env:USERNAME):(R)" | Out-Null

    function Clean-SshOutput($val) {
        if ($null -eq $val) { return "" }
        $str = ($val -join "`n")
        $str = [regex]::Replace($str, "\x1b\][^\x07\x1b]*[\x07\x1b\\]", "")
        $str = [regex]::Replace($str, "\x1b\[[0-9;]*[a-zA-Z]", "")
        return $str.Trim()
    }

    function Invoke-RemoteSsh([string]$cmd) {
        $cleanCmd = $cmd.Replace("`r`n", "`n")
        $sshArgs = @(
            "-i", $tempKeyPath,
            "-p", $deployPort,
            "-o", "StrictHostKeyChecking=no",
            "-o", "UserKnownHostsFile=/dev/null",
            "-o", "LogLevel=ERROR",
            "$deployUser@$deployHost",
            $cleanCmd
        )
        $output = & ssh @sshArgs
        if ($LASTEXITCODE -ne 0) {
            throw "SSH command failed with exit code $($LASTEXITCODE): $cmd"
        }
        return (Clean-SshOutput $output)
    }

    # --------------------------------------------------------------------------
    # Custom Remote Command Execution Mode
    # --------------------------------------------------------------------------
    if ($RemoteExec) {
        Write-Step "Executing remote command: $RemoteExec"
        $execOutput = Invoke-RemoteSsh $RemoteExec
        Write-Host $execOutput
        return
    }

    # --------------------------------------------------------------------------
    # Connectivity & Hardware Check Mode (-CheckOnly)
    # --------------------------------------------------------------------------
    if ($CheckOnly) {
        Write-Step "State [PROBING]: Probing remote host connectivity and runtime environment..."
        $uptime = Invoke-RemoteSsh "uptime"
        Write-Host " Host Uptime: $uptime" -ForegroundColor Green

        $dockerVer = Invoke-RemoteSsh "docker --version 2>/dev/null || echo 'Docker not installed'"
        Write-Host " Docker: $dockerVer" -ForegroundColor Green

        $gpuProbe = Invoke-RemoteSsh "if command -v nvidia-smi >/dev/null 2>&1; then nvidia-smi --query-gpu=name,driver_version,memory.total --format=csv,noheader; elif [ -d /dev/dri ]; then echo DRI Render Devices: `$(ls /dev/dri 2>/dev/null | tr '\n' ' '); else echo 'CPU-Only Environment'; fi"
        Write-Host " Hardware Acceleration Probe: $gpuProbe" -ForegroundColor Green

        $ps = Invoke-RemoteSsh "docker ps -a --filter name=$effectiveContainer"
        Write-Host "`nContainer Status ($effectiveContainer):" -ForegroundColor Cyan
        Write-Host $ps
        Write-Success "Connectivity and environment probe completed successfully!"
        return
    }

    # ==========================================================================
    # Strategy 1: Instant Frontend Hot Sync (-FrontendOnly, 3-5 seconds)
    # ==========================================================================
    if ($FrontendOnly) {
        Write-Step "[HotSync] Packaging native ESM frontend (Business/wwwroot)..."
        $feBundle = "fe-bundle-$([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()).tar.gz"
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $feDir = "Business/ReferenceRAG.Business/wwwroot"
        try {
            if (Test-Path $feBundle) { Remove-Item $feBundle -Force }
            & tar.exe -czf $feBundle -C $feDir .
            if ($LASTEXITCODE -ne 0 -or -not (Test-Path $feBundle)) {
                throw "Failed to create frontend bundle from '$feDir'"
            }

            # 1. Sync to host workspace
            $remoteFeTarget = "$remoteWorkdir/Business/ReferenceRAG.Business/wwwroot"
            Invoke-RemoteSsh "mkdir -p $remoteFeTarget"
            $feStreamArgs = @(
                "-i", $tempKeyPath,
                "-p", "$deployPort",
                "-o", "StrictHostKeyChecking=no",
                "-o", "UserKnownHostsFile=/dev/null",
                "-o", "LogLevel=ERROR",
                "$deployUser@$deployHost",
                "tar -xzf - -C $remoteFeTarget"
            )
            $feProc = Start-Process -FilePath "ssh" -ArgumentList $feStreamArgs -RedirectStandardInput $feBundle -NoNewWindow -Wait -PassThru
            if ($feProc.ExitCode -ne 0) {
                throw "Frontend host sync failed with exit code $($feProc.ExitCode)"
            }

            # 2. Patch running container's /app/wwwroot directly
            $containerRunning = (Invoke-RemoteSsh "docker ps -q --filter name=$effectiveContainer").Trim()
            if ($containerRunning) {
                $containerStreamArgs = @(
                    "-i", $tempKeyPath,
                    "-p", "$deployPort",
                    "-o", "StrictHostKeyChecking=no",
                    "-o", "UserKnownHostsFile=/dev/null",
                    "-o", "LogLevel=ERROR",
                    "$deployUser@$deployHost",
                    "docker exec -i $effectiveContainer tar -xzf - -C /app/wwwroot"
                )
                $cProc = Start-Process -FilePath "ssh" -ArgumentList $containerStreamArgs -RedirectStandardInput $feBundle -NoNewWindow -Wait -PassThru
                if ($cProc.ExitCode -ne 0) {
                    Write-Warn "In-container hot patch returned exit code $($cProc.ExitCode)"
                }
            }

            $sw.Stop()
            Write-Success "Frontend hot-sync completed in $($sw.ElapsedMilliseconds) ms!"
            Write-Host "Instant Web URL: $targetWebUrl (Press Ctrl+F5 in browser to view live changes)" -ForegroundColor Green
            return
        }
        finally {
            if (Test-Path $feBundle) { Remove-Item $feBundle -Force -ErrorAction SilentlyContinue }
        }
    }

    # ==========================================================================
    # Strategy 2: Fast Development Pipeline (Decoupled Sync + JIT Image Build)
    # ==========================================================================
    if ($Mode -eq "Dev") {
        # ----------------------------------------------------------------------
        # State 2: Code Sync (Strategy Pattern: Auto / GitArchive / Direct)
        # ----------------------------------------------------------------------
        $effectiveSync = $SyncMethod
        if ($SyncMethod -eq "Auto") {
            $hasGit = Test-Path ".git"
            if ($hasGit) {
                $gitStatus = (& git status --porcelain 2>&1)
                if ([string]::IsNullOrWhiteSpace($gitStatus)) {
                    $effectiveSync = "GitArchive"
                    $commitHash = (& git rev-parse --short HEAD 2>&1)
                    Write-Success "Adaptive Sync: Clean Git tree -> selected [GitArchive] strategy ($commitHash)"
                } else {
                    $effectiveSync = "Direct"
                    $dirtyCount = ($gitStatus -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }).Count
                    Write-Warn "Adaptive Sync: Detected $dirtyCount uncommitted changes -> selected [Direct] strategy"
                }
            } else {
                $effectiveSync = "Direct"
                Write-Success "Adaptive Sync: Non-git workspace -> selected [Direct] strategy"
            }
        }

        $localBundle = "rag-bundle-$([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()).tar.gz"
        try {
            if ($effectiveSync -eq "Direct") {
                Write-Step "1/5 [Sync - Direct] Packaging working tree via native tar (excluding models, data, and build artifacts)..."
                # Exclude huge models, databases, build outputs, and private credentials
                & tar.exe -czf $localBundle `
                    --exclude=.git `
                    --exclude=bin `
                    --exclude=obj `
                    --exclude=models `
                    --exclude=data `
                    --exclude=data-dev `
                    --exclude=logs `
                    --exclude=artifacts `
                    --exclude=test-vault `
                    --exclude=*.onnx `
                    --exclude=*.gguf `
                    --exclude=*.bin `
                    --exclude=*.tmp `
                    --exclude=*.tar.gz `
                    --exclude=*.zip `
                    Directory.Build.props Directory.Packages.props nuget.config packages Infrastructure Business Host support
                if ($LASTEXITCODE -ne 0 -or -not (Test-Path $localBundle)) {
                    throw "Failed to create direct workspace bundle at $localBundle"
                }
            }
            elseif ($effectiveSync -eq "GitArchive") {
                Write-Step "1/5 [Sync - GitArchive] Bundling clean HEAD commit archive..."
                & git archive --format=tar.gz --output=$localBundle HEAD
                if ($LASTEXITCODE -ne 0 -or -not (Test-Path $localBundle)) {
                    throw "Failed to create Git archive bundle at $localBundle"
                }
            }

            Write-Step "2/5 [Transfer] Streaming bundle to $deployUser@$deployHost`:$remoteWorkdir..."
            Invoke-RemoteSsh "mkdir -p $remoteWorkdir"
            $streamArgs = @(
                "-i", $tempKeyPath,
                "-p", "$deployPort",
                "-o", "StrictHostKeyChecking=no",
                "-o", "UserKnownHostsFile=/dev/null",
                "-o", "LogLevel=ERROR",
                "$deployUser@$deployHost",
                "tar -xzf - -C $remoteWorkdir"
            )
            $streamProc = Start-Process -FilePath "ssh" -ArgumentList $streamArgs -RedirectStandardInput $localBundle -NoNewWindow -Wait -PassThru
            if ($streamProc.ExitCode -ne 0) {
                throw "SSH streaming extraction failed with exit code $($streamProc.ExitCode)"
            }
            Write-Success "Workspace synchronized to $remoteWorkdir successfully!"
        }
        finally {
            if (Test-Path $localBundle) { Remove-Item $localBundle -Force -ErrorAction SilentlyContinue }
        }

        # ----------------------------------------------------------------------
        # State 3: Fast JIT Docker Build
        # ----------------------------------------------------------------------
        $devImage = "${containerBaseName}:dev-${selectedHardware}"
        if (-not $SkipBuild) {
            Write-Step "3/5 [Build] Compiling dev container image ($devImage) on remote host..."
            $dockerfileToUse = switch ($selectedHardware) {
                "cuda"  { "$remoteWorkdir/support/deploy/docker/Dockerfile.cuda" }
                "intel" { "$remoteWorkdir/support/deploy/docker/Dockerfile.intel" }
                "rocm"  { "$remoteWorkdir/support/deploy/docker/Dockerfile.rocm" }
                default { "$remoteWorkdir/support/deploy/docker/Dockerfile.dev" }
            }
            Invoke-RemoteSsh "docker build -f $dockerfileToUse -t $devImage $remoteWorkdir"
            Write-Success "Dev image ($devImage) built successfully!"
        } else {
            Write-Step "3/5 [Build] Skipping container build (-SkipBuild specified)."
        }

        # ----------------------------------------------------------------------
        # State 4: Launch Dev Container
        # ----------------------------------------------------------------------
        Write-Step "4/5 [Deploy] Starting development container ($effectiveContainer on port $devPort)..."
        # Ensure remote directories exist
        Invoke-RemoteSsh "mkdir -p $remoteModelsDir $remoteSourcesDir"

        # Hardware-specific execution arguments (Strategy Pattern)
        $hwArgs = switch ($selectedHardware) {
            "cuda" {
                "--gpus all -e NVIDIA_VISIBLE_DEVICES=all -e NVIDIA_DRIVER_CAPABILITIES=compute,utility"
            }
            "intel" {
                "--device /dev/dri:/dev/dri --group-add video --group-add render"
            }
            "rocm" {
                "--device /dev/kfd:/dev/kfd --device /dev/dri:/dev/dri --group-add video --group-add render"
            }
            default {
                # Universal CPU + Vulkan (device mount if /dev/dri exists on host)
                ""
            }
        }

        # Prepare runtime environment variables from envMap (excluding SSH/Deploy variables)
        $runtimeEnvLines = @()
        foreach ($k in $envMap.Keys) {
            if ($k -notlike "DEPLOY_*" -and $k -notlike "REMOTE_*" -and $k -ne "DOCKER_IMAGE" -and $k -ne "REFERENCERAG_MASTER_KEY") {
                $v = $envMap[$k]
                if (-not [string]::IsNullOrWhiteSpace($v)) {
                    $runtimeEnvLines += "$k=$v"
                }
            }
        }

        $runtimeEnvPath = "$remoteWorkdir/.env.runtime"
        $envFileContent = ($runtimeEnvLines -join "`n") + "`n"
        $tempEnvPath = [System.IO.Path]::GetTempFileName()
        try {
            [System.IO.File]::WriteAllText($tempEnvPath, $envFileContent, [System.Text.Encoding]::UTF8)
            $envStreamArgs = @(
                "-i", $tempKeyPath,
                "-p", "$deployPort",
                "-o", "StrictHostKeyChecking=no",
                "-o", "UserKnownHostsFile=/dev/null",
                "-o", "LogLevel=ERROR",
                "$deployUser@$deployHost",
                "cat > $runtimeEnvPath && chmod 600 $runtimeEnvPath"
            )
            $envProc = Start-Process -FilePath "ssh" -ArgumentList $envStreamArgs -RedirectStandardInput $tempEnvPath -NoNewWindow -Wait -PassThru
            if ($envProc.ExitCode -ne 0) {
                Write-Warn "Failed to stream runtime environment file to $runtimeEnvPath"
            }
        }
        finally {
            if (Test-Path $tempEnvPath) { Remove-Item $tempEnvPath -Force -ErrorAction SilentlyContinue }
        }

        $masterKeyEnv = if ($masterKey) { "-e MasterKey=$masterKey" } else { "" }
        $envFileArg = if ($runtimeEnvLines.Count -gt 0) { "--env-file $runtimeEnvPath" } else { "" }

        # Three-volume mount strategy + live frontend mount for zero-rebuild updates
        $runCmd = @"
docker rm -f $effectiveContainer 2>/dev/null || true
docker run -d \
  --name $effectiveContainer \
  --restart unless-stopped \
  -p ${devPort}:5000 \
  $hwArgs \
  $envFileArg \
  -e ASPNETCORE_ENVIRONMENT=Development \
  -e DataPath=/app/data \
  -e LogsPath=/app/logs \
  -e ReferenceRAG__Service__port=5000 \
  -e ReferenceRAG__Service__host=0.0.0.0 \
  -e SwaggerEnabled=true \
  -e DOTNET_USE_POLLING_FILE_WATCHER=true \
  $masterKeyEnv \
  -v ${effectiveContainer}-data:/app/data \
  -v ${remoteModelsDir}:/app/models \
  -v ${remoteSourcesDir}:/app/sources \
  -v ${remoteWorkdir}/Business/ReferenceRAG.Business/wwwroot:/app/wwwroot \
  $devImage
"@
        Invoke-RemoteSsh $runCmd
        Write-Success "Development container launched with live volume mounts!"

        # ----------------------------------------------------------------------
        # State 5: Health Check & Smoke Probe
        # ----------------------------------------------------------------------
        if (-not $NoHealthCheck) {
            Write-Step "5/5 [Verify] Probing container health on http://127.0.0.1:${devPort}/api/system/health..."
            $maxWaitSec = 35
            $waited = 0
            $isHealthy = $false

            while ($waited -lt $maxWaitSec) {
                Start-Sleep -Seconds 2
                $waited += 2
                try {
                    $cState = (Invoke-RemoteSsh "docker inspect --format '{{.State.Status}}' $effectiveContainer 2>/dev/null").Trim()
                    if ($cState -eq "restarting" -or $cState -eq "exited") {
                        Write-Warn "Container entered '$cState' state. Recent logs:"
                        Invoke-RemoteSsh "docker logs --tail 25 $effectiveContainer"
                        throw "Container failed to run (State: $cState)"
                    }
                    $hStatus = (Invoke-RemoteSsh "docker inspect --format '{{.State.Health.Status}}' $effectiveContainer 2>/dev/null").Trim()
                    if ($hStatus -eq "healthy") {
                        $isHealthy = $true
                        break
                    }

                    # Fallback HTTP probe via curl
                    $httpCode = (Invoke-RemoteSsh "curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:${devPort}/api/system/health 2>/dev/null || true").Trim()
                    if ($httpCode -eq "200") {
                        $isHealthy = $true
                        break
                    }
                } catch {
                    if ($_.Exception.Message -match "Container failed to run") { throw $_ }
                }
            }

            if ($isHealthy) {
                Write-Success "ReferenceRAG is healthy and responding!"
            } else {
                Write-Warn "Container did not report healthy within $maxWaitSec seconds. Logs:"
                Invoke-RemoteSsh "docker logs --tail 25 $effectiveContainer"
            }
        }

        # ----------------------------------------------------------------------
        # State 6: Test Dataset Synchronization (-SyncDataset <Smoke|Core|Full>)
        # ----------------------------------------------------------------------
        $effectiveDataset = if ($SyncDataset -ne "None") { $SyncDataset } elseif ($envMap["TEST_DATASET_TIER"]) { $envMap["TEST_DATASET_TIER"] } else { "None" }
        if ($effectiveDataset -ne "None") {
            Write-Step "6/6 [Dataset] Synchronizing '$effectiveDataset' tier test corpus to $remoteSourcesDir..."
            $tierName = $effectiveDataset.ToLower()
            $tierPath = Join-Path $RepoRoot "env/data-prepared/$tierName"

            if (-not (Test-Path $tierPath)) {
                Write-Warn "Prepared dataset for '$tierName' not found. Executing normalizer (scripts/prepare-test-corpus.mjs)..."
                & node (Join-Path $RepoRoot "scripts/prepare-test-corpus.mjs")
            }

            if (Test-Path $tierPath) {
                $dsBundle = "dataset-$tierName-$([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()).tar.gz"
                try {
                    & tar.exe -czf $dsBundle -C $tierPath .
                    if ($LASTEXITCODE -eq 0 -and (Test-Path $dsBundle)) {
                        $remoteTierDir = "$remoteSourcesDir/$tierName"
                        Invoke-RemoteSsh "mkdir -p $remoteTierDir"
                        
                        $dsStreamArgs = @(
                            "-i", $tempKeyPath,
                            "-p", "$deployPort",
                            "-o", "StrictHostKeyChecking=no",
                            "-o", "UserKnownHostsFile=/dev/null",
                            "-o", "LogLevel=ERROR",
                            "$deployUser@$deployHost",
                            "tar -xzf - -C $remoteTierDir && chmod -R 755 $remoteTierDir"
                        )
                        $dsProc = Start-Process -FilePath "ssh" -ArgumentList $dsStreamArgs -RedirectStandardInput $dsBundle -NoNewWindow -Wait -PassThru
                        if ($dsProc.ExitCode -eq 0) {
                            $fileCount = (Invoke-RemoteSsh "find $remoteTierDir -type f | wc -l").Trim()
                            Write-Success "Dataset tier [$effectiveDataset] synchronized successfully ($fileCount files in $remoteTierDir)!"
                        } else {
                            Write-Warn "Dataset stream extraction failed with exit code $($dsProc.ExitCode)"
                        }
                    }
                }
                finally {
                    if (Test-Path $dsBundle) { Remove-Item $dsBundle -Force -ErrorAction SilentlyContinue }
                }
            } else {
                Write-Warn "Unable to find prepared dataset directory at '$tierPath'."
            }
        }

        Write-Step "Dev Deployment Completed Successfully!"
        Write-Host " Dev Web URL : $targetWebUrl" -ForegroundColor Green
        Write-Host " Swagger Docs: ${targetWebUrl}swagger" -ForegroundColor Cyan
        Write-Host " Notice      : Production container remains completely isolated and untouched." -ForegroundColor Yellow
        return
    }

    # ==========================================================================
    # Strategy 3: Official Production Pipeline (GHCR Multi-Arch Image Pull)
    # ==========================================================================
    if ($Mode -eq "Official") {
        if (-not $Image) {
            Write-Step "1/4 [Pull] Pulling official image ($dockerImage)..."
            Invoke-RemoteSsh "docker pull $dockerImage"
            Write-Success "Docker pull finished!"
        } else {
            Write-Step "1/4 [Image] Using local/custom image '$dockerImage' (-Image specified, skipping pull)..."
        }

        Write-Step "2/4 [Deploy] Recreating production container ($effectiveContainer on port $prodPort)..."
        Invoke-RemoteSsh "mkdir -p $remoteModelsDir $remoteSourcesDir"

        $hwArgs = switch ($selectedHardware) {
            "cuda" {
                "--gpus all -e NVIDIA_VISIBLE_DEVICES=all -e NVIDIA_DRIVER_CAPABILITIES=compute,utility"
            }
            "intel" {
                "--device /dev/dri:/dev/dri --group-add video --group-add render"
            }
            "rocm" {
                "--device /dev/kfd:/dev/kfd --device /dev/dri:/dev/dri --group-add video --group-add render"
            }
            default { "" }
        }

        $masterKeyEnv = if ($masterKey) { "-e MasterKey=$masterKey" } else { "" }

        $prodRunCmd = @"
docker rm -f $effectiveContainer 2>/dev/null || true
docker run -d \
  --name $effectiveContainer \
  --restart unless-stopped \
  -p ${prodPort}:5000 \
  $hwArgs \
  -e ASPNETCORE_ENVIRONMENT=Production \
  -e DataPath=/app/data \
  -e LogsPath=/app/logs \
  -e ReferenceRAG__Service__port=5000 \
  -e ReferenceRAG__Service__host=0.0.0.0 \
  -e SwaggerEnabled=true \
  $masterKeyEnv \
  -v ${remoteDataDir}:/app/data \
  -v ${remoteModelsDir}:/app/models \
  -v ${remoteSourcesDir}:/app/sources \
  $dockerImage
"@
        Invoke-RemoteSsh $prodRunCmd
        Write-Success "Production container recreated successfully!"

        if (-not $NoHealthCheck) {
            Write-Step "3/4 [Verify] Probing production health check..."
            $maxWaitSec = 45
            $waited = 0
            $isHealthy = $false

            while ($waited -lt $maxWaitSec) {
                Start-Sleep -Seconds 2
                $waited += 2
                try {
                    $hStatus = (Invoke-RemoteSsh "docker inspect --format '{{.State.Health.Status}}' $effectiveContainer 2>/dev/null").Trim()
                    if ($hStatus -eq "healthy") {
                        $isHealthy = $true
                        break
                    }
                    $httpCode = (Invoke-RemoteSsh "curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:${prodPort}/api/system/health 2>/dev/null || true").Trim()
                    if ($httpCode -eq "200") {
                        $isHealthy = $true
                        break
                    }
                } catch {}
            }

            if ($isHealthy) {
                Write-Success "Production container is healthy!"
            } else {
                Write-Warn "Container did not reach healthy status within $maxWaitSec seconds. Logs:"
                Invoke-RemoteSsh "docker logs --tail 25 $effectiveContainer"
            }
        }

        Write-Step "Official Production Deployment Completed Successfully!"
        Write-Host " Production Web URL: $targetWebUrl" -ForegroundColor Green
    }
}
finally {
    if (Test-Path $tempKeyPath) {
        Remove-Item -Path $tempKeyPath -Force -ErrorAction SilentlyContinue
    }
}
