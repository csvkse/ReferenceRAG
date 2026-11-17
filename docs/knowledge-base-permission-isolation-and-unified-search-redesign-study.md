# 知识库一等公民权限隔离模型与统一搜索接口重构调研与方案设计

> **文档定位**：深入解答并解决两大核心架构问题：
> 1. **权限隔离模型错位**：项目已升级为 v2「物理隔离多知识库（每库独立 `kb.db`）」，为何 API Key 权限仍停留在旧数据源（`AllowedSources`）维度？改为「全范围」为何破坏了隔离性？如何重构为「知识库一等公民」的真正隔离？
> 2. **搜索接口碎片化与漏抽象（Leaky Abstraction）**：为何对外暴露「向量（`semantic_search`）」与「词法（`lexical_search`）」是糟糕的设计？如何收敛为业界通行的「统一知识检索（`knowledge_search`）+ 策略参数控制 / 智能路由」？
> 
> **涉及系统范围**：服务端鉴权网关（`ApiKeyMiddleware` / `ApiKeyPermissionMatrix`）、多库检索服务（`KnowledgeBaseSearchService`）、MCP 工具层（`RagSearchTools` / `KnowledgeBaseMcpTools`）、Web 管理端（`ApiKeys.js`）、以及 Agent Skill 客户端。
> **日期**：2026-11-17

---

## 目录

1. [执行摘要与核心决断 (Executive Summary)](#1-执行摘要与核心决断)
2. [问题一：API Key 权限失配根因与知识库隔离重构方案](#2-问题一api-key-权限失配根因与知识库隔离重构方案)
3. [问题二：搜索接口碎片化缺陷与统一搜索接口重构方案](#3-问题二搜索接口碎片化缺陷与统一搜索接口重构方案)
4. [设计模式驱动的整体架构优化 (Design Patterns Application)](#4-设计模式驱动的整体架构优化)
5. [业界主流方案深度对比与生态借鉴 (Industry Benchmarking)](#5-业界主流方案深度对比与生态借鉴)
6. [系统现有基础设施复用核查 (Reusable Assets Audit)](#6-系统现有基础设施复用核查)
7. [具体工程落地蓝图与平滑演进步骤 (Implementation Blueprint)](#7-具体工程落地蓝图与平滑演进步骤)

---

## 1. 执行摘要与核心决断

### 1.1 对用户两大疑问的明确结论

| 核心问题 | 结论判定 | 核心事实与根因归纳 |
|:---|:---|:---|
| **问题 1：项目不应该按知识库隔离吗？改为全范围不就没有隔离作用了吗？** | **完全正确，直击要害。改为全范围是临时避坑的妥协，彻底击穿了安全边界。** | **根因是「领域模型代差」**：v2 将物理存储升级为了知识库（`kb.db`），前端 UI 也增加了「授权知识库」复选框，但底层 `ApiKeyEntity` 和后端鉴权引擎仍停留在旧版数据源（`AllowedSources`），且 UI 将 `kb.name`（如 `"Notes"`）存入 `AllowedSources`，与底层连接器数据源名（`"Notes - Note"`）精确匹配失败，导致「勾选了库却 0 权限」；改全范围抹平了错误，但丧失了多租户多库隔离。 |
| **问题 2：搜索接口是否需要优化？不需要使用向量和词法这种名称，改为通用语义 + 参数控制？** | **完全正确，符合现代 Agent 与工业级检索演进方向。** | **根因是「实现细节泄漏（Leaky Abstraction）」**：「向量」与「BM25 词法」是存储引擎的**索引实现技术**，不是调用方或 Agent 的**业务意图**。当前 MCP 暴露 4 个同质化检索工具（`semantic_search`、`lexical_search`、`hybrid_search`、`knowledge_base_search`），白白浪费 ~1,500 Token 描述预算，导致模型在选工具时反复摇摆。应收敛为统一的 `knowledge_search` 工具，通过 `mode: "auto" \| "hybrid" \| "semantic" \| "lexical"` 参数控制，默认 `auto` 智能路由。 |

---

## 2. 问题一：API Key 权限失配根因与知识库隔离重构方案

### 2.1 缺陷全链路证据链剖析

在 v2 重构前，系统是单一全局数据库（`vectors.db`），权限管控针对的是 `appsettings.json` 中的 `config.Sources`（数据源目录路径）。
在升级为 v2 多知识库架构后，**权限层的演进发生了脱节与半吊子改造**：

```
【前端 UI (ApiKeys.js)】
  ├─ 提供了「📚 授权知识库」复选框，但 binding 的值是: kb.name (即 "Notes")
  └─ 保存时全部打包进: AllowedSources = '["Notes"]'
            │
            ▼ (协议传输与存储)
【数据层 (ApiKeyEntity)】
  ├─ 字段仅有: SourceScopeMode ("all" | "custom")
  └─ 字段仅有: AllowedSources (仍命名为 Sources，无 AllowedKnowledgeBaseIds)
            │
            ▼ (后端权限校验)
【鉴权层 (KnowledgeBaseFileAuthorization / ApiKeyPermissionMatrix)】
  ├─ 校验逻辑: _allowedSources.Contains(source.Name)
  └─ 真实物理源名称: "Notes - Note"、"Notes - 【VNote】"、"Temp"
            │
            ▼
【运行实况】 "Notes".Equals("Notes - Note") 恒为 False！
  ├─ 现象 1: 勾选了 Notes 库，但检索时 100% 切片在最后一步被 authorization.Allows 过滤掉！
  ├─ 现象 2: 检索返回 Items: [] 且 NoMatch: true，调用方以为是"知识库无此资料"！
  └─ 掩盖缺陷手段: 将 Key 改为 SourceScopeMode = "all"，绕过校验，但隔离性彻底归零！
```

### 2.2 为什么「全范围 (all)」不可接受

1. **多租户与数据安全破损**：企业级或私有化部署中，知识库包含敏感级划分（如「研发技术库」、「财务人事库」、「私有日记库」）。若 API Key 无法限定 `knowledgeBaseId`，给 Agent 配发的 Key 将能跨越物理边界检索所有库。
2. **检索噪声与性能浪费**：不限定库时，跨库检索会向所有启用库扇出并发检索（最多 8 库），既消耗 GPU 推理显存，又容易召回无关库的冗余切片（如搜技术术语召回小说正文）。

### 2.3 知识库一等公民权限隔离模型设计

权限模型必须从「**基于数据源路径字符串猜测**」演进为「**基于知识库不可变唯一 ID（`knowledgeBaseId`）的严密授权**」。

```
┌─────────────────────────────────────────────────────────────────────────┐
│                      新型 API Key 权限模型 (v2.1)                         │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                         │
│  [ScopeMode]: "all" | "knowledge_bases" | "legacy_sources"              │
│                                                                         │
│  一级物理边界 (知识库级):                                                 │
│  [AllowedKnowledgeBaseIds]: ["40745ffb28444648"]                        │
│   ├── 决定了: 1. /api/knowledge-bases 只能看见被授权的库                 │
│   ├── 决定了: 2. 检索请求省略 knowledgeBaseIds 时，自动钳制为该集合      │
│   └── 决定了: 3. 显式请求未授权库时，直接返回 403 Forbidden 或安全过滤  │
│                                                                         │
│  二级细粒度边界 (可选，连接器/文件夹级，实现 ABAC 深度下钻):               │
│  [AllowedConnectors] / [PathFilters]: 可选针对库内特定子目录二次限制      │
│                                                                         │
└─────────────────────────────────────────────────────────────────────────┘
```

#### 关键改动点：
1. **数据模型增加 `AllowedKnowledgeBaseIds`**：
   - 存储结构：JSON 字符串数组，如 `["40745ffb28444648"]` 或 `["*"]`。
   - 使用不可变 16 位 Hex ID（`kbId`），杜绝知识库重命名（Display Name 变动）带来的权限失效或越权漏洞。
2. **请求作用域自动收敛（Scope Clamping）**：
   - 当调用方发起检索未传 `KnowledgeBaseIds` 时，服务端根据当前 Key 的 `AllowedKnowledgeBaseIds` 自动填充，**而不是直接打向全系统所有库**。
   - 当调用方试图检索未授权的 `KnowledgeBaseIds` 时，服务端校验拦截并抛出标准 `forbidden` 异常。
3. **列表探查自动过滤**：
   - `GET /api/knowledge-bases` 与 MCP `knowledge_base_list` 仅返回当前 Key 有权访问的知识库，彻底消除信息泄露。

---

## 3. 问题二：搜索接口碎片化缺陷与统一搜索接口重构方案

### 3.1 现状审查：暴露底层技术的 4 组同质化检索工具

当前系统中对外暴露了多达 4 个检索入口：

| 工具名 | 标题 | 底层实现 | 本质差异 |
|:---|:---|:---|:---|
| `hybrid_search` | 混合检索（向量+词法） | `SearchAsync(..., useLexical: lexicalOnly)` | 双路召回 + RRF 融合 |
| `semantic_search`| 语义检索（向量） | `SearchAsync(..., useLexical: false)` | 仅走向量通道（纯词法库自动回落词法） |
| `lexical_search` | 词法检索（知识库） | `SearchAsync(..., useLexical: true)` | 仅走 BM25 词法通道 |
| `knowledge_base_search` | 跨库融合检索 | `SearchAsync(...)` | 实际与 `hybrid_search` 完全重合 |

### 3.2 为什么必须优化并统一搜索接口？

1. **严重的实现细节泄漏（Leaky Abstraction）**：
   - 客户端与 LLM Agent 关注的是「**找到相关的技术方案或配置答案**」，而不是底层用了「HNSW 向量索引」还是「SQLite FTS5 倒排索引」。
   - 把实现算法作为工具名强加给 Agent，是典型的「把服务端内部架构决策推卸给客户端」。
2. **模型选择歧义与 Token 资源浪费**：
   - 4 个工具入参几乎完全一致（`query`、`topK`、`knowledgeBaseIds`），占用了 MCP `tools/list` 中大量上下文（约 1,500 ~ 2,000 Token）。
   - 在 Anthropic、Berkeley Function Calling 等权威基准评测中，**功能重叠度高的同名工具会显著提升大模型的调用选错率**（例如想查代码错误码，模型却错误调用了 `semantic_search`，因为语义描述里都写着“检索”）。
3. **底层能力与上层声明脱节**：
   - 生产环境中 `Notes` 库（23.7 万切片）根本没有向量索引（`spec_lexical_only`）。
   - 当 Agent 调用 `semantic_search` 查 `Notes` 库时，后端因无向量自动切到了纯词法检索。这证明：**无论上层 Agent 选什么工具，最终能否走向量完全取决于目标知识库的物理特性**。客户端去猜选哪个工具毫无意义。

### 3.3 统一检索接口设计方案：`knowledge_search`

将 4 个碎片化工具收敛为**一个强类型、参数化、自适应的统一搜索接口**：

```
┌────────────────────────────────────────────────────────────────────────┐
│                        统一检索工具: knowledge_search                   │
├────────────────────────────────────────────────────────────────────────┤
│                                                                        │
│  入参定义:                                                             │
│  • query (string, 必填): 检索文本/关键词/问题描述                      │
│  • mode (string, 可选, 默认 "auto"): 检索策略模式                      │
│      - "auto"    : [推荐] 服务端根据查询特征与目标库规格智能决策      │
│      - "hybrid"  : 强制向量 + BM25 双路融合 (RRF)                      │
│      - "semantic": 优先向量语义相似度召回                             │
│      - "lexical" : 优先纯字面关键词/代码标识匹配 (免向量，极速)        │
│  • topK (integer, 可选, 默认 5, 上限 50): 返回条数                     │
│  • knowledgeBaseIds (string[], 可选): 显式指定检索库 (受权限裁剪)      │
│                                                                        │
│  智能路由逻辑 (mode="auto" 时内部裁决):                                │
│   1. 目标库无向量索引 (如 spec_lexical_only) ──> 自动转纯词法         │
│   2. 查询词含错误码/英文驼峰标识/路径/引号  ──> 优先词法 + 混合        │
│   3. 自然语言疑问句/抽象概念解释            ──> 混合向量 RRF           │
│                                                                        │
│  响应体核心事实字段:                                                   │
│  • items[*].channels: 透出本次命中的实际通道 (["Lexical"], ["Vector"])  │
│  • noMatch: 布尔值，无任何实词命中时显式警示                           │
│                                                                        │
└────────────────────────────────────────────────────────────────────────┘
```

#### 兼容性平滑过渡策略（Deprecation & Alias）：
- 保留 `hybrid_search`、`semantic_search`、`lexical_search` 作为**隐藏别名（Deprecated Aliases）**，内部全部重定向调用 `knowledge_search` 实现；
- 在 MCP `tools/list` 中，通过配置或注解仅暴露推荐的 `knowledge_search`，消除 Agent 的选择困难。

---

## 4. 设计模式驱动的整体架构优化

针对上述两大核心重构，采用以下经典设计模式进行体系化架构治理：

```
                                  ┌───────────────────────────┐
                                  │      外部调用入口          │
                                  │  (REST / MCP / CLI Skill) │
                                  └─────────────┬─────────────┘
                                                │
                                                ▼
┌───────────────────────────────────────────────────────────────────────────────────────────┐
│ 1. 门面模式 (Facade Pattern)                                                              │
│    IKnowledgeSearchFacade: 统一人身鉴权、范围裁剪、策略决策、指标埋点、结果脱敏             │
└───────────────────────────────────────┬───────────────────────────────────────────────────┘
                                        │
           ┌────────────────────────────┴────────────────────────────┐
           ▼                                                         ▼
┌───────────────────────────────────────┐ ┌─────────────────────────────────────────────────┐
│ 2. 策略模式 (Strategy Pattern)        │ │ 3. 适配器模式 (Adapter Pattern)                 │
│    • ISearchRetrievalStrategy:        │ │    • ScopeAdapter:                              │
│      - AutoRoutingStrategy (智能决策) │ │      双向适配 legacy AllowedSources 与          │
│      - HybridRrfStrategy (双路融合)   │ │      新 AllowedKnowledgeBaseIds                 │
│      - PureLexicalStrategy (纯字面)   │ │    • LegacyToolAdapter:                         │
│      - DenseVectorStrategy (纯向量)   │ │      把旧工具参数映射到统一门面                 │
│    • IScopeAuthorizationStrategy:     │ └─────────────────────────────────────────────────┘
│      - KnowledgeBaseScopeStrategy     │
└───────────────────────────────────────┘
                                        │
                                        ▼
┌───────────────────────────────────────────────────────────────────────────────────────────┐
│ 4. 状态机模式 (State Machine Pattern)                                                     │
│    RetrievalExecutionStateMachine: 管理跨库扇出生命周期                                   │
│    [Authorized] ──> [Fanout Dispatch] ──> [RRF Fuse] ──> [Quality Check (NoMatch)] ──> [Done]
└───────────────────────────────────────────────────────────────────────────────────────────┘
```

### 4.1 适配器模式（Adapter Pattern）：权限模型双向兼容

为了在升级到 `AllowedKnowledgeBaseIds` 的同时，不破坏已有数据库中的旧版 API Key，引入 `PermissionScopeAdapter`：
- **向上兼容读取**：
  - 若 `key.SourceScopeMode == "all"`：适配为 `ScopeKind.All`。
  - 若 Key 包含新字段 `AllowedKnowledgeBaseIds`：直接采用 KB 物理隔离策略。
  - 若 Key 仅有旧版 `AllowedSources`：自动调用 `CatalogStore` 将旧源名匹配转换为其挂载的 `KnowledgeBaseId` 集合；若无法匹配，则按连接器路径做降级保护。
- **消除了脆弱的字符串包含判断**，集中在一处收敛逻辑。

### 4.2 策略模式（Strategy Pattern）：检索通道动态调度

将检索逻辑解耦为策略接口：
```csharp
public interface ISearchExecutionStrategy
{
    SearchMode Mode { get; }
    Task<RecallSnapshot> RecallAsync(KnowledgeBaseDefinition kb, SearchContext ctx, CancellationToken ct);
}
```
- `AutoRoutingStrategy`：充当元策略（Meta-Strategy），通过快速规则评估（启发式正则 + 目标库元数据）自动指派执行 `Lexical` 还是 `Hybrid`。
- 策略模式将「检索方式」与「存储接口」彻底解耦，未来若引入 GraphRAG 或 ColBERT 重排策略，只需新增策略类，无需修改上层控制器。

### 4.3 门面模式（Facade Pattern）：单一入口收敛

建立 `KnowledgeSearchFacade`，统一承接 REST、MCP、CLI 的所有查询请求，将以下通用职责集中收紧：
1. **API Key 范围裁剪（Scope Enforcement）**；
2. **入参上下界规整（Parameter Clamping）**；
3. **策略决策与扇出融合（Strategy Dispatch & Fusion）**；
4. **质检信号标记（QueryMatchSignals / NoMatch）**；
5. **Token 友好型摘要（Compact Formatting）**。

---

## 5. 业界主流方案深度对比与生态借鉴

| 对比维度 | 向量数据库标准 (Qdrant / Weaviate / Milvus) | 搜索引擎标准 (Elasticsearch / OpenSearch) | 大模型知识库系统 (Dify / FastGPT) | 本方案重构设计 (ReferenceRAG v2.1) |
| :--- | :--- | :--- | :--- | :--- |
| **隔离单元** | **Collection / Tenant 隔离**（API Key 直接绑定允许访问的 collection 名称） | **Index / Alias 隔离**（Role 权限控制 `indices: ["kb-*"]`） | **Dataset 级隔离**（每个 API Key 属于特定 dataset） | **KnowledgeBase 物理库隔离**（`AllowedKnowledgeBaseIds` 绑定独立 `kb.db`） |
| **检索接口形态** | **单入口统一搜索**：<br>`POST /collections/{name}/points/search`，由 `params.exact` / `hybrid` 控制 | **单入口统一搜索**：<br>`POST /{index}/_search`，统一 DSL 查询体包含 match 与 knn 子句 | **单入口检索 API**：<br>`POST /v1/datasets/{id}/retrieve`，入参带 `retrieval_model` | **单入口统一检索**：<br>`knowledge_search` (MCP) + `/api/knowledge-base-search` (REST)，由 `mode` 控制 |
| **工具设计准则** | 不向外部 SDK 拆分暴露底层倒排或稠密点查找 | 不区分纯分词查询与向量查询 API，由服务端执行计划处理 | 对 Agent 仅暴露单一的「知识库检索」Tool | **严格对齐 Anthropic 准则**：单一高内聚工具，拒绝工具数量膨胀 |
| **空结果识别** | 返回空数组，由客户端比对 score 阈值 | 返回 `hits: []` | 提供 `similarity_threshold` 截断 | **内置 QueryMatchSignals**：返回 `noMatch: true` 显式信号 |

---

## 6. 系统现有基础设施复用核查

本次重构完全基于 ReferenceRAG 现有骨架，**零新增第三方 NuGet 包与外部依赖**：

| 重构所需能力 | 系统现有可复用基础设施 | 具体位置 | 复用与扩展方式 |
| :--- | :--- | :--- | :--- |
| **多库扇出与融合** | `IKnowledgeBaseSearchService` / `CrossKnowledgeBaseSearchRequest` | `ReferenceRAG.Business` | **直接复用**，核心检索骨架完全具备 |
| **通道事实与分数** | `RecallChannel` (Lexical / Vector) / `ScoreKind` | `ReferenceRAG.Core` | **直接复用**，无需改动底层数据结构 |
| **未命中判定** | `QueryMatchSignals.Evaluate` | `ReferenceRAG.Business` | **直接复用**，无缝接入统一门面 |
| **权限数据库** | `ApiKeyStore` (SQLite `data/api_keys.db`) | `ReferenceRAG.Infrastructure` | **仅需无损增加 1 列** `allowed_knowledge_base_ids` |
| **MCP 工具定义** | `ModelContextProtocol.Server` / `McpHelper.Core` | `ReferenceRAG.ApiHost` | **扩展注册** `knowledge_search`，旧工具保留别名 |
| **前端管理视图** | `ApiKeys.js` (Naive UI) | `ReferenceRAG.Business/wwwroot` | **修改复选框绑定值**：将 `kb.name` 改为 `kb.id`，提交独立字段 |

---

## 7. 具体工程落地蓝图与平滑演进步骤

重构分为三个有序推进的工程阶段，确保无停机、无数据损坏、无外部兼容破坏。

### 阶段一：权限模型升级（实现真正的知识库隔离）

1. **数据库无损升级（`ApiKeyStore.cs`）**：
   - 在 `data/api_keys.db` 的 `api_keys` 表中新增列：
     ```sql
     ALTER TABLE api_keys ADD COLUMN allowed_knowledge_base_ids TEXT DEFAULT '[]';
     ```
   - 实体类 `ApiKeyEntity` 增加字段：
     ```csharp
     public string AllowedKnowledgeBaseIds { get; set; } = "[]";
     ```
2. **权限矩阵决断器扩展（`ApiKeyPermissionMatrix.cs`）**：
   - 增加知识库级决断逻辑：
     ```csharp
     public static bool CanAccessKnowledgeBase(ApiKeyEntity key, string kbId)
     {
         if (string.Equals(key.SourceScopeMode, "all", StringComparison.OrdinalIgnoreCase)) return true;
         var allowedKbs = ParseJsonOrTokens(key.AllowedKnowledgeBaseIds);
         if (allowedKbs.Contains("*") || allowedKbs.Contains(kbId, StringComparer.OrdinalIgnoreCase)) return true;
         return false;
     }

     public static IReadOnlyList<string>? GetEffectiveAllowedKnowledgeBases(ApiKeyEntity? key)
     {
         if (key == null || string.Equals(key.SourceScopeMode, "all", StringComparison.OrdinalIgnoreCase)) return null;
         var kbs = ParseJsonOrTokens(key.AllowedKnowledgeBaseIds);
         return kbs.Contains("*") ? null : kbs.ToList();
     }
     ```
3. **检索服务接入范围收敛（`KnowledgeBaseSearchService.cs`）**：
   - 当请求传入或上下文关联了受限 Key 时，`targetKbIds` 直接与 `GetEffectiveAllowedKnowledgeBases` 取交集；若交集为空，直接返回空结果或安全拒绝。
4. **前端修复（`ApiKeys.js`）**：
   - 将「📚 授权知识库」复选框的 `:value` 由 `kb.name` 修正为 `kb.id`；
   - 提交时分别传输 `allowedKnowledgeBaseIds` 与 `allowedSources`。

---

### 阶段二：搜索接口收敛为统一 `knowledge_search`

1. **在 MCP 服务中注册全新统一工具（`KnowledgeSearchTools.cs`）**：
   ```csharp
   [McpServerTool(Title = "知识库检索（统一入口）")]
   [Description("在本地知识库中检索技术文档、笔记与最佳实践。支持通用语义理解、精确关键词匹配及混合检索。默认 auto 模式会自动依据查询特征和知识库类型智能选优。")]
   public Task<string> KnowledgeSearch(
       [Description("搜索查询文本或问题（必填）")] string query,
       [Description("检索模式：auto (默认智能决策) / hybrid (混合RRF) / semantic (向量语义) / lexical (精准词法)")] string mode = "auto",
       [Description("返回结果数量，默认 5，建议 1-20，上限 50")] int topK = 5,
       [Description("目标知识库 ID 列表，须为 JSON 数组字符串，如 [\"kb_a\"]。留空表示检索所有已授权库")] string? knowledgeBaseIds = null)
   {
       // 1. mode 参数解析与智能路由
       var (effectiveUseLexical, effectiveAllowVector) = ResolveSearchMode(mode, query);
       // 2. 调用统一检索内核
       return SearchAsync(nameof(KnowledgeSearch), query, topK, knowledgeBaseIds, effectiveUseLexical);
   }
   ```
2. **平滑废弃旧工具（Backward Compatibility）**：
   - 旧版 `hybrid_search`、`semantic_search`、`lexical_search` 在代码中保留，方法体内直接转发至 `KnowledgeSearch` 统一逻辑；
   - 在描述中标记 `[已兼容合并，新集成请优先使用 knowledge_search]`。

---

### 阶段三：Skill 与客户端无缝对齐

1. **更新 `ReferenceRAG` Skill**：
   - 将 `SKILL.md` 中的推荐调用首选更新为 `knowledge_search`；
   - 在 `scripts/rag_cli.py` 中增加统一参数支持：`python rag_cli.py search "..." --mode auto`。
2. **运行全量验收门禁**：
   - 验证配置特定知识库的 Key 无法跨库越权；
   - 验证 `knowledge_search` 在 `Notes`（纯词法）与 `默认库`（混合向量）上自动无感完成最优召回。

---

## 8. 总结

用户的两大质疑极其深刻，击中了系统从 v1 演进至 v2 过程中遗留的**权限概念断层**与**接口冗余膨胀**两个最核心的痛点。

通过：
1. **将权限模型提升至「知识库一等公民（`knowledgeBaseId` 隔离）」**，彻底修复虚假的"全范围"安全漏洞；
2. **将检索接口收敛为「统一语义检索（`knowledge_search` + `mode` 智能路由）」**，消除模型认知负担与 Token 浪费；

ReferenceRAG 将在架构整洁度、调用确定性与工业级多租户安全性上达到完全现代化的标准。
