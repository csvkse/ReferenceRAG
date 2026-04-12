#!/bin/bash
# ObsidianRAG 服务安装脚本 (Linux)
# 用法: sudo ./install-service.sh

set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
SERVICE_DIR="$(dirname "$SCRIPT_DIR")"
SERVICE_NAME="obsidianrag"
EXECUTABLE="$SERVICE_DIR/ObsidianRAG.Service"

if [ ! -f "$EXECUTABLE" ]; then
    echo "错误: 找不到可执行文件 $EXECUTABLE"
    exit 1
fi

chmod +x "$EXECUTABLE"

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
Environment=ASPNETCORE_URLS=http://0.0.0.0:5000

[Install]
WantedBy=multi-user.target
SERVICEEOF

systemctl daemon-reload
systemctl enable $SERVICE_NAME
systemctl start $SERVICE_NAME

echo "服务已安装并启动"
echo "状态: systemctl status $SERVICE_NAME"
