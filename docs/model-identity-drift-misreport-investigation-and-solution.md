# 知识库「偏离(建议重排)」误报问题调研与修复方案

> 问题提出：生产环境（`http://<PROD_HOST>:5123`）「默认知识库」在绑定模型 `local_bge-m3` 后，
> 卡片恒显示橙色徽标 **偏离(建议重排)**，与全局配置实际已对齐的事实矛盾。

---

## 1. 结论速览

| 项 | 内容 |
| :--- | :--- |
| **问题本质** | 嵌入模型**身份字段与展示字段混用**，导致配置漂移检测的字符串比较恒不相等 |
| **直接根因** | `EmbeddingSpec.ModelName` 存入展示名 `"BGE M3"`，全局配置存规范 ID `"bge-m3"`，`string.Equals` 永假 |
| **是否为真偏离** | **否**。同一模型（bge-m3）的两种称谓，索引无需重排 |
| **影响面** | 所有经 `local_*` / `prov_*` 模板路径生成 Spec 的知识库；纯词法库不受影响（早退分支） |
| **修复层次** | 上游（Spec 生成改用规范 ID）+ 下游（漂移检测走身份规范化比较）双层防御 |
| **门禁** | `ModelIdentityDriftGateTests`（20 用例，含生产场景复现与反向断言） |

---

## 2. 问题定位（证据链）

### 2.1 现象

生产「默认知识库」卡片显示：

```
绑定模型: local_bge-m3    偏离(建议重排)
```

其中「偏离(建议重排)」徽标由 [KnowledgeBases.js:1300](file:///E:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/Business/ReferenceRAG.Business/wwwroot/app/views/KnowledgeBases.js#L1300) 渲染，条件是 `driftMap[kb.id]?.status === 'Drifted'`。

### 2.2 后端判定逻辑

`GET /api/knowledge-bases/{id}/drift` → `CheckConfigDriftAsync`
（[KnowledgeBaseService.cs:1108](file:///E:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/Business/ReferenceRAG.Business/Features/KnowledgeBase/Services/KnowledgeBaseService.cs#L1108)）：

```csharp
var currentSpec = await _catalog.GetEmbeddingSpecAsync(kb.EmbeddingSpecId, ct);
var globalConfig = _configManager?.Load();
var globalModel = globalConfig?.Embedding?.ModelName ?? "default-model";
var currentModel = currentSpec?.ModelName ?? "unknown";

bool modelMatches = string.Equals(currentModel, globalModel, StringComparison.OrdinalIgnoreCase);
```

### 2.3 生产实测数据

远程只读探测（API Key 经 stdin 传入，不落盘不外泄）：

```json
{
  "Status": "Drifted",
  "CurrentEmbeddingModel": "BGE M3",        ← KB 固化的 Spec.ModelName（展示名）
  "GlobalDefaultEmbeddingModel": "bge-m3",  ← 全局 appsettings Embedding.ModelName（规范 ID）
  "CurrentChunkingStrategy": "SlidingWindow",
  "GlobalChunkingStrategy": "Balanced",
  "CanUpgrade": true
}
```

配套证据：生产 `appsettings.json` 中 `embedding.localOnnx.modelName = "bge-m3"`，
而 `ModelInfo` 注册表（[ModelManager.cs:561](file:///E:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/Infrastructure/ReferenceRAG.Infrastructure/Features/ModelManagement/Services/ModelManager.cs#L561)）中同一模型为：

```csharp
new ModelInfo
{
    Name = "bge-m3",          // 规范 ID：用于 Spec Id（"local_bge-m3"）与全局配置
    DisplayName = "BGE M3",   // 展示名：仅用于 UI 呈现
    ...
}
```

**同一模型，两个字段，两种称谓。**

### 2.4 根因确认：Spec 生成时错存展示名

[KnowledgeBaseService.cs:765](file:///E:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/Business/ReferenceRAG.Business/Features/KnowledgeBase/Services/KnowledgeBaseService.cs#L765)（修复前）：

```csharp
foreach (var m in downloaded)
{
    var specId = $"local_{m.Name}";           // Spec Id 用规范 ID
    if (!seenIds.Contains(specId))
    {
        result.Add(new EmbeddingSpec(
            Id: specId,
            ModelName: m.DisplayName ?? m.Name,   // ← ModelName 却用展示名！
            ...
        ));
    }
}
```

**同一个循环内，`Id` 用 `m.Name`（规范），`ModelName` 用 `m.DisplayName`（展示）** —— 字段语义自相矛盾。

`ModelName` 的实际语义是**模型身份键**（与全局配置 `Embedding.ModelName`、`Spec.Id` 同源比较），
却混入了仅为 UI 呈现而设计的 `DisplayName`。

### 2.5 同类问题：服务商 Spec 分支

[KnowledgeBaseService.cs:795](file:///E:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/Business/ReferenceRAG.Business/Features/KnowledgeBase/Services/KnowledgeBaseService.cs#L795)（修复前）存在同样缺陷：

```csharp
ModelName: $"{prov.Name} / {mod.DisplayName ?? mod.ModelId}",  // "SiliconFlow / BGE M3"
```

把**厂商名**也拼进了身份键，进一步污染比较语义。

### 2.6 旁证：代码中已有应对该差异的 hack

[KnowledgeBaseService.cs:1598](file:///E:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/Business/ReferenceRAG.Business/Features/KnowledgeBase/Services/KnowledgeBaseService.cs#L1598)：

```csharp
var lowerModelName = modelName.ToLowerInvariant();
if (lowerModelName.Contains("bge-m3") || ...)
```

这里用 `Contains` 而非相等比较，正是因为作者已察觉 `ModelName` 可能混入展示名形式
（`"BGE M3"` 不含 `"bge-m3"` 子串，实为该 hack 的漏网案例）。这类散落的
`Contains` 判断正是缺乏统一身份规范化的典型症状。

---

## 3. 业界主流方案调研

### 3.1 LangChain：model 与 model_provider 分离

LangChain 的 `init_chat_model` / `init_embeddings` 明确将**模型规范 ID** 与
**提供方**作为独立参数，而非拼接成前缀字符串：

> "Provider of the model, passed separately instead of as a prefix on model.
> Equivalent to the prefix form — e.g. `model='claude-sonnet-4-5'`,
> `model_provider='...'`"

**借鉴点**：身份由「规范 ID + provider」两个正交字段承载，绝不把展示名或厂商名
拼进身份字符串。本项目中 `EmbeddingSpec` 已有 `IngestionBackend` / `SearchBackend`
字段承载后端信息，`ModelName` 理应只存模型本身。

### 3.2 Qdrant：结构化模型标识 + 蓝绿迁移

Qdrant 的模型迁移教程在 upsert 时以结构化方式传入模型标识：

```python
models.PointStruct(
    id=1,
    vector=models.Document(text="...", model=OLD_MODEL),  # 规范模型标识
    payload={"text": "..."}
)
```

并提供**蓝绿迁移**（双集合双写 + 后台重嵌入 + 别名切换）与**命名向量**两种
零停机方案，同时强调「切换前先对比检索质量」。

**借鉴点**：模型标识是结构化、可比较的规范值；模型变更的处置是显式的迁移流程
（本项目已实现蓝绿影子重构，见 §5.3）。

### 3.3 嵌入漂移检测（Embedding Drift）文献

Zilliz / Evidently AI 等提出的 embedding drift 检测，指**向量分布统计特性**
的漂移（用 KS 检验、核双样本检验等对比参考集与当前分布），属于**数据质量监控**范畴。

> "To detect embedding drift, developers typically compare the statistical
> properties of current embeddings against a reference set."

**与本题的区别**：本题是**配置身份漂移**（configuration identity drift），
即「索引由哪个模型生成」这一元数据与「当前应使用哪个模型」的一致性校验，
不涉及向量分布统计。二者互补：前者防「用错模型检索」，后者防「数据分布变质」。
本修复不引入统计检验，聚焦身份比较的正确性。

### 3.4 方案对比

| 方案 | 做法 | 优点 | 缺点 | 适用性 |
| :--- | :--- | :--- | :--- | :--- |
| A. 直接字符串相等（现状） | `string.Equals(a, b)` | 简单 | 对称谓差异零容忍，误报 | ✗ |
| B. 仅上游修正 | Spec 生成改用规范 ID | 根治新数据 | 存量数据仍误报 | △ 需配合 C |
| C. 规范化比较 | 剥离非字母数字 + 后缀 + 厂商前缀后比较 | 新老数据均正确 | 需维护规范化规则 | ✓ 采用 |
| D. 引入模型 URN/哈希 | 全局唯一模型标识（如 `local:bge-m3`） | 最严谨 | 需数据迁移 + 全链路改造 | △ 过度设计 |

**采用 B+C 双层**：上游保证新数据正确，下游对存量数据免疫，成本最低且无数据迁移。

---

## 4. 系统内可复用基础设施核查

| 需求 | 现有设施 | 结论 |
| :--- | :--- | :--- |
| 模型规范 ID 来源 | `ModelInfo.Name`（`"bge-m3"`） | ✅ 直接可用，无需新增 |
| 模型展示名来源 | `ModelInfo.DisplayName`（`"BGE M3"`） | ✅ 保留供 UI 使用 |
| 规范化工具类 | 无（`QueryVectorCacheProxy.NormalizeAndHashQuery` 仅处理查询文本） | ❌ 新增 `ModelIdentity` |
| 漂移状态枚举 | `DriftStatus { Aligned, Drifted, Customized }` | ✅ 语义已足够，无需扩展 |
| 蓝绿重构基础设施 | `TriggerModelReindexJobAsync` + JobCoordinator | ✅ 已实现（见 §5.3） |
| 第三方库 | 无需引入（纯字符串规范化） | ✅ 零新增依赖 |

**结论：零新增运行时依赖，仅新增一个静态工具类。**

---

## 5. 具体解决方案

### 5.1 上游修复：Spec 生成改用规范 ID

`local_*` 分支（[KnowledgeBaseService.cs:765](file:///E:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/Business/ReferenceRAG.Business/Features/KnowledgeBase/Services/KnowledgeBaseService.cs#L765)）：

```csharp
ModelName: m.Name,   // 原为 m.DisplayName ?? m.Name
```

`prov_*` 分支（[KnowledgeBaseService.cs:795](file:///E:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/Business/ReferenceRAG.Business/Features/KnowledgeBase/Services/KnowledgeBaseService.cs#L795)）：

```csharp
ModelName: mod.ModelId,   // 原为 $"{prov.Name} / {mod.DisplayName ?? mod.ModelId}"
```

> 注：此修改不改变 Spec Id（`local_{m.Name}` 本就使用规范 ID），故不影响既有 KB 的
> `EmbeddingSpecId` 引用，仅修正 `ModelName` 字段内容。新建 KB 快照路径
> （`spec_emb_{id}`，[KnowledgeBaseService.cs:117](file:///E:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/Business/ReferenceRAG.Business/Features/KnowledgeBase/Services/KnowledgeBaseService.cs#L117)）
> 本就使用 `globalCfg.ModelName`（规范 ID），无需改动。

### 5.2 下游防御：模型身份规范化比较

新增 [ModelIdentity.cs](file:///E:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/Infrastructure/ReferenceRAG.Infrastructure/Features/ModelManagement/Services/ModelIdentity.cs)（Infrastructure 层，静态工具类）：

规范化管线（`NormalizeKey`）：
1. 去首尾空白
2. 取末段 —— 剥离 `"Provider / "` 前缀
3. 转小写
4. 迭代剥离量化/版本后缀（`-q4_k_m`、`-v1.5` 等，最多 4 层）
5. 剔除所有非字母数字字符

效果：

| 输入 | 规范化键 |
| :--- | :--- |
| `"BGE M3"` | `bgem3` |
| `"bge-m3"` | `bgem3` |
| `"SiliconFlow / BGE M3"` | `bgem3` |
| `"bge-m3-q4_k_m"` | `bgem3` |
| `"text-embedding-3-small"` | `textembedding3small` |
| `"text-embedding-3"` | `textembedding3` |

漂移检测改为（[KnowledgeBaseService.cs:1139](file:///E:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/Business/ReferenceRAG.Business/Features/KnowledgeBase/Services/KnowledgeBaseService.cs#L1139)）：

```csharp
bool modelMatches = ModelIdentity.IsSameModel(currentModel, globalModel);
```

**空值语义**：任一侧为空返回 `false`（无法判定即不判等），避免空值把任意模型误判为对齐。

**尺寸档次保护**：`small` / `large` 属不同模型，后缀剥离规则只匹配
`q\d+` / `v\d+` 形式，不会误伤 `text-embedding-3-small` vs `text-embedding-3`。

### 5.3 存量数据处置

生产 DB 中 `local_bge-m3` 的 `ModelName` 已是 `"BGE M3"`。**无需数据迁移**：
`ModelIdentity` 规范化后与 `"bge-m3"` 相等，漂移检测直接修正。
下次该 Spec 被重新生成（如模型重新下载）时，`ModelName` 会自动变为规范 ID。

若确需立即统一存量值，可在部署后执行一次性修正（可选，非必需）：

```sql
UPDATE embedding_specs SET model_name = 'bge-m3' WHERE id = 'local_bge-m3';
```

但本方案不依赖它 —— 这正是双层设计的价值：**修复不等同于数据迁移**。

---

## 6. 同类门禁与防回归测试

新增 [ModelIdentityDriftGateTests.cs](file:///E:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/tests/ReferenceRAG.Tests/ModelIdentityDriftGateTests.cs)（20 用例，三层防线）：

### 第一层：规范化器语义（11 用例）
- 展示名 vs 规范 ID 判等：`"BGE M3"` ≡ `"bge-m3"`、`"bge.m3"` ≡ `"BGEM3"` 等
- 量化/版本后缀不改变身份：`"bge-m3"` ≡ `"bge-m3-q4_k_m"`
- 厂商前缀剥离：`"SiliconFlow / BGE M3"` ≡ `"bge-m3"`
- 不同模型不判等：`bge-m3` ≢ `bge-large-zh-v1.5`、`text-embedding-3-small` ≢ `text-embedding-3`
- 空值不匹配任何值（防空值误判对齐）

### 第二层：Spec 生成用规范 ID（2 用例）
- `local_*` Spec 的 `ModelName` 必须是 `m.Name` 而非 `DisplayName`
- `prov_*` Spec 的 `ModelName` 必须是 `ModelId`，不含厂商名前缀

### 第三层：漂移检测对存量展示名免疫（2 用例）
- **生产场景复现**：DB 中 Spec 存展示名 `"BGE M3"` + 全局配置 `"bge-m3"` → 必须判 `Aligned`
- **反向断言**：全局为 `"qwen-embedding-v2"` → 必须仍判 `Drifted`（防修复过度）

### 门禁命令

```bash
dotnet test tests/ReferenceRAG.Tests/ReferenceRAG.Tests.csproj \
  --filter "FullyQualifiedName~ModelIdentityDriftGateTests"
```

---

## 7. 验证结果

| 验证项 | 命令 | 结果 |
| :--- | :--- | :--- |
| 新门禁 | `--filter FullyQualifiedName~ModelIdentityDriftGateTests` | ✅ 20/20 |
| 核心套件 | `dotnet test tests/ReferenceRAG.Tests` | ✅ 765 通过 / 47 跳过 / 0 失败 |
| 解决方案构建 | `dotnet build` | ✅ 0 错误（42 个存量警告） |
| 前端门禁 | `node check.mjs` + `npm test` | ✅ 不受影响（本次仅后端） |

---

## 8. 风险与回滚

| 风险 | 等级 | 缓解 |
| :--- | :--- | :--- |
| 规范化过度导致不同模型误判对齐 | 中 | 反向断言锁定；`small/large` 尺寸档次不参与后缀剥离；空值不判等 |
| 存量 Spec 的 `ModelName` 仍为展示名 | 低 | 防御层已覆盖；重新生成 Spec 时自动修正 |
| 影响模型能力判断（`Contains("bge-m3")`） | 无 | 规范 ID `"bge-m3"` 含子串 `"bge-m3"`，原 hack 继续有效且更可靠 |
| Spec 变更影响既有 KB 绑定 | 无 | Spec Id 未变（本就使用规范 ID），仅 `ModelName` 字段内容修正 |

**回滚**：纯代码改动，按文件 revert 即可；无数据迁移、无 schema 变更。

---

## 9. 待办

- [ ] 部署到生产并复验 `/api/knowledge-bases/dadd847d18b54162/drift` 返回 `Aligned`
- [ ] （可选）一次性统一存量 Spec 的 `ModelName` 为规范 ID

---

## 参考来源

- [LangChain: init_chat_model — model 与 model_provider 分离](https://reference.langchain.com/python/langchain/chat_models/base/init_chat_model)
- [LangChain: Embedding model integrations](https://docs.langchain.com/oss/python/integrations/embeddings)
- [Qdrant: Migrate to a New Embedding Model（蓝绿迁移）](https://qdrant.tech/documentation/tutorials-operations/embedding-model-migration/)
- [Qdrant: Incremental Embedding Updates](https://qdrant.tech/documentation/tutorials-operations/incremental-embedding-updates/)
- [Milvus: How to Switch Embedding Models](https://milvus.io/zh/blog/milvus-3-0-embedding-model-migration.md)
- [Zilliz: What is embedding drift and how do I detect it?](https://zilliz.com/ai-faq/what-is-embedding-drift-and-how-do-i-detect-it)
- [Evidently AI: 5 methods to detect drift in ML embeddings](https://www.evidentlyai.com/blog/embedding-drift-detection)
- [Drift Detection in Text Data with Document Embeddings (IDEAL 2021)](https://papers.dice-research.org/2021/IDEAL2021_DriftDetectionEmbeddings/Drift-Detection-in-Text-Data-with-Document-Embeddings-public.pdf)
