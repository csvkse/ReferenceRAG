@echo off
setlocal EnableDelayedExpansion

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
set "PRESERVE_CONFIG=true"
set "GPU_BACKEND="
set "GPU_BACKEND_DESC="

:parse_args
if "%~1"=="" goto after_args
if /i "%~1"=="--universal" (
    set "GPU_BACKEND=universal"
    shift
    goto parse_args
)
if /i "%~1"=="--directml" (
    set "GPU_BACKEND=universal"
    shift
    goto parse_args
)
if /i "%~1"=="--cuda" (
    set "GPU_BACKEND=cuda"
    shift
    goto parse_args
)
if /i "%~1"=="--nvidia" (
    set "GPU_BACKEND=cuda"
    shift
    goto parse_args
)
if /i "%~1"=="--amd" (
    set "GPU_BACKEND=amd"
    shift
    goto parse_args
)
if /i "%~1"=="--radeon" (
    set "GPU_BACKEND=amd"
    shift
    goto parse_args
)
if /i "%~1"=="--intel" (
    set "GPU_BACKEND=intel"
    shift
    goto parse_args
)
if /i "%~1"=="--arc" (
    set "GPU_BACKEND=intel"
    shift
    goto parse_args
)
if /i "%~1"=="--cpu" (
    set "GPU_BACKEND=cpu"
    shift
    goto parse_args
)
if /i "%~1"=="-b" (
    set "GPU_BACKEND=%~2"
    shift
    shift
    goto parse_args
)
if /i "%~1"=="--backend" (
    set "GPU_BACKEND=%~2"
    shift
    shift
    goto parse_args
)
if /i "%~1"=="--reset-config" (
    set "PRESERVE_CONFIG=false"
    shift
    goto parse_args
)
if /i "%~1"=="--no-preserve-config" (
    set "PRESERVE_CONFIG=false"
    shift
    goto parse_args
)
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

if not defined GPU_BACKEND (
    if defined NON_INTERACTIVE (
        set "GPU_BACKEND=universal"
        goto normalize_backend
    )
    goto prompt_gpu_backend
)
goto normalize_backend

:prompt_gpu_backend
echo.
echo ==========================================================
echo   请选择桌面版硬件加速构建方案 (GPU Backend Variant):
echo ==========================================================
echo   [1] 通用版 (Universal / DirectML) [默认 - 推荐]
echo       全显卡通用兼备 (AMD / Intel / NVIDIA / 高通骁龙 / 核显)
echo   [2] NVIDIA 专属满血版 (NVIDIA CUDA 12)
echo       专为 GeForce RTX / GTX 独显优化 (CUDA 12 + TensorRT 满血算力)
echo   [3] AMD 专属优化版 (AMD Radeon / APU)
echo       专为 AMD Radeon 独显与 Ryzen/APU 核显优化 (DirectML + Vulkan)
echo   [4] Intel 专属优化版 (Intel Arc / Ultra)
echo       专为 Intel Arc 独显与酷睿 Ultra 核显优化 (DirectML + Vulkan)
echo ----------------------------------------------------------
set "USER_CHOICE="
set /p "USER_CHOICE=[build-desktop] 请输入选项 [1-4] (直接回车默认 [1] 通用版): "
if "%USER_CHOICE%"=="" set "USER_CHOICE=1"
if "%USER_CHOICE%"=="1" set "GPU_BACKEND=universal"
if "%USER_CHOICE%"=="2" set "GPU_BACKEND=cuda"
if "%USER_CHOICE%"=="3" set "GPU_BACKEND=amd"
if "%USER_CHOICE%"=="4" set "GPU_BACKEND=intel"
if "%USER_CHOICE%"=="5" set "GPU_BACKEND=cpu"
if not defined GPU_BACKEND set "GPU_BACKEND=universal"

:normalize_backend
set "GPU_BACKEND_DESC=通用版 (Universal / DirectML + Vulkan)"
if /i "%GPU_BACKEND%"=="universal" set "GPU_BACKEND=universal"& set "GPU_BACKEND_DESC=通用版 (Universal / DirectML + Vulkan - 全显卡兼备)"
if /i "%GPU_BACKEND%"=="directml" set "GPU_BACKEND=universal"& set "GPU_BACKEND_DESC=通用版 (Universal / DirectML + Vulkan - 全显卡兼备)"
if /i "%GPU_BACKEND%"=="cuda" set "GPU_BACKEND=cuda"& set "GPU_BACKEND_DESC=NVIDIA 专属满血版 (CUDA 12 + TensorRT 极致算力)"
if /i "%GPU_BACKEND%"=="nvidia" set "GPU_BACKEND=cuda"& set "GPU_BACKEND_DESC=NVIDIA 专属满血版 (CUDA 12 + TensorRT 极致算力)"
if /i "%GPU_BACKEND%"=="amd" set "GPU_BACKEND=amd"& set "GPU_BACKEND_DESC=AMD 专属优化版 (AMD Radeon 独显与 APU 核显优化)"
if /i "%GPU_BACKEND%"=="rocm" set "GPU_BACKEND=amd"& set "GPU_BACKEND_DESC=AMD 专属优化版 (AMD Radeon 独显与 APU 核显优化)"
if /i "%GPU_BACKEND%"=="radeon" set "GPU_BACKEND=amd"& set "GPU_BACKEND_DESC=AMD 专属优化版 (AMD Radeon 独显与 APU 核显优化)"
if /i "%GPU_BACKEND%"=="intel" set "GPU_BACKEND=intel"& set "GPU_BACKEND_DESC=Intel 专属优化版 (Intel Arc 独显与酷睿 Ultra 核显优化)"
if /i "%GPU_BACKEND%"=="arc" set "GPU_BACKEND=intel"& set "GPU_BACKEND_DESC=Intel 专属优化版 (Intel Arc 独显与酷睿 Ultra 核显优化)"
if /i "%GPU_BACKEND%"=="openvino" set "GPU_BACKEND=intel"& set "GPU_BACKEND_DESC=Intel 专属优化版 (Intel Arc 独显与酷睿 Ultra 核显优化)"
if /i "%GPU_BACKEND%"=="cpu" set "GPU_BACKEND=cpu"& set "GPU_BACKEND_DESC=纯 CPU 兼容版 (零 GPU 依赖 / AVX2 多核)"

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
echo [build-desktop] GPU Model : %GPU_BACKEND_DESC% (%GPU_BACKEND%)
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

set "CONFIG_BACKUP=%TEMP%\referencerag_desktop_appsettings_backup.json"
if exist "%CONFIG_BACKUP%" del /f /q "%CONFIG_BACKUP%" >nul 2>nul

if /i "%PRESERVE_CONFIG%"=="true" (
    if exist "%OUTPUT%\appsettings.json" (
        echo [build-desktop] Backing up existing appsettings.json to preserve user configuration...
        copy /y "%OUTPUT%\appsettings.json" "%CONFIG_BACKUP%" >nul 2>nul
    )
)

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

dotnet publish "%PROJECT%" -c %CONFIGURATION% -r %RID% --self-contained %SELF_CONTAINED% --nologo -p:OS=Windows_NT -p:GpuBackend=%GPU_BACKEND% %PDB_ARG% %VERBOSITY_ARG% -o "%OUTPUT%"
set "EXIT_CODE=%ERRORLEVEL%"

if "%EXIT_CODE%"=="0" goto publish_success
goto publish_failed

:publish_success
if exist "%CONFIG_BACKUP%" (
    echo [build-desktop] Merging user configuration into published appsettings.json...
    powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT_DIR%\support\tools\scripts\merge-appsettings.ps1" -PublishedPath "%OUTPUT%\appsettings.json" -BackupPath "%CONFIG_BACKUP%"
    del /f /q "%CONFIG_BACKUP%" >nul 2>nul
)
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
echo   GPU Variant: %GPU_BACKEND_DESC% (%GPU_BACKEND%)
echo   Output Dir : "%OUTPUT%"
echo ==========================================================
if exist "%ROOT_DIR%\support\artifacts\logs" (
    echo [%date% %time%] desktop publish success - %CONFIGURATION% %RID% - %GPU_BACKEND% >> "%ROOT_DIR%\support\artifacts\logs\desktop-build.log"
)
goto publish_done

:publish_failed
if exist "%CONFIG_BACKUP%" (
    if not exist "%OUTPUT%\appsettings.json" (
        copy /y "%CONFIG_BACKUP%" "%OUTPUT%\appsettings.json" >nul 2>nul
    )
    del /f /q "%CONFIG_BACKUP%" >nul 2>nul
)
echo.
echo ==========================================================
echo [build-desktop] ERROR: Publish failed with exit code %EXIT_CODE%.
echo ==========================================================
if exist "%ROOT_DIR%\support\artifacts\logs" (
    echo [%date% %time%] desktop publish failed - code %EXIT_CODE% - %GPU_BACKEND% >> "%ROOT_DIR%\support\artifacts\logs\desktop-build.log"
)
goto publish_done

:publish_done

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
