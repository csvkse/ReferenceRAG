@echo off
chcp 65001 >nul
setlocal EnableDelayedExpansion

REM 确保日志目录存在
if not exist "%~dp0..\..\artifacts\logs" mkdir "%~dp0..\..\artifacts\logs"

echo ============================================
echo   ReferenceRAG Desktop - 发布构建
echo ============================================
echo.

cd /d "%~dp0..\..\.."
set LOG_FILE=%cd%\support\artifacts\logs\desktop-build.log
if not exist "%cd%\support\artifacts\logs" mkdir "%cd%\support\artifacts\logs"

REM 解析参数
set CONFIG=Release
set RUNTIME=win-x64
set SELF_CONTAINED=true
set VERBOSE=
set CLEAN=false

:parse_args
if "%~1"=="" goto end_parse
if /i "%~1"=="-c" set CONFIG=%~2& shift & shift & goto parse_args
if /i "%~1"=="--config" set CONFIG=%~2& shift & shift & goto parse_args
if /i "%~1"=="-r" set RUNTIME=%~2& shift & shift & goto parse_args
if /i "%~1"=="--runtime" set RUNTIME=%~2& shift & shift & goto parse_args
if /i "%~1"=="--framework-dependent" set SELF_CONTAINED=false& shift & goto parse_args
if /i "%~1"=="--clean" set CLEAN=true& shift & goto parse_args
if /i "%~1"=="-clean" set CLEAN=true& shift & goto parse_args
if /i "%~1"=="-v" set VERBOSE=true& shift & goto parse_args
if /i "%~1"=="--verbose" set VERBOSE=true& shift & goto parse_args
shift
goto parse_args
:end_parse

REM 正式发布输出目录：使用 bin 目录（动态适配配置名称）
set OUT_DIR=Host\ReferenceRAG.DesktopHost\bin\%CONFIG%\net10.0-windows\publish

echo 配置: %CONFIG%
echo 运行时: %RUNTIME%
echo 独立部署: %SELF_CONTAINED%
if "%CLEAN%"=="true" echo 干净构建: 是 (--clean)
echo 输出目录: %cd%\%OUT_DIR%
echo.

REM 检查桌面端是否正在运行（防止锁文件导致覆盖失败）
tasklist /fi "imagename eq ReferenceRAG.DesktopHost.exe" 2>nul | findstr /i "ReferenceRAG.DesktopHost.exe" >nul
if %ERRORLEVEL% neq 0 goto check_clean

echo [警告] 检测到 ReferenceRAG.DesktopHost.exe 正在运行！
echo 运行中的程序会锁死可执行文件，导致覆盖构建失败。
set KILL_APP=Y
set /p KILL_APP="是否自动结束正在运行的桌面端进程以覆盖构建? (Y/N, 默认 Y): "
if /i "%KILL_APP%"=="N" (
    echo 已取消构建，请手动退出桌面端后再运行构建。
    goto build_end
)
echo 正在停止 ReferenceRAG.DesktopHost.exe ...
taskkill /f /im ReferenceRAG.DesktopHost.exe >nul 2>&1
timeout /t 1 >nul

:check_clean
REM 若指定了 --clean，清理旧的发布目录以保证全新覆盖
if "%CLEAN%"=="true" (
    if exist "%OUT_DIR%" (
        echo 正在清理旧的发布目录...
        rmdir /s /q "%OUT_DIR%"
    )
)

REM 设置构建参数
set PUBLISH_ARGS=-c %CONFIG% -r %RUNTIME% -o "%OUT_DIR%"

if "%SELF_CONTAINED%"=="true" (
    set PUBLISH_ARGS=%PUBLISH_ARGS% --self-contained true
) else (
    set PUBLISH_ARGS=%PUBLISH_ARGS% --self-contained false
)

if not defined VERBOSE (
    set PUBLISH_ARGS=%PUBLISH_ARGS% -v m
) else (
    set PUBLISH_ARGS=%PUBLISH_ARGS% -v n
)

REM 执行构建
echo 正在构建桌面端并输出到 bin 目录...
dotnet publish "Host/ReferenceRAG.DesktopHost/ReferenceRAG.DesktopHost.csproj" %PUBLISH_ARGS%

if %ERRORLEVEL% equ 0 (
    echo.
    echo ============================================
    echo   桌面端构建成功！
    echo   输出目录: %cd%\%OUT_DIR%
    echo   可执行文件: ReferenceRAG.DesktopHost.exe
    echo ============================================

    REM 记录构建日志
    echo [%date% %time%] 桌面端构建成功 - %CONFIG% %RUNTIME% [bin] >> "%LOG_FILE%"
) else (
    echo.
    echo ============================================
    echo   桌面端构建失败！错误代码: %ERRORLEVEL%
    echo ============================================

    REM 记录失败日志
    echo [%date% %time%] 桌面端构建失败 - 错误代码: %ERRORLEVEL% >> "%LOG_FILE%"
)

:build_end
echo.
echo 按任意键返回...
pause >nul
endlocal
