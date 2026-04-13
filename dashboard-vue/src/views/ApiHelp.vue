<template>
  <n-space vertical :size="20">
    <!-- API Key 配置 -->
    <n-card title="API 认证配置" size="small">
      <n-space align="center">
        <n-input
          v-model:value="apiKey"
          type="password"
          placeholder="输入 API Key (如果服务启用了认证)"
          style="width: 400px"
          @change="saveApiKey"
        />
        <n-switch v-model:value="authEnabled" @update:value="toggleAuth">
          <template #checked>已启用</template>
          <template #unchecked>未启用</template>
        </n-switch>
        <n-button @click="testAuth">测试认证</n-button>
      </n-space>
    </n-card>

    <!-- API 文档 -->
    <n-card title="API 文档">
      <template #header-extra>
        <n-space>
          <n-button :loading="loading" @click="loadSwagger">
            刷新
          </n-button>
        </n-space>
      </template>

      <n-spin :show="loading">
        <n-tabs v-if="swaggerData" type="line" animated>
          <!-- 按 Tag 分组显示 -->
          <n-tab-pane
            v-for="tag in apiTags"
            :key="tag"
            :name="tag"
            :tab="getTagDisplayName(tag)"
          >
            <n-space vertical :size="16">
              <ApiEndpointCard
                v-for="(endpoint, index) in getEndpointsByTag(tag)"
                :key="index"
                :endpoint="endpoint"
                :schemas="swaggerData.components?.schemas || {}"
                :api-key="authEnabled ? apiKey : ''"
                @test="handleTestEndpoint"
              />
            </n-space>
          </n-tab-pane>
        </n-tabs>
        <n-empty v-else-if="!loading" description="加载 API 文档失败">
          <template #extra>
            <n-button @click="loadSwagger">重试</n-button>
          </template>
        </n-empty>
      </n-spin>
    </n-card>

    <!-- API 测试工具 -->
    <n-card title="API 测试工具">
      <n-space vertical>
        <n-form label-placement="left" label-width="80">
          <n-grid :cols="24" :x-gap="12">
            <n-grid-item :span="4">
              <n-form-item label="方法">
                <n-select
                  v-model:value="testMethod"
                  :options="methodOptions"
                />
              </n-form-item>
            </n-grid-item>
            <n-grid-item :span="20">
              <n-form-item label="路径">
                <n-input v-model:value="testPath" placeholder="/api/..." />
              </n-form-item>
            </n-grid-item>
          </n-grid>
          <n-form-item label="请求体">
            <n-input
              v-model:value="testBody"
              type="textarea"
              placeholder="JSON 格式请求体（可选）"
              :rows="8"
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
          <n-button @click="clearTestResult">
            清空结果
          </n-button>
        </n-space>

        <n-card v-if="testResponse" title="响应结果" size="small">
          <template #header-extra>
            <n-space>
              <n-tag :type="testResponseStatus >= 200 && testResponseStatus < 300 ? 'success' : 'error'" size="small">
                {{ testResponseStatus }}
              </n-tag>
              <n-text depth="3">{{ testDuration }}ms</n-text>
            </n-space>
          </template>
          <n-code :code="testResponse" language="json" word-wrap />
        </n-card>
      </n-space>
    </n-card>
  </n-space>
</template>

<script setup lang="ts">
import { ref, computed, onMounted, h, defineComponent, watch } from 'vue'
import {
  NCard, NTabs, NTabPane, NSpace, NButton, NCode, NTag, NText, NIcon,
  NInput, NSelect, NSwitch, NSpin, NEmpty, NForm, NFormItem, NGrid, NGridItem,
  NDescriptions, NDescriptionsItem, NCollapse, NCollapseItem, useMessage
} from 'naive-ui'
import { CopyOutline, CheckmarkOutline, PlayOutline } from '@vicons/ionicons5'
import axios from 'axios'

const message = useMessage()

// API Key 配置
const apiKey = ref(localStorage.getItem('rag_api_key') || '')
const authEnabled = ref(localStorage.getItem('rag_auth_enabled') === 'true')

const saveApiKey = () => {
  localStorage.setItem('rag_api_key', apiKey.value)
}

const toggleAuth = (value: boolean) => {
  localStorage.setItem('rag_auth_enabled', String(value))
  if (value && apiKey.value) {
    axios.defaults.headers.common['X-API-Key'] = apiKey.value
  } else {
    delete axios.defaults.headers.common['X-API-Key']
  }
}

const testAuth = async () => {
  try {
    const config = authEnabled.value && apiKey.value
      ? { headers: { 'X-API-Key': apiKey.value } }
      : {}
    const response = await axios.get('/api/system/status', config)
    if (response.status === 200) {
      message.success('认证成功')
    }
  } catch (error: unknown) {
    const err = error as { response?: { status: number } }
    if (err.response?.status === 401) {
      message.error('认证失败: API Key 无效')
    } else {
      message.error('连接失败')
    }
  }
}

// Swagger 数据
interface SwaggerInfo {
  title: string
  description: string
  version: string
}

interface SwaggerPath {
  get?: SwaggerOperation
  post?: SwaggerOperation
  put?: SwaggerOperation
  delete?: SwaggerOperation
  patch?: SwaggerOperation
}

interface SwaggerOperation {
  tags?: string[]
  summary?: string
  description?: string
  operationId?: string
  parameters?: SwaggerParameter[]
  requestBody?: {
    content?: Record<string, { schema?: { $ref?: string; type?: string; items?: { $ref?: string } } }>
    description?: string
    required?: boolean
  }
  responses?: Record<string, { description?: string; content?: Record<string, { schema?: { $ref?: string } }> }>
}

interface SwaggerParameter {
  name: string
  in: string
  required?: boolean
  description?: string
  schema?: { type?: string; format?: string; default?: unknown }
}

interface SwaggerData {
  openapi: string
  info: SwaggerInfo
  paths: Record<string, SwaggerPath>
  components?: {
    schemas?: Record<string, unknown>
  }
}

interface ApiEndpoint {
  path: string
  method: string
  operation: SwaggerOperation
}

const swaggerData = ref<SwaggerData | null>(null)
const loading = ref(false)

const apiTags = computed(() => {
  if (!swaggerData.value?.paths) return []
  const tags = new Set<string>()
  Object.values(swaggerData.value.paths).forEach(path => {
    ['get', 'post', 'put', 'delete', 'patch'].forEach(method => {
      const op = path[method as keyof SwaggerPath] as SwaggerOperation | undefined
      op?.tags?.forEach(tag => tags.add(tag))
    })
  })
  return Array.from(tags).sort()
})

const getEndpointsByTag = (tag: string): ApiEndpoint[] => {
  if (!swaggerData.value?.paths) return []
  const endpoints: ApiEndpoint[] = []
  Object.entries(swaggerData.value.paths).forEach(([path, methods]) => {
    ['get', 'post', 'put', 'delete', 'patch'].forEach(method => {
      const op = methods[method as keyof SwaggerPath] as SwaggerOperation | undefined
      if (op?.tags?.includes(tag)) {
        endpoints.push({ path, method, operation: op })
      }
    })
  })
  return endpoints
}

const tagDisplayNames: Record<string, string> = {
  AIQuery: 'AI 查询',
  BM25Index: 'BM25 索引',
  Dashboard: '仪表盘',
  Models: '模型管理',
  Performance: '性能测试',
  Settings: '设置',
  Sources: '数据源',
  System: '系统',
  VectorIndex: '向量索引'
}

const getTagDisplayName = (tag: string) => tagDisplayNames[tag] || tag

const loadSwagger = async () => {
  loading.value = true
  try {
    const response = await axios.get<SwaggerData>('/swagger/v1/swagger.json')
    swaggerData.value = response.data
  } catch (error) {
    message.error('加载 API 文档失败')
    console.error(error)
  } finally {
    loading.value = false
  }
}

// API Endpoint Card 组件
const ApiEndpointCard = defineComponent({
  name: 'ApiEndpointCard',
  props: {
    endpoint: { type: Object as () => ApiEndpoint, required: true },
    schemas: { type: Object as () => Record<string, unknown>, required: true },
    apiKey: { type: String, default: '' }
  },
  setup(props) {
    const expanded = ref(false)
    const copied = ref(false)
    const testLoading = ref(false)
    const testResult = ref<string | null>(null)
    const testStatus = ref(0)

    const getMethodColor = (method: string) => {
      switch (method.toUpperCase()) {
        case 'GET': return '#18a058'
        case 'POST': return '#2080f0'
        case 'PUT': return '#f0a020'
        case 'DELETE': return '#d03050'
        case 'PATCH': return '#909399'
        default: return '#909399'
      }
    }

    const resolveSchema = (ref: string | undefined): unknown => {
      if (!ref) return null
      const schemaName = ref.replace('#/components/schemas/', '')
      return props.schemas[schemaName]
    }

    const getSchemaExample = (schema: unknown): unknown => {
      if (!schema) return null
      const s = schema as Record<string, unknown>
      if (s.type === 'object' && s.properties) {
        const props = s.properties as Record<string, unknown>
        const result: Record<string, unknown> = {}
        Object.entries(props).forEach(([key, value]) => {
          const prop = value as Record<string, unknown>
          if (prop.type === 'string') {
            if (prop.format === 'date-time') result[key] = new Date().toISOString()
            else if (prop.enum) result[key] = (prop.enum as string[])[0]
            else result[key] = ''
          } else if (prop.type === 'integer' || prop.type === 'number') {
            result[key] = prop.default ?? 0
          } else if (prop.type === 'boolean') {
            result[key] = prop.default ?? false
          } else if (prop.type === 'array') {
            result[key] = []
          } else if (prop.$ref) {
            result[key] = getSchemaExample(resolveSchema(prop.$ref as string))
          }
        })
        return result
      }
      return null
    }

    const getRequestBodyExample = () => {
      const content = props.endpoint.operation.requestBody?.content
      if (!content) return null
      const jsonContent = content['application/json']
      if (jsonContent?.schema?.$ref) {
        return getSchemaExample(resolveSchema(jsonContent.schema.$ref))
      }
      return null
    }

    const generateCurl = () => {
      const baseUrl = window.location.origin
      let curl = `curl -X ${props.endpoint.method.toUpperCase()} "${baseUrl}${props.endpoint.path}"`

      if (props.apiKey) {
        curl += ` \\\n  -H "X-API-Key: ${props.apiKey}"`
      }

      const body = getRequestBodyExample()
      if (body && ['POST', 'PUT', 'DELETE', 'PATCH'].includes(props.endpoint.method.toUpperCase())) {
        curl += ` \\\n  -H "Content-Type: application/json" \\\n  -d '${JSON.stringify(body, null, 2)}'`
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

    const testDuration = ref(0)

    const testEndpoint = async () => {
      testLoading.value = true
      testResult.value = null
      const startTime = Date.now()

      try {
        const config: Record<string, unknown> = {
          method: props.endpoint.method.toLowerCase(),
          url: props.endpoint.path
        }

        if (props.apiKey) {
          config.headers = { 'X-API-Key': props.apiKey }
        }

        const body = getRequestBodyExample()
        if (body && ['post', 'put', 'delete', 'patch'].includes(config.method as string)) {
          config.data = body
        }

        const response = await axios.request(config as Parameters<typeof axios.request>[0])
        testStatus.value = response.status
        testResult.value = JSON.stringify(response.data, null, 2)
        testDuration.value = Date.now() - startTime
      } catch (error: unknown) {
        const err = error as { response?: { status: number; data: unknown } }
        testStatus.value = err.response?.status || 0
        testResult.value = err.response?.data
          ? JSON.stringify(err.response.data, null, 2)
          : String(error)
        testDuration.value = Date.now() - startTime
      } finally {
        testLoading.value = false
      }
    }

    return () => h('div', {
      style: {
        padding: '16px',
        background: 'var(--n-color)',
        borderRadius: '8px',
        border: '1px solid var(--n-border-color)'
      }
    }, [
      // Header
      h(NSpace, { align: 'center', justify: 'space-between', style: { marginBottom: '12px' } }, {
        default: () => [
          h(NSpace, { align: 'center' }, {
            default: () => [
              h(NTag, {
                size: 'small',
                style: { background: getMethodColor(props.endpoint.method), color: '#fff', fontWeight: 'bold', minWidth: '60px', textAlign: 'center' }
              }, { default: () => props.endpoint.method.toUpperCase() }),
              h(NText, { code: true, style: { fontSize: '14px' } }, { default: () => props.endpoint.path }),
              props.endpoint.operation.summary && h(NText, { depth: 3, style: { marginLeft: '8px' } }, {
                default: () => props.endpoint.operation.summary
              })
            ]
          }),
          h(NSpace, { align: 'center' }, {
            default: () => [
              h(NButton, {
                size: 'small',
                onClick: () => { expanded.value = !expanded.value },
                text: true
              }, { default: () => expanded.value ? '收起' : '展开' }),
              h(NButton, {
                size: 'small',
                onClick: copyCurl,
                type: copied.value ? 'success' : 'default'
              }, {
                default: () => copied.value ? '已复制' : '复制 cURL',
                icon: () => h(NIcon, null, { default: () => h(copied.value ? CheckmarkOutline : CopyOutline) })
              }),
              h(NButton, {
                size: 'small',
                type: 'primary',
                loading: testLoading.value,
                onClick: testEndpoint
              }, {
                default: () => '测试',
                icon: () => h(NIcon, null, { default: () => h(PlayOutline) })
              })
            ]
          })
        ]
      }),

      // Expanded content
      expanded.value && h('div', { style: { marginTop: '16px' } }, [
        // Parameters
        props.endpoint.operation.parameters && props.endpoint.operation.parameters.length > 0 && h('div', { style: { marginBottom: '16px' } }, [
          h(NText, { strong: true, style: { display: 'block', marginBottom: '8px' } }, { default: () => '参数:' }),
          h(NDescriptions, { labelPlacement: 'left', bordered: true, size: 'small', column: 1 }, {
            default: () => props.endpoint.operation.parameters?.map(param => {
              const labelParts: Array<string | ReturnType<typeof h>> = []
              if (param.required) {
                labelParts.push('[必填] ')
              }
              labelParts.push(param.name)
              labelParts.push(` (${param.in})`)
              if (param.schema?.type) {
                labelParts.push(` (${param.schema.type})`)
              }
              return h(NDescriptionsItem, {
                label: labelParts.join('')
              }, {
                default: () => param.description || '-'
              })
            })
          })
        ]),

        // Request Body
        props.endpoint.operation.requestBody && h('div', { style: { marginBottom: '16px' } }, [
          h(NSpace, { align: 'center', style: { marginBottom: '8px' } }, {
            default: () => [
              h(NText, { strong: true }, { default: () => '请求体:' }),
              props.endpoint.operation.requestBody?.required && h(NTag, { type: 'error', size: 'tiny' }, { default: () => '必填' }),
              props.endpoint.operation.requestBody?.description && h(NText, { depth: 3 }, {
                default: () => props.endpoint.operation.requestBody?.description
              })
            ]
          }),
          h(NCode, {
            language: 'json',
            code: JSON.stringify(getRequestBodyExample(), null, 2),
            wordWrap: true
          })
        ]),

        // Responses
        props.endpoint.operation.responses && h('div', [
          h(NText, { strong: true, style: { display: 'block', marginBottom: '8px' } }, { default: () => '响应:' }),
          h(NCollapse, {}, {
            default: () => Object.entries(props.endpoint.operation.responses ?? {}).map(([code, response]) =>
              h(NCollapseItem, {
                name: code,
                header: h(NSpace, { align: 'center' }, {
                  default: () => [
                    h(NTag, {
                      type: code.startsWith('2') ? 'success' : code.startsWith('4') ? 'warning' : 'error',
                      size: 'small'
                    }, { default: () => code }),
                    h(NText, null, { default: () => response.description })
                  ]
                })
              }, {
                default: () => {
                  const schema = response.content?.['application/json']?.schema
                  if (schema?.$ref) {
                    const resolved = resolveSchema(schema.$ref)
                    return h(NCode, {
                      language: 'json',
                      code: JSON.stringify(getSchemaExample(resolved), null, 2),
                      wordWrap: true
                    })
                  }
                  return h(NText, { depth: 3 }, { default: () => '无响应体' })
                }
              })
            )
          })
        ]),

        // Test result
        testResult.value && h('div', { style: { marginTop: '16px' } }, [
          h(NSpace, { align: 'center', style: { marginBottom: '8px' } }, {
            default: () => [
              h(NText, { strong: true }, { default: () => '测试结果:' }),
              h(NTag, {
                type: testStatus.value >= 200 && testStatus.value < 300 ? 'success' : 'error',
                size: 'small'
              }, { default: () => testStatus.value })
            ]
          }),
          h(NCode, { language: 'json', code: testResult.value, wordWrap: true })
        ])
      ])
    ])
  }
})

// API 测试工具
const testMethod = ref('GET')
const testPath = ref('')
const testBody = ref('')
const testLoading = ref(false)
const testResponse = ref('')
const testResponseStatus = ref(0)
const testDuration = ref(0)

const methodOptions = [
  { label: 'GET', value: 'GET' },
  { label: 'POST', value: 'POST' },
  { label: 'PUT', value: 'PUT' },
  { label: 'DELETE', value: 'DELETE' },
  { label: 'PATCH', value: 'PATCH' }
]

const runApiTest = async () => {
  if (!testPath.value) {
    message.warning('请输入 API 路径')
    return
  }
  testLoading.value = true
  testResponse.value = ''
  const startTime = Date.now()

  try {
    const config: Record<string, unknown> = {
      method: testMethod.value.toLowerCase(),
      url: testPath.value
    }

    if (authEnabled.value && apiKey.value) {
      config.headers = { 'X-API-Key': apiKey.value }
    }

    if (testBody.value && ['POST', 'PUT', 'DELETE', 'PATCH'].includes(testMethod.value)) {
      config.data = JSON.parse(testBody.value)
    }

    const response = await axios.request(config as Parameters<typeof axios.request>[0])
    testResponseStatus.value = response.status
    testResponse.value = JSON.stringify(response.data, null, 2)
  } catch (error: unknown) {
    const err = error as { response?: { status: number; data: unknown } }
    testResponseStatus.value = err.response?.status || 0
    testResponse.value = err.response?.data
      ? JSON.stringify(err.response.data, null, 2)
      : String(error)
  } finally {
    testDuration.value = Date.now() - startTime
    testLoading.value = false
  }
}

const copyTestCurl = async () => {
  const baseUrl = window.location.origin
  let curl = `curl -X ${testMethod.value} "${baseUrl}${testPath.value}"`

  if (authEnabled.value && apiKey.value) {
    curl += ` \\\n  -H "X-API-Key: ${apiKey.value}"`
  }

  if (testBody.value && ['POST', 'PUT', 'DELETE', 'PATCH'].includes(testMethod.value)) {
    curl += ` \\\n  -H "Content-Type: application/json" \\\n  -d '${testBody.value}'`
  }

  try {
    await navigator.clipboard.writeText(curl)
    message.success('cURL 已复制到剪贴板')
  } catch {
    message.error('复制失败')
  }
}

const clearTestResult = () => {
  testResponse.value = ''
  testResponseStatus.value = 0
  testDuration.value = 0
}

const handleTestEndpoint = (endpoint: { path: string; method: string }) => {
  testPath.value = endpoint.path
  testMethod.value = endpoint.method.toUpperCase()
}

// 初始化
onMounted(() => {
  if (authEnabled.value && apiKey.value) {
    axios.defaults.headers.common['X-API-Key'] = apiKey.value
  }
  loadSwagger()
})

// 监听 authEnabled 和 apiKey 变化
watch([authEnabled, apiKey], ([enabled, key]) => {
  if (enabled && key) {
    axios.defaults.headers.common['X-API-Key'] = key
  } else {
    delete axios.defaults.headers.common['X-API-Key']
  }
})
</script>

<style scoped>
:deep(.n-code) {
  max-height: 400px;
  overflow: auto;
}
</style>
