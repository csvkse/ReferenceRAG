@echo off
REM ReferenceRAG Desktop Build EntryPoint (forward to support\tools\scripts\build-desktop.bat)
call "%~dp0support\tools\scripts\build-desktop.bat" %*
exit /b %ERRORLEVEL%
