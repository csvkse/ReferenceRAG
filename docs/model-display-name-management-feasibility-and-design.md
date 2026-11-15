# 模型管理中「本地/在线模型自定义名称」功能调研与方案

> 需求：在模型管理界面为**本地模型**与**在线（服务商）模型**添加/编辑**模型名称**，
> 从根本上解决「展示名与规范 ID 混用」导致的配置漂移误报等问题。

---

## 0. 结论速览

| 项 | 结论 |
| :--- | :--- |
| **可行性** | ✅ 高。核心基础设施已存在，无需新增运行时依赖 |
| **根本性** | ✅ 是。把「用户可见名称」变成显式可管理字段，与「模型身份」彻底解耦 |
| **性能影响** | 可忽略（仅 registry 填充时一次字典查找 + 配置读取） |
| **主要欠缺** | 持久化缺失（`AddCustomModelAsync` 的 DisplayName 仅存内存）；本地扫描模型的 DisplayName 恒等于目录名 |
| **推荐方案** | **ModelDisplayNameRegistry（策略+适配器）**：以 `ModelUrn` 为键的展示名覆盖注册表，配置持久化，扫描/注册时统一应用 |
| **设计模式** | 适配器模式（本地/在线统一命名源）、策略模式（名称解析优先级）、单例注册表 |
| **工作量** | 后端 ~250 行 + 前端 ~120 行 + 测试 ~150 行 |

**关键认知**：这不是「加个输入框」，而是把系统中**隐式、分散、易腐化**的命名逻辑
收敛为**显式、集中、可管理**的一等公民。vLLM 的 `--served-model-name` 正是此理念的业界标杆。

---

## 1. 现状审计（问题证据表）

### 1.1 命名混乱的三个源头

| 来源 | Name（身份） | DisplayName（展示） | 持久化 | 问题 |
| :--- | :--- | :--- | :--- | :--- |
| **预定义模型** | `bge-m3`（[ModelManager.cs:562](file:///E:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/Infrastructure/ReferenceRAG.Infrastructure/Features/ModelManagement/Services/ModelManager.cs#L562)） | `BGE M3` | 硬编码 | ✅ 一致，但用户不可改 |
| **本地目录扫描** | 目录名（如 `bge-m3-Q4_0`） | **恒等于目录名**（[ModelManager.cs:1672](file:///E:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/Infrastructure/ReferenceRAG.Infrastructure/Features/ModelManagement/Services/ModelManager.cs#L1672)） | 无（每次扫描重建） | ❌ 用户看到的是文件名，不可自定义 |
| **在线服务商模型** | `RegisteredModelAsset.ModelId`（如 `BAAI/bge-m3`） | `DisplayName`（可空） | ✅ 配置 | ⚠️ 有字段但常为空，回退到 ModelId |
| **自定义添加** | HF ID 末段 | `displayName ?? modelName`（[ModelManager.cs:2819](file:///E:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/Infrastructure/ReferenceRAG.Infrastructure/Features/ModelManagement/Services/ModelManager.cs#L2819)） | ❌ **仅内存** | ❌ 重启即丢 |

### 1.2 已暴露的缺陷

**缺陷 A：自定义模型的 DisplayName 不持久化**

`AddCustomModelAsync` 把 `DisplayName` 写进 `_modelRegistry`（内存），但：
- `RefreshLocalModels()` → `ScanLocalModels()` 会重建未预注册条目，覆盖之
- 进程重启后 `InitializeRegistry()` 重新加载，自定义 DisplayName 丢失

**缺陷 B：本地扫描模型的展示名不可用**

```csharp
// ModelManager.cs:1672（本地目录扫描）
_modelRegistry[modelName] = new ModelInfo
{
    Name = modelName,
    DisplayName = modelName,   // ← 直接用目录名，如 "bge-m3-Q4_0"
    ...
};
```

用户在模型管理页看到的是 `bge-m3-Q4_0` 而非「BGE M3 量化版 Q4」。

**缺陷 C：展示名被误用为身份键（上一轮已修复的漂移误报根因）**

`EmbeddingSpec.ModelName` 曾存入 `m.DisplayName`（`"BGE M3"`），与全局配置的
`"bge-m3"` 比较恒不相等 → 永久误报「偏离(建议重排)」。

> 详见 [model-identity-drift-misreport-investigation-and-solution.md](file:///E:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/docs/model-identity-drift-misreport-investigation-and-solution.md)。
> 该修复（`ModelIdentity` 规范化比较）是**兜底防御**，本功能是**正本清源**。

### 1.3 散落的命名回退逻辑

前端有 20+ 处 `row.displayName || row.name` 回退；后端有
`m.DisplayName ?? m.Name`、`mod.DisplayName ?? mod.ModelId`、
`$"{prov.Name} / {mod.DisplayName ?? mod.ModelId}"` 等多种拼法。
**缺乏统一的名称解析入口**是本类问题反复出现的结构性原因。

---

## 2. 头脑风暴：可行性与欠缺分析

### 2.1 功能可行性

| 维度 | 评估 |
| :--- | :--- |
| 技术可行性 | ✅ 高。需新增：配置字段 + registry 填充钩子 + API + UI |
| 数据可行性 | ✅ 无需迁移。新增配置节，缺省回退现状行为 |
| 兼容性 | ✅ 纯加法。不改 `ModelInfo.Name`（身份），仅增加展示名覆盖 |

### 2.2 逻辑层面欠缺与对策

| 欠缺 | 影响 | 对策 |
| :--- | :--- | :--- |
| **名称与身份混淆风险** | 用户把展示名当身份用（如 API 传展示名） | 展示名**仅用于 UI 呈现**；所有 API/配置仍用 `Name`/`ModelUrn`；UI 同时展示二者 |
| **重名冲突** | 两个模型设同一展示名，UI 困惑 | 不强制唯一（vLLM 亦不强制），但 UI 在重名时附带身份标识消歧 |
| **本地模型被删除后名称残留** | 配置堆积孤儿条目 | 删除模型时联动清理；registry 填充时校验存在性 |
| **目录改名导致名称「错位」** | 用户给 `bge-m3-Q4_0` 命名「主力模型」，后目录改名 | 以 `ModelUrn`（含 modelName）为键，目录改名即视为新模型，名称不继承（符合直觉） |
| **在线模型改名影响检索** | 展示名变更是否触发重索引 | **不触发**。展示名非身份，与向量生成无关（与「切换模型」严格区分） |

### 2.3 性能层面评估

| 场景 | 成本 | 评估 |
| :--- | :--- | :--- |
| registry 填充时应用名称覆盖 | 一次 `Dictionary<ModelUrn,string>` 查找 | 可忽略（O(1)，模型数量级 <100） |
| 配置读取 | 已有 `_configManager.Load()` 调用点复用 | 可忽略 |
| 前端渲染 | 无额外请求（名称随 `/Models` 列表返回） | 可忽略 |
| 配置体积 | 每模型 ~50 字节，百模型 ~5KB | 可忽略 |

**结论：无性能风险。**

### 2.4 安全与凭据

展示名是纯文本元数据，不涉及凭据。但需防注入：
- 长度上限（如 64 字符）
- 禁控制字符（`\r\n\t` 等）
- 前后端双重校验

---

## 3. 业界主流方案调研

### 3.1 vLLM：`--served-model-name`（最相关标杆）

vLLM 明确区分两个概念：

- `--model`（位置参数）：**底层 HuggingFace 模型路径**，即真实身份
- `--served-model-name`：**API 可见名称**，即对外展示/调用名

官方文档与社区确认：

> "The `--served-model-name` argument is still supported for customizing the
> API-visible model name(s) and remains the correct way to set alternative
> model names for the API server."

关键特性：
- **支持多个别名**（`--served-model-name name1 name2`）
- **身份与展示彻底分离**：改 served name 不影响加载哪个模型
- **可随时变更**：无需重新加载模型权重

**对本项目的启示**：展示名应当是「可随时修改、不影响模型身份与加载」的轻量元数据。

### 3.2 MLflow Model Registry：registered model + aliases

MLflow 的注册模型包含 **name（唯一）、versions、aliases、tags**：

> "A registered model has a unique name, contains versions, aliases, tags,
> and other metadata."

- `name` 是注册主名（唯一）
- `aliases` 是可移动指针（如 `champion`、`challenger`）
- `tags` 是任意键值元数据

**启示**：展示名可建模为「主名 + 别名集合」。但本项目一模型一展示名已足矣，
引入完整 alias 体系属过度设计（见 §5.2 方案 D 评估）。

### 3.3 Ollama：alias 需求存在但未实现

Ollama 社区有明确的 alias 诉求（[issue #5914](https://github.com/ollama/ollama/issues/5914)）：

> "I think it would be helpful to be able to create alias names for models."

Ollama 目前的变通是 `ollama cp`（复制模型）或 Modelfile 的 `FROM`，
但均非轻量别名。**这印证了该需求是普遍存在的，且轻量实现（不改动模型本体）是正解。**

### 3.4 方案对比

| 方案 | 做法 | 优点 | 缺点 | 结论 |
| :--- | :--- | :--- | :--- | :--- |
| **A. 复用现有 DisplayName 字段** | 让用户改 `ModelInfo.DisplayName` 并持久化 | 改动最小 | `DisplayName` 语义被扫描逻辑覆写；本地模型无持久化位 | △ 需配合注册表 |
| **B. 独立展示名注册表**（推荐） | 新增 `ModelDisplayNames: { "urn": "名称" }` 配置节，registry 填充时应用 | 与身份完全解耦；扫描不覆写；可随时改 | 多一个配置节 | ✅ **采用** |
| **C. 改名即改目录** | 用户输入新名 → 重命名模型目录 | 「彻底」 | 破坏身份（Name=目录名）；影响已绑定的 Spec/配置；高风险 | ✗ 否决 |
| **D. 完整 alias 体系** | 引入多别名 + 标签 + 主名 | 最灵活 | 过度设计；UI/API 复杂度陡增 | ✗ 不采用 |

---

## 4. 系统可复用基础设施核查

| 需求 | 现有设施 | 位置 | 复用方式 |
| :--- | :--- | :--- | :--- |
| **模型身份权威键** | `ModelUrn`（`local:{name}` / `provider:{id}:{modelId}`） | [ModelUrn.cs](file:///E:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/Infrastructure/ReferenceRAG.Infrastructure/Features/ModelManagement/Helpers/ModelUrn.cs) | ✅ 直接作为注册表键 |
| **配置持久化 + 用户覆盖先例** | `DisabledModels` 列表 + `ToggleModelEnabled` | [ModelManager.cs:3385](file:///E:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/Infrastructure/ReferenceRAG.Infrastructure/Features/ModelManagement/Services/ModelManager.cs#L3385) | ✅ 完全同构的「读配置→改→Save→更新 registry」模式 |
| **在线模型展示名字段** | `RegisteredModelAsset.DisplayName` | [ObsidianRagConfig.cs:1201](file:///E:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/Infrastructure/ReferenceRAG.Infrastructure/Models/ObsidianRagConfig.cs#L1201) | ✅ 在线模型已有持久化位，统一收编 |
| **统一模型目录解析** | `ModelAssetResolver.GetCatalog()` | [ModelAssetResolver.cs](file:///E:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/Infrastructure/ReferenceRAG.Infrastructure/Features/ModelManagement/Services/ModelAssetResolver.cs) | ✅ 目录选项的 `Label` 也走同一解析 |
| **本地/在线统一抽象** | `IModelManager` / `ModelProviderConfig` 双通道 | — | ✅ 适配器模式天然统一 |
| **身份规范化比较** | `ModelIdentity`（上一轮新增） | [ModelIdentity.cs](file:///E:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/Infrastructure/ReferenceRAG.Infrastructure/Features/ModelManagement/Services/ModelIdentity.cs) | ✅ 展示名不参与身份比较，继续兜底 |
| **策略模式实践** | `IBackendResolutionStrategy` + Factory | Strategies/ | 📖 设计参照 |
| **状态机实践** | `ModelLifecycleStateMachine` | StateMachine/ | 📖 设计参照 |
| **第三方库** | **无需引入** | — | 纯字典+配置 |

**结论：零新增依赖，全部复用现有基础设施。**

---

## 5. 具体方案

### 5.1 架构：三层解耦

```
┌─────────────────────────────────────────────────────┐
│  UI 层（Models.js）                                  │
│  · 展示：displayName（来自注册表解析）                │
│  · 编辑：重命名按钮 → 弹窗 → API                     │
└──────────────────────┬──────────────────────────────┘
                       │ PUT /Models/{name}/display-name
┌──────────────────────▼──────────────────────────────┐
│  服务层（ModelManager / ModelsController）            │
│  · SetModelDisplayName(urn, name) → 校验 + 持久化    │
│  · ResetModelDisplayName(urn) → 清除覆盖             │
└──────────────────────┬──────────────────────────────┘
                       │
┌──────────────────────▼──────────────────────────────┐
│  注册表层（ModelDisplayNameRegistry）★新增            │
│  · 键：ModelUrn（身份）  值：string（展示）           │
│  · 持久化：ObsidianRagConfig.ModelDisplayNames       │
│  · 应用时机：InitializeRegistry / ScanLocalModels 后 │
└─────────────────────────────────────────────────────┘
```

### 5.2 核心组件：`ModelDisplayNameRegistry`

新增 [ModelDisplayNameRegistry.cs](file:///E:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/Infrastructure/ReferenceRAG.Infrastructure/Features/ModelManagement/Services/ModelDisplayNameRegistry.cs)（Infrastructure 层）：

```csharp
/// <summary>
/// 模型展示名覆盖注册表（策略模式 + 单例注册表）。
/// 以 ModelUrn 为身份键，承载用户自定义的展示名称；
/// 与 ModelInfo.DisplayName（硬编码/扫描推导）解耦，互不覆写。
/// </summary>
public sealed class ModelDisplayNameRegistry
{
    private readonly ConfigManager _configManager;
    private Dictionary<string, string> _overrides; // key: urn.ToString(), value: 展示名

    public ModelDisplayNameRegistry(ConfigManager configManager) { ... }

    /// <summary>解析某模型的最终展示名（策略优先级）</summary>
    public string Resolve(ModelUrn urn, string fallback)
        => _overrides.TryGetValue(urn.ToString(), out var custom) && !string.IsNullOrWhiteSpace(custom)
            ? custom
            : fallback;   // fallback = 现有 DisplayName/Name

    /// <summary>设置覆盖（含校验：非空、≤64 字符、无控制字符）</summary>
    public bool TrySet(ModelUrn urn, string? displayName, out string? error);

    /// <summary>清除覆盖，回退到系统推导名</summary>
    public bool Reset(ModelUrn urn);

    /// <summary>清掉已不存在模型的孤儿条目</summary>
    public int PruneOrphans(Func<ModelUrn, bool> existsPredicate);
}
```

**设计模式应用**：

| 模式 | 应用点 | 收益 |
| :--- | :--- | :--- |
| **策略模式** | `Resolve(urn, fallback)` 封装「用户覆盖 → 系统推导」的优先级策略 | 新增名称来源（如「按维度自动命名」）无需改调用方 |
| **注册表模式** | 单一权威字典，替代散落各处的 `DisplayName ?? Name` | 消灭 20+ 处回退逻辑的不一致 |
| **适配器模式** | `ModelUrn` 统一「本地/在线」两种身份表示 | 本地与在线模型共用一套命名 API |

### 5.3 配置 schema 扩展

[ObsidianRagConfig.cs](file:///E:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/Infrastructure/ReferenceRAG.Infrastructure/Models/ObsidianRagConfig.cs) 新增：

```csharp
/// <summary>
/// 模型展示名覆盖表（键为 ModelUrn，如 "local:bge-m3" / "provider:prov_sf:BAAI/bge-m3"）
/// </summary>
public Dictionary<string, string> ModelDisplayNames { get; set; } = new();
```

与既有 `DisabledModels` 并列，语义清晰（一个管「是否启用」，一个管「如何称呼」）。

### 5.4 应用时机（关键）

在 `ModelManager` 的两处填充后统一应用：

```csharp
private void InitializeRegistry()
{
    _modelRegistry.Clear();
    foreach (var model in PredefinedModels)
        _modelRegistry[model.Name] = model.Clone();
    ScanLocalModels();
    ApplyDisplayNameOverrides();   // ★ 新增
    CheckConfiguredModel();
}

public void RefreshLocalModels()
{
    ScanLocalModels();
    ApplyDisplayNameOverrides();   // ★ 新增
}

private void ApplyDisplayNameOverrides()
{
    foreach (var (name, model) in _modelRegistry)
    {
        var urn = ModelUrn.ForLocal(name);
        var custom = _displayNames.Resolve(urn, model.DisplayName);
        if (!string.IsNullOrEmpty(custom)) model.DisplayName = custom;
    }
}
```

**为何在 registry 填充后应用，而非改 `ScanModelDirectory`**：
扫描逻辑有多个分支（预注册/未注册/loose gguf），逐一修改易遗漏；
统一后置应用是**单一职责**的应用点，且对在线模型（不在 registry 扫描范围）
同样生效（见 §5.5）。

### 5.5 在线模型（服务商）名称管理

在线模型的 `RegisteredModelAsset.DisplayName` 已持久化于配置。两种做法：

| 做法 | 说明 | 评价 |
| :--- | :--- | :--- |
| ① 直接改 `RegisteredModelAsset.DisplayName` | 复用现有字段，加个 API | ✅ 最简单，但语义散（本地/在线两套存储） |
| ② 统一收编到 `ModelDisplayNames` | 在线也走注册表，`RegisteredModelAsset.DisplayName` 作为初始值 | ✅ **推荐**：单一权威，`/Models/catalog` 等所有出口一致 |

采用 ②。注册时以 `RegisteredModelAsset.DisplayName` 作为 fallback 初值，
用户一旦覆盖即以注册表为准。

### 5.6 API 设计（RESTful，遵循现有风格）

```csharp
// ModelsController.cs 新增

/// 设置模型展示名（本地或在线）
[HttpPut("{modelName}/display-name")]
public ActionResult SetDisplayName(string modelName, [FromBody] SetDisplayNameRequest req)

/// 清除展示名覆盖，回退系统推导名
[HttpDelete("{modelName}/display-name")]
public ActionResult ResetDisplayName(string modelName)

/// 批量查询当前所有覆盖（供管理页展示「已自定义」标记）
[HttpGet("display-names")]
public ActionResult GetDisplayNames()
```

请求体：

```csharp
public class SetDisplayNameRequest
{
    public string DisplayName { get; set; } = string.Empty;
    // 在线模型需指定 providerId（本地为空）
    public string? ProviderId { get; set; }
}
```

前端 API（[app/api/index.js](file:///E:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG/Business/ReferenceRAG.Business/wwwroot/app/api/index.js) 的 `modelsApi`）：

```js
setDisplayName: (modelName, displayName, providerId = null) =>
    api.put(`/Models/${encodeURIComponent(modelName)}/display-name`, { displayName, providerId }),
resetDisplayName: (modelName) =>
    api.delete(`/Models/${encodeURIComponent(modelName)}/display-name`),
```

### 5.7 前端 UI 设计

在 `Models.js` 的三张表（嵌入/重排/聊天）的「模型名称」列：

```
┌──────────────────────────────┬────────┐
│ BGE M3                    ✏️ │ 当前   │   ← 展示名 + 编辑图标
│ bge-m3                        │        │   ← 身份名（次要色，小字）
└──────────────────────────────┴────────┘
```

- **编辑入口**：行内铅笔图标 → 模态框（复用现有 `n-modal` + G-M5 的 95vw 规范）
- **模态框内容**：当前名称输入框 + 「重置为默认」按钮 + 身份名只读展示
- **反馈**：成功后 `message.success` + 局部刷新（无需整表重载）
- **自定义标记**：已覆盖的名称显示 ✏️ 角标，悬停提示「自定义名称」

移动端适配（G-M5/G-M9 已建门禁）：
- 模态框 `style="max-width: 95vw"`
- 触控目标 ≥44px
- 输入框 `label-placement="top"`

### 5.8 与上一轮漂移修复的关系

| 层 | 机制 | 作用 |
| :--- | :--- | :--- |
| **正本清源（本功能）** | 展示名显式可管理，`EmbeddingSpec.ModelName` 恒为规范 ID | 新数据不再混淆 |
| **兜底防御（已实现）** | `ModelIdentity` 规范化比较 | 存量数据免疫 |

二者互补，**不互相依赖**。即使不部署本功能，漂移误报也已修复；
部署本功能后，命名体验与一致性进一步提升。

---

## 6. 设计模式落地细节

### 6.1 策略模式：名称解析优先级

```
Resolve(urn, fallback):
    1. 用户覆盖（ModelDisplayNames[urn]）     ← 最高优先
    2. 系统推导（DisplayName/Name/目录名）     ← fallback
```

未来可插拔扩展（如「按量化档位自动标注」）只需在策略链中插入一环，
调用方无感。避免了散落的 `a ?? b ?? c` 三元链。

### 6.2 适配器模式：本地/在线统一

`ModelUrn` 已是现成的适配器（`ForLocal` / `ForProvider` / `Parse` 兼容三种历史格式）。
命名 API 直接以 `ModelUrn` 为参数，天然统一：

```csharp
_displayNames.TrySet(ModelUrn.ForLocal("bge-m3"), "主力向量模型", out var err);
_displayNames.TrySet(ModelUrn.ForProvider("prov_sf", "BAAI/bge-m3"), "硅基流动 BGE", out var err);
```

### 6.3 为何不需要状态机

名称管理是**无状态**的 CRUD（设置/清除/查询），不存在生命周期跃迁。
强行引入状态机属过度设计。系统的 `ModelLifecycleStateMachine`
（下载中/就绪/损坏等）继续服务于模型生命周期，与命名正交。

> 对比：上一轮的 `ReindexDrawerMachine` 是显式 FSM，因其确有
> idle→enqueuing→active→terminal 的复杂跃迁与非法转移防护需求。

---

## 7. 同类门禁与防回归测试

新增 `ModelDisplayNameGateTests`（建议用例）：

| 用例 | 断言 |
| :--- | :--- |
| `SetDisplayName_PersistsAcrossReload` | 设置后重建 `ModelManager`，名称仍生效 |
| `Resolve_PrefersUserOverride_OverScannedName` | 本地目录 `bge-m3-Q4_0` 设名「主力模型」后，`RefreshLocalModels` 不覆写 |
| `Resolve_FallsBackToSystemDerived_WhenNoOverride` | 无覆盖时返回 `DisplayName`/`Name` |
| `ResetDisplayName_RestoresSystemDerived` | 清除后回退 |
| `SetDisplayName_RejectsEmptyOrTooLongOrControlChars` | 空/超 64 字符/含 `\r\n` 均拒绝 |
| `PruneOrphans_RemovesEntriesForDeletedModels` | 模型删除后条目清理 |
| `OnlineModelDisplayName_ViaRegistry` | 在线模型同样可设置/清除 |
| `SpecModelName_UnaffectedByDisplayName` | **回归**：设展示名不改变 `EmbeddingSpec.ModelName`（身份隔离） |
| `DriftDetection_UnaffectedByDisplayName` | **回归**：改展示名不触发/不消除漂移判定 |

最后两条是关键**回归防线**：确保本功能永不破坏「身份与展示分离」的架构原则。

---

## 8. 实施计划

| 阶段 | 内容 | 预估 |
| :--- | :--- | :--- |
| P1 | `ModelDisplayNameRegistry` + 配置 schema + `ModelManager` 应用钩子 | 0.5 天 |
| P2 | API（PUT/DELETE/GET）+ 校验 + 孤儿清理 | 0.5 天 |
| P3 | 前端 UI（行内编辑 + 模态框 + 自定义标记）+ 移动端适配 | 0.5 天 |
| P4 | 门禁测试 + 全量回归 + 文档 | 0.5 天 |

**合计约 2 天。**

---

## 9. 风险与回滚

| 风险 | 等级 | 缓解 |
| :--- | :--- | :--- |
| 用户误将展示名当身份用于 API | 中 | UI 始终并列展示身份名；API 仅接受 `Name`/`ModelUrn`；文档明示 |
| 配置节损坏导致启动失败 | 低 | 反序列化容错（`Dictionary` 空值跳过）；`PruneOrphans` 自愈 |
| 与 `RegisteredModelAsset.DisplayName` 双写不一致 | 中 | 注册表为准；`RegisteredModelAsset.DisplayName` 降级为初始值 |
| 改名后用户期望重索引 | 低 | UI 明示「名称仅影响显示，不影响检索」 |

**回滚**：纯加法改动（新配置节缺省为空字典，行为与现状完全一致），按文件 revert 即可。

---

## 10. 实施状态与验收记录

方案 P1-P4 已全部落地（无历史数据兼容负担，纯加法改动）。

### 10.1 交付清单

| 阶段 | 内容 | 关键文件 |
| :--- | :--- | :--- |
| P1 | `ModelDisplayNameRegistry`（策略式名称解析：用户覆盖 → 系统推导 fallback）+ 配置节 `ModelDisplayNames` | `Features/ModelManagement/Services/ModelDisplayNameRegistry.cs`、`Models/ObsidianRagConfig.cs` |
| P1 | `ModelManager` 应用钩子（`InitializeRegistry`/`RefreshLocalModels` 后 `ApplyDisplayNameOverrides`）+ 在线模型收编（`ModelAssetResolver` 5 处）+ 删除模型时清理覆盖 | `Features/ModelManagement/Services/ModelManager.cs`、`ModelAssetResolver.cs` |
| P2 | 3 个 API 端点（PUT/DELETE/GET display-name(s)）+ 校验（非空/≤64/无控制字符/URN 有效） | `Host/ReferenceRAG.ApiHost/Controllers/ModelsController.cs` |
| P3 | 前端行内重命名 UI（本地三表 + 在线模型标签）+「自定义」标记 + 模态框（展示 URN 与语义提示） | `wwwroot/app/views/Models.js`、`wwwroot/app/api/index.js` |
| P4 | 门禁测试 + 全量回归 + 浏览器验收 | `tests/ReferenceRAG.Tests/ModelDisplayNameGateTests.cs` |

**关键设计修正**：`ApplyDisplayNameOverrides` 的 fallback 必须取自权威源 `PredefinedModels`，而非 `model.DisplayName`——后者可能已被上一次覆盖污染，直接用作 fallback 会导致 Reset 后无法回退到系统推导名（自我延续缺陷）。该缺陷在门禁测试 `Reset_RestoresPredefinedName` 中暴露并修复。

### 10.2 门禁与回归结果（实测）

| 套件 | 结果 |
| :--- | :--- |
| `ModelDisplayNameGateTests`（5 层 19 例：注册表语义/校验/孤儿清理/ModelManager 集成/身份隔离回归） | 19/19 通过 |
| 核心套件 `ReferenceRAG.Tests` | 784 通过 / 47 跳过 / 0 失败 |
| `ReferenceRAG.Host.Tests` | 457/457 通过 |
| 前端 `npm test` | 113/113 通过 |
| `node check.mjs`（59 模块 + 14 模板绑定） | 通过 |
| `check-mobile`（G-M5） | 通过 |

### 10.3 浏览器验收（隔离 WebHost + Playwright，15 项全过）

脚本 `bin/acceptance/.run/e2e-rename.mjs`（真实 Edge 无头，独立数据目录）：

- API：`GET display-names` 200;`PUT` 成功；空名/65 字符/控制字符均 400;覆盖持久化进快照;`DELETE` 重置 `removed=true` 且快照清空。
- UI：列表行显示自定义名 +「自定义」标记；重命名模态框展示 `local:bge-m3` 身份标识与「名称仅影响界面显示…」语义提示；重置后 UI 恢复预定义名 `BGE M3`;控制台零非预期错误（3 个 400 为校验阶段故意触发，已断言计数恰为 3）。

**身份隔离回归**（本功能的核心不变量）：`DisplayName_NeverAltersEmbeddingSpecModelName` 与 `DisplayName_DoesNotAffectModelIdentityComparison` 两例锁定——展示名覆盖绝不写入 `EmbeddingSpec.ModelName`，绝不参与漂移判定。

### 10.4 待办

- [x] 评审本方案
- [x] 实施 P1-P4
- [x] 门禁 + 全量回归 + 浏览器验收
- [ ] 部署后在生产验证：本地模型自定义名称 + 重启保持 + 漂移判定不受影响

---

## 参考来源

- [vLLM: Server Arguments — `--served-model-name`](https://docs.vllm.ai/en/stable/cli/serve/)
- [vLLM Forums: "served-model-name" and "model"](https://discuss.vllm.ai/t/served-model-name-and-model/2277)
- [vLLM PR #17944: remove --model from vllm serve usage](https://github.com/vllm-project/vllm/pull/17944)
- [MLflow: ML Model Registry](https://mlflow.org/docs/latest/ml/model-registry/)
- [MLflow: Model Registry Workflows](https://mlflow.org/docs/latest/ml/model-registry/workflow/)
- [Ollama issue #5914: Alias names for models](https://github.com/ollama/ollama/issues/5914)
- [Ollama: Modelfile Reference](https://docs.ollama.com/modelfile)
- [本项目：模型身份漂移误报调研与修复](model-identity-drift-misreport-investigation-and-solution.md)
