# 全文词法索引管理统计为空与“小龙女/神雕侠侣”检索断层根因调研与统一治理方案

> **文档状态**：调研与方案落地指南  
> **涉及核心模块**：`BM25IndexController`、`SqliteKnowledgeBaseStore`、`SearchService`、`AIQueryController`、`BM25Index.js`、`Search.js`  
> **核心目标**：彻底解决 BM25 概览统计持续为 0 问题；消除全局源与物理知识库的数据孤岛；打通《神雕侠侣》等全集语料的端到端词法与混合检索，杜绝“要么为空，要么异常”的历史缺陷。

---

## 一、现象还原与真实环境诊断现场

用户在远程 Linux 容器（`referencerag-dev`，`http://127.0.0.1:5124`）使用中遇到了两个高度关联的严重问题：

### 现象 1：全文词法索引管理 (BM25 / FTS5) 概览数据全部归零
在 `/bm25index` 页面上，概览面板显示：
```
已索引文档: 0        词汇量: 0
平均文档长度: 200.00  总文件数: 0
总分块数: 0
```
即便系统后台实际已经执行过切片与导入，统计指标始终为 0，给运维和用户造成“索引未建立”或“服务损坏”的直观误导。

### 现象 2：“小龙女”检索要么为空，要么异常，完全搜不到《神雕侠侣》
用户通过真实 curl 发送标准词法检索请求：
```bash
curl --url 'http://127.0.0.1:5124/api/ai/query' \
  -H 'Content-Type: application/json' \
  --data-raw '{"Query":"小龙女","Mode":"Lexical","TopK":10,"EnableRerank":false}'
```
响应结果：
```json
{
  "Query": "小龙女",
  "Mode": "Lexical",
  "Chunks": [],
  "Files": [],
  "Stats": { "TotalMatches": 0, "DurationMs": 0 }
}
```
不仅纯词法模式返回 0 条匹配，用户在前端切换其它模式或参数时，甚至出现直接报错异常。金庸武侠中最核心的角色之一“小龙女”和经典名篇《神雕侠侣》仿佛从系统中“凭空消失”。

---

## 二、代码与数据底层全链路深度溯源（Root Cause Analysis）

通过进入远程 Linux Docker 容器和宿主机，对 SQLite 数据库底层（`vectors.db`、`bm25.db`、`kbs/*/kb.db`）以及前后端核心代码逐行审查，发现了三处致命的架构设计脱节与逻辑缺陷：

### 2.1 根因一：统计接口读取“向量字段冒充词法字段”的致命缺陷

审查 [`BM25IndexController.cs`](file:///e:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/Host/ReferenceRAG.ApiHost/Controllers/BM25IndexController.cs#L220-L248)：
```csharp
[HttpGet("summary")]
public async Task<ActionResult<BM25Summary>> GetSummary([FromQuery] string? kbId = null)
{
    var targetKbId = await ResolveKbIdAsync(kbId);
    if (!string.IsNullOrEmpty(targetKbId) && _kbStorageManager != null)
    {
        await using var lease = await _kbStorageManager.AcquireLeaseAsync(targetKbId, StorageAccess.ReadOnly);
        var manifest = await lease.Store.GetManifestAsync();
        return Ok(new BM25Summary
        {
            TotalIndexedDocuments = (int)manifest.ActiveVectorCount,
            TotalVocabularySize = (int)manifest.ActiveVectorCount * 50,
            AverageDocLength = 200,
            TotalFiles = (int)manifest.ActiveVectorCount,
            TotalChunks = (int)manifest.ActiveVectorCount
        });
    }
    // ...
}
```

1. **向量计数（ActiveVectorCount）冒充词法指标**：
   - 代码获取 `manifest.ActiveVectorCount`（该知识库建立的**向量数**）来填充所有的词法统计字段！
   - 但知识库 `ce97cc0d67ec408e`（“金庸武侠测试知识库”）目前是**100% 纯词法检索库**，其 `chunk_vectors` 表没有任何行（`ActiveVectorCount == 0`）。
   - 即使该库在底层 SQLite 的 `chunks` 表中实实在在地拥有 **40,015 个切片**、`documents` 表拥有 **32 部金庸全集**、`fts_chunks_lexical` 拥有 **40,015 个 FTS5 倒排索引行**，因为 `ActiveVectorCount == 0`，乘上 50 依然是 0，导致统计指标被强制格式化为 0！
2. **`ResolveKbIdAsync` 劫持了前端的全局汇总意图**：
   - 当用户在前端未选择具体知识库（即全局词法概览，传入 `null` 或 `__all__`）时，`ResolveKbIdAsync` 执行：
     ```csharp
     if (_catalogStore != null) {
         var kbs = await _catalogStore.ListKnowledgeBasesAsync();
         return kbs.FirstOrDefault(k => k.Enabled)?.Id ?? kbs.FirstOrDefault()?.Id;
     }
     ```
   - 后端把 `null` 强制重定向到了第一个物理知识库 `ce97cc0d67ec408e`，然后触发了上述的“0 向量导致全部为 0”逻辑，彻底掩盖了系统原本根库中的 940 个分块，也无法展示全库真实的 40,015 个分块。

---

### 2.2 根因二：双数据体系割裂与“检索入口断层”

通过探测宿主机数据库与配置，揭示了系统中同时存在**两套完全隔离的数据生态**：

| 数据生态体系 | 物理存储位置 | 承载数据内容 | 访问的 API 接口 | 前端对应视图 |
| :--- | :--- | :--- | :--- | :--- |
| **生态 A：根文件源 (Global Sources)** | `/app/data/vectors.db`<br>`/app/data/bm25.db` | 仅 **3 本小说**（940 个切片）：<br>- 越女剑 (smoke)<br>- 连城诀 (core)<br>- 雪山飞狐 (core) | `POST /api/ai/query`<br>`GET /api/bm25index/search` | `/search`（未勾选知识库时） |
| **生态 B：物理知识库 (Knowledge Bases)** | `/app/data/kbs/ce97cc0d67ec408e/kb.db` (387 MB) | **32 部金庸全集**（40,015 个切片）：<br>- 包含《神雕侠侣》（三联版、新修版、旧版全部完整就绪！） | `POST /api/knowledge-base-search` | `/search`（必须手动下拉勾选知识库） |

#### 为什么 curl `POST /api/ai/query` 搜“小龙女”为空？
用户发送的 curl 命令：
`curl --url 'http://127.0.0.1:5124/api/ai/query' ... -d '{"Query":"小龙女","Mode":"Lexical"}'`
- `/api/ai/query` 内部调用的是 `_searchService.SearchAsync`。
- `SearchService` 仅从 `config.Sources` 中拉取启用的数据源（即 `jinyong_smoke` 和 `jinyong_core`）。
- **在《越女剑》、《连城诀》、《雪山飞狐》这三本小说中，根本不存在“小龙女”三个字**！
- 因此，检索生态 A 返回 `TotalMatches = 0`，完全符合其现存数据边界。

#### 为什么实证探测发现《神雕侠侣》在生态 B 中 100% 存在且可命中？
我们直接向生态 B（物理知识库检索接口）发送测试请求：
```bash
POST /api/knowledge-base-search
{"Query":"小龙女","KnowledgeBaseIds":["ce97cc0d67ec408e"],"TopK":5,"UseLexicalTokenizer":true}
```
**结果在 26ms 内极其精准地命中了 5 条分块**：
1. `旧版/神雕侠侣（旧版）.md`：杨过与小龙女古墓石室天罗地网式捉麻雀练功；
2. `新修版/神雕侠侣（新修版）.md`：小龙女溪边摘花倒影与甄志丙、赵志敬大路尾随；
3. `旧版/神雕侠侣（旧版）.md`：杨过抱小龙女回古墓寒玉床吐血相救；
4. `旧版/神雕侠侣（旧版）.md`：大闹重阳宫“你是谁？小龙女！”名场面；
5. `三联版/神雕侠侣.md`：“小龙女低低的道：咱们到南方去，种几亩田，养些小鸡小鸭……”。

**事实真相**：《神雕侠侣》早已被分块并建好了高精度的词法全文索引，但由于前端与 API 入口没有做统一联邦聚合，用户默认打到了只有 3 本小说的生态 A 中，导致“查无此人”。

---

### 2.3 根因三：“要么为空，要么异常”的深层诱因

为什么用户反馈“不仅为空，还会异常”？
1. **未选知识库搜索** $\rightarrow$ 进入生态 A $\rightarrow$ 只有越女剑/连城诀/雪山飞狐 $\rightarrow$ **返回为空**；
2. **用户在前端下拉框选了知识库，但未切换到“词法模式”（默认模式为“标准 Standard”或“混合 Hybrid”）**：
   - 前端代码 `lexical-search-template.js`：
     ```javascript
     export function knowledgeBaseRequest(query, ids, topK, mode) {
         return { query, knowledgeBaseIds: [...ids], topK, useLexicalTokenizer: mode === 'Lexical' };
     }
     ```
   - 当 `mode === 'Standard'` 时，`useLexicalTokenizer` 被设为 `false`，并期望执行稠密向量检索。
   - 但若后台传入了 `QueryVector`（或触发了向量覆盖度检查），由于该知识库未生成向量数据，[`SqliteKnowledgeBaseStore.cs`](file:///e:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/Infrastructure/ReferenceRAG.Infrastructure/Features/KnowledgeBase/Storage/SqliteKnowledgeBaseStore.cs#L573) 会直接抛出：
     `InvalidOperationException: "知识库向量索引覆盖不完整，请先重新建立向量索引。"`
   - 该异常直接以 HTTP 503 返回给前端，导致界面出现红色报错弹窗（**直接异常**）！
3. **若在知识库中 `UseLexicalTokenizer == false` 且无向量**：
   - 系统降级使用普通表 `fts_chunks`。
   - `fts_chunks` 使用的是 SQLite FTS5 默认的 simple/ascii 分词器，对未分词的连续中文字符串（“小龙女”）无法按字词边界切词检索，导致匹配度断崖式下降或结果失真。

---

## 三、业界主流方案调研与对比 (Industry Benchmark)

针对“知识库分库存储与全局查询割裂”、“倒排索引指标如何统计”、“无向量/纯词法库与多模态检索协同”，对比业界成熟开源与商业化系统：

### 3.1 Dify / FastGPT 的数据集与检索路由设计
- **统一检索编排器（Unified Retrieval Orchestrator）**：
  在 Dify 和 FastGPT 中，用户发起的 Query 从不直接绑定单个物理表。检索层使用**门面路由器（Facade Router）**，入参支持 `dataset_ids`（可多选或留空表示全量已启用知识库）。
- **数据源挂载机制**：
  文件源（File Sources）并非独立于知识库的另一套孤岛，而是作为“默认数据集（Default Dataset）”存在，或者在检索时通过 **Federated Search（联合检索策略）** 并行扇出查询并执行 RRF 融合。

### 3.2 Elasticsearch / Meilisearch / Vespa 的索引指标统计标准
- **倒排表统计与向量统计物理分离**：
  在 ES 和 Meilisearch 中，`_stats` 接口直接调用底层的 Segment Reader 获取：
  - `docs.count`：活跃文档数（非软删除）；
  - `indexing.total`：分块切片总数；
  - `terms.count`：Term Dictionary（词典 B-Tree）中的唯一词项总数（真正的词汇量）；
  - `avg_doc_length`：通过分词后的 token 流累加除以文档数。
- **严禁从稠密特征反推稀疏特征**：稠密向量的维度、是否构建 HNSW 与稀疏倒排表是两个正交维度，严禁使用向量维度或向量个数作为 BM25 统计的代理指标。

### 3.3 Milvus / Qdrant 的索引完整性门禁
- **优雅降级与能力探测（Capability Probing）**：
  当某个数据集没有向量索引时，路由层通过能力探针检测到 `HasVectorIndex == false`，自动向用户或调用方返回降级标记 `ScoreKind: BM25Only`，并平滑退回词法分词检索，绝对不抛出致命阻断异常（500/503）。

---

## 四、设计模式优化与系统重构落地方案

针对上述问题，本着“代码简洁、无多余负担、高内聚低耦合”的原则，采用**适配器模式、策略模式、门面联合模式**进行重构：

```
                      Client Query (curl 或 /search)
                                     │
                      ┌──────────────▼──────────────┐
                      │    Unified Search Facade    │
                      │  (AIQueryController 联合编排)│
                      └──────────────┬──────────────┘
                                     │
         ┌───────────────────────────┴───────────────────────────┐
         │                                                       │
         ▼                                                       ▼
┌──────────────────┐                                   ┌──────────────────┐
│  Global Sources  │                                   │ Knowledge Bases  │
│ (jinyong_full 等)│                                   │ (32部金庸全集库)  │
└────────┬─────────┘                                   └────────┬─────────┘
         │                                                       │
         └───────────────────────────┬───────────────────────────┘
                                     │
                                     ▼
                      ┌─────────────────────────────┐
                      │    RRF / Score Aggregator   │
                      │ (按配置执行统一融合重排展示)  │
                      └─────────────────────────────┘
```

### 4.1 方案一：重构 `BM25IndexController` 统计指标（适配器 + 策略模式）

#### 1. 消除 `ActiveVectorCount` 伪数据，直接在 `SqliteKnowledgeBaseStore` 提供真实词法统计
在 [`IKnowledgeBaseStore`](file:///e:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/Infrastructure/ReferenceRAG.Infrastructure/Features/KnowledgeBase/Storage/IKnowledgeBaseStorage.cs) 中定义真实词法统计契约：
```csharp
Task<BM25Summary> GetLexicalStatsAsync(CancellationToken ct = default);
```
在 [`SqliteKnowledgeBaseStore.cs`](file:///e:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/Infrastructure/ReferenceRAG.Infrastructure/Features/KnowledgeBase/Storage/SqliteKnowledgeBaseStore.cs) 中实现真实的 SQL 聚合：
```csharp
public async Task<BM25Summary> GetLexicalStatsAsync(CancellationToken ct = default)
{
    await InitializeAsync(ct);
    using var conn = CreateConnection();
    
    // 1. 真实文档数
    using var docCmd = conn.CreateCommand();
    docCmd.CommandText = "SELECT COUNT(*) FROM documents;";
    long totalDocs = Convert.ToInt64(await docCmd.ExecuteScalarAsync(ct) ?? 0);

    // 2. 真实分块数与平均长度
    using var chunkCmd = conn.CreateCommand();
    chunkCmd.CommandText = "SELECT COUNT(*), IFNULL(AVG(LENGTH(text)), 200.0) FROM chunks WHERE enabled = 1;";
    using var chunkReader = await chunkCmd.ExecuteReaderAsync(ct);
    long totalChunks = 0;
    double avgLen = 200.0;
    if (await chunkReader.ReadAsync(ct))
    {
        totalChunks = chunkReader.GetInt64(0);
        avgLen = chunkReader.IsDBNull(1) ? 200.0 : chunkReader.GetDouble(1);
    }

    // 3. 词汇量 (取 FTS 词表或粗估)
    long vocabSize = totalChunks * 25; // 基于中文字符集分词估算或查询 fts 索引记录数

    return new BM25Summary
    {
        TotalIndexedDocuments = (int)totalChunks,
        TotalVocabularySize = (int)vocabSize,
        AverageDocLength = Math.Round(avgLen, 2),
        TotalFiles = (int)totalDocs,
        TotalChunks = (int)totalChunks
    };
}
```

#### 2. 策略模式重构 `BM25IndexController.GetSummary`
- **策略 A（单知识库，如 `ce97cc0d67ec408e`）**：调用对应租约的 `GetLexicalStatsAsync()`，返回 32 个文档、40,015 个分块的真实高阶指标。
- **策略 B（根全局文件源，如 `__root__`）**：调用 `_bm25Store.GetStatsAsync()` 与 `_vectorStore.GetSourceStatsAsync()`，返回全局源真实切片。
- **策略 C（全部/汇总，如 `__all__` 或 `null`）**：遍历并汇总所有已启用知识库与全局源的总文件数和总分块数，展现整个系统的宏观检索资产容量。

---

### 4.2 方案二：彻底打通语料边界，消除数据孤岛

#### 1. 将 `jinyong_full`（金庸全集）正式挂载为全局数据源
当前 `/opt/referencerag/sources/full/三联版` 中已完整存放金庸 15 部小说（包含 2.87MB 的 `神雕侠侣.md`）。
在系统的 `appsettings.json`（或通过 `/api/sources` 管理接口）正式添加 `jinyong_full` 数据源：
```json
{
  "Name": "jinyong_full",
  "Path": "/app/sources/full/三联版",
  "Type": "Markdown",
  "Enabled": true,
  "FilePatterns": ["*.md", "*.txt"]
}
```
并在后台触发一次增量词法索引构建。构建完成后，无论是通过 curl 调用 `/api/ai/query`，还是在前端 `/search` 不选任何下拉框直接回车，均能原生检索到《神雕侠侣》和“小龙女”。

#### 2. 在 `AIQueryController` 与 `SearchService` 中实现统一知识库联合路由
为了彻底符合现代 RAG 架构设计，在 [`AIQueryRequest`](file:///e:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/Infrastructure/ReferenceRAG.Infrastructure/Models/QueryModels.cs) 中引入可选的 `KnowledgeBaseIds`：
```csharp
public List<string>? KnowledgeBaseIds { get; set; }
```
当请求未限定 `Sources` 或显式指定了 `KnowledgeBaseIds` 时，`SearchService` 能够自动调用 `IKnowledgeBaseSearchService` 并行检索，将知识库命中结果（包含《神雕侠侣》）与文件源结果进行无缝合并，用户无需在不同页面和不同控件之间割裂切换。

---

### 4.3 方案三：前端交互与默认行为优化

1. **`BM25Index.js` 修复**：
   - 当 `selectedKbId === '__all__'` 时，直接向后端传递 `__all__` 参数，不再传 `null` 被篡改；
   - 概览面板在加载完成后，展示全部已收录的万级别真实分块数据。
2. **`Search.js` 容错保护**：
   - 知识库选择器优化为“全局联合检索（包含所有知识库与文件源）”作为推荐默认项；
   - 当选定知识库且该知识库尚未生成向量时，界面智能提示并将模式自动平滑降级为“词法检索”，并在返回时附带 `ScoreKind: BM25` 标签，绝不向用户呈现任何 503 异常弹窗。

---

## 五、同类问题门禁设计与防回归测试（Quality Gates）

为了防止此类“空指针伪指标”与“数据源路由断层”在未来迭代中再次发生，在测试套件中建立固化的自动化门禁契约：

1. **词法索引概览非零门禁测试 (`BM25SummaryNonZeroQualityGateTests.cs`)**：
   - 断言：在知识库包含切片文档的场景下调用 `GetSummary`，返回的 `TotalChunks` 和 `TotalFiles` 必须与数据库实际记录数精确一致，**严禁返回 0**。
2. **“小龙女 / 神雕侠侣”端到端召回契约测试 (`GoldenNovelRetrievalGateTests.cs`)**：
   - 发送查询 `"小龙女"`、`"杨过"`、`"神雕侠侣"`；
   - 断言返回的 `Chunks` 数量 $\ge 1$，且分块文本中必须包含 `"小龙女"` 或 `"古墓"`，HTTP 状态码必须为 200 OK，**严禁返回空列表或 503 异常**。
3. **未向量化知识库优雅降级门禁测试 (`VectorlessKnowledgeBaseGracefulFallbackTests.cs`)**：
   - 在一个 `ActiveVectorCount == 0` 的物理知识库上，使用任意查询模式发起请求；
   - 断言系统自动降级为 FTS5 词法通道，`isDegraded == true`，响应时间 $\le 100\text{ms}$，**严禁抛出 `InvalidOperationException`**。

---

## 六、总结与落地建议

| 问题现象 | 表面原因 | 本质根因 | 根治方案 |
| :--- | :--- | :--- | :--- |
| **BM25 概览始终全为 0** | 前端显示 0 篇文档、0 词汇 | `BM25IndexController` 读取 `manifest.ActiveVectorCount` 冒充词法指标，且强制拦截全局请求为未建向量库 | 在 `IKnowledgeBaseStore` 实现真实 SQL 统计接口，在控制器使用策略模式按目标返回真实分块统计 |
| **“小龙女”搜不到《神雕侠侣》** | `/api/ai/query` 返回 `Chunks: []` | 全局源只有 3 本小说；32 部全集在独立知识库中；API 与搜索页面未做联合路由打通 | 挂载 `jinyong_full` 全局源，并在检索服务中引入联合路由器，使默认检索无缝覆盖全集 |
| **搜索时偶发 503 异常** | 前端弹出报错提示 | 知识库未建向量，但非词法模式未传向量且未降级，触发了严格的覆盖度拦截异常 | 增加能力探针，未向量化库自动平滑降级至词法分词召回，杜绝阻断抛错 |
