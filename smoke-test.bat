@echo off
setlocal enabledelayedexpansion

echo ==========================================================
echo   ReferenceRAG - End-to-End Core Smoke Testing
echo ==========================================================
echo.

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [smoke-test] ERROR: dotnet SDK was not found in PATH.
    exit /b 1
)

echo [smoke-test] Running core smoke tests (InProcessServer + E2E scenarios)...
dotnet test "%~dp0tests\ReferenceRAG.Host.Tests\ReferenceRAG.Host.Tests.csproj" --filter "FullyQualifiedName~Smoke" --logger "console;verbosity=normal" %*
set "EXIT_CODE=%ERRORLEVEL%"

echo.
if %EXIT_CODE% EQU 0 (
    echo ==========================================================
    echo   [smoke-test] SUCCESS: All core smoke tests passed!
    echo ==========================================================
) else (
    echo ==========================================================
    echo   [smoke-test] FAILED: Regression detected! Exit code: %EXIT_CODE%
    echo ==========================================================
)

exit /b %EXIT_CODE%
