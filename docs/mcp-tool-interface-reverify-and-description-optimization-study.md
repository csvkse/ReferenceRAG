# MCP 工具接口复验与提示词（description）优化调研

> 对象：生产 `referencerag:deploy-e3dff3c1ae3d`（SDK `McpHelper.Core` 2.2.0 @ `46d04f1`）＋ SDK `SDK/McpHelper`
> 验证方式：生产端点逐工具真实调用（正常参数 + 失败路径 + 边界值），`tools/list` 全量拉取后跑定义质量检查器
> 日期：2026-11-16

## 结论先行

1. **接口全部可用，但错误信封的 `errorCode` 覆盖不完整**：`knowledge_base_drill_down` / `get_document_info` / `semantic_search` 三处失败只回 `success:false + error`，没有 `errorCode` —— 与 `list_files` / 文件工具 / embed 系列不一致。客户端无法按错误码分支，只能解析中文文本。
2. **提示词（description）是当前最大短板，且被量化证实**：25 个工具里 **23 个没有 `outputSchema`**（只有 `list_files` 有）；**7 个工具从未说明「何时该用 / 何时改用邻居」**；8 个工具的 `topK`/`limit` 没写上界；6 个工具的「JSON 数组」参数在 schema 里声明成 `string`。用业界检查器（中文适配版）跑：**152 pass / 83 warn / 7 fail**，25 条真实任务里只有 12 条能明确选中正确工具。
3. **工具定义总字节 16.1 KB**，其中 `list_files`(1628B) 与 `search_file_content`(1427B) 最贵。业界论文实测：全量augment 描述能提升成功率 5.85pp，但**执行步数增加 67.46%、16.67% 场景回归** —— 所以不能无脑加长，要按组件选择性补。
4. **缺 `instructions` 全局提示**：`initialize` 响应里没有 `instructions` 字段（SDK 支持 `InitializeResult.Instructions` 但未设置）。这是给模型讲「这个服务器整体怎么用、先用哪个工具」的官方位置，比在 25 条描述里重复更省 token。

---

## 1. 接口复验（生产实测）

### 1.1 全部工具调用结果

| 组 | 工具 | 延迟 | 结果 |
| :--- | :--- | :--- | :--- |
| 无参 | `ping` / `get_service_info` / `knowledge_base_list` | 10–147ms | OK |
| 无参（重） | `get_index_status` | **3140ms 首调 → 19ms 缓存命中** | OK，30s TTL 生效 |
| 无参（重） | `get_system_stats` | 31ms | OK |
| 检索 | `semantic_search` | 2563ms | OK，`degraded:false` |
| 检索 | `lexical_search` | 50ms | OK |
| 检索 | `hybrid_search` | 1567ms | OK |
| 检索 | `knowledge_base_search` | 774ms | OK |
| 源/文件 | `list_sources` / `get_source_info` / `get_file_tree` | 251 / 298 / 27ms | OK |
| 源/文件 | `find_files_by_pattern` | 44ms | OK |
| 源/文件 | `search_file_content` | 731ms | OK |
| 读取 | `read_note_content` / `get_note_outline` | 88 / 46ms | OK |
| 图谱 | `graph_search_by_title` | 51ms | OK |
| 图谱 | `graph_search` | 47ms | OK（`totalNodes:0` 属数据事实） |
| 向量 | `embed_text` | 1217ms | OK |
| 向量 | `calculate_similarity` | 577ms | OK |
| 向量 | `embed_batch` | 315ms | OK |
| 向量 | `rerank_results` | 2806ms | OK |

**结论**：25 个工具全部可调用，无 HTTP 非 200，无异常崩溃。

### 1.2 失败路径与安全边界

| 用例 | 返回 | `errorCode` |
| :--- | :--- | :--- |
| `get_file_tree(NoSuchSource)` | `isError` + `source_not_found` | ✅ |
| `read_note_content(/etc/passwd)` | `path_denied`（提示先调 `list_sources`） | ✅ |
| `read_note_content(/etc/passwd, sourceName=Temp)` | `path_denied`（沙箱拒绝） | ✅ |
| `get_note_outline(/app/data/appsettings.json)` | `path_denied` | ✅ |
| `get_source_info(ZZZ)` | `source_not_found` | ✅ |
| `list_files(connectorId=unknown)` | `connector_not_found` | ✅ |
| `list_files(kbIds=[deadbeef])` | `knowledge_base_not_found` | ✅ |
| `embed_text(20k 字符)` | `too_large`（上限 8000） | ✅ |
| `embed_batch` 超长 | `too_large` | ✅ |
| `semantic_search(topK="3")` | `参数 'topK' 类型无效，期望 integer` | ⚠️ 无 errorCode（SDK 侧 `invalid_argument` 有 structuredContent，但本工具 envelope 未带） |
| **`knowledge_base_drill_down(未知切片)`** | `success:false, error:...` | ❌ **无** |
| **`get_document_info(未知 chunkId)`** | `Document 'nonexistent' not found` | ❌ **无** |
| **`semantic_search(query="")`** | `query 不能为空` | ❌ **无** |

### 1.3 分页/上限约束实测

| 参数 | 传值 | 实际行为 | 判定 |
| :--- | :--- | :--- | :--- |
| `semantic/lexical/hybrid_search.topK` | 900 | `effectiveTopK=50`，回显 `requestedTopK=900` | ✅ 有夹取且可判别 |
| **`knowledge_base_search.topK`** | 900 | **`count=900`**，payload 2.37 MB / text 1.39 MB | ❌ **无上限**，单次调用可吃掉整个上下文 |
| `list_files.limit` | 5000 | `count=365`（受某库文档数限制） | ⚠️ 无上限但受数据量自然约束 |
| `find_files_by_pattern.maxResults` | 5000 | `count=500` | ⚠️ 内部有隐式 500 上限，未在描述中声明 |
| `knowledge_base_drill_down.limit` | 2000 | 返回 27（文档总切片数），65 KB | ⚠️ 无上限 |
| `search_file_content.maxMatches` | 300 | payload 439 KB | ⚠️ 描述写了"上限 300"但 300 已是 439 KB |
| `read_note_content.endLine` | -1 | 读至末尾，上限 2MB | ✅ |

**关键发现**：`knowledge_base_search` 是唯一**没有 `effectiveTopK` 夹取**的检索工具，`topK=900` 直接返回 900 条 2.37MB。其余三个检索工具都有 `Math.Clamp(1,50)`。这是一个真实的**上下文炸弹**。

### 1.4 交叉一致性

| 检查 | 结果 |
| :--- | :--- |
| `list_sources(kbIds=[X])` vs `get_index_status()` | scoped 调用下 `Temp` 的 fileCount/chunkCount **归零**（因为该配置源不属于指定 KB），而非 scoped 时 165/24776 | ⚠️ 语义正确但易误读：scoped 时配置源行仍出现但计数为 0，未标注"不属于该库" |
| `chunkId` 从 `knowledge_base_search` → `drill_down` / `get_document_info` | 两者都能定位（需带 `knowledgeBaseId`） | ✅ |
| 仅 1 个工具（`list_files`）有 `outputSchema` | 其余 24 个只能靠文本解析 | ❌ |

---

## 2. 提示词审计

用两个检查器：业界原版（`tool_design_check.py`，英文分词）+ 中文适配版（`check_zh.py`，中文二元组）。

### 2.1 业界原版结果（英文分词，中文定义会大量 0 分 —— 这本身就是一个发现）

```
97 pass, 32 warn, 20 fail
selection: 25 tasks, 1 clear, 4 ambiguous, 20 wrong
```

原版对中文只能判 "no task word in any definition"，说明**这些中文描述在英文分词器眼里等于空描述**。这不是原版缺陷，而是提醒：**如果客户端/模型侧做基于关键词的工具预筛（tool search / deferred loading），中文描述的可检索性取决于它的分词能力**。

### 2.2 中文适配版结果

```
152 pass, 83 warn, 7 fail
selection: 25 任务 12 明确 6 歧义 7 错误
```

失败用例（真实语义任务 vs 选中工具）：

| 任务 | 检查器选中 | 期望 | 原因 |
|---|---|---|---|
| 查一下 MCP 是什么 | `get_service_info` | `semantic_search` | semantic_search 描述里没有"查/问/是什么"类词 |
| 精确查找错误码 E1001 | `search_file_content` | `lexical_search` | lexical_search 描述有"错误码"但没有"查找" |
| 在 Notes 库里搜索知识图谱相关内容 | `graph_search` | `knowledge_base_search` | "搜索"这个词被 graph/kb_search 共同占有，kb_search 描述只有一句 |
| 把这个文件第 10 到 20 行读出来 | `list_files` | `read_note_content` | read_note_content 没说"行范围"能干什么 |
| 有哪些数据源，各自多少文件 | `find_files_by_pattern` | `list_sources` | list_sources 描述有"数据源"但没有"多少文件" |
| 服务还活着吗 | `get_service_info` | `ping` | ping 只有"测试 MCP 连通性，返回 pong" |
| 根据这个切片 ID 看它属于哪个文件 | `knowledge_base_drill_down` | `get_document_info` | 两者描述几乎同义（near-duplicate） |

歧义用例 6 条（`knowledge_base_list` vs `get_system_stats`、`list_files` vs `drill_down`、`find_files` vs `graph_search`、`graph_search_by_title` vs `get_note_outline`、`get_index_status` vs `get_document_info`、`embed_text` vs `embed_batch`）。

### 2.3 组件级缺口统计

| 组件 | 缺失数 / 25 | 说明 |
| :--- | :--- | :--- |
| `outputSchema` | **24** | 只有 `list_files` 有；其余只能靠文本 JSON 二次解析 |
| 「何时该用」 | 11 | `usage-guidance` 检查缺失 |
| 「与邻居的取舍」 | **21** | `neighbour` 缺失，四组重叠工具全都没交叉指引 |
| 整型参数上下界 | 8 | `topK`(4) / `limit`(2) / `depth` / `contextLines` 未写明 |
| 无参工具 `additionalProperties:false` | 0 | 全部合规 ✅ |
| 有参工具缺 `required` | 3 | `get_file_tree` / `list_files` / `list_sources`（全可选却未显式声明空数组） |
| JSON 数组参数声明成 `string` | **6** | `knowledgeBaseIds`(5) + `documents` + `texts` —— 模型必须知道"传 JSON 字符串" |

### 2.4 现有基础设施里有一份更好的描述，但没接线

`McpCatalogRegistry`（权限矩阵的事实源）里为**全部 25 个工具**都写了更完整的业务描述，例如：

```
SemanticSearch | 在知识库中执行向量语义搜索，基于自然语言查找相关文档片段
KnowledgeBaseDrillDown | 展开知识库切片的完整正文（按 chunkId 或 documentId）
```

它当前只被用于 `RiskLevel` 与 `ModuleCode`（注解 + 权限），**描述文本没被任何地方读取**。也就是说：同一批工具存在**两份描述**（`[Description]` 特性 + 注册表字符串），一份进协议、一份只服务权限。

### 2.5 成本基线

| 项 | 值 |
|---|---|
| 25 工具定义合计（紧凑 JSON） | **16,131 B** |
| 最贵 | `list_files` 1628B、`search_file_content` 1427B |
| 最便宜 | `ping` 198B、`get_service_info` 212B |
| 若全部补 outputSchema（估） | +25 × ~600B ≈ **+15 KB** |
| 业界论文：全量 augment 的代价 | 成功率 +5.85pp，但步数 +67.46%，16.67% 场景回归 |

---

## 3. 设计与逻辑缺口（头脑风暴）

| # | 缺口 | 证据 | 影响 |
|---|---|---|---|
| D1 | 错误信封 `errorCode` 不一致 | §1.2 三处无码 | 客户端无法分支重试，只能读中文 |
| D2 | `knowledge_base_search.topK` 无上限 | 900 条 / 2.37MB | 单次调用即可打爆上下文 |
| D3 | 描述与 schema 类型不一致 | 6 处 JSON 字符串 | 模型按 `string` 传原生数组（实测被容错吃下，但纯属运气） |
| D4 | 四组重叠工具无取舍指引 | §2.2 7 条选错 | 模型选错工具 → 多轮试错 |
| D5 | 24 个工具无 `outputSchema` | §2.3 | 结构化结果只能从 text 里再 parse |
| D6 | 无 `instructions` 全局提示 | `initialize` 无字段 | 模型不知道"先 list_sources 再检索"这条硬约束 |
| D7 | `list_sources` scoped 时配置源计数归零且无标注 | §1.4 | 易被读成"这个源是空的" |
| D8 | `refId` / 旧索引语义残留 | `get_document_info` 描述仍说"否则读旧索引" | 与已删除的旧路径语义冲突 |

**性能面**：接口延迟已达标（缓存命中 19–38ms，检索 50–774ms）。真正的性能风险不是延迟而是**响应体积**：`knowledge_base_search(topK=900)` = 2.37MB、`search_file_content(maxMatches=300)` = 439KB。这两处应当有硬上限 + 截断回显。

---

## 4. 设计模式映射

| 模式 | 适用点 | 现状 | 建议 |
| :--- | :--- | :--- | :--- |
| **Adapter** | `[Description]` 特性 ↔ 协议 `description` | 已有（`LocalToolProvider` 反射读取） | 增加一条：从 `McpCatalogRegistry` 读取「补充描述」并合并，避免维护两份 |
| **Strategy** | 错误信封构造 | `McpResult.Fail(error, errorCode)` 已是唯一入口，但调用点有的漏传 code | **不需要新模式**，需要的是"要求所有 Fail 必须带 code"的门禁 + 一个 `McpErrorCodes` 常量集 |
| **StateMachine** | 工具选择引导 | 无 | **不要引入**。引导是描述文本 + instructions 的事，没有生命周期 |
| **Decorator** | 结果体积保护 | 无 | 可考虑在结果写出前做"体积守卫"（`maxPayloadBytes`），但这属于参数夹取的延伸，不是装饰器 |
| **Registry** | 描述单一事实源 | `McpCatalogRegistry` 已有全量业务描述 | 把描述也纳入它 → 一份数据同时驱动权限、注解、提示词 |

反面结论：

- **不要**给 25 个工具都堆长描述。论文实测步数 +67%，且本项目描述已经是中文长句，再加长会挤占上下文。
- **不要**引入 LLM 自动生成描述（论文的 augmentor 思路）。收益不确定、成本高，且本项目已有可用的业务描述源。

---

## 5. 具体方案

### P0 — 立刻可做（无风险，纯契约修正）

| 项 | 做法 | 门禁 |
| :--- | :--- | :--- |
| **E1 `errorCode` 全覆盖** | 三处漏码失败改为 `McpResult.Fail(msg, code)`；新增 `McpErrorCodes` 常量集（`not_found` / `invalid_argument` / `too_large` / `path_denied` / `source_not_found` / `connector_not_found` / `knowledge_base_not_found` / `forbidden`） | 遍历所有工具的失败路径，断言 `isError && structuredContent.errorCode != null` |
| **E2 `knowledge_base_search.topK` 夹取** | 与另三个检索工具对齐：`effectiveTopK = Math.Clamp(topK,1,50)`，回显 `requestedTopK`/`effectiveTopK` | `topK=900` → `count<=50` 且 `effectiveTopK==50` |
| **E3 结果体积守卫** | `list_files`/`search_file_content`/`drill_down` 加硬上限并在响应回显 `truncated:true` | 构造大 limit 调用，断言 payload < 512KB |
| **E4 参数类型修正** | `knowledgeBaseIds`/`documents`/`texts` 的 schema 注释明确「JSON 字符串」并在描述给出示例；保留 `string`（改动为 `array` 会破坏现有调用方） | schema 描述必须含 `JSON 数组字符串` |
| **E5 清理 `refId` 残留** | `get_document_info` 描述删掉"否则读旧索引"，改为"未传 knowledgeBaseId 时按旧索引查找，通常应传" | 描述不含「旧索引」字样 |

### P1 — 提示词结构化（按组件选择性补）

按论文结论，**不是全量 augment**，只补最高杠杆的组件：

| 组件 | 补什么 | 覆盖工具 |
| :--- | :--- | :--- |
| **用途 + 何时用** | 一句话用途 + 「在需要 X 时使用」 | 全部 25 |
| **邻居取舍** | 一句话「…请改用 `<邻居>`」 | 四组重叠工具：检索 4 个、列举 5 个、读取 4 个、查找 3 个 |
| **上限** | 「默认 N，上限 M」 | 8 个整型参数 |
| **失败语义** | 「失败时返回 `errorCode=…`」 | 可只在 instructions 里统一说，避免重复 |

落地方式（选一）：

- **方案 A（推荐）**：把描述集中到 `McpCatalogRegistry`，`LocalToolProvider` 读取时用注册表文本覆盖 `[Description]`。优点：一份数据驱动权限 + 注解 + 提示词；缺点：`[Description]` 特性变成冗余（需删除）。
- **方案 B**：保留 `[Description]`，另加 `Metadata["DescriptionSuffix"]` 由注册表提供"邻居取舍 + 上限"追加句。优点：改动小；缺点：两份数据仍在。

### P2 — `instructions` 全局提示

`initialize` / `server/discover` 的 `instructions` 字段（SDK 已支持 `InitializeResult.Instructions`）。内容应是**跨工具硬约束**，而不是重复工具描述：

1. 先用 `list_sources` / `knowledge_base_list` 确认库与源，再检索；不要凭空猜源名。
2. 检索四选一：语义 `semantic_search` / 精确 `lexical_search` / 混合 `hybrid_search` / 跨库 `knowledge_base_search`。
3. 拿到的都是 `chunkId + knowledgeBaseId`，展开用 `knowledge_base_drill_down`，看所属文件用 `get_document_info`。
4. 所有文件路径都要来自 `list_sources` / `get_file_tree`，绝对路径会被沙箱拒绝。
5. 单次结果有上限，需要更多时用过滤条件而不是加大 `topK`。

### P3 — `outputSchema` 分批

成本：每工具约 +600B。建议只给**结构稳定、被下游程序消费**的 6 个工具补：`list_sources` / `get_index_status` / `get_system_stats` / `knowledge_base_list` / `knowledge_base_search` / `get_document_info`。其余（`embed_*`、`calculate_similarity` 等）结果简单，文本即可。

---

## 6. 同类门禁

| 编号 | 门禁 | 锁定的不变量 |
| :--- | :--- | :--- |
| G-D1 | 失败路径 `errorCode` 全覆盖 | 遍历全部工具的已知失败输入，断言 `structuredContent.errorCode` 非空 |
| G-D2 | `knowledge_base_search.topK` 夹取 | `topK=900` → `count<=50` 且回显 `effectiveTopK` |
| G-D3 | 结果体积上限 | 大 limit 调用 payload < 512KB，且 `truncated:true` |
| G-D4 | 参数类型与描述一致 | schema 中声明 `string` 却要求 JSON 数组的参数，描述必须含「JSON 数组字符串」 |
| G-D5 | 重叠工具互相指引 | 四组工具每条描述必须含指向邻居的短语 |
| G-D6 | 整型参数写明上下界 | 所有 `integer` 参数描述含「上限/最大/最多/范围/建议」之一 |
| G-D7 | 描述组件门禁（checker 化） | 把 `check_zh.py` 的 `usage-guidance` / `neighbour` / `limits` / `output-schema` 四类降为 0 WARN |
| G-D8 | `instructions` 存在且含硬约束 | `initialize` 返回 `instructions`，断言含「先 list_sources」「chunkId」两条 |
| G-D9 | 描述单一事实源 | 注册表描述与协议描述一致（同一字符串来源） |

---

## 7. 工作顺序与风险

| 序 | 任务 | 触碰面 | 风险 |
| :--- | :--- | :--- | :--- |
| 1 | E1/E2/E5（错误码 + topK 夹取 + 描述清理） | `IndexStatusTools` / `RagSearchTools` / `KnowledgeBaseMcpTools` | 低 |
| 2 | E3/E4（体积守卫 + 参数文案） | 同上 + store | 低 |
| 3 | P2 `instructions` | SDK + 消费方启动期 | 低（官方字段，纯增量） |
| 4 | P1 提示词结构化（方案 A 注册表收敛） | `McpCatalogRegistry` + SDK Provider | 中（改描述会动所有工具，需重跑选型门禁） |
| 5 | P3 `outputSchema` 分批 | 6 个工具类 | 低 |

---

## 8. 明确不做

| 项 | 理由 |
| :--- | :--- |
| 全量长描述 augment（论文式） | 实测步数 +67%、16.67% 场景回归；本项目描述已属长句 |
| LLM 自动生成描述 | 收益不确定；注册表已有可用业务描述 |
| 把 JSON 数组参数改成 `array` 类型 | 会破坏现网调用方；改为在描述里说清并保留容错 |
| 为工具选择引入状态机 | 引导是文本问题，无生命周期 |
| 给全部 25 工具加 `outputSchema` | +15KB，多数结果简单，收益低于成本 |

---

## 9. 参考

- MCP 规范 Tools：`description` 为人类可读功能描述；`outputSchema` 定义后服务端 MUST 返回符合它的结构化结果；无参工具推荐 `additionalProperties:false`；执行错误用 `isError` 提供可自纠反馈。
- `modelcontextprotocol.info` "Writing Effective MCP Tools"：描述 < 8 词告警；参数无描述/enum 告警；无 `required` 告警；重叠描述 > 50% Jaccard 告警；四条选择判定（正确工具胜出 / 任务词在定义中出现 / 与次优差 > 20%）。
- arXiv 2602.14878 "MCP Tool Descriptions Are Smelly!"：856 工具 / 103 服务器，**97.1% 至少一种 smell，56% 用途不清**；全量 augment 成功率 +5.85pp，但步数 +67.46%、16.67% 回归；移除 Examples 组件不降性能 → **选择性补充优于全量**。
- 本项目实测脚本：`/tmp/mcpcheck/tool_design_check.py`（业界原版）、`check_zh.py`（中文适配版）、`tasks.json`（25 条真实任务）。
