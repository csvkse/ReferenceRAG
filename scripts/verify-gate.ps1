<#
.SYNOPSIS
    ReferenceRAG 质量门禁验证脚本
.DESCRIPTION
    按顺序执行前端静态 AST 符号门禁、前端单元测试、后端契约测试与核心回归测试。
.PARAMETER Full
    执行完整的全量自动化测试（包含 ONNX/语义重排等长耗时测试）
.PARAMETER DesktopSmoke
    执行需要 Windows 交互桌面和 WebView2 的正常/静默启动与恢复功能测试
#>
param(
    [switch]$Full,
    [switch]$DesktopSmoke
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepoRoot
Stop-Process -Name "testhost" -Force -ErrorAction SilentlyContinue

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
        dotnet test tests/ReferenceRAG.Tests/ReferenceRAG.Tests.csproj --filter "FullyQualifiedName~ContractTests|FullyQualifiedName~AuditFixRegressionTests|FullyQualifiedName~ModelDownloadArchitectureTests" --logger "console;verbosity=normal"
        if ($LASTEXITCODE -ne 0) {
            throw "ReferenceRAG.Tests failed with code $LASTEXITCODE"
        }

        dotnet test tests/ReferenceRAG.Host.Tests/ReferenceRAG.Host.Tests.csproj --filter "FullyQualifiedName~ContractTests|FullyQualifiedName~MafChatServiceTests|FullyQualifiedName~ThreeTierSlmOptimizationTests|FullyQualifiedName~ChatToolCalling|FullyQualifiedName~ChatThoughtAndContextUsageTests|FullyQualifiedName~ChatMultimodalAndMultiTurnToolTests|FullyQualifiedName~FrontendViewIntegrityTests|FullyQualifiedName~DesktopHostIdentityGateTests|FullyQualifiedName~StartupWindowCoordinatorTests|FullyQualifiedName~StartupWindowSizingTests|FullyQualifiedName~ConfigBootstrap|FullyQualifiedName~ConfigPathResolver|FullyQualifiedName~NetworkBindingResolver|FullyQualifiedName~DataDirectoryLease|FullyQualifiedName~LoggingPathResolution" --logger "console;verbosity=normal"
        if ($LASTEXITCODE -ne 0) {
            throw "ReferenceRAG.Host.Tests failed with code $LASTEXITCODE"
        }
    }

    if ($LASTEXITCODE -ne 0) {
        throw "Backend tests failed with code $LASTEXITCODE"
    }

    if ($DesktopSmoke) {
        dotnet run --project tests/ReferenceRAG.Desktop.SmokeTests/ReferenceRAG.Desktop.SmokeTests.csproj --verbosity quiet
        if ($LASTEXITCODE -ne 0) { throw "Normal desktop startup smoke test failed" }
        dotnet run --project tests/ReferenceRAG.Desktop.SmokeTests/ReferenceRAG.Desktop.SmokeTests.csproj --no-build -- --silent
        if ($LASTEXITCODE -ne 0) { throw "Silent desktop startup smoke test failed" }
        dotnet run --project tests/ReferenceRAG.Desktop.SmokeTests/ReferenceRAG.Desktop.SmokeTests.csproj --no-build -- --silent --early-show
        if ($LASTEXITCODE -ne 0) { throw "Early tray reveal smoke test failed" }
        dotnet run --project tests/ReferenceRAG.Desktop.SmokeTests/ReferenceRAG.Desktop.SmokeTests.csproj --no-build -- --silent --no-ready
        if ($LASTEXITCODE -ne 0) { throw "Missing frontend readiness smoke test failed" }
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
