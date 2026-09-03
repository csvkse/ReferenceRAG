# 图构建技术文档

**核心服务**：`src/ReferenceRAG.Core/Services/Graph/GraphIndexingService.cs`  
**链接提取**：`src/ReferenceRAG.Core/Services/Graph/WikiLinkExtractor.cs`  
**存储实现**：`src/ReferenceRAG.Storage/SqliteGraphStore.cs`  
**接口**：`src/ReferenceRAG.Core/Interfaces/IGraphStore.cs`  
**已知问题**：[graph-known-issues.md](graph-known-issues.md)

---

## 图的本质

### 两张表，两种实体

图由两张 SQLite 表构成，节点和边完全分离存储：

```
graph_nodes 表 → 每行是一个实体（文件、标题、标签）
graph_edges 表 → 每行是两个实体之间的一条关系
```

**graph_nodes 示例**：

| id | title | type | chunk_ids | metadata |
|----|-------|------|-----------|----------|
| `E:/vault/foo.md` | foo | document | `["uuid1","uuid2"]` | `{}` |
| `E:/vault/foo.md#安装` | 安装 | heading | `["uuid1"]` | `{}` |
| `#运维` | #运维 | tag | `[]` | `{}` |
| `E:/vault/bar.md` | bar | external | `[]` | `{}` |

**graph_edges 示例**：

| from_id | to_id | type | line_number |
|---------|-------|------|-------------|
| `E:/vault/foo.md` | `E:/vault/bar.md` | wikilink | 5 |
| `E:/vault/foo.md` | `E:/vault/foo.md#安装` | wikilink | 12 |
| `E:/vault/foo.md` | `#运维` | tag | 3 |

### 核心设计原则

**标签也是节点，不是属性**：`#运维` 是一个 `type=tag` 的节点，`foo.md → #运维` 是一条 `type=tag` 的边。查"所有打了 `#运维` 标签的文件"等价于查"所有指向 `#运维` 节点的边的 `from_id`"。

**边描述方向和类型**：边只有 `(from, to, type, line_number)` 四个字段，无权重，无额外属性。边是单向的，但查询时双向检索（`from_id=@id OR to_id=@id`），所以出链和入链都能找到。

**节点分离于切片**：节点通过 `chunk_ids`（JSON 数组）关联到向量/BM25 层的切片，图层本身不存文本内容。查节点不碰边表，查邻居才联合两张表。

### 实体类型的含义

| type | 何时创建 | chunk_ids | 图扩展能用 |
|------|----------|-----------|-----------|
| document | 文件被索引时 | 该文件全部切片 | ✓ |
| heading | 文件内标题扫描时 | 含该标题路径的切片 | ✓ |
| tag | 文件含 `#tag` 时 | 空 | ✗ |
| external | 链接目标未索引时 | 空 | ✗ |

`external` 节点是占位符：被引用的文件一旦加入索引，对应节点升级为 `document`，`chunk_ids` 自动填充，图扩展随即可用。

---

## 数据模型

### 节点类型（graph_nodes）

| 类型 | ID 格式 | 含义 | chunk_ids |
|------|---------|------|-----------|
| `document` | `E:/vault/foo.md` | 已索引的 Markdown 文件 | 文件所有切片 ID |
| `heading` | `E:/vault/foo.md#章节名` | 文档内标题 | 含该 HeadingPath 的切片 ID |
| `tag` | `#tag名` | `#hashtag` 标签 | 空列表 |
| `external` | `E:/vault/bar.md` | 被引用但未索引的文件（悬空链接） | 空列表 |

### 边类型（graph_edges）

| type | 来源 |
|------|------|
| `wikilink` | `[[page]]` 或 `[[page#heading]]` |
| `embed` | `![[page]]` 嵌入引用 |
| `tag` | `#tag` 标签引用 |

### 数据库表结构

```sql
graph_nodes (
    id        TEXT PRIMARY KEY,    -- 路径 / #tag / path#heading
    title     TEXT,                -- 文件名 / 标题文本 / tag 名
    type      TEXT,                -- document/heading/tag/external
    chunk_ids TEXT,                -- JSON 数组：["uuid1","uuid2",...]
    metadata  TEXT                 -- JSON 对象（预留扩展）
);

graph_edges (
    from_id     TEXT,
    to_id       TEXT,
    type        TEXT,              -- wikilink / embed / tag
    line_number INTEGER,
    PRIMARY KEY (from_id, to_id, type)  -- 同类型重复边只更新行号
);

CREATE INDEX idx_edges_from ON graph_edges(from_id);  -- 正向链接查询
CREATE INDEX idx_edges_to   ON graph_edges(to_id);    -- 反向链接查询
```

`chunk_ids` 和 `metadata` 序列化为 JSON 字符串存储，读取时反序列化。

---

## WikiLinkExtractor：链接提取

三类正则，逐行扫描（代码块内跳过）：

### 1. Embed 链接（优先处理）

```
正则: !\[\[([^\[\]|#]+)(?:#([^\[\]|]*))?(?:\|[^\[\]]*)?\]\]

匹配示例:
  ![[page]]              → target="page.md", heading=null
  ![[page#heading]]      → target="page.md", heading="heading"
  ![[page#heading|alias]] → target="page.md", heading="heading"
```

Embed 先处理，处理完后从当前行删除（`EmbedRegex.Replace(line, "")`），避免被 WikiLink 正则二次匹配产生重复边。

### 2. Wiki 链接

```
正则: \[\[([^\[\]|#]+)(?:#([^\[\]|]*))?(?:\|[^\[\]]*)?\]\]

匹配示例:
  [[page]]               → target="page.md", heading=null
  [[page|alias]]         → target="page.md", heading=null（别名不影响目标）
  [[page#section]]       → target="page.md", heading="section"
  [[folder/page#section]] → target="folder/page.md", heading="section"
```

### 3. Hashtag

```
正则: (?<!\w)#([a-zA-Z一-龥][a-zA-Z0-9一-龥_/-]*)

匹配: #tag  #中文标签  #tag/subtag
不匹配: 代码中的 #define（\w 前向否定断言排除）
        纯数字标签 #123（首字必须是字母或中文）
```

### 目标路径规范化（NormalizeTarget）

```csharp
// 无扩展名 → 补 .md（Obsidian 默认行为）
"foo"        → "foo.md"
"folder/bar" → "folder/bar.md"

// 有扩展名 → 保持原样
"image.png"  → "image.png"
"note.md"    → "note.md"
```

---

## 图更新流程（UpdateGraphAsync）

```
输入: FileRecord file, string markdownContent, IEnumerable<ChunkRecord> chunks,
      Func<string, string?> resolveLink   ← filename → 完整路径映射函数
```

### 阶段一：CPU 计算（不访问 DB）

**步骤 1**：构建主节点（document 类型）

```csharp
var nodeId = NormalizeNodeId(file.Path);   // "E:\\notes\\foo.md" → "E:/notes/foo.md"
var node = new GraphNode {
    Id       = nodeId,
    Title    = file.Title ?? file.FileName,
    Type     = "document",
    ChunkIds = chunks.Select(c => c.Id).ToList()
};
```

**步骤 2**：扫描所有标题 → heading 节点

```csharp
// ExtractHeadings: 逐行匹配 ^(#{1,6})\s+(.+)，跳过代码块（``` toggle）
foreach (var (heading, _) in ExtractHeadings(markdownContent))
{
    var headingNodeId = $"{nodeId}#{heading}";
    var headingChunks = chunkList
        .Where(c => c.HeadingPath != null && c.HeadingPath.Contains(heading))
        .Select(c => c.Id).ToList();
    extraNodes.Add(new GraphNode { Id=headingNodeId, Type="heading", ChunkIds=headingChunks });
}
```

`seenNodeIds` HashSet 防止同文件同名标题重复 upsert。

**步骤 3**：WikiLinkExtractor 提取所有链接 → 构建边和节点

```
foreach (target, heading, type, lineNum) in extractor.Extract(content):

  case type == "tag":
    resolvedId = "#" + target
    extraNodes += GraphNode { Id="#tag名", Type="tag" }

  case type == "wikilink" / "embed":
    rawFileId = NormalizeNodeId(target)       // "bar.md"
    
    // 双级查找：先全路径，找不到再用短文件名
    resolvedFileId = resolveLink(rawFileId)
                  ?? resolveLink(Path.GetFileName(rawFileId))
    
    if resolvedFileId != null:
      resolvedId = heading != null
                   ? $"{resolvedFileId}#{heading}"   // 带锚点
                   : resolvedFileId
      if heading != null:
        extraNodes += heading 节点
    else:
      resolvedId = rawFileId (或 rawFileId#heading)
      extraNodes += GraphNode { Type="external" }    // 悬空链接
    
    edges += GraphEdge { FromId=nodeId, ToId=resolvedId, Type=type, LineNumber=lineNum }
```

### 阶段二：单事务写入（UpsertFileGraphAsync）

**清理旧数据**（同事务内，顺序固定）：

```sql
-- 1. 清该文件的所有 outgoing 边
DELETE FROM graph_edges WHERE from_id = @fileNodeId

-- 2. 清该文件的 heading 子节点的边（前缀匹配）
DELETE FROM graph_edges WHERE from_id LIKE 'E:/notes/foo.md#%'
                           OR to_id   LIKE 'E:/notes/foo.md#%'

-- 3. 清该文件的 heading 子节点
DELETE FROM graph_nodes WHERE id LIKE 'E:/notes/foo.md#%'
```

**不清除**：其他文件指向本文件的 incoming 边（保留反向链接）。

**写节点**：

```sql
INSERT INTO graph_nodes (id, title, type, chunk_ids, metadata)
VALUES (@id, @title, @type, @chunkIds, @metadata)
ON CONFLICT(id) DO UPDATE SET
    title = excluded.title, type = excluded.type,
    chunk_ids = excluded.chunk_ids, metadata = excluded.metadata
```

文件节点 + 所有 heading/tag/external 节点全部 UPSERT（`external` 节点不覆盖已存在的 `document` 节点，因主键冲突时用 `excluded` 值，而 external → document 升级在文件被索引后自然发生）。

**写边**：

```sql
INSERT INTO graph_edges (from_id, to_id, type, line_number)
VALUES (@from, @to, @type, @line)
ON CONFLICT(from_id, to_id, type) DO UPDATE SET line_number = excluded.line_number
```

同类型重复边（如 `[[foo]]` 在文中出现两次）只保留最后出现的行号。

最后 `COMMIT`。

---

## 文件名解析（BuildFilenameMap）

Obsidian 使用短文件名解析（`[[foo]]` 找全库任何目录下的 `foo.md`），`BuildFilenameMap` 构建这个映射：

```csharp
public static IReadOnlyDictionary<string, string> BuildFilenameMap(IEnumerable<FileRecord> files)
{
    var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    foreach (var file in files)
    {
        var filename = Path.GetFileName(file.Path);   // "foo.md"
        var nodeId = NormalizeNodeId(file.Path);

        if (ambiguous.Contains(filename)) continue;

        if (map.ContainsKey(filename))
        {
            map.Remove(filename);      // 发现同名，从 map 移除
            ambiguous.Add(filename);   // 标记为模糊，后续跳过
        }
        else
        {
            map[filename] = nodeId;
        }
    }
    return map;
}
```

| 情况 | 结果 |
|------|------|
| 全库唯一 `foo.md` | `"foo.md" → "E:/vault/A/foo.md"` |
| 两个目录都有 `foo.md` | `"foo.md"` 不在 map，链接解析为 external |
| `[[folder/foo]]` | 先尝试 `"folder/foo.md"`，找不到再尝试 `"foo.md"` |

---

## 文件删除

```csharp
RemoveAsync(filePath)
    ├── DeleteHeadingNodesAsync(nodeId)
    │   // DELETE FROM graph_edges WHERE from_id LIKE 'nodeId#%' OR to_id LIKE 'nodeId#%'
    │   // DELETE FROM graph_nodes  WHERE id      LIKE 'nodeId#%'
    └── DeleteNodeAsync(nodeId)
        // DELETE FROM graph_nodes WHERE id = @id
        // DELETE FROM graph_edges WHERE from_id = @id OR to_id = @id
```

先删 heading 子节点（Bug C 修复），再删主节点。顺序错误会导致指向 heading 节点的边变成孤儿。

---

## 邻居遍历（GetNeighborsAsync）

BFS 实现，最大深度限制 1–3（硬限制防大图爆炸）：

```
queue: [(nodeId, depth)]
while queue:
    current, remaining = dequeue
    if visited: skip
    visited.add(current)
    result.Nodes += GetNode(current)
    
    if remaining > 0:
        edges = GetEdges(current, edgeTypes?)   // from_id=@id OR to_id=@id
        result.Edges += edges
        for neighbor in edges:
            enqueue(neighbor, remaining - 1)
```

`edgeTypes` 参数可过滤边类型（如只走 `wikilink`，不走 `tag`）。双向查询保证反向链接（被引用）也出现在邻居中。

---

## 孤儿节点清理（CleanupOrphanNodesAsync）

两类孤儿分别处理：

```sql
-- 类型 1：document 节点对应文件已不在 files 表（文件被删除但图未更新）
SELECT id FROM graph_nodes
WHERE type = 'document'
  AND id NOT IN (SELECT id FROM files)

-- 类型 2：heading 节点的父 document 节点已消失
SELECT id FROM graph_nodes
WHERE type = 'heading'
  AND instr(id, '#') > 0
  AND substr(id, 1, instr(id, '#') - 1)
      NOT IN (SELECT id FROM graph_nodes WHERE type = 'document')
```

找到孤儿后单事务批量删除 `graph_edges` + `graph_nodes`。

---

## 节点 ID 规范化

所有路径统一处理，保证跨平台一致：

```csharp
private static string NormalizeNodeId(string path)
    => path.Replace('\\', '/').TrimStart('/');

// Windows: "E:\\notes\\foo.md"  → "E:/notes/foo.md"
// Unix:    "/home/user/foo.md"  → "home/user/foo.md"
// wikilink target: "foo.md"     → "foo.md"（已是相对路径）
```

---

## 图统计（GetStatsAsync）

单条 SQL 一次查询所有计数：

```sql
SELECT
  (SELECT COUNT(*) FROM graph_nodes)                               AS node_count,
  (SELECT COUNT(*) FROM graph_nodes WHERE type = 'document')      AS doc_count,
  (SELECT COUNT(*) FROM graph_nodes WHERE type = 'tag')           AS tag_count,
  (SELECT COUNT(*) FROM graph_nodes WHERE type = 'heading')       AS heading_count,
  (SELECT COUNT(*) FROM graph_nodes WHERE type = 'external')      AS external_count,
  (SELECT COUNT(*) FROM graph_edges)                              AS edge_count
```

`external_count` 高表示有大量悬空链接（引用了未索引的文件）。

---

---

## 增删改查逻辑总览

### 触发时机

图操作唯一入口是**文件索引**，文件增/改/删驱动图跟着变，无独立图管理接口。

### 插入（新文件）

```
扫描文件内容 → 提取所有链接和标题
→ 生成: 1 个 document 节点 + N 个 heading 节点 + M 条边
→ 被引用但不存在的目标 → external 节点（占位）
→ 全部写入 DB（单事务）
```

### 更新（文件内容变化）

不做 diff，直接全量替换：

```
清旧：DELETE 该文件的 outgoing 边 + DELETE heading 子节点（LIKE "path#%"）
      ← 其他文件指向本文件的 incoming 边不动

再写：UPSERT 文件节点（chunk_ids 同步更新）
      UPSERT 新 heading/tag/external 节点
      UPSERT 新边
```

### 删除（文件移除）

```
步骤 1: DeleteHeadingNodesAsync  ← 先清 heading 子节点及其边
步骤 2: DeleteNodeAsync          ← 再清主节点 + 双向边
```

顺序固定，反转会留下孤儿边（Bug C 修复点）。

### 查询

| 操作 | 方法 | SQL |
|------|------|-----|
| 按 ID 取节点 | `GetNodeAsync` | `WHERE id = @id` |
| 邻居遍历 | `GetNeighborsAsync` | BFS，`from_id=@id OR to_id=@id` |
| 标题模糊搜索 | `SearchNodesAsync` | `WHERE title LIKE '%query%'` |
| 统计 | `GetStatsAsync` | 单 SQL 一次查全部计数 |

---

## 边的构建：从文本到关系

### 三类语法对应三类边

```
[[foo]]          → wikilink 边，to = foo.md
![[img.png]]     → embed 边，  to = img.png
#tag             → tag 边，    to = #tag
[[foo#章节]]     → wikilink 边，to = foo.md#章节（同时建 heading 节点）
```

### 目标节点 ID 解析（filenameMap）

```
[[foo]] 中 raw_target="foo"
  → NormalizeTarget: 无扩展名补 .md → "foo.md"
  → filenameMap 查找：
      全库唯一 foo.md  → to = "完整路径/foo.md"  (document)
      多个 foo.md     → ambiguous → to = "foo.md" (external，永久悬空)
      找不到          → to = "foo.md"            (external，占位)
```

**注意**：同名文件歧义导致链接永久 external，是图精度的主要损耗点（见 Issue-1）。

### 边唯一性

主键 `(from_id, to_id, type)`：同类型重复边只保留最新行号，不产生重复记录。  
不同 type 算不同边（同目标的 `wikilink` 和 `embed` 边并存）。

---

## 邻居查询（BFS）

```
queue = [(nodeId, depth)]
while queue:
  (current, remaining) = dequeue
  if visited: skip
  result.Nodes += GetNode(current)
  if remaining > 0:
    SELECT * FROM graph_edges
    WHERE (from_id = @id OR to_id = @id) [AND type IN ...]
    → 出链 + 入链都返回（双向）
    enqueue(neighbor, remaining - 1)
```

- 深度硬限制 1–3（防大图爆炸）
- `idx_edges_from` + `idx_edges_to` 两个索引，BFS 无需全表扫

---

## 在搜索中的两处使用

### 用法一：标题优先检索

短查询（≤32字 或 ≤4词）并行触发：

```
graphStore.SearchNodesAsync("安装指南")
  WHERE title LIKE '%安装指南%'
  → 命中节点 → 从 chunk_ids 取对应切片
  → 打固定分（精确=1.0，包含=0.92-0.95）
  → 插入候选集头部
```

**问题**：固定分数与向量分量纲不统一，文件名匹配但内容无关时强制排前（见 search-precision-issues.md Issue-3）。

### 用法二：图扩展召回（ExpandWithGraphAsync）

召回后、精排前执行，配置项 `EnableGraphExpansion` 控制开关：

```
对每个召回结果（按文件去重）：
  GetNeighborsAsync(fileNodeId, depth=1-2)
  → 取邻居节点的 chunk_ids（最多10个）
  → 批量取向量，算余弦相似度
  → 取最高分 chunk 追加到候选集
```

**目的**：补充语义不相似但结构上关联（wiki-link）的文件内容。

**问题**：
1. 追加后未重排序，`Take(TopK)` 可能直接截掉高分邻居（Issue-7）
2. 无精排时邻居 chunk 无分数衰减，可能排在直接命中前（Issue-4）

---

## 节点与切片的关联

`chunk_ids` 字段桥接图层和向量/BM25 层：

```
document 节点.chunk_ids = 该文件所有切片 ID（全量）
heading 节点.chunk_ids  = HeadingPath 含该标题名的切片 ID（字符串包含匹配，精度有限）
tag 节点.chunk_ids      = []（空，只作图汇聚点）
external 节点.chunk_ids = []（空，图扩展时直接跳过）
```

拿到节点后无需额外查询即可取出关联切片，是图层与检索层的直接桥梁。

---

*文档基于 commit `6d719cf` 编写。*
