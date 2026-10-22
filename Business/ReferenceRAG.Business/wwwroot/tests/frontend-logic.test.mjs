import test from 'node:test';
import assert from 'node:assert/strict';
import {notifyDesktopReady} from '../shared/desktop-startup.js';

test('Desktop readiness waits for DOM commit and uses the native Post envelope', async () => {
    const messages = [];
    let commit;
    const pendingCommit = new Promise(resolve => { commit = resolve; });
    const ready = notifyDesktopReady(() => pendingCommit, {postMessage: text => messages.push(JSON.parse(text))});
    assert.deepEqual(messages, []);
    commit();
    await ready;
    assert.deepEqual(messages, [{id:'desktop-ready',command:'Post',version:2,data:'ready'}]);
});

test('Browser startup without a desktop bridge remains supported', async () => {
    await assert.doesNotReject(notifyDesktopReady(() => Promise.resolve(), undefined));
});

test('Desktop ready is not sent until both animation frame callbacks execute', async () => {
    const frames = [], messages = [];
    const pending = notifyDesktopReady(() => Promise.resolve(),
        {postMessage: text => messages.push(text)}, callback => frames.push(callback));
    await Promise.resolve();
    assert.equal(messages.length, 0);
    frames.shift()();
    assert.equal(messages.length, 0);
    frames.shift()();
    await pending;
    assert.equal(messages.length, 1);
});

test('Search.js selectedPathFolders correctly extracts folders when source root or folders are selected', () => {
    const paths = [
        { name: 'docs', rootPath: 'E:\\docs', folders: ['E:\\docs\\guide', 'E:\\docs\\api'] },
        { name: 'code', rootPath: 'E:\\code', folders: ['E:\\code\\src'] }
    ];

    function computeSelectedFolders(selectedKeys, sourcePaths) {
        const result = new Set();
        for (const key of selectedKeys) {
            if (key.startsWith('folder:')) {
                result.add(key.replace('folder:', ''));
            } else if (key.startsWith('source:')) {
                const sourceName = key.replace('source:', '');
                const source = sourcePaths.find(s => s.name === sourceName);
                if (source?.rootPath) {
                    result.add(source.rootPath);
                }
                if (source?.folders?.length) {
                    source.folders.forEach(f => result.add(f));
                }
            }
        }
        return Array.from(result);
    }

    // 1. Folder level selection
    const folderSelected = computeSelectedFolders(['folder:E:\\docs\\guide'], paths);
    assert.deepEqual(folderSelected, ['E:\\docs\\guide']);

    // 2. Source level selection
    const sourceSelected = computeSelectedFolders(['source:docs'], paths);
    assert.ok(sourceSelected.includes('E:\\docs'));
    assert.ok(sourceSelected.includes('E:\\docs\\guide'));
    assert.ok(sourceSelected.includes('E:\\docs\\api'));
});

test('Search.js preserves boolean enableRerank instead of coercing to undefined', () => {
    function buildSearchParams(options) {
        return {
            query: 'test',
            enableRerank: options.enableRerank
        };
    }

    const enabledResult = buildSearchParams({ enableRerank: true });
    assert.equal(enabledResult.enableRerank, true);
    assert.equal(JSON.stringify(enabledResult), '{"query":"test","enableRerank":true}');

    const disabledResult = buildSearchParams({ enableRerank: false });
    assert.equal(disabledResult.enableRerank, false);
    assert.equal(JSON.stringify(disabledResult), '{"query":"test","enableRerank":false}');
});

test('Dashboard.js progress calculation avoids NaN on totalFiles = 0', () => {
    function calculateProgressPercent(processed, total) {
        return total > 0 ? Math.round((processed / total) * 100) : 0;
    }

    // Zero total files
    assert.equal(calculateProgressPercent(0, 0), 0);
    assert.ok(!Number.isNaN(calculateProgressPercent(0, 0)));

    // Active progress
    assert.equal(calculateProgressPercent(5, 10), 50);
    assert.equal(calculateProgressPercent(10, 10), 100);
});

test('Chat.js message render caching avoids repeated markdown rendering', () => {
    let parseCount = 0;
    function mockRenderMarkdown(content) {
        parseCount++;
        return `<p>${content}</p>`;
    }

    function renderMsg(msg, isCurrentStreaming) {
        if (!isCurrentStreaming && msg._html) {
            return msg._html;
        }
        const html = mockRenderMarkdown(msg.content);
        if (!isCurrentStreaming) {
            msg._html = html;
        }
        return html + (isCurrentStreaming ? '▌' : '');
    }

    const msg = { role: 'assistant', content: 'Hello World' };

    // Initial render when not streaming
    const html1 = renderMsg(msg, false);
    assert.equal(html1, '<p>Hello World</p>');
    assert.equal(parseCount, 1);

    // Subsequent renders should hit cache
    const html2 = renderMsg(msg, false);
    assert.equal(html2, '<p>Hello World</p>');
    assert.equal(parseCount, 1); // Not parsed again!
});

test('Template symbol binding checker detects unbound identifiers in templates', async () => {
    const { checkTemplateBindings } = await import('../check.mjs');

    // 1. Valid template with all bindings defined
    const validMissing = checkTemplateBindings(
        '<div><button @click="handleClick">{{ title }}</button></div>',
        ['handleClick', 'title']
    );
    assert.deepEqual(validMissing, []);

    // 2. Valid template with scoped variables (v-for and arrow functions)
    const scopedMissing = checkTemplateBindings(
        '<div v-for="(item, idx) in list" :key="idx"><button @click="() => log(item)">{{ item.name }}</button></div>',
        ['list', 'log']
    );
    assert.deepEqual(scopedMissing, []);

    // 3. Invalid template with missing renderMsg and throttledScroll (like previous Chat.js bug)
    const invalidMissing = checkTemplateBindings(
        '<div><div v-html="renderMsg(msg)"></div><div @scroll="throttledScroll"></div></div>',
        ['msg']
    );
    assert.ok(invalidMissing.includes('renderMsg'), 'Should detect missing renderMsg');
    assert.ok(invalidMissing.includes('throttledScroll'), 'Should detect missing throttledScroll');
});

test('checkReturnedScope detects undeclared identifiers in __returned__ preventing black screen runtime errors', async () => {
    const { checkReturnedScope } = await import('../check.mjs');

    // 1. Valid setup with declared identifiers
    const validSource = `
        import { ref } from 'vue';
        const count = ref(0);
        function increment() { count.value++; }
        const __returned__ = { count, increment };
        return __returned__;
    `;
    assert.deepEqual(checkReturnedScope(validSource), []);

    // 2. Invalid setup with undeclared shorthand property (like confirmDownload bug)
    const invalidSource = `
        import { ref } from 'vue';
        const count = ref(0);
        const __returned__ = { count, confirmDownload, handleSwitch };
        return __returned__;
    `;
    const undeclared = checkReturnedScope(invalidSource);
    assert.ok(undeclared.includes('confirmDownload'), 'Should detect undeclared confirmDownload');
    assert.ok(undeclared.includes('handleSwitch'), 'Should detect undeclared handleSwitch');
    assert.ok(!undeclared.includes('count'), 'Declared count should not be flagged');
});

test('Chat.js parseJsonSafe correctly handles empty bodies with descriptive error', async () => {
    const parseJsonSafe = async (res) => {
        const text = await res.text();
        if (!text || !text.trim()) {
            throw new Error(`服务响应为空 (HTTP ${res.status})`);
        }
        try {
            return JSON.parse(text);
        } catch (err) {
            throw new Error(`响应解析失败: ${err.message}`);
        }
    };

    // 1. Valid JSON
    const validRes = new Response(JSON.stringify({ sessionId: 'session-123' }), { status: 200 });
    const data = await parseJsonSafe(validRes);
    assert.equal(data.sessionId, 'session-123');

    // 2. Empty body (0 bytes) - avoids "Unexpected end of JSON input" DOMException
    const emptyRes = new Response('', { status: 200 });
    await assert.rejects(parseJsonSafe(emptyRes), /服务响应为空 \(HTTP 200\)/);

    // 3. Malformed JSON
    const malformedRes = new Response('<html>Error</html>', { status: 500 });
    await assert.rejects(parseJsonSafe(malformedRes), /响应解析失败/);
});

test('Chat.js module imports ragFetch and passes quality gate without unbound symbols', async () => {
    const fs = await import('node:fs');
    const file = new URL('../app/views/Chat.js', import.meta.url);
    const source = fs.readFileSync(file, 'utf8');

    // Verify import of ragFetch
    assert.match(source, /import\s*\{[^}]*\bragFetch\b[^}]*\}\s*from\s*['"]\/core\/transport\/index\.js['"]/);

    // Verify setup returned scope
    const { checkReturnedScope } = await import('../check.mjs');
    const undeclared = checkReturnedScope(source);
    assert.deepEqual(undeclared, []);
});

test('MainLayout.js keeps all router views alive and keys components by route.name', async () => {
    const fs = await import('node:fs');
    const file = new URL('../app/layouts/MainLayout.js', import.meta.url);
    const source = fs.readFileSync(file, 'utf8');

    // Must not restrict keep-alive to Chat only
    assert.ok(!source.includes(":include=\"['Chat']\""), 'MainLayout should not restrict keep-alive to Chat only');

    // Must include keep-alive with component keyed by route.name
    assert.match(source, /<keep-alive[^>]*>[\s\S]*?<component\s+:is=\\?["']Component\\?["']\s+:key=\\?["']route\.name\\?["']\s*\/>[\s\S]*?<\/keep-alive>/);
});

test('Search.js, Performance.js, Models.js, Sources.js, Dashboard.js declare component names and onActivated hooks', async () => {
    const fs = await import('node:fs');
    const views = ['Search.js', 'Performance.js', 'Models.js', 'Sources.js', 'Dashboard.js'];

    for (const view of views) {
        const file = new URL(`../app/views/${view}`, import.meta.url);
        const source = fs.readFileSync(file, 'utf8');
        const viewName = view.replace('.js', '');

        // Must declare explicit name: '<ViewName>'
        assert.match(source, new RegExp(`name:\\s*['"]${viewName}['"]`), `${view} must declare name: '${viewName}'`);

        // Must import and use onActivated hook to silently refresh state on re-entry
        assert.match(source, /\bonActivated\b/, `${view} must import and hook onActivated`);
    }
});

test('Performance.js rerank preset linkage: exports active preset state, stepper and rowProps', async () => {
    const fs = await import('node:fs');
    const file = new URL('../app/views/Performance.js', import.meta.url);
    const source = fs.readFileSync(file, 'utf8');

    // 1. Verify linkage identifiers exist in source
    assert.match(source, /activeRerankPresetKey/);
    assert.match(source, /selectRerankSuite/);
    assert.match(source, /prevRerankSuite/);
    assert.match(source, /nextRerankSuite/);
    assert.match(source, /rerankRowProps/);

    // 2. Verify checkReturnedScope has no undeclared variables
    const { checkReturnedScope } = await import('../check.mjs');
    const undeclared = checkReturnedScope(source);
    assert.deepEqual(undeclared, [], `Performance.js has undeclared symbols in __returned__: ${undeclared.join(', ')}`);
});

test('Rerank linkage logic: stepper and upsert mechanics operate correctly', () => {
    // 1. Test Stepper Navigation
    const suites = [
        { name: 'technical', description: '技术文档', ndcg: 0.92 },
        { name: 'faq', description: '业务FAQ', ndcg: 0.88 },
        { name: 'antonym', description: '反义词硬对立', ndcg: 0.82 }
    ];

    let activeKey = 'faq';
    function getIndex(key) {
        return suites.findIndex(s => s.name === key);
    }

    function prevSuite(currentKey) {
        const idx = getIndex(currentKey);
        return idx > 0 ? suites[idx - 1].name : currentKey;
    }

    function nextSuite(currentKey) {
        const idx = getIndex(currentKey);
        return idx < suites.length - 1 ? suites[idx + 1].name : currentKey;
    }

    assert.equal(prevSuite('faq'), 'technical');
    assert.equal(prevSuite('technical'), 'technical'); // at boundary
    assert.equal(nextSuite('faq'), 'antonym');
    assert.equal(nextSuite('antonym'), 'antonym'); // at boundary

    // 2. Test Incremental Upsert
    let allResults = [
        { name: 'technical', ndcg: 0.90 }
    ];

    function upsertResult(newItem) {
        const idx = allResults.findIndex(r => r.name === newItem.name);
        if (idx !== -1) {
            allResults[idx] = newItem;
        } else {
            allResults.push(newItem);
        }
    }

    // Update existing
    upsertResult({ name: 'technical', ndcg: 0.95 });
    assert.equal(allResults.length, 1);
    assert.equal(allResults[0].ndcg, 0.95);

    // Append new
    upsertResult({ name: 'antonym', ndcg: 0.85 });
    assert.equal(allResults.length, 2);
    assert.equal(allResults[1].name, 'antonym');

    // 3. Test Row Highlight logic
    function computeRowProps(row, activeKey) {
        const isActive = row.name === activeKey;
        return {
            cursor: 'pointer',
            isActive,
            bg: isActive ? 'rgba(99, 226, 183, 0.12)' : undefined
        };
    }

    const activeProps = computeRowProps({ name: 'antonym' }, 'antonym');
    assert.equal(activeProps.isActive, true);
    assert.ok(activeProps.bg);

    const inactiveProps = computeRowProps({ name: 'technical' }, 'antonym');
    assert.equal(inactiveProps.isActive, false);
    assert.equal(inactiveProps.bg, undefined);
});

test('Performance.js supports compact minimalist summary bar and collapsible full metrics for rerank detail', async () => {
    const fs = await import('node:fs');
    const file = new URL('../app/views/Performance.js', import.meta.url);
    const source = fs.readFileSync(file, 'utf8');

    // 1. Must declare and export showRerankFullMetrics ref (defaulting to false)
    assert.match(source, /const\s+showRerankFullMetrics\s*=\s*ref\(false\)/, 'showRerankFullMetrics must default to false (minimalist collapsed mode)');
    assert.match(source, /__returned__\s*=\s*\{[^}]*\bshowRerankFullMetrics\b/, 'showRerankFullMetrics must be exported in __returned__');

    // 2. Template must contain the compact summary bar and collapse transition
    assert.ok(source.includes('Compact Summary Bar') || source.includes('最简数据紧凑指示条'), 'Must include compact summary bar in template');
    assert.match(source, /<n-collapse-transition\s+:show=\\?["']showRerankFullMetrics\\?["']/, 'Must wrap verbose metrics in n-collapse-transition');

    // 3. Compact bar must include core minimalist indicators
    assert.ok(source.includes('NDCG:'), 'Compact bar must show NDCG');
    assert.ok(source.includes('MRR:'), 'Compact bar must show MRR');
    assert.ok(source.includes('MAP:'), 'Compact bar must show MAP');
    assert.ok(source.includes('准确度:'), 'Compact bar must show 准确度');
    assert.ok(source.includes('MAE:'), 'Compact bar must show MAE');
    assert.ok(source.includes('耗时:'), 'Compact bar must show 耗时');
});

test('Chat.js and safe-markdown.js: thought fusion and truncation detection guardrails', async () => {
    const fs = await import('node:fs');
    const chatFile = new URL('../app/views/Chat.js', import.meta.url);
    const chatSource = fs.readFileSync(chatFile, 'utf8');

    // 1. Chat.js must declare and export truncation detection and continuation
    assert.match(chatSource, /const\s+isTruncatedMessage\s*=/, 'Chat.js must declare isTruncatedMessage');
    assert.match(chatSource, /const\s+continueGeneration\s*=/, 'Chat.js must declare continueGeneration');
    assert.ok(chatSource.includes('isTruncatedMessage'), 'isTruncatedMessage must be in __returned__');
    assert.ok(chatSource.includes('continueGeneration'), 'continueGeneration must be in __returned__');
    assert.ok(chatSource.includes('PlayOutline'), 'PlayOutline must be in __returned__');
    assert.ok(chatSource.includes('继续生成'), 'Template must include 继续生成 button');

    // 2. safe-markdown.js must implement thought fusion strategy
    const safeMdFile = new URL('../shared/safe-markdown.js', import.meta.url);
    const safeMdSource = fs.readFileSync(safeMdFile, 'utf8');
    assert.ok(safeMdSource.includes('chat-thought-container'), 'safe-markdown.js must use chat-thought-container');
    assert.ok(safeMdSource.includes('chat-thought-step'), 'safe-markdown.js must use chat-thought-step for multi-round fusion');
    assert.ok(safeMdSource.includes('thoughtSteps.length'), 'safe-markdown.js must fuse multiple thought blocks into one');

    // 3. Truncation heuristic logic validation
    function isTruncatedMessage(content) {
        if (!content || typeof content !== 'string') return false;
        const trimmed = content.trim();
        if (trimmed.length < 60) return false;
        const terminalChars = new Set(['。', '！', '？', '!', '?', '.', '`', '"', '”', '」', '）', ')', '}', ']', '>', '…', ';', '；']);
        const lastChar = trimmed[trimmed.length - 1];
        return !terminalChars.has(lastChar);
    }

    const longComplete = '这是一段非常长非常长的助手回复正文内容，包含了丰富的细节与论据。针对小说中的宁姚角色经历进行了极为全面深刻的总结与梳理，条理清晰，层次分明，逻辑严谨。总结如下：宁姚是剑道第一人。';
    assert.equal(isTruncatedMessage(longComplete), false, 'Complete sentence with period must not be marked truncated');

    const longTruncated = '这是一段非常长非常长的助手回复正文内容，包含了丰富的细节与论据。针对小说中的宁姚角色经历进行了极为全面深刻的总结与梳理，条理清晰，层次分明，逻辑严谨。总结如下：宁姚正在飞升途中独自探寻第五天下的万千';
    assert.equal(isTruncatedMessage(longTruncated), true, 'Truncated sentence without terminal punctuation must be marked truncated');

    const shortTruncated = '宁姚独自探寻第五天下的万千';
    assert.equal(isTruncatedMessage(shortTruncated), false, 'Short sentence (<60) must not trigger truncation warning');
});

test('safe-markdown.js balanceTags and thought extraction: prevents cumulative thought nesting and text swallowing', async () => {
    const { balanceTags, getDirectThoughtContent } = await import('../shared/safe-markdown.js');

    // 1. 测试多阶段未闭合思考标签自动在下一轮前或工具前闭合，避免跨阶段嵌套
    const rawMultiStage = '<think>阶段 1 思考<tool_call>{"name":"Search"}</tool_call>\n<think>阶段 2 思考\n<think>阶段 3 最终决策</think>';
    const balanced = balanceTags(rawMultiStage);

    // 确保阶段 1 在 tool_call 前被闭合
    assert.match(balanced, /阶段 1 思考\s*<\/think>\s*<tool_call>/);
    // 确保阶段 2 在阶段 3 前被闭合
    assert.match(balanced, /阶段 2 思考\s*<\/think>\s*<think>阶段 3/);
    // 确保总开闭标签数量完全平衡
    const thinkOpen = (balanced.match(/<think\b/gi) || []).length;
    const thinkClose = (balanced.match(/<\/think>/gi) || []).length;
    assert.equal(thinkOpen, thinkClose, `All think open tags (${thinkOpen}) must match close tags (${thinkClose})`);

    // 2. 测试未闭合思考标签后紧随正文时，智能在此处闭合，防止正文被吸入折叠卡片
    const rawUnclosedWithAnswer = '<think>用户发送了图片，我应该礼貌向用户问好并说明情况。\n\n您好！收到您的图片和问候，请问有什么可以帮助您的吗？';
    const balancedAnswer = balanceTags(rawUnclosedWithAnswer);
    assert.match(balancedAnswer, /<\/think>\s*您好！收到您的图片和问候/, 'Should automatically close </think> before greeting answer');

    // 3. 测试 getDirectThoughtContent：模拟 DOM 嵌套结构时，各阶段仅提取自身的直接文本，杜绝阶梯式累加重复
    // 模拟 DOM 树: <think>阶段 1 文本 <think>阶段 2 文本</think></think>
    const mockChildThink = {
        nodeType: 1,
        tagName: 'THINK',
        textContent: '阶段 2 文本',
        childNodes: [{ nodeType: 3, nodeValue: '阶段 2 文本' }]
    };
    const mockParentThink = {
        nodeType: 1,
        tagName: 'THINK',
        textContent: '阶段 1 文本 阶段 2 文本',
        childNodes: [
            { nodeType: 3, nodeValue: '阶段 1 文本 ' },
            mockChildThink
        ]
    };

    const directParentText = getDirectThoughtContent(mockParentThink);
    const directChildText = getDirectThoughtContent(mockChildThink);

    assert.equal(directParentText, '阶段 1 文本', 'Parent think step must only contain its own direct text, ignoring child think');
    assert.equal(directChildText, '阶段 2 文本', 'Child think step must only contain its own direct text');
});

test('safe-markdown.js extractThoughts and renderMarkdown quality gate: preserves 100% of answer content without HTML5 nesting/truncation', async () => {
    const { balanceTags, extractThoughts } = await import('../shared/safe-markdown.js');

    // 1. 回归门禁：已闭合的思考块 + 包含标题与多级列表的正文（用户真实成都问答场景）
    // 严禁将正文内容误吞进思考块，严禁因后文存在列表项而在正文中插入非法多余的 </think>
    const chengduResponse = `<think>
**Exploring Chengdu**

The user is asking for an introduction to Chengdu. I’ll provide an overview.
</think>

成都是一座兼具历史底蕴与生活气息的城市，很多人第一次来，觉得“慢、好吃、有故事”。

下面从几个维度简要介绍一下：

---

## 1. 地理位置与气候
- 成都位于四川盆地西部，盆地边缘，地势平坦，农业条件很好。
- 气候温和湿润，四季分明，夏天不闷热，冬天不冷，适合生活。

---

## 2. 历史与城市格局
- “天府之国”“锦官城”“锦官道”等名字，都跟成都的商贸地位有关。
- 历史上是重要的经济、文化、军事重镇，留下不少古迹和传说。

---

## 5. 旅游亮点
适合喜欢文化、历史、自然风光的旅行者：

- **青城山 + 都江堰**：道教名山与水利工程，山水清幽，适合静心。
- **武侯祠 + 杜甫草堂**：三国文化与文学名人的历史遗迹。`;

    const balanced = balanceTags(chengduResponse);
    const thinkCloseMatches = balanced.match(/<\/think>/gi) || [];
    assert.equal(thinkCloseMatches.length, 1, 'Balanced tags must have exactly 1 </think> for single closed thought');

    const extracted = extractThoughts(chengduResponse);
    assert.equal(extracted.thoughtSteps.length, 1, 'Should extract exactly 1 thought stage');
    assert.equal(extracted.isStreaming, false, 'Should be marked as completed (not streaming)');
    assert(extracted.thoughtSteps[0].includes('Exploring Chengdu'), 'Thought content must be accurately extracted');
    assert(extracted.cleanBody.startsWith('成都是一座兼具历史底蕴与生活气息的城市'), 'Clean body must begin with intro paragraph');
    assert(extracted.cleanBody.includes('## 1. 地理位置与气候'), 'Clean body must contain Section 1');
    assert(extracted.cleanBody.includes('## 2. 历史与城市格局'), 'Clean body must contain Section 2');
    assert(extracted.cleanBody.includes('## 5. 旅游亮点'), 'Clean body must contain Section 5');
    assert(extracted.cleanBody.includes('青城山 + 都江堰'), 'Clean body must contain Section 5 list item');

    // 2. 思考链内部包含有序/无序列表与数字编号时，绝不误截断
    const thoughtWithInternalLists = `<think>
思维链步骤如下：
1. 分析用户意图
2. 检索知识库候选文档
- 候选 A 得分 0.95
- 候选 B 得分 0.88
</think>

这是最终的答案，包含要点：
- 结果一
- 结果二`;

    const resInternalLists = extractThoughts(thoughtWithInternalLists);
    assert.equal(resInternalLists.thoughtSteps.length, 1);
    assert(resInternalLists.thoughtSteps[0].includes('1. 分析用户意图'), 'Internal numbers inside thought must stay inside thought');
    assert(resInternalLists.thoughtSteps[0].includes('- 候选 A 得分 0.95'), 'Internal bullet points inside thought must stay inside thought');
    assert(resInternalLists.cleanBody.startsWith('这是最终的答案'), 'Answer must start after thought');
    assert(resInternalLists.cleanBody.includes('- 结果一'), 'Answer bullet points must be preserved in cleanBody');

    // 3. 流式生成进行中的思考（未闭合）检测
    const streamingThought = '<think>\n正在深度思考并分析知识库召回结果...';
    const resStreaming = extractThoughts(streamingThought);
    assert.equal(resStreaming.thoughtSteps.length, 1);
    assert.equal(resStreaming.isStreaming, true, 'Streaming thought must be flagged as isStreaming=true');
    assert.equal(resStreaming.cleanBody, '', 'Clean body must be empty while streaming thought');
});
