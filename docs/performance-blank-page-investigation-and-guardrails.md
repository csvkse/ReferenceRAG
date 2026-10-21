# 性能测试页面空白异常深度调研、根本原因剖析与多重质量门禁体系设计

> **版本**：v1.0.21  
> **分类**：前端运行时异常 / 跨作用域符号绑定 / 质量门禁体系  
> **涉及模块**：`Business/ReferenceRAG.Business/wwwroot/app/views/Performance.js`、`app.js`、`router/index.js`、`check.mjs`、`FrontendViewIntegrityTests.cs`

---

## 1. 问题现场与现象复现

### 1.1 异常现象还原
用户在访问系统“系统管理 -> 性能测试”页面（URL: `/#/performance`）时，界面出现大面积空白异常（如下图所示）：

- **左侧导航栏与顶部面包屑正常**：左侧 Sider 正常高亮选中“性能测试”，顶部 Header 正常展示 `ReferenceRAG / 性能测试` 及连接状态 `connected`；
- **一级分类 Tab 正常渲染**：内容区域顶部正常渲染出 `嵌入模型 (Embedding)`、`重排模型 (Rerank)`、`聊天模型 (Chat)` 三个 Segment 分类标签；
- **核心工作区完全空白**：分类标签下方原本应呈现的“模型运行状态与硬件加速卡片”、“评测参数矩阵面板”、“模型探针与性能测试表格”完全没有渲染，整片区域呈深灰色/纯黑背景，无任何内容，且界面无任何报错弹窗或文字提示。

### 1.2 浏览器控制台错误定位
通过在无头沙箱与浏览器中执行该页面脚本，控制台抛出如下致命异常：

```text
Uncaught ReferenceError: chatEvalCases is not defined
    at setup (Performance.js:2906:40)
    at callWithErrorHandling (vue.js:1:11520)
    at setupStatefulComponent (vue.js:1:12480)
    at mountComponent (vue.js:1:13090)
    at processComponent (vue.js:1:12700)
    at patch (vue.js:1:10520)
    at render (vue.js:1:10100)
```

---

## 2. 根本原因剖析 (Root Cause Analysis)

### 2.1 CRLF 换行符与大文件定位偏差
在前序版本发布（引入大模型全维工具调用评测矩阵）时，`Performance.js` 是一个体积超过 3500 行的超大单文件组件。
- 自动化代码修改工具在执行正则匹配与切片替换时，因跨平台换行符（Windows CRLF `\r\n` 与 Unix LF `\n`）的字节差异，导致预期的 `setup()` 内部核心变量声明块（包含 `chatEvalCases`、`chatEvalRunning`、`chatEvalProgress`、`chatEvalStats` 等 11 个响应式变量及评测驱动函数）未能匹配并插入到 `setup()` 函数体内；
- 但与此同时，位于文件末尾的模板字符串 `component.template` 以及返回给模板绑定的导出对象 `const __returned__ = { ... }` 替换成功，其中包含了 `{ chatEvalCases, chatEvalRunning, ... }`。

### 2.2 为什么普通静态语法检查（如 `node --check`）未能拦截？
在构建流水线中，通常使用 `node --check <file>` 进行基础语法有效性检查。然而：
1. **ES6 对象字面量简写规范（Object Literal Shorthand）**：
   在 ES6 中，`{ chatEvalCases }` 是 `{ chatEvalCases: chatEvalCases }` 的简写语法。
2. **语法有效性（Syntactic Validity） vs 词法作用域解析（Lexical Scope Binding）**：
   从 ECMAScript 语法（Grammar）角度，`const __returned__ = { chatEvalCases };` 语法结构完全合法。语法解析器（Syntax Parser）并不在语法解析阶段构建或校验词法作用域的闭包绑定，因为它可能是一个隐式全局变量或在稍后阶段求值。
3. **运行时求值爆炸**：
   只有当 JavaScript 引擎（V8、SpiderMonkey、JSCore）实际进入 `setup()` 执行上下文并求值到该赋值表达式时，引擎查询作用域链未发现 `chatEvalCases` 的声明，才会抛出 `Uncaught ReferenceError`。

### 2.3 为什么呈现为“顶部 Tab 渲染但下方空白”？
在 Vue 3 与 Naive UI 的组合中：
1. `Performance.js` 根模板由 `<n-space>` 包裹 `<n-tabs v-model:value="activeModelTab" type="segment">`；
2. Naive UI 在挂载 `<n-tabs>` 时，首先会解析并渲染 Segment Rail（标签头部的按钮导轨），这一过程在部分情况下优先由静态标签 slot 插槽驱动；
3. 一旦组件的 `setup()` 执行抛出异常，Vue 实例进入异常中断状态，停止了子树（Pane Body 内容卡片、表格、图表）的后续 VNode 挂载与 Diff 更新；
4. 另外，原本在 `app.js` 中设置的 `app.config.errorHandler` 仅向 `document.body` append 了一个未加 CSS 绝对定位与 z-index 的 `div#runtime-error`，在 Naive UI 100vh 全屏暗色布局下被遮挡在最底层视窗外，导致用户体感“界面完全卡死无响应”。

---

## 3. 四重同类长效门禁与防回归体系

为从根源上杜绝此类因“变量未声明即暴露”、“跨作用域遗漏”、“报错静默吞没”导致的白屏事故，本项目构建了四重长效防护网：

```mermaid
flowchart TD
    A[代码变更与提交] --> B[门禁一: AST 静态符号与作用域绑定分析 (check.mjs)]
    B -->|失败: 报出未声明标识符| Block1[拦截提交与构建]
    B -->|通过| C[门禁二: 无头 ESM 沙箱 Setup 执行测试]
    C -->|失败: 抛出 ReferenceError/TypeError| Block2[拦截提交与构建]
    C -->|通过| D[门禁三: .NET CI / dotnet test 测试联动 (FrontendViewIntegrityTests.cs)]
    D -->|失败: 单元测试红灯| Block3[CI 流水线阻断]
    D -->|通过| E[门禁四: 前端全局路由与错误边界降级兜底 (app.js / router)]
    E --> F[安全发布上屏]
```

### 门禁一：静态作用域与 AST 符号绑定分析（`check.mjs`）
- **核心逻辑**：自动解析所有 `wwwroot/app/views/*.js` 与 `layouts/*.js`；
- **双向校验**：
  1. 提取 `component.template` 中所有表达式标识符，比对 `__returned__`，拦截未导出的模板标识符；
  2. 提取 `const __returned__ = { ... }` 暴露的所有属性名，反向比对代码中的 `import`、顶级常量、`setup(props, { expose })` 形参以及 setup 内部声明语句（`const/let/var/function/class`）；
- **效果**：一旦发现任何暴露变量在作用域内无声明，立即输出具体的变量名并以非 0 退出码终止。

### 门禁二：无头 ESM 沙箱 Setup 执行测试（`frontend-logic.test.mjs`）
- 模拟浏览器核心对象（`window`, `document`, `localStorage`, `Image` 等）；
- 动态加载真实视图模块并显式调用 `setup({}, { expose: () => {} })`；
- 真实触发 JavaScript 引擎的词法求值，确保在模块初始化与 setup 周期内零 `ReferenceError`、零 `TypeError`。

### 门禁三：.NET CI / `dotnet test` 全自动化门禁联动（`FrontendViewIntegrityTests.cs`）
将前端视图完整性校验作为一等公民纳入后端的 xUnit 测试套件：
- 任何开发者在本地执行 `dotnet test` 或在 GitHub Actions / GitLab CI 中运行构建时，自动执行前端作用域分析器；
- 严禁任何 C# 开发者或 AI 工具在提交代码时破坏前端符号闭包；
- 保持跨平台绝对可靠（不依赖复杂的外部前端打包工具链，原生正则与 Node 引擎双重兜底）。

### 门禁四：运行时全局错误边界与用户可见友好降级机制
- **路由异常拦截 (`router.onError`)**：捕获异步组件 `import()` 失败或路由解析异常；
- **应用全局异常挂载 (`app.config.errorHandler`)**：
  - 增强为高优先级（`z-index: 99999`）、悬浮毛玻璃提示卡片；
  - 明确输出异常模块与错误栈摘要，并附带「重新加载」与「回到主页」按钮；
  - 彻底终结静默空白页。

---

## 4. 修复与验证结果

1. **`Performance.js` 修复**：
   - 将缺失的 `chatEvalCases`（7 维 12 项评测矩阵）、`chatEvalRunning`、`chatEvalProgress`、`chatEvalStats`、`chatEvalColumns`、`chatEvalDimensionColumns` 以及 5 个评测执行与导出方法完整恢复声明；
   - 验证所有 205 个暴露符号在词法作用域内 100% 存在声明。
2. **质量门禁全绿通过**：
   - `node check.mjs`：32 个 JS 模块、16 个 Vue 模板、205 个导出符号全部校验通过，0 错误；
   - `npm test`：23 项前端测试用例 100% 通过；
   - `dotnet test`：147 项后端与集成测试 100% 通过。
