# 「全部知识库」被解析成第一个知识库：图谱与 BM25 作用域坍缩调研与修复方案

> 现象：`/graph` 与 `/bm25-index` 页面选择「全部知识库」时，展示的是**第一个知识库**的数字（dev 上甚至是全 0）
> 对象：dev `referencerag-dev`（Vulkan 变体）、prod `referencerag:deploy-b3a30719c303`
> 日期：2026-11-17

---

## 结论先行

1. **根因不是前端显示，而是后端把「无作用域」当成了「第一个启用知识库」。** `GraphController.ResolveKbIdAsync` 与 `BM25IndexController.ResolveKbIdAsync` 在 `kbId` 为空**或**等于 `__all__` 时，都执行 `kbs.FirstOrDefault(k => k.Enabled)?.Id` —— 于是「全部」和「不指定」双双坍缩到同一个库。
2. **两个页面的后果不同，但性质相同。**
   - `/graph`：后端**根本没有 `__all__` 聚合分支**，无论传 `__all__` 还是不传，都只读第一个库 → prod 显示 `17196`（Notes），而两库求和是 `17767`；dev 第一个库是空库，所以截图里全是 `0`，而第二个库实际有 `32` 个节点。
   - `/bm25-index`：后端**有** `__all__` 聚合分支，但前端在「全部知识库」时传的是 `null`（`BM25Index.js:74/94/124`），`null` 又落到「第一个库」分支 → prod 显示 `237721`（Notes），而真正聚合值是 `262497`。也就是说聚合能力存在却从未被调用。
3. **顺带暴露三个同类缺陷**：`__all__` 聚合的 `AverageDocLength` 用**算术平均**而非按分块数加权（prod 显示 `693.44`，加权真值 `539.64`）；未知 `kbId` 不是 404，而是**静默建出一个空 `kb.db`**（本次探测在 prod 上生成了 `kbs/deadbeefdeadbeef/` 292KB，已清理）；MCP 的 `graph_search` / `graph_search_by_title` 在同一条 `FirstOrDefault` 路径上，语义与之完全一致（`knowledgeBaseId` 省略 → 只搜第一个库，且回显的 `knowledgeBaseId` 让调用方以为范围就是它）。
4. **不需要任何新库。** 系统的多库扇出骨架已经存在并被跨库检索使用（`IKnowledgeBaseStorageManager.AcquireLeaseAsync` + `ICatalogStore.ListKnowledgeBasesAsync`），缺的是**统一的「作用域解析」入口**与**聚合口径**。
5. **修复形态：引入单一 `KnowledgeBaseScope` 解析器**（Adapter，不是新架构）：把「无参 / `__all__` / 单库 / 非法值」四种输入解析成三个明确结果，读接口把「无参」等价于 `All`，写接口（重建/清空/索引）**要求显式作用域**；未知 ID 一律 404 且不落盘。

---

## 1. 证据

### 1.1 生产实测矩阵（`referencerag:deploy-b3a30719c303`）

知识库：`Notes`（`40745ffb…`，纯词法）、`默认知识库`（`dadd847d…`，bge-m3）

| 端点 | 无参 | `?kbId=__all__` | `?kbId=Notes` | `?kbId=默认` | 两库求和（真值） |
|:--|--:|--:|--:|--:|--:|
| `/api/graph/stats` NodeCount | **17196** | **17196** | 17196 | 571 | **17767** |
| `/api/graph/stats` EdgeCount | 31749 | 31749 | 31749 | 956 | 32705 |
| `/api/bm25index/summary` TotalChunks | **237721** | 262497 | 237721 | 24776 | 262497 |
| `/api/bm25index/summary` AverageDocLength | **503.85** | **693.44** | 503.85 | 883.03 | 加权应为 **539.64** |

判定：
- 图谱：`无参` 与 `__all__` **完全相等**且 `== Notes`，说明两者都走了「取第一个库」的分支，聚合分支不存在。
- BM25：`__all__` 正确求和（262497），但 `无参` == `Notes`；页面正是传的「无参」（null），所以看到 237721。
- `AverageDocLength` 的 `__all__=693.44` 恰好等于 `(503.85+883.03)/2`，而按分块数加权是 `539.64` —— 典型的「对平均值再平均」。

### 1.2 dev 实测（与截图一致）

知识库：`test`（0 文档）、`金庸武侠测试知识库`（32 文档）

| 端点 | 无参 | `__all__` | `test` | `金庸…` |
|:--|--:|--:|--:|--:|
| `/api/graph/stats` NodeCount | **0** | **0** | 0 | **32** |
| `/api/bm25index/summary` TotalChunks | **0** | 57733 | 0 | 57733 |

截图里的「节点总数 0 / 文档节点 0 / 边数 0」正是第一个库 `test` 的真实数字——数据没错，**作用域错了**。

### 1.3 未知 kbId 会建出空库（副作用）

```
GET /api/graph/stats?kbId=deadbeefdeadbeef     → 200 {"NodeCount":0,...}
GET /api/bm25index/summary?kbId=deadbeefdeadbeef → 200 {"TotalChunks":0,...}
```

宿主机随即出现 `/mnt/disk1/domains/Docker/dc/referencerag/data/kbs/deadbeefdeadbeef/kb.db`（292 KB）。
原因：[KnowledgeBaseStorageManager.cs:85-100](file:///E:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/Infrastructure/ReferenceRAG.Infrastructure/Features/KnowledgeBase/Storage/KnowledgeBaseStorageManager.cs#L85-L100) 用 `_stores.GetOrAdd(id, ...)` **按 id 惰性建库**，未知 ID 不会被拒绝。
（该目录已删除；`kbs/deadbeef/` 是 2026-11-16 同类探测遗留，属同一缺陷的历史产物。）

### 1.4 同一模式的全部落点

```bash
grep -rn "FirstOrDefault(k => k.Enabled)" --include=*.cs Host      # 6 处
```

| 位置 | 语义 | 评估 |
|:--|:--|:--|
| `GraphController.ResolveKbIdAsync` (L55-63) | 查询/重建/增删节点的公共入口 | **缺陷**：`__all__` 与空值都坍缩到第一个库 |
| `BM25IndexController.ResolveKbIdAsync` (L47-62) | 索引/清空/搜索的公共入口 | **缺陷**：同上（聚合分支只在 summary 里另写了一份） |
| `McpTools/GraphSearchTools.cs:66,141` | `graph_search` / `graph_search_by_title` | **缺陷**：省略 `knowledgeBaseId` 时只搜第一个库，且回显值让调用方误以为范围正确 |
| `SourcesController.cs:571,621` | 「新增源未指定库时挂到第一个启用库」 | 语义上可接受（是「挂载默认库」而非「读取统计」），但应回显目标库并写日志 |

---

## 2. 根因

### 2.1 一句话

**「未指定」与「全部」被编码成了同一个值（`null`），而解析器又把 `null` 解释成「第一个库」。** 三层含义（缺省 / 全量 / 单库）被压成两层（缺省 / 单库），聚合层因此永远收不到「要全量」的意图。

### 2.2 分层的具体断点

```
前端 Graph.js:40-42      getActiveKbId(): '__all__' → null          ← 丢弃了「全部」意图
前端 BM25Index.js:74/94/124  targetKb: '__all__' → null              ← 同上
        ↓
后端 ResolveKbIdAsync    kbId == null || kbId == '__all__' → first enabled KB
        ↓
     单库读取                    聚合分支（仅 BM25 summary 有，且从未被页面触发）
```

三处各自「看起来合理」的选择叠加，产生一个**静默改变作用域**的默认值。这和上一轮 MCP 的 P1（错拼参数被静默忽略、检索范围扩大）是同一类缺陷：**默认值改变了查询范围**。

### 2.3 为什么聚合口径也错

`BM25IndexController.GetSummary` 的 `__all__` 分支（L245-281）用 `totalLengthSum / kbCount` 求平均文档长度。但当各库分块数相差 10 倍（237,721 vs 24,776）时，算术平均与「整体语料的平均长度」不是同一个量：平均值不可跨不同权重总体再做平均（Simpson 型聚合偏差）。正确口径是按分块数加权：`Σ(chunks_i × avgLen_i) / Σchunks_i`。

---

## 3. 业界主流方案对比

| 方案 | 代表 | 做法 | 对本项目的启示 |
|:--|:--|:--|:--|
| **省略目标 = 全部**，并用显式哨兵表达「全量」 | Elasticsearch：省略索引路径、`_all`、`*` 三者等价；显式索引名才限定范围 | 语义清晰：**空 ≠ 第一个** | 采纳：读接口「无参」= `All`；`__all__` 保留为显式哨兵 |
| **禁止隐式默认值改变语义** | Google Cloud API Design Guide / AIP：可选字段的默认值必须是「无副作用」的；改变默认值是破坏性变更 | 默认值不能替用户做选择 | 采纳：写接口（重建/清空/索引）不接受「无参」，必须显式 `single|all` |
| **多资源聚合显式返回组成** | Elasticsearch `_msearch` / `_cluster/stats` 返回分片级别明细 | 聚合值必须可被拆解验证 | 采纳：聚合响应带 `scope` + `knowledgeBases[]` 明细，前端可展开核对 |
| **权重聚合而非平均的平均** | SQL `AVG` 于全表（等价加权）；Prometheus `avg` 于聚合后的直方图 | 聚合需在正确总体内计算 | 采纳：`AverageDocLength` 按分块数加权 |
| **能力发现而非猜测** | MCP `tools/list` / REST `GET /resource` 显式声明作用域参数 | 调用方不应靠猜 | 采纳：所有相关端点 `kbId` 参数描述与响应回显 `scope` |

**结论**：不必引入任何第三方库；只需把「作用域」当成**一等领域概念**建模，并统一到一个解析入口。

---

## 4. 现有基础设施复用核查（零新增依赖）

| 需求 | 已有设施 | 位置 |
|:--|:--|:--|
| 枚举知识库 | `ICatalogStore.ListKnowledgeBasesAsync` | `IKnowledgeBaseStorage.cs:209+` |
| 存在性校验（防建空库） | `IKnowledgeBaseStorageManager.ExistsAsync(kbId)` | `IKnowledgeBaseStorage.cs:197` |
| 按库取统计 | `IStorageLease.Store.GetLexicalStatsAsync()` / `.GraphStore.GetStatsAsync()` | `IKnowledgeBaseStorage.cs:15-28`、`SqliteKnowledgeBaseStore.cs:317/2767` |
| 多库扇出 + 部分失败容忍 | `KnowledgeBaseSearchService` 的 `Task.WhenAll` + `successfulKbs/failedKbs` | 业务层现成范式 |
| 统计 TTL 缓存 | `McpStatsCache`（30s，双检锁） | Host 现成 |
| 前端 KB 下拉与 `__all__` 哨兵 | `Graph.js` / `BM25Index.js` 已存在 | 只需改传参 |
| 契约门禁骨架 | `McpInputSchemaGateTests` / `GateTests` 反射枚举 | tests 现成 |

**无需新库**；唯一「新增」是一个纯 C# 解析器（约 60 行）与两处控制器改造。

---

## 5. 具体方案

### 5.1 P0：统一作用域解析器（Adapter，单一事实源）

新增 `KnowledgeBaseScope`（建议放 `Host/ReferenceRAG.ApiHost/Scopes/KnowledgeBaseScopeResolver.cs`）：

```csharp
public enum ScopeKind { Single, All }

public sealed record KnowledgeBaseScope(ScopeKind Kind, IReadOnlyList<string> Ids, string? RequestedRaw);

public static class KnowledgeBaseScopeResolver
{
    /// 读接口：null/""/"__all__" → All（**不再**是「第一个库」）
    public static async Task<KnowledgeBaseScope> ResolveReadAsync(
        ICatalogStore catalog, IKnowledgeBaseStorageManager storage, string? raw, CancellationToken ct);

    /// 写接口：null/"" → 抛 400（必须显式）；"__all__" → All；单库 → 校验存在
    public static async Task<KnowledgeBaseScope> ResolveWriteAsync(
        ICatalogStore catalog, IKnowledgeBaseStorageManager storage, string? raw, CancellationToken ct);
}
```

关键行为：

| 输入 | Read | Write |
|:--|:--|:--|
| `null` / `""` | `All`（启用库全集） | **400 `scope_required`**（"必须指定 kbId 或 `__all__`"） |
| `__all__` | `All` | `All` |
| 存在的 `kbId` | `Single` | `Single` |
| 不存在的 `kbId` | **404 `knowledge_base_not_found`**，且**不创建目录** | 同左 |

「不创建目录」的实现：解析阶段先用 `catalog.GetKnowledgeBaseAsync(id)` 判断存在性（内存/目录查询，不碰存储管理器），只有合法 ID 才允许后续 `AcquireLeaseAsync`。

### 5.2 P0：Graph 聚合（补齐缺失分支）

`GraphController` 全部读取端点（`stats` / `search` / `neighbors` / `subgraph`）与写端点（`rebuild` / `nodes` / `edges`）统一走解析器：

- `All` 读取：对每个启用库取 `GraphStore`，**求和** `GraphStats`（`NodeCount/DocCount/TagCount/HeadingCount/ExternalCount/EdgeCount`）；`search`/`neighbors` 对每库查询后按 `(kbId, nodeId)` 去重合并，并在返回的节点上补 `knowledgeBaseId`。
- `All` 重建：遍历每个库分别重建（当前只有「单库重建」实现，需要循环；且 `_isRebuilding` 是 static 全局锁，**改为按 kbId 的字典锁**，否则「全量重建」会把其它库的重建请求一并挡掉）。
- 响应增加 `scope: {kind, kbIds[], requested}`，让前端能显示「全部 = 2 个库（17,767 节点）」而不是一个裸数字。

### 5.3 P0：BM25 聚合口径与作用域

- `GetSummary`：`__all__`/无参 → 复用解析器的 `All`；`AverageDocLength` 改为 `Σ(chunks_i × avgLen_i) / Σchunks_i`（分块数为 0 的库跳过；全为 0 时回退 `200`）。
- `GetSummary` 响应增加 `scope` 与 `knowledgeBases[]` 明细（每库 chunks / files / avgLen）。
- `Search` / `IndexAll` / `ClearIndex`：
  - `Search`：`All` → 走 `IKnowledgeBaseSearchService` 跨库检索（项目已实现），而不是回落到 legacy `_bm25Store`。
  - `IndexAll` / `ClearIndex`：**写接口**，无参 → 400；`__all__` → 逐库执行并返回每库结果（不是只跑第一个）。
- `POST /bm25index/documents/{chunkId}` 目前只写 legacy store（`BM25IndexController.cs:151`），与知识库作用域无关；需标注「legacy-only」或补齐 `kbId` 参数，否则会再次出现同一类误导。

### 5.4 P1：前端不再吞掉「全部」

```js
// Graph.js:40-42（现状 → 目标）
const getActiveKbId = () => (selectedKbId.value === '__all__' ? '__all__' : selectedKbId.value || undefined);
```

`BM25Index.js:74/94/124` 同样把 `null` 改为 `'__all__'`。
并给下拉与统计区加一行说明：`当前作用域：全部知识库（2 个库合计）` / `当前作用域：Notes`，由响应里的 `scope` 渲染——**让作用域可见**，而不是靠用户猜。

### 5.5 P1：MCP 图谱工具对齐

`graph_search` / `graph_search_by_title`：
- 省略 `knowledgeBaseId` → **跨全部启用库**聚合（与 `knowledge_base_search` 的语义一致），并回显 `kbScope`；
- 节点对象补 `knowledgeBaseId`，否则模型无法把结果回链到具体库（同 `chunkId` 场景）；
- `graph_search` 现有的 `concept only` 语义保留，但描述里补一句「省略即全部库，将逐个库查询并合并」。

### 5.6 设计模式映射（含明确不做）

| 模式 | 落点 | 结论 |
|:--|:--|:--|
| **Adapter** | `KnowledgeBaseScopeResolver`：把协议字符串 → 领域作用域 | 采纳（消除「同参不同义」） |
| **Strategy** | 「单库读 / 多库扇出读」两种执行策略 | 可采纳，但**先用 if 分支即可**；策略对象在两个方法内没有第三种用途，属过度设计 |
| **Registry** | 启用库清单 | 沿用 `ICatalogStore`（不要再造缓存） |
| **StateMachine** | — | **不做**：作用域是每请求属性，与降级同理无生命周期 |
| **Decorator** | 统一拦截「未知 kbId」 | **不做**：解析器已在唯一入口做校验，再加一层会让职责分裂 |

---

## 6. 同类门禁与回归测试

| 编号 | 门禁 | 锁定的不变量 |
|:--|:--|:--|
| **G-KB1** `KnowledgeBaseScopeResolverTests` | `null`/`""` → `All`；`__all__` → `All`；存在 ID → `Single`；不存在 → 抛 404 且**未创建** `kbs/<id>` 目录（对临时数据目录做文件系统断言） | 作用域不再坍缩；未知 ID 不落盘 |
| **G-KB2** `GraphStatsAggregationGateTests` | 造 2 个库（分别 3 / 5 节点）→ `stats` 无参与 `__all__` 都必须等于 8，且 `scope.kbIds` 含两库 | 聚合 = 求和；缺省 = 全量 |
| **G-KB3** `BM25SummaryAggregationGateTests` | 两库分块数相差悬殊（如 300 vs 30）时，`AverageDocLength` 必须等于分块加权值，且**不得**等于算术平均 | 聚合口径正确 |
| **G-KB4** `ScopeRequiredForWriteGateTests` | `POST /graph/rebuild`、`POST /bm25index/index`、`DELETE /bm25index/index` 无参 → 400 `scope_required`；`__all__` → 两库都被执行 | 写操作不再「默认动第一个库」 |
| **G-KB5** `FrontendScopePropagationGateTests` | 静态断言：`Graph.js`/`BM25Index.js` 在 `'__all__'` 分支不得产出 `null`（正则 + AST 检查均可） | 前端不再吞掉全量意图 |
| **G-KB6** `McpGraphScopeGateTests` | 省略 `knowledgeBaseId` → 结果覆盖两库节点，每个节点带 `knowledgeBaseId`，响应带 `kbScope` | MCP 图谱与 REST 同语义 |
| **G-KB7**（端到端）`scope-e2e` | 仅 dev：选「全部」后页面数字 = 各库明细之和；切到单库后数字 = 该库明细 | 页面与接口一致 |

> G-KB1 中「目录未被创建」这条是最容易被忽略也最有价值的一条：它同时锁住了「静默建库」副作用。

---

## 7. 工作量与顺序

| 序 | 任务 | 触碰面 | 风险 |
|:--|:--|:--|:--|
| 1 | `KnowledgeBaseScopeResolver` + G-KB1 | 新文件 + DI | 低 |
| 2 | Graph 读端点改聚合 + G-KB2 | `GraphController` | 低 |
| 3 | Graph 写端点显式作用域 + 每库锁 + G-KB4 | `GraphController`（static 锁改字典） | 中 |
| 4 | BM25 summary 口径 + 明细 + G-KB3 | `BM25IndexController` | 低 |
| 5 | BM25 写端点作用域 + legacy 标注 | `BM25IndexController` | 低 |
| 6 | 前端传 `__all__` + 作用域可见 + G-KB5 | `Graph.js` / `BM25Index.js` | 低 |
| 7 | MCP 图谱跨库 + `knowledgeBaseId` 回填 + G-KB6 | `GraphSearchTools` | 中（响应字段新增） |
| 8 | dev 端到端 + 文档记录 | — | 低 |

**回滚**：解析器与控制器改动均为逐端点切换，任何一步可单独回退；前端传参改动与后端「无参=All」**兼容**（改前后端也能正确显示全部）。

---

## 8. 明确不做

| 项 | 理由 |
|:--|:--|
| 引入 Elasticsearch/搜索中间件 | 25 个工具与 2 个页面的规模，扇出已由现有 `IKnowledgeBaseStorageManager` 覆盖 |
| 给作用域造 StateMachine / Decorator | 作用域是每请求属性，无生命周期 |
| 把 `__all__` 换成 `*` 等通配语法 | 前端已使用 `__all__` 哨兵，换语法属无收益破坏 |
| 「无参就取第一个库」再隐式提示 | 隐式默认值正是本次缺陷本身；应显式化 |

---

## 9. 参考

- Elasticsearch：省略索引目标 / `_all` / `*` 等价于「全部索引」<https://www.elastic.co/docs/reference/elasticsearch/rest-apis/search-multiple-data-streams-indices>
- Google Cloud API Design Guide（可选字段默认值语义与破坏性变更）<https://docs.cloud.google.com/apis/design/changelog>；AIP-157 关于「改变默认值是破坏性变更」<https://google.aip.dev/157>
- 隐式默认值导致错误行为的实践总结 <https://parottasalna.com/blog/learning-notes-49-pitfall-of-implicit-default-values-in-apis/>
- 本仓库相关实现：`GraphController.cs:55-63/386-395`、`BM25IndexController.cs:47-62/245-281`、`KnowledgeBaseStorageManager.cs:85-100`、`Graph.js:40-42`、`BM25Index.js:74/94/124`、`GraphSearchTools.cs:66/141`

---

## 10. 实施状态（2026-11-17）

### 10.1 交付清单

| 方案项 | 落地位置 | 状态 |
|:--|:--|:--|
| 统一作用域解析器 | [KnowledgeBaseScopeResolver.cs](file:///E:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/Host/ReferenceRAG.ApiHost/Services/KnowledgeBaseScopeResolver.cs)（读 null/""/`__all__`→All；写 null/""→`scope_required`；未知 ID→404 且不触达存储层） | ✅ |
| Graph 读端点聚合 | `stats` 求和 + `{scope,kbIds,knowledgeBases[]}` 信封；`search`/`neighbors`/`subgraph`/`node` 跨库合并去重、节点回填 `knowledgeBaseId` | ✅ |
| Graph 写端点作用域 | `rebuild` 逐库循环 + `_rebuildingByKb` 分锁（原 static 全局锁）；节点/边增删限定单库，`__all__` 返回 400 | ✅ |
| BM25 聚合口径 | `AverageDocLength = Σ(chunks_i × avgLen_i) / Σchunks_i`；summary 返回 `scope` + 逐库明细 | ✅ |
| BM25 作用域 | `search` 全量逐库召回合并；`index`/`clear` 逐库执行并回显 `knowledgeBaseIds`；无参写 → 400 | ✅ |
| 前端作用域传递 | `Graph.js`/`BM25Index.js` 转发 `__all__`（不再折叠成 null）；统计区显示「全部知识库（N 个库合计）」；BM25 增逐库明细表 | ✅ |
| MCP 图谱跨库 | `graph_search`/`graph_search_by_title` 省略 `knowledgeBaseId` → 跨全部启用库；节点带 `knowledgeBaseId`；响应带 `kbScope` | ✅ |

### 10.2 门禁（新增 12 项）

| 门禁 | 文件 | 锁定 |
|:--|:--|:--|
| G-KB1（5） | `KnowledgeBaseScopeResolverGateTests` | 读缺省=全部；写缺省=400；未知 ID 抛异常且**未创建** `kbs/<id>` 目录 |
| G-KB2（1） | `KnowledgeBaseScopeAggregationGateTests` | Graph `stats` 无参与 `__all__` 都等于逐库求和；`scope` 与 `knowledgeBases[]` 存在 |
| G-KB3（1） | 同上 | 两库分块 3:1 时 `AverageDocLength` = 加权 775（而非算术 550） |
| G-KB4（1） | 同上 + `LexicalAuthorizationTests`（2 处期望更新） | 写操作无参 → 400 `scope_required`；显式 `__all__` 才继续 |
| G-KB5（2） | `KnowledgeBaseScopePropagationGateTests` | `Graph.js`/`BM25Index.js` 不得出现「`!=='__all__' ? … : null`」写法 |
| G-KB6（3） | 同上 | MCP 省略作用域搜两库、节点带 `knowledgeBaseId`；显式单库仍单库 |

> G-KB4 顺带修改了两条既有门禁的期望：`LexicalAuthorizationTests` 与 `LexicalRegressionHostTests` 原先断言写操作在词法模式下返回 409；现在缺省作用域先被 400 拦下，显式 `__all__` 才是 409。这是**契约变更**导致，非回归。

### 10.3 dev 真机验收（`referencerag-dev`）

**接口矩阵（dev 两库：`test` 0 文档 / `金庸武侠测试知识库` 32 文档）**

| 端点 | 无参 | `__all__` | test | 金庸 | 求和 |
|:--|--:|--:|--:|--:|--:|
| `graph/stats` nodes | **32**（scope=all, 2 库） | 32 | 0 | 32 | 32 |
| `bm25/summary` chunks | **57733**（scope=all） | 57733 | 0 | 57733 | 57733 |
| `bm25/summary` avg | 403.74 | 403.74 | 200 | 403.74 | 403.74 |

- 写端点：`POST /api/bm25index/index` 与 `POST /api/graph/rebuild` 无参 → **400 `scope_required`**；带 `?kbId=__all__` → 正常执行。
- 未知 ID：`graph/stats` / `graph/search` / `graph/neighbors` / `bm25/summary` 的 `kbId=nope` → **404 `knowledge_base_not_found`**，且 `kbs/` 下**未新增目录**（清理了此前探测遗留的 `kbs/deadbeef`）。

**MCP**

```
graph_search_by_title(query=侠)                 → kbScope=all, kbs=[2 个], count=4, 节点 knowledgeBaseId 均标注
graph_search_by_title(query=侠, kbId=test)       → kbScope=single, count=0
graph_search(nodeId=…, depth=1)                  → kbScope=all, totalNodes=1
graph_search(kbId=nope)                          → knowledge_base_not_found
```

**页面（内置浏览器 DOM 断言）**

- `/graph`：统计卡显示 `节点总数 32 / 全部知识库（2 个库合计）`，下拉为「全部知识库 (聚合统计)」。
- `/bm25-index`：索引概览显示 `已索引文档 57733 / 平均文档长度 403.74`，口径说明「平均长度按分块数加权；全部知识库（2 个库合计）」，并出现逐库明细表（test 0 / 金庸 57733）。
- 网络证据：页面加载即请求 `GET /api/bm25index/summary?kbId=__all__`（此前是 `?kbId=` 空值，后端回落首库）。
- **单库切换未做 DOM 交互验证**：Naive UI 下拉为浮层渲染，合成事件无法可靠驱动；单库行为改由 API 层矩阵（上表 `test`/`金庸` 两列）覆盖。未截图（内置浏览器无可见视口）。

### 10.4 明确未做

| 项 | 原因 |
|:--|:--|
| Graph 全量重建并行化 | 当前逐库串行循环 + 单锁，语义正确；并行需评估 IO/CPU 争用，非本次范围 |
| `bm25index/documents/{chunkId}` 补齐 kb 参数 | 该端点只写 legacy store，与本作用域体系无关；已在文档标注为 legacy-only |
| `SourcesController` 的「未指定库时挂到第一个启用库」 | 语义是「挂载默认库」而非「读取统计」，保持现状但应回显目标库（后续小改） |

### 10.5 正式环境部署与复验（`referencerag:deploy-982ac031463f`，2026-11-17）

**产物溯源**：容器内 `/app/McpHelper.Core.dll` md5 `dce874f61e6e569b8bc50239a1f42077` = `packages/McpHelper.Core.2.4.0.nupkg` 内 DLL = 新建镜像内 DLL；镜像 tag = 仓库 HEAD 短哈希 `982ac031463f`；容器 `Up (healthy)`、`/api/system/health` 200、ROCm 设备参数（`/dev/kfd /dev/dri`）沿用。

**接口矩阵（生产两库：Notes 24 万+ 分块 / 默认知识库 2.4 万+ 分块）**

| 端点 | 无参 | `__all__` | Notes | 默认 | 求和 |
|:--|--:|--:|--:|--:|--:|
| `graph/stats` nodes | **17767**（scope=all，2 库明细） | 17767 | 17196 | 571 | 17767 |
| `graph/stats` edges | **32705** | 32705 | 31749 | 956 | 32705 |
| `bm25/summary` chunks | **262497**（scope=all） | 262497 | 237721 | 24776 | 262497 |
| `bm25/summary` avg | **539.64** | 539.64 | 503.85 | 883.03 | 加权 539.64 |

对照修复前的 17196 / 237721 / 693.44：**三处全部纠正**，且无参与 `__all__` 现在完全一致。

**写作用域**：`POST /api/bm25index/index`、`POST /api/graph/rebuild` 无参 → **400 `scope_required`**。

**未知 ID**：`graph/stats` / `bm25/summary` 的 `kbId=nope` → **404 `knowledge_base_not_found`**；探测后 `kbs/` 未新增目录。宿主机残留的 `kbs/deadbeef`（2026-11-16 同类探测遗留，未被 catalog 注册）本次未删除——它不影响聚合（聚合按 catalog 启用库枚举），留待单独清理。

**MCP**：`graph_search_by_title` 无参 → `kbScope=all`、`kbIds` 两库、10 条结果节点均带 `knowledgeBaseId`；显式 `kbId=Notes` → `kbScope=single`、10 条；`graph_search` 无参 → `kbScope=all`。

**页面（内置浏览器 DOM 断言）**

- `/graph`：`节点总数 17767 / 全部知识库（2 个库合计）`、文档 6930 / 标签 10274 / 标题 563 / 边 32705。
- `/bm25-index`：`已索引文档 262497 / 平均文档长度 539.64`、统计口径「平均长度按分块数加权；全部知识库（2 个库合计）」，逐库明细表 `Notes 237721 / 默认知识库 24776`。
- 加权口径独立复核：`Σ(chunks×avg)/Σchunks = 539.64` 与接口值一致。
- 未截图（内置浏览器无可视视口）；单库下拉切换未做合成事件验证（Naive UI 浮层），由接口矩阵单库两列覆盖。

**回归复验**：MCP 生产断言 **39/39** 通过（重排降级、通道事实、边界、outputSchema 全部保持）；离线审计 `tools=25 compact=29,541B outputSchema=25.8%`、warn 0、选型 17 明确 / 1 错误（与基线一致）。

**遗留观察项**：`bm25/summary` 的 `TotalVocabularySize` 为各库之和（500000），但各库值都是按分块数估算的上限 250000，属估算口径叠加而非真实去重词表规模。若要精确值需按库读 FTS 词表大小，成本较高，暂不处理，已在页面「统计口径」文案中只声明平均长度口径。
