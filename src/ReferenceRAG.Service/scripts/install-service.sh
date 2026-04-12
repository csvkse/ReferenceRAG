#!/bin/bash
# ReferenceRAG Service Install Script (Linux)
# Usage: sudo ./install-service.sh [port] [--cuda /path/to/cuda]

set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
SERVICE_DIR="$(dirname "$SCRIPT_DIR")"
SERVICE_NAME="obsidianrag"
EXECUTABLE="$SERVICE_DIR/ReferenceRAG.Service"
PORT=5000
CUDA_PATH=""

# Parse arguments
while [[ $# -gt 0 ]]; do
    case $1 in
        --cuda)
            CUDA_PATH="$2"
            shift 2
            ;;
        --skip-cuda)
            CUDA_PATH="skip"
            shift
            ;;
        *)
            if [[ $1 =~ ^[0-9]+$ ]]; then
                PORT=$1
            fi
            shift
            ;;
    esac
done

if [ ! -f "$EXECUTABLE" ]; then
    echo "ERROR: Executable not found: $EXECUTABLE"
    exit 1
fi

chmod +x "$EXECUTABLE"

echo "Service directory: $SERVICE_DIR"
echo "Executable: $EXECUTABLE"
echo "Port: $PORT"

# Build environment variables
ENV_STRING="Environment=ASPNETCORE_URLS=http://0.0.0.0:$PORT"

# Detect CUDA
if [ "$CUDA_PATH" != "skip" ]; then
    if [ -z "$CUDA_PATH" ]; then
        # Auto-detect CUDA
        if [ -d "/usr/local/cuda" ]; then
            CUDA_PATH="/usr/local/cuda/lib64"
        elif [ -d "/usr/lib/cuda" ]; then
            CUDA_PATH="/usr/lib/cuda"
        fi
    fi

    if [ -n "$CUDA_PATH" ] && [ -d "$CUDA_PATH" ]; then
        ENV_STRING="$ENV_STRING\nEnvironment=LD_LIBRARY_PATH=$CUDA_PATH:\$LD_LIBRARY_PATH"
        echo "CUDA path: $CUDA_PATH"
    else
        echo "CUDA not detected, using CPU mode"
    fi
fi

cat > /etc/systemd/system/$SERVICE_NAME.service << SERVICEEOF
[Unit]
Description=ReferenceRAG Service
After=network.target

[Service]
Type=simple
WorkingDirectory=$SERVICE_DIR
ExecStart=$EXECUTABLE
Restart=always
RestartSec=10
User=root
$ENV_STRING

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
echo ""
echo "Usage:"
echo "  ./servicectl.sh status   # Check status"
echo "  ./servicectl.sh stop     # Stop service"
echo "  ./servicectl.sh restart  # Restart service"
