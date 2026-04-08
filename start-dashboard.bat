@echo off
echo Starting Obsidian RAG Services...
echo.

:: Start WebAPI in background
echo [1/2] Starting WebAPI on http://localhost:5000...
start "Obsidian RAG API" cmd /c "dotnet run --project src/ObsidianRAG.Service --urls http://localhost:5000"

:: Wait for API to start
timeout /t 5 /nobreak > nul

:: Start Dashboard
echo [2/2] Starting Dashboard on http://localhost:5072...
start "Obsidian RAG Dashboard" cmd /c "dotnet run --project src/ObsidianRAG.Dashboard"

echo.
echo Services started!
echo - WebAPI: http://localhost:5000
echo - Dashboard: http://localhost:5072
echo - Swagger: http://localhost:5000/swagger
echo.
echo Press any key to stop all services...
pause > nul

:: Kill processes
taskkill /FI "WINDOWTITLE eq Obsidian RAG API*" /F
taskkill /FI "WINDOWTITLE eq Obsidian RAG Dashboard*" /F
