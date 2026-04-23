@echo off
REM ============================================
REM   公共函数：读取服务端口
REM   用法：call "%~dp0_get_port.bat"
REM   返回：SERVICE_PORT 变量
REM ============================================

set SERVICE_PORT=5000

REM 尝试从 referencerag.json 读取端口
if exist "%~dp0..\referencerag.json" (
    for /f "tokens=2 delims=:" %%a in ('findstr /i "port" "%~dp0..\referencerag.json" 2^>nul') do (
        for /f "tokens=1" %%b in ("%%a") do (
            set SERVICE_PORT=%%b
            REM 移除可能的逗号
            set SERVICE_PORT=!SERVICE_PORT:,=!
        )
    )
)

REM 环境变量优先级最高
if defined OBSIDIAN_RAG_PORT set SERVICE_PORT=%OBSIDIAN_RAG_PORT%

exit /b 0
