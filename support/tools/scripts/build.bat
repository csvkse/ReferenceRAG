@echo off
chcp 65001 >nul
setlocal EnableDelayedExpansion
if not exist "%~dp0..\..\artifacts\logs" mkdir "%~dp0..\..\artifacts\logs"

echo ============================================
echo   ReferenceRAG Service - 构建脚本
echo ============================================
echo.

cd /d "%~dp0..\..\.."

REM 解析参数
set CONFIG=Release
set RUNTIME=win-x64
set SELF_CONTAINED=true
set VERBOSE=

:parse_args
if "%~1"=="" goto end_parse
if /i "%~1"=="-c" set CONFIG=%~2& shift & shift & goto parse_args
if /i "%~1"=="--config" set CONFIG=%~2& shift & shift & goto parse_args
if /i "%~1"=="-r" set RUNTIME=%~2& shift & shift & goto parse_args
if /i "%~1"=="--runtime" set RUNTIME=%~2& shift & shift & goto parse_args
if /i "%~1"=="--framework-dependent" set SELF_CONTAINED=false& shift & goto parse_args
if /i "%~1"=="-v" set VERBOSE=true& shift & goto parse_args
if /i "%~1"=="--verbose" set VERBOSE=true& shift & goto parse_args
shift
goto parse_args
:end_parse

echo 配置: %CONFIG%
echo 运行时: %RUNTIME%
echo 独立部署: %SELF_CONTAINED%
echo.

REM 设置构建参数
set PUBLISH_ARGS=-c %CONFIG% -r %RUNTIME% -o ./support/artifacts/publish/web

if "%SELF_CONTAINED%"=="true" (
    set PUBLISH_ARGS=%PUBLISH_ARGS% --self-contained true
) else (
    set PUBLISH_ARGS=%PUBLISH_ARGS% --self-contained false
)

if not defined VERBOSE (
    set PUBLISH_ARGS=%PUBLISH_ARGS% -v q
)

set "WEB_OUTPUT=%~dp0..\..\artifacts\publish\web"
set "CONFIG_BACKUP=%TEMP%\referencerag_web_appsettings_backup.json"
if exist "%CONFIG_BACKUP%" del /f /q "%CONFIG_BACKUP%" >nul 2>nul
if exist "%WEB_OUTPUT%\appsettings.json" (
    echo [build] Backing up existing appsettings.json to preserve user configuration...
    copy /y "%WEB_OUTPUT%\appsettings.json" "%CONFIG_BACKUP%" >nul 2>nul
)

REM 执行构建
echo 正在构建...
dotnet publish "Host/ReferenceRAG.WebHost/ReferenceRAG.WebHost.csproj" %PUBLISH_ARGS%
set "BUILD_STATUS=%ERRORLEVEL%"

if %BUILD_STATUS% equ 0 (
    if exist "%CONFIG_BACKUP%" (
        if not exist "%WEB_OUTPUT%\appsettings.json" (
            copy /y "%CONFIG_BACKUP%" "%WEB_OUTPUT%\appsettings.json" >nul 2>nul
        ) else (
            echo [build] Merging user configuration into published appsettings.json...
            powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0merge-appsettings.ps1" -PublishedPath "%WEB_OUTPUT%\appsettings.json" -BackupPath "%CONFIG_BACKUP%"
        )
        del /f /q "%CONFIG_BACKUP%" >nul 2>nul
    )

    echo.
    echo ============================================
    echo   构建成功！
    echo   输出目录: %cd%\support\artifacts\publish\web
    echo ============================================

    REM 记录构建日志
    echo [%date% %time%] 构建成功 - %CONFIG% %RUNTIME% >> "%~dp0..\..\artifacts\logs\build.log"
) else (
    if exist "%CONFIG_BACKUP%" (
        if not exist "%WEB_OUTPUT%\appsettings.json" (
            copy /y "%CONFIG_BACKUP%" "%WEB_OUTPUT%\appsettings.json" >nul 2>nul
        )
        del /f /q "%CONFIG_BACKUP%" >nul 2>nul
    )

    echo.
    echo.
    echo ============================================
    echo   构建失败！错误代码: %ERRORLEVEL%
    echo ============================================

    REM 记录失败日志
    echo [%date% %time%] 构建失败 - 错误代码: %ERRORLEVEL% >> "%~dp0..\..\artifacts\logs\build.log"
)

echo.
echo 按任意键返回...
pause >nul
