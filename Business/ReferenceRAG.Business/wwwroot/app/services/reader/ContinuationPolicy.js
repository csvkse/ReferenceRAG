/**
 * ContinuationPolicy: 续读动作策略 (Strategy Pattern)
 *
 * 把「自动/手动 × 向上/向下」的组合收敛为三个确定性动作，
 * 视图层不再用 if-else 判断「是否滚动」，从根上区分
 * 「数据加载」与「用户意图」：
 *   - 自动续读只追加数据，不移动视口（避免阅读位置被打断）；
 *   - 手动点击才把视口定位到新块首行；
 *   - 向上续读一律保持锚点（prepend 会改变上方布局，必须补偿）。
 */

export const ContinuationAction = Object.freeze({
    /** 向下自动：追加后保持当前视口，仅当触发裁剪时才做锚点补偿 */
    APPEND_KEEP_VIEWPORT: 'append-keep-viewport',
    /** 向下手动：明确跳转意图，定位到新块首行 */
    APPEND_FOCUS_NEW_CHUNK: 'append-focus-new-chunk',
    /** 向上（自动/手动）：prepend 后保持锚点行在视口中的像素位置 */
    PREPEND_KEEP_ANCHOR: 'prepend-keep-anchor'
});

/**
 * @param {{direction?: 'next'|'prev', auto?: boolean}} input
 * @returns {string} ContinuationAction
 */
export function resolveContinuationAction({ direction = 'next', auto = false } = {}) {
    if (direction === 'prev') return ContinuationAction.PREPEND_KEEP_ANCHOR;
    return auto
        ? ContinuationAction.APPEND_KEEP_VIEWPORT
        : ContinuationAction.APPEND_FOCUS_NEW_CHUNK;
}

/** 是否需要把视口定位到新块首行（仅手动向下） */
export function shouldFocusNewChunk(action) {
    return action === ContinuationAction.APPEND_FOCUS_NEW_CHUNK;
}

/** 是否需要在数据变更后恢复锚点（其余两种动作都需要） */
export function shouldRestoreAnchor(action) {
    return action !== ContinuationAction.APPEND_FOCUS_NEW_CHUNK;
}
