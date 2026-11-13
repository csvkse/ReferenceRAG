import { defineComponent as _defineComponent, ref, computed, watch, onMounted, onActivated, onUnmounted, nextTick } from 'vue';
import { useRouter, useRoute } from 'vue-router';
import {
    useMessage, useThemeVars, NSpace, NCard, NInput, NInputNumber, NButton, NIcon, NTag, NText,
    NRadioGroup, NRadioButton, NSpin, NAlert, NEmpty, NTooltip, NBreadcrumb,
    NBreadcrumbItem, NTabs, NTabPane, NVirtualList, NSwitch
} from 'naive-ui';
import {
    BookOutline, FolderOutline, RefreshOutline, DocumentTextOutline,
    CloseOutline, OpenOutline, ArrowDownOutline
} from '@vicons/ionicons5';
import { marked } from '/vendor/marked.js';
import { fileSystemApi, sourcesApi } from '/app/api/index.js';
import { ReaderStateMachine, ReaderState } from '/app/services/reader/ReaderStateMachine.js';
import { ReaderTier, ReaderStrategyFactory, ViewerMode, TIER_LIMITS } from '/app/services/reader/ReaderViewerStrategy.js';
import { VirtualListAdapter } from '/app/services/reader/VirtualListAdapter.js';
import { ReaderViewportSentinel } from '/app/services/reader/ReaderViewportSentinel.js';
import { ReaderWindowStrategy } from '/app/services/reader/ReaderWindowStrategy.js';
import { resolveContinuationAction, shouldFocusNewChunk, shouldRestoreAnchor } from '/app/services/reader/ContinuationPolicy.js';
import { shouldPrefetch } from '/app/services/reader/PrefetchPolicy.js';
import { LayoutPreferenceKey, readLayoutPreference, writeLayoutPreference } from '/app/services/reader/LayoutPreference.js';

const component = /*@__PURE__*/ _defineComponent({
    name: 'Reader',
    setup(__props, { expose: __expose }) {
        __expose();
        const router = useRouter();
        const route = useRoute();
        const message = useMessage();
        const themeVars = useThemeVars();

        const virtualListRef = ref(null);
        const stateMachine = new ReaderStateMachine();

        const currentPath = ref('');
        const currentSource = ref('');
        const fileName = ref('');
        const totalLines = ref(0);
        const fileSizeBytes = ref(0);
        const isMarkdownFile = ref(false);

        // 分级自适应双轨流式状态
        const isStreamingWindow = ref(false);
        const windowStartLine = ref(1);
        const windowEndLine = ref(1);
        const loadingMore = ref(false);
        const loadMoreDirection = ref(''); // 'prev' | 'next'
        const streamingTier = ref(ReaderTier.FULL);
        const autoLoadEnabled = ref(true);
        const lastLoadedHint = ref('');

        // 视图展示模式与换行控制（折行偏好跨会话持久化）
        const viewerMode = ref(ViewerMode.CODE);
        const wrapLines = ref(readLayoutPreference(LayoutPreferenceKey.READER_WRAP, true));
        watch(wrapLines, (value) => {
            writeLayoutPreference(LayoutPreferenceKey.READER_WRAP, value);
        });

        // 目标定位行与高亮词
        const targetLine = ref(1);
        const highlightQuery = ref('');
        const lineJumpInput = ref(1);
        const isLinePulsing = ref(false);

        // 状态机状态
        const loading = ref(false);
        const errorMessage = ref('');

        // 数据载荷
        const fileLines = ref([]);
        const rawContent = ref('');
        const renderedHtml = ref('');
        const headings = ref([]);
        const siblingFiles = ref([]);

        // 侧边栏折叠与激活 Tab
        const sidebarTab = ref('toc');
        const sidebarCollapsed = ref(false);

        // ------------------------------------------------------------------
        // 适配器 / 策略 / 观察者：把视口、数据供给与并发控制从视图层剥离
        // ------------------------------------------------------------------
        const viewerAdapter = new VirtualListAdapter({
            getInstance: () => virtualListRef.value,
            getItems: () => fileLines.value,
            itemSize: 28
        });
        const windowStrategy = new ReaderWindowStrategy({
            maxLines: TIER_LIMITS.MAX_LOADED_LINES
        });
        const viewportSentinel = new ReaderViewportSentinel({
            onReachEnd: () => {
                // 窗口重置 / 任意行跳转期间禁止自动续读，避免定位滚动被误判为「贴底」
                if (loading.value || !autoLoadEnabled.value || !isStreamingWindow.value || loadingMore.value) return;
                if (windowEndLine.value >= totalLines.value) return;
                if (!stateMachine.can('EXPAND_NEXT')) return;
                handleLoadMore('next', { auto: true });
            },
            onReachStart: () => {
                // 触顶自动向上续读：prepend 后由锚点补偿保持视口，不会打断阅读
                if (loading.value || !autoLoadEnabled.value || !isStreamingWindow.value || loadingMore.value) return;
                if (windowStartLine.value <= 1) return;
                if (!stateMachine.can('EXPAND_PREV')) return;
                handleLoadMore('prev', { auto: true });
            }
        });

        const attachViewportSentinel = () => {
            try {
                const container = viewerAdapter.getScrollContainer();
                // 虚拟列表用 transform 定位可见块，浏览器 scroll anchoring 的锚点
                // 选择会被 transform/尺寸变化等抑制触发器打断，且可能与手动补偿叠加。
                // 显式关闭后由 restoreAnchor 统一负责视口稳定。
                container.style.overflowAnchor = 'none';
                viewportSentinel.attach(container);
            } catch (err) {
                // 首次渲染时虚拟列表可能尚未挂载，由 loadStreamingWindow 成功后重试
                console.debug('[Reader] 视口哨兵挂载延后:', err?.message);
            }
        };

        const breadcrumbParts = computed(() => {
            if (!currentPath.value) return [];
            const parts = currentPath.value.split('/').filter(Boolean);
            return parts.map((name, idx) => ({
                name,
                path: '/' + parts.slice(0, idx + 1).join('/')
            }));
        });

        const formatBytes = (bytes) => {
            if (!bytes || bytes <= 0) return '0 B';
            const units = ['B', 'KB', 'MB', 'GB'];
            const i = Math.floor(Math.log(bytes) / Math.log(1024));
            return (bytes / Math.pow(1024, i)).toFixed(1) + ' ' + units[i];
        };

        // 平滑滚动定位目标行（统一走 VirtualListAdapter，杜绝静默失败）
        const scrollToTargetLine = async (lineNum, highlight = false) => {
            if (!lineNum || lineNum < 1) return;
            targetLine.value = lineNum;
            lineJumpInput.value = lineNum;

            // 若在流式模式下且目标行超出当前已载入的窗口范围，先拉取包含该行的窗口
            if (isStreamingWindow.value && (lineNum < windowStartLine.value || lineNum > windowEndLine.value)) {
                await loadStreamingWindow(lineNum, true);
                return;
            }

            if (viewerMode.value === ViewerMode.CODE) {
                const itemIndex = fileLines.value.findIndex(item => item.line === lineNum);
                if (itemIndex >= 0) {
                    try {
                        // 任意行跳转可能是上万行的长距离定位，采用瞬时滚动，
                        // 避免长动画期间产生大量 scroll 事件干扰视口哨兵。
                        viewerAdapter.scrollToIndex(itemIndex, { behavior: 'auto' });
                    } catch (err) {
                        console.warn('[Reader] 目标行定位失败:', err?.message);
                    }
                }
            } else if (viewerMode.value === ViewerMode.MARKDOWN) {
                const el = document.getElementById(`line-${lineNum}`);
                if (el) {
                    el.scrollIntoView({ behavior: 'smooth', block: 'center' });
                }
            }

            if (highlight) {
                isLinePulsing.value = true;
                setTimeout(() => {
                    isLinePulsing.value = false;
                }, 1500);
            }
        };

        const handleJumpInputSubmit = async () => {
            if (lineJumpInput.value > 0) {
                await scrollToTargetLine(lineJumpInput.value, true);
            }
        };

        const handleTOCClick = async (heading) => {
            if (heading?.lineNumber) {
                await scrollToTargetLine(heading.lineNumber, true);
            }
        };

        const handleSelectSibling = (file) => {
            if (!file || file.isDirectory) return;
            loadFile(file.path, currentSource.value, 1, '');
        };

        const loadSiblingFiles = async (folderPath) => {
            try {
                const res = await fileSystemApi.browse(folderPath, false);
                if (res.data?.entries) {
                    siblingFiles.value = res.data.entries.filter(e => !e.isDirectory);
                }
            } catch (e) {
                console.warn('获取同级文件失败:', e);
            }
        };

        // 大文件流式滑动窗口加载核心函数
        const loadStreamingWindow = async (centerLine = 1, forceReset = true) => {
            loading.value = true;
            errorMessage.value = '';
            viewportSentinel.disarm();
            stateMachine.abortPending();
            windowStrategy.reset();
            try {
                const effectiveSourcePath = (currentSource.value && (currentSource.value.startsWith('/') || currentSource.value.includes(':/') || currentSource.value.includes(':\\'))) ? currentSource.value : '';
                const range = ReaderStrategyFactory.calculateWindowRange(centerLine, totalLines.value, TIER_LIMITS.DEFAULT_WINDOW_SIZE);
                const res = await fileSystemApi.readRange(currentPath.value, range.startLine, range.endLine, effectiveSourcePath);
                const data = res.data;

                windowStartLine.value = data.startLine || range.startLine;
                windowEndLine.value = data.endLine || (windowStartLine.value + (data.lines?.length || 0) - 1);

                const linesArr = (data.lines || []).map((text, idx) => ({
                    line: windowStartLine.value + idx,
                    text: text
                }));

                windowStrategy.rememberChunk(windowStartLine.value, windowEndLine.value, linesArr);
                fileLines.value = linesArr;
                rawContent.value = data.content || '';
                lastLoadedHint.value = `第 ${windowStartLine.value} ~ ${windowEndLine.value} 行`;

                // 大文件流式强制代码/源码视图，防止全局 marked AST 阻塞主线程
                viewerMode.value = ViewerMode.CODE;
                stateMachine.setReady(totalLines.value);

                await nextTick();
                attachViewportSentinel();
                await scrollToTargetLine(centerLine, true);
            } catch (err) {
                console.error('流式加载窗口失败:', err);
                const msg = err.response?.data?.error || err.message || '流式读取内容失败';
                errorMessage.value = msg;
                stateMachine.setError(msg);
            } finally {
                loading.value = false;
            }
        };

        // ------------------------------------------------------------------
        // 扩充当前流式窗口：追加 → 裁剪 → 视口锚定 / 定位 → 显式反馈
        // ------------------------------------------------------------------
        const handleLoadMore = async (direction, options = {}) => {
            const { auto = false } = options;
            if (loadingMore.value || !isStreamingWindow.value) return false;
            if (auto && !autoLoadEnabled.value) return false;

            const expansion = windowStrategy.expandRange(
                windowStartLine.value,
                windowEndLine.value,
                direction,
                TIER_LIMITS.DEFAULT_WINDOW_SIZE,
                totalLines.value
            );
            if (!expansion) return false;

            // 状态机 guard：加载中 / 锚定中的重复事件被确定性拒绝
            const ticket = stateMachine.beginExpansion(direction);
            if (!ticket) return false;

            loadingMore.value = true;
            loadMoreDirection.value = direction;
            viewportSentinel.disarm();

            try {
                const effectiveSourcePath = (currentSource.value && (currentSource.value.startsWith('/') || currentSource.value.includes(':/') || currentSource.value.includes(':\\'))) ? currentSource.value : '';

                // 1. 行块缓存优先（回看时零 HTTP）
                let chunkLines = windowStrategy.resolveChunk(expansion.newChunkStart, expansion.newChunkEnd);
                if (!chunkLines) {
                    const config = ticket.signal ? { signal: ticket.signal } : {};
                    const res = await fileSystemApi.readRange(
                        currentPath.value,
                        expansion.newChunkStart,
                        expansion.newChunkEnd,
                        effectiveSourcePath,
                        config
                    );
                    chunkLines = (res.data?.lines || []).map((text, idx) => ({
                        line: expansion.newChunkStart + idx,
                        text: text
                    }));
                    windowStrategy.rememberChunk(expansion.newChunkStart, expansion.newChunkEnd, chunkLines);
                }

                // 2. 竞态校验：切换文件 / 新请求发起后，旧响应一律丢弃
                if (!stateMachine.isCurrent(ticket.generation)) {
                    console.info('[Reader] 丢弃过期的分块响应, generation =', ticket.generation);
                    stateMachine.finishExpansion();
                    return false;
                }
                if (chunkLines.length === 0) {
                    stateMachine.finishExpansion();
                    return false;
                }

                // 3. 数据变更前统一捕获锚点：向上扩充、向下追加触发裁剪时都需要补偿。
                // 自动续读由 scroll 事件同步触发，此刻虚拟列表尚未完成 scrollTop 变化后的
                // 重渲染（DOM 仍是旧视口的行），必须等一帧再捕获，否则锚点会落在旧行上。
                await new Promise(resolve => {
                    if (typeof requestAnimationFrame === 'function') {
                        requestAnimationFrame(() => resolve());
                    } else {
                        setTimeout(resolve, 0);
                    }
                });
                const anchor = viewerAdapter.captureAnchor();
                const action = resolveContinuationAction({ direction, auto });
                const firstNewLine = chunkLines[0].line;
                const lastChunkLine = chunkLines[chunkLines.length - 1].line;
                const merged = direction === 'prev'
                    ? [...chunkLines, ...fileLines.value]
                    : [...fileLines.value, ...chunkLines];

                // 4. 滑动窗口裁剪（内存恒定在 MAX_LOADED_LINES）
                const anchorLine = anchor?.line || targetLine.value || windowStartLine.value;
                const trimmed = windowStrategy.trim(merged, anchorLine, direction);
                fileLines.value = trimmed.lines;
                windowStartLine.value = trimmed.startLine;
                windowEndLine.value = trimmed.endLine;

                await nextTick();

                const loadedHint = direction === 'prev'
                    ? `第 ${trimmed.startLine} ~ ${lastChunkLine} 行`
                    : `第 ${firstNewLine} ~ ${trimmed.endLine} 行`;

                if (shouldFocusNewChunk(action)) {
                    // 5. 手动点击：明确的跳转意图，瞬时顶对齐到新块首行
                    const newIndex = fileLines.value.findIndex(item => item.line === firstNewLine);
                    let scrolled = false;
                    if (newIndex >= 0) {
                        try {
                            // 长距离 smooth 动画需数秒且连续点击会互相打断；
                            // 载入新分块属于「跳转」而非「阅读」，采用瞬时顶对齐，
                            // 视觉反馈由新块首行高亮脉冲 + toast + 最近载入标签承担。
                            const result = viewerAdapter.scrollToIndex(newIndex, { behavior: 'auto' });
                            scrolled = result.scrolled;
                            if (!scrolled) {
                                // 异常路径回退到平滑定位并等待一帧校验
                                viewerAdapter.scrollToIndex(newIndex, { behavior: 'smooth' });
                                await new Promise(resolve => setTimeout(resolve, 140));
                                scrolled = Math.abs(viewerAdapter.getViewport().scrollTop - result.before) > 1;
                            }
                            if (!scrolled) {
                                // 最终降级：直接写滚动容器 scrollTop
                                viewerAdapter.scrollToTop(newIndex * 28, 'auto');
                                scrolled = true;
                                console.warn('[Reader] scrollTo 未产生位移，已启用 scrollTop 降级通道');
                            }
                        } catch (err) {
                            console.error('[Reader] 视口定位失败:', err);
                        }
                    }

                    targetLine.value = firstNewLine;
                    lineJumpInput.value = firstNewLine;
                    isLinePulsing.value = true;
                    setTimeout(() => { isLinePulsing.value = false; }, 1500);
                    lastLoadedHint.value = loadedHint;
                    message.success(
                        `已载入第 ${firstNewLine} ~ ${trimmed.endLine} 行` +
                        (scrolled ? '，已定位至新内容' : '（视口定位失败，请手动滚动）')
                    );
                } else {
                    // 6. 自动续读 / 向上续读：只变更数据，保持视口不跳动
                    const restored = shouldRestoreAnchor(action)
                        ? viewerAdapter.restoreAnchor(anchor)
                        : false;
                    if (!restored && direction === 'prev') {
                        // 锚点行不可用时按插入行数估算补偿，避免视口整体上移
                        viewerAdapter.scrollToTop(
                            viewerAdapter.getViewport().scrollTop + chunkLines.length * 28,
                            'auto'
                        );
                        console.warn('[Reader] 锚点未能恢复，已按插入行数估算补偿');
                    }
                    lastLoadedHint.value = loadedHint;
                    if (!auto) {
                        message.success(`已向上扩充至第 ${trimmed.startLine} 行（视口已保持）`);
                    }
                }

                stateMachine.finishExpansion();
                attachViewportSentinel();
                return true;
            } catch (err) {
                if (err?.name === 'AbortError' || err?.code === 'ERR_CANCELED') {
                    console.info('[Reader] 分块请求已取消');
                    stateMachine.finishExpansion();
                    return false;
                }
                console.error('[Reader] 扩充窗口内容失败:', err);
                stateMachine.failExpansion(err);
                message.error('扩充窗口内容失败: ' + (err.response?.data?.error || err.message));
                return false;
            } finally {
                loadingMore.value = false;
                loadMoreDirection.value = '';
                // 不立即重新武装：等待用户下一次真实滚动，避免连续自动加载
                viewportSentinel.disarm();
            }
        };

        // 邻近预取：视口距边界不足一屏半时提前拉取相邻分块，正式加载直接命中 LRU
        const prefetchInFlight = new Set();
        const prefetchChunk = async (direction) => {
            if (!isStreamingWindow.value || loadingMore.value || !autoLoadEnabled.value) return;
            const step = TIER_LIMITS.DEFAULT_WINDOW_SIZE;
            let start;
            let end;
            if (direction === 'next') {
                if (windowEndLine.value >= totalLines.value) return;
                start = windowEndLine.value + 1;
                end = Math.min(totalLines.value, start + step - 1);
            } else {
                if (windowStartLine.value <= 1) return;
                end = windowStartLine.value - 1;
                start = Math.max(1, end - step + 1);
            }
            if (windowStrategy.resolveChunk(start, end)) return;
            const cacheKey = `${start}-${end}`;
            if (prefetchInFlight.has(cacheKey)) return;
            prefetchInFlight.add(cacheKey);
            try {
                const sourcePath = (currentSource.value && (currentSource.value.startsWith('/') || currentSource.value.includes(':/') || currentSource.value.includes(':\\'))) ? currentSource.value : '';
                const res = await fileSystemApi.readRange(currentPath.value, start, end, sourcePath);
                const lines = (res.data?.lines || []).map((text, idx) => ({ line: start + idx, text }));
                if (lines.length > 0) {
                    windowStrategy.rememberChunk(start, end, lines);
                }
            } catch (err) {
                // 预取失败不影响主流程，正式加载会再次请求
                console.debug('[Reader] 预取失败（忽略）:', err?.message);
            } finally {
                prefetchInFlight.delete(cacheKey);
            }
        };

        const handleVirtualScroll = () => {
            try {
                viewportSentinel.evaluate();
                const viewport = viewerAdapter.getViewport();
                const base = {
                    distanceFromEnd: viewport.distanceFromEnd,
                    scrollTop: viewport.scrollTop,
                    windowStart: windowStartLine.value,
                    windowEnd: windowEndLine.value,
                    totalLines: totalLines.value
                };
                if (shouldPrefetch({ ...base, direction: 'next' })) prefetchChunk('next');
                if (shouldPrefetch({ ...base, direction: 'prev' })) prefetchChunk('prev');
            } catch (err) {
                console.debug('[Reader] 视口检测跳过:', err?.message);
            }
        };

        // watch(immediate) 与 onMounted 会先后触发 loadFile；去重避免重复请求
        // 导致状态机在 LOADING / READY 之间抖动，进而使扩充 guard 被误拒。
        let activeLoadKey = '';
        let lastLoadStartAt = 0;

        // 核心加载函数 (自适应双轨分级管道)
        const loadFile = async (path, source = '', targetLineNum = 1, highlight = '') => {
            if (!path) return;
            const requestKey = `${path}|${source}|${targetLineNum}|${highlight}`;
            if (activeLoadKey === requestKey &&
                (loading.value || Date.now() - lastLoadStartAt < 1500)) {
                return;
            }
            activeLoadKey = requestKey;
            lastLoadStartAt = Date.now();

            // 智能路径寻址：若传入为相对路径或文件名，自动探测匹配已配置数据源
            let targetPath = path.replace(/\\/g, '/');
            if (!targetPath.startsWith('/') && !targetPath.includes(':/')) {
                try {
                    const sourcesRes = await sourcesApi.getAll();
                    const allSources = sourcesRes.data || [];
                    for (const s of allSources) {
                        const sRoot = (s.path || s.Path || '').replace(/\\/g, '/').replace(/\/$/, '');
                        if (sRoot) {
                            const candidate = `${sRoot}/${targetPath}`;
                            try {
                                const probeRes = await fileSystemApi.getMetadata(candidate);
                                if (probeRes?.data && !probeRes.data.error) {
                                    targetPath = candidate;
                                    break;
                                }
                            } catch {}
                        }
                    }
                } catch (e) {
                    console.warn('相对路径自动寻址失败:', e);
                }
            } else if (targetPath.startsWith('/') && !targetPath.substring(1).includes('/')) {
                const bareName = targetPath.substring(1);
                try {
                    const sourcesRes = await sourcesApi.getAll();
                    const allSources = sourcesRes.data || [];
                    for (const s of allSources) {
                        const sRoot = (s.path || s.Path || '').replace(/\\/g, '/').replace(/\/$/, '');
                        if (sRoot) {
                            const candidate = `${sRoot}/${bareName}`;
                            try {
                                const probeRes = await fileSystemApi.getMetadata(candidate);
                                if (probeRes?.data && !probeRes.data.error) {
                                    targetPath = candidate;
                                    break;
                                }
                            } catch {}
                        }
                    }
                } catch {}
            }

            stateMachine.abortPending();
            windowStrategy.reset();
            viewportSentinel.disarm();

            currentPath.value = targetPath;
            currentSource.value = source;
            fileName.value = currentPath.value.split('/').pop() || '';
            highlightQuery.value = highlight || '';
            targetLine.value = Math.max(1, parseInt(targetLineNum, 10) || 1);
            lineJumpInput.value = targetLine.value;

            loading.value = true;
            errorMessage.value = '';
            fileLines.value = [];
            rawContent.value = '';
            renderedHtml.value = '';
            headings.value = [];
            isStreamingWindow.value = false;
            lastLoadedHint.value = '';

            stateMachine.startLoad(currentPath.value, targetLine.value, highlightQuery.value);

            try {
                // 若 source 为源名称而非目录路径，则不作为沙箱前缀向下传递，避免路径校验越权假阳性
                const effectiveSourcePath = (source && (source.startsWith('/') || source.includes(':/') || source.includes(':\\'))) ? source : '';

                // 1. 获取文件大纲与元数据 (微秒级 O(1) 内存流式)
                const metaRes = await fileSystemApi.getMetadata(currentPath.value, effectiveSourcePath);
                const meta = metaRes.data;
                totalLines.value = meta?.totalLines || 0;
                fileSizeBytes.value = meta?.sizeBytes || 0;
                isMarkdownFile.value = Boolean(meta?.isMarkdown);
                headings.value = meta?.headings || [];

                // 2. 决策阅读层级 (分级自适应双轨策略)
                const tier = ReaderStrategyFactory.resolveTier(fileSizeBytes.value, totalLines.value);
                streamingTier.value = tier;

                if (tier === ReaderTier.STREAMING) {
                    // 大文件：自动进入流式滑动窗口模式，杜绝全量加载撞墙
                    isStreamingWindow.value = true;
                    await loadStreamingWindow(targetLine.value, true);
                } else {
                    // 小文件：全量加载模式，保留即时 Markdown 富文本与完整行数组
                    viewerMode.value = ReaderStrategyFactory.resolveMode(currentPath.value, meta?.isBinary);

                    try {
                        const textRes = await fileSystemApi.readText(currentPath.value, effectiveSourcePath);
                        const content = textRes.data?.content || '';
                        rawContent.value = content;

                        const rawSplit = content.split(/\r?\n/);
                        totalLines.value = rawSplit.length;

                        fileLines.value = rawSplit.map((text, idx) => ({
                            line: idx + 1,
                            text: text
                        }));

                        // Markdown 模式富文本编译
                        if (isMarkdownFile.value && marked) {
                            try {
                                renderedHtml.value = marked.parse(content);
                            } catch (e) {
                                console.warn('Markdown 解析失败，降级为源码模式:', e);
                                viewerMode.value = ViewerMode.CODE;
                            }
                        }

                        stateMachine.setReady(totalLines.value);
                        await nextTick();
                        attachViewportSentinel();
                        await scrollToTargetLine(targetLine.value, true);
                    } catch (readErr) {
                        // 自适应防御兜底：若后端触发 isTooLarge 拦截，透明降级至大文件流式窗口，杜绝红字报错
                        if (readErr.response?.data?.isTooLarge) {
                            console.info('检测到文件超过全量读取限制，透明降级为大文件流式阅读');
                            isStreamingWindow.value = true;
                            await loadStreamingWindow(targetLine.value, true);
                        } else {
                            throw readErr;
                        }
                    }
                }

                // 异步获取所在目录的同级文件列表
                const parentDir = currentPath.value.substring(0, currentPath.value.lastIndexOf('/'));
                if (parentDir) {
                    loadSiblingFiles(parentDir);
                }
            } catch (err) {
                console.error('加载文件失败:', err);
                const msg = err.response?.data?.error || err.message || '加载文档内容失败';
                errorMessage.value = msg;
                stateMachine.setError(msg);
            } finally {
                loading.value = false;
            }
        };

        const handleGoBack = () => {
            if (window.history.length > 1) {
                router.back();
            } else {
                router.push({ name: 'FileSearch' });
            }
        };

        const handleDownload = () => {
            if (!currentPath.value) return;
            const effectiveSourcePath = (currentSource.value && (currentSource.value.startsWith('/') || currentSource.value.includes(':/') || currentSource.value.includes(':\\'))) ? currentSource.value : '';
            const url = fileSystemApi.getDownloadUrl(currentPath.value, effectiveSourcePath);
            window.open(url, '_blank');
        };

        // 监听路由参数变化
        watch(() => route.query, (newQuery) => {
            if (route.name === 'Reader' && newQuery?.path) {
                loadFile(newQuery.path, newQuery.source || '', newQuery.line || 1, newQuery.highlight || '');
            }
        }, { immediate: true });

        onMounted(() => {
            if (route.query?.path) {
                loadFile(route.query.path, route.query.source || '', route.query.line || 1, route.query.highlight || '');
            }
        });

        onActivated(() => {
            if (route.query?.path && route.query.path !== currentPath.value) {
                loadFile(route.query.path, route.query.source || '', route.query.line || 1, route.query.highlight || '');
            } else {
                nextTick(() => attachViewportSentinel());
            }
        });

        onUnmounted(() => {
            stateMachine.abortPending();
            viewportSentinel.destroy();
        });

        const __returned__ = {
            themeVars, wrapLines,
            virtualListRef, currentPath, currentSource, fileName, totalLines, fileSizeBytes,
            isMarkdownFile, viewerMode, targetLine, highlightQuery, lineJumpInput,
            isLinePulsing, loading, errorMessage, fileLines, rawContent, renderedHtml,
            headings, siblingFiles, sidebarTab, sidebarCollapsed, breadcrumbParts,
            formatBytes, scrollToTargetLine, handleJumpInputSubmit, handleTOCClick,
            handleSelectSibling, loadFile, handleGoBack, handleDownload,
            isStreamingWindow, windowStartLine, windowEndLine, loadingMore,
            loadMoreDirection, handleLoadMore, handleVirtualScroll,
            autoLoadEnabled, lastLoadedHint,
            get BookOutline() { return BookOutline; },
            get FolderOutline() { return FolderOutline; },
            get RefreshOutline() { return RefreshOutline; },
            get DocumentTextOutline() { return DocumentTextOutline; },
            get CloseOutline() { return CloseOutline; },
            get OpenOutline() { return OpenOutline; },
            get ArrowDownOutline() { return ArrowDownOutline; }
        };
        return __returned__;
    }
});

component.template = `
  <div style="display: flex; flex-direction: column; height: calc(100vh - 100px); gap: 12px">
    <!-- 顶部工作台操作与状态栏 -->
    <n-card size="small" style="flex-shrink: 0">
      <div style="display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: 8px">
        <!-- 导航路径与返回 -->
        <n-space align="center" :size="12">
          <n-button size="small" secondary @click="handleGoBack">
            ← 返回
          </n-button>
          <n-icon :component="DocumentTextOutline" size="20" color="#18a058" />
          <n-text strong style="font-size: 15px">{{ fileName || '文本阅读器' }}</n-text>
          <n-tag v-if="currentSource" size="small" type="info" :bordered="false">{{ currentSource }}</n-tag>
          <n-tag size="small" round>{{ totalLines }} 行</n-tag>
          <n-tag size="small" :bordered="false">{{ formatBytes(fileSizeBytes) }}</n-tag>
          <n-tag v-if="isStreamingWindow" size="small" type="warning" round>大文件流式模式</n-tag>
        </n-space>

        <!-- 行定位控制与视图切换 -->
        <n-space align="center" :size="12">
          <!-- 行号快速跳转 -->
          <n-space align="center" :size="4">
            <n-text depth="3" style="font-size: 13px">跳转行:</n-text>
            <n-input-number
              v-model:value="lineJumpInput"
              :min="1"
              :max="totalLines || 1"
              size="small"
              style="width: 88px"
              @keydown.enter="handleJumpInputSubmit"
            />
            <n-button size="small" type="primary" secondary @click="handleJumpInputSubmit">
              前往
            </n-button>
          </n-space>

          <!-- 自动换行切换 -->
          <n-space align="center" :size="4">
            <n-switch v-model:value="wrapLines" size="small">
              <template #checked>自动换行</template>
              <template #unchecked>单行不折行</template>
            </n-switch>
          </n-space>

          <!-- 模式切换: 代码源码 / Markdown 富文本 (流式模式下锁定为源码视图) -->
          <n-radio-group v-if="isMarkdownFile && !isStreamingWindow" v-model:value="viewerMode" size="small">
            <n-radio-button value="code" label="源码模式 (精确行号)" />
            <n-radio-button value="markdown" label="渲染模式 (富文本)" />
          </n-radio-group>

          <!-- 下载文件 -->
          <n-button size="small" text @click="handleDownload" :disabled="!currentPath">
            <template #icon><n-icon :component="OpenOutline" /></template>
            下载文件
          </n-button>
        </n-space>
      </div>
    </n-card>

    <!-- 错误提示 -->
    <n-alert v-if="errorMessage && !loading" type="error" title="读取文件出错">
      {{ errorMessage }}
    </n-alert>

    <!-- 主工作区：左侧大纲/同级目录 + 右侧虚拟滚动正文 -->
    <div style="display: flex; flex: 1; min-height: 0; gap: 12px">
      <!-- 左侧边栏 (目录大纲与同级文件) -->
      <n-card
        size="small"
        style="width: 280px; flex-shrink: 0; display: flex; flex-direction: column; overflow: hidden"
        content-style="padding: 8px; display: flex; flex-direction: column; height: 100%"
      >
        <n-tabs v-model:value="sidebarTab" size="small" justify-content="space-evenly">
          <n-tab-pane name="toc" tab="文档大纲 (TOC)">
            <div style="overflow-y: auto; height: calc(100vh - 240px); padding-right: 4px">
              <n-empty v-if="headings.length === 0" description="本文档无层级大纲" size="small" style="margin-top: 30px" />
              <div v-else style="display: flex; flex-direction: column; gap: 4px">
                <div
                  v-for="(h, idx) in headings"
                  :key="idx"
                  :style="{
                    padding: '4px 8px',
                    paddingLeft: Math.min(28, (h.level - 1) * 12 + 6) + 'px',
                    borderRadius: '4px',
                    cursor: 'pointer',
                    fontSize: '12px',
                    whiteSpace: 'nowrap',
                    overflow: 'hidden',
                    textOverflow: 'ellipsis',
                    background: targetLine >= h.lineNumber ? 'rgba(24, 160, 88, 0.08)' : 'transparent',
                    color: targetLine >= h.lineNumber ? '#18a058' : 'inherit'
                  }"
                  @click="handleTOCClick(h)"
                  :title="h.title + ' (第 ' + h.lineNumber + ' 行)'"
                >
                  <span style="opacity: 0.6; font-size: 11px; margin-right: 4px">L{{ h.lineNumber }}</span>
                  <span>{{ h.title }}</span>
                </div>
              </div>
            </div>
          </n-tab-pane>

          <n-tab-pane name="files" tab="同级文件">
            <div style="overflow-y: auto; height: calc(100vh - 240px); padding-right: 4px">
              <n-empty v-if="siblingFiles.length === 0" description="未发现同级文件" size="small" style="margin-top: 30px" />
              <div v-else style="display: flex; flex-direction: column; gap: 4px">
                <div
                  v-for="sFile in siblingFiles"
                  :key="sFile.path"
                  :style="{
                    padding: '6px 8px',
                    borderRadius: '4px',
                    cursor: 'pointer',
                    fontSize: '12px',
                    whiteSpace: 'nowrap',
                    overflow: 'hidden',
                    textOverflow: 'ellipsis',
                    background: sFile.path === currentPath ? 'rgba(32, 128, 240, 0.1)' : 'transparent',
                    color: sFile.path === currentPath ? '#2080f0' : 'inherit'
                  }"
                  @click="handleSelectSibling(sFile)"
                  :title="sFile.name"
                >
                  📄 {{ sFile.name }}
                </div>
              </div>
            </div>
          </n-tab-pane>
        </n-tabs>
      </n-card>

      <!-- 右侧正文视口 -->
      <n-card
        size="small"
        style="flex: 1; min-width: 0; display: flex; flex-direction: column"
        content-style="padding: 0; height: 100%; display: flex; flex-direction: column; overflow: hidden"
      >
        <!--
          高度链关键修复：n-spin 内容层默认没有确定高度，会导致内部
          height:100% 逐级退化为 auto，使 .v-vl 虚拟列表退化为全量渲染
          且 scrollTo 的最小滚动判定「目标已可见」而跳过滚动。
        -->
        <n-spin
          :show="loading"
          style="height: 100%"
          content-style="height: 100%; display: flex; flex-direction: column; min-height: 0"
        >
          <!-- 大文件流式窗口动态工具栏 -->
          <div
            v-if="isStreamingWindow"
            style="background: rgba(32, 128, 240, 0.08); border-bottom: 1px solid rgba(32, 128, 240, 0.2); padding: 6px 14px; display: flex; align-items: center; justify-content: space-between; font-size: 12px; flex-shrink: 0;"
          >
            <n-space align="center" :size="8">
              <n-tag size="small" type="info" round>流式窗口</n-tag>
              <n-text depth="2">
                已载入第 <b>{{ windowStartLine }}</b> ~ <b>{{ windowEndLine }}</b> 行（共 {{ totalLines }} 行 / {{ formatBytes(fileSizeBytes) }}）
              </n-text>
              <n-tag v-if="lastLoadedHint" size="tiny" type="success" :bordered="false">
                最近载入 {{ lastLoadedHint }}
              </n-tag>
              <n-tag v-if="isMarkdownFile" size="tiny" type="warning" :bordered="false">
                源码模式 (已禁用全局富文本以防卡死)
              </n-tag>
            </n-space>
            <n-space align="center" :size="8">
              <n-text depth="3" style="font-size: 12px">自动续读</n-text>
              <n-switch v-model:value="autoLoadEnabled" size="small" />
              <n-button
                size="tiny"
                secondary
                :disabled="windowStartLine <= 1 || loadingMore"
                :loading="loadingMore && loadMoreDirection === 'prev'"
                @click="handleLoadMore('prev')"
              >
                ↑ 向上扩充 500 行
              </n-button>
              <n-button
                size="tiny"
                secondary
                :disabled="windowEndLine >= totalLines || loadingMore"
                :loading="loadingMore && loadMoreDirection === 'next'"
                @click="handleLoadMore('next')"
              >
                ↓ 向下扩充 500 行
              </n-button>
            </n-space>
          </div>

          <!-- 空状态 -->
          <div v-if="!currentPath && !loading" style="display: flex; align-items: center; justify-content: center; height: 100%">
            <n-empty description="请从文本搜索或文件源管理选择文件阅读" />
          </div>

          <!-- 源码/代码模式 (基于 NVirtualList 虚拟滚动，极速无卡顿，主题自适应) -->
          <div
            v-else-if="viewerMode === 'code'"
            style="flex: 1 1 auto; min-height: 0; width: 100%; overflow: hidden; font-family: 'Cascadia Code', Consolas, Monaco, monospace"
            :style="{
              background: themeVars.cardColor,
              color: themeVars.textColorBase
            }"
          >
            <n-virtual-list
              ref="virtualListRef"
              style="height: 100%; max-height: 100%"
              :items="fileLines"
              :item-size="28"
              :item-resizable="true"
              key-field="line"
              :on-scroll="handleVirtualScroll"
            >
              <template #default="{ item }">
                <div
                  :id="'line-' + item.line"
                  :style="{
                    display: 'flex',
                    alignItems: 'flex-start',
                    minHeight: '26px',
                    height: 'auto',
                    lineHeight: '1.6',
                    fontSize: '13px',
                    background: item.line === targetLine && isLinePulsing
                      ? 'rgba(255, 220, 40, 0.35)'
                      : (item.line === targetLine ? 'rgba(24, 160, 88, 0.12)' : 'transparent'),
                    borderLeft: item.line === targetLine ? '3px solid #f0a020' : '3px solid transparent',
                    transition: 'background 0.3s ease'
                  }"
                >
                  <!-- 行号装订线 -->
                  <div
                    :style="{
                      width: '56px',
                      flexShrink: 0,
                      textAlign: 'right',
                      paddingRight: '14px',
                      color: themeVars.textColor3,
                      userSelect: 'none',
                      fontSize: '12px',
                      background: 'rgba(128, 128, 128, 0.08)',
                      alignSelf: 'stretch',
                      paddingTop: '2px'
                    }"
                  >
                    {{ item.line }}
                  </div>

                  <!-- 行代码/文本内容 -->
                  <div
                    :style="{
                      flex: 1,
                      minWidth: 0,
                      padding: '2px 14px',
                      color: themeVars.textColorBase,
                      whiteSpace: wrapLines ? 'pre-wrap' : 'pre',
                      wordBreak: 'break-word',
                      overflowWrap: 'anywhere',
                      overflowX: wrapLines ? 'hidden' : 'auto'
                    }"
                  >
                    {{ item.text }}
                  </div>
                </div>
              </template>
            </n-virtual-list>
          </div>

          <!-- Markdown 富文本渲染模式 -->
          <div
            v-else-if="viewerMode === 'markdown'"
            :style="{
              flex: '1 1 auto',
              minHeight: 0,
              overflowY: 'auto',
              padding: '24px 36px',
              background: themeVars.cardColor,
              color: themeVars.textColorBase,
              lineHeight: 1.7
            }"
          >
            <div v-html="renderedHtml" class="markdown-preview-content"></div>
          </div>
        </n-spin>
      </n-card>
    </div>
  </div>
`;

export default component;
