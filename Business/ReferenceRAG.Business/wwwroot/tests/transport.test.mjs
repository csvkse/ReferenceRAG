import test from 'node:test';
import assert from 'node:assert/strict';
import {createIpcTransport} from '../core/transport/ipc.js';
import {
  normalizeReindexJob, JOB_STATUS_NAMES, OPERATION_PHASE_NAMES,
  isActiveStatus, isTerminalStatus, percentOf
} from '../core/jobs/reindex-model.js';

test('IPC preserves method/body/status and never falls back to HTTP', async () => {
  let listener, sent;
  const bridge = {addEventListener:(_,fn)=>listener=fn, removeEventListener(){},postMessage:value=>sent=JSON.parse(JSON.parse(value).data)};
  const transport=createIpcTransport(bridge);
  const pending=transport.fetch('/api/example',{method:'POST',body:'{"value":1}'});
  assert.equal(sent.method,'POST'); assert.equal(sent.body,'{"value":1}');
  listener({data:JSON.stringify({id:sent.id,type:'response',status:403,body:'{"error":"denied"}'})});
  const response=await pending;
  assert.equal(response.status,403); assert.equal((await response.json()).error,'denied');
  transport.dispose();
});
test('IPC streams can be consumed incrementally and cancelled', async () => {
  let listener; const sent=[];
  const bridge={addEventListener:(_,fn)=>listener=fn,removeEventListener(){},postMessage:v=>sent.push(JSON.parse(JSON.parse(v).data))};
  const transport=createIpcTransport(bridge), abort=new AbortController();
  const pending=transport.fetch('/api/chat/stream',{method:'POST',signal:abort.signal});
  const id=sent[0].id;
  listener({data:{id,type:'stream-start',status:200,headers:{'content-type':'text/event-stream'}}});
  const response=await pending, reader=response.body.getReader();
  listener({data:{id,type:'stream-chunk',data:'data: {"type":"delta"}\n\n'}});
  assert.match(new TextDecoder().decode((await reader.read()).value),/delta/);
  abort.abort();
  await assert.rejects(reader.read());
  assert.equal(sent.at(-1).type,'cancel');
  transport.dispose();
});
test('disposed and unavailable bridges reject outstanding requests', async () => {
  const transport=createIpcTransport({addEventListener(){},removeEventListener(){},postMessage(){}});
  const pending=transport.fetch('/api/test'); transport.dispose();
  await assert.rejects(pending,/disposed/i);
  const unavailable=createIpcTransport(null);
  await assert.rejects(unavailable.fetch('/api/test'),/unavailable/i);
});
test('IPC correctly proxies /api/chat/sessions and handles response payload', async () => {
  let sent;
  const bridge = {
    addEventListener: (_, fn) => bridge.listener = fn,
    removeEventListener() {},
    postMessage: v => sent = JSON.parse(JSON.parse(v).data)
  };
  const transport = createIpcTransport(bridge);
  const pending = transport.fetch('/api/chat/sessions', { method: 'POST', body: null });
  assert.equal(sent.method, 'POST');
  assert.equal(sent.path, '/api/chat/sessions');
  bridge.listener({
    data: JSON.stringify({
      id: sent.id,
      type: 'response',
      status: 200,
      body: JSON.stringify({ sessionId: 'session-xyz' })
    })
  });
  const res = await pending;
  assert.equal(res.status, 200);
  const json = await res.json();
  assert.equal(json.sessionId, 'session-xyz');
  transport.dispose();
});

// ==================== G-M7 契约门禁：ReindexJob* payload 前后端一致 + 归一化双向兼容 ====================
// 后端契约由 KnowledgeBaseReindexBroadcastGateTests（G-M6）锁定 payload 形状；
// 此处锁定前端归一化对 SignalR（camelCase+percent+字符串枚举）、REST（id，无 percent 需计算）、
// 以及防御性 PascalCase/数值枚举载荷的一致消费。

test('G-M7 SignalR 广播载荷（camelCase + percent + 字符串枚举）归一化契约', () => {
  const job = normalizeReindexJob({
    jobId: 'job-abc', knowledgeBaseId: 'kb-1', operation: 'reindex',
    status: 'Running', phase: 'Embedding',
    processedUnits: 40, totalUnits: 100, percent: 40,
    errorMessage: null, createdAt: '2026-11-14T00:00:00Z', finishedAt: null
  });
  assert.equal(job.jobId, 'job-abc');
  assert.equal(job.sourceKey, 'kb');
  assert.equal(job.targetResource, 'kb:kb-1');
  assert.equal(job.jobType, 'Reindex');
  assert.equal(job.status, 'Running');
  assert.equal(job.phase, 'Embedding');
  assert.equal(job.progressPercent, 40);
  assert.equal(job.success, true);
  assert.equal(job.detail.knowledgeBaseId, 'kb-1');
  assert.equal(job.detail.operation, 'reindex');
  assert.equal(job.detail.processedUnits, 40);
  assert.equal(job.detail.totalUnits, 100);
  assert.equal(job.detail.percent, 40);
  assert.equal(job.detail.terminal, false);
});

test('G-M7 REST /api/jobs 载荷（id，无 percent 需按 processed/total 计算）归一化契约', () => {
  const job = normalizeReindexJob({
    id: 'job-rest', knowledgeBaseId: 'kb-2', operation: 'reindex',
    status: 'Running', phase: 'Chunking', processedUnits: 25, totalUnits: 200,
    createdAt: '2026-11-14T00:00:00Z', finishedAt: null
  });
  assert.equal(job.jobId, 'job-rest');
  assert.equal(job.targetResource, 'kb:kb-2');
  assert.equal(job.detail.percent, 12.5);
  assert.equal(job.progressPercent, 13); // Math.round(12.5)
});

test('G-M7 防御性 PascalCase + 数值枚举归一化（与后端 JobStatus/OperationPhase 序号对齐）', () => {
  const job = normalizeReindexJob({
    JobId: 'job-pascal', KnowledgeBaseId: 'kb-3', Operation: 'reindex',
    Status: 1, Phase: 8, ProcessedUnits: 60, TotalUnits: 100
  });
  assert.equal(job.jobId, 'job-pascal');
  assert.equal(job.status, 'Running');
  assert.equal(job.phase, 'Embedding');
  assert.equal(job.progressPercent, 60);

  const done = normalizeReindexJob({ id: 'j', status: 4, phase: 11, processedUnits: 100, totalUnits: 100 });
  assert.equal(done.status, 'Succeeded');
  assert.equal(done.phase, 'Publishing');
  assert.equal(done.detail.terminal, true);
  assert.equal(done.progressPercent, 100);

  const failed = normalizeReindexJob({ id: 'j', status: 6, errorMessage: '维度不匹配' });
  assert.equal(failed.status, 'Failed');
  assert.equal(failed.success, false);
  assert.equal(failed.currentMessage, '维度不匹配');

  const cancelled = normalizeReindexJob({ id: 'j', status: 7 });
  assert.equal(cancelled.status, 'Cancelled');
  assert.equal(cancelled.success, false);

  // 越界数值兜底，绝不产生 undefined 状态
  const overflow = normalizeReindexJob({ status: 99, phase: 99 });
  assert.equal(overflow.status, 'Queued');
  assert.equal(overflow.phase, 'None');
  // 空值安全（缺失/越界 → 枚举零值 Queued/None，绝不产生 undefined 状态）
  assert.equal(normalizeReindexJob(null).jobId, '');
  assert.equal(normalizeReindexJob(undefined).status, 'Queued');
});

test('G-M7 枚举序与后端 JobStatus/OperationPhase 完全一致', () => {
  assert.deepEqual(JOB_STATUS_NAMES, ['Queued', 'Running', 'WaitingForInput', 'Cancelling', 'Succeeded', 'PartiallySucceeded', 'Failed', 'Cancelled']);
  assert.deepEqual(OPERATION_PHASE_NAMES, ['None', 'Scanning', 'Probing', 'Parsing', 'Profiling', 'Planning', 'Chunking', 'Rendering', 'Embedding', 'Projecting', 'Validating', 'Publishing', 'Quiescing', 'Invalidating', 'Building']);
  for (const s of ['Queued', 'Running', 'WaitingForInput', 'Cancelling']) assert.ok(isActiveStatus(s), `${s} 应为活跃态`);
  for (const s of ['Succeeded', 'PartiallySucceeded', 'Failed', 'Cancelled']) {
    assert.ok(isTerminalStatus(s), `${s} 应为终态`);
    assert.equal(isActiveStatus(s), false);
  }
});

test('G-M7 percentOf 钳制 [0,100] 与显式 percent 优先', () => {
  assert.equal(percentOf(0, 0, undefined), 0);
  assert.equal(percentOf(50, 200, undefined), 25);
  assert.equal(percentOf(300, 200, undefined), 100);
  assert.equal(percentOf(-10, 200, undefined), 0);
  assert.equal(percentOf(1, 200, 88), 88);
});
