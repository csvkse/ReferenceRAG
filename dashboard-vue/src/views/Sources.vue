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

    <!-- Sources Table -->
    <n-card title="源列表">
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
import { useMessage, useDialog, NButton, NSpace, NTag, NPopconfirm, type FormInst, type FormRules } from 'naive-ui'
import { sourcesApi } from '@/api'
import type { SourceDetail } from '@/types/api'

const message = useMessage()
const dialog = useDialog()
const formRef = ref<FormInst | null>(null)
const loading = ref(false)
const creating = ref(false)
const sources = ref<SourceDetail[]>([])

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
    width: 260,
    render: (row: SourceDetail) =>
      h(NSpace, { size: 'small' }, {
        default: () => [
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

const handleReindex = (source: SourceDetail) => {
  dialog.warning({
    title: '确认开始索引',
    content: `确定要开始索引 "${source.name}" 吗？`,
    positiveText: '确定',
    negativeText: '取消',
    onPositiveClick: async () => {
      try {
        await sourcesApi.startIndex(source.name)
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

onMounted(() => {
  loadSources()
})
</script>
