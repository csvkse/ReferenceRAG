/**
 * ReaderStateMachine: 阅读器生命周期有限状态机 (State Machine Pattern)
 * 管理文本加载、大纲解析、分块扩充与视口锚定时序，杜绝渲染竞态。
 *
 * v2 升级：引入显式迁移表与 guard、请求 generation 与 AbortController，
 * 使「加载中重复点击」「切换文件后旧请求回写」等竞态可被确定性拒绝。
 * 原有 API（startLoad/setReady/startLocating/setHighlighted/setError/reset）
 * 行为保持不变，向后兼容既有门禁测试。
 */

export const ReaderState = {
    IDLE: 'IDLE',
    LOADING: 'LOADING',
    READY: 'READY',
    LOCATING: 'LOCATING',
    HIGHLIGHTED: 'HIGHLIGHTED',
    EXPANDING_NEXT: 'EXPANDING_NEXT',
    EXPANDING_PREV: 'EXPANDING_PREV',
    ANCHORING: 'ANCHORING',
    ERROR: 'ERROR'
};

/** 显式迁移表：只有列出的事件才允许改变状态 */
export const STATE_TRANSITIONS = Object.freeze({
    [ReaderState.IDLE]: { LOAD: ReaderState.LOADING },
    [ReaderState.LOADING]: { LOADED: ReaderState.READY, FAILED: ReaderState.ERROR },
    [ReaderState.READY]: {
        LOAD: ReaderState.LOADING,
        EXPAND_NEXT: ReaderState.EXPANDING_NEXT,
        EXPAND_PREV: ReaderState.EXPANDING_PREV,
        LOCATE: ReaderState.LOCATING,
        FAILED: ReaderState.ERROR
    },
    [ReaderState.HIGHLIGHTED]: {
        LOAD: ReaderState.LOADING,
        EXPAND_NEXT: ReaderState.EXPANDING_NEXT,
        EXPAND_PREV: ReaderState.EXPANDING_PREV,
        LOCATE: ReaderState.LOCATING
    },
    [ReaderState.LOCATING]: { ANCHORED: ReaderState.READY, FAILED: ReaderState.READY },
    [ReaderState.EXPANDING_NEXT]: {
        CHUNK_READY: ReaderState.ANCHORING,
        FAILED: ReaderState.READY,
        ABORT: ReaderState.READY
    },
    [ReaderState.EXPANDING_PREV]: {
        CHUNK_READY: ReaderState.ANCHORING,
        FAILED: ReaderState.READY,
        ABORT: ReaderState.READY
    },
    [ReaderState.ANCHORING]: { ANCHORED: ReaderState.READY, FAILED: ReaderState.READY },
    [ReaderState.ERROR]: { LOAD: ReaderState.LOADING, RESET: ReaderState.IDLE }
});

export class ReaderStateMachine {
    constructor(listeners = {}) {
        this.state = ReaderState.IDLE;
        this.listeners = listeners;
        this.context = {
            filePath: '',
            targetLine: 1,
            highlightQuery: '',
            error: null
        };
        this.generation = 0;
        this.controller = null;
        this.pendingDirection = '';
    }

    getState() {
        return this.state;
    }

    transition(newState, payload = {}) {
        const oldState = this.state;
        this.state = newState;
        Object.assign(this.context, payload);

        if (this.listeners.onStateChange) {
            this.listeners.onStateChange(newState, oldState, this.context);
        }
        return true;
    }

    /** guard：判断某事件在当前状态下是否合法（不改变状态） */
    can(event) {
        return Boolean(STATE_TRANSITIONS[this.state]?.[event]);
    }

    /** 受控迁移：非法事件直接返回 false */
    dispatch(event, payload = {}) {
        const next = STATE_TRANSITIONS[this.state]?.[event];
        if (!next) return false;
        return this.transition(next, payload);
    }

    /**
     * 开启一次分块扩充，返回 { generation, signal }；若当前状态不允许则返回 null。
     * 每次调用递增 generation 并替换 AbortController，供异步回调校验新鲜度。
     */
    beginExpansion(direction) {
        const event = direction === 'next' ? 'EXPAND_NEXT' : 'EXPAND_PREV';
        if (!this.can(event)) return null;
        this.generation += 1;
        this.controller = typeof AbortController !== 'undefined' ? new AbortController() : null;
        this.pendingDirection = direction;
        this.dispatch(event, { pendingDirection: direction, error: null });
        return {
            generation: this.generation,
            signal: this.controller?.signal,
            direction
        };
    }

    isCurrent(generation) {
        return generation === this.generation &&
            (this.state === ReaderState.EXPANDING_NEXT ||
             this.state === ReaderState.EXPANDING_PREV ||
             this.state === ReaderState.ANCHORING);
    }

    finishExpansion() {
        if (this.state === ReaderState.EXPANDING_NEXT || this.state === ReaderState.EXPANDING_PREV) {
            this.dispatch('CHUNK_READY');
        }
        if (this.state === ReaderState.ANCHORING) {
            this.dispatch('ANCHORED', { pendingDirection: '' });
        }
        this.pendingDirection = '';
    }

    failExpansion(error) {
        const message = typeof error === 'string' ? error : (error?.message || '分块加载失败');
        if (!this.dispatch('FAILED', { error: message })) {
            this.transition(ReaderState.READY, { error: message });
        }
        this.pendingDirection = '';
    }

    /** 切换文件 / 组件卸载时取消在途请求并作废其 generation */
    abortPending() {
        try {
            this.controller?.abort();
        } catch { /* noop */ }
        this.controller = null;
        this.generation += 1;
        this.pendingDirection = '';
        if (this.state === ReaderState.EXPANDING_NEXT ||
            this.state === ReaderState.EXPANDING_PREV ||
            this.state === ReaderState.ANCHORING) {
            this.transition(ReaderState.READY);
        }
    }

    startLoad(filePath, targetLine = 1, highlightQuery = '') {
        this.abortPending();
        this.transition(ReaderState.LOADING, {
            filePath,
            targetLine: Math.max(1, parseInt(targetLine, 10) || 1),
            highlightQuery,
            error: null
        });
    }

    setReady(totalLines = 0) {
        if (this.state !== ReaderState.LOADING) return;
        this.transition(ReaderState.READY, { totalLines });
    }

    startLocating(targetLine) {
        const line = targetLine !== undefined ? Math.max(1, parseInt(targetLine, 10) || 1) : this.context.targetLine;
        this.transition(ReaderState.LOCATING, { targetLine: line });
    }

    setHighlighted() {
        this.transition(ReaderState.HIGHLIGHTED);
    }

    setError(error) {
        this.transition(ReaderState.ERROR, { error });
    }

    reset() {
        this.abortPending();
        this.transition(ReaderState.IDLE, {
            filePath: '',
            targetLine: 1,
            highlightQuery: '',
            error: null
        });
    }
}
