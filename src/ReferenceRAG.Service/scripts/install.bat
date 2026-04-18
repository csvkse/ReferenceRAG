@echo off
setlocal EnableDelayedExpansion

echo ============================================
echo   Web API Windows Service - 安装脚本
echo ============================================
echo.

:: 检查管理员权限
net session >nul 2>&1
if %ERRORLEVEL% neq 0 (
    echo 错误: 请以管理员身份运行此脚本
    echo 右键点击此脚本，选择"以管理员身份运行"
    pause
    exit /b 1
)

cd /d "%~dp0.."

:: 查找 exe 文件 - 按优先级检查
set EXE_PATH=

:: 1. 发布目录：exe 在当前目录
if exist "ReferenceRAG.Service.exe" (
    set EXE_PATH=%cd%\ReferenceRAG.Service.exe
    goto :found_exe
)

:: 2. 开发环境：exe 在 publish 子目录
if exist "publish\ReferenceRAG.Service.exe" (
    set EXE_PATH=%cd%\publish\ReferenceRAG.Service.exe
    goto :found_exe
)

:: 3. 兼容旧名称
if exist "WebApiWindowsService.exe" (
    set EXE_PATH=%cd%\WebApiWindowsService.exe
    goto :found_exe
)
if exist "publish\WebApiWindowsService.exe" (
    set EXE_PATH=%cd%\publish\WebApiWindowsService.exe
    goto :found_exe
)

:: 4. bin 目录
if exist "bin\Release\net10.0\win-x64\ReferenceRAG.Service.exe" (
    set EXE_PATH=%cd%\bin\Release\net10.0\win-x64\ReferenceRAG.Service.exe
    goto :found_exe
)
if exist "bin\Debug\net10.0\win-x64\ReferenceRAG.Service.exe" (
    set EXE_PATH=%cd%\bin\Debug\net10.0\win-x64\ReferenceRAG.Service.exe
    goto :found_exe
)

:found_exe
if "!EXE_PATH!"=="" (
    echo 错误: 未找到 ReferenceRAG.Service.exe
    echo 请先运行 build.bat 构建项目
    pause
    exit /b 1
)

echo 程序路径: !EXE_PATH!
echo.

:: 执行安装
"!EXE_PATH!" --install

if %ERRORLEVEL% equ 0 (
    echo.
    echo ============================================
    echo   安装完成！
    echo.
    set /p START_NOW="是否立即启动服务？(Y/N): "
    if /i "!START_NOW!"=="Y" (
        echo.
        echo 正在启动服务...
        "!EXE_PATH!" --start
    )
    echo ============================================
)

pause
