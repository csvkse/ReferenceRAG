import test from 'node:test';
import assert from 'node:assert/strict';
import { BREAKPOINTS, createBreakpoints, useBreakpoints, resetBreakpoints } from '../core/responsive.js';

// G-M1：mock matchMedia 驱动断点翻转、清理与单例复用。
function createFakeWindow(initialWidth) {
  const queries = new Map();
  let width = initialWidth;
  const evaluate = (query) => {
    const m = query.match(/\(max-width:\s*(\d+)px\)/);
    return m ? width <= Number(m[1]) : false;
  };
  const win = {
    matchMedia(query) {
      if (queries.has(query)) return queries.get(query);
      const listeners = new Set();
      const mql = {
        query,
        matches: evaluate(query),
        addEventListener: (_, fn) => listeners.add(fn),
        removeEventListener: (_, fn) => listeners.delete(fn),
        listenerCount: () => listeners.size,
        dispatch(matches) {
          mql.matches = matches;
          for (const fn of listeners) fn({ matches });
        }
      };
      queries.set(query, mql);
      return mql;
    },
    setWidth(next) {
      width = next;
      for (const mql of queries.values()) mql.dispatch(evaluate(mql.query));
    },
    queries
  };
  return win;
}

test('G-M1 断点常量与 CSS 媒体查询约定一致（767/1023）', () => {
  assert.equal(BREAKPOINTS.mobile, 768);
  assert.equal(BREAKPOINTS.tablet, 1024);
});

test('G-M1 初始宽度决定响应式状态：375 手机 / 800 平板 / 1200 桌面', () => {
  const phone = createBreakpoints(createFakeWindow(375));
  assert.equal(phone.isMobile.value, true);
  assert.equal(phone.isTablet.value, true);
  assert.equal(phone.isDesktop.value, false);

  const tablet = createBreakpoints(createFakeWindow(800));
  assert.equal(tablet.isMobile.value, false);
  assert.equal(tablet.isTablet.value, true);
  assert.equal(tablet.isDesktop.value, false);

  const desktop = createBreakpoints(createFakeWindow(1200));
  assert.equal(desktop.isMobile.value, false);
  assert.equal(desktop.isTablet.value, false);
  assert.equal(desktop.isDesktop.value, true);
});

test('G-M1 matchMedia change 事件实时翻转断点（旋转屏幕/拖拽窗口）', () => {
  const win = createFakeWindow(1200);
  const bp = createBreakpoints(win);
  assert.equal(bp.isMobile.value, false);

  win.setWidth(375);
  assert.equal(bp.isMobile.value, true);
  assert.equal(bp.isTablet.value, true);
  assert.equal(bp.isDesktop.value, false);

  win.setWidth(768);
  assert.equal(bp.isMobile.value, false);
  assert.equal(bp.isTablet.value, true);

  win.setWidth(1024);
  assert.equal(bp.isTablet.value, false);
  assert.equal(bp.isDesktop.value, true);
  bp.destroy();
});

test('G-M1 destroy 移除全部监听器，杜绝泄漏', () => {
  const win = createFakeWindow(375);
  const bp = createBreakpoints(win);
  for (const mql of win.queries.values()) assert.equal(mql.listenerCount(), 1);
  bp.destroy();
  for (const mql of win.queries.values()) assert.equal(mql.listenerCount(), 0);
});

test('G-M1 兼容旧版 Safari addListener/removeListener 回退路径', () => {
  const listeners = new Set();
  const mql = {
    matches: true,
    addListener: (fn) => listeners.add(fn),
    removeListener: (fn) => listeners.delete(fn)
  };
  const win = { matchMedia: () => mql };
  const bp = createBreakpoints(win);
  assert.equal(bp.isMobile.value, true);
  // 两个断点查询共用同一 legacy mql stub → 各注册一个监听器
  assert.equal(listeners.size, 2);
  for (const fn of listeners) fn({ matches: false });
  assert.equal(bp.isMobile.value, false);
  bp.destroy();
  assert.equal(listeners.size, 0);
});

test('G-M1 useBreakpoints 单例复用 + reset 清理；无 window 环境安全降级', () => {
  resetBreakpoints();
  const win = createFakeWindow(375);
  const a = useBreakpoints(win);
  const b = useBreakpoints();
  assert.equal(a, b);
  assert.equal(a.isMobile.value, true);
  resetBreakpoints();
  for (const mql of win.queries.values()) assert.equal(mql.listenerCount(), 0);

  const c = useBreakpoints();
  assert.notEqual(c, a);
  assert.equal(c.isMobile.value, false);
  assert.equal(c.isDesktop.value, true);
  resetBreakpoints();
});