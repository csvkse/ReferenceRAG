@echo off
setlocal

set "SCRIPT_DIR=%~dp0"
for %%I in ("%SCRIPT_DIR%..\..\..") do set "ROOT_DIR=%%~fI"
set "PROJECT=%ROOT_DIR%\Host\ReferenceRAG.DesktopHost\ReferenceRAG.DesktopHost.csproj"
set "OUTPUT=%ROOT_DIR%\Host\ReferenceRAG.DesktopHost\bin\Release\net10.0-windows\publish"
set "CONFIGURATION=Release"
set "RID=win-x64"
set "SELF_CONTAINED=true"
set "STRIP_PDB=true"
set "CLEAN=false"
set "VERBOSE=false"
set "RETRY_READY=1"
set "NON_INTERACTIVE="
set "RUN_SMOKE=false"

:parse_args
if "%~1"=="" goto after_args
if /i "%~1"=="--debug" (
    set "CONFIGURATION=Debug"
    set "STRIP_PDB=false"
    set "OUTPUT=%ROOT_DIR%\Host\ReferenceRAG.DesktopHost\bin\Debug\net10.0-windows\publish"
    shift
    goto parse_args
)
if /i "%~1"=="-c" (
    set "CONFIGURATION=%~2"
    shift
    shift
    goto parse_args
)
if /i "%~1"=="--config" (
    set "CONFIGURATION=%~2"
    shift
    shift
    goto parse_args
)
if /i "%~1"=="-r" (
    set "RID=%~2"
    shift
    shift
    goto parse_args
)
if /i "%~1"=="--runtime" (
    set "RID=%~2"
    shift
    shift
    goto parse_args
)
if /i "%~1"=="-o" (
    set "OUTPUT=%~f2"
    shift
    shift
    goto parse_args
)
if /i "%~1"=="--output" (
    set "OUTPUT=%~f2"
    shift
    shift
    goto parse_args
)
if /i "%~1"=="--clean" (
    set "CLEAN=true"
    shift
    goto parse_args
)
if /i "%~1"=="-clean" (
    set "CLEAN=true"
    shift
    goto parse_args
)
if /i "%~1"=="--framework-dependent" (
    set "SELF_CONTAINED=false"
    shift
    goto parse_args
)
if /i "%~1"=="--pdb" (
    set "STRIP_PDB=false"
    shift
    goto parse_args
)
if /i "%~1"=="-v" (
    set "VERBOSE=true"
    shift
    goto parse_args
)
if /i "%~1"=="--verbose" (
    set "VERBOSE=true"
    shift
    goto parse_args
)
if /i "%~1"=="--no-retry" (
    set "RETRY_READY="
    shift
    goto parse_args
)
if /i "%~1"=="--smoke" (
    set "RUN_SMOKE=true"
    shift
    goto parse_args
)
if /i "%~1"=="--batch" (
    set "NON_INTERACTIVE=true"
    shift
    goto parse_args
)
shift
goto parse_args

:after_args

:publish
set "EXIT_CODE=0"

echo.
echo ==========================================================
echo   ReferenceRAG Desktop - Build and Publish
echo ==========================================================
echo.
echo [build-desktop] Project   : "%PROJECT%"
echo [build-desktop] Config    : %CONFIGURATION%
echo [build-desktop] Runtime   : %RID%
echo [build-desktop] Output    : "%OUTPUT%"
echo [build-desktop] SelfCont  : %SELF_CONTAINED%
echo.

if not exist "%PROJECT%" (
    echo [build-desktop] ERROR: Project file not found:
    echo   "%PROJECT%"
    set "EXIT_CODE=1"
    goto finish
)

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [build-desktop] ERROR: dotnet SDK was not found in PATH.
    set "EXIT_CODE=1"
    goto finish
)

if /i "%RUN_SMOKE%"=="true" (
    echo [build-desktop] Running E2E core smoke test before build...
    call "%ROOT_DIR%\smoke-test.bat"
    if errorlevel 1 (
        echo [build-desktop] ERROR: Smoke test failed. Build aborted to prevent regression.
        set "EXIT_CODE=1"
        goto finish
    )
    echo [build-desktop] Smoke test passed. Continuing with publish.
    echo.
)

echo [build-desktop] Checking running processes...
taskkill /F /IM ReferenceRAG.DesktopHost.exe >nul 2>nul

if /i "%CLEAN%"=="true" (
    if exist "%OUTPUT%" (
        echo [build-desktop] Cleaning target directory: "%OUTPUT%"...
        rd /s /q "%OUTPUT%" >nul 2>nul
    )
)

set "PDB_ARG="
if /i "%STRIP_PDB%"=="true" (
    set "PDB_ARG=-p:DebugType=none -p:DebugSymbols=false"
)

set "VERBOSITY_ARG=-v m"
if /i "%VERBOSE%"=="true" (
    set "VERBOSITY_ARG=-v n"
)

echo [build-desktop] Publishing ReferenceRAG.DesktopHost (%CONFIGURATION%, %RID%, StripPdb=%STRIP_PDB%)...
echo [build-desktop] Target: "%OUTPUT%"

dotnet publish "%PROJECT%" -c %CONFIGURATION% -r %RID% --self-contained %SELF_CONTAINED% --nologo -p:OS=Windows_NT %PDB_ARG% %VERBOSITY_ARG% -o "%OUTPUT%"
set "EXIT_CODE=%ERRORLEVEL%"

if "%EXIT_CODE%"=="0" (
    if /i "%STRIP_PDB%"=="true" (
        del /s /q "%OUTPUT%\*.pdb" >nul 2>nul
    )
    if exist "%ROOT_DIR%\Host\ReferenceRAG.DesktopHost\Assets\icon.ico" (
        copy /y "%ROOT_DIR%\Host\ReferenceRAG.DesktopHost\Assets\icon.ico" "%OUTPUT%\icon.ico" >nul 2>nul
    )
    if exist "%ROOT_DIR%\Host\ReferenceRAG.DesktopHost\Assets\window-icon.ico" (
        copy /y "%ROOT_DIR%\Host\ReferenceRAG.DesktopHost\Assets\window-icon.ico" "%OUTPUT%\window-icon.ico" >nul 2>nul
    )
    if exist "%ROOT_DIR%\Host\ReferenceRAG.DesktopHost\Assets\tray-icon.ico" (
        copy /y "%ROOT_DIR%\Host\ReferenceRAG.DesktopHost\Assets\tray-icon.ico" "%OUTPUT%\tray-icon.ico" >nul 2>nul
    )
    echo.
    echo ==========================================================
    echo [build-desktop] Desktop publish succeeded!
    echo   DesktopHost: "%OUTPUT%\ReferenceRAG.DesktopHost.exe"
    echo   Output Dir : "%OUTPUT%"
    echo ==========================================================
    if exist "%ROOT_DIR%\support\artifacts\logs" (
        echo [%date% %time%] desktop publish success - %CONFIGURATION% %RID% >> "%ROOT_DIR%\support\artifacts\logs\desktop-build.log"
    )
) else (
    echo.
    echo ==========================================================
    echo [build-desktop] ERROR: Publish failed with exit code %EXIT_CODE%.
    echo ==========================================================
    if exist "%ROOT_DIR%\support\artifacts\logs" (
        echo [%date% %time%] desktop publish failed - code %EXIT_CODE% >> "%ROOT_DIR%\support\artifacts\logs\desktop-build.log"
    )
)

:finish
echo.
if defined NON_INTERACTIVE (
    exit /b %EXIT_CODE%
)
if defined RETRY_READY (
    choice /C RQ /N /M "[build-desktop] Press R to publish again, Q to close: "
    if errorlevel 2 exit /b %EXIT_CODE%
    if errorlevel 1 goto publish
) else (
    choice /C Q /N /M "[build-desktop] Press Q to close: "
)
exit /b %EXIT_CODE%
