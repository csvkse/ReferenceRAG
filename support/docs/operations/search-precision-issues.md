# 搜索精度影响问题

> 基于代码审查整理，commit `6d719cf`

---

## Issue-1：BM25 中文单字 OR 分词，噪声召回严重

**位置**：`Fts5BM25Store.cs` → `Tokenize` + `EscapeFtsQuery`

**现象**：

```
查询 "机器学习"
→ tokens: ["机","器","学","习"]
→ FTS5 MATCH: "机" OR "器" OR "学" OR "习"
```

任何含"机"、"器"、"学"、"习"任意一字的文档均命中，包括"机械""学生""习惯""仪器"等完全无关内容。

**根本原因**：缺少中文词语级分词（jieba/结巴等），退化为逐字 OR，召回率高但精度极差。

**影响量化**：混合搜索 BM25 权重 0.35，大量噪声文档获得 BM25 分数后混入候选集，拉高无关文档的 fusedScore，最终污染 Top-K 结果。查询词越短（如"学习"2字）问题越严重。

**改进方向**：
- 集成结巴分词（jieba-net / Python 进程调用）
- 或改用 phrase 查询（`NEAR` 操作符限制距离）
- 短期缓解：提高 BM25 最低分阈值，过滤低分命中

---

## Issue-2：切换 Embedding 模型后旧向量污染搜索

**位置**：`SqliteVectorStore.cs` 向量表 + `EmbeddingService.cs`

**现象**：
```
旧模型: bge-small-zh (dim=512)  → 已生成向量存于 vectors 表
切换到: bge-base-zh  (dim=768)  → 新查询向量 dim=768

向量表混存 512 维和 768 维向量
→ 维度不匹配的向量在搜索时行为未定义（取决于 VectorStore 实现）
→ 即使维度凑巧相同，不同模型向量空间完全不可比
→ 余弦相似度结果毫无意义
```

**触发条件**：切换模型后未主动执行 VectorOnly 重建（`/index/rebuild?vectorOnly=true`）。

**危险点**：无任何错误提示。搜索仍然正常返回结果，但结果质量随机退化，难以察觉。

**建议**：
- 切换模型时在配置中记录当前模型名
- 搜索时校验 `vectors.model_name` 与当前模型是否一致，不一致的向量跳过或警告
- 或切换模型时自动触发全量 VectorOnly 重建

---

## Issue-3：标题搜索固定分数与向量分数量纲不统一

**位置**：`SearchService.cs` → `ComputeTitlePriorityScore` + `TryTitleFirstSearchAsync`

**现象**：
```csharp
// 标题精确匹配强制 1.0
if (fileTitle.Equals(query)) return 1.0f;
// 标题包含匹配
if (fileTitle.Contains(query)) return 0.95f;
```

向量余弦相似度在实际场景中极少超过 0.90，通常集中在 0.70-0.85 区间。标题命中固定打 1.0，**必然排在所有向量结果之前**。

标题命中结果随后 `InsertRange(0, ...)` 插入候选集头部，精排前强制占据最前位置。

**场景复现**：
- 查询"配置"
- 全库有一个文件叫"配置.md"但内容是其他系统配置
- 真正需要的内容在"系统参数说明.md"中
- "配置.md"因文件名精确匹配打 1.0 排第一，正确结果被压后

**改进方向**：标题分数参与混合搜索融合，而非直接插入头部强制排前。

---

## Issue-4：图扩展邻居 chunk 无精排时无分数衰减

**位置**：`SearchService.cs` → `ExpandWithGraphAsync`

```csharp
// 图扩展 chunk 分数 = 纯余弦相似度，与直接召回结果同一量纲
var score = bestSim > 0 ? bestSim : fallbackScore;
```

图扩展的邻居是通过 wiki-link 结构关联的文件，语义相关度天然低于直接命中。但两者分数无差别混排。

**有精排（HybridRerank 模式）**：Reranker 重新评分，结构性关联噪声被压制，影响可控。  
**无精排（Hybrid 模式）**：邻居 chunk 若余弦相似度恰好偏高，直接排到直接召回结果之前。

**示例**：
```
直接召回: "数据库配置.md" chunk（相似度 0.82）
图扩展邻居: "数据库安装.md" chunk（相似度 0.85，但与查询无关）
→ 无精排时"数据库安装"排前
```

---

## Issue-5：流行度去偏惩罚力度过弱

**位置**：`SearchService.cs` → `DebiasByPopularity`

```csharp
var penalty = 1.0f / (1.0f + MathF.Log(count) * 0.1f);
```

| 同文件命中 chunk 数 | penalty | 实际惩罚 |
|---------------------|---------|---------|
| 1 | 1.000 | 0% |
| 5 | 0.877 | 12% |
| 10 | 0.813 | 19% |
| 50 | 0.733 | 27% |
| 100 | 0.697 | 30% |

大型文档（切片 100+，如知识库主索引页）被惩罚仅 30%，仍会主导搜索结果，细节内容文件的高相关 chunk 被压制排后。

**改进方向**：对超过阈值数量的 chunk 做更激进的惩罚，或限制同文件最多进入 Top-K 的 chunk 数量（per-file limit）。

---

## Issue-6：同义词扩展仅作用于 BM25，Embedding 不受益

**位置**：`HybridSearchService.cs`

```csharp
// 只有 BM25 做同义词扩展
var bm25Query = _synonymService?.ExpandQuery(query) ?? query;
var bm25Results = await _bm25Store.SearchAsync(bm25Query, ...);

// Embedding 直接用原始 query
var queryVector = await _embeddingService.EncodeAsync(query, EmbeddingMode.Query, ...);
```

Embedding 语义空间本身有泛化能力，同义词扩展不是必须的。但若同义词扩展使 BM25 召回了更多相关文档，这些文档在 Embedding 通道无对应分数，fusedScore 时 Embedding 分项为 0，最终融合分数被压低，**扩展效果被混合融合稀释**。

---

## Issue-7：Hybrid 模式候选池扩大但最终 trim 无排序保证

**位置**：`SearchService.cs`

```csharp
int recallTopK = request.TopK * rerankConfig.RecallFactor;  // 扩大候选池
// ...图扩展追加更多候选...
// 无精排时：
topResults = topResults.Take(request.TopK).ToList();         // 直接截断
```

候选集经过图扩展后追加到尾部（`results.Concat(additions)`），`Concat` 不排序。`Take(TopK)` 截断时，图扩展追加的高分 chunk 若排在列表后部，直接被截掉，图扩展形同虚设。

**验证**：`ExpandWithGraphAsync` 返回 `results.Concat(additions).ToList()`，没有 `OrderByDescending`。调用方也没有对合并后结果重新排序。

**改进**：图扩展后合并结果重新按 score 排序，再 Take(TopK)。

---

## 汇总

| Issue | 严重度 | 场景 | 状态 |
|-------|--------|------|------|
| 中文单字 OR 分词 | 高 | 所有中文查询 | 开放 |
| 切换模型旧向量污染 | 高 | 切换模型后未重建 | 开放 |
| 标题固定分数强制排前 | 中 | 查询词与文件名匹配但内容无关 | 开放 |
| 图扩展无精排时无衰减 | 中 | Hybrid 模式（无 Rerank） | 开放 |
| 流行度去偏过弱 | 中 | 大型文档主导结果 | 开放 |
| 同义词扩展被融合稀释 | 低 | 依赖同义词提升 BM25 召回时 | 开放 |
| 图扩展后结果未重排序 | 中 | Hybrid 模式 + 图扩展开启 | 开放 |
