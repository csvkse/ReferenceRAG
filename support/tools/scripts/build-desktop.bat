@echo off
setlocal EnableDelayedExpansion

REM 若当前不是 UTF-8 (65001)，则切换并记录原代码页以备退出时恢复
set "_RESTORE_CP="
for /f "tokens=2 delims=:." %%a in ('chcp') do (
    set "_CURR_CP=%%a"
    set "_CURR_CP=!_CURR_CP: =!"
    if not "!_CURR_CP!"=="65001" (
        set "_RESTORE_CP=!_CURR_CP!"
        chcp 65001 >nul
    )
)

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
set "BUNDLE_CUDA="

:parse_args
if "%~1"=="" goto after_args
if /i "%~1"=="-h" goto show_help
if /i "%~1"=="--help" goto show_help
if /i "%~1"=="/?" goto show_help
if /i "%~1"=="-?" goto show_help
if /i "%~1"=="--bundle-cuda" (
    set "BUNDLE_CUDA=true"
    shift
    goto parse_args
)
if /i "%~1"=="--no-bundle-cuda" (
    set "BUNDLE_CUDA=false"
    shift
    goto parse_args
)
if /i "%~1"=="--cuda-slim" (
    set "GPU_BACKEND=cuda"
    set "BUNDLE_CUDA=false"
    shift
    goto parse_args
)
if /i "%~1"=="--slim" (
    set "BUNDLE_CUDA=false"
    shift
    goto parse_args
)
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

:show_help
echo.
echo ===============================================================================
echo   ReferenceRAG Desktop - 本地构建与硬件加速打包工具
echo ===============================================================================
echo.
echo 用法 [Usage]:
echo   build-desktop.bat [选项]
echo.
echo 硬件加速方案选型 [GPU Backend Variants]:
echo   --universal, --directml    通用加速版 (DirectML + Vulkan，支持全品牌显卡) [默认推荐]
echo   --cuda, --nvidia           NVIDIA 满血版 (CUDA 12 + TensorRT，内嵌微型便携库)
echo   --cuda-slim, --slim        NVIDIA 精简版 (CUDA 12，无内嵌依赖 ~70MB，借用本机已有环境)
echo   --amd, --radeon            AMD 优化版 (DirectML + Vulkan，适配 Radeon 与 APU)
echo   --intel, --arc             Intel 优化版 (DirectML + Vulkan，适配 Arc 与 Ultra)
echo   --cpu                      纯 CPU 兼容版 (AVX2 / AVX-512，零 GPU 依赖)
echo   -b, --backend [name]       指定构建后端名称 [universal / cuda / amd / intel / cpu]
echo.
echo 构建控制选项 [Build Flags]:
echo   -c, --config [name]        构建配置 [Release 或 Debug，默认: Release]
echo   --debug                    等同于 -c Debug --pdb
echo   -r, --runtime [rid]        目标运行时标识符 [默认: win-x64]
echo   -o, --output [dir]         输出目录 [默认: Host/ReferenceRAG.DesktopHost/bin/...]
echo   --clean                    构建前清空目标输出目录
echo   --bundle-cuda              强制下载并内嵌微型 CUDA 12 核心库
echo   --no-bundle-cuda           跳过下载与内嵌微型 CUDA 12 核心库
echo   --framework-dependent      构建框架依赖版本 [非独立分发，默认: --self-contained]
echo   --pdb                      保留 PDB 调试符号文件 [默认: 去除 PDB 以压缩体积]
echo   --smoke                    构建前执行端到端自动化冒烟测试门禁 [smoke-test.bat]
echo   --batch                    非交互模式运行 [CI/CD 自动化流水线使用]
echo   --reset-config             构建后不保留历史 appsettings.json 配置
echo   -v, --verbose              输出详细的 dotnet MSBuild 构建日志
echo   -h, --help, /?             显示此帮助信息
echo.
echo 示例 [Examples]:
echo   build-desktop.bat                         交互式菜单选择显卡构建
echo   build-desktop.bat --universal             快速构建通用免依赖版
echo   build-desktop.bat --cuda-slim -c Release  快速构建 NVIDIA 极轻量精简版 (~70MB)
echo   build-desktop.bat --cuda --bundle-cuda    构建 NVIDIA 全便携解压即用版
echo   build-desktop.bat -b cpu --clean --batch  无交互构建纯 CPU 兼容版
echo.
echo ===============================================================================
if defined _RESTORE_CP chcp %_RESTORE_CP% >nul
exit /b 0

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
echo ===============================================================================
echo   ReferenceRAG Desktop - 请选择硬件加速构建方案 [GPU Backend Variant]
echo ===============================================================================
echo.
echo   [1] 通用加速版  (Universal / DirectML + Vulkan)  [默认 - 强烈推荐]
echo       - 适用: 全显卡通用 (AMD / Intel / NVIDIA / 高通骁龙 / 各类核显)
echo       - 特点: 完全免装额外 Toolkit/驱动依赖，系统具备显卡驱动即可直接硬件加速
echo.
echo   [2] NVIDIA 满血版 (NVIDIA CUDA 12 + TensorRT - 内嵌便携库)
echo       - 适用: NVIDIA GeForce RTX / GTX 独立显卡
echo       - 特点: 免装 3GB+ Toolkit，随包内嵌微型绿色 CUDA 12 库，解压即用 (~700MB)
echo.
echo   [3] NVIDIA 精简版 (NVIDIA CUDA 12 + TensorRT - 无依赖极简版)
echo       - 适用: 本机已装 CUDA 12 / PyTorch / Ollama 的开发者与极客
echo       - 特点: 零额外冗余动态库，体积极小 (~70MB)，借用本机已有环境秒速下载
echo.
echo   [4] AMD 专属优化版 (AMD Radeon 独显与 Ryzen/APU 核显)
echo       - 适用: AMD Radeon RX 6000/7000/8000 独显及锐龙 APU 核显
echo       - 特点: 免装 ROCm，预置 DirectML + Vulkan 优化管线，驱动原生承接
echo.
echo   [5] Intel 专属优化版 (Intel Arc 独显与酷睿 Ultra 核显)
echo       - 适用: Intel Arc 独显、酷睿 Ultra、Iris Xe 核显
echo       - 特点: 免装 oneAPI，预置 Intel GPU 优化管线，驱动原生承接
echo.
echo   [6] 纯 CPU 兼容版 (Pure CPU AVX2 / AVX-512)
echo       - 适用: 无独显老旧电脑、轻量云服务器或虚拟机
echo       - 特点: 零 GPU 依赖，纯多核 CPU 高兼容计算
echo.
echo ===============================================================================
set "USER_CHOICE="
set /p "USER_CHOICE=[build-desktop] 请输入选项 [1-6] (直接回车默认 [1] 通用版): "
if "%USER_CHOICE%"=="" set "USER_CHOICE=1"
if "%USER_CHOICE%"=="1" set "GPU_BACKEND=universal"
if "%USER_CHOICE%"=="2" set "GPU_BACKEND=cuda"& set "BUNDLE_CUDA=true"
if "%USER_CHOICE%"=="3" set "GPU_BACKEND=cuda"& set "BUNDLE_CUDA=false"
if "%USER_CHOICE%"=="4" set "GPU_BACKEND=amd"
if "%USER_CHOICE%"=="5" set "GPU_BACKEND=intel"
if "%USER_CHOICE%"=="6" set "GPU_BACKEND=cpu"
if not defined GPU_BACKEND set "GPU_BACKEND=universal"

:normalize_backend
set "GPU_BACKEND_DESC=通用版 [Universal / DirectML + Vulkan]"
if /i "%GPU_BACKEND%"=="universal" set "GPU_BACKEND=universal"& set "GPU_BACKEND_DESC=通用版 [Universal / DirectML + Vulkan - 全显卡兼备]"
if /i "%GPU_BACKEND%"=="directml" set "GPU_BACKEND=universal"& set "GPU_BACKEND_DESC=通用版 [Universal / DirectML + Vulkan - 全显卡兼备]"
if /i "%GPU_BACKEND%"=="cuda" set "GPU_BACKEND=cuda"& set "GPU_BACKEND_DESC=NVIDIA 专属满血版 [CUDA 12 - 内嵌微型便携库]"
if /i "%GPU_BACKEND%"=="cuda" if /i "%BUNDLE_CUDA%"=="false" set "GPU_BACKEND_DESC=NVIDIA 专属精简版 [CUDA 12 - 无依赖极简版]"
if /i "%GPU_BACKEND%"=="nvidia" set "GPU_BACKEND=cuda"& set "GPU_BACKEND_DESC=NVIDIA 专属满血版 [CUDA 12 - 内嵌微型便携库]"
if /i "%GPU_BACKEND%"=="nvidia" if /i "%BUNDLE_CUDA%"=="false" set "GPU_BACKEND_DESC=NVIDIA 专属精简版 [CUDA 12 - 无依赖极简版]"
if /i "%GPU_BACKEND%"=="amd" set "GPU_BACKEND=amd"& set "GPU_BACKEND_DESC=AMD 专属优化版 [AMD Radeon 独显与 APU 核显优化]"
if /i "%GPU_BACKEND%"=="rocm" set "GPU_BACKEND=amd"& set "GPU_BACKEND_DESC=AMD 专属优化版 [AMD Radeon 独显与 APU 核显优化]"
if /i "%GPU_BACKEND%"=="radeon" set "GPU_BACKEND=amd"& set "GPU_BACKEND_DESC=AMD 专属优化版 [AMD Radeon 独显与 APU 核显优化]"
if /i "%GPU_BACKEND%"=="intel" set "GPU_BACKEND=intel"& set "GPU_BACKEND_DESC=Intel 专属优化版 [Intel Arc 独显与酷睿 Ultra 核显优化]"
if /i "%GPU_BACKEND%"=="arc" set "GPU_BACKEND=intel"& set "GPU_BACKEND_DESC=Intel 专属优化版 [Intel Arc 独显与酷睿 Ultra 核显优化]"
if /i "%GPU_BACKEND%"=="openvino" set "GPU_BACKEND=intel"& set "GPU_BACKEND_DESC=Intel 专属优化版 [Intel Arc 独显与酷睿 Ultra 核显优化]"
if /i "%GPU_BACKEND%"=="cpu" set "GPU_BACKEND=cpu"& set "GPU_BACKEND_DESC=纯 CPU 兼容版 [零 GPU 依赖 / AVX2 多核]"

:publish
set "EXIT_CODE=0"

echo.
echo ===============================================================================
echo   ReferenceRAG Desktop - 开始构建与发布 [Build and Publish]
echo ===============================================================================
echo   [项目文件]   : "%PROJECT%"
echo   [构建配置]   : %CONFIGURATION%
echo   [目标架构]   : %RID%
echo   [硬件加速]   : %GPU_BACKEND_DESC% (%GPU_BACKEND%)
echo   [独立分发]   : %SELF_CONTAINED%
echo   [输出目录]   : "%OUTPUT%"
echo ===============================================================================
echo.

if not exist "%PROJECT%" (
    echo [build-desktop] 错误: 未找到桌面端项目文件:
    echo   "%PROJECT%"
    set "EXIT_CODE=1"
    goto finish
)

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [build-desktop] 错误: 系统环境变量 PATH 中未找到 dotnet SDK。请先安装 .NET 10 SDK。
    set "EXIT_CODE=1"
    goto finish
)

if /i "%RUN_SMOKE%"=="true" (
    echo [build-desktop] 正在执行构建前端到端自动化冒烟测试门禁...
    call "%ROOT_DIR%\smoke-test.bat"
    if errorlevel 1 (
        echo [build-desktop] 错误: 冒烟测试未通过，已中止构建以防止质量回归。
        set "EXIT_CODE=1"
        goto finish
    )
    echo [build-desktop] 冒烟测试全部通过，继续执行构建发布流程。
    echo.
)

echo [build-desktop] 检查并清理旧有运行中进程...
taskkill /F /IM ReferenceRAG.DesktopHost.exe >nul 2>nul

set "CONFIG_BACKUP=%TEMP%\referencerag_desktop_appsettings_backup.json"
if exist "%CONFIG_BACKUP%" del /f /q "%CONFIG_BACKUP%" >nul 2>nul

if /i "%PRESERVE_CONFIG%"=="true" (
    if exist "%OUTPUT%\appsettings.json" (
        echo [build-desktop] 备份已有用户配置 appsettings.json 以免被构建覆盖...
        copy /y "%OUTPUT%\appsettings.json" "%CONFIG_BACKUP%" >nul 2>nul
    )
)

if /i "%CLEAN%"=="true" (
    if exist "%OUTPUT%" (
        echo [build-desktop] 正在清理发布目标目录: "%OUTPUT%"...
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

echo [build-desktop] 正在编译发布 ReferenceRAG.DesktopHost (%CONFIGURATION%, %RID%, StripPdb=%STRIP_PDB%)...
echo [build-desktop] 目标路径: "%OUTPUT%"
echo.

dotnet publish "%PROJECT%" -c %CONFIGURATION% -r %RID% --self-contained %SELF_CONTAINED% --nologo -p:OS=Windows_NT -p:GpuBackend=%GPU_BACKEND% %PDB_ARG% %VERBOSITY_ARG% -o "%OUTPUT%"
set "EXIT_CODE=%ERRORLEVEL%"

if "%EXIT_CODE%"=="0" goto publish_success
goto publish_failed

:publish_success
if exist "%CONFIG_BACKUP%" (
    if not exist "%OUTPUT%\appsettings.json" (
        copy /y "%CONFIG_BACKUP%" "%OUTPUT%\appsettings.json" >nul 2>nul
    ) else (
        echo [build-desktop] 正在将用户个性化配置合并回 appsettings.json...
        powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT_DIR%\support\tools\scripts\merge-appsettings.ps1" -PublishedPath "%OUTPUT%\appsettings.json" -BackupPath "%CONFIG_BACKUP%"
    )
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

if /i "%GPU_BACKEND%"=="cuda" (
    call :bundle_cuda_runtime
)

echo.
echo ===============================================================================
echo   [build-desktop] 恭喜！ReferenceRAG 桌面端构建发布成功！
echo ===============================================================================
echo   主程序入口 : "%OUTPUT%\ReferenceRAG.DesktopHost.exe"
echo   硬件加速版 : %GPU_BACKEND_DESC% (%GPU_BACKEND%)
echo   目标目录   : "%OUTPUT%"
echo ===============================================================================
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
echo ===============================================================================
echo   [build-desktop] 错误: 构建发布失败，退出代码 %EXIT_CODE%。
echo ===============================================================================
if exist "%ROOT_DIR%\support\artifacts\logs" (
    echo [%date% %time%] desktop publish failed - code %EXIT_CODE% - %GPU_BACKEND% >> "%ROOT_DIR%\support\artifacts\logs\desktop-build.log"
)
goto publish_done

:publish_done

:finish
echo.
if defined NON_INTERACTIVE (
    if defined _RESTORE_CP chcp %_RESTORE_CP% >nul
    exit /b %EXIT_CODE%
)
if defined RETRY_READY (
    choice /C RQ /N /M "[build-desktop] 按 R 重新构建发布，按 Q 退出 [Press R to retry, Q to quit]: "
    if errorlevel 2 (
        if defined _RESTORE_CP chcp %_RESTORE_CP% >nul
        exit /b %EXIT_CODE%
    )
    if errorlevel 1 goto publish
) else (
    choice /C Q /N /M "[build-desktop] 按 Q 退出 [Press Q to quit]: "
)
if defined _RESTORE_CP chcp %_RESTORE_CP% >nul
exit /b %EXIT_CODE%

:bundle_cuda_runtime
if /i "%BUNDLE_CUDA%"=="false" goto skip_bundle_cuda

echo.
echo [build-desktop] NVIDIA 专属版: 正在检查 CUDA 12 核心运行库打包...
set "OUT_CUDA_DIR=%OUTPUT%\runtimes\cuda"
set "LOCAL_CUDA_DIR=%ROOT_DIR%\runtimes\cuda"

if not exist "%OUT_CUDA_DIR%\cublas64_12.dll" goto check_local_cuda
if not exist "%OUT_CUDA_DIR%\cublasLt64_12.dll" goto check_local_cuda
echo [build-desktop] 发布目录已包含完整的 CUDA 12 核心库，跳过复制 [幂等跳过]。
goto :eof

:check_local_cuda
if not exist "%LOCAL_CUDA_DIR%\cublas64_12.dll" goto check_bundle_needed
if not exist "%LOCAL_CUDA_DIR%\cublasLt64_12.dll" goto check_bundle_needed

echo [build-desktop] 检测到本地已存在 CUDA 12 核心库，正在自动打包至发布目录...
if not exist "%OUT_CUDA_DIR%" mkdir "%OUT_CUDA_DIR%"
xcopy /y /d "%LOCAL_CUDA_DIR%\*.dll" "%OUT_CUDA_DIR%\" >nul 2>nul
echo [build-desktop] CUDA 12 核心库打包成功: "%OUT_CUDA_DIR%"
goto :eof

:check_bundle_needed
if /i "%BUNDLE_CUDA%"=="true" goto do_bundle_download

if defined NON_INTERACTIVE (
    echo [build-desktop] 提示: 本地未就绪便携 CUDA 库。如需随包分发，请指定 --bundle-cuda 参数。
    goto :eof
)

echo -------------------------------------------------------------------------------
echo [build-desktop] 提示: 本地尚未下载便携式微型 CUDA 12 核心库。
echo [build-desktop] 打包微型库可使目标电脑无需安装完整 CUDA Toolkit 即可开启满血 GPU 加速。
echo -------------------------------------------------------------------------------
set "USER_BUNDLE_CHOICE="
set /p "USER_BUNDLE_CHOICE=[build-desktop] 是否现在自动拉取微型 CUDA 12 核心库并打包？[Y/N] (直接回车默认 Y): "
if "%USER_BUNDLE_CHOICE%"=="" set "USER_BUNDLE_CHOICE=Y"
if /i "%USER_BUNDLE_CHOICE%"=="Y" goto do_bundle_download
echo [build-desktop] 已跳过便携库打包。目标程序将优先使用本机 CUDA Toolkit，或后续可运行 download-cuda-minimal.bat。
goto :eof

:do_bundle_download
echo [build-desktop] 正在触发微型 CUDA 12 核心库幂等下载...
powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT_DIR%\support\tools\scripts\download-cuda-minimal.ps1" -TargetDir "%LOCAL_CUDA_DIR%"
if errorlevel 1 (
    echo [build-desktop] 警告: 微型 CUDA 12 运行库拉取未完成，跳过打包。
    echo [build-desktop] 桌面版已发布完成，后续可随时运行 download-cuda-minimal.bat 补充。
    goto :eof
)

if exist "%LOCAL_CUDA_DIR%\cublas64_12.dll" (
    if not exist "%OUT_CUDA_DIR%" mkdir "%OUT_CUDA_DIR%"
    xcopy /y /d "%LOCAL_CUDA_DIR%\*.dll" "%OUT_CUDA_DIR%\" >nul 2>nul
    echo [build-desktop] CUDA 12 核心库打包成功: "%OUT_CUDA_DIR%"
)
goto :eof

:skip_bundle_cuda
echo [build-desktop] 已指定 --no-bundle-cuda / --cuda-slim，跳过便携库打包 [生成无依赖轻量版]。
goto :eof
