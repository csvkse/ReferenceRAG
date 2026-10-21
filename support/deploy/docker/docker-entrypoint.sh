#!/bin/bash
set -e

# Default user and group IDs to 1000 (appuser)
PUID=${PUID:-1000}
PGID=${PGID:-1000}

if [ "$(id -u)" = "0" ]; then
    # If explicitly requested to run as root
    if [ "$PUID" = "0" ] || [ "$RUN_AS_ROOT" = "true" ]; then
        mkdir -p /app/data /app/models /app/logs
        exec "$@"
    fi

    # Adjust appgroup GID if requested
    EXISTING_GID=$(getent group appgroup | cut -d: -f3 || true)
    if [ -n "$EXISTING_GID" ] && [ "$EXISTING_GID" != "$PGID" ]; then
        GROUP_COLLISION=$(getent group "$PGID" | cut -d: -f1 || true)
        if [ -n "$GROUP_COLLISION" ] && [ "$GROUP_COLLISION" != "appgroup" ]; then
            groupmod -g 9999 "$GROUP_COLLISION" 2>/dev/null || true
        fi
        groupmod -g "$PGID" appgroup 2>/dev/null || true
    fi

    # Adjust appuser UID if requested
    EXISTING_UID=$(id -u appuser 2>/dev/null || true)
    if [ -n "$EXISTING_UID" ] && [ "$EXISTING_UID" != "$PUID" ]; then
        USER_COLLISION=$(getent passwd "$PUID" | cut -d: -f1 || true)
        if [ -n "$USER_COLLISION" ] && [ "$USER_COLLISION" != "appuser" ]; then
            usermod -u 9999 "$USER_COLLISION" 2>/dev/null || true
        fi
        usermod -u "$PUID" appuser 2>/dev/null || true
    fi

    # Ensure required runtime directories exist
    mkdir -p /app/data /app/models /app/logs

    # Ensure appuser owns /app/data and /app/logs if not already writable
    for dir in /app/data /app/logs; do
        if [ -d "$dir" ] && [ ! -w "$dir" ]; then
            chown -R "$PUID:$PGID" "$dir" 2>/dev/null || true
            chmod -R u+rwX "$dir" 2>/dev/null || true
        fi
    done

    # For models directory, avoid recursive chown on large model weights; ensure base dir is writable
    if [ -d /app/models ] && [ ! -w /app/models ]; then
        chown "$PUID:$PGID" /app/models 2>/dev/null || true
        chmod u+rwX /app/models 2>/dev/null || true
    fi

    # Automatically grant DRI access if /dev/dri exists (Intel / AMD GPU)
    if [ -d /dev/dri ]; then
        for dev in /dev/dri/*; do
            if [ -e "$dev" ]; then
                DEV_GID=$(stat -c '%g' "$dev" 2>/dev/null || true)
                if [ -n "$DEV_GID" ] && [ "$DEV_GID" -ne 0 ]; then
                    DEV_GROUP=$(getent group "$DEV_GID" | cut -d: -f1 || true)
                    if [ -z "$DEV_GROUP" ]; then
                        DEV_GROUP="host_dri_${DEV_GID}"
                        groupadd -g "$DEV_GID" "$DEV_GROUP" 2>/dev/null || true
                    fi
                    usermod -a -G "$DEV_GROUP" appuser 2>/dev/null || true
                fi
            fi
        done
    fi

    # Drop privileges using gosu
    if command -v gosu >/dev/null 2>&1; then
        exec gosu "$PUID:$PGID" "$@"
    else
        exec su -s /bin/bash appuser -c "$*"
    fi
else
    # Non-root execution (e.g. docker run --user or Kubernetes securityContext)
    exec "$@"
fi
