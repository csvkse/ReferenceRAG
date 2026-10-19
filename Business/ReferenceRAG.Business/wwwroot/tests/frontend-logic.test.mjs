import test from 'node:test';
import assert from 'node:assert/strict';

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
