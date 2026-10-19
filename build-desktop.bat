@echo off
setlocal EnableDelayedExpansion
set "_RESTORE_CP="
for /f "tokens=2 delims=:." %%a in ('chcp') do (
    set "_CURR_CP=%%a"
    set "_CURR_CP=!_CURR_CP: =!"
    if not "!_CURR_CP!"=="65001" (
        set "_RESTORE_CP=!_CURR_CP!"
        chcp 65001 >nul
    )
)

REM ReferenceRAG Desktop Build EntryPoint (forward to support\tools\scripts\build-desktop.bat)
call "%~dp0support\tools\scripts\build-desktop.bat" %*
set "_EXIT_CODE=%ERRORLEVEL%"

if defined _RESTORE_CP chcp %_RESTORE_CP% >nul
exit /b %_EXIT_CODE%
