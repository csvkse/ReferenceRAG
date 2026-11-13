/**
 * VirtualListAdapter: 虚拟列表视口端口适配器 (Adapter Pattern)
 *
 * 把 Naive UI NVirtualList / vueuc VVirtualList 的实现细节（.v-vl 滚动容器、
 * scrollTo 的 index/key/debounce 语义、动态行高）收敛为稳定的业务端口。
 * 目标：消除 `virtualListRef.value?.scrollTo(...)` 这类静默失败调用，
 * 让所有滚动操作可校验、可降级、可观测。
 */

export const SCROLL_DEFAULTS = Object.freeze({
    BEHAVIOR: 'smooth',
    END_THRESHOLD: 700,
    START_THRESHOLD: 200
});

/** 纯函数：从一组行元素的几何位置中选出视口锚点，便于单测 */
export function pickAnchorRow(rows, containerTop) {
    for (const row of rows || []) {
        const line = Number(String(row.id || '').replace(/^line-/, ''));
        if (!Number.isFinite(line) || line <= 0) continue;
        if (row.bottom > containerTop + 1) {
            return { line, delta: row.top - containerTop };
        }
    }
    return null;
}

/** 纯函数：视口边界状态计算，便于单测 */
export function computeViewportState(metrics, thresholds = {}) {
    const scrollTop = Math.max(0, metrics.scrollTop || 0);
    const clientHeight = Math.max(0, metrics.clientHeight || 0);
    const scrollHeight = Math.max(0, metrics.scrollHeight || 0);
    const distanceFromEnd = Math.max(0, scrollHeight - clientHeight - scrollTop);
    const endThreshold = thresholds.endThreshold ?? SCROLL_DEFAULTS.END_THRESHOLD;
    const startThreshold = thresholds.startThreshold ?? SCROLL_DEFAULTS.START_THRESHOLD;
    return {
        scrollTop,
        clientHeight,
        scrollHeight,
        distanceFromEnd,
        isAtEnd: distanceFromEnd <= endThreshold,
        isAtStart: scrollTop <= startThreshold
    };
}

export class VirtualListAdapter {
    constructor({ getInstance, getItems = () => [], itemSize = 28 } = {}) {
        this._getInstance = getInstance;
        this._getItems = getItems;
        this.itemSize = itemSize;
        this._cachedContainer = null;
    }

    _instance() {
        const inst = this._getInstance?.();
        if (!inst || typeof inst.scrollTo !== 'function') {
            throw new Error('[VirtualListAdapter] 虚拟列表实例未就绪或缺少 scrollTo 方法');
        }
        return inst;
    }

    /** 返回真正的滚动容器（vueuc .v-vl） */
    getScrollContainer() {
        const inst = this._instance();
        let el = null;
        try {
            el = typeof inst.getScrollContainer === 'function' ? inst.getScrollContainer() : null;
        } catch (err) {
            console.warn('[VirtualListAdapter] getScrollContainer 调用失败，尝试 DOM 兜底:', err);
        }
        if (!el && this._cachedContainer?.isConnected) {
            el = this._cachedContainer;
        }
        if (!el) {
            el = document.querySelector('.v-vl');
        }
        if (!el) {
            throw new Error('[VirtualListAdapter] 无法定位滚动容器 .v-vl，请检查高度链与组件挂载状态');
        }
        this._cachedContainer = el;
        return el;
    }

    getViewport() {
        const el = this.getScrollContainer();
        return computeViewportState({
            scrollTop: el.scrollTop,
            clientHeight: el.clientHeight,
            scrollHeight: el.scrollHeight
        });
    }

    isAtEnd(threshold = SCROLL_DEFAULTS.END_THRESHOLD) {
        return this.getViewport().distanceFromEnd <= threshold;
    }

    isAtStart(threshold = SCROLL_DEFAULTS.START_THRESHOLD) {
        return this.getViewport().scrollTop <= threshold;
    }

    /**
     * 按索引定位。显式禁用 debounce（debounce:true 走「最小滚动」，
     * 会把目标项贴到视口底边，且高度链异常时直接判定「已可见」而跳过滚动）。
     * 返回滚动前后 scrollTop，供调用方校验并降级。
     */
    scrollToIndex(index, { behavior = SCROLL_DEFAULTS.BEHAVIOR } = {}) {
        const inst = this._instance();
        const items = this._getItems() || [];
        const safeIndex = Math.max(0, Math.min(index, Math.max(0, items.length - 1)));
        const before = this.getViewport().scrollTop;
        inst.scrollTo({ index: safeIndex, behavior, debounce: false });
        const after = this.getViewport().scrollTop;
        return { before, after, index: safeIndex, scrolled: Math.abs(after - before) > 1 };
    }

    /** 降级通道：直接写滚动容器 scrollTop */
    scrollToTop(offset, behavior = 'smooth') {
        const el = this.getScrollContainer();
        if (typeof el.scrollTo === 'function') {
            el.scrollTo({ top: Math.max(0, offset), behavior });
        } else {
            el.scrollTop = Math.max(0, offset);
        }
    }

    /** 记录视口首行及其相对视口顶部的像素偏移，用于向上插入后的锚点补偿 */
    captureAnchor() {
        const el = this.getScrollContainer();
        const rect = el.getBoundingClientRect();
        const rows = Array.from(el.querySelectorAll('[id^="line-"]')).map((node) => {
            const r = node.getBoundingClientRect();
            return { id: node.id, top: r.top, bottom: r.bottom };
        });
        const anchor = pickAnchorRow(rows, rect.top);
        return anchor ? { ...anchor, scrollTop: el.scrollTop } : null;
    }

    /**
     * 数据变更后恢复锚点行的视口位置。
     *
     * 实现要点：不依赖「锚点行已被渲染」——追加/裁剪后虚拟列表只渲染新视口附近的行，
     * 锚点行往往不在 DOM 中；若先 scrollToIndex 再查 DOM，会因重渲染尚未完成而查不到，
     * 触发外层二次估算补偿（实测会导致约 500 行的双重补偿）。
     * 因此这里用「索引定位（精确到行高树）+ 捕获时的相对偏移」一次完成，全程不读 DOM。
     */
    restoreAnchor(anchor) {
        if (!anchor) return false;
        const items = this._getItems() || [];
        const index = items.findIndex(item => item.line === anchor.line);
        if (index < 0) return false;
        this.scrollToIndex(index, { behavior: 'auto' });
        // scrollToIndex 已把锚点行顶对齐；再叠加捕获时该行相对视口顶部的偏移
        if (anchor.delta) {
            const el = this.getScrollContainer();
            el.scrollTop = Math.max(0, el.scrollTop + anchor.delta);
        }
        return true;
    }

    /** 诊断：确认滚动容器具备可滚动能力（高度链是否成立） */
    diagnose() {
        const viewport = this.getViewport();
        return {
            ...viewport,
            healthy: viewport.clientHeight > 0 &&
                viewport.clientHeight < 3000 &&
                viewport.scrollHeight > viewport.clientHeight + 10
        };
    }
}
