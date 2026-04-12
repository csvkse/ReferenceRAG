#!/bin/bash
# ObsidianRAG 服务卸载脚本 (Linux)

set -e
SERVICE_NAME="obsidianrag"

systemctl stop $SERVICE_NAME 2>/dev/null || true
systemctl disable $SERVICE_NAME 2>/dev/null || true
rm -f /etc/systemd/system/$SERVICE_NAME.service
systemctl daemon-reload

echo "服务已卸载"
