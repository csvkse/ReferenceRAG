# ObsidianRAG Service Scripts

Scripts for registering ObsidianRAG as a system service with automatic startup.

## Windows

### Prerequisites
- NSSM (Non-Sucking Service Manager): https://github.com抽取/nssm/releases
- Install via Chocolatey: `choco install nssm`
- .NET SDK installed and project built

### Installation

```powershell
# Run as Administrator
.\install-service.ps1

# With custom path and user
.\install-service.ps1 -ServicePath "C:\Program Files\ObsidianRAG" -ServiceUser "DOMAIN\user"
```

### Service Control

```powershell
# Check status
.\servicectl.ps1 status

# Stop service
.\servicectl.ps1 stop

# Start service
.\servicectl.ps1 start

# Restart service
.\servicectl.ps1 restart
```

### Uninstallation

```powershell
# Run as Administrator
.\uninstall-service.ps1

# Skip confirmation
.\uninstall-service.ps1 -Force
```

## Linux

### Prerequisites
- systemd
- .NET SDK installed and project built
- Root/sudo access

### Installation

```bash
# Run as root
sudo ./install-service.sh

# With custom user and path
sudo ./install-service.sh --user obsidianrag --path /opt/obsidianrag
```

The script will:
1. Create a dedicated service user (obsidianrag) if it doesn't exist
2. Create required directories (/var/log/obsidianrag, data directories)
3. Install the systemd unit file
4. Enable and start the service

### Service Control

```bash
# Check status and logs
./servicectl.sh status

# Stop service
sudo ./servicectl.sh stop

# Start service
sudo ./servicectl.sh start

# Restart service
sudo ./servicectl.sh restart

# Follow logs
./servicectl.sh logs
```

### Uninstallation

```bash
# Remove service only
sudo ./uninstall-service.sh

# Remove service, user, logs, and data
sudo ./uninstall-service.sh --purge
```

## Configuration

### Windows Service Configuration

Edit `install-service.ps1` parameters:
- `-ServicePath`: Path where ObsidianRAG.Service.dll is located
- `-ServiceUser`: User account to run the service (default: LocalService)
- `-NSSMPath`: Custom path to nssm.exe if not in PATH

### Linux Service Configuration

Edit `obsidianrag.service` before installation:
- `ExecStart`: Path to dotnet and DLL
- `WorkingDirectory`: Service working directory
- `User`/`Group`: Service user (must not be root)
- `RestartSec`: Delay before restart after crash
- `ProtectSystem`: System hardening options

## Service Details

| Property | Windows | Linux |
|----------|---------|-------|
| Service Name | ObsidianRAG | obsidianrag |
| Display Name | ObsidianRAG Knowledge Base Service | - |
| Description | Vector search and RAG API for Obsidian notes | - |
| Default Path | Script directory | /opt/obsidianrag |
| Log Location | {path}/logs/ | /var/log/obsidianrag/ |
| Auto-restart | Yes (5s delay) | Yes (10s delay) |

## Troubleshooting

### Windows
```powershell
# Check event log
Get-EventLog -LogName Application -Source "ObsidianRAG" -Newest 20

# Check NSSM logs
# Located at: {service_path}/logs/service-error.log

# Manual service management
sc.exe query ObsidianRAG
sc.exe stop ObsidianRAG
sc.exe start ObsidianRAG
```

### Linux
```bash
# Check service status with details
sudo systemctl status obsidianrag -l

# View real-time logs
sudo journalctl -u obsidianrag -f

# View error log
cat /var/log/obsidianrag/error.log

# Check service configuration
sudo systemctl cat obsidianrag
```
