<template>
  <n-space vertical :size="20">
    <!-- BM25 Index Management -->
    <n-card title="BM25索引管理">
      <n-tabs type="line" animated>
        <!-- Provider Settings Tab -->
        <n-tab-pane name="provider" tab="Provider设置">
          <n-space vertical :size="16">
            <n-alert :type="providerInfo.isMatch ? 'success' : 'warning'">
              <template #header>当前状态</template>
              <n-space vertical>
                <n-text>配置 Provider: <n-tag type="info">{{ providerInfo.configuredProvider }}</n-tag></n-text>
                <n-text>运行 Provider: <n-tag :type="providerInfo.activeProvider === 'fts5' ? 'success' : 'warning'">{{ providerInfo.activeProvider }}</n-tag></n-text>
                <n-text v-if="!providerInfo.isMatch" type="warning">配置与运行中 Provider 不匹配，需要重启服务</n-text>
              </n-space>
            </n-alert>

            <n-card title="切换 BM25 Provider" style="max-width: 600px">
              <n-form :model="providerForm" label-placement="left" label-width="120">
                <n-form-item label="Provider 类型">
                  <n-radio-group v-model:value="providerForm.provider" name="providerGroup">
                    <n-space>
                      <n-radio value="fts5">
                        <n-space vertical :size="4">
                          <n-text strong>FTS5（推荐）</n-text>
                          <n-text depth="3" style="font-size: 12px">使用 SQLite FTS5 内置 BM25，性能更好</n-text>
                        </n-space>
                      </n-radio>
                      <n-radio value="legacy">
                        <n-space vertical :size="4">
                          <n-text strong>Legacy</n-text>
                          <n-text depth="3" style="font-size: 12px">手动倒排索引实现，作为备用</n-text>
                        </n-space>
                      </n-radio>
                    </n-space>
                  </n-radio-group>
                </n-form-item>
                <n-form-item>
                  <n-button
                    type="primary"
                    :loading="savingProvider"
                    :disabled="providerForm.provider === providerInfo.configuredProvider"
                    @click="handleSaveProvider"
                  >
                    保存配置（需重启服务生效）
                  </n-button>
                </n-form-item>
              </n-form>

              <n-alert type="info" style="margin-top: 16px">
                <template #header>说明</template>
                <n-text depth="3">
                  修改 Provider 配置后需要重启服务才能生效。重启后系统将使用新的 Provider 进行 BM25 索引和搜索。
                </n-text>
              </n-alert>
            </n-card>
          </n-space>
        </n-tab-pane>

        <!-- Model List Tab -->
        <n-tab-pane name="models" tab="模型列表">
          <n-space vertical :size="16">
            <!-- Action buttons -->
            <n-space>
              <n-button type="primary" @click="showCreateDialog = true">
                <template #icon><n-icon :component="AddOutline" /></template>
                创建模型
              </n-button>
              <n-button @click="loadModels" :loading="loading">
                <template #icon><n-icon :component="RefreshOutline" /></template>
                刷新
              </n-button>
            </n-space>

            <!-- Models table -->
            <n-data-table
              :columns="modelColumns"
              :data="models"
              :loading="loading"
              :row-key="(row: BM25Model) => row.name"
            />
          </n-space>
        </n-tab-pane>

        <!-- Index Operations Tab -->
        <n-tab-pane name="operations" tab="索引操作">
          <n-space vertical :size="16">
            <n-alert type="info">
              <template #header>提示</template>
              全量重建将重新处理所有已索引的文档，清空索引将从数据库中删除所有BM25索引数据。
            </n-alert>

            <n-space>
              <n-button
                type="warning"
                :loading="rebuilding"
                :disabled="!selectedModelName"
                @click="handleRebuild"
              >
                全量重建
              </n-button>
              <n-button
                type="error"
                :loading="clearing"
                :disabled="!selectedModelName"
                @click="showClearDialog = true"
              >
                清空索引
              </n-button>
              <n-select
                v-model:value="selectedModelName"
                :options="modelSelectOptions"
                placeholder="选择要操作的模型"
                style="width: 200px"
              />
            </n-space>

            <!-- Rebuild progress -->
            <n-card v-if="rebuildProgress" title="重建进度">
              <n-progress
                type="line"
                :percentage="rebuildProgress.percent"
                :status="rebuildProgress.status"
                :indicator-placement="'inside'"
              />
              <n-text depth="3" style="display: block; margin-top: 8px">
                {{ rebuildProgress.message }}
              </n-text>
            </n-card>
          </n-space>
        </n-tab-pane>

        <!-- Search Tab -->
        <n-tab-pane name="search" tab="搜索测试">
          <n-space vertical :size="16">
            <n-form :model="searchForm" inline>
              <n-form-item label="选择模型">
                <n-select
                  v-model:value="searchForm.modelName"
                  :options="modelSelectOptions"
                  placeholder="选择模型"
                  style="width: 200px"
                />
              </n-form-item>
              <n-form-item label="查询语句">
                <n-input
                  v-model:value="searchForm.query"
                  placeholder="输入查询关键词"
                  style="width: 400px"
                />
              </n-form-item>
              <n-form-item label="返回数量">
                <n-input-number
                  v-model:value="searchForm.topK"
                  :min="1"
                  :max="100"
                  style="width: 100px"
                />
              </n-form-item>
              <n-form-item>
                <n-button type="primary" :loading="searching" @click="handleSearch">
                  搜索
                </n-button>
              </n-form-item>
            </n-form>

            <!-- Search results -->
            <n-card v-if="searchResults.length > 0" title="搜索结果">
              <n-list>
                <n-list-item v-for="(result, index) in searchResults" :key="index">
                  <n-thing>
                    <template #header>
                      <n-space align="center">
                        <span>文档 {{ index + 1 }}</span>
                        <n-tag type="success" size="small">Score: {{ result.score.toFixed(4) }}</n-tag>
                      </n-space>
                    </template>
                    <n-text>{{ result.content }}</n-text>
                  </n-thing>
                </n-list-item>
              </n-list>
              <n-text depth="3" style="display: block; margin-top: 8px">
                搜索耗时: {{ searchDuration }}ms
              </n-text>
            </n-card>
          </n-space>
        </n-tab-pane>

      </n-tabs>
    </n-card>

    <!-- Create Model Dialog -->
    <n-modal v-model:show="showCreateDialog" preset="dialog" title="创建模型">
      <n-space vertical>
        <n-form :model="createForm" label-placement="left" label-width="100">
          <n-form-item label="模型名称" required>
            <n-input v-model:value="createForm.name" placeholder="输入模型名称" />
          </n-form-item>
          <n-form-item label="描述（可选）">
            <n-input
              v-model:value="createForm.description"
              type="textarea"
              placeholder="输入模型描述"
              :rows="2"
            />
          </n-form-item>
        </n-form>
      </n-space>
      <template #action>
        <n-button @click="showCreateDialog = false">取消</n-button>
        <n-button type="primary" :loading="creating" @click="handleCreate">创建</n-button>
      </template>
    </n-modal>

    <!-- Clear Confirm Dialog -->
    <n-modal v-model:show="showClearDialog" preset="dialog" title="清空索引确认">
      <n-alert type="error">
        <template #header>警告</template>
        清空索引将删除模型 "{{ selectedModelName }}" 的所有索引数据，此操作不可恢复！
      </n-alert>
      <template #action>
        <n-button @click="showClearDialog = false">取消</n-button>
        <n-button type="error" :loading="clearing" @click="handleClear">确认清空</n-button>
      </template>
    </n-modal>
  </n-space>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { h } from 'vue'
import {
  NButton,
  NSpace,
  NDataTable,
  NTag,
  NIcon,
  NInput,
  NInputNumber,
  NSelect,
  NForm,
  NFormItem,
  NAlert,
  NCard,
  NList,
  NListItem,
  NThing,
  NProgress,
  NModal,
  NRadio,
  NRadioGroup,
  useMessage
} from 'naive-ui'
import type { DataTableColumns } from 'naive-ui'
import { RefreshOutline, AddOutline } from '@vicons/ionicons5'
import { bm25IndexApi, type BM25Model, type BM25SearchResult, type BM25ProviderInfo } from '@/api'

const message = useMessage()

// State
const models = ref<BM25Model[]>([])
const loading = ref(false)
const showCreateDialog = ref(false)
const showClearDialog = ref(false)
const creating = ref(false)
const clearing = ref(false)
const rebuilding = ref(false)
const searching = ref(false)
const selectedModelName = ref<string | null>(null)
const rebuildProgress = ref<{ percent: number; status: 'default' | 'success' | 'error'; message: string } | null>(null)

const searchResults = ref<{ chunkId: string; content: string; score: number; rank: number }[]>([])
const searchDuration = ref(0)

const createForm = ref({
  name: '',
  description: ''
})

const searchForm = ref({
  modelName: null as string | null,
  query: '',
  topK: 10
})

// Provider state
const providerInfo = ref<BM25ProviderInfo>({
  configuredProvider: 'fts5',
  activeProvider: 'fts5',
  isMatch: true,
  description: ''
})
const savingProvider = ref(false)
const providerForm = ref({
  provider: 'fts5'
})

// Computed
const modelSelectOptions = computed(() =>
  models.value.map(m => ({ label: m.name, value: m.name }))
)

// Columns
const modelColumns: DataTableColumns<BM25Model> = [
  {
    title: '名称',
    key: 'name',
    width: 150
  },
  {
    title: '状态',
    key: 'enabled',
    width: 100,
    render(row) {
      return h(NTag, {
        type: row.isEnabled ? 'success' : 'warning',
        size: 'small'
      }, { default: () => row.isEnabled ? '启用' : '禁用' })
    }
  },
  {
    title: '文档数',
    key: 'totalDocuments',
    width: 100
  },
  {
    title: '词汇量',
    key: 'vocabularySize',
    width: 100
  },
  {
    title: 'avgDL',
    key: 'averageDocLength',
    width: 100,
    render(row) {
      return row.averageDocLength?.toFixed(2) || '-'
    }
  },
  {
    title: '创建时间',
    key: 'createdAt',
    width: 180,
    render(row) {
      return row.createdAt ? new Date(row.createdAt).toLocaleString() : '-'
    }
  },
  {
    title: '操作',
    key: 'actions',
    width: 200,
    render(row) {
      const buttons: VNode[] = []

      // Enable/Disable button
      buttons.push(h(NButton, {
        size: 'small',
        type: row.isEnabled ? 'warning' : 'primary',
        onClick: () => handleToggle(row)
      }, { default: () => row.isEnabled ? '禁用' : '启用' }))

      // Delete button
      buttons.push(h(NButton, {
        size: 'small',
        type: 'error',
        style: 'margin-left: 8px',
        onClick: () => handleDelete(row)
      }, { default: () => '删除' }))

      return h(NSpace, { size: 'small' }, { default: () => buttons })
    }
  }
]

// Methods
const loadProvider = async () => {
  try {
    const response = await bm25IndexApi.getProvider()
    providerInfo.value = response.data
    providerForm.value.provider = response.data.configuredProvider
  } catch (error) {
    console.error('Failed to load provider:', error)
  }
}

const handleSaveProvider = async () => {
  savingProvider.value = true
  try {
    await bm25IndexApi.setProvider(providerForm.value.provider)
    message.success('Provider 配置已保存，请重启服务使更改生效')
    await loadProvider()
  } catch (error: any) {
    console.error('Failed to save provider:', error)
    message.error(`保存失败: ${error.response?.data?.error || error.message}`)
  } finally {
    savingProvider.value = false
  }
}

const loadModels = async () => {
  loading.value = true
  try {
    const response = await bm25IndexApi.getModels()
    models.value = response.data
  } catch (error) {
    console.error('Failed to load models:', error)
    message.error('加载模型列表失败')
  } finally {
    loading.value = false
  }
}

const handleCreate = async () => {
  if (!createForm.value.name) {
    message.error('请输入模型名称')
    return
  }

  creating.value = true
  try {
    await bm25IndexApi.createModel({
      name: createForm.value.name,
      description: createForm.value.description
    })
    message.success('模型创建成功')
    showCreateDialog.value = false
    createForm.value = { name: '', description: '' }
    await loadModels()
  } catch (error: any) {
    console.error('Failed to create model:', error)
    message.error(`创建失败: ${error.response?.data?.error || error.message}`)
  } finally {
    creating.value = false
  }
}

const handleDelete = async (model: BM25Model) => {
  try {
    await bm25IndexApi.deleteModel(model.name)
    message.success('模型删除成功')
    await loadModels()
  } catch (error: any) {
    console.error('Failed to delete model:', error)
    message.error(`删除失败: ${error.response?.data?.error || error.message}`)
  }
}

const handleToggle = async (model: BM25Model) => {
  try {
    if (model.isEnabled) {
      await bm25IndexApi.disableModel(model.name)
      message.success(`模型 "${model.name}" 已禁用`)
    } else {
      await bm25IndexApi.enableModel(model.name)
      message.success(`模型 "${model.name}" 已启用`)
    }
    await loadModels()
  } catch (error: any) {
    console.error('Failed to toggle model:', error)
    message.error(`操作失败: ${error.response?.data?.error || error.message}`)
  }
}

const handleRebuild = async () => {
  if (!selectedModelName.value) {
    message.error('请选择要重建的模型')
    return
  }

  rebuilding.value = true
  rebuildProgress.value = {
    percent: 0,
    status: 'default',
    message: '正在重建索引...'
  }

  try {
    await bm25IndexApi.rebuildIndex(selectedModelName.value)
    rebuildProgress.value = {
      percent: 100,
      status: 'success',
      message: '索引重建完成'
    }
    message.success('索引重建完成')
    await loadModels()
  } catch (error: any) {
    console.error('Failed to rebuild index:', error)
    rebuildProgress.value = {
      percent: 0,
      status: 'error',
      message: `重建失败: ${error.response?.data?.error || error.message}`
    }
    message.error(`重建失败: ${error.response?.data?.error || error.message}`)
  } finally {
    rebuilding.value = false
  }
}

const handleClear = async () => {
  if (!selectedModelName.value) {
    message.error('请选择要清空的模型')
    return
  }

  clearing.value = true
  try {
    await bm25IndexApi.clearModel(selectedModelName.value)
    message.success('索引已清空')
    showClearDialog.value = false
    await loadModels()
  } catch (error: any) {
    console.error('Failed to clear index:', error)
    message.error(`清空失败: ${error.response?.data?.error || error.message}`)
  } finally {
    clearing.value = false
  }
}

const handleSearch = async () => {
  if (!searchForm.value.modelName || !searchForm.value.query) {
    message.error('请选择模型并输入查询语句')
    return
  }

  searching.value = true
  try {
    const response = await bm25IndexApi.search(
      searchForm.value.modelName,
      searchForm.value.query,
      searchForm.value.topK
    )
    const result: BM25SearchResult = response.data
    searchResults.value = result.results
    searchDuration.value = result.durationMs
  } catch (error: any) {
    console.error('Failed to search:', error)
    message.error(`搜索失败: ${error.response?.data?.error || error.message}`)
    searchResults.value = []
  } finally {
    searching.value = false
  }
}

// Lifecycle
onMounted(() => {
  loadModels()
  loadProvider()
})
</script>
