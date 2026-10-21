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
