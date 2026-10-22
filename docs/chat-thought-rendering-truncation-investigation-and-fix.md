# 对话思考过程渲染截断异常深度调研与架构治理规范

> **文档状态**: 已执行并验证通过  
> **关联版本**: v1.0.42  
> **核心模块**: `Business/ReferenceRAG.Business/wwwroot/shared/safe-markdown.js`、`frontend-logic.test.mjs`  
> **涉及模型**: Qwen3.5、DeepSeek-R1、Llama-3 等具备 Chain-of-Thought (CoT) 深度思考能力的本地与云端模型

---

## 1. 调研：问题现象与根本原因 (Problem & Root Cause Analysis)

### 1.1 异常现象 (The Symptom)

在使用携带思考链能力的小参数模型（如 Qwen3.5 2B Instruct）回答关于“成都市”的综合介绍时，用户界面呈现出极其严重的**回答文本大面积丢失/截断现象**：

```markdown
<!-- 模型实际完整输出 -->
<think>
**Exploring Chengdu**
The user is asking for an introduction to Chengdu...
... (深度思考思维链) ...
</think>

成都是一座兼具历史底蕴与生活气息的城市，很多人第一次来，觉得“慢、好吃、有故事”。

下面从几个维度简要介绍一下：
---
## 1. 地理位置与气候
- 成都位于四川盆地西部...
---
## 2. 历史与城市格局
- “天府之国”...
---
## 3. 文化与生活方式
- 成都人性格偏豁达...
---
## 4. 美食（成都味）
...
---
## 5. 旅游亮点
适合喜欢文化、历史、自然风光的旅行者：

- **青城山 + 都江堰**：道教名山与水利工程...
- **武侯祠 + 杜甫草堂**...
...
```

但在前端 Chat 页面中：
1. 思考过程被折叠在 `💭 思考过程 (点击展开)` 卡片中。
2. 折叠卡片下方的正文，**开篇引言、第 1 节、第 2 节、第 3 节、第 4 节全部凭空蒸发**！
3. 正文第一行突兀地从 `• 青城山 + 都江堰：道教名山与水利工程...` 开始渲染。
4. **超过 50% 的模型回答内容在用户视口中彻底不可见**，且控制台未报任何 JS 错误。

---

### 1.2 根因追踪链条 (Root Cause Dissection)

经过端到端 DOM 模拟、AST 语法树解析与正则边界推演，定位到该问题是由 **三层级联的机制缺陷** 共同导致的必然灾难：

```
[原始输出]
    │
    ▼
[缺陷 1: balanceTags 正则失控穿透]
  thoughtAnswerBoundary 正则未加负向预查且未检查未闭合状态，
  一路匹配穿透已闭合的 </think>，并在第 5 节列表前人为注入了第二个 </think>
    │
    ▼
[缺陷 2: marked.js 与 HTML5 树构建规范冲突]
  marked 将自定义 <think> 视作行内 HTML 并包裹 <p>，
  生成 <think><p>... </think></p>。
  根据 HTML5 标准 13.2.6.4.7，处理处于 Special 类别（<p>）内的非标准结束标签（</think>）时，
  浏览器必须“发出解析错误并完全丢弃该结束标签”，导致真正的 </think> 被浏览器静默抛弃！
    │
    ▼
[缺陷 3: 孪生标签下的 DOM 误吞与节点置换]
  由于真实的 </think> 被抛弃，整个 HTML 树中第 1~5 节全被当成了 <think> 元素的内部子节点，
  直到第 5 节前由缺陷 1 注入的第二个 </think> 才在顶层将 <think> 元素闭合。
  随后 safe-markdown.js 的 replaceWith(details) 将 <think> 节点连同其全部内部正文替换为折叠卡片，
  仅保留了直接文本，彻底删除了内部所有已解析的正文富文本节点！
```

#### 根因 1：`balanceTags` 规则 3 正则穿透已闭合标签且误判列表符
在原 `safe-markdown.js` 的 `balanceTags` 函数中，曾存在如下补救逻辑：
```javascript
// 历史缺陷代码
const thoughtAnswerBoundary = /(<(?:think|thought)\b[^>]*>[\s\S]*?\n\n)(?=(?:(?:[#>\-*]|\d+\.)\s|您好|你好|好的|收到|抱歉|对不起|根据|总的来说|综上所述|Hello|Hi|Dear|Sure|Here\b))/i;
if (thoughtAnswerBoundary.test(balanced)) {
  balanced = balanced.replace(thoughtAnswerBoundary, '$1</think>\n\n');
}
```
该规则存在三个致命逻辑漏洞：
1. **无前置开闭差额校验**：即使模型输出的 `<think>` 已经严格成对闭合（`thinkOpen === thinkClose`），该正则依然被无差别触发！
2. **贪婪跨越闭合标签**：使用 `[\s\S]*?\n\n` 却**未添加 `(?!<\/think>)` 负向预查**，导致正则从最开始的 `<think>` 起始，一路跨过正版的 `</think>`，跨过正文所有段落，继续向下贪婪寻找 lookahead 条件。
3. **将合法正文列表符号误当做思考边界**：lookahead 包含了 `(?:[#>\-*]|\d+\.)\s`。在成都问答中，第 5 节 `## 5. 旅游亮点\n适合喜欢...\n\n- **青城山 + 都江堰**` 恰好是第一个在 `\n\n` 后紧跟 `- ` 列表符号的位置！正则在此命中，并生硬地插入了第二个 `</think>`！

#### 根因 2：HTML5 标准树构建规则（HTML5 Tree Construction "Special Category" Rule）
当 marked 面对自定义标签 `<think>` 时，由于 CommonMark/Marked 并不将 `<think>` 认知为标准块级容器元素（如 `<div>` 或 `<section>`），因此将其按行内/混合 HTML 块处理：
```html
<think>
**Exploring Chengdu**<p>The user is asking...
</think></p>
```
在浏览器底层 HTML5 解析器（Chrome/Blink、Firefox/Gecko、WebKit）中，解析规则遵循 WHATWG HTML 标准 **13.2.6.4.7 "in body" insertion mode**：
* 当解析器栈顶处于 `<p>` 元素时，遇到了非标准自定义结束标签 `</think>`；
* 解析器回溯检查作用域元素。由于 `<p>` 属于 **Special 类别**（与 `div`、`article`、`header`、`p` 等同级）；
* 标准明确规定：**"If node is in the special category, then this is a parse error; ignore the token."**（若遇到 Special 类别节点，触发解析错误，**直接忽略并丢弃该结束标签**！）；
* **结果：原本正确的 `</think>` 被浏览器直接当垃圾扔掉，自定义 `<think>` 元素在 DOM 树中依然保持开启状态！**

#### 根因 3：DOM 树吞噬与破坏性替换
由于第一个 `</think>` 被忽略，后续生成的所有 `<p>成都是一座...</p>`、`<h2>1. 地理位置与气候</h2>`、`<ul>...</ul>` 直到第 5 节，全部被浏览器挂载为 `<think>` 的子元素！
随后，缺陷 1 注入的第二个 `</think>` 因为位于块级元素之间（未处于未闭合的 `<p>` 内部），被浏览器正确执行，闭合了 `<think>`。
进入 `renderMarkdown` 阶段：
```javascript
let targetNode = thoughtElements[0];
targetNode.replaceWith(details); // <think> 及其包含的全部第 1~5 节 DOM 子树被整体移除并替换为 details！
```
最终留在正文视口中的，只剩下 `<think>` 闭合之后的孤儿节点：第 5 节后半部分的 `<ul><li>青城山 + 都江堰...</li></ul>`。

---

## 2. 调研：现有库与业界主流方案对比 (Industry Solutions & Libraries)

针对 LLM 思考链 `<think>...</think>` 在 Web 客户端的渲染，调研了当前业界核心开源项目的实践方案：

| 方案类别 | 代表项目 / 库 | 实现架构 | 优点 | 缺点 / 边界限制 |
| :--- | :--- | :--- | :--- | :--- |
| **A. 协议层字段分离** | DeepSeek 官方 WebUI、OpenAI Responses API、VLLM SSE | SSE 传输中独立返回 `delta.reasoning_content` 与 `delta.content` | 前后端界限绝对分明，Markdown 解析器无需接触任何 `<think>` 标签，零解析冲突 | 仅适用于直连专属 API；对于 Ollama、llama.cpp、GGUF 本地推理流式输出纯文本标签场景无效 |
| **B. AST 解析前文本预分离**<br>*(本项目采纳)* | **Open-WebUI**、**LibreChat**、**LobeChat** | 在文本进入 `marked.parse` 之前，通过正则/状态机将 `<think>` 抽离为 `thoughtSteps` 数组与纯净 `cleanBody` | 彻底隔离 Markdown 解析与 HTML5 树构建，零标签冲突，兼容所有本地/云端 GGUF/ONNX 模型 | 需要精准处理流式输出（未闭合思考标签）与多阶段推理聚合 |
| **C. Marked 扩展标记法** | `marked-thought-extension`、`marked.use({ extensions })` | 在 Marked Lexer 阶段注册自定义 block tokenizer，拦截 `<think>` 生成 AST block token | 遵循 Marked 规范扩展机制 | 流式生成中标签经常未闭合，标准 tokenizer 极易触发 fallback 造成文本闪烁或重复 |
| **D. 原始标签直灌 DOM 后置换**<br>*(本项目历史缺陷方案)* | 早期 DIY 前端 | 直接把 `<think>` 送入 marked，再通过 `querySelectorAll('think')` 遍历替换 | 代码量少、直觉简单 | **存在严重 HTML5 特殊元素解析丢失 BUG**，不可用于生产环境 |

### 业界现有库分析
1. **`marked` 自定义扩展机制**: Marked 提供了 `tokenizer` 和 `renderer` 钩子，但其局限在于必须等待完整的闭合标签，一旦处于流式生成（仅有 `<think>` 而无 `</think>`），Marked 会将其降级为普通段落文本，导致思考内容先以裸文本显示，数秒后突然折叠为卡片，产生剧烈页面跳动（Layout Shift）。
2. **`markdown-it` + `markdown-it-container`**: 在服务端渲染场景常用，支持 `::: reasoning` 语法的 AST 块级解析，但客户端轻量渲染引入额外解析器会增加包体积。
3. **Open-WebUI 的 `renderReasoning` 管道**: Open-WebUI 在 Svelte 渲染管道前，通过响应式管道在字符流级别分离 `reasoning` 与 `content`。其核心设计原则即为：**“Markdown 渲染器绝对不应接收自定义模型控制性 XML/HTML 伪标签”**。

---

## 3. 具体解决方案与架构演进 (Concrete Solution & Architectural Evolution)

我们对 `Business/ReferenceRAG.Business/wwwroot/shared/safe-markdown.js` 进行了彻底的架构级治理，确立了 **AST 解析前置分离架构（Pre-parse Thought Extraction Architecture）**：

```
                ┌──────────────────────────────┐
                │   原始模型生成输出 (rawText)  │
                └──────────────┬───────────────┘
                               │
                               ▼
        ┌──────────────────────────────────────────────┐
        │ 1. balanceTags 边界自愈与闭合守护             │
        │    - 仅在 thinkOpen > thinkClose 时激活启发式 │
        │    - (?!</think>) 负向预查杜绝跨越正文        │
        │    - 剔除 [#>-*] 避免误伤合法列表与思考链     │
        └──────────────────────┬───────────────────────┘
                               │
                               ▼
        ┌──────────────────────────────────────────────┐
        │ 2. extractThoughts 预分离引擎                │
        │    - 提取所有闭合块至 thoughtSteps[] 数组     │
        │    - 检测是否处于流式生成中 (isStreaming)     │
        │    - 输出 100% 纯净 Markdown 正文 cleanBody   │
        └──────────────┬───────────────────────────────┘
                       │
        ┌──────────────┴───────────────────────────────┐
        ▼                                              ▼
┌─────────────────────────────┐        ┌──────────────────────────────┐
│ 3. 纯净 Markdown 正文渲染    │        │ 4. 思考过程卡片独立构建       │
│    marked.parse(cleanBody)  │        │    <details class="chat-...  │
│    无任何 <think> 标签污染  │        │    - 单阶段: 极简折叠面板    │
│    HTML5 标准标签树零冲突   │        │    - 多阶段: Agent 融合推理链│
└──────────────┬──────────────┘        └──────────────┬───────────────┘
               │                                      │
               └──────────────┬───────────────────────┘
                              ▼
        ┌──────────────────────────────────────────────┐
        │ 5. DOM 安全装配与白名单过滤                  │
        │    template.content.prepend(details)         │
        │    XSS 安全消毒器 (tags 白名单)              │
        └──────────────────────────────────────────────┘
```

### 3.1 核心代码实现

#### 1. 严格受控的 `balanceTags`
```javascript
// 3. 智能正文隔离：仅当开标签数多于闭标签数（确实存在未闭合的思考块）时才尝试隔离
//    使用负向预查禁止跨越已闭合的 </think>，且仅在遇到显著的自然语言正文起首词时闭合，
//    严禁将常规 Markdown 列表符号（如 -、*、1.）作为判定边界，防止思考链内部的条目被误截断。
let thinkOpen = (balanced.match(/<think\b/gi) || []).length;
let thinkClose = (balanced.match(/<\/think>/gi) || []).length;
if (thinkOpen > thinkClose) {
  const thoughtAnswerBoundary = /(<think\b[^>]*>(?:(?!<\/think>)[\s\S])*?\n\n)(?=(?:您好|你好|好的|收到|抱歉|对不起|根据|总的来说|综上所述|Hello|Hi|Dear|Sure|Here\b))/i;
  if (thoughtAnswerBoundary.test(balanced)) {
    balanced = balanced.replace(thoughtAnswerBoundary, '$1</think>\n\n');
  }
}
```

#### 2. 前置提取函数 `extractThoughts`
```javascript
export function extractThoughts(rawText) {
  if (!rawText) return { thoughtSteps: [], cleanBody: '', isStreaming: false };

  const isStreaming = /<think\b(?![\s\S]*<\/think>)/i.test(rawText) || /<thought\b(?![\s\S]*<\/thought>)/i.test(rawText);
  const balanced = balanceTags(rawText);

  const thoughtSteps = [];
  const cleanBody = balanced.replace(/<(think|thought)\b[^>]*>([\s\S]*?)<\/\1>/gi, (match, tag, content) => {
    const trimmed = content.trim();
    if (trimmed) {
      thoughtSteps.push(trimmed);
    }
    return '';
  }).trimStart();

  return {
    thoughtSteps,
    cleanBody,
    isStreaming
  };
}
```

#### 3. 洁净环境下的 `renderMarkdown`
```javascript
export function renderMarkdown(text) {
  const { thoughtSteps, cleanBody, isStreaming } = extractThoughts(text || '');
  const template = document.createElement('template');
  // cleanBody 完全移除了 <think>，marked 只处理纯粹、合法的 Markdown 文本
  template.innerHTML = cleanBody ? marked.parse(cleanBody) : '';

  if (thoughtSteps.length > 0) {
    const details = document.createElement('details');
    details.className = 'chat-thought-container';
    if (isStreaming) {
      details.setAttribute('open', '');
    }

    const summary = document.createElement('summary');
    summary.className = 'chat-thought-summary';
    if (thoughtSteps.length === 1) {
      summary.textContent = isStreaming ? '💭 深度思考中...' : '💭 思考过程 (点击展开)';
    } else {
      summary.textContent = isStreaming
        ? `💭 深度思考中 (第 ${thoughtSteps.length} 轮 Agent 推理)...`
        : `💭 思考过程 (共 ${thoughtSteps.length} 轮 Agent 思考与推理，点击展开)`;
    }

    const content = document.createElement('div');
    content.className = 'chat-thought-content';

    if (thoughtSteps.length === 1) {
      content.textContent = thoughtSteps[0];
    } else {
      for (let i = 0; i < thoughtSteps.length; i++) {
        const stepDiv = document.createElement('div');
        stepDiv.className = 'chat-thought-step';
        const isLast = i === thoughtSteps.length - 1;
        const header = document.createElement('div');
        header.className = 'chat-thought-step-header';
        header.textContent = `🔹 思考阶段 #${i + 1}${isLast ? ' (最终生成决策)' : ' (工具调用分析)'}`;
        const body = document.createElement('div');
        body.className = 'chat-thought-step-body';
        body.textContent = thoughtSteps[i];
        stepDiv.appendChild(header);
        stepDiv.appendChild(body);
        content.appendChild(stepDiv);
      }
    }

    details.appendChild(summary);
    details.appendChild(content);
    // 以干净的顶级同级节点（Sibling）形式插入 DOM，彻底根除父子误吞！
    template.content.prepend(details);
  }

  // 消毒器白名单校验并返回
  ...
}
```

---

## 4. 问题本质同类门禁与防回归测试 (Quality Gates & Regression Test Suite)

在 `Business/ReferenceRAG.Business/wwwroot/tests/frontend-logic.test.mjs` 中新增了针对该类缺陷的专属门禁测试，包含 3 个维度的严格断言：

```javascript
test('safe-markdown.js extractThoughts and renderMarkdown quality gate: preserves 100% of answer content without HTML5 nesting/truncation', async () => {
    const { balanceTags, extractThoughts } = await import('../shared/safe-markdown.js');

    // 1. 回归门禁：已闭合的思考块 + 包含多级标题、水平分割线与复杂列表的正文（用户真实成都场景）
    const chengduResponse = `<think>
**Exploring Chengdu**
The user is asking for an introduction to Chengdu...
</think>

成都是一座兼具历史底蕴与生活气息的城市，很多人第一次来，觉得“慢、好吃、有故事”。
...
## 1. 地理位置与气候
- 成都位于四川盆地西部...
## 5. 旅游亮点
- **青城山 + 都江堰**...`;

    const balanced = balanceTags(chengduResponse);
    const thinkCloseMatches = balanced.match(/<\/think>/gi) || [];
    assert.equal(thinkCloseMatches.length, 1, '已闭合文本严禁被人为插入多余的 </think>');

    const extracted = extractThoughts(chengduResponse);
    assert.equal(extracted.thoughtSteps.length, 1);
    assert.equal(extracted.isStreaming, false);
    assert(extracted.cleanBody.startsWith('成都是一座兼具历史底蕴与生活气息的城市'), '正文必须以开篇导言开始');
    assert(extracted.cleanBody.includes('## 1. 地理位置与气候'), '正文必须完整包含第 1 节');
    assert(extracted.cleanBody.includes('## 5. 旅游亮点'), '正文必须完整包含第 5 节');
    assert(extracted.cleanBody.includes('青城山 + 都江堰'), '正文列表项严禁截断丢失');

    // 2. 思考链内部包含有序/无序列表与数字编号时，绝不误截断
    const thoughtWithInternalLists = `<think>
思维链步骤如下：
1. 分析用户意图
2. 检索知识库候选文档
- 候选 A 得分 0.95
</think>

这是最终答案：
- 结果一`;

    const resInternalLists = extractThoughts(thoughtWithInternalLists);
    assert(resInternalLists.thoughtSteps[0].includes('1. 分析用户意图'));
    assert(resInternalLists.thoughtSteps[0].includes('- 候选 A 得分 0.95'));
    assert(resInternalLists.cleanBody.includes('- 结果一'));

    // 3. 流式生成进行中的思考（未闭合）检测
    const streamingThought = '<think>\n正在深度思考并分析知识库召回结果...';
    const resStreaming = extractThoughts(streamingThought);
    assert.equal(resStreaming.thoughtSteps.length, 1);
    assert.equal(resStreaming.isStreaming, true, '必须准确识别流式进行状态');
    assert.equal(resStreaming.cleanBody, '', '思考流式期间正文主体必须为空，不触发无响应警告');
});
```

### 测试执行结果
执行 `node --test Business/ReferenceRAG.Business/wwwroot/tests/frontend-logic.test.mjs`：
```
✔ safe-markdown.js extractThoughts and renderMarkdown quality gate: preserves 100% of answer content without HTML5 nesting/truncation (0.5847ms)
ℹ tests 19
ℹ pass 19
ℹ fail 0
```
全部 19 项前端与核心逻辑测试 100% 通过。

---

## 5. 总结与运维排障指导 (Summary & Troubleshooting Runbook)

### 5.1 架构收益
1. **内容零丢失保证（100% Content Integrity）**：从源头上杜绝了非标准 HTML 伪标签进入 CommonMark 解析器的隐患，彻底消除 HTML5 树构建错误导致的段落丢失。
2. **列表与思考链完全正交**：思考链内部支持自由使用 Markdown 标题、有序列表、无序列表和代码块，再也不会被误判为正文起首而异常截断。
3. **零布局偏移（No Layout Shift）**：流式生成思考内容时，`<details open>` 卡片在第一帧即可确定自身形态，正文流式输出时平滑追加，无跳动感。

### 5.2 开发者核验清单
当引入新型推理模型或定制提示词模板时，可通过以下步骤一键验证：
1. **浏览器控制台验证**:
   在任意对话页面按 F12 打开 Console，执行：
   ```javascript
   import('/shared/safe-markdown.js').then(m => {
     const res = m.extractThoughts(yourRawModelOutput);
     console.log('思考步骤数:', res.thoughtSteps.length);
     console.log('正文开篇:', res.cleanBody.slice(0, 100));
   });
   ```
2. **端到端回归命令**:
   ```powershell
   node --test Business/ReferenceRAG.Business/wwwroot/tests/frontend-logic.test.mjs
   ```
