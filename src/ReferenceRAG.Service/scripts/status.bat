@echo off
setlocal EnableDelayedExpansion

echo ============================================
echo   ReferenceRAG Service - 服务状态
echo ============================================
echo.

cd /d "%~dp0.."

REM 调用公共函数查找 exe
call "%~dp0_find_exe.bat"

if "%EXE_PATH%"=="" (
    echo 错误: 未找到 ReferenceRAG.Service.exe
    pause
    exit /b 1
)

"%EXE_PATH%" --status

echo.
echo 按任意键返回...
pause >nul