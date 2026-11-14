import { reactive, ref } from 'vue';
import { getInvalidationTags } from './InvalidationRegistry.js';
import { IndexJobSource } from './sources/IndexJobSource.js';
import { KbJobSource } from './sources/KbJobSource.js';

class JobCenterEngine {
  constructor() {
    this.connection = null;
    this.isConnected = ref(false);
    // 按作业源缓存 api：index -> indexJobsApi，kb -> knowledgeBasesApi
    this._apis = {};

    // 作业源适配器（适配器模式）：统一产出 UnifiedJob
    this._sources = [IndexJobSource, KbJobSource];
    
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
    this._syncInterval = null;
    this.isInitializing = false;

    // 生命周期策略（P1-4）：页面可见性 + 连接态共同决定对账节奏
    this._doc = typeof document !== 'undefined' ? document : null;
    this._visible = this._doc ? !this._doc.hidden : true;
    this._onVisibilityChange = null;
    this._pendingRefreshTags = new Set();
  }

  setApi(api, sourceKey = 'index') {
    this._apis[sourceKey] = api;
  }

  async getApi(sourceKey = 'index') {
    if (this._apis[sourceKey]) return this._apis[sourceKey];
    try {
      const mod = await import('../../app/api/index.js');
      const api = sourceKey === 'kb' ? mod.knowledgeBasesApi : mod.indexJobsApi;
      this._apis[sourceKey] = api;
      return api;
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
      
      this._bindLifecycle();
      this._registerSourceEvents();

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
      this._scheduleSync();
    } finally {
      this.isInitializing = false;
    }
  }

  /**
   * 遍历作业源注册推送事件（started→登记，progress→更新，completed→收敛）。
   */
  _registerSourceEvents() {
    for (const source of this._sources) {
      const { started, progress, completed } = source.events;
      if (started) {
        this.connection.on(started, (data) => this._setJob(source.normalize(data)));
      }
      if (progress) {
        this.connection.on(progress, (data) => this._updateJob(source.normalize(data)));
      }
      if (completed) {
        this.connection.on(completed, async (data) => {
          await this._handleJobFinished(source.normalize(data));
        });
      }
      for (const [eventName, action] of Object.entries(source.legacyEvents || {})) {
        if (action === 'sync') {
          this.connection.on(eventName, async () => { await this.syncActiveJobs(); });
        }
      }
    }
  }

  /**
   * 生命周期策略（P1-4 / §6.3）：
   * - 页面 hidden → 暂停一切定时器（推送事件仍即时处理，0 HTTP 请求）；visible → 立即对账并恢复。
   * - 连接断开/重连中 → 2s 自适应轮询兜底；重连成功 → 重新入组 + 立即对账 + 回到 10s 心跳。
   */
  _bindLifecycle() {
    if (this._doc) {
      this._onVisibilityChange = () => {
        this._visible = !this._doc.hidden;
        if (this._visible) {
          this._flushPendingRefresh();
          this.syncActiveJobs();
        } else {
          this._clearSyncTimer();
        }
      };
      this._doc.addEventListener('visibilitychange', this._onVisibilityChange);
      this._visible = !this._doc.hidden;
    }

    this.connection.onclose?.(() => {
      this.isConnected.value = false;
      this._scheduleSync();
    });
    this.connection.onreconnecting?.(() => {
      this.isConnected.value = false;
      this._scheduleSync();
    });
    this.connection.onreconnected?.(async () => {
      this.isConnected.value = true;
      try { await this.connection?.invoke('JoinIndexGroup'); } catch {}
      await this.syncActiveJobs();
      this._scheduleSync();
    });
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
  registerStartedJob({ jobId, targetResource, jobType, message, sourceKey }) {
    const type = jobType || 'Rechunk';
    const resolvedSource = sourceKey || (type === 'Reindex' ? 'kb' : 'index');
    const job = {
      jobId: jobId || `local-${Date.now()}`,
      sourceKey: resolvedSource,
      targetResource: targetResource || 'global',
      jobType: type,
      status: 'Running',
      phase: 'Preparing',
      progressPercent: 5,
      currentMessage: message || '任务启动中...',
      startedAt: new Date().toISOString(),
      detail: null
    };
    this._setJob(job);
    this._scheduleSync();
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
   * 精确获取某资源的任务（无 global 兜底）——供 KB 卡片徽标与重构抽屉使用，避免全局作业误报。
   */
  getExactJob(resourceKey) {
    this.version.value; // 建立响应式依赖
    return this.activeJobsByResource.get(resourceKey) || null;
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
    if (!job) return;

    // 真实 jobId 已被推送广播先行登记 → 直接丢弃乐观条目，避免覆盖更完整的服务端状态
    if (this.activeJobs.has(newJobId)) {
      this.activeJobs.delete(oldJobId);
      this.version.value++;
      return;
    }

    this.activeJobs.delete(oldJobId);
    job.jobId = newJobId;
    this.activeJobs.set(newJobId, job);
    if (job.targetResource) {
      this.activeJobsByResource.set(job.targetResource, job);
    }
    this.version.value++;
  }

  /**
   * 同步当前活跃任务
   */
  async syncActiveJobs() {
    for (const source of this._sources) {
      try {
        const api = await this.getApi(source.key);
        if (!api) continue;
        const list = await source.fetchActive(api);
        await this._reconcileSource(source.key, list);
      } catch (err) {
        console.warn(`[JobCenter] 同步 ${source.key} 作业失败:`, err);
      }
    }
    this._scheduleSync();
  }

  /**
   * 单源对账：清理服务端已不活跃的作业（保护 15s 内的本地乐观任务），登记/更新活跃作业。
   * 仅处理本源作业，避免索引对账误删知识库重构作业（反之亦然）。
   */
  async _reconcileSource(sourceKey, list) {
    const currentIds = new Set(list.map(j => j.jobId));
    const now = Date.now();

    for (const [id, job] of Array.from(this.activeJobs.entries())) {
      if ((job.sourceKey || 'index') !== sourceKey) continue;

      if (id.startsWith('local-')) {
        const timestamp = parseInt(id.replace('local-', ''), 10);
        if (now - timestamp > 15000) {
          // 超时 15 秒仍未被真实任务替换，判定异常结束
          await this._handleJobFinished({ ...job, status: 'Faulted', success: false });
        }
        continue;
      }

      if (!currentIds.has(id)) {
        await this._handleJobFinished({ ...job, status: 'Completed', success: job.success !== false });
      }
    }

    for (const job of list) {
      this._setJob(job);
    }
  }

  _setJob(job) {
    this.activeJobs.set(job.jobId, job);
    if (job.targetResource) {
      this.activeJobsByResource.set(job.targetResource, job);
    }
    this.version.value++;
    this._dispatchListeners(job, 'onProgress');
    this._scheduleSync();
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
    const existing = this.activeJobs.get(job.jobId);
    const targetResource = job.targetResource || existing?.targetResource;
    const jobType = job.jobType || existing?.jobType || 'Default';
    const sourceKey = job.sourceKey || existing?.sourceKey || 'index';

    // 1. 移除状态
    this.activeJobs.delete(job.jobId);
    if (targetResource) {
      this.activeJobsByResource.delete(targetResource);
    }
    this.version.value++;

    // 2. 分发任务监听
    this._dispatchListeners({ ...job, sourceKey }, job.success !== false ? 'onCompleted' : 'onFailed');

    // 3. 自动触发依赖资源失效并刷新（页面不可见时挂起，待 visible 后统一刷新，避免后台空跑）
    const tags = getInvalidationTags(jobType);
    if (this._visible) {
      await this.triggerRefresh(tags);
    } else {
      for (const tag of tags) this._pendingRefreshTags.add(tag);
    }

    this._scheduleSync();
  }

  async _flushPendingRefresh() {
    if (this._pendingRefreshTags.size === 0) return;
    const tags = Array.from(this._pendingRefreshTags);
    this._pendingRefreshTags.clear();
    await this.triggerRefresh(tags);
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

  /**
   * 自适应对账调度（P1-4）：
   * - 页面不可见 → 0 定时器（推送仍即时处理，后台 0 HTTP 请求）。
   * - 可见且有活跃作业 → 连接正常 10s 心跳；连接断开 2s 轮询兜底。
   * - 无活跃作业 → 停止定时器。
   * 幂等：目标节奏未变化时不重建定时器，避免高频推送造成 timer 抖动。
   */
  _scheduleSync() {
    let desired = null;
    if (this._visible && this.activeJobs.size > 0) {
      desired = this.isConnected.value ? 10000 : 2000;
    }
    if (desired === this._syncInterval && (desired === null || this.syncTimer)) return;
    this._clearSyncTimer();
    this._syncInterval = desired;
    if (desired !== null) {
      this.syncTimer = setInterval(() => { this.syncActiveJobs(); }, desired);
    }
  }

  _clearSyncTimer() {
    if (this.syncTimer) {
      clearInterval(this.syncTimer);
      this.syncTimer = null;
    }
    this._syncInterval = null;
  }

  _normalizeJob(data) {
    return IndexJobSource.normalize(data);
  }

  /**
   * 彻底销毁：清理定时器、可见性监听、连接与全部状态（供测试与热重载复位）。
   */
  destroy() {
    this._clearSyncTimer();
    if (this._doc && this._onVisibilityChange) {
      this._doc.removeEventListener('visibilitychange', this._onVisibilityChange);
      this._onVisibilityChange = null;
    }
    try { this.connection?.stop?.(); } catch {}
    this.connection = null;
    this.isConnected.value = false;
    this.activeJobs.clear();
    this.activeJobsByResource.clear();
    this._pendingRefreshTags.clear();
    this._visible = this._doc ? !this._doc.hidden : true;
    this.isInitializing = false;
    this.version.value++;
  }
}

export const JobCenter = new JobCenterEngine();
