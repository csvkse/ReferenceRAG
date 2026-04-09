<template>
  <n-space vertical :size="20">
    <!-- System Status -->
    <n-card title="系统状态">
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
import { ref, onMounted, onUnmounted } from 'vue'
import { useMessage } from 'naive-ui'
import {
  CheckmarkCircleOutline,
  AlertCircleOutline,
  WarningOutline,
  RefreshOutline
} from '@vicons/ionicons5'
import { systemApi } from '@/api'
import type { MetricsSummary } from '@/types/api'

const message = useMessage()

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

const loadAll = () => {
  loadStatus()
  loadQueryMetrics()
  loadSystemMetrics()
  loadAlerts()
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
