# 多知识库架构（v2）

v2 起，ReferenceRAG 的检索单元是**知识库（Knowledge Base）**：每个库拥有独立的 `kb.db`
（SQLite，含向量、词法倒排、文档与切片），彼此物理隔离，可各自绑定不同嵌入模型与分段策略。

旧版"单一 vectors.db + config.Sources"模型已停止写入新数据，仅作为历史残留保留。
**新入库内容必须通过知识库端点读取。**

---

## 1. 概念模型

| 概念 | 说明 | 端点上的一等标识 |
| :--- | :--- | :--- |
| 知识库 | 检索与授权的单元;独立 `kb.db` | `knowledgeBaseId`（16 位 hex） |
| 连接器 | 库内的数据源通道（本地目录 / 单文件） | `connectorId` |
| 文档 | 连接器下的一次入库结果 | `documentId`（GUID） |
| 切片 | 文档分块后的最小检索单元 | `chunkId`（GUID） |
| 嵌入规格 | 库固化的模型快照（自动生成 `spec_emb_<kbId>`） | `embeddingSpecId` |
| 分段规格 | 库固化的分段策略快照 | `chunkingSpecId` |

> 展示名（如 `BGE M3`）与身份（如 `bge-m3`）是两套字段，身份比较前会规范化。
> 因此"改名"不会影响检索与漂移判定。

---

## 2. 先选库，再检索

```bash
# 1) 列出所有库（含启用状态、绑定模型）
curl -s "http://localhost:7897/api/knowledge-bases"

# 2) 看某库挂载了哪些连接器、真实文件数/分块数
curl -s "http://localhost:7897/api/knowledge-bases/{id}/connectors"

# 3) 跨库（或多库）检索：KnowledgeBaseIds 留空 = 全部启用库（最多 8 库扇出）
curl -s -X POST "http://localhost:7897/api/knowledge-base-search" \
  -H "Content-Type: application/json; charset=utf-8" \
  --data-binary @- << 'EOF'
{"Query": "容器网络 bridge overlay", "KnowledgeBaseIds": ["<kbA>", "<kbB>"], "TopK": 8}
EOF
```

### 响应要点

| 字段 | 含义 |
| :--- | :--- |
| `Items[*].KnowledgeBaseId` | 该切片来自哪个库（跨库检索时用于区分来源） |
| `Items[*].ScoreKind` | `RRF`（多库融合）/ `Cosine` / `BM25` |
| `SuccessfulKnowledgeBaseIds` / `FailedKnowledgeBaseIds` | 扇出结果;`FailurePolicy=AllowPartial` 时部分失败仍返回 |
| `Degraded` / `DegradationReason` | 模型或服务级降级（如向量推理异常后回退词法） |
| `IndexDegraded` | 索引级降级（词法索引仍在后台补齐、向量覆盖不全） |
| `UsedRecallFloor` | **true 表示查询词在语料中不存在，只给了宽松兜底**——必须如实告知用户 |
| `EarlyPruned` | 级联早停（跳过部分向量推理） |

---

## 3. 精确词法检索

同一端点，body 加 `"UseLexicalTokenizer": true`：

```bash
{"Query": "IndexOutOfRangeException", "KnowledgeBaseIds": ["<kb>"], "TopK": 5, "UseLexicalTokenizer": true}
```

优点：不加载嵌入模型，延迟低；适合错误码、API 名、变量名、专有名词。
纯词法库（`embeddingSpecId = spec_lexical_only`）会自动走这条路。

---

## 4. 读取文档与切片

```bash
# 文档列表（search 按 source_key / locator 模糊匹配）
curl -s "http://localhost:7897/api/knowledge-bases/{id}/documents?page=1&pageSize=50&search=keyword"

# 某文档的切片（返回 Id / Ordinal / Text / StartLine / EndLine / HeadingPath / HasVector）
curl -s "http://localhost:7897/api/knowledge-bases/{id}/chunks?documentId={docId}&page=1&pageSize=50"

# 单切片全文
curl -s "http://localhost:7897/api/knowledge-bases/{id}/chunks/{chunkId}"

# 目录树与连接器路由（按库返回 Folders）
curl -s "http://localhost:7897/api/knowledge-bases/{id}/paths"
```

> 切片 URL 里的 `{id}` 是**知识库 ID**，不是文档 ID。切片属于特定库，跨库同名文档互不可见。

---

## 5. 配置漂移与重嵌入

```bash
# 漂移状态
curl -s "http://localhost:7897/api/knowledge-bases/{id}/drift"
```

| 状态 | 含义 | 处置 |
| :--- | :--- | :--- |
| `Aligned` | 模型与分段均与全局默认一致 | 无需操作 |
| `Customized` | 模型一致，分段策略不同 | 预期行为（用户主动选择） |
| `Drifted` | 库固化的模型与全局默认不同 | 可用蓝绿重构升级 |

```bash
# 蓝绿影子重构（作业化，返回 202 + jobId）
curl -s -X POST "http://localhost:7897/api/knowledge-bases/{id}/reindex" \
  -H "Content-Type: application/json" \
  -d '{"TargetEmbeddingSpecId":"local_bge-m3"}'

# 轮询作业
curl -s "http://localhost:7897/api/jobs/{jobId}"

# 回滚到上一代
curl -s -X POST "http://localhost:7897/api/knowledge-bases/{id}/reindex/rollback"

# 重新分段（不换模型）
curl -s -X POST "http://localhost:7897/api/knowledge-bases/{id}/rechunk" \
  -H "Content-Type: application/json" \
  -d '{"TargetChunkingSpecId":"spec_chunk_lexical_dense"}'
```

---

## 6. 常见误用

| 误用 | 后果 | 正确做法 |
| :--- | :--- | :--- |
| 用 `/api/ai/query` 当唯一入口 | 会合并旧单库召回，可能返回过期内容 | 多库场景走 `/api/knowledge-base-search` |
| 不传 `KnowledgeBaseIds` 却以为是单库 | 默认扇出所有启用库，结果可能来自不期望的库 | 显式传目标库 ID |
| 用源名（`sources`）当库标识 | v2 中库 ≠ 源;连接器才是库内的源 | 用 `knowledgeBaseId` |
| 忽略 `UsedRecallFloor` | 把宽松兜底当作精确命中告知用户 | 值为 true 时明确说明"未找到匹配内容" |
| 把切片 ID 放在别的库查询 | 404 / 空结果 | 切片 ID 与 `knowledgeBaseId` 必须配对使用 |
| 依赖 `kb.embeddingSpecId` 的原始字符串 | `spec_emb_<kbId>` 是不可读内部快照 | 用卡片/详情中的「模型名 (维度)」展示 |

---

## 7. 与 MCP 工具的对应

| MCP 工具 | 等价端点 | 关键参数 |
| :--- | :--- | :--- |
| `knowledge_base_list` | `GET /api/knowledge-bases` | 无 |
| `knowledge_base_search` | `POST /api/knowledge-base-search` | `query`、`knowledgeBaseIds`、`topK`、`useLexicalTokenizer` |
| `semantic_search` / `hybrid_search` / `lexical_search` | 同上（封装了模式差异） | `query`、`knowledgeBaseIds`、`topK` |
| `knowledge_base_drill_down` | `GET .../chunks` / `chunks/{chunkId}` | `knowledgeBaseId` + `chunkId` 或 `documentId` |
| `get_index_status` | 各库 `kb.db` 汇总 | 无（逐库返回 chunkCount / documentCount / vectorCount） |
| `list_sources` | `GET /api/Sources`（per-KB 连接器聚合） | `knowledgeBaseIds`（留空=全部启用库） |
| `list_files` | `GET .../documents` | `knowledgeBaseIds`、`sourceId`（关键词过滤）、`limit` |
| `get_document_info` | `GET .../chunks/{chunkId}` | `chunkId` + `knowledgeBaseId` |
