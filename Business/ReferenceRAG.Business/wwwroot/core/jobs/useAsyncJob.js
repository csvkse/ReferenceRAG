import { ref, computed, onMounted, onUnmounted, unref } from 'vue';
import { JobCenter } from './JobCenter.js';

/**
 * 通用异步任务 Vue 3 组合式 Hook (useAsyncJob)
 * 统一承载：
 * 1. 任务发起时乐观进入 Running 态，消除前端与后台之间的短暂状态盲区；
 * 2. 状态机 Phase 与进度百分比实时响应式映射；
 * 3. 关联资源失效自动静默刷新；
 * 4. 表格/列表多项资源并发忙碌态检测。
 */
export function useAsyncJob(targetResourceOrOptions = 'global', maybeOptions = {}) {
  let targetResource = 'global';
  let options = {};

  if (typeof targetResourceOrOptions === 'string' || (targetResourceOrOptions && typeof targetResourceOrOptions.value !== 'undefined')) {
    targetResource = targetResourceOrOptions;
    options = maybeOptions || {};
  } else if (typeof targetResourceOrOptions === 'object' && targetResourceOrOptions !== null) {
    options = targetResourceOrOptions;
    targetResource = options.targetResource || 'global';
  }

  const isLocalRunning = ref(false);
  const localError = ref(null);

  // 解析资源标识（支持 Ref、函数 getter 或普通字符串）
  const getResourceKey = () => {
    const val = unref(targetResource);
    return typeof val === 'function' ? val() : (val || 'global');
  };

  // 活跃任务对象（响应式跟踪 JobCenter 中的当前任务）
  const job = computed(() => {
    const key = getResourceKey();
    return JobCenter.getActiveJob(key);
  });

  // 综合判断是否处于运行中
  const isRunning = computed(() => {
    return isLocalRunning.value || Boolean(job.value && job.value.status === 'Running');
  });

  // 实时进度百分比 (0 - 100)
  const progressPercent = computed(() => {
    return job.value?.progressPercent ?? (isLocalRunning.value ? 5 : 0);
  });

  // 实时所处阶段 (Preparing / Chunking / Embedding / Committing / Completed 等)
  const phase = computed(() => {
    return job.value?.phase ?? (isLocalRunning.value ? 'Preparing' : '');
  });

  // 阶段或进度提示文本
  const currentMessage = computed(() => {
    return job.value?.currentMessage ?? '';
  });

  /**
   * 触发执行一个异步长任务
   * @param {() => Promise<any>} triggerApiFn 异步 API 请求函数
   * @param {Object} jobMeta { jobType, message, targetResource }
   */
  const runJob = async (triggerApiFn, jobMeta = {}) => {
    const resKey = jobMeta.targetResource || getResourceKey();
    const type = jobMeta.jobType || options.defaultJobType || 'Default';
    isLocalRunning.value = true;
    localError.value = null;

    // 1. 注册前台乐观任务，让 UI 按钮和指示器立即进入 Running 状态
    const optimisticJob = JobCenter.registerStartedJob({
      targetResource: resKey,
      jobType: type,
      message: jobMeta.message || '任务启动中...'
    });

    try {
      const response = await triggerApiFn();
      const jobId = response?.data?.jobId || response?.data?.id;
      if (jobId && optimisticJob) {
        JobCenter.replaceJobId(optimisticJob.jobId, jobId);
        optimisticJob.jobId = jobId;
      }
      return response;
    } catch (err) {
      localError.value = err;
      // 调用失败时主动清理乐观状态，避免按钮假死
      await JobCenter._handleJobFinished({
        jobId: optimisticJob?.jobId || 'local_failed',
        targetResource: resKey,
        jobType: type,
        status: 'Faulted',
        success: false
      });
      throw err;
    } finally {
      isLocalRunning.value = false;
    }
  };

  /**
   * 辅助函数：判断指定资源是否处于执行状态（专用于表格多行复用）
   * @param {string} resourceKey 资源标识，例如 "source:新建文件夹"
   * @param {string} [jobType] 可选任务类型过滤，例如 "Rechunk"
   */
  const isResourceBusy = (resourceKey, jobType) => {
    const activeJob = JobCenter.getActiveJob(resourceKey);
    if (!activeJob || activeJob.status !== 'Running') return false;
    if (jobType && activeJob.jobType !== jobType) return false;
    return true;
  };

  /**
   * 辅助函数：获取指定资源的活跃任务信息
   */
  const getResourceJob = (resourceKey) => {
    return JobCenter.getActiveJob(resourceKey);
  };

  let unsubscribe = null;
  onMounted(() => {
    if (options.onProgress || options.onCompleted || options.onFailed) {
      unsubscribe = JobCenter.subscribeJob(getResourceKey(), {
        onProgress: options.onProgress,
        onCompleted: options.onCompleted,
        onFailed: options.onFailed
      });
    }
  });

  onUnmounted(() => {
    if (unsubscribe) {
      unsubscribe();
      unsubscribe = null;
    }
  });

  return {
    job,
    isRunning,
    progressPercent,
    phase,
    currentMessage,
    error: localError,
    runJob,
    isResourceBusy,
    getResourceJob,
    JobCenter
  };
}
