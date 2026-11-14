import { reactive, computed } from 'vue';
import { isActiveStatus, isTerminalStatus, statusLabel, phaseLabel } from './reindex-model.js';

/**
 * 重构索引抽屉显式状态机（状态模式，G-M2）。
 * 状态：idle / enqueuing / active / cancelling / terminal / rollingBack。
 * 转移表缺省 = 非法（显式拒绝）；模板按钮可用性全部由 state + job + generation 派生，
 * 消灭 reindexing/reindexCancelling/reindexRollingBack/reindexActive 四布尔耦合。
 * 载体 reactive({ state, job, generation })，job 为展示快照（活跃期由 JobCenter 推送同步，终态后保留）。
 */
export const REINDEX_STATES = Object.freeze(['idle', 'enqueuing', 'active', 'cancelling', 'terminal', 'rollingBack']);

const TRANSITIONS = Object.freeze({
  idle:        { ENQUEUE: 'enqueuing' },
  // 推送可能先于 POST 响应到达：enqueuing 收到 PROGRESS/TERMINAL 直接收敛，避免竞态卡死
  enqueuing:   { ENQUEUED: 'active', ENQUEUE_409: 'active', ENQUEUE_FAIL: 'idle', PROGRESS: 'active', TERMINAL: 'terminal' },
  active:      { PROGRESS: 'active', ENQUEUED: 'active', TERMINAL: 'terminal', CANCEL: 'cancelling', CLOSE: 'active' },
  cancelling:  { CANCEL_ACK: 'active', CANCEL_FAIL: 'active', CANCEL_404: 'terminal', PROGRESS: 'cancelling', TERMINAL: 'terminal', CLOSE: 'cancelling' },
  terminal:    { ROLLBACK: 'rollingBack', ENQUEUE: 'enqueuing', CLOSE: 'terminal' },
  rollingBack: { ROLLBACK_DONE: 'idle', ROLLBACK_FAIL: 'terminal' }
});

const GUARDS = Object.freeze({
  ROLLBACK: (ctx) => Boolean(ctx?.generation?.canRollback),
  CANCEL: (ctx) => Boolean(ctx?.job?.id || ctx?.job?.jobId)
});

/** 纯转移函数：非法组合或守卫不满足 → { ok:false, state 不变, reason } */
export function nextState(state, event, ctx = {}) {
  const next = TRANSITIONS[state]?.[event];
  if (!next) return { ok: false, state, reason: `非法转移: ${state} × ${event}` };
  const guard = GUARDS[event];
  if (guard && !guard(ctx)) return { ok: false, state, reason: `守卫拒绝: ${event}` };
  return { ok: true, state: next, reason: null };
}

const STATUS_LABELS = {
  Queued: '排队中', Running: '重构进行中', WaitingForInput: '等待确认', Cancelling: '正在取消',
  Succeeded: '重构完成', PartiallySucceeded: '部分完成', Failed: '重构失败', Cancelled: '已取消'
};
const PHASE_LABELS = { None: '就绪', Planning: '规划准备', Embedding: '向量编码', Validating: '门禁校验', Publishing: '原子切换' };
const PHASE_ORDER = ['Planning', 'Embedding', 'Publishing'];
const PHASE_STEP_LABELS = { Planning: '规划准备', Embedding: '向量编码', Publishing: '原子切换' };

export function createReindexDrawerMachine() {
  const carrier = reactive({ state: 'idle', job: null, generation: null });

  const ctxOf = (payload = {}) => ({
    job: payload.job !== undefined ? payload.job : carrier.job,
    generation: payload.generation !== undefined ? payload.generation : carrier.generation
  });

  const send = (event, payload = {}) => {
    const result = nextState(carrier.state, event, ctxOf(payload));
    if (!result.ok) return result;
    carrier.state = result.state;
    if (payload.job !== undefined) carrier.job = payload.job;
    if (payload.generation !== undefined) carrier.generation = payload.generation;
    if (event === 'ENQUEUE' || event === 'ROLLBACK_DONE') carrier.job = null;
    return result;
  };

  const can = (event, ctx) => nextState(carrier.state, event, ctx ?? ctxOf()).ok;

  const setGeneration = (generation) => { carrier.generation = generation ?? null; };

  // 打开抽屉「续看」既有作业：按作业状态水合 FSM（非入队转移）
  const hydrate = (job, generation = null) => {
    carrier.job = job ?? null;
    carrier.generation = generation ?? null;
    const status = job?.status;
    carrier.state = !job ? 'idle'
      : status === 'Cancelling' ? 'cancelling'
      : isActiveStatus(status) ? 'active'
      : isTerminalStatus(status) ? 'terminal'
      : 'idle';
    return carrier.state;
  };

  const reset = () => { carrier.state = 'idle'; carrier.job = null; carrier.generation = null; };

  const state = computed(() => carrier.state);
  const job = computed(() => carrier.job);
  const generation = computed(() => carrier.generation);
  const isActive = computed(() => isActiveStatus(carrier.job?.status));
  const busy = computed(() => ['enqueuing', 'active', 'cancelling', 'rollingBack'].includes(carrier.state));
  const enqueuing = computed(() => carrier.state === 'enqueuing');
  const cancelling = computed(() => carrier.state === 'cancelling' || carrier.job?.status === 'Cancelling');
  const rollingBack = computed(() => carrier.state === 'rollingBack');
  const canCancel = computed(() => can('CANCEL') && carrier.job?.status !== 'Cancelling');
  const canRollback = computed(() => can('ROLLBACK'));
  const canEnqueue = computed(() => can('ENQUEUE'));

  const statusText = computed(() => carrier.job ? (STATUS_LABELS[carrier.job.status] || statusLabel(carrier.job.status)) : '无作业');
  const phaseText = computed(() => carrier.job ? (PHASE_LABELS[carrier.job.phase] || phaseLabel(carrier.job.phase) || carrier.job.phase || '就绪') : '就绪');
  const percent = computed(() => {
    const j = carrier.job;
    if (!j) return 0;
    if (j.status === 'Succeeded') return 100;
    if (!j.totalUnits) return 0;
    return Math.min(99, Math.round(j.processedUnits * 100 / j.totalUnits));
  });
  const progressStatus = computed(() => {
    const s = carrier.job?.status;
    return s === 'Succeeded' ? 'success' : s === 'Failed' ? 'error' : s === 'Cancelled' ? 'warning' : 'default';
  });
  const phaseSteps = computed(() => {
    const phase = carrier.job?.phase;
    const status = carrier.job?.status;
    const currentIdx = status === 'Succeeded' ? PHASE_ORDER.length : Math.max(0, PHASE_ORDER.indexOf(phase));
    return PHASE_ORDER.map((key, i) => ({
      key,
      label: PHASE_STEP_LABELS[key],
      state: status === 'Failed' && i === currentIdx ? 'failed'
        : i < currentIdx || status === 'Succeeded' ? 'done'
        : i === currentIdx && isActiveStatus(status) ? 'active' : 'pending'
    }));
  });

  return {
    carrier, send, can, hydrate, reset, setGeneration,
    state, job, generation, isActive, busy, enqueuing, cancelling, rollingBack,
    canCancel, canRollback, canEnqueue, statusText, phaseText, percent, progressStatus, phaseSteps
  };
}