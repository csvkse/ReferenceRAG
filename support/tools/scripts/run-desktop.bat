@echo off
setlocal EnableDelayedExpansion
if not exist "%~dp0..\..\artifacts\logs" mkdir "%~dp0..\..\artifacts\logs"

echo ============================================
echo   ReferenceRAG Desktop - Dev Run
echo ============================================
echo.

cd /d "%~dp0..\..\.."

REM parse args
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

echo Config: %CONFIG%
echo ExtraArgs: %EXTRA_ARGS%
echo.

REM write startup log
echo [%date% %time%] desktop dev start - %CONFIG% >> "%~dp0..\..\artifacts\logs\desktop.log"

REM WinForms app; dotnet run runs in foreground; Ctrl+C to stop
echo starting desktop dev
echo press Ctrl+C to stop
echo ============================================
dotnet run --project Host/ReferenceRAG.DesktopHost/ReferenceRAG.DesktopHost.csproj -c %CONFIG% %EXTRA_ARGS%
set EXIT_CODE=%ERRORLEVEL%

echo.
echo ============================================
if %EXIT_CODE% equ 0 (
    echo desktop exited ok
) else (
    echo desktop exited with code %EXIT_CODE%
    echo [%date% %time%] desktop dev abnormal exit - code %EXIT_CODE% >> "%~dp0..\..\artifacts\logs\desktop.log"
)
echo ============================================

endlocal
exit /b 0