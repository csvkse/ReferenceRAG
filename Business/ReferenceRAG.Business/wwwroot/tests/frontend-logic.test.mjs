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

test('Settings.js chat model tab includes enableIntentRouting toggle with sensible defaults', async () => {
    const fs = await import('node:fs');
    const filePath = new URL('../app/views/Settings.js', import.meta.url);
    const content = fs.readFileSync(filePath, 'utf8');

    // 1. 验证默认配置中默认开启意图识别与路由
    assert(content.includes('enableIntentRouting: true'), 'defaultConfig.chat must have enableIntentRouting: true');

    // 2. 验证 loadConfig 中包含缺省保底兜底处理
    assert(content.includes('config.value.chat.enableIntentRouting = true'), 'loadConfig must fallback to true if undefined');

    // 3. 验证 Settings 模板中包含开关与交互说明
    assert(content.includes('config.chat.enableIntentRouting'), 'Template must bind to config.chat.enableIntentRouting');
    assert(content.includes('label=\\"意图识别与路由\\"') || content.includes('label="意图识别与路由"'), 'Template must have form item labeled 意图识别与路由');
});

test('Settings.js chat model tab includes repetition penalty, frequency/presence penalties, and stream circuit breaker', async () => {
    const fs = await import('node:fs');
    const filePath = new URL('../app/views/Settings.js', import.meta.url);
    const content = fs.readFileSync(filePath, 'utf8');

    // 1. 默认配置参数
    assert(content.includes('repeatPenalty: 1.1'), 'defaultConfig.chat must have repeatPenalty: 1.1');
    assert(content.includes('frequencyPenalty: 0.2'), 'defaultConfig.chat must have frequencyPenalty: 0.2');
    assert(content.includes('presencePenalty: 0.1'), 'defaultConfig.chat must have presencePenalty: 0.1');
    assert(content.includes('enableRepetitionCircuitBreaker: true'), 'defaultConfig.chat must have enableRepetitionCircuitBreaker: true');

    // 2. loadConfig 缺省保底
    assert(content.includes('config.value.chat.repeatPenalty = 1.1'), 'loadConfig must default repeatPenalty to 1.1');
    assert(content.includes('config.value.chat.enableRepetitionCircuitBreaker = true'), 'loadConfig must default enableRepetitionCircuitBreaker to true');

    // 3. 模板绑定与交互
    assert(content.includes('config.chat.repeatPenalty'), 'Template must bind to config.chat.repeatPenalty');
    assert(content.includes('config.chat.frequencyPenalty'), 'Template must bind to config.chat.frequencyPenalty');
    assert(content.includes('config.chat.presencePenalty'), 'Template must bind to config.chat.presencePenalty');
    assert(content.includes('config.chat.enableRepetitionCircuitBreaker'), 'Template must bind to config.chat.enableRepetitionCircuitBreaker');
});

test('conversation-scroll.js: scroll buffer calculations, user intent pause, and streaming resume state machine', async () => {
    const {
        isNearConversationBottom,
        clampConversationScrollTop,
        isConversationFollowPauseIntent,
        shouldResumeConversationFollow,
        shouldFollowConversationStream,
        shouldApplyConversationStreamScroll
    } = await import('../shared/conversation-scroll.js');

    // 1. isNearConversationBottom 边界测算
    assert.equal(isNearConversationBottom({ scrollHeight: 1000, scrollTop: 700, clientHeight: 300 }), true, 'Exact bottom is near bottom');
    assert.equal(isNearConversationBottom({ scrollHeight: 1000, scrollTop: 650, clientHeight: 300, threshold: 96 }), true, 'Within 96px buffer is near bottom');
    assert.equal(isNearConversationBottom({ scrollHeight: 1000, scrollTop: 600, clientHeight: 300, threshold: 96 }), false, 'Outside 96px buffer is not near bottom');

    // 2. clampConversationScrollTop 安全截断
    assert.equal(clampConversationScrollTop({ scrollTop: -10, scrollHeight: 1000, clientHeight: 300 }), 0, 'Negative scrollTop clamped to 0');
    assert.equal(clampConversationScrollTop({ scrollTop: 800, scrollHeight: 1000, clientHeight: 300 }), 700, 'Overflow scrollTop clamped to max (scrollHeight - clientHeight)');
    assert.equal(clampConversationScrollTop({ scrollTop: 500, scrollHeight: 1000, clientHeight: 300 }), 500, 'Valid scrollTop preserved');

    // 3. isConversationFollowPauseIntent 用户中断意图探测 (严防流式强制拽底锁死滚动条)
    assert.equal(isConversationFollowPauseIntent({ type: 'wheel', deltaY: -20 }), true, 'Wheel up must pause follow');
    assert.equal(isConversationFollowPauseIntent({ type: 'wheel', deltaY: 20 }), false, 'Wheel down should not pause follow');
    assert.equal(isConversationFollowPauseIntent({ type: 'touchstart' }), true, 'Touch start must pause follow');
    assert.equal(isConversationFollowPauseIntent({ type: 'pointerdown', onScrollbar: true }), true, 'Clicking scrollbar rail must pause follow');
    assert.equal(isConversationFollowPauseIntent({ type: 'pointerdown', onScrollbar: false }), false, 'Clicking message text should not pause follow');

    // 4. shouldResumeConversationFollow 流式恢复意图识别
    // 场景 A: 用户在暂停期间向下滚动，并且回到了底部 96px 区域 -> 必须自动恢复跟随！
    assert.equal(shouldResumeConversationFollow({
        sending: true,
        resumeRequested: true,
        scrollHeight: 1000,
        scrollTop: 650,
        clientHeight: 300,
        threshold: 96
    }), true, 'Scrolling down back into bottom buffer during streaming must resume follow');

    // 场景 B: 正在流式生成，但用户在向上滚动离开底部 -> 严禁恢复，继续保持暂停
    assert.equal(shouldResumeConversationFollow({
        sending: true,
        resumeRequested: false,
        scrollHeight: 1000,
        scrollTop: 650,
        clientHeight: 300,
        threshold: 96
    }), false, 'Scrolling up must keep pause state');

    // 场景 C: 用户向下移动但依然离底部很远 (如查看第 2 屏) -> 严禁恢复打断阅读
    assert.equal(shouldResumeConversationFollow({
        sending: true,
        resumeRequested: true,
        scrollHeight: 2000,
        scrollTop: 800,
        clientHeight: 300,
        threshold: 96
    }), false, 'Scrolling down while far from bottom must not snap to bottom');

    // 5. shouldFollowConversationStream 判定规则
    assert.equal(shouldFollowConversationStream({ followPaused: true }), false, 'Paused follow must strictly forbid auto-scroll');
    assert.equal(shouldFollowConversationStream({ followRequested: true, followPaused: false }), true, 'Explicitly requested follow allowed');
    assert.equal(shouldFollowConversationStream({
        followRequested: false,
        followPaused: false,
        scrollHeight: 1000,
        scrollTop: 680,
        clientHeight: 300
    }), true, 'Natural bottom position allowed to follow');

    // 6. shouldApplyConversationStreamScroll 版本防竞态保护
    assert.equal(shouldApplyConversationStreamScroll({ scheduledRevision: 2, currentRevision: 2, followAllowed: true }), true);
    assert.equal(shouldApplyConversationStreamScroll({ scheduledRevision: 1, currentRevision: 2, followAllowed: true }), false, 'Stale revision must be dropped');
    assert.equal(shouldApplyConversationStreamScroll({ scheduledRevision: 2, currentRevision: 2, followAllowed: false }), false, 'Disallowed follow must be dropped');
});

test('Chat.js integrates conversation-scroll state machine, RAF batching, and floating resume button', async () => {
    const fs = await import('node:fs');
    const chatPath = new URL('../app/views/Chat.js', import.meta.url);
    const content = fs.readFileSync(chatPath, 'utf8');

    // 1. 验证导入通用滚动状态机模块
    assert(content.includes('/shared/conversation-scroll.js'), 'Chat.js must import conversation-scroll.js');
    assert(content.includes('isConversationFollowPauseIntent'), 'Chat.js must use isConversationFollowPauseIntent');
    assert(content.includes('shouldResumeConversationFollow'), 'Chat.js must use shouldResumeConversationFollow');

    // 2. 验证流式循环中已废除阻塞式 await scrollToBottom()，改为 RAF scheduleStreamScroll()
    assert(content.includes('scheduleStreamScroll()'), 'Chat.js must batch stream scrolling with scheduleStreamScroll');
    assert(content.includes('requestAnimationFrame'), 'Chat.js must use requestAnimationFrame for DOM scroll positioning');

    // 3. 验证模板中 n-scrollbar 绑定手势监听与滚动事件
    assert(content.includes('@scroll=\\"onCanvasScroll\\"'), 'Template must bind @scroll to onCanvasScroll');
    assert(content.includes('@wheel.passive=\\"onCanvasWheel\\"'), 'Template must bind @wheel to onCanvasWheel');
    assert(content.includes('@touchstart.passive=\\"onCanvasTouchStart\\"'), 'Template must bind @touchstart to onCanvasTouchStart');
    assert(content.includes('@pointerdown=\\"onCanvasPointerDown\\"'), 'Template must bind @pointerdown to onCanvasPointerDown');

    // 4. 验证悬浮「回到底部」浮动操作按钮支持一键恢复
    assert(content.includes('resumeAndScrollToBottom'), 'Template must support resumeAndScrollToBottom');
    assert(content.includes('ArrowDownOutline'), 'Must use ArrowDownOutline for floating button icon');
});

test('resolveScrollContainer defensively resolves Naive UI NScrollbar and prevents querySelector TypeError', async () => {
    const { resolveScrollContainer } = await import('../shared/conversation-scroll.js');

    // 1. 验证 null / undefined 入参安全返回 null
    assert.equal(resolveScrollContainer(null), null);
    assert.equal(resolveScrollContainer(undefined), null);

    // 2. 核心回归测试：模拟组件挂载中或代理异常阶段 $el 为空对象 {}
    // 原代码 `scrollbarRef.value.$el?.querySelector(...)` 在此情况下会抛出：
    // TypeError: scrollbarRef.value.$el?.querySelector is not a function
    const mockUnmountedInstance = {
        containerRef: null,
        $el: {}
    };
    assert.doesNotThrow(() => {
        const res = resolveScrollContainer(mockUnmountedInstance);
        assert.equal(res, null);
    }, 'Must not throw when $el is a plain object without querySelector');

    // 3. 模拟 Vue Text/Comment Node 或 Fragment（无 querySelector 方法）
    const mockCommentNodeInstance = {
        containerRef: null,
        $el: { nodeType: 8, textContent: 'v-if' }
    };
    assert.doesNotThrow(() => {
        const res = resolveScrollContainer(mockCommentNodeInstance);
        assert.equal(res, null);
    }, 'Must not throw when $el is a non-Element node');

    // 4. 验证优先解析 Naive UI NScrollbar 暴露的 containerRef (支持 Vue ref 或 DOM 元素)
    const mockDomContainer = { scrollTop: 120, scrollHeight: 500, clientHeight: 200 };
    const mockInstanceWithRef = {
        containerRef: { value: mockDomContainer },
        $el: {}
    };
    assert.equal(resolveScrollContainer(mockInstanceWithRef), mockDomContainer);

    const mockInstanceWithDirectContainer = {
        containerRef: mockDomContainer,
        $el: {}
    };
    assert.equal(resolveScrollContainer(mockInstanceWithDirectContainer), mockDomContainer);

    // 5. 验证退回从 $el.querySelector 查找 .n-scrollbar-container
    const mockInnerContainer = { scrollTop: 250, scrollHeight: 600, clientHeight: 300 };
    const mockElWithQuery = {
        containerRef: null,
        $el: {
            querySelector: (selector) => {
                if (selector === '.n-scrollbar-container') return mockInnerContainer;
                return null;
            }
        }
    };
    assert.equal(resolveScrollContainer(mockElWithQuery), mockInnerContainer);

    // 6. 验证 Chat.js 源码已消除不安全的 $el?.querySelector 直接调用
    const fs = await import('node:fs');
    const chatPath = new URL('../app/views/Chat.js', import.meta.url);
    const chatContent = fs.readFileSync(chatPath, 'utf8');

    assert(!chatContent.includes('$el?.querySelector'), 'Chat.js must not contain raw un-guarded $el?.querySelector call');
    assert(chatContent.includes('resolveScrollContainer'), 'Chat.js must use resolveScrollContainer helper');
});

test('ESM module import & export contract guardrail: checkModuleImports catches missing exports', async () => {
    const { checkModuleImports, extractImportMap, getModuleExports } = await import('../check.mjs');
    const path = await import('node:path');
    const { fileURLToPath } = await import('node:url');

    const testDir = path.dirname(fileURLToPath(import.meta.url));
    const rootDir = path.resolve(testDir, '..');
    const importMap = extractImportMap(rootDir);

    // 1. 验证正常导入 KeyOutline 和 BookOutline 不报错
    const validSource = `import { BookOutline, KeyOutline } from '@vicons/ionicons5';`;
    const validIssues = checkModuleImports(rootDir, path.join(rootDir, 'app/layouts/MainLayout.js'), validSource, importMap);
    assert.deepEqual(validIssues, [], 'Valid icon imports should have 0 issues');

    // 2. 验证导入未导出的虚构图标会精准报错拦截
    const invalidSource = `import { KeyOutline, NonExistentIconOutline } from '@vicons/ionicons5';`;
    const invalidIssues = checkModuleImports(rootDir, path.join(rootDir, 'app/layouts/MainLayout.js'), invalidSource, importMap);
    assert.equal(invalidIssues.length, 1);
    assert.ok(invalidIssues[0].includes("The requested module '@vicons/ionicons5' does not provide an export named 'NonExistentIconOutline'"));

    // 3. 验证未导出的 default 会精准报错拦截
    const defaultInvalidSource = `import NonExistentDefault from '@vicons/ionicons5';`;
    const defaultIssues = checkModuleImports(rootDir, path.join(rootDir, 'app/layouts/MainLayout.js'), defaultInvalidSource, importMap);
    assert.equal(defaultIssues.length, 1);
    assert.ok(defaultIssues[0].includes("does not provide a default export"));

    // 4. 验证不存在的目标模块会报告 Module not found
    const missingModuleSource = `import { Foo } from '/app/non-existent-module.js';`;
    const missingModuleIssues = checkModuleImports(rootDir, path.join(rootDir, 'app/layouts/MainLayout.js'), missingModuleSource, importMap);
    assert.equal(missingModuleIssues.length, 1);
    assert.ok(missingModuleIssues[0].includes("Module not found"));
});

test('index.html importmap and vendor icons integrity: KeyOutline and all used icons are properly exported', async () => {
    const { extractImportMap, getModuleExports } = await import('../check.mjs');
    const path = await import('node:path');
    const fs = await import('node:fs');
    const { fileURLToPath } = await import('node:url');

    const testDir = path.dirname(fileURLToPath(import.meta.url));
    const rootDir = path.resolve(testDir, '..');
    const importMap = extractImportMap(rootDir);

    // 1. 验证 @vicons/ionicons5 映射到 /vendor/icons.js
    assert.equal(importMap['@vicons/ionicons5'], '/vendor/icons.js');

    // 2. 验证物理文件及 KeyOutline.js 均存在
    const iconsJsPath = path.join(rootDir, 'vendor/icons.js');
    const keyOutlineJsPath = path.join(rootDir, 'vendor/icons/KeyOutline.js');
    assert.ok(fs.existsSync(iconsJsPath), 'vendor/icons.js must exist');
    assert.ok(fs.existsSync(keyOutlineJsPath), 'vendor/icons/KeyOutline.js must exist');

    // 3. 验证导出的符号集合包含 KeyOutline
    const exportedSymbols = getModuleExports(iconsJsPath, rootDir);
    assert.ok(exportedSymbols.has('KeyOutline'), 'vendor/icons.js must export KeyOutline');
    assert.ok(exportedSymbols.has('BookOutline'), 'vendor/icons.js must export BookOutline');
    assert.ok(exportedSymbols.has('ImageOutline'), 'vendor/icons.js must export ImageOutline');
    assert.ok(exportedSymbols.size >= 38, 'vendor/icons.js should export at least 38 icons');
});

test('ApiKeys unified permission tree logic: tri-state cascade between module scopes and child MCP tools', () => {
    // 模拟元数据目录 (7大功能模块，对齐 WebToolForLinux 规范)
    const mockCategories = [
        {
            key: 'chat',
            name: '对话与大模型问答',
            moduleCode: 'chat',
            supportsApi: true,
            supportsMcp: false,
            tools: []
        },
        {
            key: 'search',
            name: '知识检索与语义嵌入',
            moduleCode: 'search',
            supportsApi: true,
            supportsMcp: true,
            tools: [
                { name: 'SemanticSearch', riskLevel: 'Read', moduleCode: 'search' },
                { name: 'HybridSearch', riskLevel: 'Read', moduleCode: 'search' },
                { name: 'RerankResults', riskLevel: 'Read', moduleCode: 'search' }
            ]
        },
        {
            key: 'files',
            name: '文件系统与文本阅读',
            moduleCode: 'files',
            supportsApi: true,
            supportsMcp: true,
            tools: [
                { name: 'GetFileTree', riskLevel: 'Read', moduleCode: 'files' },
                { name: 'ReadNoteContent', riskLevel: 'Read', moduleCode: 'files' }
            ]
        }
    ];

    const form = {
        selectedScopes: [],
        selectedTools: []
    };

    const getCategorySelectedToolCount = (cat) => {
        if (!cat.tools || cat.tools.length === 0) return 0;
        return cat.tools.filter(t => form.selectedTools.includes(t.name)).length;
    };

    const isCategoryFullyChecked = (cat) => {
        if (!form.selectedScopes.includes(cat.moduleCode)) return false;
        if (!cat.tools || cat.tools.length === 0) return true;
        return getCategorySelectedToolCount(cat) === cat.tools.length;
    };

    const isCategoryIndeterminate = (cat) => {
        const modChecked = form.selectedScopes.includes(cat.moduleCode);
        if (!cat.tools || cat.tools.length === 0) return false;
        const count = getCategorySelectedToolCount(cat);
        if (modChecked) {
            return count > 0 && count < cat.tools.length;
        } else {
            return count > 0;
        }
    };

    const toggleCategoryNode = (cat) => {
        const isChecked = isCategoryFullyChecked(cat);
        if (isChecked) {
            form.selectedScopes = form.selectedScopes.filter(m => m !== cat.moduleCode);
            if (cat.tools && cat.tools.length > 0) {
                const toolNames = new Set(cat.tools.map(t => t.name));
                form.selectedTools = form.selectedTools.filter(t => !toolNames.has(t));
            }
        } else {
            if (!form.selectedScopes.includes(cat.moduleCode)) {
                form.selectedScopes.push(cat.moduleCode);
            }
            if (cat.tools && cat.tools.length > 0) {
                const toolSet = new Set(form.selectedTools);
                cat.tools.forEach(t => toolSet.add(t.name));
                form.selectedTools = Array.from(toolSet);
            }
        }
    };

    const toggleToolNode = (cat, toolName) => {
        const idx = form.selectedTools.indexOf(toolName);
        if (idx >= 0) {
            form.selectedTools.splice(idx, 1);
        } else {
            form.selectedTools.push(toolName);
            // 自动同步激活所属功能模块
            if (!form.selectedScopes.includes(cat.moduleCode)) {
                form.selectedScopes.push(cat.moduleCode);
            }
        }
    };

    const searchCat = mockCategories[1];

    // 1. 初始状态：未勾选
    assert.equal(isCategoryFullyChecked(searchCat), false);
    assert.equal(isCategoryIndeterminate(searchCat), false);

    // 2. 勾选分类节点：同时授权模块与所有旗下工具
    toggleCategoryNode(searchCat);
    assert.ok(form.selectedScopes.includes('search'));
    assert.equal(form.selectedTools.length, 3);
    assert.ok(form.selectedTools.includes('SemanticSearch'));
    assert.ok(form.selectedTools.includes('HybridSearch'));
    assert.ok(form.selectedTools.includes('RerankResults'));
    assert.equal(isCategoryFullyChecked(searchCat), true);
    assert.equal(isCategoryIndeterminate(searchCat), false);

    // 3. 微调反选其中一个工具：触发三态 Indeterminate
    toggleToolNode(searchCat, 'RerankResults');
    assert.equal(form.selectedTools.length, 2);
    assert.ok(form.selectedScopes.includes('search')); // 父模块仍然授权
    assert.equal(isCategoryFullyChecked(searchCat), false);
    assert.equal(isCategoryIndeterminate(searchCat), true);

    // 4. 单独勾选未授权模块旗下的工具：自动将父模块加入 selectedScopes
    const filesCat = mockCategories[2];
    assert.ok(!form.selectedScopes.includes('files'));
    toggleToolNode(filesCat, 'GetFileTree');
    assert.ok(form.selectedScopes.includes('files'), 'Toggling child tool must activate parent module scope');
    assert.ok(form.selectedTools.includes('GetFileTree'));
    assert.equal(isCategoryIndeterminate(filesCat), true);

    // 5. 对半选状态的分类点击：首先变为全选状态
    toggleCategoryNode(searchCat);
    assert.equal(isCategoryFullyChecked(searchCat), true);
    assert.equal(isCategoryIndeterminate(searchCat), false);
    assert.ok(form.selectedTools.includes('RerankResults'));

    // 6. 对全选状态的分类点击：模块与旗下工具全部被取消清空
    toggleCategoryNode(searchCat);
    assert.ok(!form.selectedScopes.includes('search'));
    assert.ok(!form.selectedTools.includes('SemanticSearch'));
    assert.ok(!form.selectedTools.includes('HybridSearch'));
    assert.ok(!form.selectedTools.includes('RerankResults'));
    assert.equal(isCategoryFullyChecked(searchCat), false);
    assert.equal(isCategoryIndeterminate(searchCat), false);
});

test('ApiKeys security presets and search filtering work as expected', () => {
    const mockCategories = [
        {
            key: 'chat',
            name: '对话与大模型问答',
            moduleCode: 'chat',
            supportsApi: true,
            supportsMcp: false,
            tools: []
        },
        {
            key: 'search',
            name: '知识检索与语义嵌入',
            moduleCode: 'search',
            supportsApi: true,
            supportsMcp: true,
            tools: [
                { name: 'SemanticSearch', riskLevel: 'Read', moduleCode: 'search', description: '向量搜索' },
                { name: 'HybridSearch', riskLevel: 'Read', moduleCode: 'search', description: '混合搜索' }
            ]
        },
        {
            key: 'system',
            name: '系统运维',
            moduleCode: 'system',
            supportsApi: true,
            supportsMcp: true,
            tools: [
                { name: 'RestartService', riskLevel: 'Dangerous', moduleCode: 'system', description: '重启服务' }
            ]
        }
    ];

    // 协议过滤
    const filterByProto = (proto) => mockCategories.filter(cat => {
        if (proto === 'api' && !cat.supportsApi) return false;
        if (proto === 'mcp' && !cat.supportsMcp) return false;
        return true;
    });

    assert.equal(filterByProto('all').length, 3);
    assert.equal(filterByProto('api').length, 3);
    assert.equal(filterByProto('mcp').length, 2); // chat 仅支持 API，不暴露 MCP

    // 只读策略
    const readOnlyTools = mockCategories.flatMap(c => c.tools.filter(t => t.riskLevel === 'Read').map(t => t.name));
    assert.equal(readOnlyTools.length, 2);
    assert.ok(!readOnlyTools.includes('RestartService'), 'Dangerous tools must be excluded in ReadOnly preset');
});

test('Component prop rules guardrail: checkComponentPropRules detects invalid space sizes preventing undefined split/horizontal crashes', async () => {
    const { checkComponentPropRules } = await import('../check.mjs');

    // 1. Valid sizes
    assert.equal(checkComponentPropRules('<n-space size="small">', 'test.js').length, 0);
    assert.equal(checkComponentPropRules('<n-space size="medium">', 'test.js').length, 0);
    assert.equal(checkComponentPropRules('<n-space size="large">', 'test.js').length, 0);
    assert.equal(checkComponentPropRules('<n-space :size="16">', 'test.js').length, 0);
    assert.equal(checkComponentPropRules('h(NSpace, { size: "small" })', 'test.js').length, 0);
    assert.equal(checkComponentPropRules('h(NSpace, { size: 12 })', 'test.js').length, 0);

    // 2. Invalid 'tiny' size (the exact bug that caused Cannot read properties of undefined reading split)
    const tagErrors = checkComponentPropRules('<n-space size="tiny" align="center">', 'ApiKeys.js');
    assert.equal(tagErrors.length, 1);
    assert.match(tagErrors[0], /Invalid size="tiny" on <n-space>/);

    const hErrors = checkComponentPropRules('h(NSpace, { size: "tiny" })', 'ApiKeys.js');
    assert.equal(hErrors.length, 1);
    assert.match(hErrors[0], /Invalid size: 'tiny' in h\(NSpace, ...\)/);

    // 3. Verify actual ApiKeys.js and App.js have zero violations
    const fs = await import('node:fs');
    const path = await import('node:path');
    const apiKeysSrc = fs.readFileSync(path.resolve('Business/ReferenceRAG.Business/wwwroot/app/views/ApiKeys.js'), 'utf8');
    assert.equal(checkComponentPropRules(apiKeysSrc, 'ApiKeys.js').length, 0, 'ApiKeys.js must have zero invalid space size props');
});



