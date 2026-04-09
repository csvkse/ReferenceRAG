<template>
  <n-space vertical :size="20">
    <!-- API Endpoints -->
    <n-card title="常用 API 接口">
      <n-tabs type="line" animated>
        <n-tab-pane name="query" tab="查询搜索">
          <ApiEndpoint
            title="语义搜索"
            method="POST"
            path="/api/ai/query"
            :description="'对知识库进行语义搜索，支持多种查询模式'"
            :request-body="queryExample"
            :response-example="queryResponseExample"
          />
          <ApiEndpoint
            title="深入查询"
            method="POST"
            path="/api/ai/drill-down"
            description="对特定结果进行深入上下文展开"
            :request-body="drilldownExample"
            :response-example="drilldownResponseExample"
          />
        </n-tab-pane>

        <n-tab-pane name="sources" tab="数据源管理">
          <ApiEndpoint
            title="获取所有数据源"
            method="GET"
            path="/api/Sources"
            description="获取已配置的所有数据源列表"
            :response-example="sourcesResponseExample"
          />
          <ApiEndpoint
            title="添加数据源"
            method="POST"
            path="/api/Sources"
            description="添加新的数据源目录"
            :request-body="addSourceExample"
            :response-example="addSourceResponseExample"
          />
          <ApiEndpoint
            title="触发索引"
            method="POST"
            path="/api/Sources/{name}/index"
            description="手动触发指定数据源的索引任务"
            :request-body="{ force: false }"
            :response-example="indexResponseExample"
          />
        </n-tab-pane>

        <n-tab-pane name="models" tab="模型管理">
          <ApiEndpoint
            title="获取可用模型"
            method="GET"
            path="/api/Models"
            description="获取所有支持的嵌入模型列表"
            :response-example="modelsResponseExample"
          />
          <ApiEndpoint
            title="获取当前模型"
            method="GET"
            path="/api/Models/current"
            description="获取当前使用的模型信息"
            :response-example="currentModelResponseExample"
          />
          <ApiEndpoint
            title="切换模型"
            method="POST"
            path="/api/Models/switch"
            description="切换到指定的嵌入模型"
            :request-body="{ modelName: 'bge-small-zh-v1.5' }"
            :response-example="switchModelResponseExample"
          />
          <ApiEndpoint
            title="下载模型"
            method="POST"
            path="/api/Models/download/{modelName}"
            description="开始下载指定的模型文件"
            :response-example="downloadModelResponseExample"
          />
          <ApiEndpoint
            title="获取下载进度"
            method="GET"
            path="/api/Models/download/{modelName}/progress"
            description="获取模型下载进度"
            :response-example="downloadProgressResponseExample"
          />
        </n-tab-pane>

        <n-tab-pane name="system" tab="系统监控">
          <ApiEndpoint
            title="系统状态"
            method="GET"
            path="/api/system/status"
            description="获取系统整体状态和健康检查结果"
            :response-example="systemStatusResponseExample"
          />
          <ApiEndpoint
            title="健康检查"
            method="GET"
            path="/api/system/health"
            description="执行系统健康检查"
            :response-example="healthResponseExample"
          />
          <ApiEndpoint
            title="查询指标"
            method="GET"
            path="/api/system/metrics/queries"
            description="获取查询性能指标统计"
            :response-example="metricsResponseExample"
          />
          <ApiEndpoint
            title="获取告警"
            method="GET"
            path="/api/system/alerts"
            description="获取活动告警列表"
            :response-example="alertsResponseExample"
          />
        </n-tab-pane>

        <n-tab-pane name="index" tab="索引管理">
          <ApiEndpoint
            title="启动索引任务"
            method="POST"
            path="/api/Index/start"
            description="启动索引任务，可指定特定数据源"
            :request-body="startIndexExample"
            :response-example="startIndexResponseExample"
          />
          <ApiEndpoint
            title="获取活动索引任务"
            method="GET"
            path="/api/Index/active"
            description="获取正在执行的索引任务列表"
            :response-example="activeIndexResponseExample"
          />
          <ApiEndpoint
            title="获取索引状态"
            method="GET"
            path="/api/Index/{indexId}/status"
            description="获取指定索引任务的详细状态"
            :response-example="indexStatusResponseExample"
          />
        </n-tab-pane>
      </n-tabs>
    </n-card>

    <!-- API Tester -->
    <n-card title="API 测试工具">
      <n-space vertical>
        <n-form label-placement="left" label-width="60">
          <n-form-item label="方法">
            <n-select
              v-model:value="testMethod"
              :options="methodOptions"
              style="width: 120px"
            />
          </n-form-item>
          <n-form-item label="路径">
            <n-input v-model:value="testPath" placeholder="/api/..." />
          </n-form-item>
          <n-form-item label="请求体">
            <n-input
              v-model:value="testBody"
              type="textarea"
              placeholder="JSON 格式请求体（可选）"
              :rows="6"
              style="font-family: monospace"
            />
          </n-form-item>
        </n-form>
        <n-space>
          <n-button type="primary" :loading="testLoading" @click="runApiTest">
            发送请求
          </n-button>
          <n-button @click="copyTestCurl">
            复制 cURL
          </n-button>
        </n-space>

        <n-card v-if="testResponse" title="响应" size="small">
          <template #header-extra>
            <n-tag :type="testResponseStatus >= 200 && testResponseStatus < 300 ? 'success' : 'error'" size="small">
              {{ testResponseStatus }}
            </n-tag>
          </template>
          <n-code :code="testResponse" language="json" word-wrap />
        </n-card>
      </n-space>
    </n-card>
  </n-space>
</template>

<script setup lang="ts">
import { ref, defineComponent, h } from 'vue'
import { NCard, NTabs, NTabPane, NSpace, NButton, NCode, NTag, NText, NIcon, useMessage } from 'naive-ui'
import { CopyOutline, CheckmarkOutline } from '@vicons/ionicons5'
import axios from 'axios'

const message = useMessage()

// API Examples
const queryExample = {
  query: "如何配置向量搜索",
  mode: "Hybrid",
  topK: 10,
  contextWindow: 1,
  sources: []
}

const queryResponseExample = {
  query: "如何配置向量搜索",
  mode: "Hybrid",
  context: "向量搜索配置...",
  chunks: [
    {
      refId: "chunk-001",
      filePath: "docs/setup.md",
      title: "向量搜索配置",
      content: "配置向量搜索需要...",
      score: 0.92,
      startLine: 10,
      endLine: 25
    }
  ],
  stats: {
    totalMatches: 5,
    durationMs: 45,
    estimatedTokens: 1200
  }
}

const drilldownExample = {
  query: "向量搜索",
  refIds: ["chunk-001"],
  expandContext: 2
}

const drilldownResponseExample = {
  expandedChunks: [],
  fullContext: "扩展的上下文内容..."
}

const addSourceExample = {
  path: "/path/to/obsidian/vault",
  name: "MyVault",
  type: "Obsidian",
  recursive: true
}

const addSourceResponseExample = {
  name: "MyVault",
  path: "/path/to/obsidian/vault",
  type: "Obsidian",
  enabled: true,
  fileCount: 0,
  chunkCount: 0
}

const sourcesResponseExample = [
  {
    name: "MyVault",
    path: "/path/to/obsidian/vault",
    type: "Obsidian",
    enabled: true,
    fileCount: 156,
    chunkCount: 1243,
    lastIndexed: "2026-04-09T10:00:00Z"
  }
]

const indexResponseExample = {
  id: "idx-001",
  status: "Running",
  totalFiles: 156,
  processedFiles: 0,
  progressPercent: 0
}

const modelsResponseExample = [
  {
    name: "bge-small-zh-v1.5",
    displayName: "BGE Small Chinese v1.5",
    description: "BAAI通用中文向量模型",
    dimension: 512,
    maxSequenceLength: 512,
    isDownloaded: true,
    benchmarkScore: 0.85
  }
]

const currentModelResponseExample = {
  name: "bge-small-zh-v1.5",
  displayName: "BGE Small Chinese v1.5",
  dimension: 512,
  maxSequenceLength: 512,
  isDownloaded: true
}

const switchModelResponseExample = {
  message: "已切换到模型: BGE Small Chinese v1.5",
  model: {
    name: "bge-small-zh-v1.5",
    displayName: "BGE Small Chinese v1.5"
  }
}

const downloadModelResponseExample = {
  message: "开始下载模型",
  progress: {
    modelName: "bge-m3",
    status: "downloading",
    progress: 0
  }
}

const downloadProgressResponseExample = {
  modelName: "bge-m3",
  status: "downloading",
  progress: 45.5,
  bytesReceived: 455000000,
  totalBytes: 1000000000,
  speedBytesPerSecond: 5242880,
  estimatedSecondsRemaining: 104
}

const systemStatusResponseExample = {
  status: "healthy",
  system: {
    cpuUsagePercent: 15.2,
    memoryUsedBytes: 2147483648,
    memoryTotalBytes: 17179869184,
    uptimeSeconds: 86400
  },
  index: {
    totalFiles: 156,
    totalChunks: 1243,
    totalVectors: 1243
  },
  activeAlerts: []
}

const healthResponseExample = {
  status: "healthy",
  timestamp: "2026-04-09T10:00:00Z",
  version: "1.0.0"
}

const metricsResponseExample = {
  totalQueries: 1523,
  avgQueryLatencyMs: 42.5,
  p95QueryLatencyMs: 85.0,
  p99QueryLatencyMs: 120.0,
  avgResultsPerQuery: 8.3
}

const alertsResponseExample: unknown[] = []

const startIndexExample = {
  sources: ["MyVault"],
  force: false
}

const startIndexResponseExample = {
  id: "idx-001",
  status: "Pending",
  totalFiles: 0,
  processedFiles: 0,
  progressPercent: 0
}

const activeIndexResponseExample = [
  {
    id: "idx-001",
    status: "Running",
    totalFiles: 156,
    processedFiles: 78,
    progressPercent: 50,
    currentFile: "docs/setup.md"
  }
]

const indexStatusResponseExample = {
  id: "idx-001",
  status: "Completed",
  totalFiles: 156,
  processedFiles: 156,
  progressPercent: 100,
  duration: "00:02:35"
}

// API Endpoint Component
const ApiEndpoint = defineComponent({
  name: 'ApiEndpoint',
  props: {
    title: String,
    method: String,
    path: String,
    description: String,
    requestBody: Object,
    responseExample: Object
  },
  setup(props) {
    const copied = ref(false)
    
    const getMethodColor = (method: string) => {
      switch (method) {
        case 'GET': return '#18a058'
        case 'POST': return '#2080f0'
        case 'PUT': return '#f0a020'
        case 'DELETE': return '#d03050'
        default: return '#909399'
      }
    }
    
    const generateCurl = () => {
      const baseUrl = window.location.origin
      let curl = `curl -X ${props.method} "${baseUrl}${props.path}"`
      
      if (props.requestBody && ['POST', 'PUT', 'DELETE'].includes(props.method || '')) {
        curl += ` \\\n  -H "Content-Type: application/json" \\\n  -d '${JSON.stringify(props.requestBody, null, 2)}'`
      }
      
      return curl
    }
    
    const copyCurl = async () => {
      try {
        await navigator.clipboard.writeText(generateCurl())
        copied.value = true
        setTimeout(() => { copied.value = false }, 2000)
      } catch {
        // fallback
      }
    }
    
    const copyResponse = async () => {
      try {
        await navigator.clipboard.writeText(JSON.stringify(props.responseExample, null, 2))
      } catch {
        // fallback
      }
    }
    
    return () => h('div', { style: { marginBottom: '24px', padding: '16px', background: 'var(--n-color)', borderRadius: '8px' } }, [
      // Header
      h(NSpace, { align: 'center', justify: 'space-between', style: { marginBottom: '12px' } }, {
        default: () => [
          h(NSpace, { align: 'center' }, {
            default: () => [
              h(NTag, { 
                type: 'default' as const, 
                size: 'small',
                style: { background: getMethodColor(props.method || 'GET'), color: '#fff', fontWeight: 'bold' }
              }, { default: () => props.method }),
              h(NText, { strong: true }, { default: () => props.title }),
              h(NText, { depth: 3, code: true }, { default: () => props.path })
            ]
          }),
          h(NButton, { 
            size: 'small', 
            onClick: copyCurl,
            type: copied.value ? 'success' : 'default'
          }, {
            default: () => copied.value ? '已复制' : '复制 cURL',
            icon: () => h(NIcon, null, { default: () => h(copied.value ? CheckmarkOutline : CopyOutline) })
          })
        ]
      }),
      
      // Description
      props.description ? h(NText, { depth: 3, style: { display: 'block', marginBottom: '12px' } }, { 
        default: () => props.description 
      }) : null,
      
      // Request Body
      props.requestBody ? h('div', { style: { marginBottom: '12px' } }, [
        h(NText, { depth: 3, style: { display: 'block', marginBottom: '8px' } }, { default: () => '请求体:' }),
        h(NCode, { language: 'json', code: JSON.stringify(props.requestBody, null, 2), wordWrap: true })
      ]) : null,
      
      // Response Example
      props.responseExample ? h('div', [
        h(NSpace, { align: 'center', justify: 'space-between', style: { marginBottom: '8px' } }, {
          default: () => [
            h(NText, { depth: 3 }, { default: () => '响应示例:' }),
            h(NButton, { size: 'tiny', text: true, onClick: copyResponse }, { 
              default: () => '复制' 
            })
          ]
        }),
        h(NCode, { language: 'json', code: JSON.stringify(props.responseExample, null, 2), wordWrap: true })
      ]) : null
    ])
  }
})

// API Tester
const testMethod = ref('GET')
const testPath = ref('')
const testBody = ref('')
const testLoading = ref(false)
const testResponse = ref('')
const testResponseStatus = ref(0)

const methodOptions = [
  { label: 'GET', value: 'GET' },
  { label: 'POST', value: 'POST' },
  { label: 'PUT', value: 'PUT' },
  { label: 'DELETE', value: 'DELETE' }
]

const runApiTest = async () => {
  if (!testPath.value) return
  testLoading.value = true
  testResponse.value = ''
  try {
    const config: Record<string, unknown> = {}
    if (['POST', 'PUT', 'DELETE'].includes(testMethod.value)) {
      config.data = testBody.value ? JSON.parse(testBody.value) : {}
    }
    const response = await axios.request({
      method: testMethod.value.toLowerCase(),
      url: testPath.value,
      ...config
    })
    testResponseStatus.value = response.status
    testResponse.value = JSON.stringify(response.data, null, 2)
  } catch (error: unknown) {
    const err = error as { response?: { status: number; data: unknown } }
    testResponseStatus.value = err.response?.status || 0
    testResponse.value = err.response?.data
      ? JSON.stringify(err.response.data, null, 2)
      : String(error)
  } finally {
    testLoading.value = false
  }
}

const copyTestCurl = async () => {
  const baseUrl = window.location.origin
  let curl = `curl -X ${testMethod.value} "${baseUrl}${testPath.value}"`
  
  if (testBody.value && ['POST', 'PUT', 'DELETE'].includes(testMethod.value)) {
    curl += ` \\\n  -H "Content-Type: application/json" \\\n  -d '${testBody.value}'`
  }
  
  try {
    await navigator.clipboard.writeText(curl)
    message.success('cURL 已复制到剪贴板')
  } catch {
    message.error('复制失败')
  }
}
</script>
