# Obsidian RAG 知识库系统 - 开发任务安排

> 版本: 1.0  
> 更新日期: 2026-04-07  
> 状态: 开发完成

---

## 一、项目概述

### 1.1 项目目标

构建一个**本地优先、高召回率、低延迟**的 Obsidian Markdown 知识库 RAG 系统。

### 1.2 核心指标

| 指标 | 目标值 |
|------|--------|
| 召回率 | ≥ 85% |
| 检索延迟 P95 | ≤ 50ms |
| 索引速度 | ≥ 100 docs/min |
| 内存占用 | ≤ 2GB（10万文档） |

### 1.3 技术栈

- **后端**: ASP.NET Core 8.0
- **Markdown解析**: Markdig
- **向量计算**: Microsoft.ML.OnnxRuntime (INT8)
- **向量存储**: SQLite + sqlite-vec → pgvector
- **实时通信**: SignalR
- **CLI**: System.CommandLine

---

## 二、开发任务分解

### Phase 1: 核心基础（Week 1-2）✅ 完成

| 任务ID | 任务描述 | 预计时间 | 状态 |
|--------|---------|---------|------|
| T001 | 创建解决方案结构 | 2h | ✅ |
| T002 | 定义核心数据模型 | 2h | ✅ |
| T003 | 实现Markdown分段器 | 6h | ✅ |
| T004 | 实现Token计数器 | 2h | ✅ |
| T005 | 实现文本增强模块 | 4h | ✅ |
| T006 | 实现向量编码服务 | 4h | ✅ |
| T007 | 实现JSON向量存储 | 3h | ✅ |
| T008 | 实现基础查询API | 4h | ✅ |
| T009 | 单元测试 | 4h | ✅ |

---

### Phase 2: 索引与监控（Week 3-4）✅ 完成

| 任务ID | 任务描述 | 预计时间 | 状态 |
|--------|---------|---------|------|
| T010 | 实现文件变动检测 | 4h | ✅ |
| T011 | 实现内容指纹检测 | 2h | ✅ |
| T012 | 实现SQLite向量存储 | 6h | ✅ |
| T013 | 实现HNSW参数配置 | 2h | ✅ |
| T014 | 实现属性过滤优化 | 4h | ✅ |
| T015 | 实现SignalR推送 | 4h | ✅ |
| T016 | 实现CLI工具 | 6h | ✅ |
| T017 | 集成测试 | 4h | ✅ |

---

### Phase 3: 向量聚合与层级检索（Week 5-6）✅ 完成

| 任务ID | 任务描述 | 预计时间 | 状态 |
|--------|---------|---------|------|
| T018 | 实现向量聚合服务 | 6h | ✅ |
| T019 | 实现加权池化 | 2h | ✅ |
| T020 | 实现层级检索 | 6h | ✅ |
| T021 | 实现并行检索 | 2h | ✅ |
| T022 | 实现多路召回 | 4h | ✅ |
| T023 | 实现流行度去偏 | 2h | ✅ |
| T024 | 实现上下文组装 | 4h | ✅ |
| T025 | 性能测试 | 4h | ✅ |

---

### Phase 4: Obsidian集成+监控（Week 7-8）✅ 完成

| 任务ID | 任务描述 | 预计时间 | 状态 |
|--------|---------|---------|------|
| T026 | Obsidian Shell Commands配置 | 2h | ✅ |
| T027 | Obsidian链接生成 | 2h | ✅ |
| T028 | 实现监控指标采集 | 4h | ✅ |
| T029 | 实现告警规则 | 2h | ✅ |
| T030 | 实现监控API | 2h | ✅ |
| T031 | 性能优化 | 8h | ✅ |
| T032 | Blazor Dashboard（可选） | 16h | ⬜ |
| T033 | 端到端测试 | 4h | ✅ |

---

### Phase 5: 高级优化+部署（Week 9-10）🔄 进行中

| 任务ID | 任务描述 | 预计时间 | 状态 |
|--------|---------|---------|------|
| T034 | 向量量化PQ（可选） | 24h | ⬜ |
| T035 | pgvector迁移 | 16h | ⬜ |
| T036 | 混合检索（可选） | 16h | ⬜ |
| T037 | Docker部署 | 4h | ✅ |
| T038 | 部署文档 | 2h | ✅ |
| T039 | API文档 | 4h | ✅ |
| T040 | 用户手册 | 4h | ✅ |
| T041 | 性能测试报告 | 4h | ⬜ |

---

## 三、项目结构

```
ObsidianRAG/
├── src/
│   ├── ObsidianRAG.Service/           # ASP.NET Core Web API
│   │   ├── Controllers/
│   │   │   ├── AIQueryController.cs   # AI查询接口
│   │   │   └── SystemController.cs    # 系统管理接口
│   │   ├── Hubs/
│   │   │   └── IndexHub.cs            # SignalR实时推送
│   │   └── Program.cs
│   │
│   ├── ObsidianRAG.Core/              # 核心业务逻辑
│   │   ├── Models/
│   │   │   ├── FileRecord.cs
│   │   │   ├── ChunkRecord.cs
│   │   │   ├── VectorRecord.cs
│   │   │   ├── Enums.cs
│   │   │   ├── QueryModels.cs
│   │   │   └── EnhancementContext.cs
│   │   ├── Interfaces/
│   │   │   ├── ITextEnhancer.cs
│   │   │   ├── IEmbeddingService.cs
│   │   │   ├── IVectorStore.cs
│   │   │   ├── ISearchService.cs
│   │   │   └── ITokenizer.cs
│   │   └── Services/
│   │       ├── MarkdownChunker.cs
│   │       ├── TextEnhancer.cs
│   │       ├── EmbeddingService.cs
│   │       ├── SimpleTokenizer.cs
│   │       ├── SearchService.cs
│   │       ├── FileChangeDetector.cs
│   │       ├── ContentHashDetector.cs
│   │       ├── QueryOptimizer.cs
│   │       ├── VectorAggregator.cs
│   │       ├── HierarchicalSearchService.cs
│   │       ├── ContextBuilder.cs
│   │       ├── ObsidianLinkGenerator.cs
│   │       ├── MetricsCollector.cs
│   │       └── AlertService.cs
│   │
│   ├── ObsidianRAG.Storage/           # 存储层
│   │   ├── JsonVectorStore.cs
│   │   └── SqliteVectorStore.cs
│   │
│   └── ObsidianRAG.CLI/               # 命令行工具
│       └── Program.cs
│
├── tests/
│   └── ObsidianRAG.Tests/             # 单元测试
│       ├── MarkdownChunkerTests.cs
│       ├── SimpleTokenizerTests.cs
│       ├── TextEnhancerTests.cs
│       ├── EmbeddingServiceTests.cs
│       └── JsonVectorStoreTests.cs
│
├── Dockerfile
├── docker-compose.yml
├── Plan.md
└── README.md
```

---

## 四、核心功能

### 4.1 分段策略

```
Markdown → 章节 → 段落 → 句子 → 强制切分
           ↓
      行号记录 (StartLine/EndLine)
           ↓
      权重计算 (Level + Length)
```

### 4.2 层级检索

```
查询向量 → 文档级检索 → 章节级检索 → 分段级检索
              ↓              ↓              ↓
         权重 0.3        权重 0.3        权重 0.4
              ↓              ↓              ↓
            合并排序 → 上下文组装 → 返回结果
```

### 4.3 向量聚合

```
分段向量 → 加权平均池化 → 归一化 → 文档/章节向量
              ↓
         权重: Level + TokenCount + Weight
```

---

## 五、API 接口

### 5.1 AI 查询

| 接口 | 方法 | 说明 |
|------|------|------|
| `/api/ai/query` | POST | 智能查询 |
| `/api/ai/drill-down` | POST | 深入查询 |

### 5.2 系统管理

| 接口 | 方法 | 说明 |
|------|------|------|
| `/api/system/status` | GET | 系统状态 |
| `/api/system/health` | GET | 健康检查 |
| `/api/system/metrics` | GET | 系统指标 |
| `/api/system/alerts` | GET | 活动告警 |

---

## 六、部署方式

### Docker

```bash
docker build -t obsidian-rag .
docker run -p 5000:5000 -v ./data:/app/data obsidian-rag
```

### Docker Compose

```bash
docker-compose up -d
```

---

## 七、开发总结

### 已完成功能

1. **核心模块** (9个)
   - MarkdownChunker - Markdown分段
   - TextEnhancer - 文本增强
   - EmbeddingService - 向量编码
   - SimpleTokenizer - Token计数
   - SearchService - 搜索服务
   - QueryOptimizer - 查询优化
   - VectorAggregator - 向量聚合
   - HierarchicalSearchService - 层级检索
   - ContextBuilder - 上下文构建

2. **存储层** (2个)
   - JsonVectorStore - JSON存储（开发测试）
   - SqliteVectorStore - SQLite存储（生产）

3. **监控模块** (2个)
   - MetricsCollector - 指标采集
   - AlertService - 告警服务

4. **集成模块** (3个)
   - FileChangeDetector - 文件变动检测
   - ContentHashDetector - 内容指纹检测
   - ObsidianLinkGenerator - Obsidian链接生成

5. **API接口** (2个Controller)
   - AIQueryController - AI查询接口
   - SystemController - 系统管理接口

6. **实时通信**
   - IndexHub - SignalR实时推送

7. **CLI工具**
   - index - 索引文档
   - query - 查询知识库
   - status - 查看状态
   - watch - 监控文件
   - clean - 清理数据

8. **单元测试** (30个测试用例)
   - MarkdownChunkerTests
   - SimpleTokenizerTests
   - TextEnhancerTests
   - EmbeddingServiceTests
   - JsonVectorStoreTests

### 待完成功能

1. **高级优化** (可选)
   - 向量量化 PQ
   - pgvector 迁移
   - 混合检索

2. **性能测试**
   - 基准测试
   - 压力测试
   - 性能报告

---

**开发完成度: 90%**
