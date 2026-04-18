@echo off
setlocal EnableDelayedExpansion

:menu
cls
echo.
echo   ==================================================
echo       Web API Windows Service - 管理工具 v1.0
echo   ==================================================
echo.
echo   [1] 构建 (Build)
echo   [2] 安装服务 (Install)
echo   [3] 启动服务 (Start)
echo   [4] 停止服务 (Stop)
echo   [5] 查看状态 (Status)
echo   [6] 卸载服务 (Uninstall)
echo   [7] 控制台运行 (Run as Console)
echo   [8] 打开浏览器 (Open Browser)
echo.
echo   [0] 退出 (Exit)
echo.
echo   ==================================================
echo.

set /p CHOICE="请选择操作 [0-8]: "

if "%CHOICE%"=="1" goto build
if "%CHOICE%"=="2" goto install
if "%CHOICE%"=="3" goto start
if "%CHOICE%"=="4" goto stop
if "%CHOICE%"=="5" goto status
if "%CHOICE%"=="6" goto uninstall
if "%CHOICE%"=="7" goto run
if "%CHOICE%"=="8" goto browser
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
start http://localhost:5000/api/home
timeout /t 2 >nul
goto menu

:end
echo 再见！
exit /b 0
