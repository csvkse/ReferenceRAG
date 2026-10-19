@echo off
setlocal
set "SCRIPT_DIR=%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%download-cuda-minimal.ps1" %*
exit /b %ERRORLEVEL%
