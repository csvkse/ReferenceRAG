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
