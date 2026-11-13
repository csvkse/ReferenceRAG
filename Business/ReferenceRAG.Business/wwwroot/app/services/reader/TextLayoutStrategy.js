/**
 * TextLayoutStrategy: 长行排版策略 (Strategy Pattern)
 *
 * 搜索结果页与文档阅读器共享同一套折行语义，避免两处样式漂移。
 * 折行本质是 CSS `white-space: pre-wrap`（CodeMirror 的
 * `EditorView.lineWrapping` 亦为此实现），无需引入任何第三方库。
 */

export const TextLayout = Object.freeze({
    WRAP: 'wrap',
    NO_WRAP: 'no-wrap'
});

/**
 * 行文本样式。折行时允许任意断点（应对无空格长串），
 * 不折行时保留 `pre` 与横向滚动。
 * @param {string} mode TextLayout.WRAP | TextLayout.NO_WRAP
 */
export function resolveLineStyle(mode = TextLayout.WRAP) {
    const wrap = mode === TextLayout.WRAP;
    return {
        flex: 1,
        minWidth: 0,
        whiteSpace: wrap ? 'pre-wrap' : 'pre',
        wordBreak: wrap ? 'break-word' : 'normal',
        overflowWrap: wrap ? 'anywhere' : 'normal',
        overflowX: wrap ? 'hidden' : 'auto'
    };
}

/** 行号装订线：折行后必须顶部对齐，否则行号会漂浮在段落中间 */
export function resolveGutterStyle() {
    return {
        flexShrink: 0,
        alignSelf: 'flex-start',
        paddingTop: '2px'
    };
}

/** 行容器：折行后按顶部对齐，避免行号与文本错位 */
export function resolveRowStyle() {
    return {
        display: 'flex',
        alignItems: 'flex-start'
    };
}
