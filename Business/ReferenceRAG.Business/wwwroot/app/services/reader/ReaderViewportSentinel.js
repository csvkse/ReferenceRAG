/**
 * ReaderViewportSentinel: 视口边界观察者 (Observer Pattern)
 *
 * 设计说明：调研方案原计划使用 IntersectionObserver 首尾哨兵。但虚拟列表
 * 使用 transform: translateY() 定位可见块，列表内部没有稳定的首尾 DOM 节点，
 * 且 NVirtualList 不允许在 items 之外注入子节点。因此这里采用等价且更可靠的
 * 「滚动阈值检测器」：直接监听 .v-vl 的 scroll 事件，以 scrollTop / clientHeight /
 * scrollHeight 计算触底与触顶（等价于 TanStack Virtual 的 isAtEnd / scrollEndThreshold）。
 *
 * 遵循「仅当视口贴底才自动跟随」原则：用户上翻阅读历史时不会触发自动加载，
 * 也不会被新内容拉走视口。
 */

import { computeViewportState, SCROLL_DEFAULTS } from './VirtualListAdapter.js';

export class ReaderViewportSentinel {
    constructor({
        endThreshold = SCROLL_DEFAULTS.END_THRESHOLD,
        startThreshold = SCROLL_DEFAULTS.START_THRESHOLD,
        cooldownMs = 400,
        onReachEnd = null,
        onReachStart = null,
        onScroll = null
    } = {}) {
        this.endThreshold = endThreshold;
        this.startThreshold = startThreshold;
        this.cooldownMs = cooldownMs;
        this.onReachEnd = onReachEnd;
        this.onReachStart = onReachStart;
        this.onScroll = onScroll;
        this._container = null;
        this._handler = null;
        this._lastTriggerAt = 0;
        this._armed = true;
    }

    attach(container) {
        this.detach();
        if (!container) return;
        this._container = container;
        this._handler = () => {
            this._armed = true;
            this.evaluate();
        };
        container.addEventListener('scroll', this._handler, { passive: true });
    }

    detach() {
        if (this._container && this._handler) {
            this._container.removeEventListener('scroll', this._handler);
        }
        this._container = null;
        this._handler = null;
    }

    /** 加载期间解除武装，避免「加载完成后视口仍贴底」导致连续自动加载 */
    disarm() {
        this._armed = false;
        this._lastTriggerAt = Date.now();
    }

    arm() {
        this._armed = true;
    }

    getMetrics() {
        const el = this._container;
        if (!el) return null;
        return computeViewportState({
            scrollTop: el.scrollTop,
            clientHeight: el.clientHeight,
            scrollHeight: el.scrollHeight
        }, {
            endThreshold: this.endThreshold,
            startThreshold: this.startThreshold
        });
    }

    /** 返回视口快照；触发回调时返回 { snapshot, triggered } */
    evaluate({ force = false } = {}) {
        const snapshot = this.getMetrics();
        if (!snapshot) return null;
        if (this.onScroll) this.onScroll(snapshot);

        const now = Date.now();
        const cooledDown = now - this._lastTriggerAt >= this.cooldownMs;
        if (!force && (!this._armed || !cooledDown)) {
            return { snapshot, triggered: null };
        }

        let triggered = null;
        if (snapshot.isAtEnd && this.onReachEnd) {
            triggered = 'end';
            this._lastTriggerAt = now;
            this._armed = false;
            this.onReachEnd(snapshot);
        } else if (snapshot.isAtStart && this.onReachStart) {
            triggered = 'start';
            this._lastTriggerAt = now;
            this._armed = false;
            this.onReachStart(snapshot);
        }
        return { snapshot, triggered };
    }

    destroy() {
        this.detach();
        this.onReachEnd = null;
        this.onReachStart = null;
        this.onScroll = null;
    }
}
