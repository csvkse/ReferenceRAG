<template>
  <n-space vertical :size="20">
    <!-- Stats Cards -->
    <n-grid :cols="4" :x-gap="20" :y-gap="20">
      <n-gi>
        <n-card>
          <n-statistic label="源数量" :value="stats?.sourceCount || 0">
            <template #prefix>
              <n-icon :component="FolderOutline" />
            </template>
          </n-statistic>
        </n-card>
      </n-gi>
      <n-gi>
        <n-card>
          <n-statistic label="文件数量" :value="stats?.totalFiles || 0">
            <template #prefix>
              <n-icon :component="DocumentTextOutline" />
            </template>
          </n-statistic>
        </n-card>
      </n-gi>
      <n-gi>
        <n-card>
          <n-statistic label="分块数量" :value="stats?.totalChunks || 0">
            <template #prefix>
              <n-icon :component="GridOutline" />
            </template>
          </n-statistic>
        </n-card>
      </n-gi>
      <n-gi>
        <n-card>
          <n-statistic label="平均查询时间" :value="(stats?.avgQueryTime || 0).toFixed(1)">
            <template #prefix>
              <n-icon :component="SearchOutline" />
            </template>
            <template #suffix>ms</template>
          </n-statistic>
        </n-card>
      </n-gi>
    </n-grid>

    <!-- Quick Actions -->
    <n-card title="快速操作">
      <n-space>
        <n-button type="primary" @click="handleStartIndex" :loading="isIndexing">
          <template #icon><n-icon :component="PlayOutline" /></template>
          开始索引
        </n-button>
        <n-button @click="router.push('/search')">
          <template #icon><n-icon :component="SearchOutline" /></template>
          向量搜索
        </n-button>
        <n-button @click="router.push('/sources')">
          <template #icon><n-icon :component="FolderOutline" /></template>
          管理源
        </n-button>
      </n-space>
    </n-card>

    <!-- Index Progress -->
    <n-card v-if="indexStore.progressUpdates.length > 0" title="索引进度">
      <n-list>
        <n-list-item v-for="progress in indexStore.progressUpdates" :key="progress.sourceId">
          <n-thing :title="progress.sourceName">
            <template #description>
              <n-space vertical>
                <n-progress
                  type="line"
                  :percentage="Math.round((progress.processedFiles / progress.totalFiles) * 100)"
                  :status="progress.status === 'failed' ? 'error' : progress.status === 'completed' ? 'success' : 'default'"
                />
                <n-text depth="3">{{ progress.currentFile }}</n-text>
                <n-text v-if="progress.error" type="error">{{ progress.error }}</n-text>
              </n-space>
            </template>
          </n-thing>
        </n-list-item>
      </n-list>
    </n-card>

    <!-- Sources List -->
    <n-card title="源列表">
      <template #header-extra>
        <n-button text @click="router.push('/sources')">
          查看全部
        </n-button>
      </template>
      <n-data-table
        :columns="sourceColumns"
        :data="sources"
        :loading="loadingSources"
        :bordered="false"
      />
    </n-card>

    <!-- System Status -->
    <n-card title="系统状态">
      <n-descriptions :column="3" label-placement="left">
        <n-descriptions-item label="文件总数">
          {{ stats?.totalFiles || 0 }}
        </n-descriptions-item>
        <n-descriptions-item label="分块总数">
          {{ stats?.totalChunks || 0 }}
        </n-descriptions-item>
        <n-descriptions-item label="平均查询时间">
          {{ (stats?.avgQueryTime || 0).toFixed(2) }} ms
        </n-descriptions-item>
      </n-descriptions>
    </n-card>
  </n-space>
</template>

<script setup lang="ts">
import { ref, onMounted, h } from 'vue'
import { useRouter } from 'vue-router'
import { NTag, NButton, type DataTableColumns } from 'naive-ui'
import {
  FolderOutline,
  DocumentTextOutline,
  GridOutline,
  SearchOutline,
  PlayOutline
} from '@vicons/ionicons5'
import { dashboardApi, sourcesApi, indexApi } from '@/api'
import { useIndexStore } from '@/stores/index'
import type { DashboardStats, SourceDetail } from '@/types/api'

const router = useRouter()
const indexStore = useIndexStore()

const stats = ref<DashboardStats | null>(null)
const sources = ref<SourceDetail[]>([])
const loadingSources = ref(false)
const isIndexing = ref(false)

const sourceColumns: DataTableColumns<SourceDetail> = [
  { title: '名称', key: 'name' },
  { title: '路径', key: 'path', ellipsis: { tooltip: true } },
  {
    title: '状态',
    key: 'enabled',
    width: 80,
    render: (row) => h(NTag, { type: row.enabled ? 'success' : 'default', size: 'small' }, {
      default: () => row.enabled ? '启用' : '停用'
    })
  },
  { title: '文件数', key: 'fileCount', width: 80 },
  { title: '分块数', key: 'chunkCount', width: 80 }
]

const loadStats = async () => {
  try {
    const response = await dashboardApi.getStats()
    stats.value = response.data
  } catch (error) {
    console.error('Failed to load stats:', error)
  }
}

const loadSources = async () => {
  loadingSources.value = true
  try {
    const response = await sourcesApi.getAll()
    sources.value = response.data
  } catch (error) {
    console.error('Failed to load sources:', error)
  } finally {
    loadingSources.value = false
  }
}

const handleStartIndex = async () => {
  isIndexing.value = true
  try {
    await indexApi.start({})
  } catch (error) {
    console.error('Failed to start index:', error)
  } finally {
    isIndexing.value = false
  }
}

onMounted(async () => {
  await Promise.all([loadStats(), loadSources()])
  await indexStore.connect()
})
</script>
