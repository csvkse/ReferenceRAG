@echo off
REM ============================================
REM   公共函数：查找可执行文件
REM   用法：call "%~dp0_find_exe.bat"
REM   返回：EXE_PATH 变量
REM ============================================

REM 设置默认返回值
set EXE_PATH=

REM 按优先级检查 exe 文件
REM 1. 发布目录：exe 在当前目录
if exist "ReferenceRAG.Service.exe" (
    set EXE_PATH=%cd%\ReferenceRAG.Service.exe
    exit /b 0
)

REM 2. publish 子目录
if exist "publish\ReferenceRAG.Service.exe" (
    set EXE_PATH=%cd%\publish\ReferenceRAG.Service.exe
    exit /b 0
)

REM 3. bin 目录（Release）
if exist "bin\Release\net10.0\win-x64\ReferenceRAG.Service.exe" (
    set EXE_PATH=%cd%\bin\Release\net10.0\win-x64\ReferenceRAG.Service.exe
    exit /b 0
)

REM 4. bin 目录（Debug）
if exist "bin\Debug\net10.0\win-x64\ReferenceRAG.Service.exe" (
    set EXE_PATH=%cd%\bin\Debug\net10.0\win-x64\ReferenceRAG.Service.exe
    exit /b 0
)

REM 未找到
exit /b 1
