@echo off
setlocal EnableDelayedExpansion

echo ============================================
echo   ReferenceRAG Service - 控制台运行
echo ============================================
echo.

cd /d "%~dp0.."

REM 调用公共函数查找 exe
call "%~dp0_find_exe.bat"

if "%EXE_PATH%"=="" (
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

echo 程序路径: %EXE_PATH%
echo.
echo 按 Ctrl+C 停止服务
echo ============================================
echo.

REM 记录启动日志
echo [%date% %time%] 控制台模式启动 >> "%~dp0service.log"

"%EXE_PATH%"
