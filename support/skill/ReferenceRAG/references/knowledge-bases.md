# 多知识库架构与物理隔离 (v2)

> **服务地址**：`http://127.0.0.1:5123`
> **鉴权头**：`X-API-Key: rag_live_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx`

v2 架构下，ReferenceRAG 的核心存储与检索单元是**物理隔离的独立知识库**：每个知识库拥有独立的 `kb.db`（SQLite 物理数据库文件，内含向量索引、FTS5 BM25 词法倒排、文档与切片），彼此物理隔离。

旧版"单一 vectors.db + config.Sources"已废弃并停止写入新数据。**所有新入库数据均在独立知识库中管理。**

---

## 1. 当前生产环境活动知识库实况

| 知识库名称 | 知识库 ID (`knowledgeBaseId`) | 嵌入规格 (`embeddingSpecId`) | 索引规模 | 库类型与检索特性 |
|:---|:---|:---|:---|:---|
| **Notes** | `40745ffb28444648` | `spec_lexical_only` | 6,777 篇文档<br>237,721 个切片 | **纯词法库（BM25）**：无稠密向量索引。依托 SQLite FTS5 毫秒级倒排检索。**查专有名词、技术代码、API、错误码极其精准，零模型开销**；纯抽象查询需分解为关键词。 |
| **默认知识库** | `dadd847d18b54162` | `local_bge-m3` | 165 篇文档<br>24,776 个切片<br>24,776 维向量 | **混合向量库**：具备完整的 1024 维 `bge-m3` 向量索引与 FTS5 词法索引。**擅长概念推理、意图联想与抽象提问**。 |

---

## 2. 概念模型与实体关系

```
[知识库 (Knowledge Base)] ──> 独立 kb.db
     │
     ├── 包含 1~N 个 [连接器 (Connector)] (挂载本地目录或单文件)
     │        │
     │        └── 映射为 1~N 篇 [文档 (Document)]
     │                 │
     │                 └── 切分为 1~N 个 [切片 (Chunk)]
     │
     └── 固化 [嵌入规格 (EmbeddingSpec)] 与 [分块规格 (ChunkingSpec)]
```

- **知识库 ID (`knowledgeBaseId`)**：16 进制字符串，如 `40745ffb28444648`。
- **文档 ID (`documentId`)**：32 位 Hex，代表物理文档。
- **切片 ID (`chunkId`)**：32 位 Hex，检索的最小原子单元。
- **数据源键 (`sourceKey`)**：如 `1 工作/笔记/NET/Csharp进阶语法/7.3 异步的状态机设计.md`。

---

## 3. 多库检索与扇出机制 (Fanout & Fusion)

当发起跨库检索时，服务端执行受控并行度扇出：
1. **扇出上限**：单次最多并发扇出 **8 个知识库**。
2. **纯词法库自适应**：
   - 检索服务检测到目标库为 `spec_lexical_only` 时，自动跳过向量计算（`kbQueryVector = null`），强制走 FTS5 快速通道。
   - 响应中透出 `kbChannels: {"40745ffb28444648": "lexical"}`，透明告知客户端发生通道适配。
3. **多路融合算法 (RRF)**：
   - 采用互惠排名融合（Reciprocal Rank Fusion, $k=60$），综合向量排名与 BM25 词法排名：
     $$RRF\_Score = \sum_{d \in D} \frac{1}{60 + rank_i(d)}$$
   - 最终返回的 `ScoreKind` 为 `RRF`。

---

## 4. 常用管理接口

```bash
# 1. 查询所有可用知识库 (含 ID、名称、启用状态、规格)
curl -s "http://127.0.0.1:5123/api/knowledge-bases" \
  -H "X-API-Key: rag_live_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"

# 2. 查询某库挂载的连接器与物理文件统计
curl -s "http://127.0.0.1:5123/api/knowledge-bases/40745ffb28444648/connectors" \
  -H "X-API-Key: rag_live_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"

# 3. 分页查询某库的文档清单 (支持 search 关键词过滤)
curl -s "http://127.0.0.1:5123/api/knowledge-bases/40745ffb28444648/documents?page=1&pageSize=20&search=状态机" \
  -H "X-API-Key: rag_live_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"

# 4. 获取某切片完整正文 (根据 chunkId 下钻)
curl -s "http://127.0.0.1:5123/api/knowledge-bases/40745ffb28444648/chunks/{chunkId}" \
  -H "X-API-Key: rag_live_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"
```


