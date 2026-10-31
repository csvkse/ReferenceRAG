# Naive UI 组件 `size="tiny"` 导致 `reading 'split'` / `'horizontal'` 运行时异常调研、根因分析、纵深防御与双轨门禁架构

> **文档版本**：v1.0.0  
> **分类**：前端运行时异常 / UI 组件库属性契约 / 运行时弹性容错 / 双轨静态质量门禁  
> **涉及模块**：
> - `Business/ReferenceRAG.Business/wwwroot/app/views/ApiKeys.js`
> - `Business/ReferenceRAG.Business/wwwroot/app/App.js`
> - `Business/ReferenceRAG.Business/wwwroot/check.mjs`
> - `Business/ReferenceRAG.Business/wwwroot/tests/frontend-logic.test.mjs`
> - `tests/ReferenceRAG.Host.Tests/FrontendViewIntegrityTests.cs`

---

## 1. 故障现场与问题复盘

### 1.1 异常现象与报错信息
在系统的“API Key 授权管理”页面中，用户在触发“新建 API Key”或查看数据表格时，界面右上角弹出红色错误提示框（通过全局 `app.config.errorHandler` 拦截渲染错误并弹出）：

```text
⚠️ 组件渲染或操作异常
Cannot read properties of undefined (reading 'split')
```
或者在特定渲染执行路径下表现为：
```text
⚠️ 组件渲染或操作异常
Cannot read properties of undefined (reading 'horizontal')
```

同时，新建弹窗中的“业务与 MCP 权限”卡片头部右侧（原本应展示 `REST API`、`MCP` 通道状态标签及 `已选工具/总工具数` 徽章的位置）呈现空白，局部组件树渲染中断。

### 1.2 故障影响面
- **受影响页面**：`ApiKeys.js`（API Key 管理视图，包含数据表格通道标签、业务模块标签、新建/编辑弹窗二级工具卡片头部）；
- **故障后果**：由于 Vue 组件渲染生命周期中发生未捕获的 JavaScript `TypeError`，导致目标组件及其子 VNode 渲染中断，部分元数据与操作徽标无法展示，且右上角持续显示警示 Toast，严重损害用户体验。

---

## 2. 根本原因深度剖析 (Root Cause Analysis)

### 2.1 触发代码定位
在 `Business/ReferenceRAG.Business/wwwroot/app/views/ApiKeys.js` 中存在 3 处非法属性调用：

1. **第 416 行**（数据表格 `通道权限` 列自定义渲染）：
   ```javascript
   render: (row) => h(NSpace, { size: 'tiny' }, {
       default: () => [
           h(NTag, { type: row.allowApi ? 'success' : 'default', size: 'small' }, { default: () => 'API' }),
           h(NTag, { type: row.allowMcp ? 'info' : 'default', size: 'small' }, { default: () => 'MCP' })
       ]
   })
   ```
2. **第 432 行**（数据表格 `业务功能模块` 列自定义渲染）：
   ```javascript
   return h(NSpace, { size: 'tiny' }, {
       default: () => scopes.map(s => h(NTag, { size: 'small', secondary: true }, { default: () => s }))
   });
   ```
3. **第 644 行**（弹窗内模块卡片头部）：
   ```html
   <!-- 通道标记与工具计数 -->
   <n-space size="tiny" align="center" style="flex-shrink: 0;" @click.stop>
     <n-tag v-if="cat.supportsApi" size="tiny" type="success" secondary>REST API</n-tag>
     <n-tag v-if="cat.supportsMcp" size="tiny" type="info" secondary>MCP</n-tag>
     <n-tag v-if="cat.tools && cat.tools.length > 0" size="tiny" :bordered="false">
       {{ getCategorySelectedToolCount(cat) }} / {{ cat.tools.length }} 工具
     </n-tag>
   </n-space>
   ```

### 2.2 Naive UI 内部运行机制与崩溃链路
为什么 `size="tiny"` 会导致 `reading 'split'` 和 `reading 'horizontal'`？

在 Naive UI 的组件设计中：
1. **组件尺寸规范不一致**：
   - 很多基础组件（如 `NButton`、`NTag`、`NInput`、`NSelect`）均合法支持 `'tiny' | 'small' | 'medium' | 'large'`；
   - 但是布局组件 **`NSpace` (`<n-space>`)** 的尺寸预设仅支持：
     $$\text{size} \in \{ \text{'small'}, \text{'medium'}, \text{'large'}, \text{number}, [\text{number}, \text{number}] \}$$
     **Naive UI 官方未在 Space 中定义 `'tiny'` 尺寸档位！**

2. **内部源码实现原理（查阅 `node_modules/naive-ui/index.js`）**：
   `Space` 组件在计算元素间距（margin / gap）时，代码逻辑如下：
   ```javascript
   // 1. 获取 size 参数，默认为 'medium'
   const a = computed(() => props.size || themeProps?.Space?.size || "medium");

   // 2. 计算 margin 响应式对象
   margin: computed(() => {
     const e = a.value;
     if (Array.isArray(e)) return { horizontal: e[0], vertical: e[1] };
     if ("number" == typeof e) return { horizontal: e, vertical: e };
     
     // 若为字符串，通过 createKey 拼接出主题对象属性名：
     // createKey("gap", "small")  => "gapSmall"
     // createKey("gap", "tiny")   => "gapTiny"
     const { self: { [createKey("gap", e)]: t } } = themeRef.value;
     
     // 3. 解析间距字符串，例如 "8px 12px"
     const { row: n, col: r } = getGap(t);
     return { horizontal: formatLength(r), vertical: formatLength(n) };
   })
   ```

3. **两大致命崩溃点**：
   - **崩溃点 1：`reading 'split'`**
     `Space` 组件内置的默认主题对象 `SM` 仅定义了：
     ```javascript
     const SM = {
       gapSmall: "4px 8px",
       gapMedium: "8px 12px",
       gapLarge: "12px 16px"
     };
     ```
     当传入 `size="tiny"` 时，`createKey("gap", "tiny")` 得到 `"gapTiny"`。`SM["gapTiny"]` 为 `undefined`，因此 `t = undefined`。
     此时紧接着执行 `getGap(t)`：
     ```javascript
     function getGap(e) {
       const [n, r] = e.split(" "); // e 为 undefined!
       return { row: n, col: r || n };
     }
     ```
     运行时立即抛出：
     $$\text{TypeError: Cannot read properties of undefined (reading 'split')}$$
   
   - **崩溃点 2：`reading 'horizontal'`**
     在 `Space` 的 `render()` 函数中：
     ```javascript
     render() {
       const { margin: l } = this;
       // 当 margin 的 computed 计算抛错或返回 undefined 时：
       const g = `${l.horizontal}px`; // l 为 undefined!
       ...
     }
     ```
     当尝试访问 `l.horizontal` 时，运行时抛出：
     $$\text{TypeError: Cannot read properties of undefined (reading 'horizontal')}$$

4. **问题本质**：
   开发者习惯了 `NTag` / `NButton` 的 `size="tiny"`，在需要紧凑布局时顺手为 `NSpace` 传递了 `size="tiny"`。而 Naive UI 的 `Space` 组件内部缺乏对非法 `size` 的防御性校验或回退逻辑，直接对 `theme[createKey("gap", size)]` 执行 `.split(' ')`，导致整段渲染逻辑崩溃。

---

## 3. 现有库调研、业界主流方案对比与选型

针对此类“组件库非契约属性导致第三方组件库底层代码抛出运行时未捕获异常”的问题，调研业界主流解决方案如下：

### 3.1 业界主流方案对比分析

```
                     ┌─────────────────────────────────────────────────────────┐
                     │          业界组件属性契约校验与防崩溃方案对比           │
                     └─────────────────────────────────────────────────────────┘
                                                  │
            ┌─────────────────────┬───────────────┴───────────────┬─────────────────────┐
            ▼                     ▼                               ▼                     ▼
   【方案 1: 静态类型系统】  【方案 2: Linter 规则】    【方案 3: 全局运行时兜底】 【方案 4: 轻量自研静态门禁】
     TypeScript + Volar      eslint-plugin-vue          NConfigProvider 注入     check.mjs + C# 测试
  ───────────────────────  ───────────────────────    ───────────────────────── ─────────────────────────
   基于 .d.ts 提示属性类型    解析 AST 检查合法属性       向组件注入缺失的主题键值  离线扫描源码 + 自动化测试
   需全量构建/TS 工具链      需庞大 npm 依赖与解析器     零侵入提供全局弹性容错    零依赖、毫秒级执行、全栈覆盖
```

| 方案 | 代表工具 | 运行阶段 | 优势 | 在本项目架构下的考量 |
| :--- | :--- | :--- | :--- | :--- |
| **方案 1：TypeScript + Volar / vue-tsc** | `@vue/language-tools` | 构建/编辑期 | 强类型保证，当 `size="tiny"` 不在 `SpaceProps['size']` 联合类型中时，编译报错。 | 本项目采用轻量级原生 ESM 架构（无 Webpack/Vite 重打包流水线），引入完整的 TS/Volar 需要改造全栈编译工具链，成本过高。 |
| **方案 2：ESLint AST 规则** | `eslint-plugin-vue` | 静态检查 | 规范标准，生态完善。 | 需要引入 Node.js 完整 AST 工具链与 ESLint 环境，且在 C# .NET CI 环境中无法脱离 npm 原生执行。 |
| **方案 3：UI 库全局主题弹性兜底 (Defense in Depth)** | Naive UI `NConfigProvider` | 运行时 | **全局兜底**：通过 `theme-overrides` 补齐 `gapTiny`，即使未来任何业务视图误传 `tiny`，也能优雅回退，绝不崩溃！ | 无法阻止代码中的不规范用法，单独使用不够严密，需配合静态检查。 |
| **方案 4：增强型原生 AST 门禁 (双轨防回归)** | `check.mjs` + `FrontendViewIntegrityTests.cs` | 提交/CI 门禁 | **与项目架构深度契合**：极速（<10ms）、零额外依赖、同时覆盖 Node.js 本地测试与 .NET CI 流水线。 | 需维护已知组件的属性契约规则集。 |

### 3.2 现有系统基础设施复用确认
经查证，当前代码仓已具备以下高质量基础设施，可直接复用并扩展：
1. **`Business/ReferenceRAG.Business/wwwroot/app/App.js`**：
   根组件已挂载 `<n-config-provider>`，支持通过 `:theme-overrides` 实现全局主题变量的无缝补丁注入；
2. **`Business/ReferenceRAG.Business/wwwroot/check.mjs`**：
   已作为项目核心静态质量门禁（集成了语法检查、ESM 具名导入导出校验、Vue AST 模板绑定校验、`__returned__` 词法作用域校验）；
3. **`Business/ReferenceRAG.Business/wwwroot/tests/frontend-logic.test.mjs`**：
   Node.js 原生测试套件，可直接添加属性契约规则单元测试；
4. **`tests/ReferenceRAG.Host.Tests/FrontendViewIntegrityTests.cs`**：
   .NET 8/10 xUnit 门禁套件，负责确保脱离 Node 环境下的 C# CI 依然能守卫前端质量。

---

## 4. 具体解决方案与工程实现

本项目采取**“三位一体”纵深防御体系**：
1. **业务代码修复**：将 `ApiKeys.js` 中所有非法 `size="tiny"` 修正为官方标准 `size="small"`；
2. **运行时弹性兜底（Defense-in-Depth）**：在 `App.js` 中配置 `themeOverrides.Space.gapTiny = '4px 8px'`，实现运行时免死金牌；
3. **全栈双轨静态质量门禁**：
   - 轨道一：在 `check.mjs` 中增加 `checkComponentPropRules` 门禁规则；
   - 轨道二：在 `FrontendViewIntegrityTests.cs` 中增加 C# 自动化回归测试。

```
                     ┌───────────────────────────────────────────────┐
                     │          三位一体纵深防御与长效防回归架构       │
                     └───────────────────────────────────────────────┘
                                             │
             ┌───────────────────────────────┼───────────────────────────────┐
             ▼                               ▼                               ▼
    【第一道：精准业务修复】         【第二道：运行时弹性兜底】       【第三道：全栈双轨质量门禁】
       ApiKeys.js 修复                App.js themeOverrides            check.mjs + C# xUnit
  ──────────────────────────     ──────────────────────────     ──────────────────────────
  • h(NSpace, {size:'small'})     • Space: {gapTiny:'4px 8px'}   • checkComponentPropRules
  • <n-space size="small">       • 彻底消灭 undefined.split     • CI/Git Hook 永久自动拦截
```

### 4.1 业务代码精准修复
修改 `Business/ReferenceRAG.Business/wwwroot/app/views/ApiKeys.js`：
- 第 416 行：`h(NSpace, { size: 'tiny' })` $\rightarrow$ `h(NSpace, { size: 'small' })`
- 第 432 行：`h(NSpace, { size: 'tiny' })` $\rightarrow$ `h(NSpace, { size: 'small' })`
- 第 644 行：`<n-space size="tiny"` $\rightarrow$ `<n-space size="small"`

### 4.2 全局运行时弹性兜底 (`App.js`)
在 `Business/ReferenceRAG.Business/wwwroot/app/App.js` 中通过 `themeOverrides` 向 Naive UI 注入 `gapTiny` 间距定义：

```javascript
// 全局主题兜底：扩展 Naive UI NSpace 未定义的 gapTiny，防止业务代码误传 size="tiny" 时内部 split(' ') 抛出未捕获运行时异常
const themeOverrides = {
    Space: {
        gapTiny: '4px 8px'
    }
};
```
在模板中绑定至全局 Provider：
```html
<n-config-provider :theme="currentTheme" :theme-overrides="themeOverrides" :locale="zhCN" :date-locale="dateZhCN">
  ...
</n-config-provider>
```
**防御效果**：即使未来有新成员或第三方插件在任意页面书写了 `<n-space size="tiny">`，Naive UI 的 `createKey("gap", "tiny")` 命中 `themeOverrides.Space.gapTiny`（`"4px 8px"`），`getGap` 将正常解析出 `{ row: '4px', col: '8px' }`，系统永不白屏或抛错。

### 4.3 静态质量门禁一：`check.mjs` 扩展
在 `check.mjs` 中新增并导出 `checkComponentPropRules(source, relPath)` 函数，并在 `visit()` 中自动遍历执行：

```javascript
export function checkComponentPropRules(source, relPath) {
  const issues = [];
  const validSpaceSizes = new Set(['small', 'medium', 'large']);

  // 1. Template validation: <n-space ... size="..." ...>
  for (const m of source.matchAll(/<n-space\b([^>]*)>/gi)) {
    const sizeMatch = m[1].match(/\bsize=["']([^"']+)["']/i);
    if (sizeMatch) {
      const val = sizeMatch[1].trim();
      if (!validSpaceSizes.has(val) && isNaN(Number(val))) {
        issues.push(`Invalid size="${val}" on <n-space> in ${relPath}: Naive UI Space only supports 'small'|'medium'|'large' or number/[number, number]. Passing '${val}' crashes at runtime (reading 'split'/'horizontal').`);
      }
    }
  }

  // 2. Render function validation: h(NSpace, { size: '...' })
  for (const m of source.matchAll(/h\s*\(\s*NSpace\s*,\s*\{([^}]*)\}/g)) {
    const sizeMatch = m[1].match(/\bsize\s*:\s*['"]([^'"]+)['"]/);
    if (sizeMatch) {
      const val = sizeMatch[1].trim();
      if (!validSpaceSizes.has(val) && isNaN(Number(val))) {
        issues.push(`Invalid size: '${val}' in h(NSpace, ...) in ${relPath}: Naive UI Space only supports 'small'|'medium'|'large' or number. Passing '${val}' crashes at runtime (reading 'split'/'horizontal').`);
      }
    }
  }

  return issues;
}
```

### 4.4 静态质量门禁二：Node 测试套件 (`frontend-logic.test.mjs`)
在 `frontend-logic.test.mjs` 中挂载专用回归测试用例：
```javascript
test('Component prop rules guardrail: checkComponentPropRules detects invalid space sizes preventing undefined split/horizontal crashes', async () => {
    const { checkComponentPropRules } = await import('../check.mjs');

    // 1. 合法参数放行
    assert.equal(checkComponentPropRules('<n-space size="small">', 'test.js').length, 0);
    assert.equal(checkComponentPropRules('<n-space size="medium">', 'test.js').length, 0);
    assert.equal(checkComponentPropRules('<n-space size="large">', 'test.js').length, 0);
    assert.equal(checkComponentPropRules('<n-space :size="16">', 'test.js').length, 0);
    assert.equal(checkComponentPropRules('h(NSpace, { size: "small" })', 'test.js').length, 0);
    assert.equal(checkComponentPropRules('h(NSpace, { size: 12 })', 'test.js').length, 0);

    // 2. 非法 'tiny' 阻断拦截
    const tagErrors = checkComponentPropRules('<n-space size="tiny" align="center">', 'ApiKeys.js');
    assert.equal(tagErrors.length, 1);
    assert.match(tagErrors[0], /Invalid size="tiny" on <n-space>/);

    const hErrors = checkComponentPropRules('h(NSpace, { size: "tiny" })', 'ApiKeys.js');
    assert.equal(hErrors.length, 1);
    assert.match(hErrors[0], /Invalid size: 'tiny' in h\(NSpace, ...\)/);

    // 3. 验证真实 ApiKeys.js 零违规
    const fs = await import('node:fs');
    const path = await import('node:path');
    const apiKeysSrc = fs.readFileSync(path.resolve('Business/ReferenceRAG.Business/wwwroot/app/views/ApiKeys.js'), 'utf8');
    assert.equal(checkComponentPropRules(apiKeysSrc, 'ApiKeys.js').length, 0);
});
```

### 4.5 静态质量门禁三：C# xUnit 测试套件 (`FrontendViewIntegrityTests.cs`)
在 .NET CI 层挂载跨平台自动化测试：
```csharp
[Fact]
public void AllFrontendViews_NaiveUiSpaceSize_MustBeValidEnumOrNumber()
{
    var root = FindRepoRoot();
    var files = GetFrontendViewFiles(root);
    Assert.NotEmpty(files);

    var errors = new List<string>();
    var validSizes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "small", "medium", "large" };

    var tagSpaceRegex = new Regex(@"<n-space\b([^>]*)>", RegexOptions.IgnoreCase);
    var tagSizeRegex = new Regex(@"\bsize=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
    var hSpaceRegex = new Regex(@"h\s*\(\s*NSpace\s*,\s*\{([^}]*)\}");
    var hSizeRegex = new Regex(@"\bsize\s*:\s*['""]([^'""]+)['""]");

    foreach (var file in files)
    {
        var relFile = Path.GetRelativePath(root, file).Replace('\\', '/');
        var content = File.ReadAllText(file);

        // 1. Template <n-space ... size="..." ...>
        foreach (Match tm in tagSpaceRegex.Matches(content))
        {
            var attrs = tm.Groups[1].Value;
            var sm = tagSizeRegex.Match(attrs);
            if (sm.Success)
            {
                var val = sm.Groups[1].Value.Trim();
                if (!validSizes.Contains(val) && !double.TryParse(val, out _))
                {
                    errors.Add($"[{relFile}] <n-space> 存在非法 size=\"{val}\"，Naive UI Space 仅支持 'small'|'medium'|'large' 或数字，传 tiny 会导致运行时异常 (reading 'split'/'horizontal')");
                }
            }
        }

        // 2. Render h(NSpace, { size: '...' })
        foreach (Match hm in hSpaceRegex.Matches(content))
        {
            var props = hm.Groups[1].Value;
            var sm = hSizeRegex.Match(props);
            if (sm.Success)
            {
                var val = sm.Groups[1].Value.Trim();
                if (!validSizes.Contains(val) && !double.TryParse(val, out _))
                {
                    errors.Add($"[{relFile}] h(NSpace, ...) 存在非法 size: '{val}'，Naive UI Space 仅支持 'small'|'medium'|'large' 或数字，传 tiny 会导致运行时异常 (reading 'split'/'horizontal')");
                }
            }
        }
    }

    Assert.True(errors.Count == 0,
        $"发现 {errors.Count} 处 Naive UI NSpace size 属性违规缺陷:\n" + string.Join("\n", errors));
}
```

---

## 5. 验证执行与门禁测试报告

### 5.1 门禁测试执行结果

1. **`node check.mjs` 静态质量门禁**：
   ```bash
   node Business/ReferenceRAG.Business/wwwroot/check.mjs
   ```
   > **输出**：`Validated 38 JS modules and 17 executable Vue templates (AST symbol bindings & setup exports verified); no build output generated.`  
   > **结论**：38 个模块与 17 个模板全部通过，零语法、符号或属性异常。

2. **Node 原生逻辑与属性规则测试**：
   ```bash
   node --test Business/ReferenceRAG.Business/wwwroot/tests/frontend-logic.test.mjs
   ```
   > **输出**：
   > ```text
   > ✔ Component prop rules guardrail: checkComponentPropRules detects invalid space sizes preventing undefined split/horizontal crashes (0.9079ms)
   > ℹ tests 29
   > ℹ suites 0
   > ℹ pass 29
   > ℹ fail 0
   > ℹ duration_ms 262.4272
   > ```
   > **结论**：29 项前端逻辑测试 100% 通过。

3. **.NET Host xUnit 全自动化集成门禁**：
   ```bash
   dotnet test tests/ReferenceRAG.Host.Tests/ReferenceRAG.Host.Tests.csproj --filter "FullyQualifiedName~FrontendViewIntegrityTests"
   ```
   > **输出**：
   > ```text
   > 已通过! - 失败: 0，通过: 5，已跳过: 0，总计: 5，持续时间: 3 s - ReferenceRAG.Host.Tests.dll (net10.0)
   > ```
   > **结论**：包含新增的 `AllFrontendViews_NaiveUiSpaceSize_MustBeValidEnumOrNumber` 在内全部 5 项门禁测试通过。

---

## 6. 总结与长效防回归机制

1. **根因闭环**：排查并解决了 Naive UI `NSpace` 因缺少 `gapTiny` 配置而在解析间距时发生 `undefined.split(' ')` 及 `margin.horizontal` 崩溃的核心根因；
2. **多层设防**：
   - 源码层：使用标准枚举属性；
   - 运行时层：`themeOverrides` 注入安全兜底，免疫未知扩展属性崩溃；
   - 编译检查层：`check.mjs` 离线校验；
   - CI/CD 自动化层：`node --test` + `dotnet test` 双轨拦截，任何不合规属性将直接在本地提交前或流水线上被阻断；
3. **架构资产**：本次新增的组件属性契约验证体系未来可扩展至其他 Naive UI 易踩坑组件（如 `NGrid`、`NInput` 等），持续提升 ReferenceRAG 原生轻量化前端架构的工业级健壮性。
