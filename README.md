# ReferenceRAG

ReferenceRAG 是一个面向本地知识库的 RAG 系统，目标是把本地文件夹、笔记库和代码文档接入统一的检索增强流程。

当前仓库已经包含后端服务、桌面端壳、Vue 仪表盘、存储层、核心检索逻辑、测试工程和部署脚本，整体更接近一个可运行的完整项目，而不是单纯的概念验证。

---

## 当前状态

仓库当前具备以下内容：

- `ReferenceRAG.Service`：ASP.NET Core 后端服务，提供 REST API、MCP 工具、Swagger、SignalR 和索引服务
- `ReferenceRAG.Desktop`：WPF + WebView2 桌面壳，用来启动和托管本地 Web 界面
- `dashboard-vue`：Vue 3 + Vite 前端仪表盘，作为服务的 Web UI
- `ReferenceRAG.Core`：核心检索、索引、重排、文本处理和模型管理逻辑
- `ReferenceRAG.Storage`：SQLite 向量存储、BM25 以及知识图谱相关存储实现
- `tests`：单元测试、集成测试和性能测试
- `src/ReferenceRAG.Service/scripts`：Windows 管理脚本

当前配置和代码说明：

- 默认本地服务端口由 `ReferenceRAG:Service:port` 控制，仓库内生产配置默认是 `7897`
- 桌面端已支持发布包分发，推荐直接下载发布包使用
- 桌面端默认打开本地服务地址，使用同一套服务端配置
- 仪表盘前端由后端服务托管，发布时会被打包进服务输出目录

---

## 目录结构

```text
ReferenceRAG/
├── src/
│   ├── ReferenceRAG.Core/        # 索引、检索、模型、文本处理等核心逻辑
│   ├── ReferenceRAG.Storage/     # SQLite 向量库、BM25、图谱存储
│   ├── ReferenceRAG.Service/     # Web API、MCP、SignalR、后台任务、部署脚本
│   └── ReferenceRAG.Desktop/     # WPF 桌面端壳
├── dashboard-vue/                # Vue 3 前端仪表盘
├── tests/                        # 单元测试、集成测试、性能测试
├── config/                       # 容器运行时配置
├── data/                         # 本地索引与运行时数据
├── docs/                         # 项目文档
└── skill/                        # 面向 Claude Code 的使用技能
```

---

## 使用方式

ReferenceRAG 目前支持四种常见接入方式：

| 方式 | 适用场景 | 说明 |
|------|----------|------|
| Obsidian + claudian | 在笔记软件内直接问答 | 通过本地服务接入知识库 |
| CherryStudio + MCP | 支持 MCP 的客户端 | 通过 MCP 工具调用检索能力 |
| Claude Code + Skills | 在 Claude Code 中查询 | 使用仓库内 `skill/ReferenceRAG` |
| 直接调用 API | 脚本、调试、集成测试 | 适合自动化调用 |

---

## 快速开始

### 1. 推荐方式：使用已发布的桌面端

使用步骤：

1. 打开发布页，下载 `ReferenceRAG-win-x64.zip`
2. 解压后运行 `ReferenceRAG.Desktop.exe`
3. 首次启动时，确保系统已安装 WebView2 Runtime
4. 如果你想修改默认端口或数据路径，可以编辑程序目录下的 `appsettings.json`

桌面端特点：

- 启动后会自动拉起本地服务并打开界面
- 关闭窗口不会直接退出，默认最小化到托盘
- 支持单实例运行
- 支持托盘菜单中的「打开」「退出」「开机自启动」「最小化启动」

默认访问地址通常是：

```text
http://localhost:7897
```

如果默认端口被占用，桌面端会自动回退到一个可用端口，并在启动日志中输出实际端口。

### 2. 本地开发运行

如果你需要修改代码或本地调试，再使用源码方式启动：

```bash
dotnet run --project src/ReferenceRAG.Service
dotnet run --project src/ReferenceRAG.Desktop
```

前提：

- .NET 10 SDK
- Node.js 和 npm
- SQLite 运行环境由项目自动处理

### 3. Windows 脚本管理

如果你更习惯使用脚本，可以进入服务脚本目录：

```bat
cd src/ReferenceRAG.Service/scripts
menu.bat
```

常用功能包括：

- 构建
- 安装服务
- 启动 / 停止 / 卸载服务
- 查看状态
- 以控制台方式运行
- 打开浏览器
- 查看日志

## 核心能力

### 检索

- 向量检索
- BM25 全文检索
- 混合检索
- 可选重排
- 可选上下文扩展

### 索引

- 自动后台索引
- 启动时同步
- 文件变更监控
- Markdown 分块
- 知识图谱更新

### 模型管理

- Embedding 模型管理
- Reranker 模型管理
- ONNX 推理
- CUDA 加速支持

### 服务能力

- REST API
- MCP 工具集
- Swagger 文档
- SignalR 实时通知
- Windows Service 适配

---

## 主要 API

| 接口 | 说明 |
|------|------|
| `POST /api/ai/query` | 语义查询 / 混合查询 |
| `POST /api/ai/drill-down` | 深入查询 |
| `POST /api/index/all` | 全量索引 |
| `GET /api/index/status` | 索引状态 |
| `GET /api/models` | 模型列表 |
| `POST /api/models/switch` | 切换模型 |
| `GET /api/system/health` | 健康检查 |
| `GET /api/system/status` | 系统状态 |

Swagger 在开发环境通常可直接访问：

```text
http://localhost:7897/swagger
```

---

## 配置要点

| 配置项 | 说明 |
|--------|------|
| `ReferenceRAG:Service:port` | 服务监听端口 |
| `ReferenceRAG:Service:apiKey` | 接口鉴权密钥，留空则关闭鉴权 |
| `ReferenceRAG:dataPath` | SQLite 与运行数据目录 |
| `ReferenceRAG:modelsRootPath` | 模型文件根目录 |
| `Cors:AllowedOrigins` | 允许跨域的前端地址 |

仓库内与运行相关的配置文件主要有：

- `src/ReferenceRAG.Service/appsettings.Development.Exsample.json`
- 桌面端发布目录下的 `appsettings.json`

---

## 数据与存储

默认运行时会在数据目录下维护这些内容：

- `vectors.db`：向量、BM25 和图谱相关数据
- `query_stats.db`：查询统计
- 模型文件目录
- 索引日志和运行日志

这意味着本项目不是纯无状态服务，迁移环境时要一起带上 `data/` 和 `models/`。

---

## 相关文档

- [项目介绍](docs/introduction.md)
- [索引架构](docs/index-architecture.md)
- [使用预览](PREVIEW.md)

---

## 说明

仓库当前 README 以“当前项目状态”为主，后续如果新增模块，建议同步更新：

- 启动方式
- 默认端口
- 配置文件位置
- API 列表
- 部署脚本说明
