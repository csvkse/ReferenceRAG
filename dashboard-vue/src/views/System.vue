<template>
  <n-space vertical :size="20">
    <!-- System Status -->
    <n-card title="系统状态">
      <!-- <template #header-extra>
        <n-space>
          <n-button type="warning" @click="handleRestart" :loading="restartLoading" :disabled="restartLoading">
            <template #icon><n-icon :component="RefreshOutline" /></template>
            重启服务
          </n-button>
        </n-space>
      </template> -->
      <n-spin :show="statusLoading">
        <n-grid :cols="4" :x-gap="20">
          <n-gi>
            <n-statistic label="系统状态">
              <template #prefix>
                <n-icon :color="getStatusColor(systemStatus?.status)">
                  <component :is="getStatusIcon(systemStatus?.status)" />
                </n-icon>
              </template>
              <span :style="{ color: getStatusColor(systemStatus?.status) }">
                {{ getStatusText(systemStatus?.status) }}
              </span>
            </n-statistic>
          </n-gi>
          <n-gi>
            <n-statistic label="活动告警" :value="systemStatus?.activeAlerts?.length || 0">
              <template #suffix>
                <n-text depth="3">个</n-text>
              </template>
            </n-statistic>
          </n-gi>
          <n-gi>
            <n-statistic label="总文件数" :value="indexMetrics?.totalFiles || 0" />
          </n-gi>
          <n-gi>
            <n-statistic label="总切片数" :value="indexMetrics?.totalChunks || 0" />
          </n-gi>
        </n-grid>
      </n-spin>
    </n-card>

    <!-- Active Index Jobs -->
    <n-card title="正在进行的向量索引任务">
      <template #header-extra>
        <n-button text @click="loadActiveJobs">
          <template #icon><n-icon :component="RefreshOutline" /></template>
          刷新
        </n-button>
      </template>
      <n-spin :show="jobsLoading">
        <n-empty v-if="!activeJobs?.length" description="暂无正在进行的索引任务" />
        <n-list v-else>
          <n-list-item v-for="job in activeJobs" :key="job.jobId">
            <n-thing>
              <template #header>
                <n-space align="center">
                  <n-tag :type="getJobStatusType(job.status)" size="small">
                    {{ job.status }}
                  </n-tag>
                  <n-text strong>任务 {{ job.jobId }}</n-text>
                  <n-text depth="3" style="font-size: 12px">
                    ({{ job.sources?.join(', ') || '所有源' }})
                  </n-text>
                </n-space>
              </template>
              <template #description>
                <n-space vertical :size="8">
                  <n-progress
                    type="line"
                    :percentage="job.progressPercent"
                    :status="getProgressStatus(job.status)"
                    :indicator-placement="'inside'"
                  />
                  <n-space>
                    <n-text depth="3">
                      进度: {{ job.processedFiles }} / {{ job.totalFiles }} 文件
                    </n-text>
                    <n-text depth="3" v-if="job.currentFile">
                      | 当前: {{ truncateFileName(job.currentFile) }}
                    </n-text>
                    <n-text depth="3" v-if="job.errors > 0">
                      | 错误: <n-text type="error">{{ job.errors }}</n-text>
                    </n-text>
                  </n-space>
                  <n-space v-if="job.startTime">
                    <n-text depth="3">
                      开始时间: {{ formatTime(job.startTime) }}
                    </n-text>
                    <n-text depth="3" v-if="job.duration">
                      | 已用时: {{ job.duration }}
                    </n-text>
                  </n-space>
                </n-space>
              </template>
              <template #header-extra>
                <n-button
                  v-if="job.status === 'Running' || job.status === 'Pending'"
                  type="error"
                  size="small"
                  :loading="stoppingJobs[job.jobId]"
                  @click="handleStopJob(job.jobId)"
                >
                  中断任务
                </n-button>
              </template>
            </n-thing>
          </n-list-item>
        </n-list>
      </n-spin>
    </n-card>

    <!-- Completed Index Jobs -->
    <n-card>
      <template #header>
        <n-space align="center">
          <n-text>已完成/已取消的索引任务</n-text>
          <n-tag size="small" type="info">最多保留 20 条</n-tag>
        </n-space>
      </template>
      <template #header-extra>
        <n-space>
          <n-button
            type="error"
            text
            :disabled="!completedJobs?.length"
            :loading="clearingJobs"
            @click="handleClearCompletedJobs"
          >
            <template #icon><n-icon :component="TrashOutline" /></template>
            清空记录
          </n-button>
        </n-space>
      </template>
      <n-spin :show="jobsLoading">
        <n-empty v-if="!completedJobs?.length" description="暂无已完成的索引记录" />
        <n-list v-else>
          <n-list-item v-for="job in completedJobs" :key="job.jobId">
            <n-thing>
              <template #header>
                <n-space align="center">
                  <n-tag :type="getJobStatusType(job.status)" size="small">
                    {{ job.status === 'Completed' ? '已完成' : '已取消' }}
                  </n-tag>
                  <n-text strong>任务 {{ job.jobId }}</n-text>
                  <n-text depth="3" style="font-size: 12px">
                    ({{ job.sources?.join(', ') || '所有源' }})
                  </n-text>
                </n-space>
              </template>
              <template #description>
                <n-space>
                  <n-text depth="3">
                    处理: {{ job.processedFiles }} / {{ job.totalFiles }} 文件
                  </n-text>
                  <n-text depth="3" v-if="job.errors > 0">
                    | 错误: <n-text type="error">{{ job.errors }}</n-text>
                  </n-text>
                  <n-text depth="3" v-if="job.duration">
                    | 耗时: {{ job.duration }}
                  </n-text>
                </n-space>
              </template>
              <template #header-extra>
                <n-text depth="3" style="font-size: 12px">
                  {{ formatTime(job.endTime) }}
                </n-text>
              </template>
            </n-thing>
          </n-list-item>
        </n-list>
      </n-spin>
    </n-card>

    <!-- Query Metrics -->
    <n-card title="查询指标">
      <n-spin :show="metricsLoading">
        <n-grid :cols="5" :x-gap="20">
          <n-gi>
            <n-statistic label="总查询次数" :value="queryMetrics?.totalQueries || 0" />
          </n-gi>
          <n-gi>
            <n-statistic label="平均延迟">
              <template #default>
                {{ (queryMetrics?.avgQueryLatencyMs || 0).toFixed(1) }}
              </template>
              <template #suffix>ms</template>
            </n-statistic>
          </n-gi>
          <n-gi>
            <n-statistic label="P95延迟">
              <template #default>
                {{ (queryMetrics?.p95QueryLatencyMs || 0).toFixed(1) }}
              </template>
              <template #suffix>ms</template>
            </n-statistic>
          </n-gi>
          <n-gi>
            <n-statistic label="P99延迟">
              <template #default>
                {{ (queryMetrics?.p99QueryLatencyMs || 0).toFixed(1) }}
              </template>
              <template #suffix>ms</template>
            </n-statistic>
          </n-gi>
          <n-gi>
            <n-statistic label="平均结果数">
              <template #default>
                {{ (queryMetrics?.avgResultsPerQuery || 0).toFixed(1) }}
              </template>
            </n-statistic>
          </n-gi>
        </n-grid>
      </n-spin>
    </n-card>

    <!-- System Metrics -->
    <n-card title="系统资源">
      <n-spin :show="systemMetricsLoading">
        <n-grid :cols="4" :x-gap="20">
          <n-gi>
            <n-statistic label="CPU使用率">
              <template #default>
                {{ (systemMetrics?.cpuUsagePercent || 0).toFixed(1) }}
              </template>
              <template #suffix>%</template>
            </n-statistic>
          </n-gi>
          <n-gi>
            <n-statistic label="内存使用">
              <template #default>
                {{ formatBytes(systemMetrics?.memoryUsedBytes || 0) }}
              </template>
              <template #suffix>
                <n-text depth="3">/ {{ formatBytes(systemMetrics?.memoryTotalBytes || 0) }}</n-text>
              </template>
            </n-statistic>
          </n-gi>
          <n-gi>
            <n-statistic label="磁盘使用">
              <template #default>
                {{ formatBytes(systemMetrics?.diskUsedBytes || 0) }}
              </template>
              <template #suffix>
                <n-text depth="3">/ {{ formatBytes(systemMetrics?.diskTotalBytes || 0) }}</n-text>
              </template>
            </n-statistic>
          </n-gi>
          <n-gi>
            <n-statistic label="运行时间">
              <template #default>
                {{ formatUptime(systemMetrics?.uptimeSeconds || 0) }}
              </template>
            </n-statistic>
          </n-gi>
        </n-grid>
      </n-spin>
    </n-card>

    <!-- Active Alerts -->
    <n-card title="活动告警">
      <template #header-extra>
        <n-button text @click="loadAlerts">
          <template #icon><n-icon :component="RefreshOutline" /></template>
          刷新
        </n-button>
      </template>
      <n-spin :show="alertsLoading">
        <n-empty v-if="!alerts?.length" description="暂无活动告警" />
        <n-list v-else>
          <n-list-item v-for="alert in alerts" :key="alert.name">
            <n-thing>
              <template #header>
                <n-space align="center">
                  <n-tag :type="getAlertSeverityType(alert.severity)" size="small">
                    {{ alert.severity }}
                  </n-tag>
                  <n-text>{{ alert.name }}</n-text>
                </n-space>
              </template>
              <template #description>
                <n-text depth="3">{{ alert.message }}</n-text>
              </template>
              <template #header-extra>
                <n-text depth="3" style="font-size: 12px">
                  {{ formatTime(alert.triggeredAt) }}
                </n-text>
              </template>
            </n-thing>
          </n-list-item>
        </n-list>
      </n-spin>
    </n-card>

    <!-- Health Check -->
    <n-card title="健康检查">
      <n-space>
        <n-button type="primary" :loading="healthLoading" @click="checkHealth">
          执行健康检查
        </n-button>
        <n-button @click="checkAlerts" :loading="checkingAlerts">
          检查告警
        </n-button>
      </n-space>
      <n-card v-if="healthResult" size="small" style="margin-top: 16px">
        <n-descriptions :column="3" label-placement="left">
          <n-descriptions-item label="状态">
            <n-tag :type="healthResult.status === 'healthy' ? 'success' : 'error'">
              {{ healthResult.status }}
            </n-tag>
          </n-descriptions-item>
          <n-descriptions-item label="版本">{{ healthResult.version }}</n-descriptions-item>
          <n-descriptions-item label="检查时间">{{ formatTime(healthResult.timestamp) }}</n-descriptions-item>
        </n-descriptions>
      </n-card>
    </n-card>
  </n-space>
</template>

<script setup lang="ts">
import { ref, onMounted, onUnmounted, reactive } from 'vue'
import { useMessage, useDialog } from 'naive-ui'
import {
  CheckmarkCircleOutline,
  AlertCircleOutline,
  WarningOutline,
  RefreshOutline,
  TrashOutline
} from '@vicons/ionicons5'
import { systemApi, vectorIndexApi } from '@/api'
import type { MetricsSummary, IndexJobResponse } from '@/types/api'

const message = useMessage()
const dialog = useDialog()

// Status
const statusLoading = ref(false)
const systemStatus = ref<any>(null)
const indexMetrics = ref<any>(null)

// Query Metrics
const metricsLoading = ref(false)
const queryMetrics = ref<MetricsSummary | null>(null)

// System Metrics
const systemMetricsLoading = ref(false)
const systemMetrics = ref<any>(null)

// Alerts
const alertsLoading = ref(false)
const alerts = ref<any[]>([])

// Health
const healthLoading = ref(false)
const healthResult = ref<{ status: string; version: string; timestamp: string } | null>(null)
const checkingAlerts = ref(false)

// Restart
// const restartLoading = ref(false)

// Active Jobs
const jobsLoading = ref(false)
const activeJobs = ref<IndexJobResponse[]>([])
const stoppingJobs = reactive<Record<string, boolean>>({})

// Completed Jobs
const completedJobsLoading = ref(false)
const completedJobs = ref<IndexJobResponse[]>([])
const clearingJobs = ref(false)

let refreshInterval: number | null = null

const getStatusColor = (status?: string) => {
  switch (status) {
    case 'healthy': return '#18a058'
    case 'degraded': return '#f0a020'
    case 'unhealthy': return '#d03050'
    default: return '#909399'
  }
}

const getStatusIcon = (status?: string) => {
  switch (status) {
    case 'healthy': return CheckmarkCircleOutline
    case 'degraded': return WarningOutline
    case 'unhealthy': return AlertCircleOutline
    default: return WarningOutline
  }
}

const getStatusText = (status?: string) => {
  switch (status) {
    case 'healthy': return '健康'
    case 'degraded': return '降级'
    case 'unhealthy': return '异常'
    default: return '未知'
  }
}

const getAlertSeverityType = (severity?: string) => {
  switch (severity) {
    case 'Critical': return 'error'
    case 'Warning': return 'warning'
    case 'Info': return 'info'
    default: return 'default'
  }
}

const getJobStatusType = (status?: string) => {
  switch (status) {
    case 'Running': return 'success'
    case 'Pending': return 'info'
    case 'Completed': return 'success'
    case 'Failed': return 'error'
    case 'Cancelled': return 'warning'
    default: return 'default'
  }
}

const getProgressStatus = (status?: string): 'default' | 'success' | 'error' | 'warning' => {
  switch (status) {
    case 'Completed': return 'success'
    case 'Failed': return 'error'
    case 'Cancelled': return 'warning'
    default: return 'default'
  }
}

const truncateFileName = (fileName: string, maxLength = 50) => {
  if (fileName.length <= maxLength) return fileName
  return '...' + fileName.slice(-(maxLength - 3))
}

const formatBytes = (bytes: number) => {
  if (bytes === 0) return '0 B'
  const k = 1024
  const sizes = ['B', 'KB', 'MB', 'GB', 'TB']
  const i = Math.floor(Math.log(bytes) / Math.log(k))
  return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i]
}

const formatUptime = (seconds: number) => {
  const days = Math.floor(seconds / 86400)
  const hours = Math.floor((seconds % 86400) / 3600)
  const mins = Math.floor((seconds % 3600) / 60)
  if (days > 0) return `${days}天 ${hours}小时`
  if (hours > 0) return `${hours}小时 ${mins}分钟`
  return `${mins}分钟`
}

const formatTime = (timestamp?: string) => {
  if (!timestamp) return '-'
  return new Date(timestamp).toLocaleString('zh-CN')
}

const loadStatus = async () => {
  statusLoading.value = true
  try {
    const response = await systemApi.getStatus()
    systemStatus.value = response.data
    indexMetrics.value = response.data.index
  } catch (error) {
    console.error('Failed to load status:', error)
  } finally {
    statusLoading.value = false
  }
}

const loadQueryMetrics = async () => {
  metricsLoading.value = true
  try {
    const response = await systemApi.getMetricsSummary()
    queryMetrics.value = response.data
  } catch (error) {
    console.error('Failed to load query metrics:', error)
  } finally {
    metricsLoading.value = false
  }
}

const loadSystemMetrics = async () => {
  systemMetricsLoading.value = true
  try {
    const response = await systemApi.getMetrics()
    systemMetrics.value = response.data
  } catch (error) {
    console.error('Failed to load system metrics:', error)
  } finally {
    systemMetricsLoading.value = false
  }
}

const loadAlerts = async () => {
  alertsLoading.value = true
  try {
    const response = await systemApi.getAlerts?.()
    alerts.value = response.data || []
  } catch (error) {
    console.error('Failed to load alerts:', error)
    alerts.value = []
  } finally {
    alertsLoading.value = false
  }
}

const loadActiveJobs = async () => {
  jobsLoading.value = true
  try {
    const response = await vectorIndexApi.getJobs()
    activeJobs.value = response.data || []
  } catch (error) {
    console.error('Failed to load active jobs:', error)
    activeJobs.value = []
  } finally {
    jobsLoading.value = false
  }
}

const checkHealth = async () => {
  healthLoading.value = true
  try {
    const response = await systemApi.getHealth()
    healthResult.value = response.data
    message.success('健康检查完成')
  } catch (error) {
    console.error('Health check failed:', error)
    message.error('健康检查失败')
  } finally {
    healthLoading.value = false
  }
}

const checkAlerts = async () => {
  checkingAlerts.value = true
  try {
    const response = await systemApi.checkAlerts?.()
    message.success(`检测到 ${response.data?.length || 0} 个告警`)
    await loadAlerts()
  } catch (error) {
    console.error('Alert check failed:', error)
    message.error('告警检查失败')
  } finally {
    checkingAlerts.value = false
  }
}

// const handleRestart = () => {
//   dialog.warning({
//     title: '确认重启',
//     content: '重启服务将暂时中断所有正在进行的操作。确定要重启吗？',
//     positiveText: '确认重启',
//     negativeText: '取消',
//     onPositiveClick: async () => {
//       restartLoading.value = true
//       try {
//         const response = await systemApi.restart()
//         message.success(`服务正在重启，新进程 ID: ${response.data.newProcessId}`)
//         // 等待几秒后尝试重新连接
//         setTimeout(() => {
//           loadAll()
//         }, 5000)
//       } catch (error: any) {
//         console.error('Restart failed:', error)
//         message.error(`重启失败: ${error.response?.data?.error || error.message}`)
//       } finally {
//         restartLoading.value = false
//       }
//     }
//   })
// }

const handleStopJob = async (jobId: string) => {
  dialog.warning({
    title: '确认中断任务',
    content: `确定要中断索引任务 ${jobId} 吗？已处理的文件将保留。`,
    positiveText: '确认中断',
    negativeText: '取消',
    onPositiveClick: async () => {
      stoppingJobs[jobId] = true
      try {
        await vectorIndexApi.stopJob(jobId)
        message.success(`任务 ${jobId} 已中断`)
        await loadActiveJobs()
        await loadCompletedJobs()
      } catch (error: any) {
        console.error('Stop job failed:', error)
        message.error(`中断任务失败: ${error.response?.data?.error || error.message}`)
      } finally {
        stoppingJobs[jobId] = false
      }
    }
  })
}

const loadCompletedJobs = async () => {
  completedJobsLoading.value = true
  try {
    const response = await vectorIndexApi.getCompletedJobs()
    completedJobs.value = response.data || []
  } catch (error) {
    console.error('Failed to load completed jobs:', error)
    completedJobs.value = []
  } finally {
    completedJobsLoading.value = false
  }
}

const handleClearCompletedJobs = () => {
  dialog.warning({
    title: '确认清空记录',
    content: '确定要清空所有已完成/已取消的索引任务记录吗？',
    positiveText: '确认清空',
    negativeText: '取消',
    onPositiveClick: async () => {
      clearingJobs.value = true
      try {
        await vectorIndexApi.clearCompletedJobs()
        message.success('已清空所有已完成的索引任务记录')
        completedJobs.value = []
      } catch (error: any) {
        console.error('Clear completed jobs failed:', error)
        message.error(`清空失败: ${error.response?.data?.error || error.message}`)
      } finally {
        clearingJobs.value = false
      }
    }
  })
}

const loadAll = () => {
  loadStatus()
  loadQueryMetrics()
  loadSystemMetrics()
  loadAlerts()
  loadActiveJobs()
  loadCompletedJobs()
}

onMounted(() => {
  loadAll()
  // Auto refresh every 30 seconds
  refreshInterval = window.setInterval(loadAll, 30000)
})

onUnmounted(() => {
  if (refreshInterval) {
    clearInterval(refreshInterval)
  }
})
</script>
