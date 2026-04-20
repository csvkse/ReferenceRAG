# ReferenceRAG

> 本地优先、高召回率、低延迟的知识库 RAG 系统 - **支持多源文件夹，不限于 Obsidian**

基于 ASP.NET Core + ONNX Runtime + SQLite 向量存储构建，集成 MCP 工具集，支持 Claude/CherryStudio 等多种 AI 客户端。

---

## 🎯 核心痛点与解决

AI 大模型无法直接使用本地笔记库作为外挂知识库进行检索增强。

**ReferenceRAG 将本地笔记库索引为向量，支持多种检索方式和模型切换，让 AI 在对话中实时查询本地知识。**

---

## ⭐ 特色功能

| 功能 | 说明 |
|------|------|
| **限定查询** | 支持限定笔记源（Sources）和文件夹路径，精准控制检索范围 |
| **混合检索** | 向量查询 + BM25 全文检索 + Rerank 重排，多种组合方式 |
| **模型切换** | 一键切换 Embedding/Reranker 模型，支持本地 ONNX 加速 |
| **多源支持** | 同时索引 Obsidian/Markdown/代码文档等多个文件夹 |
| **自动后台同步** | 监控文件夹变化，自动后台执行向量索引，无需手动触发 |

![向量搜索](images/preview/页面-搜索1.png)

---

> **模型下载依赖**：
> 模型来源为 HuggingFace，下载模型需自行解决网络问题（如使用代理）
> ```bash
> pip install torch transformers optimum onnx numpy
> ```

> **优先推荐**：部分模型（如 `Multilingual E5 Small`）提供原生 ONNX 支持，无需手动转换，下载后可直接使用，可大幅降低配置复杂度。

---

## 🖼️ 功能预览

> 📸 完整截图预览：**[PREVIEW.md](PREVIEW.md)**

---

## 🔌 使用方式

| 方案 | 客户端 | 说明 |
|------|--------|------|
| 方案一 | Obsidian + claudian | Obsidian 内直接 RAG 问答 |
| 方案二 | CherryStudio + MCP | CherryStudio 通过 MCP 调用 |
| 方案三 | Claude Code + Skills | Claude Code 对话中查询 |
| 方案四 | 直接调用 API | curl 直接查询（无需配置） |

详细配置请查看 **[PREVIEW.md](PREVIEW.md)**

---

## 🚀 快速开始

### 环境要求

- .NET 10.0 Runtime（下载 Release 版本无需安装 SDK）
- SQLite（自动包含）
- CUDA 12.x（可选，GPU 加速）

### 下载 Release（推荐）

直接下载 Release 程序包，双击即可运行：

- **Windows**: 下载 `ReferenceRAG-win-x64.zip`，解压后运行

Release 下载地址：[GitHub Releases](https://github.com/csvkse/ReferenceRAG/releases)

### 常用配置参数

| 配置项 | 路径 | 默认值 | 说明 |
|--------|------|--------|------|
| **程序端口** | `ReferenceRAG.service.port` | `7897` | 服务监听端口 |
| **API Key** | `ReferenceRAG.service.apiKey` | 空 | 接口认证密钥，为空则不启用 |
| **向量数据目录** | `ReferenceRAG.dataPath` | - | SQLite 向量库存储路径 |
| **模型存放目录** | `ReferenceRAG.modelsRootPath` | - | Embedding/Reranker 模型文件根目录 |

### Windows 脚本管理

项目提供 Windows 批处理脚本，方便管理服务：

```batch
cd src/ReferenceRAG.Service/scripts
menu.bat
```

> **构建说明**：构建（Build）需要安装 .NET 10.0 SDK，只能通过源码编译

**menu.bat 交互式菜单**：
| 选项 | 功能 |
|------|------|
| 1 | 构建 (Build) |
| 2 | 安装服务 (Install) |
| 3 | 启动服务 (Start) |
| 4 | 停止服务 (Stop) |
| 5 | 查看状态 (Status) |
| 6 | 卸载服务 (Uninstall) |
| 7 | 控制台运行 (Run as Console) |
| 8 | 打开浏览器 |

### Docker 部署

```bash
# 构建并启动
docker-compose up -d

# 查看日志
docker-compose logs -f
```

> **配置持久化**：容器内配置文件挂载到宿主 `config/` 目录，配置修改后重启容器不丢失。
> GPU 版本使用 `Dockerfile.gpu` 构建即可。

### 编译运行

```bash
git clone https://github.com/your-repo/ReferenceRAG.git
cd ReferenceRAG
dotnet run --project src/ReferenceRAG.Service
```

服务启动后访问 `http://localhost:7897`

### 快速查询

```bash
curl -X POST http://localhost:7897/api/ai/query \
  -H "Content-Type: application/json" \
  -d '{"query": "如何在 C# 中使用异步编程？"}'
```

---

## 📖 API 概览

| 接口 | 说明 |
|------|------|
| `POST /api/ai/query` | 知识库语义查询 |
| `POST /api/ai/drill-down` | 深入查询（上下文扩展） |
| `POST /api/index/all` | 触发全量索引 |
| `GET /api/index/status` | 索引状态 |
| `GET /api/models` | 模型列表 |
| `POST /api/models/switch` | 切换模型 |

详细 API 文档请查看 **[PREVIEW.md](PREVIEW.md)**

---

## 🌐 前端仪表板

启动服务后访问 `http://localhost:7897`，支持：

- 系统概览与实时指标
- 交互式搜索与结果展示
- 数据源管理与索引控制
- 模型下载与切换
- 性能监控与告警

---

## 🔗 相关项目

| 项目 | 说明 |
|------|------|
| [claudian](https://github.com/YishenTu/claudian) | Obsidian RAG 插件 |
| [sqlite-vec](https://github.com/asg017/sqlite-vec) | SQLite 向量扩展 |
| [ONNX Runtime](https://onnxruntime.ai/) | 跨平台 ML 推理加速器 |

---

## ❓ 常见问题（FAQ）

### 模型下载/转换失败，提示 `ModuleNotFoundError: No module named 'transformers'`

这是因为 Python 环境缺少必要的依赖包。运行以下命令安装：

```bash
pip install torch transformers optimum onnx numpy
```

> 模型来源为 **HuggingFace**，如遇网络问题，请使用国内镜像：
> ```bash
> pip install torch transformers optimum onnx numpy -i https://pypi.tuna.tsinghua.edu.cn/simple
> ```

### ONNX 转换失败，提示 `cublasLt64_12.dll is missing`

这是 CUDA cuBLAS 库未加入系统 PATH 导致的。找到 `cublasLt64_12.dll` 所在目录（通常在 `C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.x\bin\`），将其添加到系统环境变量 `Path` 中，然后**重启命令行/服务**。

### 模型下载缓慢或失败

推荐使用 `huggingface_hub` 下载模型到本地，再指定本地路径加载：

```bash
pip install huggingface_hub
huggingface-cli download BAAI/bge-small-zh-v1.5 --local-dir "模型本地目录"
```

### 前端跨域（CORS）报错

确保后端 `appsettings.json` 中的 `Cors.AllowedOrigins` 包含前端地址（如 `http://localhost:3000`），重启服务后生效。

---

## 📄 许可证

MIT License - 详见 [LICENSE](LICENSE) 文件
