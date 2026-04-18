#!/bin/bash
# ReferenceRAG Service Uninstall Script (Linux)
# Usage: sudo ./uninstall-service.sh

set -e
SERVICE_NAME="obsidianrag"

systemctl stop $SERVICE_NAME 2>/dev/null || true
systemctl disable $SERVICE_NAME 2>/dev/null || true
rm -f /etc/systemd/system/$SERVICE_NAME.service
systemctl daemon-reload

echo "Service uninstalled"
