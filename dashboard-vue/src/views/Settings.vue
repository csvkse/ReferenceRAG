<template>
  <n-space vertical :size="20">
    <n-spin :show="loading">
      <n-tabs type="line" animated>
        <!-- Embedding Settings -->
        <n-tab-pane name="embedding" tab="嵌入模型">
          <n-card>
            <n-form label-placement="left" label-width="140">
              <n-form-item label="模型路径">
                <n-input v-model:value="config.embedding.modelPath" placeholder="ONNX 模型路径" />
              </n-form-item>
              <n-form-item label="模型名称">
                <n-input v-model:value="config.embedding.modelName" placeholder="bge-small-zh-v1.5" />
              </n-form-item>
              <n-form-item label="使用 CUDA">
                <n-switch v-model:value="config.embedding.useCuda" />
              </n-form-item>
              <n-form-item v-if="config.embedding.useCuda" label="CUDA 设备 ID">
                <n-input-number v-model:value="config.embedding.cudaDeviceId" :min="0" :max="7" style="width: 200px" />
              </n-form-item>
              <n-form-item v-if="config.embedding.useCuda" label="CUDA 库路径">
                <n-input v-model:value="config.embedding.cudaLibraryPath" placeholder="CUDA DLL 所在目录（可选）" />
              </n-form-item>
              <n-form-item label="最大序列长度">
                <n-input-number v-model:value="config.embedding.maxSequenceLength" :min="32" :max="2048" style="width: 100%" />
              </n-form-item>
              <n-form-item label="批处理大小">
                <n-input-number v-model:value="config.embedding.batchSize" :min="1" :max="256" style="width: 100%" />
              </n-form-item>
              <n-form-item label="模型存储路径">
                <n-input v-model:value="config.embedding.modelsPath" placeholder="默认: models" />
              </n-form-item>
            </n-form>
          </n-card>
        </n-tab-pane>

        <!-- Chunking Settings -->
        <n-tab-pane name="chunking" tab="分段设置">
          <n-card>
            <n-form label-placement="left" label-width="140">
              <n-form-item label="最大 Token 数">
                <n-input-number v-model:value="config.chunking.maxTokens" :min="64" :max="4096" :step="64" style="width: 100%" />
              </n-form-item>
              <n-form-item label="最小 Token 数">
                <n-input-number v-model:value="config.chunking.minTokens" :min="10" :max="512" :step="10" style="width: 100%" />
              </n-form-item>
              <n-form-item label="重叠 Token 数">
                <n-input-number v-model:value="config.chunking.overlapTokens" :min="0" :max="512" :step="10" style="width: 100%" />
              </n-form-item>
              <n-form-item label="保留标题结构">
                <n-switch v-model:value="config.chunking.preserveHeadings" />
              </n-form-item>
              <n-form-item label="保留代码块">
                <n-switch v-model:value="config.chunking.preserveCodeBlocks" />
              </n-form-item>
            </n-form>
          </n-card>
        </n-tab-pane>

        <!-- Search Settings -->
        <n-tab-pane name="search" tab="搜索设置">
          <n-card>
            <n-form label-placement="left" label-width="140">
              <n-form-item label="默认返回数量">
                <n-input-number v-model:value="config.search.defaultTopK" :min="1" :max="100" style="width: 100%" />
              </n-form-item>
              <n-form-item label="上下文窗口">
                <n-input-number v-model:value="config.search.contextWindow" :min="0" :max="5" style="width: 100%" />
              </n-form-item>
              <n-form-item label="相似度阈值">
                <n-slider v-model:value="thresholdPercent" :min="0" :max="100" :step="1" />
              </n-form-item>
              <n-form-item label="启用 MMR 多样性">
                <n-switch v-model:value="config.search.enableMmr" />
              </n-form-item>
              <n-form-item v-if="config.search.enableMmr" label="MMR Lambda">
                <n-slider v-model:value="mmrLambdaPercent" :min="0" :max="100" :step="1" />
              </n-form-item>
              <n-form-item label="默认搜索源">
                <n-select
                  v-model:value="config.search.defaultSources"
                  multiple
                  placeholder="全部源"
                  clearable
                  :options="sourceNameOptions"
                />
              </n-form-item>
            </n-form>
          </n-card>
        </n-tab-pane>

        <!-- Service Settings -->
        <n-tab-pane name="service" tab="服务设置">
          <n-card>
            <n-form label-placement="left" label-width="140">
              <n-form-item label="监听端口">
                <n-input-number v-model:value="config.service.port" :min="1024" :max="65535" style="width: 200px" />
              </n-form-item>
              <n-form-item label="监听地址">
                <n-input v-model:value="config.service.host" placeholder="localhost" style="width: 200px" />
              </n-form-item>
              <n-form-item label="启用 CORS">
                <n-switch v-model:value="config.service.enableCors" />
              </n-form-item>
              <n-form-item label="启用 Swagger">
                <n-switch v-model:value="config.service.enableSwagger" />
              </n-form-item>
              <n-form-item label="日志级别">
                <n-select
                  v-model:value="config.service.logLevel"
                  :options="logLevelOptions"
                  style="width: 200px"
                />
              </n-form-item>
            </n-form>
          </n-card>
        </n-tab-pane>

        <!-- Rerank Settings -->
        <n-tab-pane name="rerank" tab="重排模型">
          <n-card v-if="config.rerank">
            <n-form label-placement="left" label-width="140">
              <n-form-item label="启用重排">
                <n-switch v-model:value="config.rerank.enabled" />
              </n-form-item>
              <n-form-item label="模型名称">
                <n-input v-model:value="config.rerank.modelName" placeholder="bge-reranker-base" />
              </n-form-item>
              <n-form-item label="当前模型">
                <n-input v-model:value="config.rerank.currentModel" placeholder="当前使用的重排模型" disabled />
              </n-form-item>
              <n-form-item label="模型路径">
                <n-input v-model:value="config.rerank.modelPath" placeholder="重排模型 ONNX 文件路径" />
              </n-form-item>
              <n-form-item label="使用 CUDA">
                <n-switch v-model:value="config.rerank.useCuda" />
              </n-form-item>
              <n-form-item v-if="config.rerank.useCuda" label="CUDA 设备 ID">
                <n-input-number v-model:value="config.rerank.cudaDeviceId" :min="0" :max="7" style="width: 200px" />
              </n-form-item>
              <n-form-item label="重排返回数量">
                <n-input-number v-model:value="config.rerank.topN" :min="1" :max="100" style="width: 100%" />
              </n-form-item>
              <n-form-item label="召回倍数">
                <n-input-number v-model:value="config.rerank.recallFactor" :min="1" :max="10" style="width: 100%" />
                <n-text depth="3" style="font-size: 12px; margin-left: 8px">候选文档数 = TopN × 召回倍数</n-text>
              </n-form-item>
            </n-form>
          </n-card>
          <n-card v-else>
            <n-text depth="3">重排模型配置未加载</n-text>
          </n-card>
        </n-tab-pane>
      </n-tabs>

      <!-- Data Path -->
      <n-card title="数据路径" style="margin-top: 16px">
        <n-form label-placement="left" label-width="140">
          <n-form-item label="数据存储路径">
            <n-input v-model:value="config.dataPath" placeholder="data" />
          </n-form-item>
        </n-form>
      </n-card>

      <!-- Save Button -->
      <n-space style="margin-top: 16px; justify-content: flex-end">
        <n-button type="primary" :loading="saving" @click="handleSave">保存配置</n-button>
      </n-space>
    </n-spin>
  </n-space>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { useMessage } from 'naive-ui'
import { settingsApi, sourcesApi } from '@/api'
import type { ObsidianRagConfig, SourceDetail } from '@/types/api'

const message = useMessage()
const loading = ref(false)
const saving = ref(false)
const sourceNames = ref<string[]>([])

const defaultConfig: ObsidianRagConfig = {
  dataPath: 'data',
  sources: [],
  embedding: {
    modelPath: '',
    modelName: 'bge-small-zh-v1.5',
    useCuda: false,
    cudaDeviceId: 0,
    maxSequenceLength: 512,
    batchSize: 32
  },
  chunking: {
    maxTokens: 512,
    minTokens: 50,
    overlapTokens: 50,
    preserveHeadings: true,
    preserveCodeBlocks: true
  },
  search: {
    defaultTopK: 10,
    contextWindow: 1,
    similarityThreshold: 0.5,
    enableMmr: true,
    mmrLambda: 0.7,
    defaultSources: []
  },
  service: {
    port: 5000,
    host: 'localhost',
    enableCors: true,
    enableSwagger: true,
    logLevel: 'Information'
  },
  rerank: {
    enabled: false,
    modelName: 'bge-reranker-base',
    currentModel: '',
    modelPath: '',
    useCuda: false,
    cudaDeviceId: 0,
    topN: 10,
    recallFactor: 3
  }
}

const config = ref<ObsidianRagConfig>(JSON.parse(JSON.stringify(defaultConfig)))

const thresholdPercent = computed({
  get: () => Math.round(config.value.search.similarityThreshold * 100),
  set: (v: number) => { config.value.search.similarityThreshold = v / 100 }
})

const mmrLambdaPercent = computed({
  get: () => Math.round(config.value.search.mmrLambda * 100),
  set: (v: number) => { config.value.search.mmrLambda = v / 100 }
})

const sourceNameOptions = computed(() =>
  sourceNames.value.map(n => ({ label: n, value: n }))
)

const logLevelOptions = [
  { label: 'Debug', value: 'Debug' },
  { label: 'Information', value: 'Information' },
  { label: 'Warning', value: 'Warning' },
  { label: 'Error', value: 'Error' },
  { label: 'Trace', value: 'Trace' },
  { label: 'Critical', value: 'Critical' },
  { label: 'None', value: 'None' }
]

const loadConfig = async () => {
  loading.value = true
  try {
    const response = await settingsApi.get()
    const data = response.data as any
    config.value = { ...JSON.parse(JSON.stringify(defaultConfig)), ...data }
    if (!config.value.embedding.modelsPath) {
      config.value.embedding.modelsPath = 'models'
    }
  } catch (error) {
    message.error('加载配置失败，使用默认值')
  } finally {
    loading.value = false
  }
}

const loadSourceNames = async () => {
  try {
    const response = await sourcesApi.getAll()
    sourceNames.value = (response.data as SourceDetail[]).map(s => s.name)
  } catch {
    // ignore
  }
}

const handleSave = async () => {
  saving.value = true
  try {
    const modelsPath = config.value.embedding.modelsPath
    if (modelsPath && modelsPath !== 'models') {
      await settingsApi.updateModelsPath(modelsPath)
    }
    await settingsApi.save(config.value)
    message.success('配置已保存')
  } catch (error: any) {
    message.error(error.response?.data?.error || '保存配置失败')
  } finally {
    saving.value = false
  }
}

onMounted(() => {
  loadConfig()
  loadSourceNames()
})
</script>
