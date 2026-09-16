@echo off
chcp 65001 >nul
setlocal EnableDelayedExpansion
if not exist "%~dp0..\..\artifacts\logs" mkdir "%~dp0..\..\artifacts\logs"

:menu
cls
echo.
echo   ==================================================
echo       ReferenceRAG Service - 管理工具 v2.0
echo   ==================================================
echo.

REM 显示服务状态
sc query ReferenceRAGService >nul 2>&1
if %ERRORLEVEL% equ 0 (
    for /f "tokens=3" %%a in ('sc query ReferenceRAGService ^| findstr STATE') do (
        if "%%a"=="RUNNING" (
            echo   [状态: 运行中]
        ) else if "%%a"=="STOPPED" (
            echo   [状态: 已停止]
        ) else (
            echo   [状态: %%a]
        )
    )
) else (
    echo   [状态: 未安装]
)

REM 读取端口
call "%~dp0_get_port.bat"
echo   [端口: %SERVICE_PORT%]
echo.
echo   [1] 构建 Web 服务 (Build WebHost)
echo   [2] 安装服务 (Install)
echo   [3] 启动服务 (Start)
echo   [4] 停止服务 (Stop)
echo   [5] 查看状态 (Status)
echo   [6] 卸载服务 (Uninstall)
echo   [7] 控制台运行 (Run as Console)
echo   [8] 打开浏览器 (Open Browser)
echo   [9] 查看日志 (View Logs)
echo   [A] 构建桌面端 (Build Desktop)
echo   [B] 桌面端开发模式运行 (Run Desktop Dev)
echo.
echo   [0] 退出 (Exit)
echo.
echo   ==================================================
echo.

set /p CHOICE="请选择操作 [0-9/A/B]: "

if "%CHOICE%"=="1" goto build
if "%CHOICE%"=="2" goto install
if "%CHOICE%"=="3" goto start
if "%CHOICE%"=="4" goto stop
if "%CHOICE%"=="5" goto status
if "%CHOICE%"=="6" goto uninstall
if "%CHOICE%"=="7" goto run
if "%CHOICE%"=="8" goto browser
if "%CHOICE%"=="9" goto logs
if /i "%CHOICE%"=="A" goto build-desktop
if /i "%CHOICE%"=="B" goto run-desktop
if "%CHOICE%"=="0" goto end

echo 无效的选择，请重新输入
timeout /t 2 >nul
goto menu

:build
call "%~dp0build.bat"
goto menu

:install
call "%~dp0install.bat"
goto menu

:start
call "%~dp0start.bat"
goto menu

:stop
call "%~dp0stop.bat"
goto menu

:status
call "%~dp0status.bat"
goto menu

:uninstall
call "%~dp0uninstall.bat"
goto menu

:run
call "%~dp0run.bat"
goto menu

:browser
echo 正在打开浏览器...
start http://localhost:%SERVICE_PORT%/api/home
timeout /t 2 >nul
goto menu

:logs
cls
echo ============================================
echo   服务日志
echo ============================================
echo.
if exist "%~dp0..\..\artifacts\logs\service.log" (
    more +0 "%~dp0..\..\artifacts\logs\service.log"
    echo.
    echo 按任意键用记事本打开完整日志...
    pause >nul
    notepad "%~dp0..\..\artifacts\logs\service.log"
) else (
    echo 暂无日志记录
    pause
)
goto menu

:build-desktop
call "%~dp0build-desktop.bat"
goto menu

:run-desktop
call "%~dp0run-desktop.bat"
goto menu

:end
echo 再见！
exit /b 0
