import test from 'node:test';
import assert from 'node:assert/strict';
import { InvalidationRegistry, getInvalidationTags } from '../core/jobs/InvalidationRegistry.js';
import { JobCenter } from '../core/jobs/JobCenter.js';

test('InvalidationRegistry maps job types to appropriate UI cache tags', () => {
  assert.deepEqual(getInvalidationTags('Rechunk'), ['sources', 'vector_index', 'dashboard_stats']);
  assert.deepEqual(getInvalidationTags('Incremental'), ['sources', 'vector_index', 'dashboard_stats']);
  assert.deepEqual(getInvalidationTags('VectorOnly'), ['vector_index', 'dashboard_stats']);
  assert.deepEqual(getInvalidationTags('Cleanup'), ['sources', 'vector_index', 'dashboard_stats']);
  assert.deepEqual(getInvalidationTags('ModelDownload'), ['models_list', 'model_status', 'cuda_availability']);
  assert.deepEqual(getInvalidationTags('UnknownType'), ['sources', 'vector_index', 'dashboard_stats']);
});

test('JobCenter registerRefresher and triggerRefresh execute registered callbacks', async () => {
  let sourcesRefreshed = 0;
  let vectorIndexRefreshed = 0;

  const unbindSources = JobCenter.registerRefresher('sources', async () => {
    sourcesRefreshed++;
  });
  const unbindIndex = JobCenter.registerRefresher('vector_index', async () => {
    vectorIndexRefreshed++;
  });

  // Trigger only vector_index
  await JobCenter.triggerRefresh(['vector_index']);
  assert.equal(sourcesRefreshed, 0);
  assert.equal(vectorIndexRefreshed, 1);

  // Trigger both
  await JobCenter.triggerRefresh(['sources', 'vector_index']);
  assert.equal(sourcesRefreshed, 1);
  assert.equal(vectorIndexRefreshed, 2);

  // Unbind sources
  unbindSources();
  await JobCenter.triggerRefresh(['sources', 'vector_index']);
  assert.equal(sourcesRefreshed, 1); // Unbound, should not increment
  assert.equal(vectorIndexRefreshed, 3);

  unbindIndex();
});

test('JobCenter tracks optimistic started job and normalizes progress', () => {
  const job = JobCenter.registerStartedJob({
    jobId: 'job-101',
    targetResource: 'source:test_docs',
    jobType: 'Rechunk',
    message: '正在重切片...'
  });

  assert.equal(job.jobId, 'job-101');
  assert.equal(job.targetResource, 'source:test_docs');
  assert.equal(job.jobType, 'Rechunk');
  assert.equal(JobCenter.isResourceBusy('source:test_docs'), true);
  assert.equal(JobCenter.getActiveJob('source:test_docs')?.jobId, 'job-101');

  // Normalized update
  const normalized = JobCenter._normalizeJob({
    indexId: 'job-101',
    targetResource: 'source:test_docs',
    phase: 'Embedding',
    progressPercent: 45.6,
    currentFile: 'chapter1.md'
  });

  assert.equal(normalized.phase, 'Embedding');
  assert.equal(normalized.progressPercent, 46);
  assert.equal(normalized.currentMessage, 'chapter1.md');
});

test('JobCenter replaceJobId seamlessly transitions local optimistic job to server jobId', () => {
  const optimistic = JobCenter.registerStartedJob({
    targetResource: 'source:test_replace',
    jobType: 'Rechunk',
    message: '启动中...'
  });

  const localId = optimistic.jobId;
  assert.ok(localId.startsWith('local-'));
  assert.equal(JobCenter.getActiveJob('source:test_replace')?.jobId, localId);

  // Transition to server id
  JobCenter.replaceJobId(localId, 'server-job-777');

  assert.equal(JobCenter.activeJobs.has(localId), false);
  assert.equal(JobCenter.activeJobs.has('server-job-777'), true);
  assert.equal(JobCenter.getActiveJob('source:test_replace')?.jobId, 'server-job-777');

  // Clean up
  JobCenter._handleJobFinished({ jobId: 'server-job-777', targetResource: 'source:test_replace' });
});

test('JobCenter completion triggers listeners and cleans active state', async () => {
  let completedFired = false;
  let refreshFired = false;

  const unbindRefresh = JobCenter.registerRefresher('sources', () => {
    refreshFired = true;
  });

  const unbindJobListener = JobCenter.subscribeJob('source:test_docs', {
    onCompleted: (job) => {
      assert.equal(job.jobId, 'job-101');
      completedFired = true;
    }
  });

  await JobCenter._handleJobFinished({
    jobId: 'job-101',
    targetResource: 'source:test_docs',
    jobType: 'Rechunk',
    status: 'Completed',
    success: true
  });

  assert.equal(completedFired, true);
  assert.equal(refreshFired, true);
  assert.equal(JobCenter.isResourceBusy('source:test_docs'), false);
  assert.equal(JobCenter.getActiveJob('source:test_docs'), null);

  unbindRefresh();
  unbindJobListener();
});

test('JobCenter global busy state propagates to individual resources', async () => {
  const globalJob = JobCenter.registerStartedJob({
    jobId: 'global-rechunk-01',
    targetResource: 'global',
    jobType: 'Rechunk',
    message: '全库切片重建中...'
  });

  // When global is active, any specific source reports busy with the global job
  assert.equal(JobCenter.isResourceBusy('source:any_folder'), true);
  assert.equal(JobCenter.getActiveJob('source:any_folder')?.jobId, 'global-rechunk-01');

  // Complete global job
  await JobCenter._handleJobFinished(globalJob);
  assert.equal(JobCenter.isResourceBusy('source:any_folder'), false);
});

// ==================== G-M3/G-M4 测试夹具 ====================

function createFakeDoc(initialHidden = false) {
  const listeners = new Set();
  return {
    hidden: initialHidden,
    addEventListener(type, fn) { if (type === 'visibilitychange') listeners.add(fn); },
    removeEventListener(type, fn) { listeners.delete(fn); },
    setHidden(hidden) {
      this.hidden = hidden;
      for (const fn of Array.from(listeners)) fn();
    },
    get listenerCount() { return listeners.size; }
  };
}

function createFakeConnection() {
  const handlers = new Map();
  const lifecycle = { close: [], reconnecting: [], reconnected: [] };
  return {
    on(name, cb) { handlers.set(name, cb); },
    onclose(cb) { lifecycle.close.push(cb); },
    onreconnecting(cb) { lifecycle.reconnecting.push(cb); },
    onreconnected(cb) { lifecycle.reconnected.push(cb); },
    async start() {},
    async stop() {},
    async invoke() {},
    emit(name, payload) { return handlers.get(name)?.(payload); },
    fireClose() { for (const cb of lifecycle.close) cb(); },
    async fireReconnected() { for (const cb of lifecycle.reconnected) await cb(); }
  };
}

const kbRunning = (processedUnits) => ({
  id: 'kb-job-1', knowledgeBaseId: 'abc', operation: 'reindex',
  status: 'Running', phase: 'Embedding', processedUnits, totalUnits: 100
});

// ==================== G-M3：KbJobSource 适配器 + 生命周期策略 ====================

test('G-M3 fake connection 推送 → 响应式 Map；断连 → 2s 兜底；hidden → 0 定时器；恢复 → 对账', async () => {
  JobCenter.destroy();
  const fakeDoc = createFakeDoc(false);
  JobCenter._doc = fakeDoc;
  JobCenter._visible = true;

  let indexActive = [];
  let kbActive = [];
  JobCenter.setApi({ getActive: async () => ({ data: indexActive }) }, 'index');
  JobCenter.setApi({ listJobs: async () => ({ data: kbActive }) }, 'kb');

  const conn = createFakeConnection();
  await JobCenter.initialize(conn);
  assert.equal(JobCenter.isConnected.value, true);

  // 推送 Started → 经 KbJobSource.normalize 进入响应式 Map（getExactJob 精确命中，无 global 兜底）
  await conn.emit('ReindexJobStarted', {
    jobId: 'kb-job-1', knowledgeBaseId: 'abc', operation: 'reindex',
    status: 'Queued', phase: 'Planning', processedUnits: 0, totalUnits: 100, percent: 0
  });
  const job = JobCenter.getExactJob('kb:abc');
  assert.ok(job, 'KB 作业应进入 activeJobsByResource');
  assert.equal(job.jobId, 'kb-job-1');
  assert.equal(job.sourceKey, 'kb');
  assert.equal(job.jobType, 'Reindex');
  // 连接正常 + 可见 + 有活跃作业 → 10s 心跳（恰好一个定时器）
  assert.equal(JobCenter._syncInterval, 10000);
  assert.ok(JobCenter.syncTimer);

  // 推送 Progress → 百分比更新
  await conn.emit('ReindexJobProgress', {
    jobId: 'kb-job-1', knowledgeBaseId: 'abc', operation: 'reindex',
    status: 'Running', phase: 'Embedding', processedUnits: 50, totalUnits: 100, percent: 50
  });
  assert.equal(JobCenter.getExactJob('kb:abc').progressPercent, 50);
  kbActive = [kbRunning(50)];

  // 断连 → 2s 自适应轮询兜底
  conn.fireClose();
  assert.equal(JobCenter.isConnected.value, false);
  assert.equal(JobCenter._syncInterval, 2000);

  // hidden → 0 定时器（推送仍即时处理，后台 0 HTTP 请求）
  fakeDoc.setHidden(true);
  assert.equal(JobCenter._visible, false);
  assert.equal(JobCenter.syncTimer, null);
  assert.equal(JobCenter._syncInterval, null);

  await conn.emit('ReindexJobProgress', {
    jobId: 'kb-job-1', knowledgeBaseId: 'abc', operation: 'reindex',
    status: 'Running', phase: 'Embedding', processedUnits: 80, totalUnits: 100, percent: 80
  });
  assert.equal(JobCenter.getExactJob('kb:abc').progressPercent, 80);
  assert.equal(JobCenter.syncTimer, null, 'hidden 期间不得重建定时器');
  kbActive = [kbRunning(80)];

  // hidden 期间重连 → 仍 0 定时器（可见性优先于连接态）
  await conn.fireReconnected();
  assert.equal(JobCenter.isConnected.value, true);
  assert.equal(JobCenter.syncTimer, null);

  // 恢复可见 → 立即对账（服务端进度 90）+ 恢复 10s 心跳
  kbActive = [kbRunning(90)];
  fakeDoc.setHidden(false);
  await JobCenter.syncActiveJobs();
  assert.equal(JobCenter._visible, true);
  assert.equal(JobCenter.getExactJob('kb:abc').progressPercent, 90);
  assert.equal(JobCenter._syncInterval, 10000);
  assert.ok(JobCenter.syncTimer);

  // 推送 Completed → 终态收敛，移出 Map，无活跃作业 → 停定时器
  await conn.emit('ReindexJobCompleted', {
    jobId: 'kb-job-1', knowledgeBaseId: 'abc', operation: 'reindex',
    status: 'Succeeded', phase: 'None', processedUnits: 100, totalUnits: 100, percent: 100,
    finishedAt: '2026-11-14T00:00:00Z'
  });
  assert.equal(JobCenter.getExactJob('kb:abc'), null);
  assert.equal(JobCenter.syncTimer, null);

  // destroy 清理可见性监听，杜绝泄漏
  JobCenter.destroy();
  assert.equal(fakeDoc.listenerCount, 0);
  assert.equal(JobCenter.syncTimer, null);
});

test('G-M3 单源对账隔离：索引对账不得误删 KB 重构作业（反之亦然）', async () => {
  JobCenter.destroy();
  JobCenter._doc = null;
  JobCenter._visible = true;

  let indexActive = [];
  let kbActive = [kbRunning(30)];
  JobCenter.setApi({ getActive: async () => ({ data: indexActive }) }, 'index');
  JobCenter.setApi({ listJobs: async () => ({ data: kbActive }) }, 'kb');

  // 直接登记一个索引作业与一个 KB 作业
  JobCenter._setJob({ jobId: 'idx-1', sourceKey: 'index', targetResource: 'source:docs', jobType: 'Incremental', status: 'Running', phase: 'Running', progressPercent: 10, currentMessage: '', success: true, detail: null });
  JobCenter._setJob({ jobId: 'kb-job-1', sourceKey: 'kb', targetResource: 'kb:abc', jobType: 'Reindex', status: 'Running', phase: 'Embedding', progressPercent: 30, currentMessage: '', success: true, detail: { operation: 'reindex' } });

  // 索引源对账返回空 → 仅清理 idx-1，KB 作业必须保留
  await JobCenter._reconcileSource('index', []);
  assert.equal(JobCenter.getExactJob('source:docs'), null);
  assert.ok(JobCenter.getExactJob('kb:abc'), 'KB 作业不得被索引对账误删');

  // KB 源对账返回空 → 清理 kb-job-1
  await JobCenter._reconcileSource('kb', []);
  assert.equal(JobCenter.getExactJob('kb:abc'), null);

  JobCenter.destroy();
});

// ==================== G-M4：InvalidationRegistry.Reindex + 完成刷新 ====================

test('G-M4 InvalidationRegistry.Reindex 标签；Reindex 完成触发 knowledge_bases 刷新', async () => {
  assert.deepEqual(getInvalidationTags('Reindex'), ['knowledge_bases', 'vector_index', 'dashboard_stats']);

  JobCenter.destroy();
  JobCenter._doc = null;
  JobCenter._visible = true;

  let kbRefreshed = 0, vectorRefreshed = 0, dashboardRefreshed = 0;
  const unbindKb = JobCenter.registerRefresher('knowledge_bases', () => { kbRefreshed++; });
  const unbindVec = JobCenter.registerRefresher('vector_index', () => { vectorRefreshed++; });
  const unbindDash = JobCenter.registerRefresher('dashboard_stats', () => { dashboardRefreshed++; });

  JobCenter.registerStartedJob({ jobId: 'kb-job-9', targetResource: 'kb:xyz', jobType: 'Reindex', sourceKey: 'kb', message: '重构中' });
  assert.equal(JobCenter.getExactJob('kb:xyz').jobId, 'kb-job-9');

  await JobCenter._handleJobFinished({ jobId: 'kb-job-9', targetResource: 'kb:xyz', jobType: 'Reindex', sourceKey: 'kb', status: 'Succeeded', success: true });

  assert.equal(kbRefreshed, 1, 'Reindex 完成必须刷新知识库列表');
  assert.equal(vectorRefreshed, 1);
  assert.equal(dashboardRefreshed, 1);
  assert.equal(JobCenter.getExactJob('kb:xyz'), null);

  unbindKb(); unbindVec(); unbindDash();
  JobCenter.destroy();
});

test('G-M4 页面不可见时完成 → 刷新挂起，恢复可见后统一补刷', async () => {
  JobCenter.destroy();
  const fakeDoc = createFakeDoc(false);
  JobCenter._doc = fakeDoc;
  JobCenter._visible = true;
  JobCenter.setApi({ getActive: async () => ({ data: [] }) }, 'index');
  JobCenter.setApi({ listJobs: async () => ({ data: [] }) }, 'kb');
  await JobCenter.initialize(createFakeConnection());

  let kbRefreshed = 0;
  const unbindKb = JobCenter.registerRefresher('knowledge_bases', () => { kbRefreshed++; });

  JobCenter.registerStartedJob({ jobId: 'kb-job-hidden', targetResource: 'kb:hidden', jobType: 'Reindex', sourceKey: 'kb' });
  fakeDoc.setHidden(true);
  await JobCenter._handleJobFinished({ jobId: 'kb-job-hidden', targetResource: 'kb:hidden', jobType: 'Reindex', sourceKey: 'kb', status: 'Succeeded', success: true });
  assert.equal(kbRefreshed, 0, 'hidden 期间不得空跑刷新');
  assert.ok(JobCenter._pendingRefreshTags.has('knowledge_bases'));

  fakeDoc.setHidden(false);
  await JobCenter._flushPendingRefresh();
  assert.equal(kbRefreshed, 1, '恢复可见后补刷一次');
  assert.equal(JobCenter._pendingRefreshTags.size, 0);

  unbindKb();
  JobCenter.destroy();
  assert.equal(fakeDoc.listenerCount, 0);
});
