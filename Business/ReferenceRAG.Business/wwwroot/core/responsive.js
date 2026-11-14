import { ref, computed, readonly } from 'vue';

// JS 与 CSS 媒体查询共用同一组断点常量（G-M5 静态门禁锁定一致性）。
export const BREAKPOINTS = Object.freeze({ mobile: 768, tablet: 1024 });

/**
 * 断点响应式基座（借鉴 VueUse useBreakpoints 的 smaller() 语义，零依赖手写）。
 * isMobile: <768px；isTablet: <1024px（含手机）；isDesktop: >=1024px。
 * 可注入 fake window 供 node --test 驱动（G-M1）。
 */
export function createBreakpoints(win) {
  const target = win || (typeof window !== 'undefined' ? window : null);
  const mobile = ref(false);
  const tablet = ref(false);
  const bindings = [];

  if (target && typeof target.matchMedia === 'function') {
    const bind = (query, state) => {
      const mql = target.matchMedia(query);
      state.value = Boolean(mql.matches);
      const handler = (event) => { state.value = Boolean(event.matches); };
      if (typeof mql.addEventListener === 'function') mql.addEventListener('change', handler);
      else if (typeof mql.addListener === 'function') mql.addListener(handler);
      bindings.push({ mql, handler });
    };
    bind(`(max-width: ${BREAKPOINTS.mobile - 1}px)`, mobile);
    bind(`(max-width: ${BREAKPOINTS.tablet - 1}px)`, tablet);
  }

  const destroy = () => {
    for (const { mql, handler } of bindings) {
      if (typeof mql.removeEventListener === 'function') mql.removeEventListener('change', handler);
      else if (typeof mql.removeListener === 'function') mql.removeListener(handler);
    }
    bindings.length = 0;
  };

  return {
    isMobile: readonly(mobile),
    isTablet: readonly(tablet),
    isDesktop: computed(() => !tablet.value),
    destroy
  };
}

let singleton = null;

export function useBreakpoints(win) {
  if (!singleton) singleton = createBreakpoints(win);
  return singleton;
}

export function resetBreakpoints() {
  if (singleton) singleton.destroy();
  singleton = null;
}