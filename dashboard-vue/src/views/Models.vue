<template>
  <n-space vertical :size="20">
    <!-- Current Model -->
    <n-card title="当前模型">
      <n-spin :show="currentLoading">
        <n-descriptions v-if="currentModel" :column="5" label-placement="left">
          <n-descriptions-item label="模型名称">
            <n-tag type="primary">{{ currentModel.displayName || currentModel.name }}</n-tag>
          </n-descriptions-item>
          <n-descriptions-item label="维度">{{ currentModel.dimension }}</n-descriptions-item>
          <n-descriptions-item label="最大序列长度">{{ currentModel.maxSequenceLength }}</n-descriptions-item>
          <n-descriptions-item label="格式">
            <n-tag :type="getFormatTagType(currentModel.onnxFormat)" size="small">
              {{ getFormatText(currentModel.onnxFormat) }}
            </n-tag>
          </n-descriptions-item>
          <n-descriptions-item label="状态">
            <n-tag :type="currentModel.isDownloaded ? 'success' : 'warning'">
              {{ currentModel.isDownloaded ? '已下载' : '未下载' }}
            </n-tag>
          </n-descriptions-item>
        </n-descriptions>
      </n-spin>
    </n-card>

    <!-- Download/Convert Progress -->
    <n-card v-if="activeDownloads.length > 0" title="任务进度">
      <n-list>
        <n-list-item v-for="download in activeDownloads" :key="download.modelName">
          <n-thing>
            <template #header>
              <n-space align="center">
                <span>{{ getModelDisplayName(download.modelName) }}</span>
                <n-tag :type="getDownloadStatusType(download.status)" size="small">
                  {{ getDownloadStatusText(download.status) }}
                </n-tag>
              </n-space>
            </template>
            
            <!-- Progress bar -->
            <n-progress 
              type="line" 
              :percentage="download.progress" 
              :status="getProgressStatus(download.status)"
              :indicator-placement="'inside'"
              style="margin: 12px 0"
            />
            
            <!-- Error message -->
            <n-alert v-if="download.status === 'failed' && download.errorMessage" type="error" style="margin-top: 8px">
              <template #header>操作失败</template>
              <n-text>{{ download.errorMessage }}</n-text>
              <n-text v-if="getErrorHelp(download.errorCode)" depth="3" style="display: block; margin-top: 8px; font-size: 12px">
                <n-icon :component="InformationCircleOutline" style="vertical-align: middle; margin-right: 4px" />
                {{ getErrorHelp(download.errorCode) }}
              </n-text>
            </n-alert>
          </n-thing>
        </n-list-item>
      </n-list>
    </n-card>

    <!-- Available Models -->
    <n-card title="可用模型">
      <template #header-extra>
        <n-space>
          <n-button text @click="showAddCustomDialog = true">
            <template #icon><n-icon :component="AddOutline" /></template>
            添加模型
          </n-button>
          <n-button text @click="loadModels">
            <template #icon><n-icon :component="RefreshOutline" /></template>
            刷新
          </n-button>
        </n-space>
      </template>
      
      <!-- Info alert -->
      <n-alert type="info" style="margin-bottom: 16px">
        <template #header>提示</template>
        <n-text>
          部分模型需要从 PyTorch 格式转换为 ONNX 格式，转换过程需要 Python 环境。<br>
          请确保已安装：<n-code code="pip install torch transformers onnx" language="bash" :inline="true" />
        </n-text>
      </n-alert>
      
      <n-data-table
        :columns="modelColumns"
        :data="models"
        :loading="modelsLoading"
        :row-key="(row: ModelInfo) => row.name || ''"
      />
    </n-card>

    <!-- Switch Model Dialog -->
    <n-modal v-model:show="showSwitchDialog" preset="dialog" title="切换模型">
      <n-space vertical>
        <n-text>确定要切换到模型 <strong>{{ selectedModel?.displayName }}</strong> 吗？</n-text>
        <n-alert v-if="selectedModel && !selectedModel.isDownloaded" type="warning" title="注意">
          该模型尚未下载，将先下载模型文件。<br>
          <n-text depth="3" v-if="!selectedModel.hasOnnx">
            此模型需要从 PyTorch 转换为 ONNX，请确保 Python 环境已配置。
          </n-text>
        </n-alert>
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

    <!-- Download Confirm Dialog -->
    <n-modal v-model:show="showDownloadDialog" preset="dialog" title="下载模型" style="width: 600px">
      <n-spin :show="loadingOptions">
        <n-space vertical>
          <n-text>即将下载模型 <strong>{{ selectedModel?.displayName }}</strong></n-text>

          <!-- 显示下载选项 -->
          <template v-if="downloadOptions">
            <!-- 需要 PyTorch 转换 -->
            <n-alert v-if="downloadOptions.needsConversion" type="warning" title="需要转换">
              <n-text>
                此模型没有预转换的 ONNX 文件，需要从 PyTorch 转换。<br>
                请确保已安装 Python 和相关依赖：<br>
                <n-code code="pip install torch transformers onnx" language="bash" style="margin-top: 8px" />
              </n-text>
            </n-alert>

            <!-- 有多个选项需要用户选择 -->
            <template v-else-if="downloadOptions.needsUserSelection">
              <n-form-item label="选择要下载的版本">
                <n-radio-group v-model:value="selectedFile" style="width: 100%">
                  <n-space vertical style="width: 100%">

                    <!-- 根目录选项 -->
                    <template v-if="downloadOptions.rootOptions.length > 0">
                      <n-text depth="3" style="font-size: 12px; margin-top: 8px">根目录</n-text>
                      <n-radio
                        v-for="option in downloadOptions.rootOptions"
                        :key="option.path"
                        :value="option.path"
                        style="width: 100%"
                      >
                        <n-space align="center" justify="space-between" style="width: 100%">
                          <n-space align="center">
                            <span>{{ option.displayName }}</span>
                            <n-tag v-if="option.isRecommended" type="success" size="small">推荐</n-tag>
                            <n-tag v-if="option.hasExternalData" type="warning" size="small">外部格式</n-tag>
                            <n-tag v-else type="info" size="small">嵌入式</n-tag>
                            <n-tag v-if="option.isQuantized" type="warning" size="small">量化</n-tag>
                            <n-tag v-if="option.targetPlatform" type="default" size="small">{{ option.targetPlatform }}</n-tag>
                          </n-space>
                        </n-space>
                      </n-radio>
                    </template>

                    <!-- 子目录选项 -->
                    <template v-if="downloadOptions.subfolderOptions.length > 0">
                      <n-text depth="3" style="font-size: 12px; margin-top: 12px">onnx/ 子目录</n-text>
                      <n-radio
                        v-for="option in downloadOptions.subfolderOptions"
                        :key="option.path"
                        :value="option.path"
                        style="width: 100%"
                      >
                        <n-space align="center" justify="space-between" style="width: 100%">
                          <n-space align="center">
                            <span>{{ option.displayName }}</span>
                            <n-tag v-if="option.isRecommended" type="success" size="small">推荐</n-tag>
                            <n-tag v-if="option.hasExternalData" type="warning" size="small">外部格式</n-tag>
                            <n-tag v-else type="info" size="small">嵌入式</n-tag>
                            <n-tag v-if="option.isQuantized" type="warning" size="small">量化</n-tag>
                            <n-tag v-if="option.targetPlatform" type="default" size="small">{{ option.targetPlatform }}</n-tag>
                          </n-space>
                        </n-space>
                      </n-radio>
                    </template>

                  </n-space>
                </n-radio-group>
              </n-form-item>

              <!-- 选中项的描述 -->
              <n-alert v-if="selectedFileDescription" type="info" style="margin-top: 8px">
                {{ selectedFileDescription }}
              </n-alert>
            </template>

            <!-- 只有一个选项，无需选择 -->
            <template v-else>
              <n-descriptions :column="1" label-placement="left">
                <n-descriptions-item label="版本">
                  <n-space align="center">
                    <span>{{ downloadOptions.recommendedOption?.displayName }}</span>
                    <n-tag v-if="downloadOptions.recommendedOption?.hasExternalData" type="warning" size="small">外部格式</n-tag>
                    <n-tag v-else type="info" size="small">嵌入式</n-tag>
                  </n-space>
                </n-descriptions-item>
                <n-descriptions-item label="维度">{{ selectedModel?.dimension }}</n-descriptions-item>
                <n-descriptions-item label="大小">{{ formatBytes(selectedModel?.modelSizeBytes || 0) }}</n-descriptions-item>
              </n-descriptions>
            </template>
          </template>

          <!-- 格式说明 -->
          <n-alert type="info" style="margin-top: 8px">
            <template #header>格式说明</template>
            <n-text style="font-size: 13px">
              <strong>嵌入式</strong>：单个 ONNX 文件，兼容性好，有 2GB 限制。<br>
              <strong>外部格式</strong>：分离的权重文件，支持大模型，下载后可转换为嵌入式。
            </n-text>
          </n-alert>
        </n-space>
      </n-spin>

      <template #action>
        <n-button @click="showDownloadDialog = false">取消</n-button>
        <n-button type="primary" @click="confirmDownload" :disabled="!selectedFile && !downloadOptions?.needsConversion">开始下载</n-button>
      </template>
    </n-modal>

    <!-- Convert Format Dialog -->
    <n-modal v-model:show="showConvertDialog" preset="dialog" title="转换模型格式">
      <n-space vertical>
        <n-text>转换模型 <strong>{{ selectedModel?.displayName }}</strong> 的 ONNX 格式</n-text>
        <n-descriptions :column="1" label-placement="left">
          <n-descriptions-item label="当前格式">
            <n-tag :type="getFormatTagType(selectedModel?.onnxFormat)" size="small">
              {{ getFormatText(selectedModel?.onnxFormat) }}
            </n-tag>
          </n-descriptions-item>
          <n-descriptions-item label="目标格式">
            <n-radio-group v-model:value="targetFormat">
              <n-space>
                <n-radio value="embedded">
                  <n-space align="center">
                    <span>嵌入式</span>
                    <n-text depth="3" style="font-size: 12px">(CUDA 兼容性好)</n-text>
                  </n-space>
                </n-radio>
                <n-radio value="external">
                  <n-space align="center">
                    <span>外部数据</span>
                    <n-text depth="3" style="font-size: 12px">(支持大模型)</n-text>
                  </n-space>
                </n-radio>
              </n-space>
            </n-radio-group>
          </n-descriptions-item>
        </n-descriptions>
        <n-alert type="info" title="说明">
          <n-text>
            <strong>嵌入式格式</strong>：单个 ONNX 文件，CUDA 兼容性好，但有 2GB 限制。<br>
            <strong>外部数据格式</strong>：分离的权重文件，支持大模型，但 CUDA 动态 batch 可能有问题。
          </n-text>
        </n-alert>
        <n-alert v-if="selectedModel && selectedModel.onnxFormat === targetFormat" type="warning">
          模型已是目标格式，无需转换。
        </n-alert>
      </n-space>
      <template #action>
        <n-button @click="showConvertDialog = false">取消</n-button>
        <n-button 
          type="primary" 
          :disabled="selectedModel?.onnxFormat === targetFormat"
          :loading="converting" 
          @click="confirmConvert">
          开始转换
        </n-button>
      </template>
    </n-modal>

    <!-- Add Custom Model Dialog -->
    <n-modal v-model:show="showAddCustomDialog" preset="dialog" title="添加自定义模型">
      <n-space vertical>
        <n-form :model="customModelForm">
          <n-form-item label="HuggingFace 模型 ID" required>
            <n-input 
              v-model:value="customModelForm.huggingFaceId" 
              placeholder="例如: BAAI/bge-base-zh-v1.5"
            />
          </n-form-item>
          <n-form-item label="显示名称（可选）">
            <n-input 
              v-model:value="customModelForm.displayName" 
              placeholder="留空则使用模型名称"
            />
          </n-form-item>
        </n-form>
        <n-alert type="info">
          <n-text>
            输入 HuggingFace 上的模型 ID，系统将自动获取模型信息并添加到列表中。<br>
            格式：<n-code code="owner/model-name" :inline="true" />
          </n-text>
        </n-alert>
      </n-space>
      <template #action>
        <n-button @click="showAddCustomDialog = false">取消</n-button>
        <n-button type="primary" :loading="addingCustom" @click="confirmAddCustom">添加</n-button>
      </template>
    </n-modal>

    <!-- Delete Model Dialog -->
    <n-modal v-model:show="showDeleteDialog" preset="dialog" title="删除模型">
      <n-space vertical>
        <n-text>确定要删除模型 <strong>{{ selectedModel?.displayName }}</strong> 吗？</n-text>
        <n-alert type="warning" title="警告">
          删除后将同时删除模型文件夹及其所有内容，此操作不可恢复。
        </n-alert>
      </n-space>
      <template #action>
        <n-button @click="showDeleteDialog = false">取消</n-button>
        <n-button type="error" :loading="deleting" @click="confirmDelete">确认删除</n-button>
      </template>
    </n-modal>
  </n-space>
</template>

<script setup lang="ts">
import { ref, h, onMounted, onUnmounted, computed } from 'vue'
import { NTag, NButton, NSpace, NProgress, NAlert, NCode, NIcon, NPopconfirm, NRadioGroup, NRadio, NFormItem, NSpin, useMessage } from 'naive-ui'
import type { DataTableColumns } from 'naive-ui'
import { RefreshOutline, AddOutline, InformationCircleOutline } from '@vicons/ionicons5'
import { modelsApi } from '@/api'
import type { ModelInfo, DownloadProgress, ModelDownloadOptions } from '@/types/api'

const message = useMessage()
const models = ref<ModelInfo[]>([])
const currentModel = ref<ModelInfo | null>(null)
const modelsLoading = ref(false)
const currentLoading = ref(false)
const showSwitchDialog = ref(false)
const showDownloadDialog = ref(false)
const showConvertDialog = ref(false)
const showAddCustomDialog = ref(false)
const showDeleteDialog = ref(false)
const selectedModel = ref<ModelInfo | null>(null)
const switching = ref(false)
const converting = ref(false)
const deleting = ref(false)
const addingCustom = ref(false)
const targetFormat = ref<'embedded' | 'external'>('embedded')
const deleteOldVectors = ref(false)
const downloadProgress = ref<Map<string, DownloadProgress>>(new Map())
const downloadOptions = ref<ModelDownloadOptions | null>(null)
const selectedFile = ref<string | null>(null)
const loadingOptions = ref(false)
const customModelForm = ref({
  huggingFaceId: '',
  displayName: ''
})
let progressInterval: number | null = null

const activeDownloads = computed(() => {
  return Array.from(downloadProgress.value.values()).filter(
    d => d.status === 'downloading' || d.status === 'completed' || d.status === 'failed'
  )
})

const selectedFileDescription = computed(() => {
  if (!selectedFile.value || !downloadOptions.value) return null
  const option = downloadOptions.value.allOptions.find(o => o.path === selectedFile.value)
  return option?.description || null
})

const getFormatTagType = (format?: string) => {
  switch (format) {
    case 'embedded': return 'success'
    case 'external': return 'warning'
    default: return 'default'
  }
}

const getFormatText = (format?: string) => {
  switch (format) {
    case 'embedded': return '嵌入式'
    case 'external': return '外部数据'
    default: return '未知'
  }
}

const modelColumns: DataTableColumns<ModelInfo> = [
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
    title: '描述',
    key: 'description',
    ellipsis: { tooltip: true }
  },
  {
    title: '维度',
    key: 'dimension',
    width: 70
  },
  {
    title: '格式',
    key: 'onnxFormat',
    width: 100,
    render(row) {
      if (!row.isDownloaded) return '-'
      return h(NTag, { type: getFormatTagType(row.onnxFormat), size: 'small' }, {
        default: () => getFormatText(row.onnxFormat)
      })
    }
  },
  {
    title: '大小',
    key: 'modelSizeBytes',
    width: 100,
    render(row) {
      return row.modelSizeBytes ? formatBytes(row.modelSizeBytes) : '-'
    }
  },
  {
    title: '状态',
    key: 'isDownloaded',
    width: 90,
    render(row) {
      const progress = downloadProgress.value.get(row.name || '')
      if (progress && progress.status === 'downloading') {
        return h(NProgress, { 
          type: 'line', 
          percentage: progress.progress, 
          status: 'default' as const,
          style: 'width: 70px'
        })
      }
      return h(NTag, { type: row.isDownloaded ? 'success' : 'warning', size: 'small' }, {
        default: () => row.isDownloaded ? '已下载' : '未下载'
      })
    }
  },
  {
    title: '操作',
    key: 'actions',
    width: 200,
    render(row) {
      const progress = downloadProgress.value.get(row.name || '')
      const isDownloading = progress && progress.status === 'downloading'
      
      if (row.name === currentModel.value?.name) {
        return h(NTag, { type: 'default', size: 'small' }, { default: () => '使用中' })
      }
      
      const buttons: ReturnType<typeof h>[] = []
      
      if (isDownloading) {
        buttons.push(h(NButton, {
          size: 'small',
          disabled: true
        }, { default: () => '处理中...' }))
      } else if (!row.isDownloaded) {
        buttons.push(h(NButton, {
          type: 'primary',
          size: 'small',
          onClick: () => handleDownload(row)
        }, { default: () => '下载' }))
      }
      
      if (row.isDownloaded && !isDownloading) {
        buttons.push(h(NButton, {
          type: 'primary',
          size: 'small',
          onClick: () => handleSwitch(row)
        }, { default: () => '切换' }))
        
        // 格式转换按钮
        buttons.push(h(NButton, {
          size: 'small',
          onClick: () => handleConvert(row)
        }, { default: () => '转换' }))
        
        // 删除按钮
        buttons.push(h(NPopconfirm, {
          onPositiveClick: () => handleDelete(row)
        }, {
          trigger: () => h(NButton, { size: 'small', type: 'error' }, { default: () => '删除' }),
          default: () => `确定删除模型 "${row.displayName || row.name}"？此操作不可恢复。`
        }))
      }
      
      return h(NSpace, { size: 'small' }, { default: () => buttons })
    }
  }
]

const getModelDisplayName = (modelName: string) => {
  const model = models.value.find(m => m.name === modelName)
  return model?.displayName || modelName
}

const getDownloadStatusType = (status: string) => {
  switch (status) {
    case 'downloading': return 'info'
    case 'completed': return 'success'
    case 'failed': return 'error'
    case 'cancelled': return 'warning'
    default: return 'default'
  }
}

const getDownloadStatusText = (status: string) => {
  switch (status) {
    case 'downloading': return '处理中'
    case 'completed': return '已完成'
    case 'failed': return '失败'
    case 'cancelled': return '已取消'
    default: return '空闲'
  }
}

const getProgressStatus = (status: string): 'default' | 'error' | 'success' | 'warning' | undefined => {
  switch (status) {
    case 'completed': return 'success'
    case 'failed': return 'error'
    case 'cancelled': return 'warning'
    default: return 'default'
  }
}

// 错误码对应的帮助信息
const getErrorHelp = (errorCode?: number) => {
  switch (errorCode) {
    case 3: return '文件下载不完整，请检查网络连接后重试。'
    case 4: return 'ONNX转换失败，请确保Python环境已正确配置。'
    case 5: return 'Python环境不可用，请安装Python 3.8+。'
    case 6: return 'Python依赖缺失，请运行: pip install torch transformers optimum onnx'
    case 7: return '模型过大不适合嵌入式格式，请使用"外部数据"格式重试。'
    case 8: return '网络错误，请检查网络连接后重试。'
    case 9: return '存储空间不足，请清理磁盘空间后重试。'
    default: return null
  }
}

const formatBytes = (bytes: number) => {
  if (bytes === 0) return '0 B'
  const k = 1024
  const sizes = ['B', 'KB', 'MB', 'GB']
  const i = Math.floor(Math.log(bytes) / Math.log(k))
  return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i]
}

const loadModels = async () => {
  modelsLoading.value = true
  try {
    const response = await modelsApi.getAll()
    models.value = response.data
  } catch (error) {
    console.error('Failed to load models:', error)
    message.error('加载模型列表失败')
  } finally {
    modelsLoading.value = false
  }
}

const loadCurrentModel = async () => {
  currentLoading.value = true
  try {
    const response = await modelsApi.getCurrent()
    currentModel.value = response.data
  } catch (error) {
    console.error('Failed to load current model:', error)
  } finally {
    currentLoading.value = false
  }
}

const handleDownload = async (model: ModelInfo) => {
  selectedModel.value = model
  selectedFile.value = null
  downloadOptions.value = null

  // 获取下载选项
  loadingOptions.value = true
  try {
    const response = await modelsApi.getDownloadOptions(model.name || '')
    downloadOptions.value = response.data

    // 如果只有一个选项或不需要用户选择，自动选择推荐选项
    if (downloadOptions.value && !downloadOptions.value.needsUserSelection && downloadOptions.value.recommendedOption) {
      selectedFile.value = downloadOptions.value.recommendedOption.path
    } else if (downloadOptions.value && downloadOptions.value.allOptions.length > 0) {
      // 有多个选项，选择推荐项
      const recommended = downloadOptions.value.allOptions.find(o => o.isRecommended)
      selectedFile.value = recommended?.path || downloadOptions.value.allOptions[0].path
    }
  } catch (error) {
    console.error('Failed to get download options:', error)
    downloadOptions.value = null
  } finally {
    loadingOptions.value = false
  }

  showDownloadDialog.value = true
}

const confirmDownload = async () => {
  if (!selectedModel.value || !selectedModel.value.name) return

  showDownloadDialog.value = false
  message.info(`开始下载模型: ${selectedModel.value.displayName}`)

  try {
    await modelsApi.download(selectedModel.value.name, selectedFile.value || undefined)

    downloadProgress.value.set(selectedModel.value.name, {
      modelName: selectedModel.value.name,
      status: 'downloading',
      progress: 0,
      bytesReceived: 0,
      totalBytes: selectedModel.value.modelSizeBytes || 0,
      speedBytesPerSecond: 0,
      estimatedSecondsRemaining: null
    })

    startProgressPolling()
  } catch (error: any) {
    console.error('Failed to start download:', error)
    message.error(`下载失败: ${error.response?.data?.error || error.message}`)
  }
}

const handleSwitch = (model: ModelInfo) => {
  selectedModel.value = model
  deleteOldVectors.value = false
  showSwitchDialog.value = true
}

const confirmSwitch = async () => {
  if (!selectedModel.value || !selectedModel.value.name) return
  
  switching.value = true
  try {
    const response = await modelsApi.switch(selectedModel.value.name, deleteOldVectors.value)
    message.success(response.data.message || `已切换到 ${selectedModel.value.displayName}`)
    showSwitchDialog.value = false
    
    await loadCurrentModel()
    await loadModels()
  } catch (error: any) {
    console.error('Failed to switch model:', error)
    message.error(`切换失败: ${error.response?.data?.error || error.message}`)
  } finally {
    switching.value = false
  }
}

const handleConvert = (model: ModelInfo) => {
  selectedModel.value = model
  targetFormat.value = model.onnxFormat === 'embedded' ? 'external' : 'embedded'
  showConvertDialog.value = true
}

const confirmConvert = async () => {
  if (!selectedModel.value || !selectedModel.value.name) return
  
  converting.value = true
  try {
    await modelsApi.convert(selectedModel.value.name, targetFormat.value)
    
    downloadProgress.value.set(`convert_${selectedModel.value.name}`, {
      modelName: selectedModel.value.name,
      status: 'downloading',
      progress: 0,
      bytesReceived: 0,
      totalBytes: 0,
      speedBytesPerSecond: 0,
      estimatedSecondsRemaining: null
    })
    
    showConvertDialog.value = false
    message.info(`开始转换模型格式: ${selectedModel.value.displayName}`)
    
    // Poll for convert progress
    const pollConvertProgress = async () => {
      try {
        const response = await modelsApi.getConvertProgress(selectedModel.value!.name!)
        const progress = response.data
        
        downloadProgress.value.set(`convert_${selectedModel.value!.name!}`, progress)
        
        if (progress.status === 'completed') {
          message.success('模型格式转换完成')
          await loadModels()
          await loadCurrentModel()
        } else if (progress.status === 'failed') {
          message.error(`转换失败: ${progress.errorMessage}`)
        } else {
          setTimeout(pollConvertProgress, 2000)
        }
      } catch (error) {
        console.error('Failed to get convert progress:', error)
      }
    }
    
    setTimeout(pollConvertProgress, 2000)
  } catch (error: any) {
    console.error('Failed to start convert:', error)
    message.error(`转换失败: ${error.response?.data?.error || error.message}`)
  } finally {
    converting.value = false
  }
}

const handleDelete = async (model: ModelInfo) => {
  if (!model.name) return
  
  deleting.value = true
  try {
    await modelsApi.delete(model.name)
    message.success(`已删除模型: ${model.displayName || model.name}`)
    await loadModels()
  } catch (error: any) {
    console.error('Failed to delete model:', error)
    message.error(`删除失败: ${error.response?.data?.error || error.message}`)
  } finally {
    deleting.value = false
  }
}

const confirmDelete = async () => {
  if (!selectedModel.value) return
  await handleDelete(selectedModel.value)
  showDeleteDialog.value = false
}

const confirmAddCustom = async () => {
  if (!customModelForm.value.huggingFaceId) {
    message.error('请输入 HuggingFace 模型 ID')
    return
  }
  
  addingCustom.value = true
  try {
    const response = await modelsApi.addCustom(
      customModelForm.value.huggingFaceId,
      customModelForm.value.displayName || undefined
    )
    message.success(response.data.message || '已添加自定义模型')
    showAddCustomDialog.value = false
    customModelForm.value = { huggingFaceId: '', displayName: '' }
    await loadModels()
  } catch (error: any) {
    console.error('Failed to add custom model:', error)
    message.error(`添加失败: ${error.response?.data?.error || error.message}`)
  } finally {
    addingCustom.value = false
  }
}

const startProgressPolling = () => {
  if (progressInterval) return
  
  progressInterval = window.setInterval(async () => {
    const downloadingModels = Array.from(downloadProgress.value.keys())
      .filter(name => {
        const p = downloadProgress.value.get(name)
        return p && (p.status === 'downloading')
      })
    
    if (downloadingModels.length === 0) {
      stopProgressPolling()
      return
    }
    
    for (const modelName of downloadingModels) {
      try {
        const response = await modelsApi.getDownloadProgress(modelName)
        downloadProgress.value.set(modelName, response.data)
        
        if (response.data.status === 'completed') {
          message.success(`模型 ${getModelDisplayName(modelName)} 下载完成`)
          await loadModels()
        } else if (response.data.status === 'failed') {
          message.error(`模型 ${getModelDisplayName(modelName)} 下载失败`)
          await loadModels()
        }
      } catch (error) {
        console.error('Failed to get progress:', error)
      }
    }
  }, 2000)
}

const stopProgressPolling = () => {
  if (progressInterval) {
    clearInterval(progressInterval)
    progressInterval = null
  }
}

onMounted(() => {
  loadModels()
  loadCurrentModel()
})

onUnmounted(() => {
  stopProgressPolling()
})
</script>
