#!/bin/bash
# ObsidianRAG Service Install Script (Linux)
# Usage: sudo ./install-service.sh

set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
SERVICE_DIR="$(dirname "$SCRIPT_DIR")"
SERVICE_NAME="obsidianrag"
EXECUTABLE="$SERVICE_DIR/ObsidianRAG.Service"
PORT=${1:-5000}

if [ ! -f "$EXECUTABLE" ]; then
    echo "ERROR: Executable not found: $EXECUTABLE"
    exit 1
fi

chmod +x "$EXECUTABLE"

echo "Service directory: $SERVICE_DIR"
echo "Executable: $EXECUTABLE"
echo "Port: $PORT"

cat > /etc/systemd/system/$SERVICE_NAME.service << SERVICEEOF
[Unit]
Description=ObsidianRAG Service
After=network.target

[Service]
Type=simple
WorkingDirectory=$SERVICE_DIR
ExecStart=$EXECUTABLE
Restart=always
RestartSec=10
User=root
Environment=ASPNETCORE_URLS=http://0.0.0.0:$PORT

[Install]
WantedBy=multi-user.target
SERVICEEOF

systemctl daemon-reload
systemctl enable $SERVICE_NAME
systemctl start $SERVICE_NAME

echo ""
echo "========================================"
echo "Service installed and started"
echo "========================================"
echo ""
echo "URL: http://localhost:$PORT"
echo "Status: systemctl status $SERVICE_NAME"
echo "Logs: journalctl -u $SERVICE_NAME -f"
