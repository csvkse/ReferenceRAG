@echo off
setlocal EnableDelayedExpansion

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
echo   [1] 构建 (Build)
echo   [2] 安装服务 (Install)
echo   [3] 启动服务 (Start)
echo   [4] 停止服务 (Stop)
echo   [5] 查看状态 (Status)
echo   [6] 卸载服务 (Uninstall)
echo   [7] 控制台运行 (Run as Console)
echo   [8] 打开浏览器 (Open Browser)
echo   [9] 查看日志 (View Logs)
echo.
echo   [0] 退出 (Exit)
echo.
echo   ==================================================
echo.

set /p CHOICE="请选择操作 [0-9]: "

if "%CHOICE%"=="1" goto build
if "%CHOICE%"=="2" goto install
if "%CHOICE%"=="3" goto start
if "%CHOICE%"=="4" goto stop
if "%CHOICE%"=="5" goto status
if "%CHOICE%"=="6" goto uninstall
if "%CHOICE%"=="7" goto run
if "%CHOICE%"=="8" goto browser
if "%CHOICE%"=="9" goto logs
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
echo   服务日志 (最近 30 行)
echo ============================================
echo.
if exist "%~dp0service.log" (
    more +0 "%~dp0service.log" | findstr /n "^" | findstr "^[1-9][0-9]*:" | more +0
    echo.
    echo 按任意键查看完整日志...
    pause >nul
    notepad "%~dp0service.log"
) else (
    echo 暂无日志记录
    pause
)
goto menu

:end
echo 再见！
exit /b 0
