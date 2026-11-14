/**
 * 索引作业源适配器（适配器模式）。
 * 承载原 JobCenter._normalizeJob 的索引专用归一化逻辑与 IndexStarted/Progress/Completed 事件，
 * Dashboard / Sources 行为零变化。产出 UnifiedJob（sourceKey:'index'）。
 */
export const IndexJobSource = {
  key: 'index',
  events: {
    started: 'IndexStarted',
    progress: 'IndexProgress',
    completed: 'IndexCompleted'
  },
  // 旧版完成事件仅触发一次对账
  legacyEvents: { IndexComplete: 'sync' },

  normalize(raw) {
    const data = raw || {};
    const jobId = data.jobId || data.indexId || '';
    const targetResource = data.targetResource ||
      (data.sources?.length === 1 ? `source:${data.sources[0]}` : 'global');

    const progressPercent = data.progressPercent !== undefined ? Math.round(data.progressPercent) : 0;
    const phase = data.phase || (data.status === 'Completed' ? 'Completed' : 'Running');
    const jobType = data.jobType || (data.rechunk ? 'Rechunk' : (data.vectorOnly ? 'VectorOnly' : 'Incremental'));

    return {
      jobId,
      sourceKey: 'index',
      targetResource,
      jobType,
      status: data.status || 'Running',
      phase,
      progressPercent,
      currentMessage: data.currentFile || data.message || '',
      success: data.success ?? (data.errors?.length ? false : true),
      detail: {
        sources: data.sources ?? null,
        currentFile: data.currentFile ?? null,
        errors: data.errors ?? null
      }
    };
  },

  async fetchActive(api) {
    const res = await api.getActive();
    const list = res?.data || [];
    return list.map(item => this.normalize(item));
  }
};