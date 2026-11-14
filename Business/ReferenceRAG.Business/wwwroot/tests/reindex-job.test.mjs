import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

// G7 前端契约门禁（docs/vector-model-switch-reindex-timeout-root-cause-and-async-job-architecture.md §6）：
// 切换模型必须「入队即返回 + 抽屉轮询渲染进度 + 终态 toast」，禁止在请求内等待全量重嵌入完成。
// 视图依赖 Vue/naive-ui 无法在 Node 直接 import，这里抽取纯函数做行为验证 + 源码契约断言。

const here = dirname(fileURLToPath(import.meta.url));
const viewSrc = readFileSync(join(here, '../app/views/KnowledgeBases.js'), 'utf8');
const apiSrc = readFileSync(join(here, '../app/api/index.js'), 'utf8');

function extract(source, pattern, label) {
  const m = source.match(pattern);
  assert.ok(m, `源码契约缺失: ${label}`);
  return m[1];
}

const normalizeReindexJob = new Function(`return (${extract(
  viewSrc,
  /const normalizeReindexJob = (\(raw\) => \{[\s\S]*?\n        \};)/,
  'normalizeReindexJob'
).replace(/;$/, '')})`)();

const STATUS_LABELS = new Function(`return (${extract(
  viewSrc, /const REINDEX_STATUS_LABELS = (\{[\s\S]*?\});/, 'REINDEX_STATUS_LABELS'
)})`)();

const PHASE_LABELS = new Function(`return (${extract(
  viewSrc, /const REINDEX_PHASE_LABELS = (\{[\s\S]*?\});/, 'REINDEX_PHASE_LABELS'
)})`)();

const ACTIVE_STATUSES = new Function(`return (${extract(
  viewSrc, /const REINDEX_ACTIVE_STATUSES = (\[[^\]]*\]);/, 'REINDEX_ACTIVE_STATUSES'
)})`)();

const percentArrow = extract(
  viewSrc, /const reindexPercent = computed\((\(\) => \{[\s\S]*?\n        \})\);/, 'reindexPercent'
);
const percentOf = (job) => new Function('reindexJob', `return (${percentArrow})()`)({ value: job });

// ==================== 行为验证：枚举归一化（后端 JsonStringEnumConverter 字符串 / 数值双兼容） ====================

test('G7 normalizeReindexJob 数值枚举 → 名称（与后端 JobStatus/OperationPhase 序号对齐）', () => {
  const running = normalizeReindexJob({ id: 'j1', status: 1, phase: 8, processedUnits: 40, totalUnits: 100 });
  assert.equal(running.status, 'Running');
  assert.equal(running.phase, 'Embedding');
  assert.equal(running.processedUnits, 40);

  const done = normalizeReindexJob({ id: 'j2', status: 4, phase: 11 });
  assert.equal(done.status, 'Succeeded');
  assert.equal(done.phase, 'Publishing');

  const cancelled = normalizeReindexJob({ status: 7, phase: 0 });
  assert.equal(cancelled.status, 'Cancelled');
  assert.equal(cancelled.phase, 'None');

  // 越界数值兜底，绝不产生 undefined 状态
  const overflow = normalizeReindexJob({ status: 99, phase: 99 });
  assert.equal(overflow.status, 'Queued');
  assert.equal(overflow.phase, 'None');
});

test('G7 normalizeReindexJob 字符串枚举直通 + 空值安全', () => {
  assert.equal(normalizeReindexJob(null), null);
  assert.equal(normalizeReindexJob(undefined), null);

  const job = normalizeReindexJob({ id: 'j3', status: 'Cancelling', phase: 'Embedding' });
  assert.equal(job.status, 'Cancelling');
  assert.equal(job.phase, 'Embedding');
  assert.equal(job.processedUnits, 0);
  assert.equal(job.totalUnits, 0);

  const bare = normalizeReindexJob({});
  assert.equal(bare.status, 'Queued');
  assert.equal(bare.phase, 'None');
});

test('G7 活跃态集合与后端单飞语义一致（Queued/Running/Cancelling）', () => {
  assert.deepEqual(ACTIVE_STATUSES, ['Queued', 'Running', 'Cancelling']);
  for (const terminal of ['Succeeded', 'PartiallySucceeded', 'Failed', 'Cancelled']) {
    assert.equal(ACTIVE_STATUSES.includes(terminal), false);
    assert.ok(STATUS_LABELS[terminal], `终态 ${terminal} 必须有中文标签`);
  }
  for (const phase of ['Planning', 'Embedding', 'Publishing']) {
    assert.ok(PHASE_LABELS[phase], `阶段 ${phase} 必须有中文标签`);
  }
});

test('G7 进度百分比：运行中封顶 99，Succeeded 恒 100，空数据为 0', () => {
  assert.equal(percentOf(null), 0);
  assert.equal(percentOf({ status: 'Running', processedUnits: 0, totalUnits: 0 }), 0);
  assert.equal(percentOf({ status: 'Running', processedUnits: 50, totalUnits: 200 }), 25);
  assert.equal(percentOf({ status: 'Running', processedUnits: 200, totalUnits: 200 }), 99);
  assert.equal(percentOf({ status: 'Succeeded', processedUnits: 200, totalUnits: 200 }), 100);
});

// ==================== 源码契约：入队即返回，绝不等待全量完成 ====================

test('G7 handleTriggerReindex 仅 POST 入队 → 打开抽屉，409 冲突转抽屉续看', () => {
  const handler = extract(
    viewSrc,
    /const handleTriggerReindex = async \(\) => \{([\s\S]*?)\n        \};/,
    'handleTriggerReindex'
  );
  assert.match(handler, /knowledgeBasesApi\.reindex\(targetKb\.id, \{\s*targetEmbeddingSpecId/);
  assert.match(handler, /await openReindexDrawer\(targetKb, job\)/);
  assert.match(handler, /err\.response\?\.status === 409/);
  assert.match(handler, /重构作业已入队/);
  // 禁止在请求内轮询等待终态（旧架构同步等待的回归防线）
  assert.doesNotMatch(handler, /setInterval|while\s*\(|AwaitTerminal|getReindexStatus/);
});

test('G7 抽屉轮询生命周期：1.5s 轮询、终态停止、卸载/关闭清理定时器', () => {
  assert.match(viewSrc, /setInterval\(pollReindexStatus, 1500\)/);
  assert.match(viewSrc, /const stopReindexPolling = \(\) => \{\s*if \(reindexPollTimer\) \{\s*clearInterval\(reindexPollTimer\);/);
  assert.match(viewSrc, /onUnmounted\(stopReindexPolling\)/);
  assert.match(viewSrc, /@after-leave="stopReindexPolling"/);
  // 终态裁决：非活跃即停轮询，且仅在「活跃 → 终态」跃迁时 toast
  assert.match(viewSrc, /if \(cur && !REINDEX_ACTIVE_STATUSES\.includes\(cur\.status\)\) \{\s*stopReindexPolling\(\);/);
});

test('G7 终态反馈：成功/失败/取消 toast + 成功后刷新列表', () => {
  assert.match(viewSrc, /message\.success\(`重构完成：\$\{cur\.processedUnits\} 个切片向量已原子切换至新代次`\)/);
  assert.match(viewSrc, /message\.error\('重构失败: ' \+ \(cur\.errorMessage \|\| '未知错误'\)\)/);
  assert.match(viewSrc, /message\.warning\('重构作业已取消，影子表已清理'\)/);
  const successBranch = extract(viewSrc, /if \(cur\.status === 'Succeeded'\) \{([\s\S]*?)\} else if/, '成功分支');
  assert.match(successBranch, /await loadKnowledgeBases\(\)/);
});

test('G7 取消与回滚走真实后端端点，且回滚仅在可回滚且无活跃作业时可见', () => {
  assert.match(viewSrc, /knowledgeBasesApi\.cancelJob\(reindexJob\.value\.id\)/);
  assert.match(viewSrc, /knowledgeBasesApi\.rollbackReindex\(reindexKb\.value\.id\)/);
  assert.match(viewSrc, /v-if="reindexGeneration\?\.canRollback && !reindexActive"/);
  assert.match(viewSrc, /v-if="reindexActive" type="error" secondary :loading="reindexCancelling" @click="handleCancelReindex"/);
});

test('G7 抽屉渲染契约：进度条/统计/阶段时间线/代次信息全部绑定真实状态', () => {
  assert.match(viewSrc, /<n-drawer v-model:show="showReindexDrawer"/);
  assert.match(viewSrc, /:percentage="reindexPercent"/);
  assert.match(viewSrc, /:processing="reindexActive"/);
  assert.match(viewSrc, /v-for="step in reindexPhaseSteps"/);
  assert.match(viewSrc, /\{\{ reindexJob\?\.processedUnits \?\? 0 \}\}/);
  assert.match(viewSrc, /\{\{ reindexSpeedText \}\}/);
  assert.match(viewSrc, /\{\{ reindexEtaText \}\}/);
  assert.match(viewSrc, /reindexGeneration\.shadowExists \? reindexGeneration\.shadowRowCount \+ ' 行 \(重构中\/可续跑\)' : '无'/);
  // 卡片徽标：进行中作业点亮百分比并可点击续看
  assert.match(viewSrc, /reindexBadgePercent\(kb\.id\)/);
  assert.match(viewSrc, /@click="openReindexDrawer\(kb, reindexStatusMap\[kb\.id\]\.activeJob\)"/);
});

test('G7 setup 导出完整：抽屉所有绑定均在 __returned__ 中注册', () => {
  const returned = extract(viewSrc, /const __returned__ = \{([\s\S]*?)\n        \};/, '__returned__');
  for (const binding of [
    'showReindexDrawer', 'reindexKb', 'reindexJob', 'reindexGeneration',
    'reindexActive', 'reindexStatusLabel', 'reindexPhaseLabel', 'reindexPercent',
    'reindexProgressStatus', 'reindexPhaseSteps', 'reindexSpeedText', 'reindexEtaText',
    'reindexCancelling', 'reindexRollingBack', 'reindexStatusMap',
    'openReindexDrawer', 'stopReindexPolling', 'handleCancelReindex', 'handleRollbackReindex',
    'refreshReindexBadges', 'reindexBadgePercent', 'handleTriggerReindex'
  ]) {
    assert.ok(returned.includes(binding), `__returned__ 缺少绑定: ${binding}`);
  }
});

// ==================== API 层契约：端点与后端控制器路由一一对应 ====================

test('G7 knowledgeBasesApi 暴露 reindex/status/rollback/jobs 端点', () => {
  assert.match(apiSrc, /reindex: \(id, data = \{\}\) => api\.post\(`\/knowledge-bases\/\$\{encodeURIComponent\(id\)\}\/reindex`, data\)/);
  assert.match(apiSrc, /getReindexStatus: \(id\) => api\.get\(`\/knowledge-bases\/\$\{encodeURIComponent\(id\)\}\/reindex\/status`\)/);
  assert.match(apiSrc, /rollbackReindex: \(id\) => api\.post\(`\/knowledge-bases\/\$\{encodeURIComponent\(id\)\}\/reindex\/rollback`\)/);
  assert.match(apiSrc, /getJob: \(jobId\) => api\.get\(`\/jobs\/\$\{encodeURIComponent\(jobId\)\}`\)/);
  assert.match(apiSrc, /cancelJob: \(jobId\) => api\.post\(`\/jobs\/\$\{encodeURIComponent\(jobId\)\}\/cancel`\)/);
});