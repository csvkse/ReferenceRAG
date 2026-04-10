<template>
  <n-space vertical :size="20">
    <!-- Add Source -->
    <n-card title="添加源">
      <n-space vertical>
        <n-form
          ref="formRef"
          :model="newSource"
          :rules="formRules"
          label-placement="left"
          label-width="80"
        >
          <n-form-item label="路径" path="path">
            <n-input v-model:value="newSource.path" placeholder="输入文件夹绝对路径（必填）" />
          </n-form-item>
          <n-form-item label="名称">
            <n-input v-model:value="newSource.name" placeholder="输入源名称（可选，默认使用文件夹名）" />
          </n-form-item>
          <n-form-item label="递归扫描">
            <n-switch v-model:value="newSource.recursive" />
          </n-form-item>
          <n-space>
            <n-button type="primary" :loading="creating" @click="handleCreateSource">
              添加
            </n-button>
            <n-button @click="resetForm">重置</n-button>
          </n-space>
        </n-form>
      </n-space>
    </n-card>

    <!-- Vector Index Management -->
    <n-card title="向量索引管理">
      <template #header-extra>
        <n-space>
          <n-button size="small" :loading="loadingIndex" @click="loadVectorIndex">
            刷新
          </n-button>
        </n-space>
      </template>
      <n-spin :show="loadingIndex">
        <n-space vertical :size="16">
          <!-- Current Model Info -->
          <n-descriptions label-placement="left" :column="3" bordered size="small">
            <n-descriptions-item label="当前模型">
              <n-tag type="primary">{{ indexSummary?.currentModel || '-' }}</n-tag>
            </n-descriptions-item>
            <n-descriptions-item label="向量维度">
              {{ indexSummary?.currentDimension || '-' }}
            </n-descriptions-item>
            <n-descriptions-item label="总文件数">
              {{ indexSummary?.totalFiles || 0 }}
            </n-descriptions-item>
            <n-descriptions-item label="总分块数">
              {{ indexSummary?.totalChunks || 0 }}
            </n-descriptions-item>
          </n-descriptions>

          <!-- Model Stats Table -->
          <n-data-table
            :columns="indexColumns"
            :data="indexSummary?.modelStats || []"
            :bordered="false"
            size="small"
          />

          <!-- Actions -->
          <n-space>
            <n-popconfirm @positive-click="handleRebuildAll">
              <template #trigger>
                <n-button type="primary" :loading="rebuilding">
                  重建全部向量索引
                </n-button>
              </template>
              确定要使用当前模型重建所有向量索引吗？这将删除现有向量并重新生成。
            </n-popconfirm>
            <n-popconfirm @positive-click="handleCleanup">
              <template #trigger>
                <n-button type="warning" :loading="cleaning">
                  清理孤立索引
                </n-button>
              </template>
              确定要清理孤立向量索引（模型已不存在的向量数据）吗？
            </n-popconfirm>
            <n-popconfirm @positive-click="handleDeleteAllIndex">
              <template #trigger>
                <n-button type="error" :loading="deletingAll">
                  删除所有向量索引
                </n-button>
              </template>
              确定要删除所有向量索引吗？此操作不可恢复！
            </n-popconfirm>
          </n-space>
        </n-space>
      </n-spin>
    </n-card>

    <!-- Sources Table -->
    <n-card title="源列表">
      <template #header-extra>
        <n-button size="small" :loading="loading" @click="loadSources">
          刷新
        </n-button>
      </template>
      <n-data-table
        :columns="columns"
        :data="sources"
        :loading="loading"
        :bordered="false"
        :row-key="(row: SourceDetail) => row.name"
      />
    </n-card>
  </n-space>
</template>

<script setup lang="ts">
import { ref, h, onMounted } from 'vue'
import { useMessage, useDialog, NButton, NSpace, NTag, NPopconfirm, NInput, type FormInst, type FormRules } from 'naive-ui'
import { sourcesApi, vectorIndexApi } from '@/api'
import type { SourceDetail, IndexSummary, ModelStat } from '@/types/api'

const message = useMessage()
const dialog = useDialog()
const formRef = ref<FormInst | null>(null)
const loading = ref(false)
const creating = ref(false)
const sources = ref<SourceDetail[]>([])

// Edit state
const editingSource = ref<SourceDetail | null>(null)
const editingName = ref('')

// Vector index state
const loadingIndex = ref(false)
const rebuilding = ref(false)
const cleaning = ref(false)
const deletingAll = ref(false)
const indexSummary = ref<IndexSummary | null>(null)

const newSource = ref({
  path: '',
  name: '',
  recursive: false
})

const formRules: FormRules = {
  path: { required: true, message: '请输入文件夹路径', trigger: 'blur' }
}

const columns = [
  {
    title: '名称',
    key: 'name',
    width: 160
  },
  {
    title: '路径',
    key: 'path',
    ellipsis: { tooltip: true }
  },
  {
    title: '状态',
    key: 'enabled',
    width: 80,
    render: (row: SourceDetail) =>
      h(NTag, { type: row.enabled ? 'success' : 'default', size: 'small' }, {
        default: () => row.enabled ? '启用' : '停用'
      })
  },
  {
    title: '文件数',
    key: 'fileCount',
    width: 80
  },
  {
    title: '分块数',
    key: 'chunkCount',
    width: 80
  },
  {
    title: '最近索引',
    key: 'lastIndexed',
    width: 160,
    render: (row: SourceDetail) => row.lastIndexed
      ? new Date(row.lastIndexed).toLocaleString('zh-CN')
      : '-'
  },
  {
    title: '操作',
    key: 'actions',
    width: 320,
    render: (row: SourceDetail) =>
      h(NSpace, { size: 'small' }, {
        default: () => [
          h(NButton, {
            size: 'small',
            onClick: () => handleEdit(row)
          }, {
            default: () => '编辑'
          }),
          h(NButton, {
            size: 'small',
            type: row.enabled ? 'warning' : 'primary',
            onClick: () => handleToggle(row)
          }, {
            default: () => row.enabled ? '停用' : '启用'
          }),
          h(NButton, {
            size: 'small',
            onClick: () => handleReindex(row)
          }, {
            default: () => '开始索引'
          }),
          h(NPopconfirm, {
            onPositiveClick: () => handleDelete(row)
          }, {
            trigger: () => h(NButton, { size: 'small', type: 'error' }, { default: () => '删除' }),
            default: () => '确定要删除此源吗？'
          })
        ]
      })
  }
]

const indexColumns = [
  {
    title: '模型',
    key: 'modelName',
    render: (row: ModelStat) =>
      h(NSpace, { align: 'center', size: 'small' }, {
        default: () => [
          row.isCurrentModel ? h(NTag, { type: 'success', size: 'small' }, { default: () => '当前' }) : null,
          row.modelName
        ]
      })
  },
  {
    title: '维度',
    key: 'dimension',
    width: 80
  },
  {
    title: '向量数',
    key: 'vectorCount',
    width: 100,
    render: (row: ModelStat) => row.vectorCount.toLocaleString()
  },
  {
    title: '存储大小',
    key: 'storageMB',
    width: 100,
    render: (row: ModelStat) => `${row.storageMB.toFixed(2)} MB`
  },
  {
    title: '操作',
    key: 'actions',
    width: 120,
    render: (row: ModelStat) =>
      h(NSpace, { size: 'small' }, {
        default: () => [
          h(NPopconfirm, {
            onPositiveClick: () => handleDeleteModelIndex(row.modelName)
          }, {
            trigger: () => h(NButton, { size: 'small', type: 'error', disabled: row.isCurrentModel }, { default: () => '删除' }),
            default: () => `确定要删除模型 "${row.modelName}" 的向量索引吗？`
          })
        ]
      })
  }
]

const loadSources = async () => {
  loading.value = true
  try {
    const response = await sourcesApi.getAll()
    sources.value = response.data
  } catch (error) {
    message.error('加载源列表失败')
  } finally {
    loading.value = false
  }
}

const loadVectorIndex = async () => {
  loadingIndex.value = true
  try {
    const response = await vectorIndexApi.getSummary()
    indexSummary.value = response.data
  } catch (error) {
    message.error('加载向量索引信息失败')
  } finally {
    loadingIndex.value = false
  }
}

const handleCreateSource = async () => {
  try {
    await formRef.value?.validate()
  } catch {
    return
  }
  creating.value = true
  try {
    await sourcesApi.create({
      path: newSource.value.path,
      name: newSource.value.name || undefined,
      recursive: newSource.value.recursive || undefined
    })
    message.success('源添加成功')
    resetForm()
    await loadSources()
  } catch (error: unknown) {
    const err = error as { response?: { data?: { error?: string }; status?: number } }
    const msg = err.response?.data?.error || '添加源失败'
    message.error(msg)
  } finally {
    creating.value = false
  }
}

const resetForm = () => {
  newSource.value = { path: '', name: '', recursive: false }
  formRef.value?.restoreValidation()
}

const handleToggle = async (source: SourceDetail) => {
  try {
    await sourcesApi.toggle(source.name, !source.enabled)
    message.success(source.enabled ? '已停用' : '已启用')
    await loadSources()
  } catch (error) {
    message.error('操作失败')
  }
}

const handleEdit = (source: SourceDetail) => {
  editingSource.value = source
  editingName.value = source.name
  dialog.create({
    title: '编辑源名称',
    content: () => h(NInput, {
      value: editingName.value,
      onUpdateValue: (val: string) => { editingName.value = val },
      placeholder: '请输入新的源名称'
    }),
    positiveText: '保存',
    negativeText: '取消',
    onPositiveClick: async () => {
      if (!editingName.value.trim()) {
        message.error('源名称不能为空')
        return false
      }
      if (editingName.value === source.name) {
        return
      }
      try {
        await sourcesApi.update(source.name, { name: editingName.value })
        message.success('源名称已更新')
        await loadSources()
      } catch (error) {
        message.error('更新失败')
        return false
      }
    }
  })
}

const handleReindex = (source: SourceDetail) => {
  dialog.warning({
    title: '确认开始索引',
    content: `确定要开始索引 "${source.name}" 吗？`,
    positiveText: '确定',
    negativeText: '取消',
    onPositiveClick: async () => {
      try {
        await vectorIndexApi.startIndex({ sources: [source.name] })
        message.success('索引已启动')
      } catch (error) {
        message.error('启动索引失败')
      }
    }
  })
}

const handleDelete = async (source: SourceDetail) => {
  try {
    await sourcesApi.delete(source.name)
    message.success('源已删除')
    await loadSources()
  } catch (error) {
    message.error('删除失败')
  }
}

const handleRebuildAll = async () => {
  rebuilding.value = true
  try {
    const response = await vectorIndexApi.rebuild()
    message.success(response.data.message || '向量索引重建任务已启动')
    await loadVectorIndex()
  } catch (error) {
    message.error('启动重建失败')
  } finally {
    rebuilding.value = false
  }
}

const handleCleanup = async () => {
  cleaning.value = true
  try {
    const response = await vectorIndexApi.cleanup()
    message.success(response.data.message)
    await loadVectorIndex()
  } catch (error) {
    message.error('清理失败')
  } finally {
    cleaning.value = false
  }
}

const handleDeleteAllIndex = async () => {
  deletingAll.value = true
  try {
    const response = await vectorIndexApi.deleteAll()
    message.success(`已删除 ${response.data.totalDeleted} 条向量`)
    await loadVectorIndex()
  } catch (error) {
    message.error('删除失败')
  } finally {
    deletingAll.value = false
  }
}

const handleDeleteModelIndex = async (modelName: string) => {
  try {
    const response = await vectorIndexApi.deleteByModel(modelName)
    message.success(response.data.message)
    await loadVectorIndex()
  } catch (error) {
    message.error('删除失败')
  }
}

onMounted(() => {
  loadSources()
  loadVectorIndex()
})
</script>
