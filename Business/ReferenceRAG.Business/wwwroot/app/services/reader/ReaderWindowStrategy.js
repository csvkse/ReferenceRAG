/**
 * ReaderWindowStrategy: 大文件数据供给策略 (Strategy + Repository/Cache)
 *
 * 职责：
 *  1. 行块 LRU 缓存（避免回看时重复 HTTP 请求）；
 *  2. 追加 / 前置后的滑动窗口裁剪，使内存占用恒定在 MAX_LOADED_LINES；
 *  3. 对外暴露统一的数据供给端口，视图层不关心数据来自全量读取还是分块读取。
 *
 * 与 ReaderViewerStrategy 的分工：后者负责纯算术（窗口范围计算），
 * 本类负责运行时状态（缓存、裁剪、请求去重）。
 */

import { TIER_LIMITS, ReaderStrategyFactory } from './ReaderViewerStrategy.js';

export const DEFAULT_MAX_LOADED_LINES = 2000;
// 16 块 ≈ 8000 行，覆盖「向下读 4000 行后回看」的典型往返，命中即零请求
export const DEFAULT_CACHE_CHUNKS = 16;

export class LineChunkCache {
    constructor(maxChunks = DEFAULT_CACHE_CHUNKS) {
        this.maxChunks = Math.max(1, maxChunks);
        this._map = new Map();
    }

    static key(startLine, endLine) {
        return `${startLine}-${endLine}`;
    }

    get(startLine, endLine) {
        const key = LineChunkCache.key(startLine, endLine);
        if (!this._map.has(key)) return null;
        const value = this._map.get(key);
        // LRU: 命中后移到队尾
        this._map.delete(key);
        this._map.set(key, value);
        return value;
    }

    set(startLine, endLine, lines) {
        const key = LineChunkCache.key(startLine, endLine);
        if (this._map.has(key)) this._map.delete(key);
        this._map.set(key, lines);
        while (this._map.size > this.maxChunks) {
            this._map.delete(this._map.keys().next().value);
        }
        return lines;
    }

    clear() {
        this._map.clear();
    }

    get size() {
        return this._map.size;
    }
}

export class ReaderWindowStrategy {
    constructor({
        maxLines = DEFAULT_MAX_LOADED_LINES,
        cache = new LineChunkCache()
    } = {}) {
        this.maxLines = maxLines;
        this.cache = cache;
    }

    reset() {
        this.cache.clear();
    }

    /** 优先命中缓存，未命中返回 null 由调用方发起请求 */
    resolveChunk(startLine, endLine) {
        return this.cache.get(startLine, endLine);
    }

    rememberChunk(startLine, endLine, lines) {
        return this.cache.set(startLine, endLine, lines);
    }

    /**
     * 追加 / 前置后按锚点行裁剪窗口。
     * @param {Array<{line:number,text:string}>} lines 当前完整行数组
     * @param {number} anchorLine 用户当前视口锚点行（保持其前后上下文）
     * @param {'prev'|'next'} direction 本次扩充方向
     */
    trim(lines, anchorLine, direction = 'next') {
        const trimmed = ReaderStrategyFactory.trimWindowRange(
            lines, anchorLine, this.maxLines, direction
        );
        return { ...trimmed, direction };
    }

    /** 计算下一次扩充区间（委托纯算术策略，便于单测） */
    expandRange(windowStart, windowEnd, direction, step, totalLines) {
        return ReaderStrategyFactory.expandWindowRange(
            windowStart, windowEnd, direction, step, totalLines
        );
    }

    get limits() {
        return {
            maxLines: this.maxLines,
            cacheChunks: this.cache.maxChunks,
            step: TIER_LIMITS.DEFAULT_WINDOW_SIZE
        };
    }
}
