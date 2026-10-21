# ReferenceRAG Docker 容器化部署指南

本目录包含 ReferenceRAG 针对不同硬件计算平台打造的多架构 Docker 镜像构建与部署文件。

---

## 一、支持的镜像变体 (Image Variants)

官方镜像发布于 GitHub Container Registry (`ghcr.io`)：

| 变体名称 | 镜像 Tag 标签 | 适用硬件与环境 | 依赖与驱动 |
| :--- | :--- | :--- | :--- |
| **通用 CPU 版**（默认） | `ghcr.io/csvkse/referencerag:latest`<br/>`ghcr.io/csvkse/referencerag:cpu` | 纯 CPU / 云服务器 / 轻量部署 | 无特殊驱动依赖，内置 OpenMP 与 Vulkan 运行时 |
| **NVIDIA GPU 满血版** | `ghcr.io/csvkse/referencerag:cuda`<br/>`ghcr.io/csvkse/referencerag:nvidia` | NVIDIA GeForce / RTX / Tesla 独显 | 宿主机需安装 [NVIDIA Container Toolkit](https://docs.nvidia.com/datacenter/cloud-native/container-toolkit/latest/install-guide.html) |
| **AMD ROCm GPU 版** | `ghcr.io/csvkse/referencerag:rocm`<br/>`ghcr.io/csvkse/referencerag:amd` | AMD Radeon 独显 / Ryzen APU 核显 | 依赖 Linux KFD 与 DRI 渲染设备（`/dev/kfd`, `/dev/dri`） |
| **Intel GPU / 核显版** | `ghcr.io/csvkse/referencerag:intel`<br/>`ghcr.io/csvkse/referencerag:openvino` | Intel Arc 独显 / 酷睿 Iris Xe & UHD 核显 | 依赖 Linux DRI 渲染设备（`/dev/dri`），内置 Level Zero 驱动 |

---

## 二、使用 `docker run` 快速启动

> **提示**：运行前请确保当前目录下存在 `data`、`models`、`logs` 挂载目录（或使用自定义绝对路径）。
> 如果挂载目录不存在，Docker 会自动以 root 权限创建，建议提前创建：
> ```bash
> mkdir -p data models logs
> ```

### 1. 通用 CPU 容器启动（默认推荐）
```bash
docker run -d \
  --name reference-rag-cpu \
  -p 5123:5000 \
  -v $(pwd)/data:/app/data \
  -v $(pwd)/models:/app/models \
  -v $(pwd)/logs:/app/logs \
  --restart unless-stopped \
  ghcr.io/csvkse/referencerag:latest
```

### 2. NVIDIA GPU 容器启动（CUDA 12.8 原生硬件加速）
```bash
docker run -d \
  --name reference-rag-cuda \
  --gpus all \
  -p 5123:5000 \
  -v $(pwd)/data:/app/data \
  -v $(pwd)/models:/app/models \
  -v $(pwd)/logs:/app/logs \
  --restart unless-stopped \
  ghcr.io/csvkse/referencerag:cuda
```

### 3. AMD ROCm GPU 容器启动（HIP / ROCm 硬件加速）
```bash
docker run -d \
  --name reference-rag-rocm \
  --device=/dev/kfd \
  --device=/dev/dri \
  --group-add video \
  -p 5123:5000 \
  -v $(pwd)/data:/app/data \
  -v $(pwd)/models:/app/models \
  -v $(pwd)/logs:/app/logs \
  --restart unless-stopped \
  ghcr.io/csvkse/referencerag:rocm
```

### 4. Intel GPU 容器启动（Arc 独显 / Iris Xe 核显）
```bash
docker run -d \
  --name reference-rag-intel \
  --device=/dev/dri \
  -p 5123:5000 \
  -v $(pwd)/data:/app/data \
  -v $(pwd)/models:/app/models \
  -v $(pwd)/logs:/app/logs \
  --restart unless-stopped \
  ghcr.io/csvkse/referencerag:intel
```

---

## 三、使用 Docker Compose 快速编排

本目录已内置标准编排模板 `compose.yml`，可根据当前主机配置选择激活对应的 profile：

```bash
# 1. 启动默认 CPU 容器
docker compose up -d

# 2. 启动 NVIDIA CUDA 容器
docker compose --profile cuda up -d

# 3. 启动 AMD ROCm 容器
docker compose --profile rocm up -d

# 4. 启动 Intel GPU 容器
docker compose --profile intel up -d

# 查看运行日志
docker compose logs -f

# 停止容器
docker compose down
```

---

## 四、服务健康检查与访问

容器启动就绪后：
- **Web 控制台**：打开浏览器访问 `http://localhost:5123`
- **Swagger API 文档**：访问 `http://localhost:5123/swagger`
- **健康检查探针**：
  ```bash
  curl http://localhost:5123/api/system/health
  ```
- **查看容器内 GPU 加速状态**：
  ```bash
  curl http://localhost:5123/api/models/runtime-status
  ```

---

## 五、配置文件持久化与安全升级 (Configuration & Safe Upgrade)

### 1. 为什么无需挂载单独的 `appsettings.json` 文件？
在传统 Docker 部署中，如果尝试直接挂载单文件（如 `-v /host/path/appsettings.json:/app/appsettings.json`），一旦宿主机该文件尚未创建，Docker 会自动在宿主机创建名为 `appsettings.json` 的**空目录**并挂载进容器，导致 .NET 启动读取 JSON 失败或崩溃。

### 2. 自动持久化与平滑升级机制 (`data/appsettings.json`)
ReferenceRAG 针对容器化环境进行了自适应路径优化：
- **默认定位**：在容器环境下，运行时主配置文件的默认位置自动重定向至 `/app/data/appsettings.json`。
- **冷启动自愈释放**：宿主机只需挂载数据目录 `-v /host/path/data:/app/data`。首次冷启动时，`ConfigBootstrap` 发现数据卷中尚未存在配置文件，会自动从镜像内置模板复制生成标准的 `appsettings.json` 到挂载卷中。
- **Web 端修改即时落盘**：在前端设置页修改 API Key、知识库源目录、模型路径或端口偏好后，系统会自动保存至挂载卷中的 `data/appsettings.json`。
- **镜像升级零丢失**：拉取新版本镜像并重新启动容器时，挂载目录中的 `data/appsettings.json` 原样保留，所有历史配置 100% 不会丢失；若新版本包含配置结构调整，`ConfigBootstrap` 会自动执行版本迁移升级。
- **宿主机直接编辑**：如果需要离线直接调整配置，可在宿主机上直接用文本编辑器编辑 `data/appsettings.json`，重启容器即可生效。

---

## 六、挂载卷权限与用户映射机制 (Permissions & PUID / PGID)

在 Linux NAS、Unraid、群晖等环境中，宿主机挂载的目录通常由 `root` 或 `nobody:users`（UID 99, GID 100）拥有。ReferenceRAG 镜像内置了智能权限适配机制：

1. **自动权限自愈**：
   容器启动时，内置 Entrypoint 会自动检测 `/app/data` 与 `/app/logs` 是否具有写入权限。如果权限缺失，会自动将其属主调整为容器应用用户，无需手动在宿主机繁琐执行 `chown`。
2. **支持自定义用户/组映射 (PUID / PGID)**：
   可通过环境变量自定义应用运行时的 UID 与 GID：
   - **Unraid 默认用户体系**：
     ```bash
     -e PUID=99 -e PGID=100
     ```
   - **直接以 root 运行（跳过权限转换）**：
     ```bash
     -e PUID=0 -e PGID=0
     ```
3. **显卡设备组权限动态匹配 (`/dev/dri`)**：
   挂载 `--device=/dev/dri` 时，Entrypoint 会自动探测宿主机 GPU 渲染设备属组 GID 并动态加入用户组，无需手动在 Docker 命令中拼装 `--group-add`。
