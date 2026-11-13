/**
 * ReaderViewerStrategy: 文本阅读视图策略模式 (Strategy Pattern)
 * 根据不同文件后缀名与内容形态，分流至最适宜的展示与交互策略
 */

export const ViewerMode = Object.freeze({
    CODE: 'code',
    MARKDOWN: 'markdown',
    TEXT: 'text'
});

export const ReaderTier = Object.freeze({
    FULL: 'full',              // 轻量全量读取模式 (<= 2MB 且 <= 5000 行)
    STREAMING: 'streaming'     // 大文件流式滑动窗口模式 (> 2MB 或 > 5000 行)
});

export const TIER_LIMITS = Object.freeze({
    MAX_FULL_READ_BYTES: 2 * 1024 * 1024, // 2MB
    MAX_FULL_READ_LINES: 5000,
    DEFAULT_WINDOW_SIZE: 500,
    // 滑动窗口常驻上限：与后端 MaxAllowedReadLines(2000) 对齐，
    // 防止「只扩不裁」导致 fileLines 随点击次数线性增长。
    MAX_LOADED_LINES: 2000
});

export class CodeViewerStrategy {
    constructor() {
        this.name = ViewerMode.CODE;
        this.label = '代码源码';
    }

    canHandle(ext, isBinary) {
        if (isBinary) return false;
        const codeExts = new Set([
            '.cs', '.js', '.mjs', '.ts', '.jsx', '.tsx', '.vue',
            '.json', '.xml', '.yaml', '.yml', '.toml',
            '.html', '.css', '.scss', '.sql', '.py', '.go', '.rs',
            '.java', '.c', '.cpp', '.h', '.sh', '.bat', '.ps1'
        ]);
        return codeExts.has(ext);
    }
}

export class MarkdownViewerStrategy {
    constructor() {
        this.name = ViewerMode.MARKDOWN;
        this.label = 'Markdown 渲染';
    }

    canHandle(ext, isBinary) {
        if (isBinary) return false;
        return ext === '.md' || ext === '.markdown' || ext === '.mdown';
    }
}

export class PlainTextViewerStrategy {
    constructor() {
        this.name = ViewerMode.TEXT;
        this.label = '纯文本';
    }

    canHandle(_ext, isBinary) {
        return !isBinary;
    }
}

export class ReaderStrategyFactory {
    static getStrategies() {
        return [
            new MarkdownViewerStrategy(),
            new CodeViewerStrategy(),
            new PlainTextViewerStrategy()
        ];
    }

    /**
     * 根据文件路径及元数据匹配最合适的初始展示模式
     * @param {string} filePath
     * @param {boolean} isBinary
     * @returns {string} ViewerMode
     */
    static resolveMode(filePath, isBinary = false) {
        if (isBinary) return ViewerMode.TEXT;
        const ext = '.' + (filePath.split('.').pop() || '').toLowerCase();
        if (ext === '.md' || ext === '.markdown') {
            return ViewerMode.MARKDOWN;
        }
        const codeStrategy = new CodeViewerStrategy();
        if (codeStrategy.canHandle(ext, isBinary)) {
            return ViewerMode.CODE;
        }
        return ViewerMode.TEXT;
    }

    /**
     * 判断文档载入层级 (分级自适应双轨策略)
     * @param {number} sizeBytes 文件字节大小
     * @param {number} totalLines 文件总行数
     * @returns {string} ReaderTier.FULL | ReaderTier.STREAMING
     */
    static resolveTier(sizeBytes = 0, totalLines = 0) {
        if ((sizeBytes && sizeBytes > TIER_LIMITS.MAX_FULL_READ_BYTES) ||
            (totalLines && totalLines > TIER_LIMITS.MAX_FULL_READ_LINES)) {
            return ReaderTier.STREAMING;
        }
        return ReaderTier.FULL;
    }

    /**
     * 计算围绕目标行的滑动窗口 [startLine, endLine] (1-based)
     * @param {number} targetLine 目标行号
     * @param {number} totalLines 总行数
     * @param {number} windowSize 窗口大小 (默认 500)
     * @returns {{ startLine: number, endLine: number }}
     */
    static calculateWindowRange(targetLine = 1, totalLines = 1, windowSize = 500) {
        const total = Math.max(1, totalLines || 1);
        const target = Math.min(Math.max(1, targetLine || 1), total);
        const size = Math.min(Math.max(50, windowSize || 500), total);

        // 默认让 targetLine 位于窗口的前 20%~30% 位置 (预留 100 行前置上下文)
        const leadingContext = Math.min(100, Math.floor(size * 0.2));
        let start = Math.max(1, target - leadingContext);
        let end = Math.min(total, start + size - 1);

        // 如果尾部贴近 total，反向补偿 start 确保铺满 windowSize
        if (end - start + 1 < size && start > 1) {
            start = Math.max(1, end - size + 1);
        }

        return { startLine: start, endLine: end };
    }

    /**
     * 扩充当前窗口范围
     * @param {number} currentStart 当前起始行
     * @param {number} currentEnd 当前结束行
     * @param {'prev' | 'next'} direction 扩充方向
     * @param {number} step 步长 (默认 500)
     * @param {number} totalLines 总行数
     * @returns {{ newChunkStart: number, newChunkEnd: number, mergedStart: number, mergedEnd: number } | null}
     */
    static expandWindowRange(currentStart, currentEnd, direction, step = 500, totalLines = 1) {
        const total = Math.max(1, totalLines || 1);
        const curStart = Math.max(1, currentStart || 1);
        const curEnd = Math.min(total, currentEnd || curStart);

        if (direction === 'prev') {
            if (curStart <= 1) return null; // 无法再向上扩充
            const newChunkStart = Math.max(1, curStart - step);
            const newChunkEnd = curStart - 1;
            return {
                newChunkStart,
                newChunkEnd,
                mergedStart: newChunkStart,
                mergedEnd: curEnd
            };
        } else {
            if (curEnd >= total) return null; // 无法再向下扩充
            const newChunkStart = curEnd + 1;
            const newChunkEnd = Math.min(total, curEnd + step);
            return {
                newChunkStart,
                newChunkEnd,
                mergedStart: curStart,
                mergedEnd: newChunkEnd
            };
        }
    }

    /**
     * 滑动窗口裁剪：把已载入行数组限制在 maxLines 以内，并围绕锚点行保留上下文。
     * 纯函数，不修改入参；返回新的数组切片与裁剪后的窗口边界。
     * @param {Array<{line:number,text:string}>} lines 按行号升序排列的当前窗口
     * @param {number} anchorLine 视口锚点行（用户正在看的行）
     * @param {number} maxLines 常驻上限
     * @returns {{lines:Array, startLine:number, endLine:number, trimmed:number}}
     */
    static trimWindowRange(lines, anchorLine = 1, maxLines = TIER_LIMITS.MAX_LOADED_LINES, direction = 'next') {
        const source = Array.isArray(lines) ? lines : [];
        if (source.length === 0) {
            return { lines: [], startLine: 1, endLine: 0, trimmed: 0 };
        }
        const limit = Math.max(1, Math.floor(maxLines) || TIER_LIMITS.MAX_LOADED_LINES);
        if (source.length <= limit) {
            return {
                lines: source,
                startLine: source[0].line,
                endLine: source[source.length - 1].line,
                trimmed: 0
            };
        }

        let anchorIndex = source.findIndex(item => item.line >= anchorLine);
        if (anchorIndex < 0) anchorIndex = source.length - 1;

        // 按扩充方向偏好裁剪：向下保留尾部（刚加载的新块一定可见），
        // 向上保留头部（刚加载的上方块不会被立即淘汰）。
        // 若该切法会把锚点挤出窗口，则回退到以锚点为中心保留 40% 前置上下文。
        let start = direction === 'prev' ? 0 : Math.max(0, source.length - limit);
        let end = Math.min(source.length, start + limit);
        if (anchorIndex < start || anchorIndex >= end) {
            const leading = Math.floor(limit * 0.4);
            start = Math.max(0, anchorIndex - leading);
            end = Math.min(source.length, start + limit);
            if (end - start < limit) {
                start = Math.max(0, end - limit);
            }
        }

        const sliced = source.slice(start, end);
        return {
            lines: sliced,
            startLine: sliced[0].line,
            endLine: sliced[sliced.length - 1].line,
            trimmed: source.length - sliced.length
        };
    }
}
