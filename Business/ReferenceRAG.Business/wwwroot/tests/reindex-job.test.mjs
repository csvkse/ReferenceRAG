import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { nextState, createReindexDrawerMachine, REINDEX_STATES } from '../core/jobs/ReindexDrawerMachine.js';
import { normalizeReindexJob, toReindexShape, isActiveStatus, isTerminalStatus } from '../core/jobs/reindex-model.js';

// G-M2 前端契约门禁（docs/mobile-adaptation-and-frontend-job-ux-optimization-feasibility.md §6.1/§7）：
// 重构抽屉改用显式 FSM + JobCenter 推送，删除手写 setInterval；取消 404→info「作业已结束」；
// 模板按钮可用性全部由 state 派生。FSM 为纯模块，可直接驱动转移表（与后端 JobStateMachine 测试风格对齐）。

const here = dirname(fileURLToPath(import.meta.url));
const viewSrc = readFileSync(join(here, '../app/views/KnowledgeBases.js'), 'utf8');
const apiSrc = readFileSync(join(here, '../app/api/index.js'), 'utf8');

const running = (processedUnits = 0, totalUnits = 100) => ({ id: 'job-1', jobId: 'job-1', status: 'Running', phase: 'Embedding', processedUnits, totalUnits, errorMessage: null });
const terminalJob = (status, extra = {}) => ({ id: 'job-1', jobId: 'job-1', status, phase: 'None', processedUnits: 100, totalUnits: 100, errorMessage: null, ...extra });

// ==================== 转移表：全合法路径 ====================

const LEGAL = [
  ['idle', 'ENQUEUE', 'enqueuing', {}],
  ['enqueuing', 'ENQUEUED', 'active', {}],
  ['enqueuing', 'ENQUEUE_409', 'active', {}],
  ['enqueuing', 'ENQUEUE_FAIL', 'idle', {}],
  ['enqueuing', 'PROGRESS', 'active', {}],
  ['enqueuing', 'TERMINAL', 'terminal', {}],
  ['active', 'PROGRESS', 'active', {}],
  ['active', 'ENQUEUED', 'active', {}],
  ['active', 'TERMINAL', 'terminal', {}],
  ['active', 'CANCEL', 'cancelling', { job: running() }],
  ['active', 'CLOSE', 'active', {}],
  ['cancelling', 'CANCEL_ACK', 'active', {}],
  ['cancelling', 'CANCEL_FAIL', 'active', {}],
  ['cancelling', 'CANCEL_404', 'terminal', {}],
  ['cancelling', 'PROGRESS', 'cancelling', {}],
  ['cancelling', 'TERMINAL', 'terminal', {}],
  ['cancelling', 'CLOSE', 'cancelling', {}],
  ['terminal', 'ROLLBACK', 'rollingBack', { generation: { canRollback: true } }],
  ['terminal', 'ENQUEUE', 'enqueuing', {}],
  ['terminal', 'CLOSE', 'terminal', {}],
  ['rollingBack', 'ROLLBACK_DONE', 'idle', {}],
  ['rollingBack', 'ROLLBACK_FAIL', 'terminal', {}]
];

test('G-M2 转移表全合法路径：state × event → next', () => {
  for (const [state, event, next, ctx] of LEGAL) {
    const r = nextState(state, event, ctx);
    assert.equal(r.ok, true, `${state} × ${event} 应合法`);
    assert.equal(r.state, next, `${state} × ${event} → ${next}`);
    assert.equal(r.reason, null);
  }
});

test('G-M2 非法组合显式拒绝（转移表缺省 = 非法，状态不变）', () => {
  const ILLEGAL = [
    ['idle', 'TERMINAL'], ['idle', 'CANCEL'], ['idle', 'PROGRESS'], ['idle', 'ROLLBACK'],
    ['enqueuing', 'CANCEL'], ['enqueuing', 'ROLLBACK'],
    ['active', 'ROLLBACK'], ['active', 'ENQUEUE'], ['active', 'ROLLBACK_DONE'],
    ['cancelling', 'ENQUEUE'], ['cancelling', 'ROLLBACK'],
    ['terminal', 'CANCEL'], ['terminal', 'PROGRESS'], ['terminal', 'TERMINAL'], ['terminal', 'CANCEL_ACK'],
    ['rollingBack', 'CANCEL'], ['rollingBack', 'PROGRESS'], ['rollingBack', 'ENQUEUE'], ['rollingBack', 'TERMINAL']
  ];
  for (const [state, event] of ILLEGAL) {
    const r = nextState(state, event, { job: running(), generation: { canRollback: true } });
    assert.equal(r.ok, false, `${state} × ${event} 应被拒绝`);
    assert.equal(r.state, state, '拒绝时状态保持不变');
    assert.ok(r.reason, '拒绝必须给出原因');
  }
});

test('G-M2 守卫：CANCEL 需作业 id；ROLLBACK 需 generation.canRollback', () => {
  assert.equal(nextState('active', 'CANCEL', {}).ok, false);
  assert.equal(nextState('active', 'CANCEL', { job: {} }).ok, false);
  assert.equal(nextState('active', 'CANCEL', { job: { id: 'x' } }).ok, true);
  assert.equal(nextState('terminal', 'ROLLBACK', {}).ok, false);
  assert.equal(nextState('terminal', 'ROLLBACK', { generation: { canRollback: false } }).ok, false);
  assert.equal(nextState('terminal', 'ROLLBACK', { generation: { canRollback: true } }).ok, true);
});

test('G-M2 状态集合与后端单飞语义对齐', () => {
  assert.deepEqual([...REINDEX_STATES], ['idle', 'enqueuing', 'active', 'cancelling', 'terminal', 'rollingBack']);
  for (const s of ['Queued', 'Running', 'WaitingForInput', 'Cancelling']) assert.ok(isActiveStatus(s));
  for (const s of ['Succeeded', 'PartiallySucceeded', 'Failed', 'Cancelled']) assert.ok(isTerminalStatus(s));
});

// ==================== 机器实例：黄金路径 + 取消 + 回滚 ====================

test('G-M2 黄金路径：入队 → 活跃 → 进度 → 终态 → 回滚 → idle', () => {
  const m = createReindexDrawerMachine();
  assert.equal(m.state.value, 'idle');
  assert.equal(m.canEnqueue.value, true);

  assert.equal(m.send('ENQUEUE').ok, true);
  assert.equal(m.state.value, 'enqueuing');
  assert.equal(m.enqueuing.value, true);
  assert.equal(m.carrier.job, null, 'ENQUEUE 清空旧快照');

  m.send('ENQUEUED', { job: running(0) });
  assert.equal(m.state.value, 'active');
  assert.equal(m.isActive.value, true);
  assert.equal(m.canCancel.value, true);
  assert.equal(m.canRollback.value, false);

  m.send('PROGRESS', { job: running(50) });
  assert.equal(m.percent.value, 50);
  assert.equal(m.state.value, 'active');

  m.send('TERMINAL', { job: terminalJob('Succeeded'), generation: { canRollback: true } });
  assert.equal(m.state.value, 'terminal');
  assert.equal(m.percent.value, 100);
  assert.equal(m.progressStatus.value, 'success');
  assert.equal(m.canRollback.value, true);
  assert.equal(m.canCancel.value, false);
  assert.equal(m.isActive.value, false);

  assert.equal(m.send('ROLLBACK').ok, true);
  assert.equal(m.state.value, 'rollingBack');
  assert.equal(m.rollingBack.value, true);
  assert.equal(m.canRollback.value, false);

  m.send('ROLLBACK_DONE', { generation: { canRollback: false } });
  assert.equal(m.state.value, 'idle');
  assert.equal(m.carrier.job, null);
});

test('G-M2 取消路径：CANCEL → cancelling → CANCEL_ACK → active → TERMINAL(Cancelled)', () => {
  const m = createReindexDrawerMachine();
  m.hydrate(running(30));
  assert.equal(m.state.value, 'active');

  m.send('CANCEL', { job: running(30) });
  assert.equal(m.state.value, 'cancelling');
  assert.equal(m.cancelling.value, true);
  assert.equal(m.canCancel.value, false, 'cancelling 期间不可重复取消');

  m.send('CANCEL_ACK');
  assert.equal(m.state.value, 'active', 'Cancelling 仍属活跃，等待终态事件');

  m.send('TERMINAL', { job: terminalJob('Cancelled') });
  assert.equal(m.state.value, 'terminal');
  assert.equal(m.progressStatus.value, 'warning');
  assert.equal(m.statusText.value, '已取消');
});

test('G-M2 CANCEL_404 → terminal（作业已结束，info 分类而非 error）', () => {
  const m = createReindexDrawerMachine();
  m.hydrate(running(80));
  m.send('CANCEL', { job: running(80) });
  assert.equal(m.state.value, 'cancelling');

  const snapshot = terminalJob('Succeeded');
  const r = m.send('CANCEL_404', { job: snapshot });
  assert.equal(r.ok, true);
  assert.equal(m.state.value, 'terminal');
  assert.equal(m.carrier.job.status, 'Succeeded', '保留终态快照用于结果展示');
  assert.equal(m.percent.value, 100);
});

test('G-M2 CANCEL_FAIL → active（取消请求失败回退，不误判终态）', () => {
  const m = createReindexDrawerMachine();
  m.hydrate(running(40));
  m.send('CANCEL', { job: running(40) });
  m.send('CANCEL_FAIL');
  assert.equal(m.state.value, 'active');
  assert.equal(m.canCancel.value, true);
});

test('G-M2 终态按钮派生：canRollback 仅在 terminal+canRollback；canEnqueue 在 idle/terminal', () => {
  const m = createReindexDrawerMachine();
  assert.equal(m.canEnqueue.value, true);
  assert.equal(m.canCancel.value, false);
  assert.equal(m.canRollback.value, false);

  m.hydrate({ ...running(10), status: 'Cancelling' });
  assert.equal(m.state.value, 'cancelling');
  assert.equal(m.canCancel.value, false);

  m.hydrate(terminalJob('Failed'), { canRollback: false });
  assert.equal(m.state.value, 'terminal');
  assert.equal(m.canRollback.value, false);
  assert.equal(m.canEnqueue.value, true);
  assert.equal(m.progressStatus.value, 'error');

  m.hydrate(terminalJob('Succeeded'), { canRollback: true });
  assert.equal(m.canRollback.value, true);

  m.send('ROLLBACK');
  assert.equal(m.canCancel.value, false);
  assert.equal(m.canRollback.value, false);
  assert.equal(m.canEnqueue.value, false);
  assert.equal(m.busy.value, true);
});

test('G-M2 hydrate 按作业状态水合：active/cancelling/terminal/idle', () => {
  const m = createReindexDrawerMachine();
  assert.equal(m.hydrate(null), 'idle');
  assert.equal(m.hydrate(running()), 'active');
  assert.equal(m.hydrate({ ...running(), status: 'Cancelling' }), 'cancelling');
  assert.equal(m.hydrate({ ...running(), status: 'Queued' }), 'active');
  assert.equal(m.hydrate(terminalJob('Succeeded')), 'terminal');
  assert.equal(m.hydrate(terminalJob('Cancelled')), 'terminal');
  m.reset();
  assert.equal(m.state.value, 'idle');
  assert.equal(m.carrier.job, null);
  assert.equal(m.carrier.generation, null);
});

test('G-M2 进度/阶段派生：percent 封顶、phaseSteps、progressStatus', () => {
  const m = createReindexDrawerMachine();
  assert.equal(m.percent.value, 0);
  m.hydrate(running(200, 200));
  assert.equal(m.percent.value, 99, '运行中封顶 99');
  m.hydrate(terminalJob('Succeeded'));
  assert.equal(m.percent.value, 100);
  assert.deepEqual(m.phaseSteps.value.map(s => s.state), ['done', 'done', 'done']);

  m.hydrate({ ...running(40), phase: 'Embedding' });
  assert.deepEqual(m.phaseSteps.value.map(s => s.state), ['done', 'active', 'pending']);
  assert.equal(m.progressStatus.value, 'default');

  m.hydrate({ ...terminalJob('Failed'), phase: 'Embedding' });
  assert.deepEqual(m.phaseSteps.value.map(s => s.state), ['done', 'failed', 'pending']);
  assert.equal(m.progressStatus.value, 'error');
});

test('G-M2 终态重复 TERMINAL 被拒绝（防止重复 toast）', () => {
  const m = createReindexDrawerMachine();
  m.hydrate(running());
  assert.equal(m.send('TERMINAL', { job: terminalJob('Succeeded') }).ok, true);
  assert.equal(m.send('TERMINAL', { job: terminalJob('Succeeded') }).ok, false, 'terminal × TERMINAL 非法');
});

// ==================== 归一化 → 展示快照契约 ====================

test('G-M2 normalizeReindexJob → toReindexShape 字段对齐模板消费', () => {
  const unified = normalizeReindexJob({ id: 'j9', knowledgeBaseId: 'kb-9', operation: 'reindex', status: 'Running', phase: 'Embedding', processedUnits: 40, totalUnits: 100 });
  const shape = toReindexShape(unified);
  assert.equal(shape.id, 'j9');
  assert.equal(shape.status, 'Running');
  assert.equal(shape.phase, 'Embedding');
  assert.equal(shape.processedUnits, 40);
  assert.equal(shape.totalUnits, 100);
  assert.equal(shape.percent, 40);
  const numeric = toReindexShape(normalizeReindexJob({ id: 'jN', status: 4, phase: 11, processedUnits: 100, totalUnits: 100 }));
  assert.equal(numeric.status, 'Succeeded');
  assert.equal(numeric.phase, 'Publishing');
  assert.equal(toReindexShape(null).id, '');
  assert.equal(toReindexShape(null).processedUnits, 0);
});

// ==================== 视图源码契约：删除轮询，接入 JobCenter + FSM ====================

test('G-M2 视图删除手写轮询，改用 JobCenter 推送 + useAsyncJob + FSM', () => {
  assert.doesNotMatch(viewSrc, /setInterval|pollReindexStatus|stopReindexPolling|reindexPollTimer/, '禁止手写轮询回归');
  assert.match(viewSrc, /import \{ JobCenter \} from '\/core\/jobs\/JobCenter\.js'/);
  assert.match(viewSrc, /import \{ useAsyncJob \} from '\/core\/jobs\/useAsyncJob\.js'/);
  assert.match(viewSrc, /import \{ createReindexDrawerMachine \} from '\/core\/jobs\/ReindexDrawerMachine\.js'/);
  assert.match(viewSrc, /const machine = createReindexDrawerMachine\(\)/);
  assert.match(viewSrc, /reindexAsyncJob\.runJob\(/);
  assert.match(viewSrc, /JobCenter\.subscribeJob\(kbKey/);
  assert.match(viewSrc, /JobCenter\.getExactJob\(`kb:\$\{kb\.id\}`\)/);
  assert.match(viewSrc, /JobCenter\.registerRefresher\('knowledge_bases'/);
  assert.match(viewSrc, /await JobCenter\.initialize\(\)/);
});

test('G-M2 入队即返回：handleTriggerReindex 仅 runJob 入队 → 开抽屉，409 续看，失败回 idle', () => {
  const handler = viewSrc.match(/const handleTriggerReindex = async \(\) => \{([\s\S]*?)\n        \};/);
  assert.ok(handler, 'handleTriggerReindex 缺失');
  const body = handler[1];
  assert.match(body, /machine\.send\('ENQUEUE'\)/);
  assert.match(body, /knowledgeBasesApi\.reindex\(targetKb\.id, \{ targetEmbeddingSpecId/);
  assert.match(body, /machine\.send\('ENQUEUED'/);
  assert.match(body, /await openReindexDrawer\(targetKb, res\.data\)/);
  assert.match(body, /err\.response\?\.status === 409/);
  assert.match(body, /machine\.send\('ENQUEUE_409'\)/);
  assert.match(body, /machine\.send\('ENQUEUE_FAIL'\)/);
  assert.doesNotMatch(body, /setInterval|while\s*\(|getReindexStatus/, '禁止请求内等待终态');
});

test('G-M2 取消 404 → info「作业已结束」+ 终态快照（G-d 修复）', () => {
  assert.match(viewSrc, /machine\.send\('CANCEL_404', \{ job: snapshot \}\)/);
  assert.match(viewSrc, /message\.info\('作业已结束'\)/);
  assert.match(viewSrc, /err\.response\?\.status === 404/);
  assert.match(viewSrc, /knowledgeBasesApi\.getJob\(job\.id\)/);
  assert.match(viewSrc, /machine\.send\('CANCEL_FAIL'\)/);
});

test('G-M2 关抽屉不停作业 + keep-alive 失活暂停观测 + 卸载清理', () => {
  assert.match(viewSrc, /@after-leave="onReindexDrawerClosed"/);
  assert.match(viewSrc, /const onReindexDrawerClosed = \(\) => \{\s*machine\.send\('CLOSE'\);\s*unsubscribeReindex\(\);/);
  assert.match(viewSrc, /onDeactivated\(\(\) => \{ unsubscribeReindex\(\); \}\)/);
  assert.match(viewSrc, /onUnmounted\(\(\) => \{[\s\S]*?unsubscribeReindex\(\);[\s\S]*?unbindKbRefresh\(\);/);
});

test('G-M2 模板按钮可用性全部由 FSM 派生（消灭四布尔耦合）', () => {
  assert.match(viewSrc, /const reindexing = machine\.enqueuing/);
  assert.match(viewSrc, /const reindexCancelling = machine\.cancelling/);
  assert.match(viewSrc, /const reindexRollingBack = machine\.rollingBack/);
  assert.match(viewSrc, /const reindexActive = machine\.isActive/);
  assert.match(viewSrc, /v-if="canRollback"/);
  assert.match(viewSrc, /:disabled="!canCancel"/);
  assert.match(viewSrc, /v-if="kbActiveJob\(kb\.id\)"/);
  assert.match(viewSrc, /v-if="reindexGenerationMap\[kb\.id\]\?\.canRollback && !kbActiveJob\(kb\.id\)"/);
  assert.doesNotMatch(viewSrc, /const reindexCancelling = ref|const reindexRollingBack = ref|const reindexStatusMap = ref/);
});

test('G-M2 终态反馈：成功/部分/失败/取消 toast 分类', () => {
  assert.match(viewSrc, /message\.success\(`重构完成：\$\{shape\.processedUnits\} 个切片向量已原子切换至新代次`\)/);
  assert.match(viewSrc, /message\.error\('重构失败: ' \+ \(shape\.errorMessage \|\| '未知错误'\)\)/);
  assert.match(viewSrc, /message\.warning\('重构作业已取消，影子表已清理'\)/);
  assert.match(viewSrc, /message\.warning\(`重构部分完成：\$\{shape\.processedUnits\}\/\$\{shape\.totalUnits\} 个切片成功`\)/);
});

// ==================== API 层契约：端点与后端控制器路由一一对应 ====================

test('G-M2 knowledgeBasesApi 暴露 reindex/status/rollback/jobs 端点', () => {
  assert.match(apiSrc, /reindex: \(id, data = \{\}\) => api\.post\(`\/knowledge-bases\/\$\{encodeURIComponent\(id\)\}\/reindex`, data\)/);
  assert.match(apiSrc, /getReindexStatus: \(id\) => api\.get\(`\/knowledge-bases\/\$\{encodeURIComponent\(id\)\}\/reindex\/status`\)/);
  assert.match(apiSrc, /rollbackReindex: \(id\) => api\.post\(`\/knowledge-bases\/\$\{encodeURIComponent\(id\)\}\/reindex\/rollback`\)/);
  assert.match(apiSrc, /getJob: \(jobId\) => api\.get\(`\/jobs\/\$\{encodeURIComponent\(jobId\)\}`\)/);
  assert.match(apiSrc, /cancelJob: \(jobId\) => api\.post\(`\/jobs\/\$\{encodeURIComponent\(jobId\)\}\/cancel`\)/);
  assert.match(apiSrc, /listJobs: \(kbId = null\) => api\.get\('\/jobs', \{ params: \{ knowledgeBaseId: kbId \} \}\)/);
});