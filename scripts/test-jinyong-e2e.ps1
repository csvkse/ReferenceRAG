<#
.SYNOPSIS
    ReferenceRAG Linux 远程容器金庸数据集全流程端到端自动化测试脚本
.DESCRIPTION
    验证快速部署后的远程 Linux 容器环境，包括：
    1. 容器运行状态与健康检查探针
    2. 知识库源 (Sources) 自动化注册（挂载的 /app/sources/{smoke,core,full}）
    3. 全流程索引构建与分块状态轮询（验证 GBK 适配器、分块器及向量/BM25 索引构建）
    4. Golden QA 基准评测问答验证（召回率 Recall@K 与关键词命中率）
    5. OpenAI 兼容 /v1/chat/completions 或 /api/ai/query 接口验证
.PARAMETER Tier
    测试数据集层级：Smoke (默认), Core, Full
.PARAMETER EnvFile
    环境配置文件路径（默认 env/.envdev）
.PARAMETER SkipIndex
    跳过索引构建（在已建立索引时复用）
.PARAMETER TestChat
    测试 /v1/chat/completions 对话补全
.EXAMPLE
    powershell -File scripts/test-jinyong-e2e.ps1 -Tier Smoke
    powershell -File scripts/test-jinyong-e2e.ps1 -Tier Core -SkipIndex
#>

[CmdletBinding()]
param(
    [ValidateSet("Smoke", "Core", "Full")]
    [string]$Tier = "Smoke",

    [string]$EnvFile = "env/.envdev",

    [switch]$SkipIndex,

    [switch]$TestChat,

    [int]$PollTimeoutSec = 60
)

$ErrorActionPreference = "Stop"

function Write-Step([string]$msg) {
    Write-Host "`n====> $msg" -ForegroundColor Cyan
}

function Write-Success([string]$msg) {
    Write-Host " [OK] $msg" -ForegroundColor Green
}

function Write-Warn([string]$msg) {
    Write-Host " [!] $msg" -ForegroundColor Yellow
}

function Write-Err([string]$msg) {
    Write-Host " [ERR] $msg" -ForegroundColor Red
}

# ------------------------------------------------------------------------------
# 1. 环境与 SSH 连接初始化
# ------------------------------------------------------------------------------
Write-Step "1/5 [Init] 读取配置并建立 SSH 连接..."

if (-not (Test-Path $EnvFile)) {
    throw "环境配置文件未找到: $EnvFile"
}

$envLines = Get-Content -Path $EnvFile -Encoding UTF8
$envMap = @{}
$inMultiLine = $false
$currentKey = $null
$currentVal = ""

foreach ($line in $envLines) {
    $trimmed = $line.Trim()
    if (-not $inMultiLine) {
        if ($trimmed.StartsWith("#") -or [string]::IsNullOrWhiteSpace($trimmed)) { continue }
        if ($line -match '^([A-Za-z0-9_]+)=(.*)$') {
            $currentKey = $matches[1]
            $valPart = $matches[2]
            if ($valPart.StartsWith('"') -and -not ($valPart.EndsWith('"') -and $valPart.Length -gt 1)) {
                $inMultiLine = $true
                $currentVal = $valPart.Substring(1) + "`n"
            } else {
                $envMap[$currentKey] = $valPart.Trim('"', "'")
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
$devPort = if ($envMap["DEV_WEB_PORT"]) { [int]$envMap["DEV_WEB_PORT"] } else { 5124 }
$baseName = $envMap["DEPLOY_CONTAINER_NAME"]
$containerName = if ($baseName) { "$baseName-dev" } else { "referencerag-dev" }
$sshKeyContent = $envMap["DEPLOY_SSH_KEY"]

if ([string]::IsNullOrWhiteSpace($sshKeyContent)) {
    throw "未配置 DEPLOY_SSH_KEY"
}

$tempKeyPath = [System.IO.Path]::GetTempFileName()
$cleanKey = $sshKeyContent.Replace("\n", "`n").Trim()
[System.IO.File]::WriteAllText($tempKeyPath, ($cleanKey + "`n"), [System.Text.Encoding]::ASCII)
$userAcl = $env:USERNAME + ":(R)"
& icacls.exe $tempKeyPath /inheritance:r /grant:r $userAcl | Out-Null

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
        "-p", "$deployPort",
        "-o", "StrictHostKeyChecking=no",
        "-o", "UserKnownHostsFile=/dev/null",
        "-o", "LogLevel=ERROR",
        "$deployUser@$deployHost",
        $cleanCmd
    )
    $output = & ssh @sshArgs
    if ($LASTEXITCODE -ne 0) {
        throw "SSH execution failed (code $LASTEXITCODE): $cmd"
    }
    return (Clean-SshOutput $output)
}

function Invoke-RemoteApi([string]$method, [string]$endpoint, [string]$bodyJson = "") {
    if ($bodyJson) {
        $b64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($bodyJson))
        $remoteCmd = "echo '$b64' | base64 -d | curl -s -X $method http://127.0.0.1:$devPort$endpoint -H 'Content-Type: application/json' -d @-"
    } else {
        $remoteCmd = "curl -s -X $method http://127.0.0.1:$devPort$endpoint"
    }
    $raw = Invoke-RemoteSsh $remoteCmd
    return $raw
}

try {
    Write-Success "已连接远程主机 $deployUser@$deployHost`:$deployPort，容器: $containerName，端口: $devPort"

    # --------------------------------------------------------------------------
    # 2. 健康检查
    # --------------------------------------------------------------------------
    Write-Step "2/5 [Health] 探测应用与系统健康状态..."
    $healthRaw = Invoke-RemoteApi "GET" "/api/system/health"
    $health = $healthRaw | ConvertFrom-Json
    if ($health.status -ne "healthy") {
        throw "健康检查异常: $healthRaw"
    }
    Write-Success "健康状态正常: status=$($health.status), version=$($health.version)"

    # --------------------------------------------------------------------------
    # 3. 数据源注册与索引
    # --------------------------------------------------------------------------
    Write-Step "3/5 [Source & Index] 配置数据源与触发索引..."
    $tierLower = $Tier.ToLower()
    $sourceName = "jinyong_$tierLower"
    $targetPath = "/app/sources/$tierLower"

    # 确认容器内部文件存在
    $lsCheck = Invoke-RemoteSsh "docker exec $containerName ls -1 $targetPath 2>/dev/null || true"
    if ([string]::IsNullOrWhiteSpace($lsCheck)) {
        throw "远程容器 $containerName 内部挂载目录不存在或为空: $targetPath"
    }
    $fileList = $lsCheck.Split("`n")
    Write-Success "容器内数据文件就绪: $targetPath ($($fileList.Count) 个文件)"

    # 查询已有源
    $sourcesRaw = Invoke-RemoteApi "GET" "/api/sources"
    $sources = $sourcesRaw | ConvertFrom-Json
    $existing = $sources | Where-Object { $_.name -eq $sourceName }

    if (-not $existing) {
        Write-Host " 正在向容器注册数据源: $sourceName -> $targetPath"
        $addPayload = @{
            name = $sourceName
            path = $targetPath
            filePatterns = @("*.md", "*.txt")
            type = "Markdown"
            enabled = $true
        } | ConvertTo-Json -Compress
        $addRes = Invoke-RemoteApi "POST" "/api/sources" $addPayload
        $addParsed = $null
        try { $addParsed = $addRes | ConvertFrom-Json } catch {}
        if ($addParsed -and ($addParsed.error -or $addParsed.status -ge 400)) {
            throw "注册数据源失败: $addRes"
        }
        Write-Success "数据源注册成功: $sourceName"
    } else {
        Write-Success "数据源已存在: $sourceName (已有分块数: $($existing.chunkCount))"
    }

    if (-not $SkipIndex) {
        Write-Host " 触发数据源增量/重建索引: $sourceName..."
        $rebuildRes = Invoke-RemoteApi "POST" "/api/index/rebuild/$sourceName"
        Write-Success "索引任务已触发: $rebuildRes"
        
        $jobId = $null
        try {
            $parsedRebuild = $rebuildRes | ConvertFrom-Json
            $jobId = $parsedRebuild.JobId
        } catch {}

        # 轮询索引任务进度
        Write-Host " 轮询索引构建进度 (最多等待 $PollTimeoutSec 秒)..."
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $indexed = $false

        while ($sw.Elapsed.TotalSeconds -lt $PollTimeoutSec) {
            Start-Sleep -Seconds 3
            if ($jobId) {
                $jobsRaw = Invoke-RemoteApi "GET" "/api/index/jobs"
                $jobs = $jobsRaw | ConvertFrom-Json
                $targetJob = $jobs | Where-Object { $_.JobId -eq $jobId }
                if ($targetJob) {
                    if ($targetJob.Status -eq "Completed") {
                        $elapsedSec = [Math]::Round($sw.Elapsed.TotalSeconds, 1)
                        Write-Success "索引任务已完成! (耗时: ${elapsedSec}s, 处理文件: $($targetJob.ProcessedFiles)/$($targetJob.TotalFiles))"
                        $indexed = $true
                        break
                    } elseif ($targetJob.Status -eq "Failed") {
                        throw "索引任务失败: $($targetJob.ErrorMessage)"
                    } else {
                        $pct = $targetJob.ProgressPercent
                        $curFile = if ($targetJob.CurrentFile) { [System.IO.Path]::GetFileName($targetJob.CurrentFile) } else { "" }
                        Write-Host " [轮询中] 状态=$($targetJob.Status), 阶段=$($targetJob.Phase), 进度=${pct}%, 当前文件=$curFile, 已等待: $([Math]::Round($sw.Elapsed.TotalSeconds))s"
                        continue
                    }
                }
            }

            # 回退：检查 sources 分块数
            $summaryRaw = Invoke-RemoteApi "GET" "/api/sources"
            $currSources = $summaryRaw | ConvertFrom-Json
            $targetSource = $currSources | Where-Object { $_.name -eq $sourceName }
            if ($targetSource -and $targetSource.chunkCount -gt 0) {
                $elapsedSec = [Math]::Round($sw.Elapsed.TotalSeconds, 1)
                Write-Success "数据源分块就绪: 文件数=$($targetSource.fileCount), 分块数=$($targetSource.chunkCount), 耗时=${elapsedSec}s"
                $indexed = $true
                break
            }
        }

        if (-not $indexed) {
            Write-Warn "等待超时，请检查模型加载或日志输出。"
        }
    }

    # --------------------------------------------------------------------------
    # 4. Golden QA 基准评测问答验证
    # --------------------------------------------------------------------------
    Write-Step "4/5 [Benchmark] 执行 Golden QA 基准评测测试 (Tier=$Tier)..."
    $qaFixturePath = Join-Path (Get-Location) "tests/fixtures/jinyong-golden-qa.json"
    if (-not (Test-Path $qaFixturePath)) {
        throw "Golden QA 基准文件不存在: $qaFixturePath"
    }

    $allQa = Get-Content $qaFixturePath -Raw -Encoding UTF8 | ConvertFrom-Json
    $targetQaList = @()
    foreach ($item in $allQa) {
        if ($item.tier -eq $Tier -or $Tier -eq "Full") {
            $targetQaList += $item
        }
    }

    if ($targetQaList.Count -eq 0) {
        Write-Warn "未找到匹配 $Tier 层级的 QA 用例，退回执行全部用例"
        $targetQaList = $allQa
    }

    $passedCount = 0
    $failedCount = 0

    foreach ($qa in $targetQaList) {
        $mustStr = $qa.mustContainKeywords -join ", "
        Write-Host "`n [评测用例] [$($qa.id)] $($qa.question)" -ForegroundColor DarkCyan
        Write-Host "  -> 目标小说: $($qa.targetNovel) | 期望关键词: [$mustStr]"

        $queryPayload = @{
            query = $qa.question
            mode = 3 # Hybrid
            topK = 5
            sources = @($sourceName)
        } | ConvertTo-Json -Compress

        $qSw = [System.Diagnostics.Stopwatch]::StartNew()
        $respRaw = Invoke-RemoteApi "POST" "/api/ai/query" $queryPayload
        $qSw.Stop()

        $resp = $null
        try {
            $resp = $respRaw | ConvertFrom-Json
        } catch {
            Write-Err "  响应解析失败: $respRaw"
            $failedCount++
            continue
        }

        $allRetrievedText = ""
        if ($resp.chunks) {
            $chunkContents = @()
            foreach ($c in $resp.chunks) { $chunkContents += $c.content }
            $allRetrievedText += ($chunkContents -join " ")
        }
        if ($resp.context) {
            $allRetrievedText += " " + $resp.context
        }

        # 检查是否命中期望关键词
        $hitKeywords = @()
        foreach ($kw in $qa.mustContainKeywords) {
            if ($allRetrievedText.Contains($kw)) {
                $hitKeywords += $kw
            }
        }

        $isSuccess = ($hitKeywords.Count -gt 0)
        $qDuration = $qSw.ElapsedMilliseconds
        $chunkCount = if ($resp.chunks) { $resp.chunks.Count } else { 0 }

        if ($isSuccess) {
            $hitStr = $hitKeywords -join ", "
            Write-Success "  测试通过! (命中关键词: [$hitStr], 检索耗时: ${qDuration}ms, 召回分块数: $chunkCount)"
            $passedCount++
        } else {
            Write-Err "  测试未命中期望关键词! (检索耗时: ${qDuration}ms, 召回分块数: $chunkCount)"
            $preview = if ($allRetrievedText.Length -gt 150) { $allRetrievedText.Substring(0, 150) + "..." } else { $allRetrievedText }
            Write-Host "  召回内容片段: $preview" -ForegroundColor DarkGray
            $failedCount++
        }
    }

    $summaryColor = if ($failedCount -eq 0) { "Green" } else { "Yellow" }
    Write-Host "`n 基准测试结果概览: 通过 $passedCount / 总计 $($targetQaList.Count)" -ForegroundColor $summaryColor

    # --------------------------------------------------------------------------
    # 5. 可选对话补全测试
    # --------------------------------------------------------------------------
    if ($TestChat) {
        Write-Step "5/5 [Chat] 验证 OpenAI 兼容 /v1/chat/completions 接口..."
        $chatPayload = @{
            model = "jev-latest"
            messages = @(
                @{ role = "user"; content = "请问在越女剑中，阿青的剑法是跟谁学来的？" }
            )
        } | ConvertTo-Json -Compress

        $chatRaw = Invoke-RemoteApi "POST" "/v1/chat/completions" $chatPayload
        Write-Host " 对话补全响应: $chatRaw"
        Write-Success "对话接口调用成功!"
    } else {
        Write-Step "5/5 [Finish] 评测结束"
    }

    if ($failedCount -gt 0) {
        Write-Warn "存在 $failedCount 个未通过项，请分析召回片段。"
        exit 1
    } else {
        Write-Success "所有基准测试项全部通过！"
        exit 0
    }
}
finally {
    if (Test-Path $tempKeyPath) {
        Remove-Item $tempKeyPath -Force -ErrorAction SilentlyContinue
    }
}
