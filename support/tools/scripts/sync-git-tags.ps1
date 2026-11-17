<#
.SYNOPSIS
    ReferenceRAG - 远程已推送标签智能同步器 (Caching Proxy & Safe Fetch)
.DESCRIPTION
    1. 检查上次同步缓存 (.git/.tag_sync_cache)，在 TTL (默认 300 秒/5分钟) 内直接秒级跳过，耗时 0ms；
    2. 若缓存过期或传入 -Force，在后台执行带超时熔断 (默认 2 秒) 的静默 git fetch --tags origin；
    3. 若离线、超时或未联网，自动静默降级，确保本地打包 100% 成功且不受网络影响；
    4. 无论开发者是在 GitHub Releases 网页打 Tag，还是在另一台机器手动 push，本地打包均可自动感应对齐。
#>
param(
    [int]$TtlSeconds = 300,
    [int]$TimeoutSeconds = 2,
    [switch]$Force,
    [switch]$Quiet
)

$ErrorActionPreference = "SilentlyContinue"

# 1. 定位 Git 仓库根目录
$repoRoot = (git rev-parse --show-toplevel 2>$null)
if (-not $repoRoot -or -not (Test-Path (Join-Path $repoRoot ".git"))) {
    exit 0
}

$cacheDir = Join-Path $repoRoot ".git"
$cacheFile = Join-Path $cacheDir ".tag_sync_cache"

# 2. 检查 TTL 缓存 (默认 5 分钟内跳过网络查询，保证日常打包 0ms 延迟)
if (-not $Force -and (Test-Path $cacheFile)) {
    try {
        $lastSyncTime = (Get-Item $cacheFile).LastWriteTimeUtc
        $elapsedSeconds = ((Get-Date).ToUniversalTime() - $lastSyncTime).TotalSeconds
        if ($elapsedSeconds -lt $TtlSeconds) {
            # 缓存有效，极速跳过
            exit 0
        }
    } catch { }
}

# 3. 检查是否存在远程仓库 origin
$remotes = git remote 2>$null
if (-not ($remotes -contains "origin")) {
    exit 0
}

# 4. 执行带严格超时熔断的后台拉取任务
try {
    if (-not $Quiet) {
        Write-Host "[TagSync] 正在检查远程已推送标签 (缓存过期或首次检测)..." -ForegroundColor DarkGray
    }

    $fetchScript = {
        git fetch --tags --quiet --prune-tags origin 2>$null
    }

    $job = Start-Job -ScriptBlock $fetchScript
    $completed = Wait-Job $job -Timeout $TimeoutSeconds

    if ($completed) {
        Receive-Job $job -ErrorAction SilentlyContinue | Out-Null
        # 刷新缓存时间戳
        Set-Content -Path $cacheFile -Value (Get-Date).ToString("o") -Force
        if (-not $Quiet) {
            Write-Host "[TagSync] 远程标签同步完成，已自动刷新本地版本镜像。" -ForegroundColor Green
        }
    } else {
        Stop-Job $job -ErrorAction SilentlyContinue | Out-Null
        if (-not $Quiet) {
            Write-Host "[TagSync] 远程探测超时 (>${TimeoutSeconds}s)，静默降级为本地已缓存标签构建。" -ForegroundColor DarkGray
        }
    }
    Remove-Job $job -Force -ErrorAction SilentlyContinue | Out-Null
} catch {
    # 离线环境静默忽略，绝不中断构建
    if (-not $Quiet) {
        Write-Host "[TagSync] 处于离线环境或网络异常，采用本地已存标签构建。" -ForegroundColor DarkGray
    }
}

exit 0
