/**
 * 知识库重构作业源适配器（适配器模式，G-a/G-h 修复）。
 * 订阅后端 ReindexJobStarted/Progress/Completed 广播（P1-1），归一化为 UnifiedJob（sourceKey:'kb'，
 * targetResource:`kb:{id}`）。fetchActive 拉取 /api/jobs 并按 operation==='reindex' + 活跃态客户端过滤
 * （JobsController 不支持 operation 查询参数）。
 */
import { normalizeReindexJob, isActiveStatus } from '../reindex-model.js';

export const KbJobSource = {
  key: 'kb',
  events: {
    started: 'ReindexJobStarted',
    progress: 'ReindexJobProgress',
    completed: 'ReindexJobCompleted'
  },

  normalize(raw) {
    return normalizeReindexJob(raw);
  },

  async fetchActive(api) {
    const res = await api.listJobs();
    const list = res?.data || [];
    return list
      .map(item => normalizeReindexJob(item))
      .filter(job => isActiveStatus(job.status));
  }
};