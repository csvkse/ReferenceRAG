@echo off
REM ReferenceRAG CUDA 12 Minimal Runtime Downloader EntryPoint
call "%~dp0support\tools\scripts\download-cuda-minimal.bat" %*
exit /b %ERRORLEVEL%
