@echo off
chcp 65001 >nul
title ReferenceRAG One-Click Deploy

echo.
echo ========================================
echo  ReferenceRAG 一键发布部署
echo ========================================
echo.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0deploy.ps1" %*

pause
