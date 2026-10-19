import { reactive, ref } from '../../vendor/vue.js';
import { getInvalidationTags } from './InvalidationRegistry.js';

class JobCenterEngine {
  constructor() {
    this.connection = null;
    this.isConnected = ref(false);
    this._api = null;
    
    // 响应式任务字典: jobId -> UnifiedJob
    this.activeJobs = reactive(new Map());
    
    // 资源标识 -> UnifiedJob (例如 "source:新建文件夹" -> job)
    this.activeJobsByResource = reactive(new Map());
    
    // 资源刷新器注册表: tag -> Set<() => Promise<void>>
    this.resourceRefreshers = new Map();
    
    // 任务单次回调监听器: jobId/resource -> Set<{ onProgress, onCompleted, onFailed }>
    this.jobListeners = new Map();
    
    // 响应式版本号，用于确保任何 computed/template 追踪到 Map 的变动
    this.version = ref(0);

    this.syncTimer = null;
    this.isInitializing = false;
  }

  setApi(api) {
    this._api = api;
  }

  async getApi() {
    if (this._api) return this._api;
    try {
      const mod = await import('../../app/api/index.js');
      return mod.indexJobsApi;
    } catch {
      return null;
    }
  }

  /**
   * 初始化全局通信与监听
   */
  async initialize(customConnection = null) {
    if (this.connection || this.isInitializing) return;
    this.isInitializing = true;
    
    try {
      if (customConnection) {
        this.connection = customConnection;
      } else {
        const { createIndexConnection } = await import('../transport/index-events.js');
        this.connection = createIndexConnection();
      }
      
      this.connection.onreconnected(async () => {
        try {
          await this.connection?.invoke('JoinIndexGroup');
        } catch {}
        await this.syncActiveJobs();
      });

      // 1. 任务启动事件
      this.connection.on('IndexStarted', (data) => {
        const job = this._normalizeJob(data);
        this._setJob(job);
      });

      // 2. 进度与阶段跃迁事件
      this.connection.on('IndexProgress', (data) => {
        const job = this._normalizeJob(data);
        this._updateJob(job);
      });

      // 3. 任务完成事件 (包含阶段与资源)
      this.connection.on('IndexCompleted', async (data) => {
        const job = this._normalizeJob(data);
        await this._handleJobFinished(job);
      });

      // 4. 兼容旧事件
      this.connection.on('IndexComplete', async () => {
        await this.syncActiveJobs();
      });

      await this.connection.start();
      this.isConnected.value = true;
      try {
        await this.connection.invoke('JoinIndexGroup');
      } catch {}
      
      // 同步当前活跃任务
      await this.syncActiveJobs();
    } catch (err) {
      console.warn('[JobCenter] 通信连接异常，已启动自适应轮询兜底:', err);
      this.isConnected.value = false;
    } finally {
      this.isInitializing = false;
    }
  }

  /**
   * 注册资源自动刷新器
   * 当关联该标签的任务完成时，自动触发回调刷新视图
   */
  registerRefresher(tag, callback) {
    if (!this.resourceRefreshers.has(tag)) {
      this.resourceRefreshers.set(tag, new Set());
    }
    const set = this.resourceRefreshers.get(tag);
    set.add(callback);

    // 返回解绑函数
    return () => {
      set.delete(callback);
      if (set.size === 0) {
        this.resourceRefreshers.delete(tag);
      }
    };
  }

  /**
   * 手动触发指定标签的刷新
   */
  async triggerRefresh(tags) {
    const tagList = Array.isArray(tags) ? tags : [tags];
    const callbacksToRun = new Set();
    
    for (const tag of tagList) {
      const set = this.resourceRefreshers.get(tag);
      if (set) {
        for (const cb of set) {
          callbacksToRun.add(cb);
        }
      }
    }

    // 并发静默执行刷新
    await Promise.allSettled(Array.from(callbacksToRun).map(cb => {
      try {
        return Promise.resolve(cb());
      } catch (err) {
        console.error('[JobCenter] 刷新回调异常:', err);
        return Promise.resolve();
      }
    }));
  }

  /**
   * 监听特定资源或任务的完成事件
   */
  subscribeJob(resourceOrJobId, listener) {
    if (!this.jobListeners.has(resourceOrJobId)) {
      this.jobListeners.set(resourceOrJobId, new Set());
    }
    const set = this.jobListeners.get(resourceOrJobId);
    set.add(listener);

    return () => {
      set.delete(listener);
      if (set.size === 0) {
        this.jobListeners.delete(resourceOrJobId);
      }
    };
  }

  /**
   * 注册前台新发起的任务（实现瞬时感知）
   */
  registerStartedJob({ jobId, targetResource, jobType, message }) {
    const job = {
      jobId: jobId || `local-${Date.now()}`,
      targetResource: targetResource || 'global',
      jobType: jobType || 'Rechunk',
      status: 'Running',
      phase: 'Preparing',
      progressPercent: 5,
      currentMessage: message || '任务启动中...',
      startedAt: new Date().toISOString()
    };
    this._setJob(job);
    this._ensurePolling();
    return job;
  }

  /**
   * 获取某资源当前正在执行的任务（若有）
   */
  getActiveJob(resourceKey) {
    this.version.value; // 建立响应式依赖
    return this.activeJobsByResource.get(resourceKey) || 
      (resourceKey !== 'global' ? this.activeJobsByResource.get('global') : null) || null;
  }

  /**
   * 判断某资源是否正在忙
   */
  isResourceBusy(resourceKey) {
    this.version.value; // 建立响应式依赖
    if (this.activeJobsByResource.has('global')) return true;
    return this.activeJobsByResource.has(resourceKey);
  }

  /**
   * 将前台乐观生成的任务ID无缝替换为服务端返回的真实任务ID
   */
  replaceJobId(oldJobId, newJobId) {
    if (!oldJobId || !newJobId || oldJobId === newJobId) return;
    const job = this.activeJobs.get(oldJobId);
    if (job) {
      this.activeJobs.delete(oldJobId);
      job.jobId = newJobId;
      this.activeJobs.set(newJobId, job);
      if (job.targetResource) {
        this.activeJobsByResource.set(job.targetResource, job);
      }
      this.version.value++;
    }
  }

  /**
   * 同步当前活跃任务
   */
  async syncActiveJobs() {
    try {
      const api = await this.getApi();
      if (!api) return;
      const res = await api.getActive();
      const list = res?.data || [];
      const currentIds = new Set(list.map(j => j.jobId));
      
      // 清除已不在服务端的任务（注意保护尚在等待接口响应的本地乐观任务 local-*）
      const now = Date.now();
      for (const [id, job] of Array.from(this.activeJobs.entries())) {
        if (id.startsWith('local-')) {
          const timestamp = parseInt(id.replace('local-', ''), 10);
          if (now - timestamp > 15000) {
            // 超时 15 秒仍未被真实任务替换，判定异常结束
            await this._handleJobFinished({ ...job, status: 'Faulted', success: false });
          }
          continue;
        }

        if (!currentIds.has(id)) {
          await this._handleJobFinished({ ...job, status: 'Completed', success: true });
        }
      }

      // 添加或更新活跃任务
      for (const item of list) {
        const job = this._normalizeJob(item);
        this._setJob(job);
      }

      this._ensurePolling();
    } catch (err) {
      console.warn('[JobCenter] 同步任务失败:', err);
    }
  }

  _setJob(job) {
    this.activeJobs.set(job.jobId, job);
    if (job.targetResource) {
      this.activeJobsByResource.set(job.targetResource, job);
    }
    this.version.value++;
    this._dispatchListeners(job, 'onProgress');
  }

  _updateJob(job) {
    const existing = this.activeJobs.get(job.jobId);
    const updated = { ...(existing || {}), ...job };
    this.activeJobs.set(job.jobId, updated);
    if (updated.targetResource) {
      this.activeJobsByResource.set(updated.targetResource, updated);
    }
    this.version.value++;
    this._dispatchListeners(updated, 'onProgress');
  }

  async _handleJobFinished(job) {
    const targetResource = job.targetResource || this.activeJobs.get(job.jobId)?.targetResource;
    const jobType = job.jobType || this.activeJobs.get(job.jobId)?.jobType || 'Default';

    // 1. 移除状态
    this.activeJobs.delete(job.jobId);
    if (targetResource) {
      this.activeJobsByResource.delete(targetResource);
    }
    this.version.value++;

    // 2. 分发任务监听
    this._dispatchListeners(job, job.success !== false ? 'onCompleted' : 'onFailed');

    // 3. 自动触发依赖资源失效并刷新
    const tags = getInvalidationTags(jobType);
    await this.triggerRefresh(tags);

    this._ensurePolling();
  }

  _dispatchListeners(job, callbackName) {
    const targets = [job.jobId, job.targetResource, '*'].filter(Boolean);
    for (const key of targets) {
      const listeners = this.jobListeners.get(key);
      if (listeners) {
        for (const listener of Array.from(listeners)) {
          try {
            listener[callbackName]?.(job);
          } catch (err) {
            console.error(`[JobCenter] Listener ${callbackName} error:`, err);
          }
        }
      }
    }
  }

  _ensurePolling() {
    if (this.activeJobs.size > 0 && !this.syncTimer) {
      this.syncTimer = setInterval(() => {
        this.syncActiveJobs();
      }, 2000);
    } else if (this.activeJobs.size === 0 && this.syncTimer) {
      clearInterval(this.syncTimer);
      this.syncTimer = null;
    }
  }

  _normalizeJob(data) {
    const jobId = data.jobId || data.indexId || '';
    const targetResource = data.targetResource || 
      (data.sources?.length === 1 ? `source:${data.sources[0]}` : 'global');
    
    const progressPercent = data.progressPercent !== undefined ? Math.round(data.progressPercent) : 0;
    const phase = data.phase || (data.status === 'Completed' ? 'Completed' : 'Running');
    const jobType = data.jobType || (data.rechunk ? 'Rechunk' : (data.vectorOnly ? 'VectorOnly' : 'Incremental'));

    return {
      jobId,
      targetResource,
      jobType,
      status: data.status || 'Running',
      phase,
      progressPercent,
      currentMessage: data.currentFile || data.message || '',
      success: data.success ?? (data.errors?.length ? false : true)
    };
  }
}

export const JobCenter = new JobCenterEngine();
