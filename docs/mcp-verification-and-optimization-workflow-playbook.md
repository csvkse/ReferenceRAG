# MCP 工具面验证与优化流程手册

> 适用对象：ReferenceRAG 的 MCP 服务（`/api/mcp`，25 个工具）
> 目的：把「怎么验证 / 怎么判定不合格 / 怎么修 / 怎么防回归 / 怎么验收」固化成可重复流程，而不是一次性调研
> 关联材料：三轮调研（`mcp-production-tool-verification-and-contract-hardening-study.md`、`mcp-tool-interface-reverify-and-description-optimization-study.md`、`mcp-production-tool-semantics-defects-and-pattern-optimization-study.md`）
> 最后更新：2026-11-17

---

## 0. 一句话流程

```
取产物指纹 → tools/list 全量 → 逐工具真实调用（正常/失败/边界/错拼/并发）
      → 契约校验（outputSchema vs structuredContent）
      → 描述审计（组件 + 预算 + 选型）
      → 分类定级（P0/P1/P2）→ 修 → 加门禁 → 部署 → 复验 → 页面验收 → 写文档
```

**核心原则（三轮验证沉淀下来的）**：

1. **schema 合规 ≠ 工具可用**。第一轮 `tools/list` 100% 合规，真实调用仍暴露「任意文件读取」「结果被吞成空数组」。
2. **异常路径比正常路径更能暴露问题**。错拼参数、不确定 ID、0/负数、超长文本要各打一遍。
3. **不要只看 `success:true`**。要看语义：数字是否自洽、分数是否随内容变化、失败是否可判别。
4. **产物溯源先做，再谈验证**。部署的可能是旧 SDK：容器内 DLL 的 md5 必须等于仓库 feed 里 nupkg 内 DLL 的 md5。
5. **验证脚本必须留下断言与退出码**，否则「跑一遍看着都对」无法回归。

---

## 1. 阶段 A：产物溯源（先确认验的是这版代码）

```bash
# 1) 仓库侧：解包 nupkg 取 SDK 程序集指纹
mkdir -p /tmp/nupkg && cd /tmp/nupkg && rm -rf * \
  && unzip -o -q <repo>/packages/McpHelper.Core.<ver>.nupkg \
  && md5sum lib/net10.0/McpHelper.Core.dll \
  && grep -o 'commit="[^"]*"' McpHelper.Core.nuspec

# 2) 生产侧：容器内程序集指纹
ssh <host> 'docker exec <container> sh -c "ls -l /app/McpHelper.Core.dll && md5sum /app/McpHelper.Core.dll"'

# 3) 部署提交与仓库 HEAD
ssh <host> 'docker ps --format "{{.Names}}\t{{.Image}}\t{{.Status}}"'
```

判定：

| 现象 | 结论 |
|:--|:--|
| 两侧 md5 一致，且 nuspec `commit` == 部署镜像 tag 的短哈希 | 验的是目标版本，可继续 |
| md5 不一致 | **停止**。适配层是旧版，后面所有结论都要重做 |
| nuspec commit 早于修复提交 | 打包顺序错了（先 pack 后提交），需重打 |

> 真实事故：2026-11-16 曾出现 nuspec 记 `8976849` 而修复在 `46d04f1`，镜像「健康 + 接口全通」却跑的是旧 SDK。本地 NuGet 缓存 `~/.nuget/packages/mcphelper.core/<ver>` 也要在重打后清掉，否则构建仍复用旧程序集。

---

## 2. 阶段 B：协议面基线（`initialize` / `tools/list`）

一次性采集，作为后续所有断言的输入：

```python
# 最小 SSE JSON-RPC 客户端（无需任何依赖）
POST /api/mcp
headers: Content-Type: application/json
         Accept: application/json, text/event-stream
         X-API-Key: <来自 env/.envprod 或 .envdev>
body:   {"jsonrpc":"2.0","id":1,"method":"initialize","params":{
            "protocolVersion":"2025-06-18","capabilities":{},
            "clientInfo":{"name":"rr-verify","version":"1"}}}
# 响应是 SSE：逐行取 data: 之后最后一条
```

必须采集并断言的项：

| 检查 | 判定标准 | 失败后果 |
|:--|:--|:--|
| `instructions` 存在 | 非空，且包含「先 list_sources」「chunkId」「errorCode」三条硬约束 | 模型不知道工具间调用顺序，只能靠试错 |
| 工具数量与注册一致 | 25（`McpCatalogRegistry` 与反射注册集相同） | 权限矩阵与协议面漂移 |
| `title` 覆盖 | 25/25 | 客户端 UI 只能显示 snake_case 名 |
| `outputSchema` 形态 | 声明了的工具必须 `anyOf[成功, 失败]` | 失败载荷不合 schema → 严格客户端校验报错 |
| `inputSchema` | 无参工具 `additionalProperties:false`；有参必须有 `required` | 模型误判「无参」 |
| `annotations` | 只读工具 `readOnlyHint+idempotentHint` | 客户端无法自动放行只读调用 |
| 工具序稳定 | 两次 `tools/list` 顺序一致 | 破坏 prompt cache 命中 |
| 定义体积 | 紧凑 JSON ≤ 30 KB，`outputSchema` 占比 ≤ 35%，单条描述 ≤ 600 字符 | 挤占模型上下文 |

协议版本矩阵（可选，用于确认缓存提示门控）：

| 客户端请求 | 期望 |
|:--|:--|
| `2025-06-18` / `2025-11-25` | 协商成功；`tools/list` 不含 `ttlMs/cacheScope` |
| `2026-07-28` 或更高 | `initialize` 返回 `-32022` 且 `data.supported` 列出受支持版本 |

---

## 3. 阶段 C：逐工具真实调用矩阵

### 3.1 调用分级

每个工具至少跑三档，**参数必须来自上一次调用的真实返回值**（不要凭空编 ID，否则「空结果」分不清是缺陷还是输入错）：

| 档位 | 做法 | 目的 |
|:--|:--|:--|
| 正常 | 真实参数 | 语义正确性、延迟 |
| 失败 | 未知 source / 未知 kb / 未知 chunk / 越界路径 / 空 query | `isError` 可判别、`errorCode` 有值 |
| 边界 | `topK=900`、`limit=0/-1/5000`、`maxMatches=300`、`contextLines=99`、9000 字符、批量 400 条 | 夹取是否生效、是否有回显、是否 `too_large` |

### 3.2 必测的「语义」断言（不是只看 success）

| 工具组 | 必须断言 |
|:--|:--|
| 检索 4 件套 | ① 结果里 `channels` 存在；② 纯词法库上 `semantic_search` 不得伪装成向量命中；③ 不存在的词 → `noMatch:true`；④ `degraded/indexDegraded/kbChannels` 三值齐全 |
| `knowledge_base_drill_down` / `get_document_info` | 用上一步的 `chunkId + knowledgeBaseId` 能定位；未知 chunk → `chunk_not_found` |
| `rerank_results` | ① 同一批文档**换顺序/换查询**，分数必须随内容变化；② 降级时 `relevanceScore` 为 `null` 且 `scoreKind=degraded`；③ 不得出现 `0.85/0.733/0.617` 这类位置衰减序列 |
| 文件 4 件套 | `find_files` 返回的字符串能否直接喂给 `read_note_content`；`search_file_content` 的 hunk 行号能否直接用于按行读取 |
| 统计类 | `get_system_stats.index.*` 与 `get_index_status` 逐库求和一致；`list_sources` 的每行计数不被全局快照覆盖 |
| 向量类 | `embed_text` 与 `calculate_similarity` 使用同一字符上限；`embed_batch` 有条数上限 |

### 3.3 参数绑定专项（最容易「静默」的一类）

```
错拼参数：knowledgeBaseId(单数) / kbIds / topk / source / depth
判定：必须 isError=true 且 errorCode=invalid_argument，消息里列出可用参数名
反例：静默忽略 → 检索了全部知识库还回 success:true（2026-11-16 实际发生）
```

同时验证同一语义在 5+ 个工具上的行为**完全一致**：

| 输入 | 统一期望 |
|:--|:--|
| `null` / `""` / `"[]"` | 视为「不限定」 |
| `["kb"]` JSON 数组 | 单库 |
| `a,b` 逗号分隔 | 两库 |
| `[1,2]` / 未闭合数组 | `invalid_argument`，且**不泄漏** `Path: $ \| LineNumber` |

### 3.4 并发与容量

| 场景 | 期望 |
|:--|:--|
| 8 路并发检索 | 无错误；延迟线性增长可接受（推理侧是串行闸门） |
| 16 路并发 `embed_text` | 无排队失败 |
| 单次响应体积 | 任一工具 ≤ 512 KB；超预算应 `too_large` 拒绝而不是静默截断 |

---

## 4. 阶段 D：契约校验（outputSchema vs structuredContent）

声明了 `outputSchema` 就有规范强制力：**服务器 MUST 返回符合它的 `structuredContent`**（`2025-11-25/server/tools`）。校验要点：

| 检查 | 反例（真实踩过） |
|:--|:--|
| `required` 字段实际存在 | `get_system_stats` 由 `Dictionary<string,object>` 反射，schema 要求 `system.count/capacity`，载荷里根本没有 |
| `type` 与实际值一致 | `list_files.appliedFilters.connectorId` 声明 `string`，实际 `null` |
| 无 `additionalProperties` 外字段 | 载荷里出现 schema 未声明的键 |
| 失败载荷也在 schema 内 | 失败信封带 `structuredContent{success:false,errorCode}`，旧 schema 只描述成功形态 |
| 无反射噪声 | schema 里出现 `capacity`/`comparer`/`values` 一定是 Dictionary 反射 |

校验器可以手写（无需引入 JSON Schema 库）：

```python
def validate(schema, val, path='$'):
    if 'anyOf' in schema:
        for b in schema['anyOf']:
            if not validate(b, val, path): return []
        return [f"{path}: no anyOf branch matched"]
    t = schema.get('type'); types = t if isinstance(t, list) else [t]
    if val is None and 'null' in types: return []
    # object：required → additionalProperties(false/dict) → 递归 properties
    # array：逐项 validate(items)
    # integer/number/string/boolean：类型严格（bool 不算 int）
```

判定：7 个声明 schema 的工具，**成功载荷 + 失败载荷**都必须 0 违规。

---

## 5. 阶段 E：提示词（description）审计

### 5.1 三项独立检查

| 检查 | 方法 | 阈值 |
|:--|:--|:--|
| **组件完整性** | 「用途 / 何时该用 / 邻居取舍 / 上限」四件套 + 无参闭合 + `required` | 组件缺失 = 0 |
| **预算** | 定义总字节、`outputSchema` 占比、单条描述长度 | ≤30 KB / ≤35% / ≤600 字符 |
| **选型** | 25 条真实任务对每个工具做 IDF 加权打分，要求期望工具胜出且与次优差 ≥20% | 明确 ≥17（基线）、错误 ≤1 |

离线审计脚本已入库，可直接接 CI：

```bash
python scripts/mcp-tool-audit.py http://127.0.0.1:5124/api/mcp --tasks scripts/mcp-tool-tasks.json
# 退出码非 0 = 有 FAIL（预算/选型回归）
```

### 5.2 已知的两类「假告警」，不要盲改

| 现象 | 原因 | 处置 |
|:--|:--|:--|
| `精确查找错误码 E1001` 被判给 `search_file_content` | 检查器分词产物：`lexical_search` 描述里没有「精确查找」二元组 | 不改描述；真实语义差异已写在 `UseInstead` |
| 7 条「区分度不足」 | 任务词（文件/向量/版本/切片）被两个工具共同持有，属分词重叠 | 不改描述；裁决顺序写在 `instructions` |

### 5.3 描述层真实缺陷（检查器抓不到，需单独扫）

| 缺陷 | 扫法 |
|:--|:--|
| **幽灵工具名** | 抽出所有 `xxx_yyy` 形态 token，必须 ∈ 工具名 ∪ 错误码 ∪ 示例占位符白名单（曾出现 `kb_list`，真实名 `knowledge_base_list`） |
| **上限自相矛盾** | 同一参数在「特性描述 / 注册表 Limits / 引擎实现」三处比对（曾出现 `maxMatches` 300 vs 500） |
| **未回显生效值** | 凡是夹取过的参数都要回显 `requested*/effective*` |

### 5.4 成本结论（为什么不再加长描述）

- 25 工具定义 25–29 KB；描述 8.8 KB 已是中文长句。
- arXiv 2602.14878：全量 augment 成功率 +5.85pp，但**步数 +67.46%、16.67% 场景回归**。
- Anthropic `writing-tools-for-agents`：优先「减少工具/提高信号密度」，不是「给每个工具写长文」。
- 结论：预算投在**结果语义（structuredContent、降级标志）**与**契约校验**，不是继续堆描述。

---

## 6. 阶段 F：定级与修复顺序

| 级别 | 判定标准 | 例 |
|:--|:--|:--|
| **P0** | 输出不可信且无标志 | 重排降级发伪分数；越权读任意文件；结果被整段吞掉 |
| **P1** | 语义丢失 / 静默改变行为 | 静默忽略错拼参数导致扩大检索范围；纯词法库伪装成向量命中；无 `noMatch` |
| **P2** | 契约与文案不一致 | `outputSchema` 违规；幽灵工具名；上限矛盾；缺 `title` |

修复顺序按「正确性 → 契约 → 文案 → 评估」：

```
重排分数语义 → 检索通道/降级/无结果 → 参数单入口 + 边界 → outputSchema 双形态
→ 未知参数拒绝（SDK）→ 文案与 title → 离线审计基线
```

**设计模式落点（及明确不引入的）**：

| 模式 | 落点 | 结论 |
|:--|:--|:--|
| Adapter | 参数解析（`knowledgeBaseIds` 单入口）、重排分数来源 | 要做：消除「同参不同义」 |
| Strategy | 召回通道/融合 | 已有，只需把通道事实传出去，不新增策略 |
| Registry | 描述 + 权限 + 注解单一事实源 | 已有，保持 |
| **StateMachine** | 降级 / 工具选择 | **不做**：降级是每请求属性，非生命周期；工具选择无状态 |
| **Decorator** | 统一校验 | **不做**：SDK handler 已是统一入口，塞进 Decorator 会让校验职责再次分裂 |

---

## 7. 阶段 G：门禁（防回归）

现有 MCP 相关门禁共 **94 项**（`dotnet test --filter FullyQualifiedName~Mcp`），分组如下：

| 文件 | 锁定什么 |
|:--|:--|
| `McpInputAndSemanticsGateTests`(12) | ID 解析一致性、JSON 错误不泄漏内部路径、边界、重排不伪造分数、融合保留通道、无结果判定 |
| `McpToolContractSemanticsGateTests`(6) | `title` 全覆盖、描述只引用真实工具名、描述预算、`outputSchema` 双形态、无 Dictionary 反射 |
| `McpToolDescriptionContractGateTests`(9) | 描述来自注册表、四件套齐全、邻居指引、上限声明、`instructions` 硬约束、`Fail` 必带码、`OkBounded` 拒绝超限 |
| `McpInputSchemaGateTests`(8) | 无参闭合、`required` 与签名一致、命名 snake_case、`list_files` schema |
| `McpToolContractHardeningGateTests`(8) | 失败信封可判别、`structuredContent` 带码、沙箱策略、源解析一致 |
| `McpListFilesFilterGateTests`(7) | `connectorId` 精确过滤、未知 connector 报错、通配符按字面、`truncated` |
| `McpKnowledgeBaseRoutingGateTests`(5) | 检索走知识库、scoped、drill-down |
| `McpKnowledgeBaseStatsGateTests`(3) | 逐库计数不被全局快照覆盖 |
| `scripts/mcp-tool-audit.py` | 描述预算 + 25 条选型（可接 CI，退出码判定） |

门禁编写原则：

1. **用反射枚举工具**（`[McpServerToolType]` + `[McpServerTool]`），新增工具自动纳入覆盖，不维护清单。
2. **断言不变量而非快照**：例如「降级时不得出现 0.85 序列」，而不是「分数等于 0.85」。
3. **门禁跟随契约变更同步更新**：契约从「单一成功形态」改成 `anyOf` 时，旧断言要改成取 `variants[0]`，否则门禁会假失败。

---

## 8. 阶段 H：部署与复验

### 8.1 部署（沿用既有流水线）

```powershell
# dev（快循环，容器 referencerag-dev，端口 5124）
./scripts/deploy-linux.ps1 -Mode Dev -EnvFile env/.envdev

# 仅前端热补（3 秒级）
./scripts/deploy-linux.ps1 -Mode Dev -EnvFile env/.envdev -FrontendOnly

# 生产（先提交 → pack → 提交 nupkg → git archive）
./scripts/deploy-linux.ps1 -Mode Official -EnvFile env/.envprod -Image referencerag:deploy-<short-hash>
```

SDK 改动严格顺序（否则部署的是旧适配层）：

```
改 SDK → 提交 → dotnet pack → 提交 .nupkg → git archive → build 镜像
→ rm -rf ~/.nuget/packages/mcphelper.core/<ver> → 部署 → md5 溯源
```

### 8.2 复验

1. 健康检查 + `tools/list` 与本地 listing 逐字段 diff（`title` / `outputSchema` / `instructions`）。
2. 跑阶段 C 的断言脚本（本文档示例 36 项），全部 PASS 才算通过。
3. 跑 `scripts/mcp-tool-audit.py`，退出码为 0。
4. **产物溯源再核一次**（部署后 md5）。

---

## 9. 阶段 I：页面验收（内置浏览器）

改动涉及前端时必须走这一步，因为「接口对」不等于「页面能用」。

| 页面 | 关注点 |
|:--|:--|
| `/search` | 结果头部标签：`未命中查询`、`通道 向量+词法/词法`、`宽松兜底`；正常查询不误报 |
| `/performance` | 重排预设跑通；降级时显示「降级」标签与原因，不给 S/A/C 评级；相关性列 null 显示「降级」而非 `0.000` |
| `/guide` | MCP 页参数表与真实 schema 一致；有「响应语义与失败约定」表；无 `kb_list` 残留 |
| `/apikeys` | 工具树 25 节点，标题/描述与注册表一致（改 title 不应影响权限树） |

**操作方式（当前环境限制）**：内置浏览器在无可视视口时 `take_screenshot` 会返回 `NATIVE_BROWSER_VIEWPORT_UNAVAILABLE`，`click` 同理。此时改用：

```js
// 结构断言（可替代截图）
() => ({ title: document.title, text: document.body.innerText.slice(0, 800) })
```

并对关键交互用 `document.querySelector(...).click()` + 等待后检查 DOM 文本。**若确实需要视觉证据，必须在报告里写明「未截图及原因」**，不能声称「页面正常」了事。

---

## 10. 结果记录模板

每次复验写进 `docs/` 的调研文档末尾，字段固定：

```
### 复验记录 <日期>
- 目标版本：<镜像 tag> / SDK <ver> @ <commit>
- 产物溯源：容器 md5 = feed md5 = <hash>
- 协议面：instructions ✅ / title 25/25 / outputSchema 7(anyOf) / 定义 29,427B
- 调用矩阵：36 断言，0 失败
- 契约校验：7 工具成功+失败形态 0 违规
- 描述审计：warns 0；选型 17 明确 / 7 歧义 / 1 错误（基线）
- 边界：limit=0→invalid_argument；limit=5000→effective=200；9000 字符→too_large
- 页面：/search /performance /guide /apikeys 结构断言通过（未截图：无可见视口）
- 未做项：<列明原因>
```

---

## 11. 快速清单（Checklist）

**每次改动 MCP 工具后**

- [ ] `dotnet test --filter FullyQualifiedName~Mcp`（94 项）全绿
- [ ] `scripts/mcp-tool-audit.py` 退出码 0
- [ ] 新增/改动的参数：边界（0/-1/超上限）、错拼、回显 `effective*`
- [ ] 新增/改动的输出字段：是否需要在 `outputSchema` 里声明（含失败形态）
- [ ] 新工具：`title` + 注册表四件套 + `instructions` 是否需要补约束
- [ ] 描述里引用的工具名真实存在（无幽灵名）

**每次部署后**

- [ ] 健康检查 200
- [ ] md5 溯源一致（容器 = feed = 新 pack）
- [ ] 断言脚本 36 项全 PASS
- [ ] 页面结构断言（视改动范围）
- [ ] 文档末尾追加复验记录

---

## 12. 参考

- MCP 规范 Tools：`2025-11-25`（`outputSchema` 强制 conformance、`isError` 与协议错误的区分）<https://modelcontextprotocol.io/specification/2025-11-25/server/tools>；draft（`ttlMs/cacheScope`、分页）<https://modelcontextprotocol.io/specification/draft/server/tools>
- Anthropic, *Writing effective tools for agents*（工具选择、命名空间、token 效率、错误引导、评估驱动）<https://www.anthropic.com/research/writing-tools-for-agents>
- Anthropic, *Advanced tool use / tool search*（按需发现，Spring AI 实测 34–64% token 节省）<https://www.anthropic.com/research/advanced-tool-use>
- arXiv 2602.14878, *MCP Tool Descriptions Are Smelly!*（全量 augment 的代价）<https://arxiv.org/html/2602.14878v1>
- 本仓库脚本：`scripts/mcp-tool-audit.py`、`scripts/mcp-tool-tasks.json`；门禁：`tests/ReferenceRAG.Host.Tests/Mcp*.cs`
