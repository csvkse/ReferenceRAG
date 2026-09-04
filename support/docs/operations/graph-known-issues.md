# 图构建已知问题

> 基于代码审查整理，commit `6d719cf`

---

## Issue-1：同名文件导致链接永久解析为 external

**位置**：`GraphIndexingService.cs` → `BuildFilenameMap`

**现象**：
```
vault/
  A/foo.md
  B/foo.md

[[foo]] → filenameMap 中 "foo.md" 标记 ambiguous → 不进 map
        → 解析结果 = external 节点（悬空）
        → 图扩展时 external.ChunkIds 为空 → 跳过
```

同名文件只要存在两个，所有指向该名称的链接**永远**解析为悬空，无论实际目标是否明确。

**影响**：Obsidian vault 中同名文件很常见（如多个 `README.md`、`index.md`），这些文件之间的关联在图中完全丢失，图扩展无法覆盖。

**根本原因**：`BuildFilenameMap` 采用"有歧义就不解析"策略，没有上下文消歧（Obsidian 本身用最近路径规则消歧）。

---

## Issue-2：heading 节点 chunk_ids 用字符串包含匹配，精度低

**位置**：`GraphIndexingService.cs` → `UpdateGraphAsync`

```csharp
var headingChunks = chunkList
    .Where(c => c.HeadingPath != null && c.HeadingPath.Contains(heading))
    .Select(c => c.Id).ToList();
```

**现象**：标题"安装"会匹配所有 HeadingPath 含"安装"的切片，包括"快速安装""安装与配置""卸载与安装"等。heading 节点的 `chunk_ids` 范围偏大，不精确。

**影响**：图扩展时从 heading 节点取出的候选 chunk 包含无关切片，增加计算量，降低精准度。

---

## Issue-3：删除顺序错误导致 heading 边变孤儿（已修复，留档）

**位置**：`GraphIndexingService.cs` → `RemoveAsync`

**原始问题**：先删主节点再删 heading 子节点，主节点消失后 heading 的边无处挂靠，变成孤儿边。

**当前修复**：
```csharp
await _graphStore.DeleteHeadingNodesAsync(nodeId, ct);  // 先删子节点
await _graphStore.DeleteNodeAsync(nodeId, ct);           // 再删主节点
```

**残留风险**：`CleanupOrphanNodesAsync` 仅清理 `document` 和 `heading` 类型孤儿，`tag` 和 `external` 类型无清理逻辑，长期运行后 tag/external 节点会持续积累。

---

## Issue-4：图扩展深度实际上限为 2，与接口不一致

**位置**：`SearchService.cs` → `ExpandWithGraphAsync`

```csharp
var depth = Math.Clamp(config.GraphExpansionDepth, 1, 2);  // 强制最大 2
```

但 `GetNeighborsAsync` 接口支持 depth=3，`IGraphStore` 文档也说最大 3。配置项 `GraphExpansionDepth` 设为 3 时静默截断为 2，无日志提示，用户无感知。

---

## Issue-5：图扩展邻居 chunk 无分数衰减（无精排时影响大）

**位置**：`SearchService.cs` → `ExpandWithGraphAsync`

```csharp
// 邻居 chunk 分数 = 纯余弦相似度，无任何惩罚
var score = bestSim > 0 ? bestSim : fallbackScore;
```

邻居文件是通过 wiki-link 结构关联的，语义相关度天然低于直接命中结果。但图扩展的 chunk 和直接召回的 chunk 使用同一分数尺度混排。

**有精排时**：Reranker 用 query-document 交叉编码重新评分，图扩展噪声被压制。  
**无精排时**：邻居 chunk 若恰好余弦相似度高，会排在直接命中结果之前，导致结果跑偏。

**建议**：无精排路径对图扩展 chunk 乘以衰减系数（如 0.7），明确区分两类来源。

---

## Issue-6：incoming 边不随文件更新清理，可能指向旧 heading 节点

**位置**：`SqliteGraphStore.cs` → `UpsertFileGraphAsync`

更新文件时只清除：
- 该文件的 outgoing 边
- 该文件的 heading 子节点及其边

**不清除**：其他文件指向本文件 heading 节点的 incoming 边。

**场景**：
```
bar.md 含 [[foo#旧章节名]]
foo.md 重新索引，"旧章节名" 改为 "新章节名"
  → foo.md#旧章节名 节点被删除
  → bar.md → foo.md#旧章节名 的边仍然存在（指向已删除节点）
```

边存在但目标节点已消失，BFS 遍历时 `GetNodeInternal` 返回 null，结果中出现空节点。`CleanupOrphanNodesAsync` 不处理这类孤儿边。

---

## Issue-7：embed 和 wikilink 类型边对同一目标可能重复召回

**位置**：`WikiLinkExtractor.cs` + `GraphIndexingService.cs`

同一行 `![[foo]]` 产生 type=embed 的边，而隔壁段落 `[[foo]]` 产生 type=wikilink 的边，主键 `(from_id, to_id, type)` 不同，两条边均保留。

图扩展 BFS 时同一邻居节点通过两条不同类型的边被访问，`existingFileIds` 只在添加候选后去重，BFS 内部不去重，`traversalCache` 命中后跳过重复遍历，但同一邻居节点出现在 `traversal.Nodes` 两次时仍会被处理两次（`neighborNodes` 不去重）。

**影响**：轻微，最坏情况同一邻居文件被添加两次候选，被 `existingFileIds` 拦截，浪费一次向量查询。

---

## 汇总

| Issue | 严重度 | 状态 |
|-------|--------|------|
| 同名文件链接永久 external | 高 | 开放 |
| heading chunk_ids 包含匹配不精确 | 中 | 开放 |
| 删除顺序（Bug C） | 高 | 已修复 |
| tag/external 孤儿节点无清理 | 低 | 开放 |
| 图扩展深度上限静默截断 | 低 | 开放 |
| 图扩展无精排时无分数衰减 | 中 | 开放 |
| incoming 边指向已删 heading | 中 | 开放 |
| embed+wikilink 双边重复遍历 | 低 | 开放 |
