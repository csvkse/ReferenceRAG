# 长句查询的同类退化、词法 vs 向量本质差异与 SAG-Wiki 借鉴

> **文档状态**：调研完成，方案分级给出（P0 可立即落地，P1/P2 待排期）。**本文档不改代码**。
> **前置文档**：`docs/lexical-query-degeneration-and-first-query-18s-root-cause-and-fix.md`（词法侧 18 秒事故的根因、修复、门禁与生产实测）
> **参考项目**：`D:\D\【3】工作项目经验-WorkTest\Rust\【参考项目】\SAG-Wiki-main`（Rust + SQLite FTS5 本地检索，无向量）
> **调研日期**：2026-11-14

---

## 目录

- [一、执行摘要](#一执行摘要)
- [二、先把问题定义清楚：什么叫"同类问题"](#二先把问题定义清楚什么叫同类问题)
- [三、Q1：本系统是否还存在同类问题](#三q1本系统是否还存在同类问题)
- [四、Q2：词法 vs 向量在长句上的本质差异](#四q2词法-vs-向量在长句上的本质差异)
- [五、Q3：SAG-Wiki 是怎么做的（源码级）](#五q3sag-wiki-是怎么做的源码级)
- [六、头脑风暴：可行性与欠缺](#六头脑风暴可行性与欠缺)
- [七、设计模式方案](#七设计模式方案)
- [八、现有基础设施与第三方库复用](#八现有基础设施与第三方库复用)
- [九、业界主流方案横向对比](#九业界主流方案横向对比)
- [十、演进方案：P0 / P1 / P2](#十演进方案p0--p1--p2)
- [十一、新增门禁设计](#十一新增门禁设计)
- [十二、风险与"不做什么"](#十二风险与不做什么)
- [十三、附录](#十三附录)

---

## 一、执行摘要

### 1.1 三个问题的直接回答

| 问题 | 回答 |
| :--- | :--- |
| **① 是否还存在同类问题？** | **存在，共 9 处**（L1~L5 / V1~V3 / F1），其中 **4 处是高危**：<br>**(L1)** `MaxCandidateRatio=0.35` 是**比例**而非绝对上限 —— 语料放大到 100 万块时预算变成 35 万行（200k 合成语料实测 2.5µs/行，外推 35 万行 ≈ **875ms**；若剪枝完全失效（75% 语料命中）则是 75 万行 ≈ **1.85s**）<br>**(L2)** 兜底层 / 召回地板层**完全不受候选预算约束** —— 纯虚词查询（「她的他」）在 57k 金庸库上 `est=25983/57733`（**45% 语料，已超 35% 预算但代码不处置**），`kb.fts.match` **181ms**，是正常查询（15ms）的 **12 倍**<br>**(V1)** 向量路径**比词法更严重**：`chunk_vectors` 无索引、无 `LIMIT`、无候选预算、`ORDER BY` 在内存里做，且把**全库正文读进内存**并构造 O(N) 个 `RecallItem` |
| **② 词法 vs 向量有本质区别吗？不如向量吗？** | **有本质区别，但"不如向量"是错的。** 两者退化方向**相反**：词法的成本随**查询长度**爆炸（候选集膨胀），精度反而随长度上升（召回↑）；向量的成本与查询长度**无关**（恒为 O(N)），但精度随长度**下降**（单向量被多概念平均 → 语义稀释）。<br>**关键实验**：FTS5 中出现在 95% 文档里的词，其对 bm25 的贡献是 **1e-6**（实测）—— 这意味着**词法剪枝几乎不损失排序质量**，剪掉的正是"分数上等于零"的词。向量没有可剪的东西，只能砍候选。<br>真正的问题不是"选哪个引擎"，而是**查询意图错配**：用户输入的是"原文引用"，系统按"关键词 OR"处理。 |
| **③ SAG-Wiki 怎么做的？能借鉴吗？** | 它**没有向量**，全靠 4 层防护：`take(80)` 词元上限 / **稀有词子集 + BM25 上界提前返回** / 扩展轮**每词一条 MATCH 并行 + 交错合并** / 只 hydrate top-k。<br>**可借鉴 3 项**：每词拆分并行召回、`retrieval_vocabulary` 预计算词表统计、只回表 top-k。<br>**不可直接移植 1 项**：BM25 上界剪枝需要**自研 rank 函数**共享 `avgdl/IDF`（它为此写了 C 扩展 `sag_rank.c`），且其判据 `s_k > B` **数学上不充分**（应比较 `s_k - s_{k+1} ≥ B`），我们评估后认为**收益不如"AND 分层 + 覆盖率重排"**。<br>**它的反例**：快速搜索路径 `search_files_page` 仍是全 OR + 全量 `COUNT(DISTINCT)` + 窗口函数全量物化 —— 和我们修之前一模一样。 |

### 1.2 一句话结论

> 上一轮修的是"词法查询构造错了"。这一轮要修的是**"成本模型错了"**：
> 词法把"候选集大小"当成了副产品，向量把"全表扫描"当成了实现细节，
> 融合把"两路叠加"当成了默认。三者都缺少同一个东西——**一个显式的、可观测的、有硬上限的召回预算**。

### 1.3 建议优先级

| 优先级 | 事项 | 一句话理由 | 预估收益 |
| :--- | :--- | :--- | :--- |
| **P0-1** | 给候选集加**绝对上限**（`min(ratio×N, 5_000)`），并对兜底层/地板层同样生效 | 唯一的"语料放大即复现"风险 | 1M 语料：35 万行 ≈ 875ms → 5 千行 ≈ **12ms** |
| **P0-2** | 新增 **AND 分层**（稀有锚点全部必须命中），置于 OR 层之前 | 多约束查询的候选集可降 1~2 个数量级（实测 370ms → 42ms） | 候选集 ↓ 90%+ |
| **P0-3** | **覆盖率重排**（命中词数优先），并把 `Coverage` 透出到 DTO | 同时解决"长句精度"与上一轮遗留 P1（兜底结果被误认为精确命中） | 精度 ↑；白屏/误导 ↓ |
| **P1-1** | `lexical_term_stats(term, df)` **增量维护的 df 表** | 把"64 次串行 COUNT"变成"1 次索引查找"，且拿到**精确** df | plan 阶段 64ms → <1ms |
| **P1-2** | 向量路径**两阶段 + 有界堆 + 不驻留正文** | 与词法对称化，消除 O(N) 内存与 O(N log N) 排序 | 内存 O(N)→O(k)；排序 O(N log N)→O(N log k) |
| **P1-3** | 给词法/向量召回加**超时 + 取消** | 目前只有 `CancellationToken`，慢查询无法中断 | 消除"18s 无响应"类事故 |
| **P2-1** | **查询意图分类**（原文引用 / 多约束 / 主题描述）+ 策略路由 | 从"一个引擎打天下"变成"按意图选引擎" | 精度与延迟同时改善 |
| **P2-2** | 表达式**编译器适配器** + **召回预算状态机** | 把隐式 `foreach+break` 变成显式、可测、可观测 | 可维护性 |
| **P2-3** | 向量 ANN（Vectorlite/HNSW）或 sqlite-vec `vec0` 替换 BLOB 暴力扫描 | 百万级向量的唯一出路 | 100K+ 向量 3~100× |

---

## 二、先把问题定义清楚：什么叫"同类问题"

### 2.1 一个统一的形式

上一轮的事故可以抽象成一句话：

> **查询的"候选集大小"没有上界，且与 `topK` 无关。**

用成本模型写出来：

```
词法:  cost_lexical ≈ O( |M(Q)| )            |M(Q)| = 命中文档数，随 |Q| 与词频单调增长
向量:  cost_dense   ≈ O( N · d )             N = 全库向量数，d = 维度，与 |Q| 无关
融合:  cost_hybrid  ≈ cost_lexical + cost_dense   （无联合预算，退化叠加）
```

"同类问题"= **任何一条路径上，`|候选集|` 或 `N` 未被硬性约束，导致成本随语料规模线性/超线性增长，而 `topK` 的 `LIMIT` 无法阻止它。**

注意一个反直觉的事实（本轮实测确认）：

> **`LIMIT` 只能砍"回表"，砍不了"打分"。**

在 200,000 块的合成语料上（12 个高频虚词，各命中 ~75%）：

| SQL | 耗时 |
| :--- | ---: |
| `... MATCH '全OR' ORDER BY rank LIMIT 10` | **369.71 ms** |
| `... MATCH '全OR' ORDER BY rank LIMIT 100000` | **413.31 ms** |

`LIMIT 10` 与 `LIMIT 100000` 只差 12%。因为 FTS5 的 `rank` 是**逐行**计算的，且需要**全局排序**才能知道前 10 名是谁。
这解释了为什么上一轮的"两阶段改造"只把 16.7s 降到 0.97s —— 它砍掉的是 `JOIN chunks`（214MB），**没有砍掉 bm25 打分**。

> **推论**：要让词法变快，唯一有效的杠杆是**让命中集本身变小**（更严的表达式），
> 而不是"加 LIMIT""加窗口""加缓存"。这一点决定了后面所有方案的方向。

### 2.2 三类退化的判别式

| 类型 | 判别式 | 本系统命中 |
| :--- | :--- | :---: |
| **词元爆炸** | 表达式是"多词 OR"，且 `Σdf ≫ topK` | ✅ |
| **全表扫描** | SQL 里没有 `LIMIT` / 没有 ANN 索引 / 排序在内存 | ✅ |
| **延迟叠加** | 两条路径各自无预算，串行或并行相加 | ✅ |

---

## 三、Q1：本系统是否还存在同类问题

### 3.1 审计范围

| 路径 | 入口 | 关键实现 |
| :--- | :--- | :--- |
| 词法（KB 内） | `SqliteKnowledgeBaseStore.RecallAsync` | `:1612-1955` |
| 词法（FTS 执行） | `ExecuteLexicalQueryInternalAsync` | `:1493-1610` |
| 向量（KB 内，SIMD） | `InMemoryTensorProjectionPool.TryScanSimd` | `:173-236` |
| 向量（KB 内，SQL 回落） | `RecallAsync` 向量分支 | `:1878-1926` |
| 融合 | `AdaptiveQueryAwareFusion.FuseWithZScore` | `:61-105` |
| 跨库 | `KnowledgeBaseSearchService` + `RrfSearchFusionStrategy` | — |

> 生产默认知识库 `dadd847d18b54162` 当前是 `Mode: PureLexical`、`EmbeddingSpecId: spec_lexical_only`，
> **向量路径未启用**。所以下面的 V 类问题是**潜伏的**：一旦切到 `Hybrid`/`Deep` 就立刻暴露。

### 3.2 词法路径：4 个残留缺口 + 1 个缺失层

#### L1（高危）`MaxCandidateRatio` 是比例，不是绝对上限

```csharp
// LexicalQueryPlanner.cs:34
public double MaxCandidateRatio { get; init; } = 0.35;

// LexicalQueryPlanner.cs:127
var budget = Math.Max(1.0, opt.MaxCandidateRatio * corpusSize);
```

`35% × 24,776 = 8,671` 行 → 可接受；
`35% × 1,000,000 = 350,000` 行 → **按实测 2.5µs/行外推 ≈ 875ms，退化复现**。

`candidateWindow`（`SqliteKnowledgeBaseStore.cs:1522`，`clamp(TopK*8, 80, 2000)`）看起来像上限，
但它作用在 `LIMIT` 上，**只限制回表，不限制打分**（见 §2.1 实测）。

**结论**：这是一个"语料一放大就复现"的定时炸弹，而本项目定位是"本地知识库"，语料从 2 万到 100 万是必然路径。

#### L2（高危）兜底层与地板层完全不受预算约束

```csharp
// LexicalQueryPlanner.cs:140-151 —— 全句都是虚词时的兜底
if (anchors.Count == 0)
{
    anchors = tokens.Distinct(...).OrderBy(Df).Take(opt.MaxCommonFallbackTerms).ToList();
    estimated = anchors.Sum(t => (long)Df(t));   // ← 只记录，不约束
    usedFallbackAnchors = true;
}
```

触发条件：**所有词都是单字**（`:119` 要求 `t.Length >= 2` 才能进锚点层）或所有词 df > 15%。
此时 `anchors` 取 df 最小的 3 个 —— 在中文里就是「他/她/手/着/的」这类，
`OR` 起来命中 **~95% 语料**，`estimated` 可能是 `0.95N`，但**没有任何代码因这个数字而改变行为**。

召回地板（`:195-208`）同理：它刻意"回落虚词"，且注释明确说"仍受两阶段候选窗口约束"——
但如 §2.1 所述，候选窗口**不约束打分**。

**结论**：这是上一轮"召回地板"的**正确性修复带来的性能回退面**。

**实测证据（开发容器 `<dev-host>:5124`，金庸库 `ce97cc0d67ec408e`，57,733 块）**：

| 查询 | `plan` 明细 | `kb.fts.match` | 总耗时 | 条数 |
| :--- | :--- | ---: | ---: | ---: |
| 「他笑嘻嘻的走上前去，拉着她的手」（有内容词） | `anchors=4 pruned=6 est=3570/57733` | 6~12 ms | **15 ms** | 10 |
| 「她的他」（纯虚词） | `anchors=3 pruned=0(-) est=25983/57733 near=0 fallback=common` | **181 ms** | **183 ms** | 10 |
| 「他的手」（纯虚词） | 同上形态 | — | **246 ms** | 10 |
| 「她的他」（冷态首查） | 同上 | — | **555 ms** | 10 |

三个要点：

1. `est=25983/57733` = **45% 语料**，**已经超过 `MaxCandidateRatio=0.35` 的预算，但代码不做任何处置** ——
   `estimated` 在 `:149` 被算出来，之后只用于回填 `LexicalQueryPlan`，**没有任何分支因它而改变行为**。
2. `pruned=0(-)` 说明这一层**没有剪掉任何词**（三个词全进了兜底锚点），所谓"兜底"实际就是"全句 OR"。
3. 181ms / 15ms = **12 倍**，且随语料线性增长 —— 这就是"同类问题"在金庸库上的直接体现。

> 注意它**不会**像 18 秒那样炸，因为两阶段把回表限制在 2000 行；但**打分仍覆盖 25,983 行**。
> 语料放大 10 倍 → 约 1.8s。这就是 L1 + L2 叠加后的真实风险曲线。

#### L3（中）df 统计是 N 次串行 `COUNT`

```csharp
// SqliteKnowledgeBaseStore.cs:973-1024 —— 逐词查询，串行
foreach (var term in terms) { ... COUNT(*) FROM (SELECT 1 ... MATCH @t LIMIT @cap) ... }
```

`ExtractLexicalTerms` 上限是 `Take(64)`（`:935`）。用户粘贴一整段话 → 64 个词 → **64 次索引查找**。
实测（200k 语料）单次带上限 `COUNT` 为 **7.53ms**；即便按 24,776 语料折算到 ~1ms，
64 词也要 **60ms 量级**，与"总耗时 52ms"同数量级。

**对比**：SAG-Wiki 用 8 个只读连接**并行**跑 df（`core/src/store.rs:2127-2157`），且结果进 LRU 缓存。

#### L4（中）NEAR 层门槛导致"原文引用"退化

```csharp
// LexicalQueryPlanner.cs:168
if (nearClauses.Count < 2) nearClauses.Clear();   // 少于 2 条就整层丢弃
```

后果：**两词查询永远无法生成 NEAR 约束**（最多 1 条），三词查询刚好 2 条。
而"原文引用"型查询（"走上前去"、"拉着她的手"）恰恰是 2~4 个词 —— 最需要短语约束的区间，反而被丢弃。

更关键的是 `:119` 的 `t.Length >= 2`：中文单字词（她/手/拉/着）**既不进锚点、也不进 NEAR**，
于是"拉着她的手"这种短句退化成"无约束"或"全虚词兜底"。

#### 补充：缺少 AND 层

当前阶梯是：

```
① (NEAR1 AND NEAR2) OR NEAR1 OR NEAR2     ← 只在 ≥2 条 NEAR 时存在
② anchors 的 OR                            ← 主力层
③ 原文短语（仅当 ①② 都为空）
④ 召回地板（仅当 estimated == 0）
```

**没有"所有稀有锚点必须命中"的 AND 层**。而 ES 的 `low_freq_operator=and` 正是这一层。
实测（200k 语料）：

| 表达式 | 耗时 |
| :--- | ---: |
| `(12 个高频词 OR)` | 369.71 ms |
| `(12 个高频词 OR) AND "稀有词"` | **42.37 ms** |

差 8.7 倍。AND 层的代价只是"可能 0 结果"——而 0 结果本来就会 fall through 到下一层。

### 3.3 向量路径：比词法更严重的同类问题

#### V1（高危）SQL 回落的向量召回是"无 LIMIT 全表扫描 + 全正文驻留"

```csharp
// SqliteKnowledgeBaseStore.cs:1878-1926
SELECT v.chunk_id, v.document_id, d.source_key, c.text,
       v.vector_blob, ...                       -- ← 连正文一起读
FROM chunk_vectors v
JOIN chunks c ON c.id = v.chunk_id
JOIN documents d ON d.id = c.document_id
WHERE c.enabled = 1 AND ...                     -- ← 没有 LIMIT，没有 ORDER BY
```

```csharp
while (await reader.ReadAsync(ct))
{
    var blob = (byte[])reader.GetValue(4);
    var sim = CosineSimilarity(request.QueryVector.AsSpan(), MemoryMarshal.Cast<byte, float>(blob.AsSpan()));
    if (sim > 0.0) vectorItems.Add(new RecallItem(..., Text: text, ...));   // ← 每行一个对象 + 正文
}
vectorItems = vectorItems.OrderByDescending(v => v.Score).Take(request.TopK).ToList();  // ← 内存排序
```

三宗罪：
1. **无 `LIMIT`**：命中多少行就读多少行；
2. **正文随行读**：`c.text` 被 `GetString(3)` 物化，即**全库正文进内存**；
3. **O(N) 对象 + O(N log N) 排序**：`sim > 0` 的行全部构造 `RecallItem` 后再排序。

按 57,733 块 × 1024 维 × 4B ≈ **236MB** 向量 + 约 **57MB** 正文，**每次查询**。

#### V2（高危）SIMD 快路径解决了 IO，但没解决复杂度

`InMemoryTensorProjectionPool` 把 BLOB 变成连续矩阵，确实把 IO 干掉了（注释称 10 万向量 3.5ms）。
但看实现：

```csharp
// InMemoryTensorProjectionPool.cs:193
var scoredList = new List<(int Index, double Score)>(Math.Min(count, 1024));
for (int i = 0; i < count; i++) { ... if (sim > 0.0) scoredList.Add((i, sim)); }
var topScored = scoredList.OrderByDescending(x => x.Score).Take(topK);   // ← 全量排序
```

- **内存**：`scoredList` 是 O(N) 个元组（16B/个）→ 100 万向量 = 16MB/次查询；
- **时间**：`OrderByDescending().Take(k)` 是**全量排序** O(N log N)，不是 O(N log k)；
- **常驻内存**：`TensorSnapshot.Texts` 持有**全库正文**（`:36`）→ 57k 块约 57MB，100 万块约 1GB。

**这是与词法最刺眼的不对称**：词法做了"两阶段（先取 id+分数，再回表）"，
向量却把"取分数"和"取正文"绑在一起，还顺手全量排序。

#### V3（中）无 ANN、无候选预算

`chunk_vectors` 只有 `PRIMARY KEY(chunk_id)` 和 `idx_chunk_vectors_doc(document_id)`（`:136-144`），
**没有任何向量索引**。全库路径的 `SqliteVectorStore` 用了 sqlite-vec 的 `vec0`，但 `vec0` 本身也是
**暴力 KNN（SIMD）**，不是 ANN —— 它把复杂度从"C# 逐行"降到"C 逐行"，量级不变。

### 3.4 融合路径：无联合预算

`RecallAsync` 里词法与向量是**串行**执行的（`:1800-1949`），
`AdaptiveQueryAwareFusion.FuseWithZScore`（`:61-105`）只负责合并分数。
两路各自无硬上限 → **退化叠加**：词法全库 + 向量全表 = 两条 18 秒相加。

### 3.5 实测证据汇总

> 探针环境：本地 SQLite 3.53.1（FTS5），Python `sqlite3`，200,000 块合成语料，
> 12 个高频虚词各命中 ~75%，5 个稀有内容词各命中 0.1%。脚本见 §13.1。

| 场景 | 耗时 | 说明 |
| :--- | ---: | :--- |
| 全 OR（12 虚词）`ORDER BY rank LIMIT 10` | **369.71 ms** | 退化形态，`LIMIT` 无效 |
| 全 OR（12 虚词）`LIMIT 100000` | 413.31 ms | 与上面只差 12% → 证明打分是主成本 |
| 全 OR（12 虚词）`COUNT(*)` | 56.50 ms | 全量计数 |
| 全 OR 带上限 `COUNT(... LIMIT 30000)` | **7.53 ms** | 现行 df 统计手法，有效 |
| 锚点 OR（5 稀有词）`LIMIT 10` | **0.35 ms** | 剪枝后 |
| `(12 虚词 OR) AND "稀有词"` | **42.37 ms** | AND 层，比纯 OR 快 8.7× |
| 短语 `"de ta"`（两高频词相邻） | 116.62 ms | **短语约束对高频词无效** |
| `NEAR("de" "ta", 5)` | 120.15 ms | 同上 |
| 覆盖率重算（2000 候选 × 12 词，内存 `Contains`） | **2.12 ms** | 覆盖率重排完全可行 |
| 覆盖率重算（2000 候选 × 12 词，SQL 逐词 `IN`） | 199.56 ms | ❌ SQL 方式不可行，必须走内存 |

两条重要副产物：

1. **短语/NEAR 只对稀有词便宜**。用高频词做短语约束，等价于"扫全库再验证相邻"，比 OR 还慢。
   这解释了 `LexicalQueryPlanner.cs:156` 注释里"避免用高频词做短语约束"的正确性。
2. **覆盖率必须在内存里算**（候选正文已经在手），SQL 逐词 `IN (2000 ids)` 要 200ms，差 94 倍。

### 3.6 Q1 结论

| 编号 | 缺口 | 严重度 | 是否"同类问题" |
| :--- | :--- | :---: | :---: |
| L1 | 候选预算是比例非绝对上限 | **高** | ✅ 词元爆炸 |
| L2 | 兜底/地板层无预算 | **高** | ✅ 词元爆炸 |
| L3 | df 统计 64 次串行 COUNT | 中 | ⚠️ 常数放大 |
| L4 | NEAR 层门槛过严 + 单字词排除 | 中 | ❌ 是精度问题 |
| L5 | 缺 AND 层 | 中 | ✅ 可大幅缩小候选集 |
| V1 | 向量 SQL 全表扫描 + 全正文驻留 | **高** | ✅ 全表扫描 |
| V2 | SIMD 全量排序 + O(N) 内存 + 正文常驻 | **高** | ✅ 全表扫描 |
| V3 | 无 ANN、无候选预算 | 中 | ✅ 全表扫描 |
| F1 | 融合无联合预算 | 中 | ✅ 延迟叠加 |

---

## 四、Q2：词法 vs 向量在长句上的本质差异

### 4.1 成本模型：一个随查询长度爆炸，一个恒定

| 维度 | 词法（BM25 / FTS5） | 向量（dense） |
| :--- | :--- | :--- |
| 单次查询的计算量 | `O(\|M(Q)\|)`：命中文档数 | `O(N·d)`：全库向量数 × 维度 |
| **与查询长度的关系** | **单调递增**：词元越多 → OR 越宽 → 命中越多 | **无关**：长句和短句都是一次前向 + 一次扫描 |
| 最坏情况 | 命中全库（本事故：107,366 行） | 全表扫描（本系统现状：57,733 行 × 1024 维） |
| 可否用 `LIMIT` 加速 | **否**（需全局排序） | **否**（需全部比较才能确定 top-k） |
| 可加速的手段 | 更严的表达式（AND/NEAR/稀有锚点） | ANN 索引 / 有界堆 / 预过滤 |

**所以：在"延迟"这个维度上，长句查询向量确实更稳。** 这是"不如向量"这个直觉的来源，也是它唯一成立的地方。

### 4.2 精度：退化方向相反

| 维度 | 词法 | 向量 |
| :--- | :--- | :--- |
| 长句时发生什么 | 变成"更宽的 OR" → **召回↑、精度↓**，但**每个词的证据仍可逐项核对** | 多个概念被**平均成一个点** → 语义稀释（query dilution），**具体约束被抹平** |
| 否定 / 数字 / 版本号 / 专名 | 精确（`"2024-10"` 就是 `2024-10`） | 容易丢失（"不含 X"和"包含 X"在向量空间里可能很近） |
| 同义词 / 跨语言 / 口语 | 弱（要靠扩展表） | 强 |
| 可解释性 | 高（哪个词命中、命中几次） | 低（只有一个余弦值） |
| 失败形态 | 返回很多"部分相关"的结果 | 返回"主题对但细节错"的结果 |

**关键判断**：

> 长句查询在词法上是**成本问题**（可以靠工程解决），在向量上是**信息问题**（不可逆）。
> 把长句丢给向量，等于**用一个有损压缩去替代一个只是慢的精确检索**。

### 4.3 关键实验：高频词对 bm25 的贡献是 1e-6

这是本轮最有价值的一个实测（脚本见 §13.2）。1,000 篇文档，`de` 出现在 950 篇（95%），`rare` 出现在 10 篇（1%）：

```
MATCH 'de'          -> [(11, -1e-06), (12, -1e-06), ...]
MATCH 'rare'        -> [(1, -3.73589), (2, -3.73589), ...]
MATCH 'de OR rare'  -> [(1, -3.735891), ...]

doc1 同时含 de + rare:
  score(rare)        = -3.735890
  score(de OR rare)  = -3.735891
  => de 的贡献 = -0.0000008  （占比 0.0000%）
```

原因：FTS5 的 IDF 是 `log((N - n + 0.5) / (n + 0.5))`，当 `n > N/2` 时为负，**FTS5 源码把它截断为 `1e-6`**。
（用同一实验反解可验证：`rare` 的 `log((1000-10+0.5)/(10+0.5)) = 4.547`，
乘上 BM25 长度归一因子 `0.8218` 恰好等于观测到的 `3.7359`。）

**推论（这是整篇文档最重要的一条）**：

> 剪掉高频词，**几乎不改变排序**——因为它剪掉的正是"分数上等于零"的词。
> 剪枝的唯一代价是**召回**（只命中被剪词的那批文档会掉出候选），而不是**排序质量**。
> 这正是"召回地板"存在的理由，也是"剪枝 + 地板"这个组合在数学上站得住的原因。

反过来看向量：**它没有"可以剪的词"**。一个长句被编码成一个向量，你无法说"这个维度不重要"。
所以向量的候选集只能靠 ANN 或预过滤来砍，砍不动就只能全扫。

### 4.4 长句查询其实有三种意图，需要三套解法

这是本轮的第二个关键判断。把"长句"当成一类问题是错的，它至少分三类：

| 意图 | 特征 | 典型输入 | 最优解法 | 现状 |
| :--- | :--- | :--- | :--- | :--- |
| **A. 原文引用** | 像一句话、含标点、无疑问词、用户"记得原文里有这么一句" | 「他笑嘻嘻的走上前去，拉着她的手」 | **短语/NEAR + 覆盖率排序**（命中词数优先）。词法完胜，且极快 | ❌ 被当成"关键词 OR" |
| **B. 多约束** | 含数字/专名/版本/限定词，本质是**合取** | 「2024年10月 自考13000 真题 选词填空 副词辨析」 | **AND 分层 + minimum_should_match** | ⚠️ 只有 OR，靠 BM25 隐式排序 |
| **C. 主题描述** | 含疑问词、抽象名词、口语 | 「怎么理解苏轼的旷达与无奈」 | **向量优先 + 词法兜底 + rerank** | ✅ Hybrid 覆盖 |

**现状的问题**：A 类被当成 B 类处理（全 OR），于是"命中 2 个虚词的无关文档"和"命中 9 个词的原文"
在 BM25 里分数接近（因为虚词贡献 ≈ 0，见 §4.3），排序靠运气；在技术文档库里干脆 0 命中后回落虚词，
返回 10 条完全无关的英语真题（上一轮生产实测）。

**这就是上一轮遗留 P1 的真正解法**：不是"给前端加个提示"，而是**把覆盖率变成排序主键**。
覆盖率 0/10 的文档根本不该出现在结果里。

### 4.5 Q2 结论

| 说法 | 判定 |
| :--- | :--- |
| "长句查询词法一定不如向量" | **错**。只在延迟维度成立；精度维度词法更稳 |
| "向量能解决长句" | **错**。向量把成本变成常数，但把精度变成了不可逆的有损压缩 |
| "长句应该走混合检索" | **部分对**。混合解决"至少一路对"，但不解决任何一侧的退化，且会让延迟叠加 |
| "应该先分意图再路由" | **对**。A/B/C 三类意图的最优解完全不同，混在一起做必然次优 |

---

## 五、Q3：SAG-Wiki 是怎么做的（源码级）

### 5.1 架构：纯本地 FTS5 + LLM 打分，**没有向量**

| 项 | 事实 | 出处 |
| :--- | :--- | :--- |
| 存储 | 单个 SQLite 文件 + FTS5（`content=''` contentless + `contentless_delete=1`） | `core/src/store.rs:273-294` |
| 分词 | `jieba-rs` 的 `cut_for_search` + NFKC + 小写 + **45 个硬编码停用词** | `core/src/text.rs:26-30, :73-82` |
| 打分 | **自研 C 扩展** `sag_rank.c`（BM25，k1=1.2/b=0.75，与官方一致） | `core/native/sag_rank.c:137-191` |
| 精排 | 调外部 LLM（Jev）逐条打 0~1 分 | `core/src/models.rs:207-263` |
| 向量 | **完全没有**（`Cargo.toml` 无任何 embedding 依赖） | `core/Cargo.toml` |
| 并发 | **8 个只读连接** + 2GB mmap | `core/src/store.rs:366-373` |
| 索引写 | 分词在写入前完成，FTS5 只做空格切分 | `core/src/store.rs:1712-1723` |

> 这一点很重要：**SAG-Wiki 没有向量，所以它必须把词法做到极致**。
> 而我们的系统有向量，反而让词法的退化被"反正有向量兜底"掩盖了两年。

### 5.2 四层防护

#### 第 1 层：词元上限 80（最弱的一层）

```rust
// core/src/text.rs:140-154
pub fn match_query(text: &str) -> String {
    search_terms(text).iter().map(|s| quote(s)).collect::<Vec<_>>().join(" OR ")
}
pub fn search_terms(text: &str) -> Vec<String> {
    ... .take(80).collect()      // 只保留前 80 个去重词元
}
```

**它在这一层完全不剪枝**——15 字中文句照样全 OR。所以"SAG 解决了长查询问题"这个说法是**不准确**的。

#### 第 2 层：稀有词子集 + BM25 上界提前返回（核心）

```rust
// core/src/store.rs:1984-2001
if key.required.is_none() && key.limit >= 100 {
    // ① 按 IDF 降序（越稀有越前），只保留最稀有的 55%
    selected.sort_by(|&a, &b| idfs[b].total_cmp(&idfs[a]).then(a.cmp(&b)));
    selected.truncate((idfs.len() as f64 * 0.55).ceil() as usize);
    // ② 被丢掉的 45% 最多还能贡献多少分：2.2 = k1+1 是单 phrase 贡献的上界
    let bound = 2.2 * idfs.iter().enumerate()
        .filter(|(i, _)| !selected.contains(i)).map(|(_, idf)| idf).sum::<f64>();
    // ③ 用子集重跑
    let possible = read(&restricted, &values)?;
    // ④ 上界成立 → 直接返回
    if possible.len() == key.limit && -possible.last().unwrap().bm25 > bound {
        return Ok(possible);
    }
}
read(sql, &values)   // 兜底：跑完整 OR
```

**为什么它必须自研 rank 函数**：上界比较要求"子集查询"与"完整查询"使用**完全相同的 IDF/avgdl**，
而官方 `bm25()` 的 auxdata 缓存是 per-statement 的，两次查询的统计量可能不同。
`core/native/sag_rank.c:1-3` 的注释写得很清楚：

```c
/* BM25 calculations adapted from SQLite ext/fts5/fts5_aux.c (public domain).
 * Reuse exact per-query statistics across filtered searches. */
```

#### 第 3 层：扩展轮"每词一条 MATCH"并行 + 交错合并

```rust
// core/src/store.rs:1839-1879 —— 8 线程按词分片
let workers = required.len().min(QUERY_WORKERS);   // 最多 8
(worker..required.len()).step_by(workers).map(|position| {
    self.candidate_query_on(worker, CandidateQuery {
        required: Some(required[position].clone()),   // ← 每个词单独一条
        ...
    })
})
```

```rust
// core/src/retrieval.rs:147-165 —— 交错合并（round-robin）
for rank in 0..limit {
    for list in &lists { if let Some(c) = list.get(rank) { ... } }
}
```

**这是最值得借鉴的一层**：一条 20 词的 OR 需要扫全部命中行；
20 条 `(expr) AND ("term")` 每条都被 `AND` 收窄，且都有 `LIMIT k`，还能并行。
我们实测 `(12 虚词 OR) AND "稀有词"` 比纯 OR 快 **8.7 倍**，方向一致。

#### 第 4 层：只 hydrate top-k

```rust
// core/src/store.rs:1957-1968 —— 先截断再 JOIN
top_hits AS MATERIALIZED (SELECT id,score FROM ranked WHERE position=1 ORDER BY score,id LIMIT ?3 OFFSET ?4)
```

这一层我们**已经做了**（两阶段）。

### 5.3 逐条评述：能借鉴什么、不能借鉴什么

| 机制 | 判定 | 理由 |
| :--- | :--- | :--- |
| 词元上限 80 | ⚠️ 部分 | 我们已有 `Take(64)`，等价 |
| 45 词停用词表 | ❌ 不借鉴 | **缺 `她/着/就/也/都/很`**，正是我们事故里的元凶；我们改用 df 动态剪枝，更稳 |
| 稀有词子集 + 上界 | ⚠️ 评估后不移植 | 见下方"严格性分析" |
| 每词一条并行 + 交错 | ✅ **借鉴** | 需要多只读连接，见 P1-2 |
| `retrieval_vocabulary` 预计算 | ✅ **借鉴** | 对应 P1-1 的 `lexical_term_stats` |
| 只 hydrate top-k | ✅ 已有 | 两阶段 |
| `content=''` contentless | ❌ 不借鉴 | 我们正文在 `chunks` 表，且已有 `fts_chunks` 双写的历史包袱 |
| 回源 + SHA-256 校验 | ❌ 不借鉴 | 每条候选一次文件读 + 哈希，候选集大时成本盖过收益 |
| 8 只读连接 + 2GB mmap | ✅ **借鉴** | 当前是单连接串行，df 统计与多 tier 无法并行 |
| Jev（LLM 精排） | ❌ 不引入 | 延迟不可控，且我们已有 ONNX rerank |

#### 关于"上界剪枝"的严格性分析（重要）

设子集查询 `Q_s ⊂ Q`，被丢弃词集 `D`。对任意文档 `d`：

```
score_Q(d) = score_{Q_s}(d) + Σ_{t∈D} c_t(d),   其中 c_t(d) ≤ (k1+1)·idf_t = 2.2·idf_t
⇒ score_Q(d) ≤ score_{Q_s}(d) + B,             B = 2.2 · Σ_{t∈D} idf_t
```

取子集查询前 k 名 `S`，最小分 `s_k`。对 `d ∉ S`：`score_{Q_s}(d) ≤ s_{k+1}`（第 k+1 名分数）。

要**保证** `d` 进不了 top-k，需要：

```
s_k  ≥  s_{k+1} + B        ← 正确条件：第 k 名与第 k+1 名的【分差】≥ 被丢词的总上界
```

而 SAG 的实现是 `s_k > B`（拿 `s_k` 直接与 `B` 比），**这是一个更宽松、数学上不充分的条件**：
它把 `s_{k+1}` 当成了 0。在"分数断层明显"的长查询里通常成立，在"分数密集"时会漏判。

> **它的后果不是崩溃，而是"返回的 top-k 与完整查询不一致"**——一个静默的正确性偏差。
> 项目里有等价性测试（`store.rs:2645-2672`），但只覆盖了它自己的语料分布。

**我们要不要移植？评估结论：不要。** 三个理由：

1. 严格版需要第 k+1 名分数（`LIMIT k+1`），实现复杂度上升；
2. 需要自己算 idf（FTS5 不暴露），要复刻 `log((N-n+0.5)/(n+0.5))` 与 `1e-6` 截断 —— 可以做（§4.3 已验证公式），但增加了"我们的 idf 与 FTS5 的 idf 不一致"的长期维护风险；
3. **收益可被更简单的手段覆盖**：`AND 分层 + 覆盖率重排` 不需要任何 idf，就能同时把候选集压下来、把精度提上去。

### 5.4 它的反例：快速搜索路径

```rust
// core/src/store.rs:1772-1804
let expression = text::match_query(query);          // 全 OR
SELECT COUNT(DISTINCT c.document_id) ... WHERE chunk_fts MATCH ?1   // ← 全量计数
WITH matched AS MATERIALIZED (SELECT ..., bm25(chunk_fts,2,2,1) ...) -- ← 全量物化
ranked AS (SELECT ..., row_number() OVER (PARTITION BY document_id ORDER BY score,id) ...)
```

**没有上界剪枝、没有候选窗口、全量 `COUNT(DISTINCT)`、窗口函数要求全量物化。**
这正是我们上一轮修掉的那个形状。

> 结论：**SAG-Wiki 只在"深入检索/Agent 搜索"那条路上做了优化，用户直接打 15 字走的那条路和我们的旧实现同构。**
> 所以它不是一个"已经解决了长查询问题"的参照系，而是一个"同一问题在另一个项目里也没解决干净"的旁证。

### 5.5 对比表

| 维度 | SAG-Wiki | 本项目（当前） |
| :--- | :--- | :--- |
| 向量检索 | 无 | 有（BLOB 暴力扫描） |
| 查询剪枝 | 无（全 OR） | 有（df 剪枝 + 预算） |
| 上界剪枝 | 有（近似，仅 `limit≥100`） | 无 |
| AND 层 | 无（扩展轮用 required 做 AND） | **无** |
| 并行召回 | 8 线程按词分片 | 无（单连接串行） |
| df 统计 | 预计算表 + 8 线程 + 缓存 | 每次查询 N 次 COUNT + 静态缓存 |
| 两阶段回表 | 有（`top_hits` 先截断） | 有 |
| 覆盖率排序 | 无 | 无 |
| 意图分类 | 隐式（`phrases.len()>=8`） | 隐式（`Query.Length>15`） |
| 召回地板 | 无（全 OR 天然有结果） | 有 |
| 精排 | 外部 LLM | 本地 ONNX rerank |

---

## 六、头脑风暴：可行性与欠缺

### 6.1 性能欠缺

| # | 欠缺 | 量级 | 依据 |
| :-- | :--- | :--- | :--- |
| P1 | 候选预算是比例，语料放大即复现 | 1M 语料 ≈ 875ms（35% 预算）/ 1.85s（剪枝失效 75% 命中） | 200k 实测 2.5µs/行线性外推 |
| P2 | 兜底/地板层无预算 | 可达 95% 语料 | 代码路径分析 |
| P3 | 无 AND 层 | 候选集大 8.7× | 200k 实测 |
| P4 | df 统计 64 次串行 | 60ms 量级 | 单次 7.53ms（200k）折算 |
| P5 | 向量全表扫描 + 全正文驻留 | 236MB + 57MB / 查询 | 57,733 × 1024 × 4B |
| P6 | SIMD 全量排序 | O(N log N) | 代码 `OrderByDescending().Take(k)` |
| P7 | 融合无联合预算 | 两路相加 | 代码路径 |
| P8 | 无超时 | 无法中断 | 仅 `CancellationToken` |

### 6.2 逻辑 / 语义欠缺

| # | 欠缺 | 后果 |
| :-- | :--- | :--- |
| L1 | 无覆盖率概念 | "命中 2 个虚词的无关文档"与"命中 9 词的原文"排序接近 |
| L2 | 兜底/地板结果与精确命中**视觉无差别** | 上一轮遗留 P1：技术文档库返回 10 条无关英语真题 |
| L3 | `ScoreKind.RRF` 被 Z-Score 融合复用 | 前端徽标显示"Z-Score 融合"但枚举是 RRF，**标签撒谎** |
| L4 | `SearchStats.IsEarlyPruned` 声明但**从未赋值** | 前端只能靠 `items.every(scoreKind==BM25)` 猜 |
| L5 | `LexicalQueryPlan` 未进 DTO | 剪了多少词、候选多大、是否地板，前端全看不到 |
| L6 | 短语/NEAR 对高频词无效但无守卫 | 若有人放开 `EnableNearTier` 条件会立刻退化 |
| L7 | 向量 `sim > 0` 才收集，等于**静默丢弃**负相关 | 候选不足时无任何提示 |

### 6.3 工程 / 可维护欠缺

| # | 欠缺 | 证据 |
| :-- | :--- | :--- |
| E1 | `LexicalQueryPlanner` 是**静态类 + 静态 Default 配置** | `LexicalQueryPlanner.cs:89, :98` —— 无法注入、无法按 KB 差异化 |
| E2 | `SearchStrategyFactory` 已写好但**主链路未使用** | `SearchExtensions.cs:74` 注册，全仓无消费者 |
| E3 | 三套融合实现语义重叠 | `AdaptiveQueryAwareFusion` / `RrfSearchFusionStrategy` / `HybridSearchService` 内部 |
| E4 | 停用词表散落 6 处、全硬编码 | `SynonymService` / `BM25Searcher` / `TokenizationCacheProxy` / `StatisticalPrfOperator` / `JiebaPosAnalysisAdapter` / `AdaptiveSearchFusionHelper` |
| E5 | 策略分派是 `if/else` 深嵌套 | `SearchService.cs:248, :431, :435-443, :595-598, :1278, :1334` |
| E6 | 无 `IOptions<T>`，配置走 `ConfigManager.Load()` | 全仓 0 命中 |

### 6.4 可行性矩阵

| 方案 | 改动面 | 收益 | 风险 | 结论 |
| :--- | :--- | :--- | :--- | :--- |
| 候选绝对上限 | `LexicalQueryPlanner` 1 个字段 + 2 处判断 | 高 | 低（可能召回下降，用地板兜） | ✅ **P0** |
| AND 分层 | planner 加 1 个 tier | 高 | 低（0 结果自动 fall through） | ✅ **P0** |
| 覆盖率重排 | 检索后处理 + DTO 加 1 字段 | 高（同时修 P1 遗留） | 低（2.12ms/2000 候选） | ✅ **P0** |
| df 统计表 | 新表 + 写路径 + 校验 | 高 | 中（漂移） | ✅ **P1** |
| 向量两阶段 + 有界堆 | 中等（改 pool + 回表） | 高 | 中（需重载快照） | ✅ **P1** |
| 意图分类 | 新组件 + 路由 | 中高 | 中（误分类） | ✅ **P2** |
| 表达式编译器 | 重构 planner 输出 | 中 | 低 | ✅ **P2** |
| 召回预算状态机 | 重构阶梯 | 中 | 低 | ✅ **P2** |
| BM25 上界剪枝 | 需自算 idf + 严格判据 | 中 | 中高 | ⏸ 暂缓 |
| 引入 ANN | 新依赖 + 重建 | 高（百万级） | 高 | ⏸ P2 |
| 引入 ES/Meilisearch | 部署形态改变 | 低 | 高 | ❌ 不做 |

---

## 七、设计模式方案

### 7.1 现状诊断：模式都已经写好了，只是没接上

这个项目**不缺设计模式**——`SearchStrategyFactory`、`ISearchFusionStrategy`、
`ResilienceCircuitBreaker`、13 个 `*StateMachine`、`IEmbeddingBackendAdapter` 全都在。
问题在于：

```
SearchStrategyFactory ──注册──► DI 容器 ──✗──► 无人消费（死代码）
SearchService.SearchCoreAsync ──if/else──► 6 个分支（真实执行路径）
```

> **所以本轮的"用设计模式优化"不是"引入新模式"，而是"把已有的模式接上，并补三个真正缺的抽象"。**

### 7.2 目标架构

```
                        ┌─────────────────────────────┐
                        │   IQueryIntentClassifier    │  新增（策略）
                        │  原文引用 / 多约束 / 主题描述 │
                        └──────────────┬──────────────┘
                                       │ Intent
                        ┌──────────────▼──────────────┐
                        │   IRetrievalPlanner          │  新增（策略族）
                        │  ExactPhrase │ AndFirst │ DenseFirst
                        └──────────────┬──────────────┘
                                       │ RetrievalPlan（结构化，非字符串）
                        ┌──────────────▼──────────────┐
                        │  ILexicalExpressionCompiler  │  新增（适配器）
                        │      Fts5 │ (未来)其它后端    │
                        └──────────────┬──────────────┘
                                       │ FTS5 表达式
                        ┌──────────────▼──────────────┐
                        │  RecallBudgetStateMachine    │  新增（状态机）
                        │ Normal→Narrow→Widen→Floor→GiveUp
                        └──────────────┬──────────────┘
                                       │ 候选集（受预算约束）
                        ┌──────────────▼──────────────┐
                        │  ICoverageRanker             │  新增（策略）
                        │  覆盖率优先 → bm25 次键       │
                        └──────────────┬──────────────┘
                                       │
                        ┌──────────────▼──────────────┐
                        │  ISearchFusionStrategy       │  已有（3 套，需收敛）
                        └─────────────────────────────┘

  横切（已有，装饰器接上即可）：
    BudgetedRecallDecorator（超时/预算）  CachedRecallDecorator  TracedRecallDecorator
```

### 7.3 策略模式：意图分类 + 三套规划器

**现状**：`isLongQuery = Query.Length > 15`（`SqliteKnowledgeBaseStore.cs:1756`）——
一个字符数阈值，既不是分类也不是策略。

**方案**：

```csharp
public enum QueryIntent { ExactPhrase, MultiConstraint, TopicDescription }

public interface IQueryIntentClassifier
{
    QueryIntent Classify(string query, IReadOnlyList<string> tokens, IReadOnlyDictionary<string,int> df, int corpusSize);
}
```

判定规则（**纯本地、零模型、可解释**，每条都可在门禁里断言）：

| 意图 | 判别式 | 依据 |
| :--- | :--- | :--- |
| `ExactPhrase` | 含中文标点 `，。！？；` 或长度 ≥ 8 且**虚词占比 ≥ 30%** | "像一句话"——虚词多说明是自然句，不是关键词堆 |
| `MultiConstraint` | 含数字/专名/书名号/引号，或 `tokens.Count >= 4` 且虚词占比 < 15% | "多约束" |
| `TopicDescription` | 含疑问词（如何/怎么/为什么/是什么/有哪些）或动词短语开头 | "主题描述" |

> 注意 `ExactPhrase` 的判别式**故意用虚词占比而不是"看起来像句子"**：
> 因为虚词占比正是我们已有的 df 统计能直接算出来的（`Σ df(虚词) / tokens`），零额外成本。

三套规划器：

| 规划器 | 表达式形态 | 何时用 |
| :--- | :--- | :--- |
| `ExactPhrasePlanner` | `"词1 词2 词3"`（整句短语）→ 退化为 `NEAR(词i 词i+1, 5)` 链 → 退化为覆盖率 OR | A 类 |
| `AndFirstPlanner` | `"a" AND "b" AND "c"` → 逐词放宽（去掉 df 最大的）→ OR | B 类 |
| `DenseFirstPlanner` | 向量优先 + 词法兜底 + rerank | C 类 |

**关键点**：`ExactPhrasePlanner` 的第一层是**整句短语**。
实测表明短语对高频词很贵（116ms），但对**稀有词**极便宜——而"原文引用"的首词通常稀有。
所以它的成本曲线正好和"关键词 OR"相反：**越像原文越便宜**。

### 7.4 适配器模式：把"规划"与"FTS5 语法"解耦

**现状**：`LexicalQueryPlan.Queries` 是 `IReadOnlyList<string>`——**已经编译好的 FTS5 表达式**。
这带来三个问题：

1. 无法测试"规划逻辑"而不涉及 FTS5 语法；
2. 无法换后端（比如未来用 sqlite-vec 的 `MATCH` 或外接引擎）；
3. 转义/注入风险分散在 planner 内部（`Escape()`、`QuotePhrase()`）。

**方案**：

```csharp
// 结构化计划：只表达"要什么"，不表达"怎么写"
public sealed record LexicalQueryPlan(
    QueryIntent Intent,
    IReadOnlyList<LexicalClause> Clauses,     // ← 结构化子句，不是字符串
    int CorpusSize, long EstimatedMatchedDocs, ...);

public abstract record LexicalClause;
public sealed record TermClause(string Term) : LexicalClause;
public sealed record AllOfClause(IReadOnlyList<LexicalClause> Children) : LexicalClause;   // AND
public sealed record AnyOfClause(IReadOnlyList<LexicalClause> Children) : LexicalClause;   // OR
public sealed record NearClause(IReadOnlyList<string> Terms, int Slop) : LexicalClause;
public sealed record PhraseClause(string Text) : LexicalClause;

// 适配器：把结构化计划编译成目标后端的表达式
public interface ILexicalExpressionCompiler
{
    string Compile(LexicalClause clause);
}
public sealed class Fts5ExpressionCompiler : ILexicalExpressionCompiler { ... }
```

**收益**：
- 门禁可以直接断言 `plan.Clauses` 的结构（`AllOf` 里有几个 `TermClause`），**不依赖字符串匹配**——比现在的 `Assert.DoesNotContain("\"的\"", plan.Queries[1])` 稳定得多；
- 转义集中在一处，注入面收敛；
- 未来接 sqlite-vec / 外部引擎时只换适配器。

### 7.5 状态机：把"阶梯"从 `foreach + break` 变成显式状态

**现状**：

```csharp
// SqliteKnowledgeBaseStore.cs:1800-1805
foreach (var q in lexicalQueryPlan.Queries)
{
    ... execute ...
    if (bm25Items.Count >= request.TopK) break;    // ← 隐式状态机
}
```

问题：
- 无法回答"为什么走到了第 3 层"（只能看 span 条数）；
- 无法对"每一层"单独施加超时/预算；
- 无法表达"这一层是地板层，结果要打标记"。

**方案**：

```
                  ┌──────────┐
      ┌──────────►│  Normal  │  NEAR/AND 层，最严格
      │           └────┬─────┘
      │        结果<k   │  结果≥k
      │                ▼
      │           ┌──────────┐
      │           │  Narrow  │  锚点 OR 层（受绝对预算约束）
      │           └────┬─────┘
      │                │
      │           ┌────▼─────┐
      │           │  Widen   │  放宽：去掉最稀有的 1 个锚点重试
      │           └────┬─────┘
      │                │
      │           ┌────▼─────┐
      │           │  Floor   │  回落虚词（必须打 UsedRecallFloor 标记）
      │           └────┬─────┘
      │                │
      │           ┌────▼─────┐
      └───────────│ GiveUp   │  返回空 + 明确语义（"无匹配"而非"宽松结果"）
                  └──────────┘

  迁移条件（guard）：
    Normal→Narrow  : 结果 < topK
    Narrow→Widen   : 结果 < topK 且 anchors.Count > 1 且 预算未耗尽
    Widen→Floor    : 结果 == 0 且 pruned.Count > 0
    Floor→GiveUp   : 结果 == 0 或 覆盖率全为 0
  退出条件：
    Normal/Narrow/Widen : 结果 ≥ topK
    任意状态            : 超时 / 取消
```

**收益**：
- 每一步的耗时、候选数、覆盖率都能进 `TimingChain`（现在只有"第几层"）；
- 地板层结果可以带 `IsFloorResult=true` 一路透到前端（修 L2）；
- 超时可以按层分配（`Normal: 200ms, Narrow: 200ms, Widen: 100ms, Floor: 100ms`）。

### 7.6 装饰器：横切能力不用改主链路

```csharp
public interface ILexicalRecall
{
    Task<RecallSnapshot> RecallAsync(RecallRequest request, CancellationToken ct);
}

public sealed class BudgetedRecallDecorator(ILexicalRecall inner, RecallBudget budget) : ILexicalRecall
{
    // 硬上限：候选数 / 墙钟时间 / 单层超时
}
public sealed class TracedRecallDecorator(ILexicalRecall inner, TimingChain chain) : ILexicalRecall { }
public sealed class CachedRecallDecorator(ILexicalRecall inner) : ILexicalRecall { }   // 可选，长尾命中率低
```

> 与现有的 `FallbackResilientSearchStrategy`（`Strategies/FallbackResilientSearchStrategy.cs:16`）
> 是同一范式——**装饰器已经在项目里用过，照抄即可**。

### 7.7 迁移路径（不破坏现有 14 条门禁）

| 阶段 | 动作 | 门禁是否要改 |
| :--- | :--- | :--- |
| ① | 加绝对候选上限 + AND 层（改 `LexicalQueryPlanner`，输出仍是 `Queries` 字符串） | 否（现有 G4-1~G4-11 仍绿） |
| ② | 加覆盖率重排（后处理，不改表达式） | 新增 G8 |
| ③ | 引入 `LexicalClause` 结构化输出，`Queries` 变成**编译产物**（保留为 `[Obsolete]` 兼容属性） | 新增 G9（结构断言） |
| ④ | 引入 `IQueryIntentClassifier`，默认全走 `AndFirstPlanner`（行为等价于 ①） | 新增 G10 |
| ⑤ | 引入状态机，替换 `foreach+break` | 现有 G4-6/G7-1 覆盖 |
| ⑥ | 向量侧两阶段 + 有界堆 | 新增 G11/G12 |

**关键约束**：每一步都必须是**行为等价或可证明更优**的重构，不能"顺手改语义"。
这是上一轮"只锁快不锁对"教训的直接应用。

---

## 八、现有基础设施与第三方库复用

### 8.1 可直接复用（零新增依赖）

| 资产 | 位置 | 复用方式 | 复用度 |
| :--- | :--- | :--- | :---: |
| `TimingScope` / `TimingChain` | `Tracing/TimingChain.cs` | 覆盖率、预算、状态机迁移全部进 span | ✅ 直接 |
| `LexicalIndexBackfillWorker` + Signal | `Business/.../Services/` | df 表增量维护可挂在这条链上 | ✅ 直接 |
| `ReconciliationJob` | `Business/.../Services/` | df 表漂移校验（与索引对账同频） | ✅ 直接 |
| `RepairLexicalIndexAsync` | `SqliteKnowledgeBaseStore` | df 表全量重建的兜底入口 | ✅ 直接 |
| `kb_manifest.lexical_indexed_rowid` | `InitializeAsync` | df 表的版本锚（零新增表即可判断是否需重建） | ✅ 直接 |
| `ResilienceCircuitBreaker` | `Features/Resilience/` | 向量路径异常熔断（现在 catch 后直接 throw） | ✅ 直接 |
| `ISearchFusionStrategy` + 2 个实现 | `Fusion/SearchFusionStrategies.cs` | 融合收敛到这一族，删掉重复实现 | ⚠️ 需收敛 |
| `SearchStrategyFactory` + 6 个策略 | `Strategies/` | **已写好未使用**，直接接主链路 | ✅ 直接 |
| `FallbackResilientSearchStrategy` | `Strategies/` | 装饰器范式参照 | ✅ 直接 |
| `CascadeEarlyPruningStateMachine` | `Lexical/` | 状态机范式参照（含 guard + 迁移表） | ✅ 直接 |
| `QueryVectorCacheProxy`（LRU 2048） | `Storage/` | 覆盖率/意图分类结果可复用同款 LRU | ✅ 直接 |
| `JiebaPosAnalysisAdapter` | `Lexical/` | 意图分类可直接用它的词性（疑问词/动词短语） | ✅ 直接 |
| `LongQueryRefiner.DistilledTerms` | `Lexical/` | 多约束查询的蒸馏词已现成 | ✅ 直接 |
| `InMemoryTensorProjectionPool` | `Storage/` | 加"有界堆 + 不驻留正文"即可，不必重写 | ⚠️ 改造 |
| `sqlite-vec`（`vec0`） | `Directory.Packages.props:63` | 若要统一向量存储，可替换 `chunk_vectors` BLOB | ⏸ P2 |
| `System.Numerics.Tensors` | 已引用 | `TensorPrimitives.CosineSimilarity` 继续用 | ✅ 直接 |

### 8.2 需要新建

| 组件 | 说明 | 工作量 |
| :--- | :--- | :--- |
| `lexical_term_stats(term TEXT PK, df INTEGER)` | 增量维护的 df 表 | 中 |
| `ICoverageRanker` | 覆盖率重排（内存 `Contains`，2.12ms/2000 候选） | 小 |
| `IQueryIntentClassifier` | 意图分类 | 小 |
| `ILexicalExpressionCompiler` | 结构化计划 → FTS5 表达式 | 中 |
| `RecallBudgetStateMachine` | 阶梯显式化 | 中 |
| `BoundedTopK<T>` | 有界最小堆（替代 `OrderByDescending().Take(k)`） | 小 |

### 8.3 明确不引入

| 候选 | 不引入原因 |
| :--- | :--- |
| Elasticsearch / OpenSearch | JVM 常驻内存、部署形态改变；且本问题是"查询构造 + 预算"，换引擎不解决 |
| Meilisearch / Typesense | 需独立进程 + 全量重建索引；**但借鉴其 `words` 排序规则思想**（覆盖率） |
| Lucene.NET | 双索引体系；BMW 能力虽好，但为它重建 57k 块索引不划算 |
| Tantivy（Rust） | 跨语言 FFI，与现有 .NET 栈冲突 |
| SPLADE / learned sparse | 需要模型推理，与"本地轻量"定位冲突；且解决的是精度不是成本 |
| Qdrant / Milvus | 独立服务，与"单文件部署"定位冲突 |
| `IMemoryCache` / Polly | 项目已有自研 LRU 与 `ResilienceCircuitBreaker`，再引一套反而分裂 |

---

## 九、业界主流方案横向对比

### 9.1 五种范式

| 系统 / 算法 | 核心机制 | 长句怎么处理 | 候选集上界 | 可移植性 |
| :--- | :--- | :--- | :--- | :--- |
| **Lucene 8+ / ES** | **Block-Max WAND**：块级得分上界 + 跳过整块 | 与查询长度无关；靠 top-k 剪枝 | ✅ 动态阈值 | ❌ 需 Lucene |
| **ES `common terms`（已移除）** | 高频词不参与打分、低频词作强约束 | 显式分"高频/低频"两组 | ✅ | ✅ 思想可移植 |
| **Meilisearch** | `words` ranking rule（按命中词数分层）+ `maxTotalHits` | 按"命中几个词"排序 | ✅ 硬上限 | ✅ **思想最值得抄** |
| **SAG-Wiki** | 稀有词子集 + BM25 上界 | `phrases.len()>=8` 才触发 | ⚠️ 近似 | ⚠️ 需自研 rank |
| **SQLite FTS5（现状）** | 无任何自动机制 | 完全由调用方负责 | ❌ | — |
| **sqlite-vec `vec0`** | 暴力 SIMD KNN（非 ANN） | 与查询长度无关 | ✅ `LIMIT k` | ✅ 已引入 |

### 9.2 重要更正：ES 8 已移除 common terms query

上一版文档（`lexical-query-degeneration-...md` §6.1）写"本方案直接对齐 ES `common terms query`"。
**这个说法需要修正**：

> Elasticsearch 8.0.0 的 release notes 明确列出 **`Remove CommonTermsQuery and cutoff_frequency param`**（#42654）。
> 也就是说 `cutoff_frequency` 这套 API 在 8.x **已不存在**。

它的**思想**（"高频词不作约束、低频词作强约束"）仍然是正确的，也是我们 `CommonTermCutoff=0.15` 的来源；
但表述上应改为"**对齐 ES 7.x 的 common terms 语义 / Lucene 的 `low_freq_operator=and` 思路**"，
而不是"对齐 ES 现行 API"。

ES 8 里替代它的是普通的 `bool` 组合：

```json
{
  "bool": {
    "must":     [ { "match": { "body": "稀有词" } } ],          // 低频：强约束
    "should":   [ { "match": { "body": "的" } }, ... ],          // 高频：只加分不约束
    "minimum_should_match": 1
  }
}
```

**这恰好就是我们要加的 AND 层 + OR 层结构**。所以 P0-2 的方案不是"发明"，而是"补上 ES 早就有的那一层"。

### 9.3 结果窗口：业界共识是"不要数清楚"

| 系统 | 机制 | 含义 |
| :--- | :--- | :--- |
| ES | 结果窗口默认 `10000`，`track_total_hits` 默认关闭 | **故意不精确计数**，因为精确计数要遍历全部命中 |
| Meilisearch | `pagination.maxTotalHits` 默认 `1000` | 硬上限 |
| SAG-Wiki | `candidate_k = 50/100` | 每轮候选固定 |
| **本项目** | 词法 `candidateWindow ≤ 2000`，但**打分仍覆盖全部命中** | ⚠️ 只限制了回表 |

> **共识**：候选集必须有**绝对**上限，且"总数"允许是估计值。
> 我们已经在 `LoadTermDocumentFrequenciesAsync` 里用了"带上限 COUNT"（估 df），
> 但还没有把同样的思想用到"候选集"上——这正是 L1。

---

## 十、演进方案：P0 / P1 / P2

### P0-1 候选集绝对上限

**改动**：`LexicalQueryPlannerOptions` 增加 `MaxCandidateDocs = 5_000`；

```csharp
var budget = Math.Min(opt.MaxCandidateDocs, Math.Max(1.0, opt.MaxCandidateRatio * corpusSize));
```

并对**兜底层**（`:140-151`）与**地板层**（`:195-208`）同样施加约束：
- 兜底层：若 `estimated > budget`，只保留 df 最小的 1 个词（而不是 3 个），并标记 `UsedFallbackAnchors`；
- 地板层：地板的 `floorTerms` 从 3 个降到 1 个（df 最小），并标记 `UsedRecallFloor`。

**预期**：1M 语料候选从 350k → 5k（70×）；兜底层从 95% → ≤ 单词语料占比。
**风险**：召回下降 → 由地板层 + 覆盖率排序兜住；`GiveUp` 时明确返回"无匹配"。
**门禁**：`G8_1_CandidateBudget_MustHaveAbsoluteCap`（结构：源码必须出现绝对上限常量；行为：1M 语料 `EstimatedMatchedDocs ≤ 5_000`）。

### P0-2 AND 分层

**改动**：在 `queries` 列表**最前面**插入一层：

```csharp
// 所有稀有锚点必须同时命中（ES low_freq_operator=and 的等价物）
if (anchors.Count >= 2)
{
    queries.Insert(0, string.Join(" AND ", anchors.Select(a => $"\"{Escape(a)}\"")));
}
```

**预期**：实测 `(OR) AND "稀有词"` 比纯 OR 快 8.7×；多约束查询候选集降 1~2 个数量级。
**风险**：0 结果 → 自动 fall through 到 OR 层（已有 `foreach + break` 语义）。
**门禁**：`G8_2_AndTier_MustPrecedeOrTier`（结构：`Queries[0]` 含 `AND` 且不含 ` OR `；行为：AND 层命中数 ≤ OR 层）。

### P0-3 覆盖率重排 + 透出

**改动**：

1. 取到候选后，在内存里对每个候选算 `coverage = 命中的查询词数 / 查询词数`（实测 2.12ms/2000 候选）；
2. 排序键：`(coverage desc, bm25 asc)`——**覆盖率优先，bm25 次键**；
3. `RecallItem` 增加 `Coverage` 字段；`CrossKnowledgeBaseSearchResponse` 增加 `UsedRecallFloor`；
4. 前端：`coverage < 0.2` 的候选打"宽松匹配"标签；`UsedRecallFloor=true` 时顶部提示。

**预期**：修上一轮遗留 P1（兜底结果被误认为精确命中）；A 类查询精度显著提升。
**风险**：覆盖率把长文档优势削弱 → 用"查询词去重后的覆盖率"，且只对 `tokens.Count >= 6` 的查询启用。
**门禁**：`G8_3_Coverage_MustRankAboveBm25`、`G8_4_FloorResult_MustBeFlagged`。

### P1-1 `lexical_term_stats` 增量 df 表

**改动**：

```sql
CREATE TABLE IF NOT EXISTS lexical_term_stats (
    term TEXT PRIMARY KEY,
    df   INTEGER NOT NULL
) WITHOUT ROWID;
```

- 写路径：`WriteLexicalBatchAsync` 内，对每个 `(chunk, term)` 做 `INSERT ... ON CONFLICT(term) DO UPDATE SET df=df+1`；
- 删路径：从 `chunks.text` 重新分词后递减；
- 重建：`RepairLexicalIndexAsync` 时全量 `INSERT ... SELECT` 重算；
- 对账：`ReconciliationJob` 抽样比对 `lexical_term_stats.df` 与 `COUNT` 实测值，偏差 > 1% 触发全量重建。

**预期**：plan 阶段从 64 次 COUNT（60ms 量级）→ 1 次索引查找（<1ms）；且拿到**精确** df（不再受 `cap` 限制），
`EstimatedMatchedDocs` 从"下界"变成"准确值"。
**风险**：漂移 → 对账 + repair。
**门禁**：`G8_5_TermStats_MustMatchExactCount`（行为：随机 20 个词，表内 df 与实测 `COUNT` 完全一致）。

### P1-2 向量路径两阶段 + 有界堆

**改动**：

1. `TensorSnapshot` 只保留 `ChunkIds / Vectors`，**不保留 `Texts`**；
2. `TryScanSimd` 用 `PriorityQueue<int,double>`（容量 `topK`）替代 `List + OrderByDescending`；
3. top-k 出来后，用一次 `WHERE chunk_id IN (...)` 回表取正文（与词法两阶段同构）。

**预期**：
- 内存：常驻从 O(N·(d+正文)) → O(N·d)（去掉正文）+ 每查询 O(k)；
- 时间：排序 O(N log N) → O(N log k)；
- 100 万块时从"必然 OOM" → "可运行"。
**风险**：回表多一次往返（~1ms），可接受。
**门禁**：`G8_6_VectorScan_MustUseBoundedHeap`（结构：不得出现 `OrderByDescending(...).Take(` 在扫描路径）、`G8_7_VectorSnapshot_MustNotRetainTexts`（结构：`TensorSnapshot` 不得有 `Texts` 字段）。

### P1-3 召回超时

**改动**：`RecallRequest` 增加 `BudgetMs`（默认 3000ms，`Deep` 模式 8000ms）；
在 `ExecuteLexicalQueryInternalAsync` 与向量扫描循环里检查 `Stopwatch.Elapsed > BudgetMs` → 抛出 `RecallBudgetExceededException`
→ 由状态机迁移到 `GiveUp` 并返回已有候选（部分结果 + `IsTruncated=true`）。

**门禁**：`G8_8_Recall_MustRespectBudget`（行为：构造 10 万块语料，预算 200ms，必须返回且不超时 > 500ms）。

### P2-1 意图分类（见 §7.3）
### P2-2 表达式编译器 + 状态机（见 §7.4 / §7.5）
### P2-3 ANN

若要支撑百万级向量，三条路：

| 方案 | 形态 | 说明 |
| :--- | :--- | :--- |
| `Vectorlite`（HNSW for SQLite） | 换扩展 | 10K+ 向量时比暴力快 3~100×，99.9% recall |
| `sqlite-vec` `vec0` + partition key | 换表 | 仍是暴力，但 C 实现 + 可按 KB 分区 |
| 外部服务（Qdrant） | 换架构 | 与"单文件部署"冲突，不推荐 |

> 推荐顺序：先把 P1-2（两阶段 + 有界堆）做完再评估 ANN。
> 因为 100 万块 × 1024 维 = 4GB，**内存装不下时任何"内存池"方案都失效**，那才是 ANN 的真正触发点。

---

## 十一、新增门禁设计

沿用上一轮的方法论：**结构门禁优先于时间门禁**，每条都要有**对照组**。

| # | 用例 | 锁住的不变量 | 若回归会怎样失败 |
| :-- | :--- | :--- | :--- |
| G8-1 | `CandidateBudget_MustHaveAbsoluteCap` | 源码必须出现绝对上限；100 万语料 `EstimatedMatchedDocs ≤ 5_000` | 有人把绝对上限删掉 → 立即红 |
| G8-2 | `FallbackAnchors_MustRespectBudget` | 纯虚词查询（「她的他」）在 57k 语料上 `EstimatedMatchedDocs ≤ 预算` | 兜底层回到全库扫描 |
| G8-3 | `AndTier_MustPrecedeOrTier` | `Queries[0]` 含 `AND`；AND 层命中 ≤ OR 层命中 | 有人删了 AND 层 |
| G8-4 | `AndTier_MustNotReturnEmpty_WhenTermsCoexist` | **对照组**：两词确实共现时，AND 层必须非空 | AND 层过严导致 0 结果 |
| G8-5 | `Coverage_MustRankAboveBm25` | 命中 9/10 词的低分文档必须排在命中 2/10 词的高分文档前 | 覆盖率被移除或降级为次键 |
| G8-6 | `FloorResult_MustBeFlagged` | `UsedRecallFloor=true` 时 `RecallItem.IsFloor=true` 或响应含标记 | 宽松兜底被伪装成精确命中 |
| G8-7 | `TermStats_MustMatchExactCount` | 随机 20 词，`lexical_term_stats.df` 与 `COUNT` 完全相等 | df 表漂移 |
| G8-8 | `VectorScan_MustUseBoundedHeap` | 结构：扫描路径不得出现全量 `OrderByDescending().Take(` | 有人改回全量排序 |
| G8-9 | `VectorSnapshot_MustNotRetainTexts` | 结构：`TensorSnapshot` 不得持有 `Texts` | 全库正文重新常驻内存 |
| G8-10 | `Recall_MustRespectBudget` | 10 万块 + 200ms 预算，实际 ≤ 500ms 且返回部分结果 | 超时机制失效 |
| G8-11 | `IntentClassifier_MustBeDeterministic` | 同一输入两次分类结果相同；三类意图各有正样本 | 引入随机性/模型依赖 |
| G8-12 | `Plan_MustBeStructurallyAssertable` | `LexicalQueryPlan.Clauses` 可枚举且类型正确 | 退回字符串拼接 |

**门禁设计要点**（补充上一轮 §8.3）：

6. **"更快的白屏"必须能被门禁抓到**：G8-6 是这一轮的 G4-11——
   上一轮的教训是"只锁快不锁对"，这一轮要额外锁"对的结果有没有被正确标记"。
7. **绝对上限要锁在常量上，不要锁在行为上**：`EstimatedMatchedDocs ≤ 5_000` 在 100 万语料上才可测，
   跑一次要几十秒；更好的写法是断言源码里有 `MaxCandidateDocs` 且 `Plan()` 里出现了 `Math.Min(`。
   —— **结构门禁秒级，行为门禁分钟级，能用结构就别用行为。**

---

## 十二、风险与"不做什么"

### 12.1 风险矩阵

| 风险 | 概率 | 影响 | 缓解 |
| :--- | :---: | :--- | :--- |
| 绝对上限导致召回下降 | 中 | 中 | 地板层 + 覆盖率排序兜底；`GiveUp` 时明确"无匹配" |
| AND 层过严导致 0 结果 | 中 | 低 | 自动 fall through；G8-4 对照组 |
| 覆盖率偏袒短文档 | 中 | 中 | 只对 `tokens.Count >= 6` 启用；用去重词覆盖率 |
| df 表漂移 | 中 | 中 | `ReconciliationJob` 对账 + `Repair` 全量重建 |
| 向量两阶段多一次回表 | 高 | 低 | ~1ms，远小于省下的排序与内存 |
| 意图分类误判 | 中 | 中 | 纯规则可解释；误判只影响路由不影响正确性 |
| 状态机重构引入行为差异 | 中 | 中 | 分阶段重构，每阶段行为等价 |

### 12.2 明确"不做什么"

| 不做 | 原因 |
| :--- | :--- |
| ❌ 把长句统一转成向量检索 | 向量把成本变常数，但把精度变成不可逆的有损压缩（§4.2） |
| ❌ 无脑 Hybrid | 混合只保证"至少一路对"，不解决任一侧退化，且延迟叠加 |
| ❌ 移植 SAG 的 BM25 上界剪枝 | 需自研 rank 共享统计量；判据数学上不充分；收益被 AND 层覆盖 |
| ❌ 引入 ES / Meilisearch | 换引擎不解决"查询构造 + 预算"，只增加部署与运维成本 |
| ❌ 给 FTS5 结果加查询缓存 | 长尾自然句命中率极低，且掩盖根因 |
| ❌ 用停用词表替代 df 剪枝 | SAG 的 45 词表缺 `她/着`，正是事故元凶；df 是语料自适应，停用词表是静态猜测 |
| ❌ 在 SQL 里算覆盖率 | 实测 199.56ms vs 内存 2.12ms（94×） |
| ❌ 假设"向量能兜住词法" | 生产默认库是 `PureLexical`，向量根本没启用 |

---

## 十三、附录

### 13.1 探针 A：FTS5 打分成本与候选集规模

```python
# 200,000 块合成语料：12 个高频虚词（各 ~75%）+ 5 个稀有词（各 0.1%）
import sqlite3, time, random
c = sqlite3.connect(':memory:')
c.execute("CREATE VIRTUAL TABLE t USING fts5(body, tokenize='unicode61')")
common = ['de','ta','ta1','zhe','ta2','shou','le','shi','zai','he','yu','huo']
rare   = ['xiaoxixi','zoushang','qianqu','las','tade']
random.seed(7)
rows = []
for i in range(1, 200001):
    toks = [w for w in common if random.random() < 0.75]
    toks.append('filler%d' % (i % 997))
    if i <= 200: toks.append(random.choice(rare))
    rows.append((i, ' '.join(toks)))
c.executemany("INSERT INTO t(rowid, body) VALUES(?,?)", rows)
c.execute("INSERT INTO t(t) VALUES('optimize')")

allor  = ' OR '.join('"%s"' % w for w in common)
pruned = ' OR '.join('"%s"' % w for w in rare)

def timed(label, sql, args=(), n=3):
    best = 1e9
    for _ in range(n):
        t0 = time.perf_counter(); r = c.execute(sql, args).fetchall()
        best = min(best, (time.perf_counter() - t0) * 1000)
    print(f"{label:<58} {best:8.2f} ms  rows={len(r)}")

timed("全OR ORDER BY rank LIMIT 10",        "SELECT rowid FROM t WHERE t MATCH ? ORDER BY rank LIMIT 10", (allor,))
timed("全OR ORDER BY rank LIMIT 100000",    "SELECT rowid FROM t WHERE t MATCH ? ORDER BY rank LIMIT 100000", (allor,))
timed("全OR COUNT(*)",                      "SELECT count(*) FROM t WHERE t MATCH ?", (allor,))
timed("全OR 带上限 COUNT(LIMIT 30000)",      "SELECT count(*) FROM (SELECT 1 FROM t WHERE t MATCH ? LIMIT 30000)", (allor,))
timed("锚点OR(5 稀有词) LIMIT 10",           "SELECT rowid FROM t WHERE t MATCH ? ORDER BY rank LIMIT 10", (pruned,))
timed("(全OR) AND 稀有词",                   "SELECT rowid FROM t WHERE t MATCH ? ORDER BY rank LIMIT 10", (f'({allor}) AND "xiaoxixi"',))
timed('短语 "de ta"',                       "SELECT rowid FROM t WHERE t MATCH ? ORDER BY rank LIMIT 10", ('"de ta"',))
timed('NEAR("de" "ta", 5)',                 "SELECT rowid FROM t WHERE t MATCH ? ORDER BY rank LIMIT 10", ('NEAR("de" "ta", 5)',))
```

**原始输出**：

```
语料: 200000 块, 12 个高频虚词各命中 ~75%

--- 打分成本：LIMIT 是否减少工作量 ---
全 OR（12 虚词）ORDER BY rank LIMIT 10                                369.71 ms   rows=10
全 OR（12 虚词）ORDER BY rank LIMIT 100000                            413.31 ms   rows=100000
全 OR（12 虚词）COUNT(*)                                               56.50 ms   rows=1
全 OR（12 虚词）带上限 COUNT(LIMIT 30000)                                  7.53 ms   rows=1

--- 剪枝后 ---
锚点 OR（5 个稀有词）ORDER BY rank LIMIT 10                                0.35 ms   rows=10

--- SAG 式：全 OR 再 AND 一个稀有词 ---
(12虚词OR) AND "xiaoxixi"                                          42.37 ms   rows=10

--- 短语/子串：原文引用场景 ---
短语 "de ta"（相邻）                                                116.62 ms   rows=10
NEAR("de" "ta", 5)                                             120.15 ms   rows=10

--- 覆盖率：内存 Contains vs SQL 逐词 IN ---
C# 式内存 Contains（2000 候选 × 12 词）                                2.12 ms   rows=2000
```

### 13.2 探针 B：高频词对 BM25 的贡献

```python
import sqlite3
c = sqlite3.connect(':memory:')
c.execute("CREATE VIRTUAL TABLE t USING fts5(body, tokenize='unicode61')")
rows = [(i, ('de filler' if i <= 950 else 'filler') + (' rare' if i <= 10 else ''))
        for i in range(1, 1001)]
c.executemany("INSERT INTO t(rowid, body) VALUES(?,?)", rows)

for expr in ('de', 'rare', 'de OR rare'):
    print(expr, '->', c.execute(
        "SELECT rowid, round(bm25(t),6) FROM t WHERE t MATCH ? ORDER BY rank LIMIT 3",
        (expr,)).fetchall())

d1_rare = c.execute("SELECT bm25(t) FROM t WHERE rowid=1 AND t MATCH 'rare'").fetchone()[0]
d1_both = c.execute("SELECT bm25(t) FROM t WHERE rowid=1 AND t MATCH 'de OR rare'").fetchone()[0]
print(f"doc1: rare={d1_rare:.6f}  de+rare={d1_both:.6f}  de 贡献={d1_both-d1_rare:.8f}")
```

**原始输出**：

```
MATCH 'de'          -> [(11, -1e-06), (12, -1e-06), (13, -1e-06)]
MATCH 'rare'        -> [(1, -3.73589), (2, -3.73589), (3, -3.73589)]
MATCH 'de OR rare'  -> [(1, -3.735891), (2, -3.735891), (3, -3.735891)]

doc1: rare=-3.735890  de+rare=-3.735891  de 贡献=-0.00000082
```

**公式验证**：`idf(rare) = log((1000-10+0.5)/(10+0.5)) = 4.547`；
BM25 长度因子（doc1 有 3 词、avgdl=1.96）= `2.2/(1+1.2*(0.25+0.75*3/1.96)) = 0.8218`；
`4.547 × 0.8218 = 3.7359` ✅ 与观测完全一致。
`idf(de) = log((1000-950+0.5)/(950+0.5)) = -2.93` → 被截断为 `1e-6` ✅。

### 13.3 SAG-Wiki 关键代码索引

| 机制 | 路径:行号 |
| :--- | :--- |
| FTS5 建表（contentless） | `core/src/store.rs:273-294` |
| 分词 + 停用词 | `core/src/text.rs:26-30, :73-82` |
| 全 OR 表达式（无剪枝） | `core/src/text.rs:140-154` |
| **稀有词子集 + 上界提前返回** | `core/src/store.rs:1984-2001` |
| **每词一条并行召回** | `core/src/store.rs:1839-1879` |
| **交错合并** | `core/src/retrieval.rs:147-165` |
| 只 hydrate top-k | `core/src/store.rs:1957-1968` |
| 自研 BM25 C 扩展 | `core/native/sag_rank.c:108-213` |
| 8 只读连接 + 2GB mmap | `core/src/store.rs:263, :366-373` |
| df 并行统计（列过滤） | `core/src/store.rs:2127-2157` |
| 查询缓存（按版本失效） | `core/src/store.rs:110-158` |
| **反例：快速搜索全表扫描** | `core/src/store.rs:1772-1804` |
| 官方取舍说明 | `docs/research/DigUp与SAG检索对比-2026-11-03.md:278-289` |

### 13.4 本项目关键代码索引

| 项 | 路径:行号 |
| :--- | :--- |
| 规划器配置项 | `Infrastructure/.../Search/Lexical/LexicalQueryPlanner.cs:10-37` |
| 候选预算（比例） | `:127` |
| 全虚词兜底（无预算） | `:140-151` |
| NEAR 门槛 | `:168` |
| 召回地板 | `:195-208` |
| 两阶段候选窗口 | `.../Storage/SqliteKnowledgeBaseStore.cs:1522-1523` |
| df 统计（逐词 COUNT） | `:973-1024` |
| 向量 SQL 全表扫描 | `:1878-1926` |
| SIMD 全量排序 | `.../Storage/InMemoryTensorProjectionPool.cs:193-208` |
| 全库正文常驻 | `:36, :44-74` |
| 融合（Z-Score） | `.../Search/Fusion/AdaptiveQueryAwareFusion.cs:61-105` |
| 策略工厂（未使用） | `Business/.../Search/Strategies/SearchStrategyFactory.cs:63-77` |
| 模式分派 if/else | `Business/.../Search/Services/SearchService.cs:248, :431, :1278` |
| `IsEarlyPruned` 未赋值 | `Infrastructure/.../Models/QueryModels.cs:242` |

### 13.5 术语

| 术语 | 含义 |
| :--- | :--- |
| **候选集（candidate set）** | `MATCH` 命中的文档集合，其大小决定 bm25 打分成本 |
| **覆盖率（coverage）** | 候选命中的查询词数 / 查询词总数 |
| **语义稀释（query dilution）** | 长查询被编码为单向量后，多个概念被平均，具体约束被抹平 |
| **上界剪枝（bound pruning）** | 用"被丢弃词的最大可能贡献"判断是否可提前返回 |
| **Block-Max WAND** | Lucene 8 的 top-k 剪枝算法，用块级得分上界跳过整块倒排 |
| **候选预算（candidate budget）** | 对候选集大小的硬约束，与 `topK` 解耦 |
| **召回地板（recall floor）** | 锚点全不命中时回落虚词，保证"有结果"优先于"够快" |

### 13.6 相关文档

| 文档 | 关系 |
| :--- | :--- |
| `docs/lexical-query-degeneration-and-first-query-18s-root-cause-and-fix.md` | 前置：词法侧 18 秒事故的根因、修复、14 条门禁、生产实测 |
| `docs/reader-bidirectional-continuation-and-search-wrap-optimization.md` | 同项目的搜索页/阅读器性能工作，方法论可参照 |
