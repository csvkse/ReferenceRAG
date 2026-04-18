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

### 3. 配置文件

编辑 `reference-rag.json` 配置文件，添加源文件夹：

```json
{
  "sources": [
    {
      "name": "我的笔记",
      "path": "C:\\Users\\YourName\\Documents\\Notes",
      "type": "obsidian"
    }
  ]
}
```

### 4. 启动服务

### 5. 下载向量模型（可选）

```bash
# Windows
scripts\download-model.bat

# Linux/macOS
chmod +x scripts/download-model.sh
./scripts/download-model.sh
```

> 如果不下载模型，系统会自动使用模拟模式进行测试。

### 6. GPU 加速配置（可选）

要启用 GPU 加速，需要安装 **CUDA 12.x**：

1. **下载 CUDA Toolkit 12.x**
   
   下载地址：https://developer.nvidia.com/cuda-12-6-0-download-archive

2. **安装后重启电脑**

3. **验证安装**：
   ```bash
   nvcc --version
   nvidia-smi
   ```

4. **启用 GPU**：编辑配置文件设置 `embedding.useCuda: true`

### 7. 索引文档

通过 API 触发索引：

```bash
# 触发所有源索引
curl -X POST http://localhost:5000/api/index/all

# 强制重新索引
curl -X POST http://localhost:5000/api/index/reindex
```

### 8. 启动服务

```bash
dotnet run --project src/ReferenceRAG.Service
```

服务将在 `http://localhost:5000` 启动。

### 9. 测试查询

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
      "filePath": "notes/obsidian-config.md",
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

详细文档位于 [.planning/](.planning/) 目录：

- [.planning/codebase/](.planning/codebase/) - 代码库分析
- [.planning/intel/](.planning/intel/) - 情报文件
- [.planning/research/](.planning/research/) - 研究资料

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

2. 可用 MCP 工具：

| 工具 | 功能 |
|------|------|
| `sources-list` | 列出所有数据源 |
| `sources-add` | 添加新数据源 |
| `query-knowledge` | 知识库查询 |
| `search-files` | 文件内容搜索 |
| `run-script` | 执行自定义脚本 |

详见 MCP 工具源码：`src/McpTools/`

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
│   │   └── McpTools/              # MCP 工具集（Claude Code 集成）
│   ├── ReferenceRAG.Core/         # 核心业务逻辑
│   │   ├── Services/             # 服务层（SearchService, EmbeddingService 等）
│   │   ├── Models/               # 领域模型
│   │   ├── Abstractions/          # 接口抽象
│   │   └── Indexing/             # 索引处理
│   └── ReferenceRAG.Storage/      # 存储层（SQLite + 向量存储）
├── tests/
│   └── ReferenceRAG.Tests/        # 单元测试
├── .planning/                    # GSD 规划目录
├── scripts/                      # 脚本（模型下载等）
├── Dockerfile
├── docker-compose.yml
└── README.md
```

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
