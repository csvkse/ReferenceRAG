#!/bin/bash
#
# servicectl.sh - Control ReferenceRAG systemd service
#
# Usage: ./servicectl.sh [status|start|stop|restart|logs]
#

SERVICE_NAME="obsidianrag"

# Color output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
NC='\033[0m'

ACTION="${1:-status}"

# Check if running as root for privileged actions
needs_root() {
    [[ $EUID -ne 0 ]]
}

check_root() {
    if needs_root; then
        echo -e "${RED}[ERROR]${NC} This action requires root privileges. Use sudo."
        exit 1
    fi
}

case "$ACTION" in
    status)
        if systemctl is-active --quiet "${SERVICE_NAME}.service"; then
            echo -e "${GREEN}●${NC} ${SERVICE_NAME}.service - ReferenceRAG Knowledge Base Service"
            echo -e "   Active: ${GREEN}active (running)${NC}"
            echo ""
            echo -e "${CYAN}Recent logs:${NC}"
            journalctl -u "${SERVICE_NAME}.service" --no-pager -n 5
        else
            echo -e "${RED}●${NC} ${SERVICE_NAME}.service - ReferenceRAG Knowledge Base Service"
            echo -e "   Active: ${RED}inactive${NC}"
            echo ""
            echo -e "${CYAN}Recent logs:${NC}"
            journalctl -u "${SERVICE_NAME}.service" --no-pager -n 10
        fi
        ;;

    start)
        check_root
        echo -e "${GREEN}Starting ${SERVICE_NAME}...${NC}"
        systemctl start "${SERVICE_NAME}.service"
        sleep 1
        if systemctl is-active --quiet "${SERVICE_NAME}.service"; then
            echo -e "${GREEN}Service started successfully${NC}"
        else
            echo -e "${RED}Service failed to start${NC}"
            exit 1
        fi
        ;;

    stop)
        check_root
        echo -e "${YELLOW}Stopping ${SERVICE_NAME}...${NC}"
        systemctl stop "${SERVICE_NAME}.service"
        sleep 1
        if ! systemctl is-active --quiet "${SERVICE_NAME}.service"; then
            echo -e "${GREEN}Service stopped${NC}"
        else
            echo -e "${RED}Service still running${NC}"
            exit 1
        fi
        ;;

    restart)
        check_root
        echo -e "${GREEN}Restarting ${SERVICE_NAME}...${NC}"
        systemctl restart "${SERVICE_NAME}.service"
        sleep 2
        if systemctl is-active --quiet "${SERVICE_NAME}.service"; then
            echo -e "${GREEN}Service restarted successfully${NC}"
        else
            echo -e "${RED}Service failed to restart${NC}"
            exit 1
        fi
        ;;

    logs)
        shift
        journalctl -u "${SERVICE_NAME}.service" --no-pager -f "$@"
        ;;

    *)
        echo "Usage: $0 {status|start|stop|restart|logs}"
        echo ""
        echo "Commands:"
        echo "  status   Show service status and recent logs"
        echo "  start    Start the service"
        echo "  stop     Stop the service"
        echo "  restart  Restart the service"
        echo "  logs     Follow service logs (Ctrl+C to exit)"
        echo ""
        echo "Most commands require sudo for privileged actions."
        exit 1
        ;;
esac
