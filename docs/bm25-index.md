# BM25 全文索引技术文档

**实现**：`src/ReferenceRAG.Storage/Fts5BM25Store.cs`  
**接口**：`src/ReferenceRAG.Core/Interfaces/IBM25Store.cs`  
**依赖**：`SharedSqliteConnection`（与 VectorStore、GraphStore 共用同一连接）

---

## 底层：SQLite FTS5 虚拟表

```sql
CREATE VIRTUAL TABLE bm25_fts USING fts5(
    id      UNINDEXED,   -- chunk UUID，不进全文索引
    content,             -- 分词后空格分隔 token
    tokenize='unicode61 remove_diacritics 1'
);
```

- `UNINDEXED`：`id` 只做存储，不参与倒排索引，避免 UUID 字符污染词表
- `unicode61`：FTS5 内置分词器，处理英文够用，但中文按字符而非按词切分——写入前预处理解决此问题
- `remove_diacritics 1`：去除变音符，统一比较 café / cafe

---

## 分词策略（中英文混合）

读写操作均调用同一个 `Tokenize()` 方法，逐字扫描输入流：

```
┌─────────────────────────────────────────────────────┐
│ char 是中文？（7 个 Unicode 范围）                   │
│   → flush 当前英文 buffer → 单字作为 token           │
│                                                      │
│ char 是空白？                                         │
│   → flush 英文 buffer                                │
│                                                      │
│ char 是标点？（Unicode 标点分类，全类别）             │
│   → flush 英文 buffer，标点本身丢弃                  │
│                                                      │
│ 其他（英文字母/数字）                                 │
│   → 追加到英文 buffer                                │
└─────────────────────────────────────────────────────┘
最终：英文 token 转小写 + 过滤约 80 个停用词
```

中文 Unicode 范围覆盖：

| 范围 | 说明 |
|------|------|
| U+4E00–U+9FFF | 基本汉字 |
| U+3400–U+4DBF | 扩展 A |
| U+F900–U+FAFF | 兼容汉字 |
| U+20000–U+2A6DF | 扩展 B |
| U+2A700–U+2CEAF | 扩展 C/D/E |

**示例**：

| 输入 | 结果 tokens |
|------|------------|
| `"机器学习 Machine Learning"` | `["机","器","学","习","machine","learning"]` |
| `"the quick brown fox"` | `["quick","brown","fox"]`（the 是停用词） |
| `"RAG#检索增强"` | `["rag","检","索","增","强"]` |
| `"config.json 配置"` | `["config","json","配","置"]` |

---

## 写入流程

### 单条写入（IndexDocumentAsync）

```
TokenizeForIndex(content) → "深 度 学 习 ..."
DELETE FROM bm25_fts WHERE id = @id    ← 先清旧版本
INSERT INTO bm25_fts VALUES (@id, @tokenized)
```

### 批量写入（IndexBatchAsync）

```
单个 SQLite 事务
  遍历 (chunkId, content)：
    DELETE + INSERT 每条
COMMIT（失败 ROLLBACK）
```

**为什么用事务**：SQLite 默认每条 INSERT 开一个隐式事务，1000 条触发 1000 次 fsync。批量事务只 1 次 fsync，速度差距约 100x。

### 调用时机

`FileIndexPipeline.FinalizeAsync` 中无条件执行（包括 VectorOnly 模式），确保 BM25 索引始终与切片同步：

```csharp
await _bm25Store.IndexBatchAsync(ctx.Chunks.Select(c => (c.Id, c.Content)));
```

注意：BM25 使用 `c.Content`（原始文本），而 Embedding 使用 `c.EnhancedContent`（增强文本）。

---

## 查询流程

```
SearchAsync("机器学习", topK=10, k1=1.5, b=0.75)
    │
    ▼ Tokenize("机器学习") → ["机","器","学","习"]
    │
    ▼ EscapeFtsQuery → '"机" OR "器" OR "学" OR "习"'
    │
    ▼ SQL:
    SELECT id, content, bm25(bm25_fts, 1.5, 0.75) AS score
    FROM bm25_fts
    WHERE bm25_fts MATCH '"机" OR "器" OR "学" OR "习"'
    ORDER BY bm25(bm25_fts, 1.5, 0.75)
    LIMIT @topK
```

**BM25 参数**：

| 参数 | 默认值 | 含义 |
|------|--------|------|
| `k1` | 1.5 | 词频饱和系数，越高则高频词得分增长越快 |
| `b` | 0.75 | 文档长度归一化系数，1.0 完全归一化，0.0 不归一化 |

**返回值**：FTS5 `bm25()` 返回负值（越小越相关），代码用 `Math.Abs()` 转正后返回。

---

## 删除操作

### 删除指定 chunk（DeleteDocumentsByIdsAsync）

```
单事务：
  foreach chunkId:
    DELETE FROM bm25_fts WHERE id = @id
COMMIT
```

### 清空所有索引（ClearIndexAsync）

```sql
DELETE FROM bm25_fts
```

### 孤儿清理（CleanupOrphanDocumentsAsync）

找到 FTS5 中 `chunk_id` 不在 `chunks` 表的记录并删除：

```sql
SELECT id FROM bm25_fts
WHERE id NOT IN (SELECT id FROM chunks)
```

找到孤儿 → 单事务批量 DELETE。触发时机：手动调用 `/index/cleanup` API。

---

## 在混合搜索中的角色

`HybridSearchService` 同时运行 BM25 + Embedding，两路结果融合后返回：

### 同义词扩展（前处理）

BM25 查询前先做同义词扩展：

```csharp
var bm25Query = _synonymService?.ExpandQuery(query) ?? query;
```

`SynonymService` 将查询词扩展为包含同义词的扩展查询，提升关键词 miss 场景的召回率。

### 融合模式

**模式 1：加权平均（默认，`UseRRF=false`）**

```
normalizedBM25   = bm25Score / maxBm25Score
normalizedEmbed  = embedScore / maxEmbedScore
fusedScore       = 0.35 × normalizedBM25 + 0.65 × normalizedEmbed
```

BM25 权重 0.35，Embedding 权重 0.65（可配置，两者之和必须为 1.0）。

**模式 2：RRF（`UseRRF=true`）**

```
两路都有: fusedScore = 1/(k + bm25Rank) + 1/(k + embedRank)
仅一路有: fusedScore = 0.5/(k + rank)   ← 0.5 折扣惩罚单路命中
k = 60（平滑因子，可配置）
```

RRF 对绝对分数不敏感，适合两路分数量纲差异较大的场景。

### 结果中的内容优先级

BM25 存储的是**分词后 token**，不能直接展示。搜索结果内容按优先级：

```
向量搜索结果的 Content（原始文本）
  > 从 SQLite chunks 表读取的 Content（原始文本）
  > BM25 返回的分词内容（最后 fallback）
```

---

## 并发控制

`Fts5BM25Store` 与 `SqliteVectorStore`、`SqliteGraphStore` 共用 `SharedSqliteConnection` 的 `SemaphoreSlim(1,1)` 锁，所有写操作串行。每个方法进入时 `await _lock.WaitAsync()`，finally 中 `_lock.Release()`。

---

## 接口定义（IBM25Store）

```csharp
Task IndexDocumentAsync(string chunkId, string content);
Task IndexBatchAsync(IEnumerable<(string chunkId, string content)> documents, IProgress<int>? progress = null);
Task ClearIndexAsync();
Task DeleteDocumentsByIdsAsync(IEnumerable<string> chunkIds);
Task<List<BM25SearchResult>> SearchAsync(string query, int topK = 10, float k1 = 1.5f, float b = 0.75f);
Task<BM25IndexStats> GetStatsAsync();
Task<int> CleanupOrphanDocumentsAsync(CancellationToken cancellationToken = default);
```

`BM25SearchResult`：`{ ChunkId, Content, Score, Rank }`  
`BM25IndexStats`：`{ TotalDocuments, AverageDocLength, VocabularySize }`

词表大小通过 FTS5 `vocab` 虚拟表查询：

```sql
CREATE VIRTUAL TABLE temp.bm25_vocab USING fts5vocab(bm25_fts, 'row');
SELECT COUNT(*) FROM temp.bm25_vocab;
```

---

*文档基于 commit `6d719cf` 编写。*
