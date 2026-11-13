/**
 * LayoutPreference: 排版偏好的轻量持久化 (Repository Pattern)
 *
 * 折行开关属于用户长期偏好，刷新/路由切换后应保持。仅依赖 localStorage，
 * 隐私模式或禁用存储时静默回退默认值，不阻断渲染。
 */

const PREFIX = 'referencerag.layout.';

export const LayoutPreferenceKey = Object.freeze({
    READER_WRAP: 'reader.wrapLines',
    FILE_SEARCH_WRAP: 'fileSearch.wrapLines'
});

export function readLayoutPreference(key, fallback = true) {
    try {
        const raw = globalThis.localStorage?.getItem(PREFIX + key);
        if (raw === null || raw === undefined) return fallback;
        return raw === 'true' || raw === '1';
    } catch {
        return fallback;
    }
}

export function writeLayoutPreference(key, value) {
    try {
        globalThis.localStorage?.setItem(PREFIX + key, value ? 'true' : 'false');
        return true;
    } catch {
        // 隐私模式 / 配额不足：静默忽略，不影响交互
        return false;
    }
}
