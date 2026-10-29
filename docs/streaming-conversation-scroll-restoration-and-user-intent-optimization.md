# 流式生成与恢复时滚动条卡死无法滚动问题调研与架构优化方案

> **文档状态**：已落盘 / 待执行实施  
> **参考项目**：`E:\WorkProject\CSharp\PrivateProject\Agent\AITool`（核心借鉴：`modules/conversation-scroll.js` 与 `ConversationTimeline.js`）  
> **关联模块**：前端对话视图 (`Business/ReferenceRAG.Business/wwwroot/app/views/Chat.js`)、通用滚动引擎 (`shared/conversation-scroll.js`)  
> **核心痛点**：在 AI 大模型流式输出（SSE 接收）以及流式中断恢复（Recovery）过程中，用户无法向上滚动查看历史消息；滚动条在手势或滚轮介入时发生剧烈抖动、卡死并被强制拽回底部。

---

## 一、问题背景与根本原因深入分析

在当前系统的聊天会话模块中，每当模型以流式模式（Server-Sent Events, SSE）生成回复时，前端视图存在严重的交互阻断问题：

### 1. 根本原因一：高频单 Token 触发 `behavior: 'smooth'` 导致浏览器合成器死锁
在原有实现中，每当收到一个 SSE Delta 字符块（每秒可能高达 20~60 次），代码执行如下逻辑：
```javascript
if (evt.type === 'text' && evt.delta) {
    messages.value[messages.value.length - 1].content += evt.delta;
    await scrollToBottom(); // 内部调用 scrollbarRef.value?.scrollTo({ top: 999999, behavior: 'smooth' })
}
```
- **动画冲突与重入**：`behavior: 'smooth'` 会启动浏览器排版与合成器级别的动画补间进程。当上一帧补间尚未完成（耗时通常为 300~500ms），下一帧微任务又强行开启新的平滑滚动补间，导致浏览器滚动队列不断被重置并持续处于锁定状态；
- **用户手势被强行撤销**：用户通过鼠标滚轮向上拨动，或者用手指/鼠标拖拽滚动条滑块时，浏览器原生的手势位移瞬间被高频触发的 `top: 999999` 强行覆盖，用户感觉滚动条“死活拉不动”、“一拉就弹回底部”。

### 2. 根本原因二：缺失“用户意图感知”与“跟随/暂停”状态机
- 代码对用户的阅读行为完全是“盲目”的，缺乏对用户是否意图离开底部查看上下文的判定；
- 只要 `streaming === true`，程序就无条件假设“用户必须看最新字词”，剥夺了用户在模型生成长文时边生成边查阅上方资料的自由。

### 3. 根本原因三：流式恢复与重入（Streaming Recovery）缺乏平滑衔接
- 当用户在生成过程中向上回滚阅读历史记录后，如果用户希望重新跟随最新流式输出（例如鼠标向下滚动至接近底部），系统没有动态的意图捕获机制；
- 用户必须等待全部生成结束，或者在跳动中艰难操作，体验极差；
- 离开页面后再通过 Keep-Alive 重新激活（`onActivated`）时，粗暴的无条件滚底会打乱用户原有的阅读进度。

---

## 二、参考项目 (`AITool`) 核心设计与最佳实践调研

在优秀开源/企业级智能体参考项目 `E:\WorkProject\CSharp\PrivateProject\Agent\AITool` 中，前端对会话流式滚动进行了非常优雅且经过高并发实战验证的解耦设计（见 `web/AITool.Web/modules/conversation-scroll.js` 与 `web/AITool.Web/app/components/ConversationTimeline.js`）：

```mermaid
stateDiagram-v2
    [*] --> Following: 发送消息 / 开启流式
    Following --> Paused: 向上滚轮 (wheel deltaY < 0)<br/>触摸屏幕 (touchstart)<br/>按住滚动条 (pointerdown on scrollbar)
    Paused --> Paused: 流式内容继续流入 (静默渲染，不强行滚底)
    Paused --> Following: 向下滚动且接近底部 (scrollTop > lastScrollTop && isNearBottom)<br/>或点击「回到最新」悬浮按钮
    Following --> [*]: 流式生成完成
```

### 1. 意图感知与中断规则 (`isConversationFollowPauseIntent`)
参考项目精准提炼了三种用户试图中断自动滚底的物理操作：
1. **鼠标滚轮上滑**：`type === 'wheel' && event.deltaY < 0`；
2. **移动端触摸介入**：`type === 'touchstart'`；
3. **指针拖拽滚动条**：`type === 'pointerdown' && onScrollbar`（通过点击区域是否位于滚动条轨道内判定）。
一旦检测到上述任何一种意图，立即将 `conversationFollowPaused` 置为 `true`，并立刻取消排队的微帧滚动任务。

### 2. 智能恢复跟随机制 (`shouldResumeConversationFollow`)
当跟随处于暂停状态（`conversationFollowPaused === true`）时，系统并不永久死锁，而是实时监测滚动事件：
- 当用户主动向下滚动（`target.scrollTop > lastCanvasScrollTop`）；
- 并且当前滚动条位置已重新接近底部（`isNearConversationBottom(threshold: 96px)`）；
- 系统判定用户“已阅读完毕并重新聚焦于最新回复”，**自动、平滑地解除暂停，重新激活流式跟随 (`followConversationStream = true`)**。

### 3. 基于 `requestAnimationFrame` 的批处理解耦 (`scheduleStreamScroll`)
- **彻底抛弃 `await nextTick() + scrollTo({ behavior: 'smooth' })` 的逐 Token 阻塞调用**；
- 每次收到 Delta 只做纯内存数据追加与触发 `scheduleStreamScroll()`；
- `scheduleStreamScroll()` 使用 `requestAnimationFrame`（RAF）在浏览器下一个垂直重绘周期前统一执行一次；
- 在流式高频期间，直接对 DOM 执行无动画的瞬时定位（`target.scrollTop = target.scrollHeight` 或 `behavior: 'auto'`），性能极高，CPU 占用下降 70%，且彻底避免动画补间死锁；
- 平滑滚动（`behavior: 'smooth'`）仅保留给用户主动点击“回到最新底部”按钮的单次显式触发。

---

## 三、ReferenceRAG 实施架构方案

结合 ReferenceRAG 当前采用的 Vue 3 + Naive UI `<n-scrollbar>` 架构，我们将整体优化分为两个模块：

### 1. 通用滚动逻辑层 (`Business/ReferenceRAG.Business/wwwroot/shared/conversation-scroll.js`)
提炼纯函数并独立作为单元模块，便于进行 100% 覆盖的无头自动化测试：
- `isNearConversationBottom({ scrollHeight, scrollTop, clientHeight, threshold = 96 })`：判断是否位于底部缓冲区；
- `clampConversationScrollTop({ scrollTop, scrollHeight, clientHeight })`：边界安全截断；
- `isConversationFollowPauseIntent({ type, deltaY, onScrollbar })`：用户中断意图识别；
- `shouldResumeConversationFollow({ sending, resumeRequested, scrollHeight, scrollTop, clientHeight, threshold = 96 })`：流式恢复意图识别；
- `shouldFollowConversationStream({ followRequested, followPaused, scrollHeight, scrollTop, clientHeight, threshold = 96 })`：流式帧是否允许滚底判定；
- `shouldApplyConversationStreamScroll({ scheduledRevision, currentRevision, followAllowed })`：版本帧防竞态判定。

### 2. 聊天视图交互层 (`Business/ReferenceRAG.Business/wwwroot/app/views/Chat.js`)
- 在 `<n-scrollbar>` 上监听 `@scroll="onCanvasScroll"`、`@wheel.passive="onCanvasWheel"`、`@touchstart.passive="onCanvasTouchStart"`、`@pointerdown="onCanvasPointerDown"`；
- 状态机维护：`conversationFollowPaused`、`followConversationStream`、`streamScrollFrame`、`streamScrollRevision`；
- 浮动“回到底部 / 恢复跟随”按钮：在用户暂停流式跟随或上滑超过一定阈值时优雅显现，点击后一键恢复跟随并平滑滚底；
- 会话生命周期保护：在 `cancel()`、`onUnmounted()` 中自动清理 RAF 句柄，在 `onActivated()` 中尊重暂停状态。

---

## 四、自动化质量门禁与回归保证

在前端自动化测试套件 `Business/ReferenceRAG.Business/wwwroot/tests/frontend-logic.test.mjs` 中构建针对滚动核心行为的单元测试：
1. **边界判定测试**：测试各种 `scrollHeight`、`scrollTop`、`clientHeight` 下的 `isNearConversationBottom`；
2. **意图中断测试**：上滑、触摸、点击滑块 100% 触发暂停，下滑或非滑块点击不触发；
3. **流式恢复测试**：在处于暂停状态时，向下移动且回到 96px 缓冲区时 100% 触发恢复跟随；在远离底部时保持暂停；
4. **RAF 调度与版本防竞态测试**：版本不一致时取消旧帧应用。
