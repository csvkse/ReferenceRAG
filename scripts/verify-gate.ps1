<#
.SYNOPSIS
    ReferenceRAG 质量门禁验证脚本
.DESCRIPTION
    按顺序执行前端静态 AST 符号门禁、前端单元测试、后端契约测试与核心回归测试。
.PARAMETER Full
    执行完整的全量自动化测试（包含 ONNX/语义重排等长耗时测试）
#>
param(
    [switch]$Full
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepoRoot

function Write-Section([string]$title) {
    Write-Host "`n========================================================" -ForegroundColor Cyan
    Write-Host " [Quality Gate] $title" -ForegroundColor Cyan
    Write-Host "========================================================" -ForegroundColor Cyan
}

try {
    # ── 1. 前端静态语法与 AST 符号绑定门禁 ──
    Write-Section "1/3 Frontend Static AST & Symbol Binding Check"
    $frontendDir = Join-Path $RepoRoot "Business\ReferenceRAG.Business\wwwroot"
    Push-Location $frontendDir
    try {
        node check.mjs
        if ($LASTEXITCODE -ne 0) {
            throw "Frontend static check failed with code $LASTEXITCODE"
        }
    }
    finally {
        Pop-Location
    }

    # ── 2. 前端单元测试 ──
    Write-Section "2/3 Frontend Unit Tests"
    Push-Location $frontendDir
    try {
        npm test
        if ($LASTEXITCODE -ne 0) {
            throw "Frontend tests failed with code $LASTEXITCODE"
        }
    }
    finally {
        Pop-Location
    }

    # ── 3. 后端契约门禁与核心回归测试 ──
    Write-Section "3/3 Backend Quality Gate Contract & Regression Tests"
    if ($Full) {
        Write-Host "Running FULL test suite across solution..." -ForegroundColor Yellow
        dotnet test --logger "console;verbosity=normal"
    }
    else {
        Write-Host "Running QualityGateContractTests & AuditFixRegressionTests..." -ForegroundColor Yellow
        dotnet test tests/ReferenceRAG.Tests/ReferenceRAG.Tests.csproj --filter "FullyQualifiedName~ContractTests|FullyQualifiedName~AuditFixRegressionTests" --logger "console;verbosity=normal"
        if ($LASTEXITCODE -ne 0) {
            throw "ReferenceRAG.Tests failed with code $LASTEXITCODE"
        }

        dotnet test tests/ReferenceRAG.Host.Tests/ReferenceRAG.Host.Tests.csproj --filter "FullyQualifiedName~ContractTests|FullyQualifiedName~MafChatServiceTests" --logger "console;verbosity=normal"
        if ($LASTEXITCODE -ne 0) {
            throw "ReferenceRAG.Host.Tests failed with code $LASTEXITCODE"
        }
    }

    if ($LASTEXITCODE -ne 0) {
        throw "Backend tests failed with code $LASTEXITCODE"
    }

    Write-Host "`n========================================================" -ForegroundColor Green
    Write-Host " [SUCCESS] All Quality Gates Passed!" -ForegroundColor Green
    Write-Host "========================================================`n" -ForegroundColor Green
    exit 0
}
catch {
    Write-Host "`n========================================================" -ForegroundColor Red
    Write-Host " [FAILURE] Quality Gate Blocked: $_" -ForegroundColor Red
    Write-Host "========================================================`n" -ForegroundColor Red
    exit 1
}
