/**
 * Reindex 作业纯函数模型（G-M7 契约核心）。
 * 统一归一化来自 SignalR 广播（camelCase + 字符串枚举）与 REST /api/jobs（camelCase + 字符串枚举，
 * 经 axios 拦截器转换）以及防御性的 PascalCase / 数字枚举载荷，产出 JobCenter 消费的 UnifiedJob。
 * 无任何 Vue / 网络依赖，可直接单测。
 */

// JobStatus 枚举序（与后端 KnowledgeBaseModels.cs 保持一致）
export const JOB_STATUS_NAMES = [
  'Queued', 'Running', 'WaitingForInput', 'Cancelling',
  'Succeeded', 'PartiallySucceeded', 'Failed', 'Cancelled'
];

// OperationPhase 枚举序
export const OPERATION_PHASE_NAMES = [
  'None', 'Scanning', 'Probing', 'Parsing', 'Profiling', 'Planning',
  'Chunking', 'Rendering', 'Embedding', 'Projecting', 'Validating',
  'Publishing', 'Quiescing', 'Invalidating', 'Building'
];

export const TERMINAL_STATUSES = ['Succeeded', 'PartiallySucceeded', 'Failed', 'Cancelled'];
export const ACTIVE_STATUSES = ['Queued', 'Running', 'WaitingForInput', 'Cancelling'];

const STATUS_LABELS = {
  Queued: '排队中', Running: '进行中', WaitingForInput: '等待确认', Cancelling: '取消中',
  Succeeded: '已完成', PartiallySucceeded: '部分完成', Failed: '失败', Cancelled: '已取消'
};

const PHASE_LABELS = {
  None: '空闲', Scanning: '扫描', Probing: '探测', Parsing: '解析', Profiling: '画像',
  Planning: '规划', Chunking: '分段', Rendering: '渲染', Embedding: '向量化',
  Projecting: '投影', Validating: '校验', Publishing: '发布', Quiescing: '静默',
  Invalidating: '失效', Building: '构建'
};

/** 数字枚举 → 名称；字符串原样返回；缺失/越界 → fallback */
export function enumName(value, names, fallback = 'Unknown') {
  if (typeof value === 'number') return names[value] ?? fallback;
  if (typeof value === 'string' && value.length > 0) return value;
  return fallback;
}

export function isTerminalStatus(status) {
  return TERMINAL_STATUSES.includes(status);
}

export function isActiveStatus(status) {
  return ACTIVE_STATUSES.includes(status);
}

export function statusLabel(status) {
  return STATUS_LABELS[status] ?? status ?? '';
}

export function phaseLabel(phase) {
  return PHASE_LABELS[phase] ?? phase ?? '';
}

/** 进度百分比：优先显式 percent，否则按 processed/total 计算；钳制 [0,100] */
export function percentOf(processedUnits, totalUnits, explicitPercent) {
  let pct;
  if (typeof explicitPercent === 'number' && !Number.isNaN(explicitPercent)) {
    pct = explicitPercent;
  } else if (typeof totalUnits === 'number' && totalUnits > 0) {
    pct = (100 * (processedUnits || 0)) / totalUnits;
  } else {
    pct = 0;
  }
  return Math.max(0, Math.min(100, pct));
}

function pick(raw, keys) {
  for (const k of keys) {
    if (raw[k] !== undefined && raw[k] !== null) return raw[k];
  }
  return undefined;
}

/**
 * 归一化 reindex 作业载荷 → UnifiedJob（sourceKey:'kb'）。
 * 兼容 SignalR（jobId/percent）与 REST（id，无 percent 需计算）。
 */
export function normalizeReindexJob(raw) {
  const data = raw || {};
  const jobId = pick(data, ['jobId', 'JobId', 'id', 'Id']) ?? '';
  const knowledgeBaseId = pick(data, ['knowledgeBaseId', 'KnowledgeBaseId']) ?? '';
  const operation = pick(data, ['operation', 'Operation']) ?? 'reindex';
  const status = enumName(pick(data, ['status', 'Status']), JOB_STATUS_NAMES, 'Queued');
  const phase = enumName(pick(data, ['phase', 'Phase']), OPERATION_PHASE_NAMES, 'None');
  const processedUnits = pick(data, ['processedUnits', 'ProcessedUnits']) ?? 0;
  const totalUnits = pick(data, ['totalUnits', 'TotalUnits']) ?? 0;
  const explicitPercent = pick(data, ['percent', 'Percent', 'progressPercent', 'ProgressPercent']);
  const errorMessage = pick(data, ['errorMessage', 'ErrorMessage']) ?? null;
  const createdAt = pick(data, ['createdAt', 'CreatedAt']) ?? null;
  const finishedAt = pick(data, ['finishedAt', 'FinishedAt']) ?? null;

  const percent = percentOf(processedUnits, totalUnits, explicitPercent);
  const terminal = isTerminalStatus(status);
  const success = status === 'Failed' || status === 'Cancelled' ? false : true;

  return {
    jobId,
    sourceKey: 'kb',
    targetResource: knowledgeBaseId ? `kb:${knowledgeBaseId}` : 'global',
    jobType: 'Reindex',
    status,
    phase,
    progressPercent: Math.round(percent),
    currentMessage: errorMessage || phaseLabel(phase),
    success,
    detail: {
      knowledgeBaseId,
      operation,
      processedUnits,
      totalUnits,
      percent,
      errorMessage,
      createdAt,
      finishedAt,
      terminal
    }
  };
}

/**
 * UnifiedJob → 抽屉展示快照（reindex-shaped）。
 * 模板消费 id/status/phase/processedUnits/totalUnits/errorMessage，与后端 REST 作业字段对齐。
 */
export function toReindexShape(unified) {
  const u = unified || {};
  const d = u.detail || {};
  return {
    id: u.jobId ?? '',
    jobId: u.jobId ?? '',
    status: u.status ?? 'Queued',
    phase: u.phase ?? 'None',
    processedUnits: d.processedUnits ?? 0,
    totalUnits: d.totalUnits ?? 0,
    percent: d.percent ?? u.progressPercent ?? 0,
    errorMessage: d.errorMessage ?? null,
    createdAt: d.createdAt ?? null,
    finishedAt: d.finishedAt ?? null
  };
}