#!/bin/bash
#
# install-service.sh - Install ObsidianRAG as a systemd service on Linux
#
# Usage: sudo ./install-service.sh [--user USER] [--path PATH]
#
# Options:
#   --user USER    User to run the service (default: obsidianrag)
#   --path PATH    Installation path (default: /opt/obsidianrag)
#

set -e

SERVICE_NAME="obsidianrag"
SERVICE_USER="${SERVICE_USER:-obsidianrag}"
SERVICE_GROUP="${SERVICE_GROUP:-obsidianrag}"
SERVICE_PATH="${SERVICE_PATH:-/opt/obsidianrag}"
SERVICE_FILE="/etc/systemd/system/${SERVICE_NAME}.service"
LOG_DIR="/var/log/${SERVICE_NAME}"
DATA_DIR="${SERVICE_PATH}/data"

# Color output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

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

# Parse arguments
while [[ $# -gt 0 ]]; do
    case $1 in
        --user)
            SERVICE_USER="$2"
            shift 2
            ;;
        --path)
            SERVICE_PATH="$2"
            shift 2
            ;;
        *)
            log_error "Unknown option: $1"
            exit 1
            ;;
    esac
done

SERVICE_GROUP="${SERVICE_GROUP:-${SERVICE_USER}}"
DLL_PATH="${SERVICE_PATH}/ObsidianRAG.Service.dll"

# Check if .NET is installed
if ! command -v dotnet &> /dev/null; then
    log_error ".NET SDK not found. Please install .NET 8.0 or later."
    exit 1
fi

# Check if service file exists
if [[ ! -f "$DLL_PATH" ]]; then
    log_error "ObsidianRAG.Service.dll not found at: ${DLL_PATH}"
    log_error "Please build the project first: dotnet build -c Release"
    exit 1
fi

# Create service user if it doesn't exist
if ! id "${SERVICE_USER}" &>/dev/null; then
    log_info "Creating service user: ${SERVICE_USER}"
    useradd --system --no-create-home --shell /usr/sbin/nologin --comment "ObsidianRAG Service Account" "${SERVICE_USER}" || {
        log_error "Failed to create user"
        exit 1
    }
fi

# Create required directories
log_info "Creating directories..."
mkdir -p "${LOG_DIR}"
mkdir -p "${DATA_DIR}"
mkdir -p "${SERVICE_PATH}/logs"

# Set ownership
chown -R "${SERVICE_USER}:${SERVICE_GROUP}" "${LOG_DIR}"
chown -R "${SERVICE_USER}:${SERVICE_GROUP}" "${DATA_DIR}"
chown -R "${SERVICE_USER}:${SERVICE_GROUP}" "${SERVICE_PATH}/logs"

# Copy service file
log_info "Installing systemd service file..."
SERVICE_FILE_TEMP=$(mktemp)
sed "s|/opt/obsidianrag|${SERVICE_PATH}|g" "$(dirname "$0")/obsidianrag.service" > "${SERVICE_FILE_TEMP}"
sed -i "s|User=obsidianrag|User=${SERVICE_USER}|g" "${SERVICE_FILE_TEMP}"
sed -i "s|Group=obsidianrag|Group=${SERVICE_GROUP}|g" "${SERVICE_FILE_TEMP}"
sed -i "s|/var/log/obsidianrag|${LOG_DIR}|g" "${SERVICE_FILE_TEMP}"

mv "${SERVICE_FILE_TEMP}" "${SERVICE_FILE}"
chmod 644 "${SERVICE_FILE}"

# Reload systemd
log_info "Reloading systemd daemon..."
systemctl daemon-reload

# Enable and start service
log_info "Enabling and starting service..."
systemctl enable "${SERVICE_NAME}.service"
systemctl start "${SERVICE_NAME}.service"

# Check status
sleep 2
if systemctl is-active --quiet "${SERVICE_NAME}.service"; then
    log_info ""
    log_info "Service '${SERVICE_NAME}' installed and running successfully!"
    echo ""
    echo "  Status: $(systemctl is-active ${SERVICE_NAME}.service)"
    echo "  User: ${SERVICE_USER}"
    echo "  Working Directory: ${SERVICE_PATH}"
    echo "  Log: ${LOG_DIR}/service.log"
    echo ""
    echo "Service commands:"
    echo "  sudo systemctl status ${SERVICE_NAME}  - Check status"
    echo "  sudo systemctl stop ${SERVICE_NAME}    - Stop service"
    echo "  sudo systemctl restart ${SERVICE_NAME} - Restart service"
    echo "  sudo journalctl -u ${SERVICE_NAME} -f   - View logs"
else
    log_warn "Service installed but may not be running."
    echo ""
    echo "Check logs for errors:"
    echo "  journalctl -u ${SERVICE_NAME} -n 50"
    echo "  cat ${LOG_DIR}/error.log"
fi
