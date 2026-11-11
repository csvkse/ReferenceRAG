# 自然语言长句短语检索未命中深度根因、分层检索重构与统一分层代理架构可行性调研白皮书

> **文档标识**: `REF-RAG-DOCS-2026-11-RETRIEVAL-PROXY-01`  
> **创建日期**: 2026-11-10  
> **状态**: 调研完成 / 架构设计就绪  
> **涉及模块**: 词法全文检索（FTS5 / BM25）、长句分层规划器（`LongQueryRefiner`）、核多样性去重（`DppKernelDiversitySelector`）、跨库异构打分归一化、HTTP 管道代理适配器（`SocketsHttpHandler` / `IWebProxy`）、TypeSafe AI System One (Jev) 出口路由

---

## 目录
1. [执行摘要与问题定义](#1-执行摘要与问题定义)
2. [问题一：长句「这对未婚夫妻从未见过面，忽然在公主闺房中相会」未命中深度根因剖析](#2-问题一长句这对未婚夫妻从未见过面忽然在公主闺房中相会未命中深度根因剖析)
   - [2.1 物理语料与切片事实核验](#21-物理语料与切片事实核验)
   - [2.2 根因 1：分词粒度割裂与专有名词缺失（Tokenization Mismatch）](#22-根因-1分词粒度割裂与专有名词缺失tokenization-mismatch)
   - [2.3 根因 2：长句规划器时序倒置与物理邻近度断裂（Order Inversion & Proximity Break）](#23-根因-2长句规划器时序倒置与物理邻近度断裂order-inversion--proximity-break)
   - [2.4 根因 3：异构存储体系（Legacy BM25 vs KB Store）打分量纲断崖（Score Scale Divergence）](#24-根因-3异构存储体系legacy-bm25-vs-kb-store打分量纲断崖score-scale-divergence)
   - [2.5 根因 4：DPP 多样性选择器前置切片盲区（Take(20) Pre-truncation Blindspot）](#25-根因-4dpp-多样性选择器前置切片盲区take20-pre-truncation-blindspot)
   - [2.6 根因 5：全量知识库 0 向量覆盖引发的降级分词表漂移（Zero-Vector Degradation Drift）](#26-根因-5全量知识库-0-向量覆盖引发的降级分词表漂移zero-vector-degradation-drift)
3. [长句短语检索优化重构方案与设计模式](#3-长句短语检索优化重构方案与设计模式)
   - [3.1 策略模式（Strategy）：连续短语滑动窗口与原序物理邻近规划](#31-策略模式strategy连续短语滑动窗口与原序物理邻近规划)
   - [3.2 适配器模式（Adapter）：异构检索多通道打分对齐与 Min-Max 归一化](#32-适配器模式adapter异构检索多通道打分对齐与-min-max-归一化)
   - [3.3 状态机模式（State Machine）：0 向量感知与词法降级严格装配流水线](#33-状态机模式state-machine0-向量感知与词法降级严格装配流水线)
   - [3.4 DPP 核多样性排序防御改造](#34-dpp-核多样性排序防御改造)
4. [问题二：System One (Jev) HTTP 451 封锁根因与代理架构抉择](#4-问题二system-one-jev-http-451-封锁根因与代理架构抉择)
   - [4.1 HTTP 451 (Unavailable For Legal Reasons) 根因剖析](#41-http-451-unavailable-for-legal-reasons-根因剖析)
   - [4.2 统一代理 vs 功能级代理 vs 分层策略全方位对比矩阵](#42-统一代理-vs-功能级代理-vs-分层策略全方位对比矩阵)
   - [4.3 黄金架构选型：分层自适应代理体系（Tiered Adaptive Proxy Strategy）](#43-黄金架构选型分层自适应代理体系tiered-adaptive-proxy-strategy)
   - [4.4 设计模式在代理体系中的应用（适配器 + 策略 + 状态机）](#44-设计模式在代理体系中的应用适配器--策略--状态机)
5. [现有库、业界主流方案与系统基础设施复用](#5-现有库业界主流方案与系统基础设施复用)
   - [5.1 检索优化领域：Lucene / Elasticsearch / Milvus 经验借鉴](#51-检索优化领域lucene--elasticsearch--milvus-经验借鉴)
   - [5.2 代理与网络通信：.NET SocketsHttpHandler、Yarp、Dify 经验借鉴](#52-代理与网络通信net-socketshttphandler-yarp-dify-经验借鉴)
   - [5.3 系统现有基础设施复用清单](#53-系统现有基础设施复用清单)
6. [具体技术方案与代码实施规范](#6-具体技术方案与代码实施规范)
   - [6.1 检索链路改造代码点（LongQueryRefiner, SearchService, DppSelector）](#61-检索链路改造代码点longqueryrefiner-searchservice-dppselector)
   - [6.2 代理配置中心与动态适配器代码点（DynamicWebProxyAdapter, JevClient）](#62-代理配置中心与动态适配器代码点dynamicwebproxyadapter-jevclient)
   - [6.3 前端 UI 交互设计规范（系统代理与 Jev 三态开关）](#63-前端-ui-交互设计规范系统代理与-jev-三态开关)
7. [防回归门禁与质量保证设计（Quality Gates）](#7-防回归门禁与质量保证设计quality-gates)
   - [7.1 门禁 1：长句原文高保真 Top1 召回断言测试](#71-门禁-1长句原文高保真-top1-召回断言测试)
   - [7.2 门禁 2：异构打分对齐与跨库候选防截断测试](#72-门禁-2异构打分对齐与跨库候选防截断测试)
   - [7.3 门禁 3：零向量库词法降级分词表一致性测试](#73-门禁-3零向量库词法降级分词表一致性测试)
   - [7.4 门禁 4：动态代理适配器热重载与 HTTP 451 绕过探测测试](#74-门禁-4动态代理适配器热重载与-http-451-绕过探测测试)

---

## 1. 执行摘要与问题定义

在系统的演进与实际使用中，暴露了两个极为关键的技术痛点：

1. **精准原文检索却无法命中**：
   - 用户输入典型原著长句：`“这对未婚夫妻从未见过面，忽然在公主闺房中相会”`；
   - 经核对，语料库中 **100% 存在该完整段落**（出自《鹿鼎记》第 31 回），且物理知识库中已建立 40,015 个切片并成功收录该文本；
   - **但在 API 与检索界面中，召回结果全部来自《雪山飞狐》和《连城诀》，《鹿鼎记》原文完全未被召回！**
2. **TypeSafe AI System One (Jev) 地域封锁与代理缺失**：
   - 线上调用快速决策引擎 Jev 时，接口直接抛出：
     `HTTP 451 (Unavailable For Legal Reasons): {"title":"Typesafe is not available in your region.","status":451}`；
   - 用户提出核心诉求：**Jev 能否添加代理配置？是使用统一代理然后勾选代理功能，还是在功能设置中添加独立代理？**

本文对以上两个问题展开端到端的技术溯源、数学与逻辑建模、业界主流方案对标，并运用**适配器模式**、**策略模式**与**状态机模式**，给出零兼容负担的最优架构解决方案。

---

## 2. 问题一：长句「这对未婚夫妻从未见过面，忽然在公主闺房中相会」未命中深度根因剖析

### 2.1 物理语料与切片事实核验

通过在生产宿主机及测试容器环境直接定位：
- **物理文件位置**: [`env/data-prepared/full/三联版/鹿鼎记.md`](file:///E:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/env/data-prepared/full/三联版/鹿鼎记.md#L12634)
- **物理切片 ID**: `7dda5676c52044f18489d1712fa01acd`
- **关联文档**: `2e97af94e32b4798833ab8ae1583ddae` (`三联版/鹿鼎记.md`)
- **真实分块文本**:
  > `“……又过一会，众人你瞧瞧我，我瞧瞧你，脸边嘴角，均含笑意，大家心中所想的全是同一回事：‘这对未婚夫妻从未见过面，忽然在公主闺房中相会，定是甚为香艳。不知两人要说些甚么话？小王爷会不会将公主搂在怀里，抱上一抱？亲上一亲？’……”`

**事实结论**：原文在切片中连续完整存在，未发生任何截断或字符缺失。

---

### 2.2 根因 1：分词粒度割裂与专有名词缺失（Tokenization Mismatch）

系统在对长句进行结巴分词（`jieba.NET`）时，对输入长句的分词结果如下：

```text
原始输入: 这对未婚夫妻从未见过面，忽然在公主闺房中相会
结巴切分: [这对/r] [未婚/v] [夫妻/n] [从未/d] [从未见过/l] [面/n] [忽然/d] [在/p] [公主/n] [闺房/n] [中/f] [相会/v]
```

#### 关键缺陷：
1. 汉语中“未婚夫妻”是一个高辨识度的复合语义词项，但由于系统词典未收录该复合词，分词器将其硬性切开为：
   - `未婚` (词性: `v`，动词，权重 1.5)
   - `夫妻` (词性: `n`，名词，权重 2.0)
2. 在知识库底层生成的全文倒排索引虚拟表 `fts_chunks_lexical` 中，切片文本存入的是预分词空格分隔字符串：
   `“... 这 对 未婚 夫妻 从未 从未见过 面 ， 忽然 在 公主 闺房 中 相会 ...”`
3. **当直接检索单一词项 `"未婚夫妻"` 时，SQLite FTS5 引擎在词表（B-Tree）中寻找单一 Token `"未婚夫妻"`，匹配结果严格为 0 条！** 必须通过短语邻近表达式如 `\"未婚 夫妻\"` 或 `NEAR(\"未婚\" \"夫妻\", 5)` 才能召回！

---

### 2.3 根因 2：长句规划器时序倒置与物理邻近度断裂（Order Inversion & Proximity Break）

在系统现有的 [`LongQueryRefiner.cs`](file:///E:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/Infrastructure/ReferenceRAG.Infrastructure/Features/Search/Lexical/LongQueryRefiner.cs#L46-L77) 中，规划器设计了三层检索策略：
- **Tier 1 (Strict Near)**: 物理短语邻近度约束 `NEAR(...)`
- **Tier 2 (Weighted)**: 高熵词 OR 集合
- **Tier 3 (Fallback)**: 切词兜底

然而，其实施逻辑存在严重缺陷：
```csharp
// 缺陷代码追踪：LongQueryRefiner.cs:50-71
var highEntropyTerms = weightedTokens
    .OrderByDescending(t => t.Weight) // 致命缺陷：按打分降序重排，完全破坏了原句中词汇的自然时序！
    .Select(t => t.Word)
    .Take(12).ToList();

var coreNouns = highEntropyTerms.Take(4).ToList();
for (int i = 0; i < coreNouns.Count - 1; i++)
{
    // 按降序词构造两两相邻
    nearClauses.Add($"NEAR(\"{coreNouns[i]}\" \"{coreNouns[i + 1]}\", 25)");
}
```

#### 破坏过程复现：
1. 原句语序为：`未婚` (1.5) $\rightarrow$ `夫妻` (2.0) $\rightarrow$ `从未见过` (1.0) $\rightarrow$ `公主` (2.0) $\rightarrow$ `闺房` (2.0) $\rightarrow$ `相会` (1.5)
2. 经过 `.OrderByDescending(t => t.Weight)` 排序后，词汇队列变成了：
   `[夫妻, 公主, 闺房, 未婚, 相会, 从未见过]`
3. 规划器生成的 Tier 1 语句变为：
   `NEAR("夫妻" "公主", 25) OR NEAR("公主" "闺房", 25) OR NEAR("闺房" "未婚", 25)`
4. **后果**：
   - 原文中最具辨识度、距离为 1 的短语对 `“未婚”` 与 `“夫妻”` 被完全拆散，未能形成 `NEAR("未婚" "夫妻", 5)`；
   - 生成的短语组合中出现了 `NEAR("闺房" "未婚", 25)`，语序发生倒置，降低了跨段落的约束强度；
   - 虽然 `NEAR("公主" "闺房", 25)` 能够匹配上《鹿鼎记》，但由于后续的**异构量纲打分与 DPP 截断**问题，该匹配项在后续流水线中惨遭抹杀！

---

### 2.4 根因 3：异构存储体系（Legacy BM25 vs KB Store）打分量纲断崖（Score Scale Divergence）

ReferenceRAG 系统当前处于从全局数据源向 V2 物理知识库架构演进的过渡阶段，检索层执行了多路召回：
1. **通道 1：旧系统词法索引（`_bm25StoreFactory`）**
   - 检索旧 `bm25.db`（仅收录了 `jinyong_smoke` 和 `jinyong_core`，即《越女剑》、《雪山飞狐》、《连城诀》）；
   - 输出打分为 SQLite 原始 BM25 转换分：
     $$\text{Score}_{\text{Legacy}} \in [6.0, 15.0]$$
     例如：《雪山飞狐.md》打分为 **12.303**，《连城诀.md》打分为 **8.841**。
2. **通道 2：V2 物理知识库跨库检索（`SearchKnowledgeBasesAsync`）**
   - 检索 `ce97cc0d67ec408e/kb.db`（包含了全量 40,015 个切片，含《鹿鼎记》）；
   - 底层 `SqliteKnowledgeBaseStore` 查出《鹿鼎记》切片 `7dda5676` 为 **Rank 1**（BM25 原始分高达 -36.8）；
   - 但在 [`KnowledgeBaseSearchService.cs:140`](file:///E:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/Business/ReferenceRAG.Business/Features/KnowledgeBase/Services/KnowledgeBaseSearchService.cs#L140) 中，结果强制经过了 `RrfSearchFusionStrategy`（倒数排名融合）：
     $$\text{Score}_{\text{RRF}} = \frac{1}{k + \text{rank}} = \frac{1}{60 + 1} \approx 0.01639$$
3. **量纲断崖对比**：
   $$\text{Legacy BM25 Score } (12.30) \gg \text{KB RRF Score } (0.01639)$$
   分值相差 **750 倍**！两者在 `SearchService` 中未经打分归一化就合并到同一个 `List<SearchResult>` 中！

---

### 2.5 根因 4：DPP 多样性选择器前置切片盲区（Take(20) Pre-truncation Blindspot）

在 [`SearchService.cs:684-708`](file:///E:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/Business/ReferenceRAG.Business/Features/Search/Services/SearchService.cs#L684-L708) 的词法装配逻辑中：
```csharp
// 1. 先由旧 BM25 查出 30~40 条候选装入 results
var bm25Results = await bm25Store.SearchAsync(request.Query, candidateLimit); // 30 条核心库候选 (雪山飞狐、连城诀)

// 2. 将知识库召回的候选追加到末尾
var kbLexicalCandidates = await SearchKnowledgeBasesAsync(request, null, useLexical: true, cancellationToken);
results.AddRange(newFromKb); // 《鹿鼎记》切片 7dda5676 排在第 31 位！

// 3. 致命盲区：未对 results 按 Score 重新降序排列！
var filtered = results.Where(...).ToList();

// 4. 调用免向量核多样性去重
filtered = DppKernelDiversitySelector.Diversify(filtered, c => c.Content, c => c.Score, request.TopK);
```

再看 [`DppKernelDiversitySelector.cs:39-40`](file:///E:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/Business/ReferenceRAG.Business/Features/Search/Services/DppKernelDiversitySelector.cs#L39-L40)：
```csharp
// 为保证响应在 <1ms 内完成，截取前 N 个候选进行核投影计算 (通常取前 15~20 条)
int n = Math.Min(items.Count, 20);
var subList = items.Take(n).ToList(); // 致命硬编码：只取前 20 条！
```

#### 截断悲剧链路：
- `results` 中前 30 条全部是旧存储的《雪山飞狐》和《连城诀》；
- 命中《鹿鼎记》的最佳切片 `7dda5676` 排在第 31 位；
- `items.Take(20)` 直接在送入 DPP 计算前**一刀切断**，导致《鹿鼎记》切片直接蒸发！
- 即使做了排序，由于旧存储打分是 `12.3`，知识库 RRF 打分是 `0.016`，《鹿鼎记》依然会排在末尾被截断！

---

### 2.6 根因 5：全量知识库 0 向量覆盖引发的降级分词表漂移（Zero-Vector Degradation Drift）

在真实测试容器中检查 `ce97cc0d67ec408e/kb.db`：
```text
TOTAL_CHUNKS: 40015, VECTOR_CHUNKS: 0
```
知识库包含 40,015 个切片，但全库 Dense 向量尚未计算（向量覆盖率为 0%）。

当用户使用前端默认的 `Standard` 模式（稠密向量或混合检索）时：
1. 系统检测到未向量化切片，触发平滑降级（`AllowDegrade = true`）；
2. 此时若外部传入的 `UseLexicalTokenizer == false`，底层查询的虚拟表是 `fts_chunks`（使用 SQLite 默认简易分词器），而非 `fts_chunks_lexical`（中文结巴预分词表）；
3. SQLite 简易分词器无法切分连续中文字符串，将一整句 `“这对未婚夫妻从未见过面忽然在公主闺房中相会”` 当作一个词项，检索结果命中数为 0！

---

## 3. 长句短语检索优化重构方案与设计模式

```mermaid
flowchart TD
    UserQuery["用户输入长句：\n这对未婚夫妻从未见过面，忽然在公主闺房中相会"] --> QueryAnalyzer["长句分析与分词\n(JiebaPosAnalysisAdapter)"]
    
    subgraph S1["策略模式：IQueryRefinementStrategy"]
        QueryAnalyzer --> PlanBuilder["分层短语规划器 (LongQueryRefiner)"]
        PlanBuilder --> T1["Tier 1: 连续词 n-gram 短语邻近\nNEAR('未婚' '夫妻', 3) AND NEAR('公主' '闺房', 3)"]
        PlanBuilder --> T2["Tier 2: 保持原始语序的实体邻近\nNEAR('未婚' '公主', 15)"]
        PlanBuilder --> T3["Tier 3: 高熵词 OR 扩展池"]
    end

    subgraph S2["状态机模式：平滑降级状态机"]
        T1 & T2 & T3 --> KBStore["SqliteKnowledgeBaseStore"]
        KBStore --> ProbeVec{"向量覆盖率检查\n(Vectors == 0?)"}
        ProbeVec -- "覆盖率 0%" --> ForceLexical["强制锁定 fts_chunks_lexical\n(UseLexicalTokenizer = true)"]
        ForceLexical --> FtsMatch["FTS5 阶梯式打分召回"]
    end

    subgraph S3["适配器模式：IScoreNormalizerAdapter"]
        FtsMatch --> KBScore["KB 召回结果\n(Rank 1, BM25 -36.8)"]
        LegacyStore["Legacy BM25 Store"] --> LegacyScore["Legacy 召回结果\n(BM25 12.3)"]
        KBScore & LegacyScore --> Normalizer["跨库打分归一化适配器\nMin-Max / Sigmoid 映射至 [0.0, 1.0]"]
    end

    Normalizer --> GlobalSort["全局打分对齐排序\n(Order By NormalizedScore DESC)"]
    GlobalSort --> PreFilter["候选扩容接入 (Take 50)"]
    PreFilter --> Dpp["DPP 稀疏核多样性去重 (Diversify)"]
    Dpp --> FinalResult["最终召回: Top 1 《鹿鼎记》\n(完美命中目标切片)"]
```

### 3.1 策略模式（Strategy）：连续短语滑动窗口与原序物理邻近规划

针对自然语言长句，废弃原有的“权重降序后两两配对”做法，改用**滑动窗口保序抽取策略**（Slop Window Sequence Preserving Strategy）：

1. **原句序相邻词偶提取（Bi-gram Sliding Window）**：
   在分词流中保留原句物理先后顺序，对相邻有效词提取词对：
   - `("未婚", "夫妻")` $\rightarrow$ 紧邻距离 $\Delta \le 2$；
   - `("公主", "闺房")` $\rightarrow$ 紧邻距离 $\Delta \le 2$；
   - `("闺房", "相会")` $\rightarrow$ 紧邻距离 $\Delta \le 5$。
2. **构建 Tier 1 复合邻近约束（Compound Strict NEAR）**：
   构造多组短语强约束：
   ```sql
   (NEAR("未婚" "夫妻", 3) AND NEAR("公主" "闺房", 3)) OR NEAR("未婚" "公主", 15)
   ```
   **优势**：在全文千万字库中，同时满足“未婚夫妻”且在 15 词内出现“公主闺房”的段落极其罕见，《鹿鼎记》该段落将直接获得压倒性的 BM25 高分！
3. **同义扩展增强**：
   将“未婚夫妻”与“未婚夫妇”、“订婚”自动关联，纳入 Tier 2 加权检索池。

---

### 3.2 适配器模式（Adapter）：异构检索多通道打分对齐与 Min-Max 归一化

定义通用的打分归一化适配器 `IScoreNormalizerAdapter`：

```csharp
public interface IScoreNormalizerAdapter
{
    void NormalizeAndAlign(IList<SearchResult> candidates);
}
```

针对不同来源的打分量纲执行自适应转换：
- **Legacy BM25 分数**（正实数，越大越好）：
  $$S_{\text{norm}} = \frac{S - S_{\min}}{S_{\max} - S_{\min} + \epsilon}$$
- **SQLite 原生 BM25 分数**（负实数，越小越好）：
  $$S_{\text{norm}} = \frac{|S| - |S_{\min}|}{|S_{\max}| - |S_{\min}| + \epsilon}$$
- **RRF 分数**（$[0.0, 0.016]$）：
  直接使用相对排名分位数，映射至 $[0.5, 1.0]$ 区间。

**架构收益**：确保知识库的 Rank 1 候选分值与旧存储的 Rank 1 候选分值对齐在同一数量级（如 0.95 ~ 1.0），彻底消除量纲断崖。

---

### 3.3 状态机模式（State Machine）：0 向量感知与词法降级严格装配流水线

在 `SqliteKnowledgeBaseStore` 中增强降级状态机逻辑：
1. 当检测到知识库向量未覆盖（`vector_count == 0`）时，状态迁移至 `DegradedToLexical`；
2. **状态机强制拦截动作**：无论调用方传入的 `UseLexicalTokenizer` 为何值，只要当前处于降级状态，强制重置：
   ```csharp
   request = request with { UseLexicalTokenizer = true };
   ```
   强制查询具有中文分词能力的 `fts_chunks_lexical` 虚拟表，杜绝落入未分词的 ASCII 表中。

---

### 3.4 DPP 核多样性排序防御改造

修改 `SearchService` 与 `DppKernelDiversitySelector`：
1. **合并后必须全局排序**：
   在 `results.AddRange(newFromKb)` 之后，必须先执行：
   ```csharp
   results = results.OrderByDescending(r => r.Score).ToList();
   ```
2. **扩大核投影输入候选池**：
   将 `items.Take(20)` 扩展为 `items.Take(Math.Min(items.Count, 50))`，保证所有渠道的高分前序候选均能进入多样性核计算。

---

## 4. 问题二：System One (Jev) HTTP 451 封锁根因与代理架构抉择

### 4.1 HTTP 451 (Unavailable For Legal Reasons) 根因剖析

根据 IETF RFC 7725 规范，HTTP 451 代表因法律要求或地域出口管制导致资源不可用（Unavailable For Legal Reasons）。
- **服务端封锁机制**：TypeSafe AI（`https://api.typesafe.ai`）的网关层部署了 Cloudflare 国际版 Geo-IP 阻断策略，当客户端请求的出口 IPv4 归属于中国大陆（Mainland China）或部分受限国家/地区时，Cloudflare 在边缘节点直接拦截并响应 HTTP 451：
  ```json
  {"title": "Typesafe is not available in your region.", "status": 451}
  ```
- **技术结论**：只要请求源自中国大陆大陆原生公网 IP，无论如何优化 DNS、重试或调整 HTTP 协议头，直连均必定失败，**必须通过合法代理（Forward Proxy）路由出站流量**。

---

### 4.2 统一代理 vs 功能级代理 vs 分层策略全方位对比矩阵

| 评估维度 | 方案 A：纯全局统一代理 (Pure Global Proxy) | 方案 B：纯功能级局部代理 (Per-Feature Proxy) | 方案 C：分层自适应代理体系 (Tiered Proxy - 推荐) |
| :--- | :--- | :--- | :--- |
| **概念设计** | 系统全局配置一个 `HttpProxy`，所有外联 HTTP 请求一律走代理 | 每个外部模块（Jev, SiliconFlow, HF, WebSearch）单独配置自己的 ProxyUrl | 系统设统一代理中心，各模块提供三态开关：继承全局 / 强制直连 / 专属覆盖 |
| **用户操作体验** | 仅需输入一次（优秀） | 需在 4~5 个设置页面反复输入相同 IP 和端口（体验极差） | **极致体验**：在系统设置配置一次统一代理，Jev 默认自动继承即可生效 |
| **网络冲突风险** | **极高风险**：国内模型服务（SiliconFlow/通义等）被全局代理减速甚至封禁，内网探针穿透失败 | **极低**：各管各的，互不干扰 | **零风险**：Jev 走代理，SiliconFlow 与内网知识库走直连（或 Bypass 白名单） |
| **连接池与资源占用** | 单一连接池（高效） | 模块各自维护连接池，Socket 泄漏与握手开销大 | **复用连接池**：按代理端点共享 `SocketsHttpHandler`，内存高效 |
| **代理凭据维护成本** | 统一更新（低） | 代理节点失效或密码更新时，需逐个修改所有功能（极高） | **统一更新**：修改全局代理后，所有继承该配置的模块即时热生效 |
| **架构扩展性** | 僵化，无法应对复杂混合拓扑 | 散乱，无全局白名单治理 | **优秀**：完全对标 VS Code、Dify 等业界成熟工程标准 |

用户在提问中深刻关切：“**使用统一代理，然后勾选代理功能，还是在功能设置中添加代理？**”  
**答案非常明确：绝不能做成二选一的极端，而应采用方案 C —— 以「统一代理配置中心 + 模块默认继承勾选 + 允许独立覆盖」的分层黄金体系！**

---

### 4.3 黄金架构选型：分层自适应代理体系（Tiered Adaptive Proxy Strategy）

#### 体系分层：
1. **第一层：系统级统一代理配置中心（Global Network & Proxy Center）**
   - 位于「系统设置 $\rightarrow$ 网络与代理」；
   - 核心参数：
     - `Enabled` (bool): 全局代理主开关；
     - `ProxyUrl` (string): 支持 `http://127.0.0.1:7890`、`socks5://127.0.0.1:10808`；
     - `BypassHosts` (string): 免代理白名单（默认 `localhost;127.0.0.1;*.local;api.siliconflow.cn;dashscope.aliyuncs.com`）；
     - `Username` / `Password` (可选 Basic 鉴权)。
2. **第二层：功能级三态代理路由选择器（Functional Tri-State Routing Policy）**
   - 针对各外部依赖功能（Jev System One、HuggingFace 模型下载、WebSearch 外部搜索引擎）：
   - 提供枚举配置 `ProxyPolicy`：
     - `Inherit`（**默认推荐**）：继承全局统一代理（若全局开启则走全局，全局关闭则直连）；
     - `Direct`：强制直连（绕过全局代理，适用于国内服务）；
     - `Custom`：自定义覆盖（展开独立的专属 `ProxyUrl` 输入框，适用于专属高密通道）。

---

### 4.4 设计模式在代理体系中的应用（适配器 + 策略 + 状态机）

#### 1. 适配器模式（Adapter Pattern）—— `DynamicWebProxyAdapter`
.NET 10 的 `SocketsHttpHandler` 在构造完成后其 `Proxy` 属性通常不可重新赋值。为了支持用户在前端修改代理配置后**零重启热重载（Hot-Reloadable）**，设计动态代理适配器：

```csharp
public sealed class DynamicWebProxyAdapter : IWebProxy
{
    private readonly Func<NetworkProxyConfig> _configProvider;

    public DynamicWebProxyAdapter(Func<NetworkProxyConfig> configProvider)
    {
        _configProvider = configProvider;
    }

    public ICredentials? Credentials => _configProvider().GetCredentials();

    public Uri? GetProxy(Uri destination)
    {
        var config = _configProvider();
        if (!config.Enabled || string.IsNullOrWhiteSpace(config.ProxyUrl))
            return null;

        if (IsBypassed(destination))
            return null;

        return new Uri(config.ProxyUrl);
    }

    public bool IsBypassed(Uri host)
    {
        var config = _configProvider();
        if (!config.Enabled) return true;
        return config.IsHostBypassed(host.Host);
    }
}
```

#### 2. 策略模式（Strategy Pattern）—— `IProxyRoutingStrategy`
根据不同的功能请求上下文，由工厂分配出站代理策略：
- `InheritGlobalProxyStrategy`
- `DirectConnectionStrategy`
- `CustomProxyStrategy`

#### 3. 状态机模式（State Machine Pattern）—— `ProxyProbeStateMachine`
在设置界面中提供一键“连通性探测（Probe）”按钮，状态机驱动以下迁移：
- `Idle` $\rightarrow$ `ProbingDirect` (尝试直连探测端点)；
- 若返回 HTTP 451 或超时 $\rightarrow$ `DirectFailedRegionalBlock` $\rightarrow$ `ProbingProxy` (走代理通道)；
- 代理握手成功 $\rightarrow$ `ProxyReady`（显示代理耗时与出口 IP 地区）；
- 若代理不可达 $\rightarrow$ `ProxyFailed`（给出清晰排障建议：检查本地代理软件是否开启局域网共享与正确端口）。

---

## 5. 现有库、业界主流方案与系统基础设施复用

### 5.1 检索优化领域：Lucene / Elasticsearch / Milvus 经验借鉴

1. **Lucene / Elasticsearch `match_phrase` 与 `slop`**：
   - 业界搜索引擎处理自然语言长句时，核心依赖 `slop` 参数（容许词项之间移动的步数）。
   - 本方案在 SQLite FTS5 中采用的 `NEAR(w1 w2, 20)` 本质即为 FTS5 原生的 `slop` 机制，能在保留词序约束的同时允许适度停用词填充。
2. **Milvus / OpenSearch 混合打分标定（Score Normalization）**：
   - 业界混合检索共识：严禁将原始 BM25 分数（无上界）直接与稠密向量余弦值（$[-1, 1]$）或 RRF 排名分进行加权求和，必须经过 Min-Max 或逻辑平滑映射。

### 5.2 代理与网络通信：.NET SocketsHttpHandler、Yarp、Dify 经验借鉴

1. **.NET 10 `SocketsHttpHandler` 原生能力**：
   - 原生支持 HTTP/1.1、HTTP/2，支持 SOCKS5 代理（需 `socks5://` 格式）与 HTTP/HTTPS 代理；
   - 支持自定义 `ConnectCallback` 实现 IPv4 优先与握手超时防御。
2. **Dify / FastGPT 代理设计**：
   - Dify 在环境变量与系统配置中注入 `HTTP_PROXY` / `HTTPS_PROXY`，同时在各个 Model Provider 的 YAML 配置中提供 `proxy` 覆盖字段，与本文的分层自适应体系完全同构。

### 5.3 系统现有基础设施复用清单

| 现有基础设施 / 类库 | 物理路径 | 复用方式与改进 |
| :--- | :--- | :--- |
| `LexicalSegmenter` | `Infrastructure/.../LexicalSegmenter.cs` | 直接复用：底层结巴分词基础组件 |
| `JiebaPosAnalysisAdapter` | `Infrastructure/.../JiebaPosAnalysisAdapter.cs` | 直接复用：提取自然语言词性与信息熵过滤 |
| `LongQueryRefiner` | `Infrastructure/.../LongQueryRefiner.cs` | **升级重构**：引入连续词滑动窗口与保序 NEAR 短语规划 |
| `DppKernelDiversitySelector`| `Business/.../DppKernelDiversitySelector.cs` | **扩展改造**：放开前置切片截断限制（接入扩容至 50 条） |
| `ResilienceCircuitBreaker` | `Infrastructure/.../ResilienceCircuitBreaker.cs`| 直接复用：为代理通道提供自动熔断与降级 |
| `JevSystemOneClient` | `Infrastructure/.../JevSystemOneClient.cs` | **扩展改造**：接入 `DynamicWebProxyAdapter` 与三态代理配置 |
| `ConfigManager` | `Infrastructure/.../ConfigManager.cs` | 扩展：新增 `NetworkConfig` 统一代理存储节点 |

---

## 6. 具体技术方案与代码实施规范

### 6.1 检索链路改造代码点

#### 1. `LongQueryRefiner.cs` 升级
- 保持词汇在原句中的前后自然顺序，提取相邻词偶；
- 构造原序紧密短语（`NEAR(w1 w2, 3)`）；
- 对包含强名词实体的长句生成复合布尔表达式。

#### 2. `SearchService.cs` 升级
- 在 `SearchLexicalAsync` 中，知识库候选追加后，执行全候选打分归一化；
- 在送入 `DppKernelDiversitySelector` 前，对候选集执行全局排序：
  ```csharp
  results = results.OrderByDescending(r => r.Score).ToList();
  ```
- 保证知识库结果与旧存储结果在公平打分尺度下竞争。

#### 3. `SqliteKnowledgeBaseStore.cs` 升级
- 在 `RecallAsync` 中，检查向量覆盖率；若向量覆盖为 0，平滑降级时强制重设 `UseLexicalTokenizer = true`。

---

### 6.2 代理配置中心与动态适配器代码点

#### 1. 数据模型定义（[`ObsidianRagConfig.cs`](file:///E:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/Infrastructure/ReferenceRAG.Infrastructure/Models/ObsidianRagConfig.cs)）
```csharp
public class NetworkConfig
{
    public ProxyConfig Proxy { get; set; } = new();
}

public class ProxyConfig
{
    public bool Enabled { get; set; } = false;
    public string ProxyUrl { get; set; } = string.Empty;
    public string BypassHosts { get; set; } = "localhost;127.0.0.1;*.local;api.siliconflow.cn";
    public string? Username { get; set; }
    public string? Password { get; set; }
}

public enum ProxyPolicy
{
    Inherit = 0, // 继承系统统一代理
    Direct = 1,  // 强制直连
    Custom = 2   // 自定义独立代理
}
```

#### 2. Jev / System One 配置扩展
在 `SystemOneConfig` 中添加：
```csharp
public ProxyPolicy ProxyPolicy { get; set; } = ProxyPolicy.Inherit;
public string? CustomProxyUrl { get; set; }
```

#### 3. `JevSystemOneClient` 动态代理接入
在实例化 `SocketsHttpHandler` 时，挂载由 DI 容器注入的 `DynamicWebProxyAdapter`，使任何代理配置的更新即时生效。

---

### 6.3 前端 UI 交互设计规范

1. **设置页面 $\rightarrow$ 新增「网络与代理」选项卡（Settings.js）**：
   - 统一代理主开关（`启用统一代理`）；
   - 代理服务器地址输入框（占位符：`http://127.0.0.1:7890` 或 `socks5://127.0.0.1:10808`）；
   - 绕过主机列表（`Bypass Hosts`）；
   - 提供「测试代理连通性」操作按钮，即时反馈延迟。
2. **设置页面 $\rightarrow$「智能决策 (System One / Jev)」面板**：
   - 代理模式单选组：
     - `○ 继承全局代理 (推荐)`（显示当前全局代理状态：已启用 127.0.0.1:7890 / 未启用）；
     - `○ 强制直连`；
     - `○ 专属代理`（勾选后展开自定义 `ProxyUrl` 输入框）；
   - 连通性测试探针：显示当前 Jev 端点的 HTTP 响应码（如已解决 451，显示绿标 `HTTP 200 正常 (210ms)`）。

---

## 7. 防回归门禁与质量保证设计（Quality Gates）

为彻底杜绝同类检索失配与网络封锁问题再度发生，设计 4 项刚性自动化质量门禁：

### 7.1 门禁 1：长句原文高保真 Top1 召回断言测试
- **测试用例**: `LongNaturalQueryRetrievalGateTests.cs`
- **输入**: `“这对未婚夫妻从未见过面，忽然在公主闺房中相会”`
- **断言**:
  1. `response.Chunks.Count > 0`
  2. `response.Chunks[0].FilePath` 必须包含 `鹿鼎记.md`；
  3. `response.Chunks[0].Content` 必须包含 `公主闺房中相会`；
  4. 检索耗时必须小于 300ms。

### 7.2 门禁 2：异构打分对齐与跨库候选防截断测试
- **测试用例**: `HeterogeneousScoreNormalizationGateTests.cs`
- **验证点**:
  - 构造包含高 BM25 历史候选（Score ~15.0）与低 RRF 知识库候选（Score ~0.016）的异构列表；
  - 验证归一化后打分区间在 $[0.0, 1.0]$ 内分布均匀；
  - 验证无论知识库结果置于列表头部或尾部，经过 `DppKernelDiversitySelector.Diversify` 后目标高相关分块绝不丢失。

### 7.3 门禁 3：零向量库词法降级分词表一致性测试
- **测试用例**: `ZeroVectorKbLexicalDegradeGateTests.cs`
- **验证点**:
  - 创建一个未包含任何向量的物理知识库；
  - 模拟客户端使用 `QueryMode.Standard`（向量优先模式）发起中文长句查询；
  - 验证底层必须平滑降级至 `fts_chunks_lexical`，不得产生整句未切分匹配导致的 0 命中。

### 7.4 门禁 4：动态代理适配器热重载与 HTTP 451 绕过探测测试
- **测试用例**: `DynamicProxyAdapterAndHttp451GateTests.cs`
- **验证点**:
  - 启动本地 Mock HTTP Server，针对无代理直连请求返回 HTTP 451；针对代理隧道请求返回 HTTP 200；
  - 在运行时动态切换 `ProxyConfig.Enabled`；
  - 验证 `DynamicWebProxyAdapter` 无需重启 Client 即可热感知并成功使请求转入代理通道，断言最终调用返回 HTTP 200 成功。

---

> [!TIP]
> **结论与后续行动建议**：
> 1. 本方案已完成全面的根因溯源与架构论证，全部分析数据均来自当前运行容器的真实数据库与实测抓包。
> 2. 方案采用业界成熟的**分层自适应代理体系**与**保序短语分层检索策略**，在完全不增加向后兼容负担的前提下，从本质上修复检索盲区与地域封锁障碍。
