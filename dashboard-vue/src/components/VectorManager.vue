<template>
  <n-space vertical :size="20">
    <n-card title="向量存储统计">
      <template #header-extra>
        <n-space>
          <n-popconfirm @positive-click="handleDeleteOrphaned">
            <template #trigger>
              <n-button type="warning" size="small" :disabled="stats.length === 0">清理孤立项</n-button>
            </template>
            确定删除无关联模型的向量数据？
          </n-popconfirm>
          <n-button text @click="loadStats">
            <template #icon><n-icon :component="RefreshOutline" /></template>
            刷新
          </n-button>
        </n-space>
      </template>

      <n-data-table
        :columns="columns"
        :data="stats"
        :loading="loading"
        :row-key="(row: VectorStats) => row.modelName"
      />
    </n-card>
  </n-space>
</template>

<script setup lang="ts">
import { ref, h, onMounted } from 'vue'
import { NTag, NButton, NSpace, NIcon, NPopconfirm, useMessage } from 'naive-ui'
import type { DataTableColumns } from 'naive-ui'
import { RefreshOutline } from '@vicons/ionicons5'
import { vectorsApi } from '@/api'
import type { VectorStats } from '@/types/api'

const message = useMessage()
const stats = ref<VectorStats[]>([])
const loading = ref(false)

const formatBytes = (bytes: number) => {
  if (bytes === 0) return '0 B'
  const k = 1024
  const sizes = ['B', 'KB', 'MB', 'GB']
  const i = Math.floor(Math.log(bytes) / Math.log(k))
  return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i]
}

const columns: DataTableColumns<VectorStats> = [
  { title: '模型名称', key: 'modelName' },
  { title: '维度', key: 'dimension', width: 90 },
  { title: '向量数量', key: 'vectorCount', width: 110 },
  {
    title: '占用空间',
    key: 'storageBytes',
    width: 110,
    render(row) { return formatBytes(row.storageBytes) }
  },
  {
    title: '状态',
    key: 'modelExists',
    width: 100,
    render(row) {
      return h(NTag, { type: row.modelExists ? 'success' : 'warning', size: 'small' }, {
        default: () => row.modelExists ? '模型存在' : '已删除'
      })
    }
  },
  {
    title: '最后更新',
    key: 'lastUpdated',
    width: 170,
    render(row) { return row.lastUpdated ? new Date(row.lastUpdated).toLocaleString() : '-' }
  },
  {
    title: '操作',
    key: 'actions',
    width: 90,
    render(row) {
      return h(NPopconfirm, {
        onPositiveClick: () => handleDelete(row.modelName)
      }, {
        trigger: () => h(NButton, { size: 'small', type: 'error' }, { default: () => '删除' }),
        default: () => `确定删除模型 "${row.modelName}" 的所有向量数据？`
      })
    }
  }
]

const loadStats = async () => {
  loading.value = true
  try {
    const response = await vectorsApi.getStats()
    stats.value = response.data
  } catch (error) {
    console.error('Failed to load vector stats:', error)
    message.error('加载向量统计失败')
  } finally {
    loading.value = false
  }
}

const handleDelete = async (modelName: string) => {
  try {
    const response = await vectorsApi.deleteByModel(modelName)
    message.success(response.data.message || `已删除 ${modelName} 的向量数据`)
    await loadStats()
  } catch (error: any) {
    message.error(error.response?.data?.error || '删除失败')
  }
}

const handleDeleteOrphaned = async () => {
  try {
    const response = await vectorsApi.deleteOrphaned()
    message.success(response.data.message || '孤立项清理完成')
    await loadStats()
  } catch (error: any) {
    message.error(error.response?.data?.error || '清理失败')
  }
}

onMounted(() => {
  loadStats()
})
</script>
