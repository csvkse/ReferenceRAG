#!/bin/bash
#
# uninstall-service.sh - Remove ObsidianRAG systemd service
#
# Usage: sudo ./uninstall-service.sh [--purge]
#
# Options:
#   --purge    Also remove user, logs, and data directories
#

set -e

SERVICE_NAME="obsidianrag"
SERVICE_FILE="/etc/systemd/system/${SERVICE_NAME}.service"
LOG_DIR="/var/log/${SERVICE_NAME}"
DATA_DIR="/opt/obsidianrag/data"
SERVICE_PATH="/opt/obsidianrag"

# Color output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m'

log_info() {
    echo -e "${GREEN}[INFO]${NC} $1"
}

log_warn() {
    echo -e "${YELLOW}[WARN]${NC} $1"
}

log_error() {
    echo -e "${RED}[ERROR]${NC} $1"
}

# Check if running as root
if [[ $EUID -ne 0 ]]; then
    log_error "This script must be run as root (use sudo)"
    exit 1
fi

PURGE=false
while [[ $# -gt 0 ]]; do
    case $1 in
        --purge)
            PURGE=true
            shift
            ;;
        *)
            log_error "Unknown option: $1"
            exit 1
            ;;
    esac
done

# Check if service exists
if [[ ! -f "$SERVICE_FILE" ]]; then
    log_info "Service '${SERVICE_NAME}' is not installed."
    exit 0
fi

# Stop and disable service
log_info "Stopping and disabling service..."
systemctl stop "${SERVICE_NAME}.service" 2>/dev/null || true
systemctl disable "${SERVICE_NAME}.service" 2>/dev/null || true

# Remove service file
log_info "Removing systemd service file..."
rm -f "${SERVICE_FILE}"
systemctl daemon-reload

# Remove user (get username from service file if exists)
if [[ -f "$SERVICE_FILE" ]] || systemctl cat "${SERVICE_NAME}.service" &>/dev/null 2>&1; then
    SERVICE_USER=$(systemctl cat "${SERVICE_NAME}.service" 2>/dev/null | grep "^User=" | cut -d= -f2)
fi
SERVICE_USER="${SERVICE_USER:-obsidianrag}"

log_info "Service '${SERVICE_NAME}' has been uninstalled."

# Optional purge
if [[ "$PURGE" == true ]]; then
    log_warn "Purging data..."
    
    # Remove directories
    [[ -d "$LOG_DIR" ]] && rm -rf "$LOG_DIR" && log_info "Removed: $LOG_DIR"
    [[ -d "$DATA_DIR" ]] && rm -rf "$DATA_DIR" && log_info "Removed: $DATA_DIR"
    [[ -d "$SERVICE_PATH/logs" ]] && rm -rf "$SERVICE_PATH/logs" && log_info "Removed: $SERVICE_PATH/logs"
    
    # Remove user if exists and not used by other services
    if id "${SERVICE_USER}" &>/dev/null; then
        OTHER_SERVICES=$(systemctl list-unit-files --type=service | grep -c "${SERVICE_USER}" || true)
        if [[ "$OTHER_SERVICES" -eq 0 ]] || [[ "$OTHER_SERVICES" -eq 1 && -f "/etc/systemd/system/${SERVICE_USER}.service" ]]; then
            userdel "${SERVICE_USER}" 2>/dev/null && log_info "Removed user: ${SERVICE_USER}" || true
        fi
    fi
    
    log_info "Purge complete."
fi
