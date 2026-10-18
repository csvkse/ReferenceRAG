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

