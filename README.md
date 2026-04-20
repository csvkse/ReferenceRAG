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

---

> **模型下载依赖**：
> ```bash
> pip install torch transformers optimum onnx numpy
> ```

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

### 下载 Release（无需编译）

直接下载 Release 程序包，双击即可运行：

- **Windows**: 下载 `ReferenceRAG-win-x64.zip`，解压后运行 `ReferenceRAG.Service.exe`

Release 下载地址：[GitHub Releases](https://github.com/csvkse/ReferenceRAG/releases)

### 编译运行

```bash
# 克隆并运行
git clone https://github.com/your-repo/ReferenceRAG.git
cd ReferenceRAG
dotnet run --project src/ReferenceRAG.Service
```

服务启动后访问 `http://localhost:7897`

### 快速查询

```bash
# 查询知识库
curl -X POST http://localhost:7897/api/ai/query \
  -H "Content-Type: application/json" \
  -d '{"query": "如何在 C# 中使用异步编程？"}'

# 限定笔记源查询
curl -X POST http://localhost:7897/api/ai/query \
  -H "Content-Type: application/json" \
  -d '{"query": "如何在 C# 中使用异步编程？", "sources": ["我的笔记"]}'
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

## 🐳 快速部署

### Docker

```bash
docker run -d \
  --name reference-rag \
  -p 7897:7897 \
  -v ./data:/app/data \
  -v ./models:/app/models \
  -v /path/to/notes:/app/vault:ro \
  reference-rag
```

### 环境变量

| 变量 | 说明 | 默认值 |
|------|------|--------|
| `ASPNETCORE_URLS` | 监听地址 | `http://localhost:7897` |
| `DataPath` | 数据存储路径 | `data` |
| `ModelPath` | ONNX 模型路径 | `models/` |

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

## 📄 许可证

MIT License - 详见 [LICENSE](LICENSE) 文件
