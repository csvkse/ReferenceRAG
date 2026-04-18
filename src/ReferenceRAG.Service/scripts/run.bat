@echo off
setlocal EnableDelayedExpansion

echo ============================================
echo   Web API Windows Service - 控制台运行
echo ============================================
echo.

cd /d "%~dp0.."

:: 查找 exe 文件 - 按优先级检查
set EXE_PATH=

if exist "ReferenceRAG.Service.exe" (
    set EXE_PATH=%cd%\ReferenceRAG.Service.exe
    goto :found_exe
)
if exist "publish\ReferenceRAG.Service.exe" (
    set EXE_PATH=%cd%\publish\ReferenceRAG.Service.exe
    goto :found_exe
)
if exist "WebApiWindowsService.exe" (
    set EXE_PATH=%cd%\WebApiWindowsService.exe
    goto :found_exe
)
if exist "publish\WebApiWindowsService.exe" (
    set EXE_PATH=%cd%\publish\WebApiWindowsService.exe
    goto :found_exe
)
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
    echo 未找到已编译的程序，正在构建...
    echo.
    dotnet build -c Release -r win-x64
    if %ERRORLEVEL% neq 0 (
        echo 构建失败！
        pause
        exit /b 1
    )
    set EXE_PATH=%cd%\bin\Release\net10.0\win-x64\ReferenceRAG.Service.exe
)

echo 程序路径: !EXE_PATH!
echo.
echo 按 Ctrl+C 停止服务
echo ============================================
echo.

"!EXE_PATH!"
