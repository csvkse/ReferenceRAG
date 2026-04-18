@echo off
setlocal EnableDelayedExpansion

echo ============================================
echo   Web API Windows Service - 构建脚本
echo ============================================
echo.

cd /d "%~dp0.."

:: 解析参数
set CONFIG=Release
set RUNTIME=win-x64
set SELF_CONTAINED=true

:parse_args
if "%~1"=="" goto end_parse
if /i "%~1"=="-c" set CONFIG=%~2& shift & shift & goto parse_args
if /i "%~1"=="--config" set CONFIG=%~2& shift & shift & goto parse_args
if /i "%~1"=="-r" set RUNTIME=%~2& shift & shift & goto parse_args
if /i "%~1"=="--runtime" set RUNTIME=%~2& shift & shift & goto parse_args
if /i "%~1"=="--framework-dependent" set SELF_CONTAINED=false& shift & goto parse_args
shift
goto parse_args
:end_parse

echo 配置: %CONFIG%
echo 运行时: %RUNTIME%
echo 独立部署: %SELF_CONTAINED%
echo.

if "%SELF_CONTAINED%"=="true" (
    dotnet publish -c %CONFIG% -r %RUNTIME% --self-contained true -o ./publish
) else (
    dotnet publish -c %CONFIG% -r %RUNTIME% --self-contained false -o ./publish
)

if %ERRORLEVEL% equ 0 (
    echo.
    echo ============================================
    echo   构建成功！
    echo   输出目录: %cd%\publish
    echo ============================================
) else (
    echo.
    echo 构建失败！
)

pause
