/**
 * PrefetchPolicy: 邻近预取判定 (Strategy Pattern)
 *
 * 在视口距离窗口边界不足一屏半时，提前把相邻分块写入 LRU 缓存，
 * 使触底/触顶触发的正式加载直接命中缓存，消除网络等待。
 * 纯函数，便于单测。
 */

/** 距边界小于该像素值时触发预取（约 50 行） */
export const PREFETCH_THRESHOLD_PX = 1500;

export function shouldPrefetch({
    direction = 'next',
    distanceFromEnd = 0,
    scrollTop = 0,
    windowStart = 1,
    windowEnd = 1,
    totalLines = 0,
    thresholdPx = PREFETCH_THRESHOLD_PX
} = {}) {
    if (direction === 'next') {
        return windowEnd < totalLines && distanceFromEnd <= thresholdPx;
    }
    return windowStart > 1 && scrollTop <= thresholdPx;
}
