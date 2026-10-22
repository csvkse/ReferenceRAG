<#
.SYNOPSIS
    ReferenceRAG - TemporaryDiagnostics Latest Release Auto-Sync Script (Plan B)

.DESCRIPTION
    Queries GitHub API (https://api.github.com/repos/csvkse/TemporaryDiagnostics/releases/latest)
    to check the latest release and .nupkg package assets.
    - If a newer release exists, downloads latest .nupkg packages into ./packages directory;
    - Automatically cleans up older packages and updates Directory.Packages.props;
    - Idempotent: Skips downloading if the local packages already match the latest remote version;
    - Offline fallback: If GitHub API is unreachable, seamlessly continues with local packages.
#>

[CmdletBinding()]
param(
    [string]$Repo = "csvkse/TemporaryDiagnostics",
    [string]$PackagesDir = "",
    [string]$PropsFile = "",
    [string]$Token = "",
    [switch]$Force = $false,
    [switch]$OfflineFallback = $true,
    [int]$TimeoutSeconds = 15
)

$ErrorActionPreference = "Stop"

# 1. Resolve paths
$scriptDir = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($scriptDir)) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
}
$rootDir = (Get-Item "$scriptDir\..\..\..").FullName

if ([string]::IsNullOrWhiteSpace($PackagesDir)) {
    $PackagesDir = Join-Path $rootDir "packages"
}
if ([string]::IsNullOrWhiteSpace($PropsFile)) {
    $PropsFile = Join-Path $rootDir "Directory.Packages.props"
}

$PackagesDir = [System.IO.Path]::GetFullPath($PackagesDir)
$PropsFile = [System.IO.Path]::GetFullPath($PropsFile)

if (-not (Test-Path $PackagesDir)) {
    New-Item -ItemType Directory -Path $PackagesDir -Force | Out-Null
}

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "  ReferenceRAG - TemporaryDiagnostics Auto-Sync Tool" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "Target Repository: $Repo" -ForegroundColor Gray
Write-Host "Packages Dir:      $PackagesDir" -ForegroundColor Gray
Write-Host "Props File:        $PropsFile" -ForegroundColor Gray

# 2. Read declared version from Directory.Packages.props
$currentVersion = "0.0.0"
if (Test-Path $PropsFile) {
    $propsContent = [System.IO.File]::ReadAllText($PropsFile)
    if ($propsContent -match '<PackageVersion\s+Include="TemporaryDiagnostics\.AspNetCore"\s+Version="([^"]+)"') {
        $currentVersion = $matches[1]
    }
}
Write-Host "Current declared version: $currentVersion" -ForegroundColor Yellow

# 3. Prepare GitHub API headers and authentication
if ([string]::IsNullOrWhiteSpace($Token)) {
    $Token = $env:GITHUB_TOKEN
}

$apiUrl = "https://api.github.com/repos/$Repo/releases/latest"
$headers = @{
    "User-Agent" = "ReferenceRAG-AutoSync-Tool"
    "Accept"     = "application/vnd.github.v3+json"
}
if (-not [string]::IsNullOrWhiteSpace($Token)) {
    $headers["Authorization"] = "Bearer $Token"
    Write-Host "GitHub authorization token detected (Rate Limit safe)" -ForegroundColor DarkGray
}

# 4. Query GitHub API for latest release
$latestRelease = $null
try {
    [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12 -bor [System.Net.SecurityProtocolType]::Tls13
    Write-Host "Querying latest release from $Repo..." -ForegroundColor Gray
    $latestRelease = Invoke-RestMethod -Uri $apiUrl -Headers $headers -TimeoutSec $TimeoutSeconds -ErrorAction Stop
}
catch {
    $errMessage = $_.Exception.Message
    Write-Host "[Warning] Failed to fetch latest release: $errMessage" -ForegroundColor Yellow

    if ($OfflineFallback) {
        $existingPackages = Get-ChildItem -Path $PackagesDir -Filter "TemporaryDiagnostics.*.nupkg" -ErrorAction SilentlyContinue
        if ($existingPackages -and $existingPackages.Count -gt 0) {
            Write-Host "[Fallback] Using $($existingPackages.Count) existing local packages in $PackagesDir" -ForegroundColor Green
            exit 0
        }
        else {
            Write-Error "API request failed and no fallback packages exist in $PackagesDir!"
            exit 1
        }
    }
    else {
        throw $_
    }
}

if (-not $latestRelease) {
    Write-Host "[Warning] Empty release response object" -ForegroundColor Yellow
    exit 0
}

# 5. Extract remote version and .nupkg assets
$remoteTag = $latestRelease.tag_name
$remoteVersion = $remoteTag.TrimStart('v').TrimStart('V')
Write-Host "Remote latest version: $remoteVersion (Tag: $remoteTag)" -ForegroundColor Cyan

$nupkgAssets = @()
if ($latestRelease.assets) {
    $nupkgAssets = @($latestRelease.assets | Where-Object { $_.name -like "*.nupkg" })
}

if ($nupkgAssets.Count -eq 0) {
    Write-Host "[Warning] No .nupkg assets found in remote release $remoteTag, keeping local packages" -ForegroundColor Yellow
    exit 0
}

Write-Host "Found $($nupkgAssets.Count) package assets in release:" -ForegroundColor Gray
foreach ($asset in $nupkgAssets) {
    $sizeKb = [math]::Round($asset.size / 1024, 1)
    Write-Host "  - $($asset.name) ($sizeKb KB)" -ForegroundColor DarkGray
}

# 6. Idempotency Check: skip if up to date
$allFilesExist = $true
foreach ($asset in $nupkgAssets) {
    $targetPath = Join-Path $PackagesDir $asset.name
    if (-not (Test-Path $targetPath)) {
        $allFilesExist = $false
        break
    }
    if ((Get-Item $targetPath).Length -eq 0) {
        $allFilesExist = $false
        break
    }
}

if ((-not $Force) -and ($currentVersion -eq $remoteVersion) -and $allFilesExist) {
    Write-Host "[Idempotent] Local packages are already up to date (v$remoteVersion), skipping download." -ForegroundColor Green
    exit 0
}

Write-Host "Synchronizing to latest version: v$remoteVersion..." -ForegroundColor Cyan

# 7. Download new packages into temp directory
$tempDir = Join-Path $PackagesDir ".download_tmp"
if (Test-Path $tempDir) {
    Remove-Item -Path $tempDir -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Path $tempDir -Force | Out-Null

try {
    foreach ($asset in $nupkgAssets) {
        $destPath = Join-Path $tempDir $asset.name
        Write-Host "Downloading $($asset.name)..." -ForegroundColor Gray
        $dlHeaders = @{
            "User-Agent" = "ReferenceRAG-AutoSync-Tool"
            "Accept"     = "application/octet-stream"
        }
        if (-not [string]::IsNullOrWhiteSpace($Token)) {
            $dlHeaders["Authorization"] = "Bearer $Token"
        }

        Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $destPath -Headers $dlHeaders -TimeoutSec 60 -ErrorAction Stop
        
        $item = Get-Item $destPath
        if ($item.Length -lt 1024) {
            throw "Downloaded package $($asset.name) is unexpectedly small ($($item.Length) bytes)"
        }
        $kb = [math]::Round($item.Length / 1024, 1)
        Write-Host "Downloaded: $($asset.name) ($kb KB)" -ForegroundColor Green
    }

    # 8. Clean up older packages and move new packages
    $oldPackages = Get-ChildItem -Path $PackagesDir -Filter "TemporaryDiagnostics.*.nupkg" -ErrorAction SilentlyContinue
    foreach ($oldPkg in $oldPackages) {
        if ($oldPkg.Name -notlike "*$remoteVersion.nupkg") {
            Write-Host "Removing older package: $($oldPkg.Name)" -ForegroundColor DarkGray
            Remove-Item -Path $oldPkg.FullName -Force -ErrorAction SilentlyContinue
        }
    }

    foreach ($asset in $nupkgAssets) {
        $srcFile = Join-Path $tempDir $asset.name
        $dstFile = Join-Path $PackagesDir $asset.name
        Move-Item -Path $srcFile -Destination $dstFile -Force
    }
}
finally {
    if (Test-Path $tempDir) {
        Remove-Item -Path $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# 9. Update Directory.Packages.props
if (Test-Path $PropsFile) {
    Write-Host "Updating $PropsFile to version $remoteVersion..." -ForegroundColor Gray
    $propsText = [System.IO.File]::ReadAllText($PropsFile)
    
    $pattern = '(?<=<PackageVersion\s+Include="TemporaryDiagnostics\.[^"]+"\s+Version=")[^"]+(?="\s*/>)'
    $newPropsText = [System.Text.RegularExpressions.Regex]::Replace($propsText, $pattern, $remoteVersion)

    [System.IO.File]::WriteAllText($PropsFile, $newPropsText)
    Write-Host "Successfully updated Directory.Packages.props to $remoteVersion" -ForegroundColor Green
}

Write-Host "==========================================================" -ForegroundColor Green
Write-Host "  TemporaryDiagnostics auto-sync complete: v$remoteVersion" -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
