# ReferenceRAG

> 本地优先、高召回率、低延迟的知识库 RAG 系统 - **支持多源文件夹，不限于 Obsidian**

基于 Semantic Kernel + SQLite 向量存储构建，集成 MCP 工具集支持 Claude Code。

## ✨ 特性

- 🚀 **高性能**: 检索延迟 P95 ≤ 50ms
- 🎯 **高召回**: 召回率 ≥ 85%
- 📁 **多源支持**: 同时索引多个文件夹，不限 Obsidian
- 📊 **层级检索**: 文档 → 章节 → 分段三级检索
- 🔄 **实时监控**: 内置指标采集和告警
- 🔗 **链接生成**: 自动生成 `[[file#L10-L20]]` 格式链接
- 🤖 **MCP 集成**: Claude Code 工具集，直接在 AI 对话中查询知识库
- 🐳 **容器化**: Docker 一键部署

## 🏗️ 架构

```
┌─────────────────────────────────────────────────────────────┐
│                     Data Sources                              │
│     (Obsidian Vault / Markdown / Code Docs / Custom)         │
└──────────────────────────┬──────────────────────────────────┘
                           │ FileSystemWatcher
                           ▼
┌─────────────────────────────────────────────────────────────┐
│                    ReferenceRAG.Service                       │
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────┐         │
│  │ AI Query API│  │ Index Hub   │  │ System API  │         │
│  └──────┬──────┘  └──────┬──────┘  └──────┬──────┘         │
│         └────────────────┼────────────────┘                 │
│                          ▼                                   │
│  ┌─────────────────────────────────────────────────────┐    │
│  │               McpTools (MCP 工具集)                  │    │
│  │   SourceManager / QueryTools / ScriptSystem           │    │
│  └─────────────────────────────────────────────────────┘    │
└─────────────────────────┬───────────────────────────────────┘
                          │
                          ▼
┌─────────────────────────────────────────────────────────────┐
│                     ReferenceRAG.Core                         │
│  ┌───────────────┐  ┌───────────────┐  ┌───────────────┐   │
│  │MarkdownChunker│  │EmbeddingService│  │ SearchService │   │
│  └───────────────┘  └───────────────┘  └───────────────┘   │
│  ┌───────────────┐  ┌───────────────┐  ┌───────────────┐   │
│  │VectorAggregator│  │ QueryOptimizer│  │MetricsCollector│   │
│  └───────────────┘  └───────────────┘  └───────────────┘   │
└─────────────────────────────────────────────────────────────┘
                          │
                          ▼
┌─────────────────────────────────────────────────────────────┐
│                    ReferenceRAG.Storage                       │
│  ┌───────────────┐  ┌───────────────┐                      │
│  │JsonVectorStore│  │SqliteVectorStore│                    │
│  └───────────────┘  └───────────────┘                      │
└─────────────────────────────────────────────────────────────┘
```

## 🚀 快速开始

### 1. 克隆项目

```bash
git clone https://github.com/your-repo/ReferenceRAG.git
cd ReferenceRAG
```

### 2. 安装依赖

```bash
dotnet restore
```

### 3. 初始化配置

```bash
dotnet run --project src/ReferenceRAG.CLI -- config init
```

### 4. 添加源文件夹（支持多个）

```bash
# 添加 Obsidian 笔记库
dotnet run --project src/ReferenceRAG.CLI -- source add /path/to/obsidian/vault --name "我的笔记" --type obsidian

# 添加普通 Markdown 文件夹
dotnet run --project src/ReferenceRAG.CLI -- source add /path/to/documents --name "文档库" --type markdown

# 添加代码文档
dotnet run --project src/ReferenceRAG.CLI -- source add /path/to/project/docs --name "项目文档" --type code

# 查看所有源
dotnet run --project src/ReferenceRAG.CLI -- source list
```

**Windows 示例:**
```powershell
dotnet run --project src/ReferenceRAG.CLI -- source add "C:\Users\YourName\Documents\Notes" --name "笔记"
```

### 5. GPU 加速配置（可选）

要启用 GPU 加速，需要安装 **CUDA 12.x**：

1. **下载 CUDA Toolkit 12.x**
   
   下载地址：https://developer.nvidia.com/cuda-12-6-0-download-archive
   
   选择：
   - Operating System: Windows
   - Architecture: x86_64
   - Version: 10/11
   - Installer: exe (local)

2. **安装后重启电脑**

3. **验证安装**：
   ```bash
   nvcc --version
   nvidia-smi
   ```

4. **启用 GPU**：
   
   编辑 `reference-rag.json` 或 `src/ReferenceRAG.Service/reference-rag.json`：
   ```json
   {
     "embedding": {
       "useCuda": true,
       "cudaDeviceId": 0
     }
   }
   ```

**性能对比**：
| 模式 | 速度 |
|------|------|
| CPU | ~4,000 字/秒 |
| GPU (CUDA) | ~50,000-100,000 字/秒 |

**推荐 BatchSize 配置**：

| 模型 | 推荐 BatchSize | GPU 占用 | 说明 |
|------|---------------|---------|------|
| bge-small-zh-v1.5 | 2 | ~80% | 最佳性能，避免资源争用 |
| bge-small-zh-v1.5 | 1 | ~40-50% | 低负载场景 |

> **注意**：BatchSize 过大反而会降低性能，因为 GPU 资源争用和显存带宽瓶颈。对于小模型，小批次往往更高效。

> **注意**：CUDA 13.x 可能存在兼容性问题，推荐使用 CUDA 12.x。

### 6. 索引文档

```bash
# 索引所有源
dotnet run --project src/ReferenceRAG.CLI -- index

# 索引指定源
dotnet run --project src/ReferenceRAG.CLI -- index --source "我的笔记"

# 强制重新索引
dotnet run --project src/ReferenceRAG.CLI -- index --force
```

### 7. 启动服务

```bash
dotnet run --project src/ReferenceRAG.Service
```

服务将在 `http://localhost:5000` 启动。

### 8. 测试查询

```bash
# 查询所有源
curl -X POST http://localhost:5000/api/ai/query \
  -H "Content-Type: application/json" \
  -d '{"query": "如何配置？", "mode": "standard"}'

# 限定源查询
curl -X POST http://localhost:5000/api/ai/query \
  -H "Content-Type: application/json" \
  -d '{"query": "关键词", "sources": ["我的笔记"]}'
```

## 📖 API 文档

### AI 查询接口

```http
POST /api/ai/query
Content-Type: application/json

{
  "query": "如何配置 Obsidian？",
  "mode": "standard",
  "topK": 10
}
```

**查询模式**:
- `quick`: 快速模式，~1000 tokens，3 个结果
- `standard`: 标准模式，~3000 tokens，10 个结果（默认）
- `deep`: 深度模式，~6000 tokens，20 个结果

**响应示例**:

```json
{
  "query": "如何配置 Obsidian？",
  "mode": "standard",
  "context": "...",
  "chunks": [
    {
      "refId": "@1",
      "filePath": "docs/obsidian-config.md",
      "content": "...",
      "score": 0.89,
      "obsidianLink": "[[obsidian-config#L45-L52]]"
    }
  ],
  "stats": {
    "totalMatches": 10,
    "durationMs": 23,
    "estimatedTokens": 2847
  }
}
```

### 深入查询

```http
POST /api/ai/drill-down
Content-Type: application/json

{
  "query": "如何配置 Obsidian？",
  "refIds": ["@1", "@2"],
  "expandContext": 2
}
```

### 系统状态

```http
GET /api/system/status
```

### 健康检查

```http
GET /api/system/health
```

## 🛠️ CLI 命令

### 源管理

```bash
# 列出所有源
reference-rag source list

# 添加源
reference-rag source add /path/to/folder --name "名称" --type obsidian|markdown|code|custom

# 移除源
reference-rag source remove "名称"

# 启用/禁用源
reference-rag source enable "名称"
reference-rag source disable "名称"
```

### 索引

```bash
# 索引所有源
reference-rag index

# 索引指定源
reference-rag index --source "我的笔记"

# 强制重新索引
reference-rag index --force

# 详细输出
reference-rag index --verbose
```

### 查询

```bash
# 标准查询
reference-rag query "搜索关键词"

# 限定源查询
reference-rag query "关键词" --source "我的笔记" --source "文档库"

# 深度模式
reference-rag query "详细内容" --mode deep --top-k 20
```

### 监控

```bash
# 监控所有源
reference-rag watch

# 监控指定源
reference-rag watch --source "我的笔记"
```

### 其他

```bash
# 查看状态
reference-rag status

# 查看配置
reference-rag config show

# 清理索引
reference-rag clean --confirm
```

## 🐳 Docker 部署

```bash
# 构建镜像
docker build -t reference-rag .

# 运行容器
docker run -d \
  -p 5000:5000 \
  -v ./data:/app/data \
  -v ./models:/app/models \
  -v /path/to/vault:/app/vault:ro \
  reference-rag

# 或使用 docker-compose
docker-compose up -d
```

## 🔧 配置

### 环境变量

| 变量 | 说明 | 默认值 |
|------|------|--------|
| `ASPNETCORE_ENVIRONMENT` | 运行环境 | `Production` |
| `DataPath` | 数据存储路径 | `data` |
| `ModelPath` | ONNX 模型路径 | `models/bge-small-zh-v1.5.onnx` |

### appsettings.json

```json
{
  "DataPath": "data",
  "ModelPath": "models/bge-small-zh-v1.5.onnx",
  "Chunking": {
    "MaxTokens": 512,
    "MinTokens": 50
  },
  "Search": {
    "DefaultTopK": 10,
    "ContextWindow": 1
  }
}
```

## 📖 文档

- [多源文件夹配置](docs/MULTI_SOURCE.md) - 多文件夹支持详解
- [模型配置](docs/MODEL_CONFIG.md) - 向量模型配置指南
- [Vault 配置](docs/VAULT_CONFIG.md) - 单源配置参考

## 📊 监控指标

### 系统指标

- CPU 使用率
- 内存使用量
- 线程数
- 运行时间

### 索引指标

- 总文件数
- 总分段数
- 总 token 数
- 索引时间范围

### 查询指标

- 查询总数
- 平均延迟
- P95/P99 延迟
- 平均结果数

### 告警规则

| 规则 | 条件 | 严重程度 |
|------|------|----------|
| HighQueryLatency | P95 延迟 > 100ms | Warning |
| HighMemoryUsage | 内存 > 2GB | Warning |
| LowRecall | 平均结果 < 3 | Info |

## 🔗 MCP 集成

### Claude Code 配置

在 Claude Code 中启用知识库查询：

1. 配置 MCP 服务器（项目已包含 `McpTools`）

2. 使用 MCP 工具查询知识库：

```bash
# 列出所有源
mmx query-tools sources-list

# 查询知识库
mmx query-tools query-knowledge --query "如何配置？" --mode "standard"

# 搜索文件内容
mmx query-tools search-files --query "关键词" --sources "笔记"
```

### Obsidian 自动链接

系统自动生成 Obsidian 兼容链接：

```
[[filename#L10-L20]]
[[filename#heading]]
[[filename#^block-id]]
```

## 🧪 测试

```bash
# 运行所有测试
dotnet test

# 运行特定测试
dotnet test --filter "FullyQualifiedName~MarkdownChunkerTests"
```

## 📁 项目结构

```
ReferenceRAG/
├── src/
│   ├── ReferenceRAG.Service/      # ASP.NET Core Web API 服务
│   ├── ReferenceRAG.Core/         # 核心业务逻辑
│   │   ├── Services/             # 服务层（SearchService, EmbeddingService 等）
│   │   ├── Models/               # 领域模型
│   │   ├── Abstractions/          # 接口抽象
│   │   └── Indexing/             # 索引处理
│   ├── ReferenceRAG.Storage/      # 存储层（SQLite + 向量存储）
│   ├── ReferenceRAG.CLI/          # 命令行工具
│   └── McpTools/                  # MCP 工具集（Claude Code 集成）
│       ├── SourceManager/         # 源管理工具
│       ├── QueryTools/            # 查询工具
│       └── ScriptSystem/          # 脚本系统
├── tests/
│   └── ReferenceRAG.Tests/        # 单元测试
├── docs/                         # 文档
├── .planning/                    # GSD 规划目录
├── scripts/                      # 脚本（模型下载等）
├── Dockerfile
├── docker-compose.yml
└── README.md
```

### MCP 工具集

系统提供 MCP (Model Context Protocol) 工具集，支持 Claude Code 集成：

| 工具 | 功能 |
|------|------|
| `sources-list` | 列出所有数据源 |
| `sources-add` | 添加新数据源 |
| `query-knowledge` | 知识库查询 |
| `search-files` | 文件内容搜索 |
| `run-script` | 执行自定义脚本 |

详见 [.planning/codebase/](.planning/codebase/) 目录下的分析文档。

## 📝 开发进度

详见 [.planning/ROADMAP.md](.planning/ROADMAP.md)

### 已完成 ✅

- Phase 1: 核心基础
- Phase 2: 索引与监控
- Phase 3: 向量聚合与层级检索
- Phase 4: Obsidian 集成 + 监控
- Phase 5: MCP 工具集 + 服务管理

### 进行中 🔄

- Phase 6: 高级优化 + 部署

## 🤝 贡献

欢迎提交 Issue 和 Pull Request！

## 📄 许可证

MIT License
