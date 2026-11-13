import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import { SearchHitAdapter } from '../app/services/reader/SearchHitAdapter.js';
import { ReaderStateMachine, ReaderState } from '../app/services/reader/ReaderStateMachine.js';
import { ReaderTier, ReaderStrategyFactory, ViewerMode, TIER_LIMITS } from '../app/services/reader/ReaderViewerStrategy.js';
import { pickAnchorRow, computeViewportState } from '../app/services/reader/VirtualListAdapter.js';
import { ReaderViewportSentinel } from '../app/services/reader/ReaderViewportSentinel.js';
import { ReaderWindowStrategy, LineChunkCache } from '../app/services/reader/ReaderWindowStrategy.js';
import { ContinuationAction, resolveContinuationAction, shouldFocusNewChunk, shouldRestoreAnchor } from '../app/services/reader/ContinuationPolicy.js';
import { TextLayout, resolveLineStyle, resolveGutterStyle, resolveRowStyle } from '../app/services/reader/TextLayoutStrategy.js';
import { shouldPrefetch, PREFETCH_THRESHOLD_PX } from '../app/services/reader/PrefetchPolicy.js';
import { LayoutPreferenceKey, readLayoutPreference, writeLayoutPreference } from '../app/services/reader/LayoutPreference.js';

const __dirname = path.dirname(fileURLToPath(import.meta.url));

test('SearchHitAdapter correctly adapts RAG chunk into ReaderNavigationTarget and Route location', () => {
    const chunk = {
        source: 'DocsSource',
        filePath: 'docs/architecture.md',
        startLine: 142,
        endLine: 160,
        content: 'This is a sample chunk content.'
    };

    const target = SearchHitAdapter.fromRagChunk(chunk, 'sample query');
    assert.equal(target.sourceName, 'DocsSource');
    assert.equal(target.filePath, 'docs/architecture.md');
    assert.equal(target.fileName, 'architecture.md');
    assert.equal(target.targetLine, 142);
    assert.equal(target.endLine, 160);
    assert.equal(target.highlightPattern, 'sample query');
    assert.equal(target.isRegex, false);

    const location = SearchHitAdapter.toRouteLocation(target);
    assert.equal(location.name, 'Reader');
    assert.equal(location.query.path, 'docs/architecture.md');
    assert.equal(location.query.source, 'DocsSource');
    assert.equal(location.query.line, '142');
    assert.equal(location.query.highlight, 'sample query');
    assert.equal(location.hash, '#L142');
});

test('SearchHitAdapter correctly adapts FileSearch match line into ReaderNavigationTarget', () => {
    const file = {
        sourceName: 'BackendSource',
        relativePath: 'src/Controllers/SearchController.cs',
        fullPath: 'D:/Work/src/Controllers/SearchController.cs'
    };
    const line = {
        lineNumber: 58,
        text: 'public async Task<IActionResult> Search()',
        isMatch: true
    };

    const target = SearchHitAdapter.fromFileMatch(file, line, 'SearchController', false);
    assert.equal(target.sourceName, 'BackendSource');
    assert.equal(target.filePath, 'D:/Work/src/Controllers/SearchController.cs');
    assert.equal(target.fileName, 'SearchController.cs');
    assert.equal(target.targetLine, 58);
    assert.equal(target.highlightPattern, 'SearchController');

    const location = SearchHitAdapter.toRouteLocation(target);
    assert.equal(location.name, 'Reader');
    assert.equal(location.query.line, '58');
    assert.equal(location.hash, '#L58');
});

test('ReaderStateMachine correctly transitions states and stores execution context', () => {
    const transitions = [];
    const sm = new ReaderStateMachine({
        onStateChange: (to, from, ctx) => {
            transitions.push({ to, from, targetLine: ctx.targetLine });
        }
    });

    assert.equal(sm.getState(), ReaderState.IDLE);

    sm.startLoad('docs/guide.md', 88, 'install');
    assert.equal(sm.getState(), ReaderState.LOADING);
    assert.equal(sm.context.filePath, 'docs/guide.md');
    assert.equal(sm.context.targetLine, 88);

    sm.setReady(1200);
    assert.equal(sm.getState(), ReaderState.READY);
    assert.equal(sm.context.totalLines, 1200);

    sm.startLocating(88);
    assert.equal(sm.getState(), ReaderState.LOCATING);

    sm.setHighlighted();
    assert.equal(sm.getState(), ReaderState.HIGHLIGHTED);

    sm.setError('File read failure');
    assert.equal(sm.getState(), ReaderState.ERROR);
    assert.equal(sm.context.error, 'File read failure');

    sm.reset();
    assert.equal(sm.getState(), ReaderState.IDLE);
    assert.equal(sm.context.error, null);

    assert.equal(transitions.length, 6);
});

test('ReaderStrategyFactory correctly selects optimal viewer mode based on extension', () => {
    assert.equal(ReaderStrategyFactory.resolveMode('docs/readme.md', false), ViewerMode.MARKDOWN);
    assert.equal(ReaderStrategyFactory.resolveMode('docs/readme.markdown', false), ViewerMode.MARKDOWN);
    assert.equal(ReaderStrategyFactory.resolveMode('src/program.cs', false), ViewerMode.CODE);
    assert.equal(ReaderStrategyFactory.resolveMode('app/main.js', false), ViewerMode.CODE);
    assert.equal(ReaderStrategyFactory.resolveMode('config.json', false), ViewerMode.CODE);
    assert.equal(ReaderStrategyFactory.resolveMode('notes.txt', false), ViewerMode.TEXT);
    assert.equal(ReaderStrategyFactory.resolveMode('binary.dll', true), ViewerMode.TEXT);
});

test('ReaderStrategyFactory resolves tiered loading modes for small vs large files', () => {
    // 1MB, 2000 lines -> FULL
    assert.equal(ReaderStrategyFactory.resolveTier(1024 * 1024, 2000), ReaderTier.FULL);
    // 2MB (exact), 5000 lines -> FULL
    assert.equal(ReaderStrategyFactory.resolveTier(2 * 1024 * 1024, 5000), ReaderTier.FULL);
    // 3MB (exceeds 2MB), 3000 lines -> STREAMING
    assert.equal(ReaderStrategyFactory.resolveTier(3 * 1024 * 1024, 3000), ReaderTier.STREAMING);
    // 500KB, 8000 lines (exceeds 5000 lines) -> STREAMING
    assert.equal(ReaderStrategyFactory.resolveTier(500 * 1024, 8000), ReaderTier.STREAMING);
});

test('ReaderStrategyFactory calculateWindowRange centers window safely around targetLine', () => {
    // Normal case: target = 250 in 2000 lines with windowSize = 500
    // leadingContext = 100 => start = 150, end = 649
    const r1 = ReaderStrategyFactory.calculateWindowRange(250, 2000, 500);
    assert.equal(r1.startLine, 150);
    assert.equal(r1.endLine, 649);
    assert.equal(r1.endLine - r1.startLine + 1, 500);

    // Near beginning: target = 5 in 1000 lines
    const r2 = ReaderStrategyFactory.calculateWindowRange(5, 1000, 500);
    assert.equal(r2.startLine, 1);
    assert.equal(r2.endLine, 500);

    // Near end: target = 990 in 1000 lines
    const r3 = ReaderStrategyFactory.calculateWindowRange(990, 1000, 500);
    assert.equal(r3.startLine, 501);
    assert.equal(r3.endLine, 1000);

    // Boundary defense: totalLines small (< windowSize)
    const r4 = ReaderStrategyFactory.calculateWindowRange(10, 80, 500);
    assert.equal(r4.startLine, 1);
    assert.equal(r4.endLine, 80);
});

test('ReaderStrategyFactory expandWindowRange expands in prev and next directions', () => {
    // Upward expansion: from [501, 1000] with step 500
    const prevExp = ReaderStrategyFactory.expandWindowRange(501, 1000, 'prev', 500, 2000);
    assert.ok(prevExp);
    assert.equal(prevExp.newChunkStart, 1);
    assert.equal(prevExp.newChunkEnd, 500);
    assert.equal(prevExp.mergedStart, 1);
    assert.equal(prevExp.mergedEnd, 1000);

    // Upward expansion when already at start
    const prevBlocked = ReaderStrategyFactory.expandWindowRange(1, 500, 'prev', 500, 2000);
    assert.equal(prevBlocked, null);

    // Downward expansion: from [1, 500] with step 500
    const nextExp = ReaderStrategyFactory.expandWindowRange(1, 500, 'next', 500, 2000);
    assert.ok(nextExp);
    assert.equal(nextExp.newChunkStart, 501);
    assert.equal(nextExp.newChunkEnd, 1000);
    assert.equal(nextExp.mergedStart, 1);
    assert.equal(nextExp.mergedEnd, 1000);

    // Downward expansion when already at end
    const nextBlocked = ReaderStrategyFactory.expandWindowRange(1501, 2000, 'next', 500, 2000);
    assert.equal(nextBlocked, null);
});

test('Reader.js implements transparent isTooLarge fallback and sliding window UI', () => {
    const readerSource = fs.readFileSync(path.resolve(__dirname, '../app/views/Reader.js'), 'utf8');
    assert.ok(readerSource.includes('isStreamingWindow'), 'Reader.js must define isStreamingWindow');
    assert.ok(readerSource.includes('loadStreamingWindow'), 'Reader.js must implement loadStreamingWindow');
    assert.ok(readerSource.includes('handleLoadMore'), 'Reader.js must implement handleLoadMore for bidirectional expansion');
    assert.ok(readerSource.includes('readErr.response?.data?.isTooLarge'), 'Reader.js must handle isTooLarge transparent fallback');
});

test('Reader.js resolves dark mode contrast, text wrap, and downward scroll defects', () => {
    const readerSource = fs.readFileSync(path.resolve(__dirname, '../app/views/Reader.js'), 'utf8');
    // 门禁 1: 消除硬编码浅色背景，杜绝暗色模式白字在白底上的冲突
    assert.ok(!readerSource.includes('background: #fafafa'), 'Reader.js must not contain hardcoded #fafafa background');
    assert.ok(!readerSource.includes('background: #fff;'), 'Reader.js must not contain hardcoded #fff; background');
    assert.ok(readerSource.includes('useThemeVars'), 'Reader.js must import useThemeVars');
    assert.ok(readerSource.includes('themeVars.cardColor'), 'Reader.js must use themeVars.cardColor for container backgrounds');

    // 门禁 2: 自动换行与虚拟列表动态高度
    assert.ok(readerSource.includes('wrapLines'), 'Reader.js must provide wrapLines reactive state');
    assert.ok(readerSource.includes(':item-resizable="true"'), 'n-virtual-list must enable item-resizable');
    assert.ok(readerSource.includes('pre-wrap'), 'Reader.js must support pre-wrap style for wrapped content');

    // 门禁 3（已升级）: 视口推进闭环不得再依赖易静默失败的可选链调用
    assert.ok(!readerSource.includes('virtualListRef.value?.scrollTo'),
        'handleLoadMore must NOT use silent optional-chaining scrollTo');
    assert.ok(readerSource.includes('VirtualListAdapter'),
        'Reader.js must delegate viewport control to VirtualListAdapter');
    assert.ok(readerSource.includes('content-style="height: 100%'),
        'n-spin content layer must have a definite height to keep the percentage height chain intact');
});

test('router/index.js contains routes for FileSearch and Reader', () => {
    const routerSource = fs.readFileSync(path.resolve(__dirname, '../app/router/index.js'), 'utf8');
    assert.ok(routerSource.includes("name: 'FileSearch'"), 'FileSearch route should exist');
    assert.ok(routerSource.includes("path: 'file-search'"), 'file-search path should exist');
    assert.ok(routerSource.includes("name: 'Reader'"), 'Reader route should exist');
    assert.ok(routerSource.includes("path: 'reader'"), 'reader path should exist');
});

test('MainLayout.js includes files-and-reading menu group with Sources, FileSearch, and Reader', () => {
    const layoutSource = fs.readFileSync(path.resolve(__dirname, '../app/layouts/MainLayout.js'), 'utf8');
    assert.ok(layoutSource.includes("key: 'files-and-reading'"), 'files-and-reading menu key should exist');
    assert.ok(layoutSource.includes("key: 'Sources'"), 'Sources menu item should exist');
    assert.ok(layoutSource.includes("key: 'FileSearch'"), 'FileSearch menu item should exist');
    assert.ok(layoutSource.includes("key: 'Reader'"), 'Reader menu item should exist');
});

test('FileSearch, Sources and SourceFileManagerDrawer implement deep cross-navigation', () => {
    const fileSearchSrc = fs.readFileSync(path.resolve(__dirname, '../app/views/FileSearch.js'), 'utf8');
    assert.ok(fileSearchSrc.includes('syncFromRoute'), 'FileSearch must support syncing query params from URL');
    assert.ok(fileSearchSrc.includes('updateRouteQuery'), 'FileSearch must keep URL query in sync on search');

    const sourcesSrc = fs.readFileSync(path.resolve(__dirname, '../app/views/Sources.js'), 'utf8');
    assert.ok(sourcesSrc.includes('handleSearchSource'), 'Sources must provide handleSearchSource');
    assert.ok(sourcesSrc.includes("path: '/file-search'"), 'Sources must navigate to /file-search');

    const drawerSrc = fs.readFileSync(path.resolve(__dirname, '../app/components/SourceFileManagerDrawer.js'), 'utf8');
    assert.ok(drawerSrc.includes('goToFullSearch'), 'Drawer must support navigating to full FileSearch');
    assert.ok(drawerSrc.includes('openReaderFromSearch'), 'Drawer must support opening search matches in Reader');
});

// ---------------------------------------------------------------------------
// v2 门禁：视口推进、高度链、锚点、竞态、窗口裁剪与自动续读
// ---------------------------------------------------------------------------

test('VirtualListAdapter pure helpers compute viewport state and viewport anchor', () => {
    const nearTop = computeViewportState({ scrollTop: 0, clientHeight: 400, scrollHeight: 14000 });
    assert.equal(nearTop.isAtStart, true);
    assert.equal(nearTop.isAtEnd, false);
    assert.equal(nearTop.distanceFromEnd, 13600);

    const nearEnd = computeViewportState({ scrollTop: 13500, clientHeight: 400, scrollHeight: 14000 });
    assert.equal(nearEnd.isAtEnd, true);
    assert.equal(nearEnd.isAtStart, false);

    const rows = [
        { id: 'line-10', top: -40, bottom: -12 },
        { id: 'line-11', top: -12, bottom: 16 },
        { id: 'line-12', top: 16, bottom: 44 }
    ];
    assert.deepEqual(pickAnchorRow(rows, 0), { line: 11, delta: -12 });
    assert.equal(pickAnchorRow([], 0), null);
});

test('ReaderViewportSentinel only fires at viewport end and respects disarm', () => {
    const container = {
        scrollTop: 0,
        clientHeight: 400,
        scrollHeight: 14000,
        addEventListener() {},
        removeEventListener() {}
    };
    let endHits = 0;
    const sentinel = new ReaderViewportSentinel({
        cooldownMs: 0,
        onReachEnd: () => { endHits += 1; }
    });
    sentinel.attach(container);

    let result = sentinel.evaluate();
    assert.equal(result.snapshot.isAtEnd, false);
    assert.equal(result.triggered, null);
    assert.equal(endHits, 0);

    container.scrollTop = 13600;
    result = sentinel.evaluate();
    assert.equal(result.snapshot.isAtEnd, true);
    assert.equal(result.triggered, 'end');
    assert.equal(endHits, 1);

    // 加载期间解除武装后，即便视口仍贴底也不得连续自动加载
    sentinel.disarm();
    result = sentinel.evaluate();
    assert.equal(result.triggered, null);
    assert.equal(endHits, 1);

    sentinel.destroy();
});

test('ReaderStateMachine guards concurrent expansions with generation token', () => {
    const sm = new ReaderStateMachine();
    sm.startLoad('big.md', 1, '');
    sm.setReady(19116);
    assert.equal(sm.can('EXPAND_NEXT'), true);

    const ticket = sm.beginExpansion('next');
    assert.ok(ticket);
    assert.equal(sm.getState(), ReaderState.EXPANDING_NEXT);
    assert.equal(sm.isCurrent(ticket.generation), true);
    assert.equal(sm.can('EXPAND_NEXT'), false);
    assert.equal(sm.beginExpansion('next'), null);

    sm.finishExpansion();
    assert.equal(sm.getState(), ReaderState.READY);
    assert.equal(sm.isCurrent(ticket.generation), false);

    const second = sm.beginExpansion('next');
    sm.abortPending();
    assert.equal(sm.isCurrent(second.generation), false);
    assert.equal(sm.getState(), ReaderState.READY);
});

test('ReaderStrategyFactory.trimWindowRange keeps a bounded sliding window around the anchor', () => {
    const lines = Array.from({ length: 3000 }, (_, i) => ({ line: i + 1, text: 'x' }));
    const trimmed = ReaderStrategyFactory.trimWindowRange(lines, 1500, 2000);
    assert.equal(trimmed.lines.length, 2000);
    assert.equal(trimmed.trimmed, 1000);
    assert.ok(trimmed.startLine <= 1500 && trimmed.endLine >= 1500,
        'the anchor line must stay inside the retained window');

    const small = ReaderStrategyFactory.trimWindowRange(lines.slice(0, 500), 250, 2000);
    assert.equal(small.lines.length, 500);
    assert.equal(small.trimmed, 0);
    assert.equal(small.startLine, 1);

    // 方向偏好：向上扩充不得把刚加载的头部块立刻裁掉，向下扩充保留尾部新块
    const upward = ReaderStrategyFactory.trimWindowRange(lines, 1500, 2000, 'prev');
    assert.equal(upward.startLine, 1);
    const downward = ReaderStrategyFactory.trimWindowRange(lines, 2500, 2000, 'next');
    assert.equal(downward.endLine, 3000);

    // 锚点落在方向偏好切片之外时，必须回退到以锚点为中心
    const fallback = ReaderStrategyFactory.trimWindowRange(lines, 100, 2000, 'next');
    assert.ok(fallback.startLine <= 100 && fallback.endLine >= 100);
});

test('ReaderWindowStrategy caches line chunks with LRU eviction', () => {
    const strategy = new ReaderWindowStrategy({
        maxLines: 2000,
        cache: new LineChunkCache(2)
    });
    strategy.rememberChunk(1, 500, ['a']);
    strategy.rememberChunk(501, 1000, ['b']);
    assert.deepEqual(strategy.resolveChunk(1, 500), ['a']);

    strategy.rememberChunk(1001, 1500, ['c']);
    assert.equal(strategy.resolveChunk(501, 1000), null, 'least-recently-used chunk must be evicted');
    assert.deepEqual(strategy.resolveChunk(1, 500), ['a']);
});

test('Reader.js closes the viewport loop with height chain, adapter, anchor, sentinel and race guards', () => {
    const readerSource = fs.readFileSync(path.resolve(__dirname, '../app/views/Reader.js'), 'utf8');
    const adapterSource = fs.readFileSync(path.resolve(__dirname, '../app/services/reader/VirtualListAdapter.js'), 'utf8');

    // 高度链：n-spin 内容层 + flex item 的 min-height: 0
    assert.ok(readerSource.includes('content-style="height: 100%'), 'spin content must have definite height');
    assert.ok(readerSource.includes('min-height: 0'), 'flex children must allow shrinking');
    assert.ok(readerSource.includes('style="height: 100%; max-height: 100%"'),
        'virtual list must inherit a definite height');

    // 滚动语义：禁用 debounce 最小滚动 + 平滑定位 + 显式降级
    assert.ok(adapterSource.includes('debounce: false'), 'adapter must disable vueuc minimal-scroll');
    assert.ok(readerSource.includes("behavior: 'smooth'"), 'expansion must use smooth scrolling');
    assert.ok(readerSource.includes("behavior: 'auto'"), 'a non-animated retry path must exist');
    assert.ok(readerSource.includes('scrollToIndex(newIndex'), 'expansion must target the new chunk index');
    assert.ok(readerSource.includes('scrollToTop('), 'a scrollTop fallback must exist');

    // 锚点保持 / 竞态 / 窗口裁剪 / 自动续读
    assert.ok(readerSource.includes('captureAnchor()') && readerSource.includes('restoreAnchor(anchor)'),
        'upward expansion must preserve the viewport anchor');
    assert.ok(readerSource.includes('beginExpansion(') && readerSource.includes('isCurrent(ticket.generation)'),
        'expansion must be guarded by the state machine with generation validation');
    assert.ok(readerSource.includes('windowStrategy.trim('), 'loaded lines must be trimmed to a bounded window');
    assert.ok(readerSource.includes('ReaderViewportSentinel') && readerSource.includes(':on-scroll="handleVirtualScroll"'),
        'viewport sentinel must be attached to the virtual list scroll event');
    assert.ok(readerSource.includes('autoLoadEnabled'), 'users must be able to disable auto continuation');
});

// ---------------------------------------------------------------------------
// v3 门禁：搜索页折行、双向续读、无跳动续读
// ---------------------------------------------------------------------------

test('ContinuationPolicy separates data loading from viewport intent', () => {
    assert.equal(resolveContinuationAction({ direction: 'next', auto: true }), ContinuationAction.APPEND_KEEP_VIEWPORT);
    assert.equal(resolveContinuationAction({ direction: 'next', auto: false }), ContinuationAction.APPEND_FOCUS_NEW_CHUNK);
    assert.equal(resolveContinuationAction({ direction: 'prev', auto: true }), ContinuationAction.PREPEND_KEEP_ANCHOR);
    assert.equal(resolveContinuationAction({ direction: 'prev', auto: false }), ContinuationAction.PREPEND_KEEP_ANCHOR);

    assert.equal(shouldFocusNewChunk(ContinuationAction.APPEND_KEEP_VIEWPORT), false);
    assert.equal(shouldFocusNewChunk(ContinuationAction.APPEND_FOCUS_NEW_CHUNK), true);
    assert.equal(shouldRestoreAnchor(ContinuationAction.APPEND_KEEP_VIEWPORT), true);
    assert.equal(shouldRestoreAnchor(ContinuationAction.PREPEND_KEEP_ANCHOR), true);
    assert.equal(shouldRestoreAnchor(ContinuationAction.APPEND_FOCUS_NEW_CHUNK), false);
});

test('TextLayoutStrategy resolves wrap and no-wrap line styles', () => {
    const wrap = resolveLineStyle(TextLayout.WRAP);
    assert.equal(wrap.whiteSpace, 'pre-wrap');
    assert.equal(wrap.overflowWrap, 'anywhere');
    assert.equal(wrap.overflowX, 'hidden');

    const noWrap = resolveLineStyle(TextLayout.NO_WRAP);
    assert.equal(noWrap.whiteSpace, 'pre');
    assert.equal(noWrap.overflowWrap, 'normal');
    assert.equal(noWrap.overflowX, 'auto');

    assert.equal(resolveGutterStyle().alignSelf, 'flex-start');
    assert.equal(resolveRowStyle().alignItems, 'flex-start');
});

test('FileSearch.js provides a wrap toggle and top-aligned gutters', () => {
    const src = fs.readFileSync(path.resolve(__dirname, '../app/views/FileSearch.js'), 'utf8');
    assert.ok(src.includes('wrapLines'), 'FileSearch must expose wrapLines state');
    assert.ok(src.includes('TextLayoutStrategy'), 'FileSearch must reuse the shared layout strategy');
    assert.ok(src.includes('lineTextStyle'), 'FileSearch must bind the resolved line style');
    assert.ok(src.includes('gutterStyle'), 'FileSearch must keep the line number top-aligned');
    assert.ok(!src.includes('white-space: pre; font-size: 13px'), 'FileSearch must not hardcode nowrap');
});

test('Reader.js wires bidirectional continuation and keeps the viewport stable on auto load', () => {
    const src = fs.readFileSync(path.resolve(__dirname, '../app/views/Reader.js'), 'utf8');
    assert.ok(src.includes("handleLoadMore('prev', { auto: true })"), 'onReachStart must trigger upward continuation');
    assert.ok(src.includes('resolveContinuationAction'), 'Reader must delegate viewport intent to the policy');
    assert.ok(src.includes('shouldFocusNewChunk(action)'), 'only manual clicks may focus the new chunk');
    assert.ok(src.includes('shouldRestoreAnchor(action)'), 'auto loads must restore the anchor');
    assert.ok(src.includes("overflowAnchor = 'none'"), 'virtual list must disable native scroll anchoring');
});

// ---------------------------------------------------------------------------
// v4 门禁：邻近预取、缓存容量与折行偏好持久化
// ---------------------------------------------------------------------------

test('PrefetchPolicy triggers only near window boundaries', () => {
    assert.equal(PREFETCH_THRESHOLD_PX, 1500);
    assert.equal(shouldPrefetch({ direction: 'next', distanceFromEnd: 400, windowEnd: 500, totalLines: 2000 }), true);
    assert.equal(shouldPrefetch({ direction: 'next', distanceFromEnd: 5000, windowEnd: 500, totalLines: 2000 }), false);
    assert.equal(shouldPrefetch({ direction: 'next', distanceFromEnd: 100, windowEnd: 2000, totalLines: 2000 }), false);
    assert.equal(shouldPrefetch({ direction: 'prev', scrollTop: 300, windowStart: 501 }), true);
    assert.equal(shouldPrefetch({ direction: 'prev', scrollTop: 300, windowStart: 1 }), false);
    assert.equal(shouldPrefetch({ direction: 'prev', scrollTop: 5000, windowStart: 501 }), false);
});

test('LineChunkCache defaults to a 16-chunk window', () => {
    assert.equal(new LineChunkCache().maxChunks, 16);
});

test('LayoutPreference persists wrap toggle and falls back safely', () => {
    const store = new Map();
    const original = globalThis.localStorage;
    globalThis.localStorage = {
        getItem: (key) => (store.has(key) ? store.get(key) : null),
        setItem: (key, value) => store.set(key, String(value))
    };
    try {
        assert.equal(readLayoutPreference(LayoutPreferenceKey.READER_WRAP, true), true);
        assert.equal(writeLayoutPreference(LayoutPreferenceKey.READER_WRAP, false), true);
        assert.equal(readLayoutPreference(LayoutPreferenceKey.READER_WRAP, true), false);
        assert.equal(writeLayoutPreference(LayoutPreferenceKey.FILE_SEARCH_WRAP, true), true);
        assert.equal(readLayoutPreference(LayoutPreferenceKey.FILE_SEARCH_WRAP, false), true);

        // 存储被禁用时回退默认值且不抛异常
        globalThis.localStorage = {
            getItem() { throw new Error('denied'); },
            setItem() { throw new Error('denied'); }
        };
        assert.equal(readLayoutPreference('any.key', true), true);
        assert.equal(writeLayoutPreference('any.key', false), false);
    } finally {
        if (original === undefined) delete globalThis.localStorage;
        else globalThis.localStorage = original;
    }
});

test('Reader prefetches adjacent chunks and both pages persist wrap preference', () => {
    const readerSrc = fs.readFileSync(path.resolve(__dirname, '../app/views/Reader.js'), 'utf8');
    assert.ok(readerSrc.includes('shouldPrefetch'), 'Reader must use the prefetch policy');
    assert.ok(readerSrc.includes('prefetchChunk'), 'Reader must implement adjacent chunk prefetch');
    assert.ok(readerSrc.includes('LayoutPreferenceKey.READER_WRAP'), 'Reader wrap toggle must be persisted');

    const searchSrc = fs.readFileSync(path.resolve(__dirname, '../app/views/FileSearch.js'), 'utf8');
    assert.ok(searchSrc.includes('LayoutPreferenceKey.FILE_SEARCH_WRAP'), 'FileSearch wrap toggle must be persisted');
});

test('Sources.js treats knowledge-base connector rows as read-only', () => {
    const src = fs.readFileSync(path.resolve(__dirname, '../app/views/Sources.js'), 'utf8');
    assert.ok(src.includes("CONNECTOR_TYPE = 'KnowledgeBaseConnector'"), 'connector type must be explicit');
    assert.ok(src.includes('isConnectorRow'), 'connector rows must be detected');
    assert.ok(src.includes('去知识库管理'), 'connector rows must link to knowledge base management');
    assert.ok(src.includes('if (isConnectorRow(row))'), 'write actions must be gated by connector detection');
    assert.ok(src.indexOf('if (isConnectorRow(row))') < src.indexOf('handleDelete(row)'),
        'delete action must only live in the non-connector branch');
});
