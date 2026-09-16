@echo off
setlocal EnableDelayedExpansion
if not exist "%~dp0..\..\artifacts\logs" mkdir "%~dp0..\..\artifacts\logs"

echo ============================================
echo   ReferenceRAG Desktop - 开发模式运行
echo ============================================
echo.

cd /d "%~dp0..\..\.."

REM 解析参数
set CONFIG=Debug
set EXTRA_ARGS=

:parse_args
if "%~1"=="" goto end_parse
if /i "%~1"=="-c" set CONFIG=%~2& shift & shift & goto parse_args
if /i "%~1"=="--config" set CONFIG=%~2& shift & shift & goto parse_args
if /i "%~1"=="--serve-http" set EXTRA_ARGS=%EXTRA_ARGS% --serve-http& shift & goto parse_args
shift
goto parse_args
:end_parse

echo 配置: %CONFIG%
echo 额外参数: %EXTRA_ARGS%
echo.

REM 记录启动日志
echo [%date% %time%] 桌面端开发模式启动 - %CONFIG% >> "%~dp0..\..\artifacts\logs\desktop.log"

REM 桌面端为 WinForms 应用，dotnet run 前台运行；Ctrl+C 退出
echo 正在以开发模式启动桌面端（Ctrl+C 退出）...
echo ============================================
dotnet run --project Host/ReferenceRAG.DesktopHost/ReferenceRAG.DesktopHost.csproj -c %CONFIG% %EXTRA_ARGS%
set EXIT_CODE=%ERRORLEVEL%

echo.
echo ============================================
if %EXIT_CODE% equ 0 (
    echo 桌面端已退出（正常）
) else (
    echo 桌面端退出，错误代码: %EXIT_CODE%
    echo [%date% %time%] 桌面端开发模式异常退出 - 错误代码: %EXIT_CODE% >> "%~dp0..\..\artifacts\logs\desktop.log"
)
echo ============================================

endlocal
exit /b 0