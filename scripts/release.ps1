<#
.SYNOPSIS
    ReferenceRAG - 规范化版本发布与标签控制脚本 (Release State Machine)
.DESCRIPTION
    按照严格的状态机流程执行版本号校验、工作区纯净度检查、Git 附注标签创建及远程推送。
    打完标签后，所有本地构建 (build-desktop.bat) 及 CI 流水线将自动继承此版本。
.PARAMETER Version
    发布的版本号，如 "1.0.52" 或 "v1.0.52"。
.PARAMETER Message
    Release 说明信息，默认形如 "Release v1.0.52"。
.PARAMETER Push
    是否在打完标签后直接推送到远程仓库 origin。
.PARAMETER AllowDirty
    是否允许在工作区包含未提交代码时强行打标签（默认禁止）。
.EXAMPLE
    pwsh scripts/release.ps1 -Version 1.0.52
.EXAMPLE
    pwsh scripts/release.ps1 -Version 1.0.52 -Message "Release v1.0.52: 修复桌面端版本号同步" -Push
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0, Mandatory = $false)]
    [string]$Version,

    [Parameter(Position = 1, Mandatory = $false)]
    [string]$Message,

    [switch]$Push,
    [switch]$AllowDirty
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Split-Path -Parent $ScriptDir
Set-Location $RepoRoot

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "  ReferenceRAG 规范化版本发布管理器 (Release FSM)         " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 阶段 1: 版本号输入与语义校验
if ([string]::IsNullOrWhiteSpace($Version)) {
    # 尝试读取当前最新的 Git Tag 作为参考提示
    $latestTag = (git describe --tags --abbrev=0 --match "v*" 2>$null)
    if (-not $latestTag) { $latestTag = "无历史标签" }
    Write-Host "[Info] 当前最新标签参考: $latestTag" -ForegroundColor DarkGray
    $Version = Read-Host "请输入要发布的新版本号 (例如 1.0.52 或 v1.0.52)"
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    Write-Error "版本号不能为空，发版流程终止。"
    exit 1
}

# 提取并规范化版本号
$cleanVer = $Version.TrimStart("v").Trim()
if ($cleanVer -notmatch '^\d+\.\d+\.\d+(-[\w\.]+)?$') {
    Write-Error "版本号格式不合法: '$Version'。必须符合语义化版本规范，例如 1.0.52、1.1.0 或 1.1.0-rc.1。"
    exit 1
}

$tagName = "v$cleanVer"
if ([string]::IsNullOrWhiteSpace($Message)) {
    $Message = "Release $tagName"
}

Write-Host "`n[State: 准备发布] 目标版本: $cleanVer  |  标签名称: $tagName" -ForegroundColor Green

# 阶段 2: 状态机守卫 - 工作区纯净度检测
Write-Host "[State: 校验工作区纯净度]..." -ForegroundColor Gray
$dirtyFiles = git status --porcelain
if ($dirtyFiles -and -not $AllowDirty) {
    Write-Host "`n[ERROR] 发版终止: 检测到工作区存在未提交或未暂存的文件！" -ForegroundColor Red
    Write-Host "为了保证 Git Tag 与代码版本的严格对应及构建的可重现性，发版前工作区必须纯净。" -ForegroundColor Yellow
    Write-Host "未提交变更清单:" -ForegroundColor Yellow
    $dirtyFiles | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkYellow }
    Write-Host "`n处理建议:" -ForegroundColor Cyan
    Write-Host "  1. 提交所有变更: git commit -am 'feat/fix: ...'" -ForegroundColor White
    Write-Host "  2. 或暂存修改:   git stash" -ForegroundColor White
    Write-Host "  3. 紧急强制发版可使用参数: -AllowDirty`n" -ForegroundColor DarkGray
    exit 1
}

# 阶段 3: 本地与远程标签冲突排查
Write-Host "[State: 校验标签唯一性]..." -ForegroundColor Gray
$localTagExists = git tag -l $tagName
if ($localTagExists) {
    Write-Error "标签 $tagName 已存在于本地仓库中！请使用不同的版本号。"
    exit 1
}

# 校验远程标签 (若网络通畅)
try {
    $remoteTag = git ls-remote --tags origin $tagName 2>$null
    if ($remoteTag) {
        Write-Error "标签 $tagName 已存在于远程仓库 origin 中！无法重复发布。"
        exit 1
    }
} catch {
    Write-Host "[Warning] 无法连通远程仓库检测标签，跳过远程冲突探测。" -ForegroundColor DarkYellow
}

# 阶段 4: 创建附注 Git 标签
Write-Host "[State: 创建 Git 标签] -> $tagName" -ForegroundColor Green
git tag -a $tagName -m "$Message"
if ($LASTEXITCODE -ne 0) {
    Write-Error "创建 Git 标签失败。"
    exit 1
}
Write-Host "[SUCCESS] 本地标签 $tagName 创建成功！" -ForegroundColor Green

# 阶段 5: 推送与发布协同
$shouldPush = $Push
if (-not $shouldPush) {
    $answer = Read-Host "是否立即推送标签到远程 origin 并触发 CI 构建发布？(y/N)"
    if ($answer -eq 'y' -or $answer -eq 'Y') {
        $shouldPush = $true
    }
}

if ($shouldPush) {
    Write-Host "`n[State: 推送标签到远程] -> git push origin $tagName" -ForegroundColor Cyan
    git push origin $tagName
    if ($LASTEXITCODE -eq 0) {
        Write-Host "`n[SUCCESS] 标签 $tagName 已成功推送至远程仓库！" -ForegroundColor Green
        Write-Host "GitHub Actions (desktop-release.yml) 流水线已自动触发构建全量分包。" -ForegroundColor Green
    } else {
        Write-Warning "标签推送至远程仓库失败，请检查网络权限后手动执行: git push origin $tagName"
    }
} else {
    Write-Host "`n[Info] 标签仅保存在本地。如需推送到远程，请执行: git push origin $tagName" -ForegroundColor Yellow
}

# 阶段 6: 本地打包提示
Write-Host "`n==========================================================" -ForegroundColor Green
Write-Host "  发版闭环完成: 动态版本注入已就绪                         " -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
Write-Host "从当前开始，您在本地运行以下命令打包软件:" -ForegroundColor White
Write-Host "  .\build-desktop.bat" -ForegroundColor Cyan
Write-Host "输出的桌面端安装包版本将自动锁定为: $cleanVer" -ForegroundColor Cyan
Write-Host "彻底告别 v1.0.0 僵死版本！`n" -ForegroundColor Green
