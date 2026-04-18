@echo off
setlocal EnableDelayedExpansion

echo ============================================
echo   Web API Windows Service - 停止服务
echo ============================================
echo.

:: 检查管理员权限
net session >nul 2>&1
if %ERRORLEVEL% neq 0 (
    echo 错误: 请以管理员身份运行此脚本
    pause
    exit /b 1
)

cd /d "%~dp0.."

:: 查找 exe 文件 - 按优先级检查
set EXE_PATH=

if exist "ReferenceRAG.Service.exe" (
    set EXE_PATH=%cd%\ReferenceRAG.Service.exe
    goto :found_exe
)
if exist "publish\ReferenceRAG.Service.exe" (
    set EXE_PATH=%cd%\publish\ReferenceRAG.Service.exe
    goto :found_exe
)
if exist "WebApiWindowsService.exe" (
    set EXE_PATH=%cd%\WebApiWindowsService.exe
    goto :found_exe
)
if exist "publish\WebApiWindowsService.exe" (
    set EXE_PATH=%cd%\publish\WebApiWindowsService.exe
    goto :found_exe
)
if exist "bin\Release\net10.0\win-x64\ReferenceRAG.Service.exe" (
    set EXE_PATH=%cd%\bin\Release\net10.0\win-x64\ReferenceRAG.Service.exe
    goto :found_exe
)
if exist "bin\Debug\net10.0\win-x64\ReferenceRAG.Service.exe" (
    set EXE_PATH=%cd%\bin\Debug\net10.0\win-x64\ReferenceRAG.Service.exe
    goto :found_exe
)

:found_exe
if "!EXE_PATH!"=="" (
    echo 错误: 未找到 ReferenceRAG.Service.exe
    pause
    exit /b 1
)

"!EXE_PATH!" --stop

pause
