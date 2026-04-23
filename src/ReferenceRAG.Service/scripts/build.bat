@echo off
setlocal EnableDelayedExpansion

echo ============================================
echo   ReferenceRAG Service - 构建脚本
echo ============================================
echo.

cd /d "%~dp0.."

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
set PUBLISH_ARGS=-c %CONFIG% -r %RUNTIME% -o ./publish

if "%SELF_CONTAINED%"=="true" (
    set PUBLISH_ARGS=%PUBLISH_ARGS% --self-contained true
) else (
    set PUBLISH_ARGS=%PUBLISH_ARGS% --self-contained false
)

if not defined VERBOSE (
    set PUBLISH_ARGS=%PUBLISH_ARGS% -v q
)

REM 执行构建
echo 正在构建...
dotnet publish %PUBLISH_ARGS%

if %ERRORLEVEL% equ 0 (
    echo.
    echo ============================================
    echo   构建成功！
    echo   输出目录: %cd%\publish
    echo ============================================

    REM 记录构建日志
    echo [%date% %time%] 构建成功 - %CONFIG% %RUNTIME% >> "%~dp0build.log"
) else (
    echo.
    echo ============================================
    echo   构建失败！错误代码: %ERRORLEVEL%
    echo ============================================

    REM 记录失败日志
    echo [%date% %time%] 构建失败 - 错误代码: %ERRORLEVEL% >> "%~dp0build.log"
)

echo.
echo 按任意键返回...
pause >nul
