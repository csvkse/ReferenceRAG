@echo off
setlocal EnableDelayedExpansion

echo ============================================
echo   ReferenceRAG Service - 启动服务
echo ============================================
echo.

REM 检查管理员权限
net session >nul 2>&1
if %ERRORLEVEL% neq 0 (
    echo 错误: 请以管理员身份运行此脚本
    pause
    exit /b 1
)

cd /d "%~dp0.."

REM 调用公共函数查找 exe
call "%~dp0_find_exe.bat"

if "%EXE_PATH%"=="" (
    echo 错误: 未找到 ReferenceRAG.Service.exe
    echo 请先运行 build.bat 构建项目
    pause
    exit /b 1
)

echo 程序路径: %EXE_PATH%
echo.

"%EXE_PATH%" --start

if %ERRORLEVEL% equ 0 (
    echo 服务启动成功！
    echo [%date% %time%] 服务启动成功 >> "%~dp0service.log"
) else (
    echo 服务启动失败，错误代码: %ERRORLEVEL%
    echo [%date% %time%] 服务启动失败 - 错误代码: %ERRORLEVEL% >> "%~dp0service.log"
)

echo.
echo 按任意键返回...
pause >nul
