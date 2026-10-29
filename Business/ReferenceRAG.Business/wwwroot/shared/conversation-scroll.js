/**
 * 对话视图流式生成滚动引擎与用户意图状态机
 * 借鉴参考项目: AITool (modules/conversation-scroll.js)
 * 解决大模型流式输出过程中用户无法回滚、滚动条卡死、以及流式恢复时无法自适应滚底的问题。
 */

/**
 * 判断当前滚动条位置是否处于接近底部的安全缓冲区内（默认 96px 阈值）
 */
export function isNearConversationBottom({ scrollHeight = 0, scrollTop = 0, clientHeight = 0, threshold = 96 } = {}) {
    return Number(scrollHeight) - Number(scrollTop) - Number(clientHeight) < Number(threshold);
}

/**
 * 约束计算安全的 scrollTop，防止负数或超出最大高度溢出
 */
export function clampConversationScrollTop({ scrollTop = 0, scrollHeight = 0, clientHeight = 0 } = {}) {
    const max = Math.max(0, Number(scrollHeight) - Number(clientHeight));
    return Math.max(0, Math.min(Number(scrollTop) || 0, max));
}

/**
 * 用户中断意图探测 (Pause Intent)
 * 满足以下任一条件时，判定用户意图离开最新流式输出去阅读上文：
 * 1. 鼠标滚轮向上拨动 (wheel 且 deltaY < 0)；
 * 2. 移动端/触摸板触发触摸起始 (touchstart)；
 * 3. 指针直接按在滚动条滑块/轨道区域 (pointerdown onScrollbar)。
 */
export function isConversationFollowPauseIntent({ type = '', deltaY = 0, onScrollbar = false } = {}) {
    return (type === 'wheel' && Number(deltaY) < 0)
        || type === 'touchstart'
        || (type === 'pointerdown' && Boolean(onScrollbar));
}

/**
 * 流式恢复判定 (Resume Follow Intent)
 * 当自动滚底处于暂停状态时，若同时满足：
 * 1. 正在处于流式生成或会话推送中 (sending === true)；
 * 2. 用户主动向下移动滚动条 (resumeRequested / scrollTop > lastScrollTop)；
 * 3. 滚动条已重新进入距离底部 96px 的近底缓冲区。
 * 则平滑恢复自动滚底跟随。
 */
export function shouldResumeConversationFollow({ sending = false, resumeRequested = false, scrollHeight = 0, scrollTop = 0, clientHeight = 0, threshold = 96 } = {}) {
    return Boolean(sending && resumeRequested && isNearConversationBottom({ scrollHeight, scrollTop, clientHeight, threshold }));
}

/**
 * 流式微帧是否允许应用滚底
 * 若已被用户手势暂停，严禁强行滚底；若已请求跟随或自然近底，则允许。
 */
export function shouldFollowConversationStream({ followRequested = false, followPaused = false, scrollHeight = 0, scrollTop = 0, clientHeight = 0, threshold = 96 } = {}) {
    if (followPaused) return false;
    return Boolean(followRequested) || isNearConversationBottom({ scrollHeight, scrollTop, clientHeight, threshold });
}

/**
 * 帧调度版本防竞态判定
 */
export function shouldApplyConversationStreamScroll({ scheduledRevision = 0, currentRevision = 0, followAllowed = false } = {}) {
    return Boolean(followAllowed && scheduledRevision === currentRevision);
}

/**
 * 健壮解析滚动容器元素
 * 适配 Naive UI NScrollbarInst、Vue 组件实例代理、真实 DOM 元素等挂载状态
 * 严格防范 inst.$el 为非 DOM 元素（如未挂载代理、空对象、Fragment 或文本节点）导致 querySelector is not a function 异常
 */
export function resolveScrollContainer(inst) {
    if (!inst) return null;

    // 1. Naive UI NScrollbar 暴露的 containerRef (支持 DOM 元素或 Vue Ref)
    const rawContainer = inst.containerRef?.value || inst.containerRef;
    if (rawContainer && typeof rawContainer.scrollTop === 'number') {
        return rawContainer;
    }

    // 2. inst.$el 安全访问（必须严格检查 querySelector 为 function，防止代理对象或空对象报错）
    const el = inst.$el;
    if (el) {
        if (typeof el.querySelector === 'function') {
            const sub = el.querySelector('.n-scrollbar-container');
            if (sub && typeof sub.scrollTop === 'number') return sub;
        }
        if (typeof el.scrollTop === 'number') {
            return el;
        }
    }

    // 3. 若 inst 本身即为真实 DOM 元素
    if (typeof inst.querySelector === 'function') {
        const sub = inst.querySelector('.n-scrollbar-container');
        if (sub && typeof sub.scrollTop === 'number') return sub;
    }
    if (typeof inst.scrollTop === 'number') {
        return inst;
    }

    return null;
}

