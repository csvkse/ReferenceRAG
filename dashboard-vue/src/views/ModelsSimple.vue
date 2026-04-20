<template>
  <n-space vertical :size="20">
    <!-- 模型根目录设置 -->
    <n-card title="模型根目录">
      <n-form label-placement="left" label-width="120">
        <n-form-item label="模型存储路径">
          <n-space align="center" style="width: 100%">
            <n-input
              v-model:value="modelsPath"
              placeholder="默认: models"
              style="width: 300px"
            />
            <n-button
              type="primary"
              :loading="savingModelsPath"
              @click="handleSaveModelsPath"
            >
              保存
            </n-button>
            <n-button
              type="info"
              @click="handleScanModels"
              :loading="scanning"
            >
              扫描模型
            </n-button>
          </n-space>
        </n-form-item>
      </n-form>
      <n-alert type="info" style="margin-top: 12px">
        <template #header>目录结构说明</template>
        <n-text>
          模型根目录下应包含以下子目录：<br>
          • <n-code code="Embedding/" :inline="true" /> - 存放嵌入模型<br>
          • <n-code code="Reranker/" :inline="true" /> - 存放重排模型<br>
          每个模型一个文件夹，文件夹名称即为模型名称，需包含 model.onnx 文件
        </n-text>
      </n-alert>
    </n-card>

    <!-- 当前使用的模型 -->
    <n-card title="当前使用的模型">
      <n-spin :show="currentLoading">
        <n-space vertical :size="16">
          <n-descriptions v-if="currentModel" :column="4" label-placement="left">
            <n-descriptions-item label="嵌入模型">
              <n-tag type="primary">{{ currentModel.displayName || currentModel.name }}</n-tag>
            </n-descriptions-item>
            <n-descriptions-item label="维度">{{ currentModel.dimension }}</n-descriptions-item>
            <n-descriptions-item label="最大序列长度">{{ currentModel.maxSequenceLength }}</n-descriptions-item>
            <n-descriptions-item label="状态">
              <n-tag type="success">使用中</n-tag>
            </n-descriptions-item>
          </n-descriptions>
          <n-text v-else depth="3">未配置嵌入模型</n-text>

          <n-divider style="margin: 8px 0" />

          <n-descriptions v-if="currentRerankModel" :column="3" label-placement="left">
            <n-descriptions-item label="重排模型">
              <n-tag type="info">{{ currentRerankModel.displayName || currentRerankModel.name }}</n-tag>
            </n-descriptions-item>
            <n-descriptions-item label="维度">{{ currentRerankModel.dimension }}</n-descriptions-item>
            <n-descriptions-item label="状态">
              <n-tag type="success">使用中</n-tag>
            </n-descriptions-item>
          </n-descriptions>
          <n-text v-else depth="3">未配置重排模型</n-text>
        </n-space>
      </n-spin>
    </n-card>

    <!-- 嵌入模型列表 -->
    <n-card title="嵌入模型 (Embedding/)">
      <template #header-extra>
        <n-button text @click="loadEmbeddingModels">
          <template #icon><n-icon :component="RefreshOutline" /></template>
          刷新
        </n-button>
      </template>

      <n-data-table
        :columns="embeddingModelColumns"
        :data="embeddingModels"
        :loading="embeddingLoading"
        :row-key="(row: ModelInfo) => row.name || ''"
      />
    </n-card>

    <!-- 重排模型列表 -->
    <n-card title="重排模型 (Reranker/)">
      <template #header-extra>
        <n-button text @click="loadRerankModels">
          <template #icon><n-icon :component="RefreshOutline" /></template>
          刷新
        </n-button>
      </template>

      <n-data-table
        :columns="rerankModelColumns"
        :data="rerankModels"
        :loading="rerankLoading"
        :row-key="(row: ModelInfo) => row.name || ''"
      />
    </n-card>

    <!-- 切换模型确认对话框 -->
    <n-modal v-model:show="showSwitchDialog" preset="dialog" title="切换模型">
      <n-space vertical>
        <n-text>确定要切换到模型 <strong>{{ selectedModel?.displayName }}</strong> 吗？</n-text>
        <n-alert type="info" title="提示">
          切换模型后需要重新索引数据以获得最佳效果。
        </n-alert>
        <n-form-item v-if="selectedModel && currentModel && selectedModel.dimension !== currentModel.dimension" label="旧向量数据">
          <n-radio-group v-model:value="deleteOldVectors">
            <n-space>
              <n-radio :value="false">保留旧向量数据</n-radio>
              <n-radio :value="true">删除旧向量数据</n-radio>
            </n-space>
          </n-radio-group>
        </n-form-item>
        <n-alert v-if="deleteOldVectors && selectedModel && currentModel && selectedModel.dimension !== currentModel.dimension" type="error" style="margin-top: 8px">
          删除后将无法恢复旧模型的向量数据
        </n-alert>
      </n-space>
      <template #action>
        <n-button @click="showSwitchDialog = false">取消</n-button>
        <n-button type="primary" :loading="switching" @click="confirmSwitch">确认切换</n-button>
      </template>
    </n-modal>
  </n-space>
</template>

<script setup lang="ts">
import { ref, h, onMounted } from 'vue'
import { NTag, NButton, NSpace, NCode, useMessage } from 'naive-ui'
import type { DataTableColumns } from 'naive-ui'
import { RefreshOutline } from '@vicons/ionicons5'
import { modelsApi, settingsApi } from '@/api'
import type { ModelInfo, ReferenceRAGConfig } from '@/types/api'

const message = useMessage()
const modelsPath = ref('')
const savingModelsPath = ref(false)
const scanning = ref(false)

const embeddingModels = ref<ModelInfo[]>([])
const rerankModels = ref<ModelInfo[]>([])
const currentModel = ref<ModelInfo | null>(null)
const currentRerankModel = ref<ModelInfo | null>(null)

const embeddingLoading = ref(false)
const rerankLoading = ref(false)
const currentLoading = ref(false)

const showSwitchDialog = ref(false)
const selectedModel = ref<ModelInfo | null>(null)
const switching = ref(false)
const deleteOldVectors = ref(false)

const loadModelsPath = async () => {
  try {
    const response = await settingsApi.get()
    const config = response.data as ReferenceRAGConfig
    modelsPath.value = (config as any).modelsRootPath || 'models'
  } catch (error) {
    console.error('Failed to load models path:', error)
    modelsPath.value = 'models'
  }
}

const handleSaveModelsPath = async () => {
  savingModelsPath.value = true
  try {
    await settingsApi.updateModelsPath(modelsPath.value)
    message.success('模型路径已保存')
  } catch (error: any) {
    console.error('Failed to save models path:', error)
    message.error(`保存失败: ${error.response?.data?.error || error.message}`)
  } finally {
    savingModelsPath.value = false
  }
}

const handleScanModels = async () => {
  scanning.value = true
  try {
    // 调用扫描接口
    await modelsApi.scanModels()
    message.success('模型扫描完成')
    await Promise.all([
      loadEmbeddingModels(),
      loadRerankModels()
    ])
  } catch (error: any) {
    console.error('Failed to scan models:', error)
    message.error(`扫描失败: ${error.response?.data?.error || error.message}`)
  } finally {
    scanning.value = false
  }
}

const loadEmbeddingModels = async () => {
  embeddingLoading.value = true
  try {
    const response = await modelsApi.getAll()
    embeddingModels.value = response.data
  } catch (error) {
    console.error('Failed to load embedding models:', error)
    message.error('加载嵌入模型列表失败')
  } finally {
    embeddingLoading.value = false
  }
}

const loadRerankModels = async () => {
  rerankLoading.value = true
  try {
    const response = await modelsApi.getRerankModels()
    rerankModels.value = response.data
  } catch (error) {
    console.error('Failed to load rerank models:', error)
    message.error('加载重排模型列表失败')
  } finally {
    rerankLoading.value = false
  }
}

const loadCurrentModels = async () => {
  currentLoading.value = true
  try {
    const [embeddingRes, rerankRes] = await Promise.all([
      modelsApi.getCurrent(),
      modelsApi.getCurrentRerankModel()
    ])
    currentModel.value = embeddingRes.data
    currentRerankModel.value = rerankRes.data
  } catch (error) {
    console.error('Failed to load current models:', error)
  } finally {
    currentLoading.value = false
  }
}

const handleSwitchEmbedding = (model: ModelInfo) => {
  selectedModel.value = model
  deleteOldVectors.value = false
  showSwitchDialog.value = true
}

const handleSwitchRerank = async (model: ModelInfo) => {
  if (!model.name) return

  try {
    const response = await modelsApi.switchRerankModel(model.name)
    message.success(response.data.message || `已切换到重排模型: ${model.displayName || model.name}`)
    await loadCurrentModels()
    await loadRerankModels()
  } catch (error: any) {
    console.error('Failed to switch rerank model:', error)
    message.error(`切换失败: ${error.response?.data?.error || error.message}`)
  }
}

const confirmSwitch = async () => {
  if (!selectedModel.value || !selectedModel.value.name) return

  switching.value = true
  try {
    const response = await modelsApi.switch(selectedModel.value.name, deleteOldVectors.value)
    message.success(response.data.message || `已切换到 ${selectedModel.value.displayName}`)
    showSwitchDialog.value = false

    await loadCurrentModels()
    await loadEmbeddingModels()
  } catch (error: any) {
    console.error('Failed to switch model:', error)
    message.error(`切换失败: ${error.response?.data?.error || error.message}`)
  } finally {
    switching.value = false
  }
}

const embeddingModelColumns: DataTableColumns<ModelInfo> = [
  {
    title: '模型名称',
    key: 'displayName',
    render(row) {
      return h(NSpace, { align: 'center' }, {
        default: () => [
          h('span', row.displayName || row.name),
          row.name === currentModel.value?.name ? h(NTag, { type: 'success', size: 'small' }, { default: () => '当前' }) : null
        ]
      })
    }
  },
  {
    title: '维度',
    key: 'dimension',
    width: 80
  },
  {
    title: '最大序列长度',
    key: 'maxSequenceLength',
    width: 120
  },
  {
    title: '路径',
    key: 'localPath',
    ellipsis: { tooltip: true }
  },
  {
    title: '操作',
    key: 'actions',
    width: 120,
    render(row) {
      if (row.name === currentModel.value?.name) {
        return h(NTag, { type: 'default', size: 'small' }, { default: () => '使用中' })
      }

      return h(NButton, {
        type: 'primary',
        size: 'small',
        onClick: () => handleSwitchEmbedding(row)
      }, { default: () => '切换' })
    }
  }
]

const rerankModelColumns: DataTableColumns<ModelInfo> = [
  {
    title: '模型名称',
    key: 'displayName',
    render(row) {
      return h(NSpace, { align: 'center' }, {
        default: () => [
          h('span', row.displayName || row.name),
          row.name === currentRerankModel.value?.name ? h(NTag, { type: 'success', size: 'small' }, { default: () => '当前' }) : null
        ]
      })
    }
  },
  {
    title: '维度',
    key: 'dimension',
    width: 80
  },
  {
    title: '最大序列长度',
    key: 'maxSequenceLength',
    width: 120
  },
  {
    title: '路径',
    key: 'localPath',
    ellipsis: { tooltip: true }
  },
  {
    title: '操作',
    key: 'actions',
    width: 120,
    render(row) {
      if (row.name === currentRerankModel.value?.name) {
        return h(NTag, { type: 'default', size: 'small' }, { default: () => '使用中' })
      }

      return h(NButton, {
        type: 'primary',
        size: 'small',
        onClick: () => handleSwitchRerank(row)
      }, { default: () => '切换' })
    }
  }
]

onMounted(() => {
  loadModelsPath()
  loadCurrentModels()
  loadEmbeddingModels()
  loadRerankModels()
})
</script>
