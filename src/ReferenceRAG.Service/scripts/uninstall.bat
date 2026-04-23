@echo off
setlocal EnableDelayedExpansion

echo ============================================
echo   ReferenceRAG Service - 卸载脚本
echo ============================================
echo.

REM 检查管理员权限
net session >nul 2>&1
if %ERRORLEVEL% neq 0 (
    echo 错误: 请以管理员身份运行此脚本
    echo 右键点击此脚本，选择"以管理员身份运行"
    pause
    exit /b 1
)

cd /d "%~dp0.."

REM 调用公共函数查找 exe
call "%~dp0_find_exe.bat"

if "%EXE_PATH%"=="" (
    echo 错误: 未找到 ReferenceRAG.Service.exe
    pause
    exit /b 1
)

echo 程序路径: %EXE_PATH%
echo.
echo 警告: 即将卸载服务
echo.

set /p CONFIRM="确认卸载？(Y/N): "
if /i not "!CONFIRM!"=="Y" (
    echo 已取消卸载
    pause
    exit /b 0
)

REM 先停止服务（如果正在运行）
"%EXE_PATH%" --stop >nul 2>&1

REM 执行卸载
"%EXE_PATH%" --uninstall

if %ERRORLEVEL% equ 0 (
    echo.
    echo 卸载完成！
    echo [%date% %time%] 服务卸载成功 >> "%~dp0service.log"
) else (
    echo.
    echo 卸载失败，错误代码: %ERRORLEVEL%
    echo [%date% %time%] 服务卸载失败 - 错误代码: %ERRORLEVEL% >> "%~dp0service.log"
)

echo.
echo 按任意键返回...
pause >nul