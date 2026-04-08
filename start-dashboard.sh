#!/bin/bash

echo "Starting Obsidian RAG Services..."
echo ""

# Start WebAPI in background
echo "[1/2] Starting WebAPI on http://localhost:5000..."
dotnet run --project src/ObsidianRAG.Service --urls http://localhost:5000 &
API_PID=$!

# Wait for API to start
sleep 5

# Start Dashboard
echo "[2/2] Starting Dashboard on http://localhost:5173..."
dotnet run --project src/ObsidianRAG.Dashboard &
DASHBOARD_PID=$!

echo ""
echo "Services started!"
echo "- WebAPI: http://localhost:5000"
echo "- Dashboard: http://localhost:5173"
echo "- Swagger: http://localhost:5000/swagger"
echo ""
echo "Press Ctrl+C to stop all services..."

# Wait for Ctrl+C
trap "kill $API_PID $DASHBOARD_PID 2>/dev/null; exit 0" SIGINT SIGTERM
wait
