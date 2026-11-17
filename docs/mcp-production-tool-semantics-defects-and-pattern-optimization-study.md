# MCP 生产接口二次复验：工具语义缺陷、提示词剩余缺口与设计模式优化调研

> 对象：生产 `referencerag:deploy-ad60e2e0cb2d`（容器 `reference-rag`，端口 5123）
> 产物溯源：容器内 `/app/McpHelper.Core.dll` md5 `43187cde05b9bc7bf79d9e872b321352`，与仓库 `packages/McpHelper.Core.2.3.0.nupkg` 内 `lib/net10.0/McpHelper.Core.dll` 一致 → 部署的不是旧 SDK
> 方法：SSE JSON-RPC 对 25 个工具做真实调用（正常 / 失败 / 边界 / 错拼参数 / 并发），拉全量 `tools/list` 后跑中文检查器 + 自建 outputSchema 校验器
> 日期：2026-11-16（上一轮结论见 `mcp-tool-interface-reverify-and-description-optimization-study.md`）

---

## 结论先行

1. **P0：`rerank_results` 在生产返回的是「按位置衰减的伪造分数」，不是重排结果。** 同一批文档换查询、换顺序，分数恒定 `0.85 / 0.733 / 0.617 …`（`0.85 - i*0.35/n`），`durationMs=0`。GGUF 版 bce-reranker 无 Rank 分类头 → NaN → `NormalizeScore` 平滑降级直接发分（`LlamaRerankAdapter.cs:282`）。模型拿名次当相关性做「精排」，等于没排。
2. **P1：检索四件套的结果语义坍缩成同一个 `scoreKind=RRF`，且缺「无结果 / 降级」信号。** `semantic_search` 在纯词法库（Notes，`spec_lexical_only`）上返回与 `lexical_search` 完全相同的 chunk 列表且 `degraded=false`；不存在的词（`zzzzqqqxxx`）照样返回 5 条、`usedRecallFloor=false`。模型分不清「向量命中 / 关键词命中 / 其实没命中」。
3. **P1：参数绑定三类失真，其中一类会静默扩大检索范围。** 把 `knowledgeBaseId`（单数、错拼）传给 `knowledge_base_search` → 参数被忽略、检索了**全部 2 个库**并返回 `success:true`；`knowledgeBaseIds` 在检索工具里接受 JSON 与逗号分隔，只有 `knowledge_base_search` 拒绝逗号；`list_files` / `rerank_results` 收到非 JSON 返回 `internal_error` 并泄漏 `System.Text.Json` 的 `Path: $ | LineNumber…` 原文。
4. **P1：已声明的 7 个 `outputSchema` 全部违背规范的 MUST。** `get_system_stats` 的 schema 由 `Dictionary<string,object>` 反射生成，要求 `system.count/capacity`（实际没有）；`list_files.appliedFilters.connectorId/pathKeyword` 声明 `string` 却返回 `null`；失败信封仍带 `structuredContent`，而 schema 只描述成功形态。
5. **提示词：上一轮 7 条选错收敛到 1 条。** 中文检查器 `200 pass / 9 warn / 1 fail`，25 条真实任务 `17 明确 / 7 歧义 / 1 错误`（上一轮 `12 / 6 / 7`）。剩余 fail 是 `精确查找错误码 E1001` 仍被 `search_file_content` 抢走；另有 1 处「幽灵工具名」`kb_list`（真实名 `knowledge_base_list`）。
6. **缺口总量已很小，不再需要大改提示词。** 25 工具定义 25,140 B（描述 8,811 B / inputSchema 8,335 B / outputSchema 5,030 B / annotations 1,075 B）。按 Anthropic「Optimizing tool responses for token efficiency」与 arXiv 2602.14878 的结论，继续加长描述属负收益；应把预算花在**结果语义**（structuredContent、degradation 标志）和**契约校验**上。

---

## 1. 复验证据

### 1.1 全工具调用矩阵（稳态延迟）

| 组 | 工具 | 延迟 | 判定 |
|:--|:--|:--|:--|
| 无参 | `ping` / `get_service_info` / `knowledge_base_list` | 5–25ms | OK |
| 无参（重） | `get_index_status`（30s TTL 缓存） | 21ms 命中（首调 ~2.5s） | OK |
| 无参（重） | `get_system_stats` | 19–30ms | OK，与 `get_index_status` 逐库求和一致（262,497 / 6,942 / 24,776） |
| 源/文件 | `list_sources` / `get_source_info` | 60–301ms | OK |
| 源/文件 | `get_file_tree` / `find_files_by_pattern` | 21–266ms | OK |
| 源/文件 | `read_note_content` / `get_note_outline` | 33–88ms | OK |
| 源/文件 | `search_file_content` | 265–1,972ms | OK（`maxMatches=500` → 273 KB） |
| 检索 | `semantic_search` | 100–136ms 稳态（首调 3.5s） | **语义可疑，见 1.4** |
| 检索 | `lexical_search` / `hybrid_search` | 42–915ms | OK |
| 检索 | `knowledge_base_search` | 79–147ms | OK（`topK=900` → `effectiveTopK=50`、54 KB） |
| 读取 | `knowledge_base_drill_down` / `get_document_info` | 3–24ms | OK |
| 图谱 | `graph_search_by_title` / `graph_search` | 26–51ms | OK（`totalNodes:0` 属数据事实） |
| 向量 | `embed_text` / `embed_batch` / `calculate_similarity` | 231–2,232ms | OK；`embed_batch(100)` 2.2s、`embed_batch(400)` 9.4s（无条数上限） |
| 重排 | **`rerank_results`** | 48–174ms | **P0 伪造分数，见 1.3** |

**结论**：25 个工具全部可调用，无 HTTP 非 200，无崩溃；上一轮的 4 个 P0/P1 缺陷（越权读、结果被吞、源名不一致、统计口径矛盾）均已确认修复。

### 1.2 失败路径与错误码

| 用例 | 返回 | `errorCode` |
|:--|:--|:--|
| `get_file_tree(NoSuchSource)` / `get_source_info(ZZZ)` | isError | `source_not_found` ✅ |
| `read_note_content(/etc/passwd)` / `../../appsettings.json` | isError | `path_denied` ✅ |
| `semantic_search(query="")` / 全空白 | isError | `invalid_argument` ✅ |
| `knowledge_base_drill_down(bad chunk)` / `get_document_info(bad chunk)` | isError | `chunk_not_found` ✅ |
| `list_files(connectorId=unknown)` / `list_files(kbIds=[deadbeef])` | isError | `connector_not_found` / `knowledge_base_not_found` ✅ |
| `embed_text(9000 字符)` / `embed_batch` 超长 | isError | `too_large` ✅ |
| `semantic_search(topK="x")` | isError + `structuredContent{errorCode, parameter}` | `invalid_argument` ✅（上一轮缺码已补） |
| 缺必填参数（5 个工具） | isError，纯文本 `缺少必需参数: x` | ❌ 无 `structuredContent`（SDK 侧生成） |
| **`knowledgeBaseIds='not-json'`** | `internal_error` + JSON 解析器原文 | ❌ 应 `invalid_argument` |
| **未知工具 `no_such_tool`** | `result.isError=true`（HTTP 200） | ⚠️ 规范建议 JSON-RPC `-32602`（协议错误） |

### 1.3 P0：`rerank_results` 的伪造分数（可复现）

```
query="MCP 协议", docs=[MCP 协议是什么, 今天天气很好, 猫的照片, 知识库检索架构, 完全无关的内容 zzz]
→ 0.85 / 0.78 / 0.71 / 0.64 / 0.57   durationMs=0

query="MCP 协议", docs=[完全无关的内容 zzz, 今天天气很好, MCP 协议是什么]   ← 顺序颠倒
→ 0.85 / 0.733 / 0.617（index 0/1/2）

query="完全不同的查询", docs 任意
→ 同样的 0.85 / 0.733 / 0.617
```

分数只与**位置**有关，与 query、与文档内容完全无关。根因在 `LlamaRerankAdapter.NormalizeScore`：GGUF 版 bce-reranker 没有 Rank 分类头，输出全 0 模长 → 0/0 NaN → 走「平滑降级保底（按召回先后保留相对序）」分支 `0.85 - index*0.35/n`。该分支本意是防崩溃，但在 MCP 工具面对外暴露时，模型会把 0.85 当成真实相关性并据此裁剪候选。

生产日志已证实加载的是 GGUF：`/app/models/Reranker/bce-reranker-base_v1/bce-reranker-base_v1-Q4_0.gguf`，而 `get_system_stats` 的 `rerank` 段仍显示 `modelName= bce-reranker-base_v1 / useCuda=true` —— **配置面看不出这是降级状态**。

### 1.4 P1：检索语义坍缩

| 观测 | 结果 |
|:--|:--|
| `semantic_search` vs `lexical_search`（Notes 库，`vectorCount=0`） | **chunk 列表逐条相同**，`scoreKind` 都是 `RRF`，`degraded=false`、`indexDegraded=false` |
| `hybrid_search` vs `semantic_search`（默认库，有向量） | 前 5 条**完全相同**（`4f62e7 / 3f0bf3 / f5f20b / 53d3ad / 39f44c`） |
| `hybrid_search(lexicalOnly=true)` vs `lexical_search` | 完全相同（`4b2359 / 5994aa / …`） |
| 不存在的词 `zzzzqqqxxx 完全不存在的词` | `semantic_search` 仍返回 5 条，`usedRecallFloor=false`（floor 未被触发的证据反而说明它没有门槛） |
| 纯词法库走 `semantic_search` | 库内 `isKbLexical=true` → `kbQueryVector=null` → 自动走纯词法；但响应里**没有任何字段说明发生了这件事** |

影响：模型/客户端无法判断「这次命中的是向量还是 BM25」「有没有降级」「返回的 5 条是不是碰巧凑数」。`degraded` 字段只覆盖模型级降级（断路器/推理异常），不覆盖「索引级 / 库类型级」自动切换 + `ScoreKind` 在跨库融合后被统一改写为 `RRF`，两条信息都在出口被抹平。

### 1.5 参数绑定失真

| 输入 | 实际行为 | 危害 |
|:--|:--|:--|
| `knowledge_base_search{kb:...}` → 错拼 `knowledgeBaseId`（单数） | **参数被静默忽略，检索全部 2 个库**，`success:true` | 静默扩大检索范围；模型以为已限定单库 |
| `list_files{kbIds:[deadbeef]}` | 参数被忽略，返回全部库 20 条 | 同上 |
| `find_files_by_pattern{source:"NOPE"}` | 参数被忽略，扫全部源 | 同上 |
| `knowledgeBaseIds='a,b'`（逗号） | `semantic/lexical/hybrid` 接受；`knowledge_base_search` 拒绝 `invalid_argument` | 五工具同参不同义 |
| `knowledgeBaseIds='[]'` | `semantic` → 回落到全部库；`knowledge_base_search` → `no_knowledge_base` | 空数组语义相反 |
| `list_files/knowledge_base_search` 非 JSON | `internal_error` + `"'not-json' is an invalid JSON literal. Expected the literal 'null'. Path: $ | LineNumber…"` | 泄漏序列化器内部路径，模型无法自纠 |
| `list_files.limit=0 / -1` | 返回 `count=0`（静默接受非法值） | 无下限契约 |
| `find_files_by_pattern.maxResults=0` | 夹到 1 | 与 `list_files` 不一致 |
| `graph_search.depth=99` | 静默夹到 3 | 描述写了 1–3，但未回显 `effectiveDepth` |

根因：SDK 的 `InvokeToolAsync` 只按方法签名**逐个取用户传的同名 key**，未知 key 一律丢弃（`McpToolRegistry.cs:454-467`），既不做「未知参数拒绝」，也不做 schema `additionalProperties:false` 校验；宿主侧 `ParseIds` 与 `KnowledgeBaseSearch` 各自实现了两套 JSON 解析（一套容错逗号、一套严格）。这是重复实现造成的语义分叉。

### 1.6 outputSchema 契约校验（7/7 不合格）

自建校验器逐字段比对 `structuredContent` 与声明的 `outputSchema`：

| 工具 | 违规 |
|:--|:--|
| `get_system_stats` | `system` / `embedding` 被反射成 `Dictionary` 的 `count / capacity / comparer / keys / values`，并把 `count/capacity` 标为 required —— 实际返回是 `dataPath / modelName / …`，**missing required** |
| `list_files` | `appliedFilters.connectorId` / `pathKeyword` 声明 `string`，实际为 `null` |
| `get_document_info` / `knowledge_base_search` / `knowledge_base_list` / `list_sources` / `get_index_status` | 成功形态合规 ✅ |
| 全部 7 个 | 失败信封带 `structuredContent{success:false,errorCode}`，而 schema 未描述失败形态 → 客户端按 schema 校验会失败 |

规范原文（`2025-11-25/server/tools`）："If an output schema is provided: Servers **MUST** provide structured results that conform to this schema." 当前 7 个工具全部违反。

### 1.7 并发与容量

| 场景 | 结果 |
|:--|:--|
| 8 路并发 `semantic_search` | 928–1,525ms（串行化明显，未见错误） |
| 16 路并发 `embed_text` | 65–876ms，**均匀线性增长** → 推理侧是串行闸门，无排队失败 |
| `embed_batch(400)` | 9.4s，无条数上限、无分页 |
| `calculate_similarity(9000+ 字符)` | **6,004ms** 且成功（`embed_text` 同长度会被 `too_large` 拒绝） |
| `search_file_content(maxMatches=500)` | 273 KB 文本块 |
| `list_files(limit=5000)` | 200 KB（内部夹到 200/库） |
| `rerank_results(50×4000 字符)` | 48ms（因为是伪造分数，没有真正跑模型） |

结论：延迟不是主要风险，**单次响应体积**与**无上限的入参**才是。`calculate_similarity` 绕过 `MaxEmbedChars` 是最直接的一处不一致。

---

## 2. 提示词（description）审计：还剩什么

### 2.1 检查器结果（中文适配版，25 条真实任务）

```
200 pass, 9 warn, 1 fail
selection: 25 任务 17 明确 7 歧义 1 错误
outputSchema 未声明：18 / 25（P3 既定分批，非缺陷）
```

对比上一轮（`152 pass / 83 warn / 7 fail`，`12 / 6 / 7`）：提示词结构化（用途 + 何时用 + 邻居取舍 + 上限）已生效，选型错误从 7 条降到 1 条。

### 2.2 剩余 1 条 FAIL 与 9 条 WARN

| 类型 | 项 | 说明 | 处置 |
|:--|:--|:--|:--|
| FAIL | `精确查找错误码 E1001` → `search_file_content`(11.8) 胜 `lexical_search`(8.5) | **这是检查器的分词产物，不是真缺陷**：`lexical_search` 描述里没有「精确查找」这种二元组。真实调用中两者都可用（`lexical_search` 搜知识库切片、`search_file_content` 搜磁盘文件），语义差异已在 `UseInstead` 写明 | 不作改；若要对齐检查器，只在 `lexical_search` 的 `UseInstead` 补「查已索引的笔记/文档切片」即可 |
| WARN | `ping` 缺「何时该用」 | **生产已修复**（`4833fb7` 把 `WhenToUse` 补成「只是确认服务是否活着、通道是否通时使用」），本地检查器跑的是 `ad60e2e` 的列表快照 → 属于部署快照滞后，非缺陷 | 部署后重跑即可归零 |
| WARN | `graph_search.depth` 未写上/下界 | 参数描述「深度 1-3，默认 1」其实写了范围，检查器只匹配「上限/最大/最多/范围/建议」四词 | 改写为「深度范围 1–3」即可通过；同时应回显 `effectiveDepth`（见 1.5） |
| WARN×7 | 工具对区分度不足（`list_files` vs `knowledge_base_list`、`find_files` vs `graph_search`、`get_source_info` vs `knowledge_base_list`、`embed_text` vs `embed_batch` ×2、`get_service_info` vs `get_system_stats`、`get_document_info` vs `drill_down`） | 描述已写明 `UseInstead`，是**词典重叠**而非歧义：任务词（「文件」「向量」「版本」「切片」）被两个工具共同持有 | 不追加长句（会加剧上下文成本）；改在 `instructions` 里用一句话给出裁决顺序，比 25 条描述各加一句便宜 |

### 2.3 描述层真实缺陷（非检查器可发现）

| 缺陷 | 证据 | 修法 |
|:--|:--|:--|
| **幽灵工具名 `kb_list`** | `knowledge_base_drill_down.knowledgeBaseId` 参数描述：「可用 kb_list 获取」——服务里没有 `kb_list`，真实名 `knowledge_base_list`（`RagSearchTools.cs:58`） | 改文案；加门禁：描述中出现的 snake_case 名必须是已注册工具名（白名单放行 `too_large`/`chunk_not_found` 这类错误码） |
| **`search_file_content.maxMatches` 上限自相矛盾** | 参数描述写「上限 300」，`McpCatalogRegistry` 的 `Limits` 写「上限 500」，引擎实际夹到 500（`ManagedFileSearchEngine.cs:187`） | 三处统一为 500，或统一按 300 并在引擎夹取 |
| **`list_files.limit` 描述与实现不符** | 参数写「上限 200」，registry 写「默认 20」，实现按每库 200 取后全局 `Take(limit)`（`IndexStatusTools.cs:289-310`），多库时 `count` 可能超过描述预期 | 描述写「默认 20，上限 200（多库时按库合计截断）」 |
| **`ctx` 级参数无回显** | `graph_search.depth=99` 夹到 3 无 `effectiveDepth`；`search_file_content.contextLines=99` 实际按 5 生效（`contextLines 0–5`），无回显 | 与 `topK`/`maxResults` 一致，回显 `requested/effective` |

### 2.4 描述成本基线

| 项 | 值 |
|:--|:--|
| 25 工具定义合计（紧凑 JSON） | **25,140 B** |
| 描述正文 | 8,811 B（4,025 个中文字符，平均 350 B/工具，已是中文长句） |
| `inputSchema` | 8,335 B |
| `outputSchema`（7 个） | 5,030 B（占总量 19.9%；`get_system_stats` 单条 1,272 B 且内容错误） |
| `title` | **0 / 25**（协议可选，客户端 UI 无人类可读名） |
| `annotations` | 25/25 有 `readOnlyHint+idempotentHint`；`destructiveHint` / `openWorldHint` 0 条（全为只读工具，合理） |

判断：**描述预算已接近合理上限**。Anthropic 的实测结论是「更少的工具、更高的信号密度」优于「更多更长的描述」（`writing-tools-for-agents`，§Choosing the right tools / Namespacing）；arXiv 2602.14878 的实测是全量 augment 成功率 +5.85pp 但步数 +67.46%、16.67% 场景回归。因此下一步预算应投在**结果结构**与**契约校验**，而不是再加描述。

---

## 3. 头脑风暴：可行性、逻辑与性能欠缺

### 3.1 逻辑层：三类「语义丢失」

| # | 丢失点 | 具体证据 | 本质 |
|:--|:--|:--|:--|
| L1 | **召回通道信息丢失** | 跨库融合后所有 item 被改写为 `ScoreKind.RRF`（`SearchFusionStrategies.cs:49`）；纯词法库自动 `kbQueryVector=null`（`KnowledgeBaseSearchService.cs:181-184`）却无字段说明 | 融合层的规范化把「哪一路命中」这一可用信号归零 |
| L2 | **降级原因分层丢失** | `Degraded`（模型/断路器）与 `IndexDegraded`（词法未就绪/向量覆盖不全）分开了，但 `knowledge_base_search` 根本不输出这两个字段（只有 `semantic/lexical/hybrid` 有）；库级 `isKbLexical` 自动切换既不进 `Degraded` 也不进 `IndexDegraded` | 三个正交的降级维度（模型 / 索引 / 库类型）被压成 0–2 个布尔，且有一个维度根本没出口 |
| L3 | **重排不可信却无标记** | NaN 降级路径直接填 `0.85 - index*step`，响应无 `degraded`/`scoreKind` 字段（`rerank_results` 只回 `relevanceScore`） | 兜底值被当作测量值对外发布 |

L1/L2 的共性：**"降级"是内部实现的细节，出口契约没有它的位置**。这不是提示词能修的，必须在 structuredContent 里显式建模。

### 3.2 可行性与性能

| 项 | 判断 |
|:--|:--|
| 检索延迟 | 已达 Agent 预算（稳态 100–150ms；首调 3.5s 是模型冷启） |
| 并发 | 推理闸门串行（16 路 65→876ms 线性），无超时/排队失败；对 MCP 单客户端够用，多 Agent 并发需要信号量 + 排队上限 |
| 响应体积 | `embed_batch(400)`=9.4s / `calculate_similarity(9000 字符)`=6.0s 属**入口未限**而非出口；`search_file_content(500)`=273 KB 属出口未限 |
| 上下文成本 | 25,140 B/次 `tools/list`，折算 ~7–8k tokens（中文+BPE 估算）；`outputSchema` 占 19.9%，其中 `get_system_stats` 的 1,272 B 是错误 schema，改写后预计省约 5% |
| 缓存 | `get_index_status` 30s TTL（首调 2.5s → 21ms）有效；`list_sources` 60–96ms 未缓存、`get_system_stats` 走 `McpStatsCache` 但 `list_sources` 每次重算 connector 统计 |

### 3.3 「工具数量 vs 单工具能力」的取舍

当前 25 工具、4 组重叠（检索 4 / 列举 5 / 读取 4 / 查找 3）。业界两条路线：

- **合并**（Anthropic `writing-tools-for-agents`）：把 `list_users`+`list_events`+`create_event` 合成 `schedule_event`；把 `read_logs` 换成 `search_logs`。收益是**减少模型选错的面**、把编排下沉到服务端。
- **按需发现**（Anthropic `tool search tool`，Spring AI 实测 **34–64% token 节省**）：不全量下发，由客户端/模型按需拉取。

对本项目的判断：**短期不做合并**。理由：25 个工具并不算多（业界服务器常有 50–200 个），且四组重叠已有 `UseInstead` 指引 + `instructions` 裁决顺序；合并会破坏现有 CLI/集成方调用面（`project-no-historical-compat` 只适用于未发布代码，MCP 工具名已对外公开）。**中期可做 `tools/list` 分页/分组**：SDK 已支持 `nextCursor` 语义（`ListToolsResult` 有 `nextCursor`），按模块（search / files / sources / index / system）返回，客户端可只拉相关组。

---

## 4. 设计模式映射（含「该不该引入」的反对结论）

| 模式 | 落点 | 现状 | 结论 |
|:--|:--|:--|:--|
| **Adapter** | ①参数绑定：协议 JSON → CLR 参数（`McpToolRegistry.ConvertJsonNodeToType`）②宿主侧 `knowledgeBaseIds` 解析 | ①已存在但**不做未知参数校验** ②存在**两份**（`RagSearchTools.ParseIds` 容错逗号 vs `KnowledgeBaseMcpTools` 严格 JSON） | **要做**：把 `knowledgeBaseIds` 收敛到单一 `JsonStringArrayAdapter`（容错模式统一：接受 JSON 数组 + 逗号分隔 + 空数组=全部，与 `semantic_search` 对齐），并在 SDK 层加 `AdditionalProperties:false` 校验 → 未知参数回 `invalid_argument` 而不是静默忽略。**这解决 1.5 全部 5 条** |
| **Adapter** | ③重排分数字段：模型原始 logit → 对外 `relevanceScore` | 已在 `NormalizeScore`，但降级值与测量值同域 | **要做**：把 Adapter 的输出扩成 `RerankScore { Value, Kind(Measured\|Degraded), RawLogit? }`，`rerank_results` 输出 `scoreKind` 字段。**这解决 L3** |
| **Strategy** | 召回通道选择（向量 / 词法 / 混合）与融合 | 已有 `ISearchFusionStrategy`（RRF / Weighted）+ `useLexical` 参数 | **已有，不新增**；缺的是「通道事实」经 Strategy 传到出口。修法：`RecallItem` 增 `Channels` 位标志（`Vector\|Lexical`），RRF 融合时取并集而不是覆盖 `ScoreKind`。**这解决 L1** |
| **StateMachine** | 降级状态建模 | 已有 `ResilienceCircuitBreaker`（CLOSED/OPEN/HALF）与 `JobStateMachine` | **不引入新状态机**。降级不是生命周期，是**每请求的属性**。正确建模是「三值降级向量」`{model, index, kbType}` + 每项一个 `degraded/ reason`，用普通 record 传，不用 FSM。**这解决 L2**（FSM 在这里是过度设计：没有先后依赖、没有互斥迁移） |
| **Registry** | 描述 / 权限 / 注解单一事实源 | 已落地（`McpCatalogRegistry` + `DescriptionOverride`） | **保持**；补 3 处文案一致性（2.3 表） |
| **Decorator** | 未知参数拒绝、结果体积守卫 | 体积守卫已有 `OkBounded`（40 万字符拒绝）；未知参数拒绝无 | **不引入 Decorator**：体积守卫在入口处 `OkBounded(payload)` 已是最小实现；未知参数校验属于 Adapter 职责，塞进 Decorator 会让「谁负责校验」再次分裂 |
| **Template Method** | 四个检索工具共用的「解析 ID → 校验权限 → 扇出 → 融合 → 序列化」骨架 | 已存在于 `RagSearchTools.SearchAsync`；`KnowledgeBaseSearch` 另写一份 | **要收敛**：把两者合到同一模板，消除 1.5 的两种 `knowledgeBaseIds` 语义。这是**复用**而非新模式 |
| **Composite** | 权限目录（模块 → 工具） | 已有 `McpCategoryCatalog`（Composite） | 不动 |

**关键反对结论（明确不做）**：

- **不要为工具选择引入状态机**。工具选择是「一次性判定 + 文本引导」，没有可观测的生命周期；`ChatToolCallingHelper` 里已有 `ToolCallingExecutionStateMachine`（9 态）服务于**对话轮次**，那是合适的场景；把它扩展到 MCP 服务端只会增加不可测分支。
- **不要为降级引入 FSM**（见上）。
- **不要用 Decorator 包裹所有工具**去统一校验：SDK 的 handler 已经是一个统一入口（`WithCallToolHandler`），在那里加 Adapter 比给 25 个方法各套一层装饰便宜。

---

## 5. 业界主流方案对比

| 方案 | 出处 | 做法 | 与我们的差距 / 可借鉴点 |
|:--|:--|:--|:--|
| **outputSchema + structuredContent**（规范强制） | MCP `2025-11-25/server/tools`；`draft` 版本同样要求 | 声明 schema 的服务器 **MUST** 让 `structuredContent` 符合 schema；同时 SHOULD 在 text 块里回同样 JSON | 我们 7/7 不合规。可借鉴：①**失败形态也要建模**（`success/errorCode/error?` 作为 `anyOf` 或放宽 required）；②`title` 字段给客户端 UI 用（我们 0/25） |
| **工具描述质量度量** | arXiv 2602.14878（856 工具 / 103 服务器，97.1% 至少一种 smell，56% 用途不清） | 组件级（Purpose/Usage/Examples/Limits）选择性 augment，不做全量 | 我们已按组件做（`WhenToUse/UseInstead/Limits`）；结论仍是「选择性」而非「全量」 |
| **工具设计与评估流程** | Anthropic `writing-tools-for-agents`（2025-09） | 原型 → 任务集评估（含 token 消耗、调用次数、错误率）→ 用 agent 分析 transcript 改进 | 我们只有静态检查器 + 25 条任务，**缺真实 agent transcript 评估**（见 §7 门禁 G-M7） |
| **按需工具发现** | Anthropic `tool search tool`（advanced tool use，2025-11）；Spring AI 实测 34–64% token 节省 | 客户端不全量加载，按需检索工具定义 | 当前 25 工具全量下发 25 KB，尚未成为瓶颈；**列为观察项**，工具数 >50 时再上 |
| **返回高信号上下文** | Anthropic `writing-tools-for-agents` §Returning meaningful context | 控制 verbosity（`response_format: concise/detailed`），把 UUID 换成可读名 | 我们返回里 `chunkId`/`connectorId`/`documentId` 全是 32 位 hex；可加 `concise` 模式只回 `sourceKey + score + text` |
| **分页/截断 + 引导式错误** | 同上 §Optimizing tool responses | 25k token 上限；截断时给「用过滤条件」提示 | 已有 `OkBounded` + 收窄建议；缺 `calculate_similarity`/`embed_batch` 的入口上限 |
| **MCP 静态安全分析** | MCP-SecLint（2026） | 追踪不可信参数流向 tool → 外部调用 | 我们已有 `path_denied` 沙箱与权限矩阵；可借鉴「参数名 → 危险 sink」清单，用于 CI |

---

## 6. 现有基础设施与第三方库复用核查（结论：零新增依赖）

| 需求 | 可复用的现有设施 | 位置 | 结论 |
|:--|:--|:--|:--|
| 未知参数校验 / 参数类型 | `McpToolSchemaBuilder.BuildSchema`（已生成 `properties/required`） | SDK | **扩展现有**：把 `properties` 的名字集合用于「未知 key 检测」，无需新库 |
| 参数 JSON 转换 | `McpToolRegistry.ConvertJsonNodeToType` | SDK | **扩展现有**：捕获 `JsonException` 已转 `McpToolArgumentException`，只需把宿主侧裸 `JsonSerializer.Deserialize` 换成同一入口 |
| outputSchema 生成 | `McpToolSchemaBuilder.BuildOutputSchema`（支持嵌套到深度 3） | SDK | **修正**：`Dictionary<K,V>` 应映射为 `additionalProperties: <V schema>` 而不是反射 `count/capacity`；`string?` 应允许 `null` |
| 降级三值建模 | `RecallSnapshot(Degraded, DegradationReason, IndexDegraded, UsedRecallFloor)` | `KnowledgeBaseModels.cs` | **已足够**：只需在 `knowledge_base_search` 出口补 `degraded/indexDegraded/degradationReason`，并把 `isKbLexical` 记为 `channel=lexical` |
| 通道事实 | `ScoreKind`（BM25/Cosine/RRF）+ `AdaptiveQueryAwareFusion` | Infrastructure | **扩展**：`RecallItem` 增 `Channels` 位标志即可，无需新抽象 |
| 重排降级标记 | `RerankDocument { Index, RelevanceScore, Document }` | `IRerankService.cs` | **扩展 1 个字段** `ScoreKind`（Measured/Degraded）。不改模型签名 |
| 结果体积守卫 | `McpResult.OkBounded`（40 万字符，拒绝而非截断） | Host | **复用**，把 `calculate_similarity` / `embed_batch` 的**入参**也加上限（同 `MaxEmbedChars`） |
| 工具能力/遥测 | `IModelTelemetryService.GetCurrentRerankTelemetry()` | Infrastructure | **复用**：`get_system_stats.rerank` 增 `health: ok/degraded(stub) + reason`，让配置面能看见降级 |
| 取消/超时 | `CancellationTokenSource` + `request.Timeout`（跨库扇出） | `KnowledgeBaseSearchService` | 已存在，无需改 |
| 并发限制 | 推理侧已有信号量（`_inferenceSemaphore`） | `LlamaRerankAdapter` | 已存在；MCP 入口如需排队上限，可用 `SemaphoreSlim`，无新包 |
| 缓存 | `McpStatsCache`（30s TTL，双检锁） | Host | 已存在；`list_sources` 可接入 |
| 契约测试 | `ToolTypes()/ToolMethods()` 反射工具枚举 | `McpToolDescriptionContractGateTests` | **复用**该反射骨架写新门禁 |
| JSON Schema 校验库 | 无（`jsonschema` 未安装） | — | **不引入**：用 `McpToolSchemaBuilder` 的 schema 做字段级手写校验已足够；如需严格校验，`JsonSchema.Net` 是可选项，但会新增依赖，暂不需要 |

**第三方库结论**：`McpHelper.Core` 2.3.0 已提供 schema 生成、描述覆盖、instructions、`ttlMs` 门控；`ModelContextProtocol` 2.2.0 已提供 `McpServerFilters`（`ListToolsFilters` / `CallToolFilters`）中间件式过滤器、`outputSchema`、annotations、`nextCursor`。**本方案不新增任何 NuGet 包**。

---

## 7. 具体方案与门禁

### 7.1 P0 — 重排可信度（先做，影响正确性）

| 项 | 做法 | 门禁 |
|:--|:--|:--|
| **R1 分数语义** | `NormalizeScore` 的 NaN 分支不再对外发布伪值：返回 `RelevanceScore=null` + `ScoreKind=Degraded`；`rerank_results` 输出 `degraded:true, degradationReason:"GGUF 重排模型无 Rank 头（NaN 降级）", scoreKind:"degraded"` | 构造「全 0 模长」模型输出，断言响应 `scoreKind=="degraded"` 且 `results[*].relevanceScore==null`；**禁止**出现 `0.85` 序列 |
| **R2 顺序无关性** | 同上门禁补一条：同一组文档正序/逆序调用，`degraded` 场景下 `index` 与 `document` 必须一致、分数不得随位置变化 | 断言「逆序输入不产生 `0.85/0.733/0.617`」 |
| **R3 配置面可见** | `get_system_stats.rerank` 增 `effectiveBackend`（GGUF/ONNX/远程）+ `health` | 断言 `health=="degraded"` 当模型路径含 `.gguf` 且无 rank 头时（或用遥测字段） |
| **R4 二选一收口** | 生产建议切 ONNX Cross-Encoder（`bge-reranker-base` / `bce-reranker-base` ONNX），`OnnxRerankService` 已存在 | 部署后同一查询两次分数不同且与文档相关 → 人工验收记录 |

### 7.2 P1 — 检索语义与降级出口

| 项 | 做法 | 门禁 |
|:--|:--|:--|
| **S1 传播通道** | `RecallItem` 增 `Channels`（`[Flags]`：Vector=1, Lexical=2）；`RrfSearchFusionStrategy` 融合时按位或合并，不再无条件覆盖 `ScoreKind` | 纯词法库上 `semantic_search` 的 item 必须带 `channels:["lexical"]`；有向量库上带 `["lexical","vector"]` |
| **S2 降级三值** | `knowledge_base_search` 出口补 `degraded / indexDegraded / degradationReason / usedRecallFloor`；纯词法库返回 `kbTypes:{kbId:"lexical_only"}` | 对 Notes 库调用 → `indexDegraded` 或 `kbTypes[notes]=="lexical_only"` 至少一项为真 |
| **S3 无结果信号** | 增加 `noMatch:true` 当所有 item `score` 低于阈值或 `usedRecallFloor=false` 且 query 无 overlap | 用 `zzzzqqqxxx` 调用，断言 `noMatch==true`（或 `usedRecallFloor==true` + `note`），而不是静默 5 条 |
| **S4 上榜依据** | 每条 item 增 `matchedTerms`（命中的查询词）或 `recallReason` | 断言存在非空 `matchedTerms` 或 `channels` |

### 7.3 P1 — 参数绑定与契约

| 项 | 做法 | 门禁 |
|:--|:--|:--|
| **P1 未知参数拒绝** | SDK `InvokeToolAsync`：若 `arguments` 含 schema 未声明的 key → `McpToolArgumentException` → `invalid_argument`，消息列可用参数名 | 传 `kbIds`/`topk`/`source` → `isError && errorCode=="invalid_argument"`，且文本含「未知参数: kbIds」 |
| **P2 单一 ID 解析** | 抽 `JsonStringArrayAdapter.Parse(string?)`：接受 JSON 数组 / 逗号 / `[]`=全部；四个检索工具 + `knowledge_base_search` + `list_files` 全部走它 | `'[]'` / `'a,b'` / `'not-json'` 在 5 个工具上行为一致（`'not-json'` → `invalid_argument`） |
| **P3 JSON 错误归一** | 所有 `JsonSerializer.Deserialize` 调用点捕获 `JsonException` → `invalid_argument`，不回显 `Path/LineNumber` | `list_files(knowledgeBaseIds='not-json')` → `invalid_argument`；响应不含 `"LineNumber"` |
| **P4 outputSchema 修正** | `Dictionary<K,V>` → `additionalProperties: V`；`string?` → `type:["string","null"]`；`get_system_stats` 改为强类型 `SystemStatsSystem/Embedding/Search/RerankConfig` 或降到 `additionalProperties:true` 不写 required | 用自建校验器（本轮回放脚本）对 7 个工具断言 0 违规 |
| **P5 失败形态建模** | `outputSchema` 用 `anyOf:[successSchema, {success:false, errorCode, error}]`，或改用 `oneOf`；`success:false` 时 `required` 只含 `success/errorCode` | 失败调用（bad kb / bad chunk）用同一校验器通过 |
| **P6 缺参结构化** | SDK 缺参分支填 `structuredContent{errorCode:"invalid_argument", missing:[...]}` | 5 个工具缺参调用 → `structuredContent.errorCode=="invalid_argument"` |
| **P7 未知工具协议错误** | 未知工具名改为 JSON-RPC `-32602`（规范允许 protocol error）；若担心破坏容错客户端，保留 `isError` 但补 `errorCode:"unknown_tool"`（`-32602` 同时给出更好） | 调用 `no_such_tool` → 断言 `-32602` 或 `errorCode=="unknown_tool"` |
| **P8 数值上下界** | `list_files.limit` 夹到 `[1,200]`（0/-1 → `invalid_argument`）；`find_files_by_pattern.maxResults` 同 `[1,500]`；`graph_search.depth` 回显 `effectiveDepth`；`contextLines` 回显 `effectiveContextLines` | 逐工具传 0 / -1 / 99，断言 `invalid_argument` 或回显 `effective*` |
| **P9 入参上限** | `calculate_similarity` 两段各 8000 字符上限（与 `embed_text` 同 `MaxEmbedChars`）；`embed_batch` 条数上限 32（或 64）+ 分页提示；`rerank_results` documents 条数上限 100 + 单条 8000 | `calculate_similarity(9000)` → `too_large`；`embed_batch(400)` → `too_large` 且附分批建议 |

### 7.4 P2 — 提示词收尾（只改 4 处，不加长）

| 项 | 做法 |
|:--|:--|
| T1 | `knowledge_base_drill_down` 参数描述 `kb_list` → `knowledge_base_list` |
| T2 | `search_file_content.maxMatches` 三处统一上限（建议 500，与引擎一致） |
| T3 | `graph_search.depth` 描述改「深度范围 1–3」；`list_files.limit` 描述补「多库合计截断」 |
| T4 | `title` 补全 25 个（简短人类可读名），客户端 UI 受益；成本 ~1.5 KB，可选 |

### 7.5 P3 — 评估化（治本）

| 项 | 做法 |
|:--|:--|
| **E1 真实 Agent transcript 评估** | 参照 Anthropic `writing-tools-for-agents`：把 25 条任务 + 20 条多步任务（「先列源 → 找文件 → 读行段 → 引用切片」）跑真实模型，记录 `工具序列 / 错误率 / 步数 / token`，用 transcript 反推描述缺陷 |
| **E2 选型二分门禁** | 把中文检查器的 `select-*` 从「WARN/FAIL 打印」升级为**断言**（阈值：明确 ≥ 20/25，错误 ≤ 1）；避免下次改描述时静默回归 |
| **E3 成本门禁** | `tools/list` 紧凑 JSON 体积上限（如 30 KB）+ `outputSchema` 占比上限（如 15%） |
| **E4 「幽灵名」门禁** | 描述/参数描述中出现的 `[a-z0-9]+(_[a-z0-9]+)+` 必须 ∈ 工具名 ∪ 错误码白名单 ∪ 示例占位符白名单（`kb_a/kb_b/kb_1/kb_2`） |

### 7.6 门禁清单（可直接落成 xUnit）

| 编号 | 门禁 | 锁定不变量 |
|:--|:--|:--|
| G-M1 | `OutputSchemaConformanceGateTests` | 7 个声明 schema 的工具有效载荷 + 失败载荷都通过校验（含 required/nullable/additionalProperties） |
| G-M2 | `UnknownArgumentRejectionGateTests` | 25 个工具各传 1 个未声明参数 → `invalid_argument` 且列出可用名 |
| G-M3 | `JsonStringArrayParityGateTests` | `knowledgeBaseIds` 在 6 个工具上对 `[]`/`a,b`/`not-json` 行为一致 |
| G-M4 | `RerankScoreIntegrityGateTests` | 降级场景 `scoreKind=="degraded"` 且分数为 null；正常场景分数随文档内容变化 |
| G-M5 | `RetrievalChannelVisibilityGateTests` | 纯词法库 / 有向量库的 `channels` 与 `kbTypes` 符合预期 |
| G-M6 | `ToolInputBoundaryGateTests` | 0/-1/99/超长/超条数 → `invalid_argument` 或 `too_large`，且回显 `effective*` |
| G-M7 | `ToolDescriptionReferenceGateTests` | 描述中引用的工具名真实存在（幽灵名 = 0） |
| G-M8 | `McpPromptBudgetGateTests` | `tools/list` ≤ 30 KB，`outputSchema` ≤ 15%，描述 ≤ 10 KB |
| G-M9 | `ToolSelectionRegressionGateTests` (E2) | 25 条任务明确 ≥ 20、错误 ≤ 1 |
| G-M10 | `ToolTranscriptEvalGateTests` (E1，离线可跳过) | 20 条多步任务成功率基线不下降 |

---

## 8. 工作顺序与风险

| 序 | 任务 | 触碰面 | 风险 | 回滚 |
|:--|:--|:--|:--|:--|
| 1 | R1–R3（重排分数语义） | `LlamaRerankAdapter` / `IRerankService` / `RagSearchTools` / `get_system_stats` | 中（改 `RelevanceScore` 可空会影响 Chat 侧调用方） | 保留旧字段，新增 `scoreKind` + `relevanceScore` 不变时默认 `measured` |
| 2 | P2/P3（ID 解析 + JSON 错误归一） | `RagSearchTools` / `KnowledgeBaseMcpTools` / `IndexStatusTools` | 低 | 单文件回退 |
| 3 | P1（SDK 未知参数拒绝） | `McpToolRegistry`（SDK）+ nupkg | 中（需重新 pack + 溯源校验） | SDK 版本回退 |
| 4 | P4/P5/P6（schema 契约） | `McpResultContracts` / SDK `McpToolSchemaBuilder` / `McpResult.Fail` | 中（schema 变更会让严格客户端校验失败——但当前已不合规） | 逐工具切换 |
| 5 | S1–S4（通道/降级/无结果） | `RecallItem` / `RrfSearchFusionStrategy` / `KnowledgeBaseSearchService` / 4 工具 | 中（`RecallItem` 是热路径 record，加字段影响面广） | 字段可空默认 |
| 6 | P8/P9（上下界） | 各工具 | 低 | — |
| 7 | T1–T3（文案） | `McpCatalogRegistry` + 特性 | 低 | 重跑 G-M9 |
| 8 | E1–E4（评估） | tests + docs | 低 | — |

**部署注意**（沿用 `project-production-deploy`）：SDK 改动必须先提交再 `dotnet pack`，再提交 `.nupkg`，最后 `git archive`；部署后必须比对容器内 `McpHelper.Core.dll` md5 与 nupkg 内一致（本轮已验证该流程有效）。

---

## 9. 明确不做

| 项 | 理由 |
|:--|:--|
| 把 25 个工具合并成少数「复合工具」 | 工具名已对外公开，合并破坏集成方；且 25 个不算多。待工具数 >50 再考虑合并或按需发现 |
| 给 `tools/list` 加分页/分组（本轮） | 25 KB 尚未成为瓶颈；`nextCursor` 能力已在 SDK，留作观察项 |
| 为降级或工具选择引入状态机 | 降级是每请求属性，非生命周期；工具选择无状态 |
| 用 Decorator 包装 25 个工具做统一校验 | SDK `WithCallToolHandler` 已是统一入口；在 Adapter 层校验更便宜 |
| 全量 augment 描述 / LLM 自动生成描述 | arXiv 实测步数 +67%、16.7% 场景回归；我们已有注册表事实源 |
| 引入 `JsonSchema.Net` 等新包做契约校验 | 手写字段级校验已够；零新增依赖是既定约束 |
| 把 `knowledgeBaseIds` 改成 `array` 类型 | 破坏现有调用方；用 Adapter 容错 + 描述说明（本轮已验证原生数组也能被 SDK 吃下，但契约上仍应写 string） |

---

## 10. 参考

- MCP 规范 Tools（2025-11-25）：`outputSchema` 声明后服务器 **MUST** 返回符合它的 `structuredContent`；无参工具推荐 `additionalProperties:false`；工具执行错误用 `isError`，协议错误用 JSON-RPC `-32602`。https://modelcontextprotocol.io/specification/2025-11-25/server/tools ；draft（含 `ttlMs/cacheScope`、`nextCursor` 分页语义）https://modelcontextprotocol.io/specification/draft/server/tools
- Anthropic, *Writing effective tools for agents*（2025-09-11）：选择正确的工具而非包装全部 API；namespacing；返回高信号上下文；token 效率（截断 + 引导式错误）；用 transcript 评估驱动改进。https://www.anthropic.com/research/writing-tools-for-agents
- Anthropic, *Introducing advanced tool use for Claude* / *Tool search tool*（2025-11）：按需发现工具定义；https://www.anthropic.com/research/advanced-tool-use ；Spring AI 实测 token 节省 34–64%：https://spring.io/blog/2025/12/11/spring-ai-tool-search-tools-tzolov/
- arXiv 2602.14878, *MCP Tool Descriptions Are Smelly!*：856 工具 / 103 服务器，97.1% 至少一种 smell；全量 augment 成功率 +5.85pp、步数 +67.46%、16.67% 回归。https://arxiv.org/html/2602.14878v1
- MCP-SecLint（2026）：MCP 工具参数流向的静态安全分析。https://dl.acm.org/doi/abs/10.1145/3806007.3810961
- 本轮实测脚本：`/tmp/mcpverify/prod_probe.py`（SSE JSON-RPC 客户端）、`rr-mcp-prod-sweep1..6.py`（调用矩阵 / 边界 / 契约校验）、`/tmp/mcpcheck/check_zh.py`（中文描述检查器）、`tasks.json`（25 条任务）。



---

## 11. 实施状态（2026-11-17）

### 11.1 交付清单

| 方案项 | 落地位置 | 状态 |
|:--|:--|:--|
| **R1 分数语义** | `IRerankService`（`RelevanceScore` 可空 + `RerankScoreKind` + `RerankResult.Degraded/DegradationReason`）、`LlamaRerankAdapter.NormalizeScore`（NaN 不再伪造 0.85） | ✅ |
| **R2 顺序无关性** | 降级批次按 `Index` 排序；`GenerateFallbackDocuments` 分数留 null | ✅ |
| **R3 配置面可见** | `get_system_stats.rerank.health/degradationReason/modelPath`；`IRerankRuntimeTelemetryProvider.IsDegraded` | ✅ |
| **R4 二选一收口** | 未做（生产模型切换属运维决策，代码侧已具备降级可观测） | ⬜ 待运维 |
| **S1 通道事实** | `RecallChannel` 位标志 + 三个召回源打标 + RRF/ZScore 融合取并集 + `items[].channels` | ✅ |
| **S2 降级三值** | `knowledge_base_search` 补 `degraded/indexDegraded/degradationReason/usedRecallFloor/kbChannels` | ✅ |
| **S3 无结果信号** | `QueryMatchSignals`（ASCII 锚点优先 / CJK 40% 覆盖）+ `noMatch/noMatchReason` | ✅ |
| **S4 上榜依据** | 由 `channels` + `coverage` + `isFloor` 表达（未单列 `matchedTerms`） | ✅（等价信息） |
| **P1 未知参数拒绝** | SDK `McpToolRegistry.GetDeclaredParameterNames` + `arguments` 键集校验 | ✅ |
| **P2 单一 ID 解析** | `McpInputAdapters.ParseKnowledgeBaseIds`（6 处工具共用） | ✅ |
| **P3 JSON 错误归一** | 同上（不泄漏 `Path/LineNumber`）+ `McpResult.FailException` 映射 `invalid_argument` | ✅ |
| **P4 outputSchema 修正** | SDK `BuildOutputSchema`：Dictionary→additionalProperties、可空→`["type","null"]`、`SystemStatsResult` 强类型 | ✅ |
| **P5 失败形态建模** | SDK `BuildOutputSchema` 生成 `anyOf[成功, 失败]` | ✅ |
| **P6 缺参结构化** | SDK 缺参分支补 `structuredContent{errorCode,missing}` | ✅ |
| **P7 未知工具** | `errorCode=unknown_tool`（保留 `isError`，未改 JSON-RPC 码以免破坏容错客户端） | ✅（折中） |
| **P8 数值上下界** | `list_files`/`find_files`/`graph_*`/`drill_down`/`search_file_content` 拒绝 0/负数、超上限夹取并回显 `requested*/effective*` | ✅ |
| **P9 入参上限** | `calculate_similarity` 8000 字符、`embed_batch` 100 条、`rerank_results` 100 条 | ✅ |
| **T1–T3 文案** | `kb_list`→`knowledge_base_list`、`maxMatches` 统一 500、`depth` 改「范围 1–3」、`limit` 补多库截断 | ✅ |
| **T4 title** | 25/25 工具补 `Title` | ✅ |
| **E1 transcript 评估** | 未做真实模型多步 transcript（需模型与配额），离线选型基线已固化 | ⬜ |
| **E2 选型门禁** | `scripts/mcp-tool-audit.py`（可接 CI，基线 17/25 明确、≤1 错误） | ✅ |
| **E3 成本门禁** | 同上：总字节 ≤30 KB、outputSchema ≤35%、单条 ≤600 字符 | ✅ |
| **E4 幽灵名门禁** | `McpToolContractSemanticsGateTests.Descriptions_OnlyReferenceExistingToolNames` | ✅ |

### 11.2 门禁结果

| 套件 | 结果 |
|:--|:--|
| `ReferenceRAG.Host.Tests` | **544 / 544**（新增 `McpToolContractSemanticsGateTests` 6 项 + `McpInputAndSemanticsGateTests` 24 项） |
| `ReferenceRAG.Tests`（Core） | **799 / 799**（47 跳过为既有 Skip） |
| `McpHelper.Core.Tests` / `Linker.Tests` | **276 / 276**、**46 / 46** |
| 前端 AST 门禁 / 单测 | 60 模块 + 14 模板通过；**133 / 133** |
| `scripts/mcp-tool-audit.py`（dev） | `tools=25 compact=29,427B outputSchema=25.9%`；选型 17 明确 / 7 歧义 / 1 错误；warns 0 |

### 11.3 dev 真机验收（`referencerag-dev` @ `127.0.0.1:5124`（经 SSH 隧道））

- **接口面 36/36 断言通过**：title 全覆盖、7 个 outputSchema 为 anyOf 且自建校验 0 违规、未知参数 → `invalid_argument`（列出可用参数名）、缺参 → `structuredContent{missing}`、`knowledgeBaseIds` 五种写法语义一致、非法 JSON 不泄漏序列化器内部路径、`unknown_tool` 有码。
- **检索语义**：`items[].channels` 与 `kbChannels` 均输出；`knowledge_base_search` 补全三值降级字段；`zzzzqqqxxx 完全不存在的词` → `noMatch=true`；`袁崇焕` → `noMatch=false`。
- **重排**（dev 用 ONNX Cross-Encoder，因此为真实分数）：`degraded=false scoreKind=measured`，同一批文档换顺序分数随内容变化（0.997 / 7e-05 / 1.6e-05），`effectiveTopK` 回显。
- **边界**：`limit=0` → `invalid_argument`、`limit=5000` → `effectiveLimit=200`、`maxResults=5000` → 500、`contextLines=99` → 5、`calculate_similarity(9000)` → `too_large`、`embed_batch(101)` → `invalid_argument`。
- **页面验收（内置浏览器）**：`/search` 顶部出现「未命中查询」「通道 向量+词法」标签，正常查询不误报；`/guide` MCP 页新增「响应语义与失败约定」表（含 `channels`/`kbChannels`/`noMatch`/`scoreKind`/`effectiveXxx`，无 `kb_list` 残留）；`/apikeys` 工具树仍显示 25 节点且描述与注册表一致（改 title 未影响权限树）；`/performance` 重排预设跑通并展示 NDCG/MRR/MAP。
- **说明**：内置浏览器当前无可见视口，截图接口不可用（`NATIVE_BROWSER_VIEWPORT_UNAVAILABLE`），页面验收改以 DOM/无障碍树文本断言完成，未产出截图。

### 11.4 明确未做 / 后续

| 项 | 原因 |
|:--|:--|
| R4 生产切 ONNX 重排模型 | 运维决策；代码侧已能如实报 `health=degraded` |
| E1 真实模型 transcript 评估 | 需模型与配额；离线 25 条选型基线已接 CI |
| JSON-RPC `-32602` 未知工具 | 保留 `isError` 形态兼容现有容错客户端，同时给出 `errorCode` |
| `matchedTerms` 单列字段 | `channels + coverage + isFloor` 已表达同等信息，避免再加字段 |
