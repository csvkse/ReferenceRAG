# 重建切片功能设计与原子化切片重构落地方案
(Re-chunk Feature & Atomic In-Place Chunk Rebuilding Design)

---

## 1. 背景与用户痛点分析

在 RAG 系统的日常维护与参数调优中，切片（Chunking）规则经常发生演进（例如：由固定切分切换为自适应模式、调整目标模型窗口利用率、启用表格结构保护与表头传承等）。

在当前的系统操作中，用户在数据源管理（`Sources` 页面）常常遭遇以下困惑：
- **旧切片数据冗余滞留**：例如历史遗留的单文档产生了 913 个细碎旧切片，但无法直观地触发重新切片；
- **“增量索引”无法触发重切**：增量索引以文件内容哈希（ContentHash）作为首要判决条件，当磁盘文件内容未修改时，系统判定内容未变直接跳过，切片数依然滞留在 913；
- **“补全向量”不动分块**：其职责仅为缺失向量的分块补算嵌入，明确不改变现有分块结构；
- **“重建索引”成本高昂且语义混淆**：界面的“重建索引”字面偏向向量模型重新推理，且底层逻辑调用了 `DeleteSourceAsync`，强行清空源库数据再从头重建，存在中间数据不可用的风险。

为此，系统亟需提供清晰、安全、原子化的**【重建切片】（Rechunk）**功能。

---

## 2. 核心架构与策略模式扩展

```mermaid
flowchart TD
    subgraph 用户触发层 (UI / API)
        UI1[数据源行操作: 点击【重建切片】] --> API1[POST /index/rechunk/{sourceName}]
        UI2[全局管理卡片: 点击【重建切片（全局）】] --> API2[POST /index/rechunk]
    end

    subgraph 调度层 (IndexJobsController & IndexService)
        API1 & API2 --> Req[构建 IndexRequest: Rechunk=true, Force=true]
        Req --> Factory[IndexStrategyFactory 策略工厂]
    end

    subgraph 策略执行层 (RechunkIndexStrategy)
        Factory --> Strat[RechunkIndexStrategy 策略]
        Strat --> Scan[扫描目标数据源文件清单]
        Scan --> Prep[Phase 1: Pipeline.PrepareAsync]
    end

    subgraph 文件级原子更替层 (Atomic In-Place Replacement)
        Prep --> C1[1. 重读文本，应用当前生效的 ChunkingOptions]
        C1 --> C2[2. MarkdownChunker 自适应切分 + 表格保护]
        C2 --> C3[3. 形成新分块集合 (如从 913 缩减至 ~200)]
        C3 --> Finalize[Phase 3: Pipeline.FinalizeAsync]
        Finalize --> D1[批量 Upsert 新分块]
        Finalize --> D2[原子清退该文件的旧分块 ID 与旧向量]
        Finalize --> D3[为新分块生成高维向量 + 更新 BM25 + 更新图谱]
        Finalize --> D4[更新 FileRecord.ChunkCount]
    end

    subgraph 成果反馈
        D4 --> UIUpdate[前端数据源列表自动刷新<br/>分块数从 913 瞬间更新为新分块数]
    end
```

---

## 3. 详细设计与实现规约

### 3.1 策略模式扩展（`RechunkIndexStrategy`）
- 命名空间：`ReferenceRAG.Business.Features.Indexing.Strategies`
- 契约接口：`IIndexExecutionStrategy`
- 激活条件：`CanHandle(IndexRequest request) => request.Rechunk;`
- 执行特点：
  - 强制 `request.Force = true;`，确保不受 `ContentHash` 跳过限制；
  - 采用文件级流水线原子替换机制，**绝对不提前调用 `DeleteSourceAsync`**，杜绝删库空白期；
  - 遇到异常时具备单个文件故障隔离能力，不破坏其他文件的数据。

### 3.2 接口层设计
- `POST /index/rechunk/{sourceName}`：启动单源重建切片任务；
- `POST /index/rechunk`：启动全局（或指定源列表）重建切片任务；
- 返回标准 `IndexJobResponse`，前端通过 SSE/WebSocket 实时感知进度。

### 3.3 前端交互设计（`Sources.js`）
- 在操作列增加 `[重建切片]` 按钮，并加宽操作列至 `480px` 避免折行拥挤；
- 增加确认弹窗：向用户详细说明*“将应用当前生效的分块规则重新切分该数据源的所有文档，原有的旧分块将被原子替换”*；
- 在全局管理卡片中增加 `[重建切片（全局）]` 快捷入口。

---

## 4. 实施计划与步骤

1. **后端模型与接口**：
   - 在 `IndexRequest` 与 `IndexJobRequest` 中新增 `Rechunk` 属性；
   - 创建 `RechunkIndexStrategy.cs` 并注册进 `IndexStrategyFactory` 与 DI 容器；
   - 在 `IndexController.cs` 中添加 `rechunk/{sourceName}` 与 `rechunk` 端点。
2. **前端落地**：
   - 在 `wwwroot/app/api/index.js` 中增加 `rechunk` 与 `rechunkSource` 方法；
   - 在 `wwwroot/app/views/Sources.js` 中接入按钮、弹窗逻辑与状态管理。
3. **验证回归**：
   - 编写单元测试验证 `RechunkIndexStrategy` 的工厂分发与参数透传；
   - 校验前端 AST 与 Vue 模板语法；
   - 运行全量单测套件，保证 100% 绿灯。
