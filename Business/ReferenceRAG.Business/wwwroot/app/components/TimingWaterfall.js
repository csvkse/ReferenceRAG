import { defineComponent as _defineComponent, ref, computed, watch } from 'vue';
import { NSpace, NText, NTag, NButton } from 'naive-ui';
import { LayoutPreferenceKey, readLayoutPreference, writeLayoutPreference } from '/app/services/reader/LayoutPreference.js';

/**
 * 全链路耗时链瀑布图。
 *
 * 数据契约（后端 ReferenceRAG.Core.Tracing.TimingChain）：
 *   { traceId, totalMs, unattributedMs, spans: [{ id, parentId, name, displayName,
 *     category, startMs, durationMs, selfMs, waitMs, coldStart, inputCount, outputCount,
 *     detail }], summary: { coldStartMs, indexBuildMs, lockWaitMs, ioMs, cpuMs,
 *     pureRetrievalMs, verdict } }
 *
 * 采用「扁平数组 + ParentId」表达树结构（对齐 OpenTelemetry / Chrome Trace 语义）：
 * 这里在客户端按 parentId 反推层级深度做缩进，不依赖后端返回嵌套结构。
 *
 * 无 timingChain 时整块不渲染，调用方自动回退到旧版阶段标签行。
 */

const SPAN_COLORS = {
    Request: '#6366f1',
    Phase: '#3b82f6',
    SubPhase: '#0ea5e9',
    Io: '#f59e0b',
    LockWait: '#ef4444',
    ModelLoad: '#a855f7',
    Network: '#14b8a6',
    Cpu: '#22c55e',
    Assembly: '#64748b'
};

const CATEGORY_LABELS = [
    { key: 'Request', label: '请求', color: SPAN_COLORS.Request },
    { key: 'Phase', label: '阶段', color: SPAN_COLORS.Phase },
    { key: 'Io', label: 'IO', color: SPAN_COLORS.Io },
    { key: 'LockWait', label: '锁等待', color: SPAN_COLORS.LockWait },
    { key: 'ModelLoad', label: '模型加载', color: SPAN_COLORS.ModelLoad },
    { key: 'Cpu', label: 'CPU', color: SPAN_COLORS.Cpu },
    { key: 'Assembly', label: '装配', color: SPAN_COLORS.Assembly }
];

const DEFAULT_VISIBLE = 14;

const component = /*@__PURE__*/ _defineComponent({
    name: 'TimingWaterfall',
    props: {
        chain: { type: Object, default: null },
        // 默认折叠：只展示汇总标签，明细需用户显式展开。
        // 逐行 Span 属于"排查时才看"的细节，默认铺开会把结果列表往下推很远。
        defaultExpanded: { type: Boolean, default: false }
    },
    setup(props) {
        const expanded = ref(readLayoutPreference(LayoutPreferenceKey.TIMING_WATERFALL_EXPANDED, props.defaultExpanded));
        // 展开后仍先给前 N 行，避免一次渲染上百行；"展开全部"再放开
        const showAll = ref(false);

        // 数据换了（新一次检索）就回到用户偏好的初始态，避免上一次点开的"全部"残留
        watch(() => props.chain, () => { showAll.value = false; });

        const toggleExpanded = () => {
            expanded.value = !expanded.value;
            if (!expanded.value) showAll.value = false;
            writeLayoutPreference(LayoutPreferenceKey.TIMING_WATERFALL_EXPANDED, expanded.value);
        };

        const toggleShowAll = () => { showAll.value = !showAll.value; };

        const hasData = computed(() => {
            const c = props.chain;
            return !!(c && Array.isArray(c.spans) && c.spans.length > 0);
        });

        const total = computed(() => Math.max(1, props.chain?.totalMs || 1));
        const summary = computed(() => props.chain?.summary || null);
        const unattributed = computed(() => props.chain?.unattributedMs || 0);
        const legend = CATEGORY_LABELS;

        const verdictType = computed(() => {
            const s = summary.value;
            if (!s) return 'default';
            if ((s.indexBuildMs || 0) > 0 || (s.coldStartMs || 0) > 0) return 'warning';
            if ((s.modelLoadMs || 0) >= total.value * 0.3) return 'warning';
            if ((s.lockWaitMs || 0) > 0 && s.lockWaitMs >= total.value * 0.3) return 'error';
            return 'success';
        });

        const rows = computed(() => {
            const c = props.chain;
            if (!c || !Array.isArray(c.spans) || c.spans.length === 0) return [];
            const byId = new Map(c.spans.map(s => [s.id, s]));
            // 先还原成树再深度优先展开：若只按 startMs 排序，并行分支的子 Span
            // 会插到兄弟节点之间，视觉上出现「子比父还大」「子挂在错的父下面」。
            const childrenOf = new Map();
            const roots = [];
            c.spans.forEach(s => {
                const p = s.parentId;
                if (p && byId.has(p)) {
                    if (!childrenOf.has(p)) childrenOf.set(p, []);
                    childrenOf.get(p).push(s);
                } else {
                    roots.push(s);
                }
            });
            const byStart = (a, b) => ((a.startMs || 0) - (b.startMs || 0)) || ((b.durationMs || 0) - (a.durationMs || 0));
            roots.sort(byStart);
            childrenOf.forEach(list => list.sort(byStart));

            const ordered = [];
            const visited = new Set();
            const walk = (span, depth) => {
                if (!span || visited.has(span.id)) return;   // 防御环形 parentId
                visited.add(span.id);
                const kids = childrenOf.get(span.id) || [];
                // 0ms 的叶子是噪音（租约、状态裁决），但 0ms 的父节点要留，否则子节点会丢失缩进
                if ((span.durationMs || 0) > 0 || kids.length > 0) ordered.push([span, depth]);
                kids.forEach(k => walk(k, depth + 1));
            };
            roots.forEach(r => walk(r, 0));
            // 兜底：parentId 指向缺失节点的 Span 不能丢
            c.spans.forEach(s => walk(s, 0));

            const max = total.value;
            return ordered.map(([s, depth]) => {
                const startPct = Math.max(0, Math.min(100, ((s.startMs || 0) / max) * 100));
                const widthPct = Math.max(0.5, Math.min(100 - startPct, ((s.durationMs || 0) / max) * 100));
                const selfMs = (s.selfMs === undefined || s.selfMs === null) ? (s.durationMs || 0) : s.selfMs;
                const color = SPAN_COLORS[s.category] || '#94a3b8';
                const parts = [s.name || ''];
                if (s.detail) parts.push(s.detail);
                parts.push('总 ' + (s.durationMs || 0) + 'ms');
                parts.push('自身 ' + selfMs + 'ms');
                if (s.waitMs) parts.push('等待 ' + s.waitMs + 'ms');
                if (s.inputCount !== null && s.inputCount !== undefined) parts.push('入 ' + s.inputCount);
                if (s.outputCount !== null && s.outputCount !== undefined) parts.push('出 ' + s.outputCount);
                return {
                    id: s.id,
                    label: s.displayName || s.name || s.id,
                    depth,
                    indent: depth * 12,
                    startPct,
                    widthPct,
                    durationMs: s.durationMs || 0,
                    selfMs,
                    waitMs: s.waitMs || 0,
                    cold: !!s.coldStart,
                    color,
                    title: parts.join(' · ')
                };
            });
        });

        const visibleRows = computed(() => {
            if (!expanded.value) return [];
            return showAll.value ? rows.value : rows.value.slice(0, DEFAULT_VISIBLE);
        });

        // 折叠时提示"里面还有多少条"，避免用户以为没有明细
        const spanCount = computed(() => rows.value.length);
        const hasMoreRows = computed(() => expanded.value && rows.value.length > DEFAULT_VISIBLE);
        const previewLimit = DEFAULT_VISIBLE;

        const rowStyle = (row) => ({
            display: 'flex',
            alignItems: 'center',
            gap: '8px',
            padding: '3px 8px',
            background: row.depth === 0 ? 'rgba(99,102,241,0.06)' : 'transparent',
            borderTop: row.depth === 0 ? '1px solid rgba(128,128,128,0.10)' : 'none'
        });

        const dotStyle = (row) => ({
            marginLeft: row.indent + 'px',
            width: '6px',
            height: '6px',
            borderRadius: '50%',
            background: row.color,
            flex: '0 0 6px'
        });

        const barStyle = (row) => ({
            position: 'absolute',
            left: row.startPct + '%',
            width: row.widthPct + '%',
            top: '0',
            bottom: '0',
            background: row.color,
            borderRadius: '3px',
            opacity: row.depth === 0 ? 0.95 : 0.72
        });

        const __returned__ = {
            expanded,
            showAll,
            toggleExpanded,
            toggleShowAll,
            hasData,
            total,
            summary,
            unattributed,
            legend,
            verdictType,
            rows,
            visibleRows,
            spanCount,
            hasMoreRows,
            previewLimit,
            rowStyle,
            dotStyle,
            barStyle,
            get NSpace() { return NSpace; },
            get NText() { return NText; },
            get NTag() { return NTag; },
            get NButton() { return NButton; }
        };
        return __returned__;
    }
});

component.template = "<div v-if=\"hasData\" style=\"margin: 12px 0 4px;\">\n  <div style=\"display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: 8px; margin-bottom: 8px;\">\n    <n-space align=\"center\" :size=\"8\">\n      <n-button text size=\"tiny\" type=\"primary\" :title=\"expanded ? '收起明细' : '展开明细'\" @click=\"toggleExpanded\">\n        <span style=\"font-size: 11px;\">{{ expanded ? '▾' : '▸' }}</span>\n      </n-button>\n      <n-text strong style=\"font-size: 13px; cursor: pointer; user-select: none;\" @click=\"toggleExpanded\">耗时链瀑布</n-text>\n      <n-tag size=\"small\" :bordered=\"false\" round>总 {{ total }}ms</n-tag>\n      <n-tag v-if=\"summary && summary.verdict\" size=\"small\" :type=\"verdictType\" :bordered=\"false\" round>{{ summary.verdict }}</n-tag>\n      <n-tag v-if=\"summary\" size=\"small\" type=\"success\" :bordered=\"false\" round>纯检索 {{ summary.pureRetrievalMs }}ms</n-tag>\n      <n-tag v-if=\"unattributed > 0\" size=\"small\" type=\"info\" :bordered=\"false\" round title=\"未能归因到任何子 Span 的时间\">未归因 {{ unattributed }}ms</n-tag>\n      <n-text v-if=\"!expanded\" depth=\"3\" style=\"font-size: 11px;\">共 {{ spanCount }} 个 Span，点击标题展开明细</n-text>\n    </n-space>\n    <n-space align=\"center\" :size=\"6\">\n      <n-tag v-if=\"summary && summary.modelLoadMs > 0\" size=\"small\" type=\"warning\" :bordered=\"false\" round title=\"向量推理/模型加载耗时（不含检索本身）\">模型 {{ summary.modelLoadMs }}ms</n-tag>\n      <n-tag v-if=\"summary && summary.indexBuildMs > 0\" size=\"small\" type=\"error\" :bordered=\"false\" round>索引构建 {{ summary.indexBuildMs }}ms</n-tag>\n      <n-tag v-if=\"summary && summary.coldStartMs > 0\" size=\"small\" type=\"warning\" :bordered=\"false\" round>冷启动 {{ summary.coldStartMs }}ms</n-tag>\n      <n-tag v-if=\"summary && summary.lockWaitMs > 0\" size=\"small\" type=\"error\" :bordered=\"false\" round>锁等待 {{ summary.lockWaitMs }}ms</n-tag>\n      <n-tag v-if=\"summary\" size=\"small\" :bordered=\"false\" round>IO {{ summary.ioMs }}ms</n-tag>\n      <n-tag v-if=\"summary\" size=\"small\" :bordered=\"false\" round>CPU {{ summary.cpuMs }}ms</n-tag>\n    </n-space>\n  </div>\n  <div v-if=\"expanded\" style=\"border: 1px solid rgba(128,128,128,0.18); border-radius: 8px; overflow: hidden;\">\n    <div v-for=\"row in visibleRows\" :key=\"row.id\" :style=\"rowStyle(row)\">\n      <div :title=\"row.title\" style=\"width: 230px; flex: 0 0 230px; display: flex; align-items: center; gap: 6px; overflow: hidden;\">\n        <span :style=\"dotStyle(row)\"></span>\n        <span style=\"font-size: 12px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis;\">{{ row.label }}</span>\n        <span v-if=\"row.cold\" style=\"font-size: 10px; color: #d97706;\">冷</span>\n        <span v-if=\"row.waitMs > 0\" style=\"font-size: 10px; color: #dc2626;\">等{{ row.waitMs }}</span>\n      </div>\n      <div style=\"flex: 1; min-width: 120px; position: relative; height: 14px; background: rgba(128,128,128,0.08); border-radius: 3px;\">\n        <div :title=\"row.title\" :style=\"barStyle(row)\"></div>\n      </div>\n      <div style=\"width: 122px; flex: 0 0 122px; text-align: right; font-size: 12px; font-variant-numeric: tabular-nums;\">\n        <b>{{ row.durationMs }}ms</b>\n        <span v-if=\"row.selfMs !== row.durationMs\" style=\"color: #94a3b8; font-size: 11px;\"> / 自身 {{ row.selfMs }}</span>\n      </div>\n    </div>\n  </div>\n  <div v-if=\"expanded\" style=\"display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: 8px; margin-top: 6px;\">\n    <n-space align=\"center\" :size=\"10\">\n      <span v-for=\"item in legend\" :key=\"item.key\" style=\"display: inline-flex; align-items: center; gap: 4px; font-size: 11px; color: #94a3b8;\">\n        <span :style=\"{ width: '8px', height: '8px', borderRadius: '2px', background: item.color, display: 'inline-block' }\"></span>{{ item.label }}\n      </span>\n    </n-space>\n    <n-space align=\"center\" :size=\"6\">\n      <n-button v-if=\"hasMoreRows\" text size=\"tiny\" type=\"primary\" @click=\"toggleShowAll\">\n        {{ showAll ? ('只看前 ' + previewLimit + ' 个') : ('展开全部 ' + spanCount + ' 个 Span') }}\n      </n-button>\n      <n-button text size=\"tiny\" @click=\"toggleExpanded\">收起明细</n-button>\n    </n-space>\n  </div>\n</div>\n";

export default component;
