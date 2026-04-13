import axios from 'axios'
import type {
  AddSourceRequest,
  SourceDetail,
  AIQueryRequest,
  AIQueryResponse,
  DrilldownRequest,
  DrilldownResponse,
  DashboardStats,
  ReferenceRAGConfig,
  IndexRequest,
  IndexJob,
  BenchmarkRequest,
  BenchmarkResult,
  QuickTestResult,
  BatchOptimizationRequest,
  BatchOptimizationResult,
  MemoryTestResult,
  ShortTextTestRequest,
  SemanticTestResult,
  LongTextTestRequest,
  LongTextTestResult,
  ModelInfo,
  ModelDownloadOptions,
  VectorStats,
  SystemStatus,
  SystemMetrics,
  IndexMetrics,
  MetricsSummary,
  Alert,
  AlertRule,
  VectorModelIndex,
  DeleteResult,
  BulkDeleteResult,
  CleanupResult,
  RebuildJob,
  RebuildRequest,
  MigrateResult,
  IndexSummary,
  IndexJobRequest,
  IndexJobResponse,
  PathsResponse
} from '@/types/api'

// PascalCase ↔ camelCase conversion utilities
// Backend uses PropertyNamingPolicy = null (PascalCase), frontend uses camelCase

const toCamel = (str: string) =>
  str.charAt(0).toLowerCase() + str.slice(1)

const toPascal = (str: string) =>
  str.charAt(0).toUpperCase() + str.slice(1)

function transformKeysDeep(obj: unknown, keyFn: (key: string) => string): unknown {
  if (obj === null || obj === undefined) return obj
  if (typeof obj === 'string' || typeof obj === 'number' || typeof obj === 'boolean') return obj
  if (Array.isArray(obj)) return obj.map((item) => transformKeysDeep(item, keyFn))
  if (obj instanceof Date) return obj
  const result: Record<string, unknown> = {}
  for (const [k, v] of Object.entries(obj as Record<string, unknown>)) {
    result[keyFn(k)] = transformKeysDeep(v, keyFn)
  }
  return result
}

const api = axios.create({
  baseURL: '/api',
  timeout: 60000,
  headers: { 'Content-Type': 'application/json' }
})

// Transform outgoing request body: camelCase → PascalCase
api.interceptors.request.use((config) => {
  if (config.data && typeof config.data === 'object') {
    config.data = transformKeysDeep(config.data, toPascal)
  }
  return config
})

// Transform incoming response data: PascalCase → camelCase
api.interceptors.response.use(
  (response) => {
    if (response.data && typeof response.data === 'object') {
      response.data = transformKeysDeep(response.data, toCamel)
    }
    return response
  },
  (error) => {
    console.error('API Error:', error.response?.data || error.message)
    // 401 未授权，跳转登录页
    if (error.response?.status === 401) {
      // 清除本地存储的 API Key
      localStorage.removeItem('reference_rag_api_key')
      // 跳转登录页（避免重复跳转）
      if (window.location.pathname !== '/login') {
        window.location.href = '/login'
      }
    }
    return Promise.reject(error)
  }
)

// AI Query
export const aiQueryApi = {
  query: (data: AIQueryRequest) => api.post<AIQueryResponse>('/ai/query', data),
  drilldown: (data: DrilldownRequest) => api.post<DrilldownResponse>('/ai/drill-down', data)
}

// Dashboard
export const dashboardApi = {
  getStats: () => api.get<DashboardStats>('/Dashboard/stats'),
  getSources: () => api.get<SourceDetail[]>('/Dashboard/sources')
}

// Sources
export const sourcesApi = {
  getAll: () => api.get<SourceDetail[]>('/Sources'),
  getByName: (name: string) => api.get<SourceDetail>(`/Sources/${name}`),
  create: (data: AddSourceRequest) => api.post('/Sources', data),
  update: (name: string, data: { name?: string; enabled?: boolean; recursive?: boolean; filePatterns?: string[] }) =>
    api.put(`/Sources/${name}`, data),
  delete: (name: string, deleteData = false) =>
    api.delete(`/Sources/${name}?deleteData=${deleteData}`),
  toggle: (name: string, enabled: boolean) =>
    api.patch(`/Sources/${name}/toggle`, { enabled }),
  startIndex: (name: string, force = false) =>
    api.post(`/Sources/${name}/index`, { force }),
  scan: (name: string) => api.get(`/Sources/${name}/scan`),
  getFiles: (name: string) => api.get(`/Sources/${name}/files`)
}

// Index
export const indexApi = {
  start: (data?: IndexRequest) => api.post<IndexJob>('/Index/start', data || {}),
  getStatus: (indexId: string) => api.get<IndexJob>(`/Index/${indexId}/status`),
  getActive: () => api.get<IndexJob[]>('/Index/active'),
  stop: (indexId: string) => api.post(`/Index/${indexId}/stop`)
}

// Settings
export interface CudaAvailability {
  isAvailable: boolean
  message: string
}

export const settingsApi = {
  get: () => api.get<ReferenceRAGConfig>('/Settings'),
  save: (config: ReferenceRAGConfig) => api.post('/Settings', config),
  updateModelsPath: (modelsPath: string, migrateExisting = false) =>
    api.patch('/Settings/models-path', { modelsPath, migrateExisting }),
  getCudaAvailability: () => api.get<CudaAvailability>('/Settings/cuda-availability')
}

// Performance
export const performanceApi = {
  benchmark: (data?: BenchmarkRequest) => api.post<BenchmarkResult>('/Performance/benchmark', data || {}),
  quickTest: (textLength = 10000) => api.get<QuickTestResult>(`/Performance/quick-test?textLength=${textLength}`),
  batchSizes: (data?: BatchOptimizationRequest) => api.post<BatchOptimizationResult>('/Performance/batch-sizes', data || {}),
  memoryTest: (vectorCount = 1000, dimension = 512) =>
    api.get<MemoryTestResult>(`/Performance/memory-test?vectorCount=${vectorCount}&dimension=${dimension}`)
}

// Semantic Test
export const semanticTestApi = {
  shortText: (data: ShortTextTestRequest) => api.post<SemanticTestResult>('/SemanticTest/short-text', data),
  longText: (data: LongTextTestRequest) => api.post<LongTextTestResult>('/SemanticTest/long-text', data),
  getRecords: () => api.get<SemanticTestResult[]>('/SemanticTest/records'),
  clearRecords: () => api.delete('/SemanticTest/records'),
  getPresets: () => api.get('/SemanticTest/presets'),
  runPreset: (suiteName: string) => api.post(`/SemanticTest/preset/${suiteName}`),
  getStatistics: () => api.get('/SemanticTest/statistics')
}

// Models
export const modelsApi = {
  getAll: () => api.get<ModelInfo[]>('/Models'),
  getCurrent: () => api.get<ModelInfo>('/Models/current'),
  switch: (modelName: string, deleteOldVectors = false) => api.post(`/Models/switch`, { modelName, deleteOldVectors }),
  download: (modelName: string, onnxFilePath?: string) =>
    api.post(`/Models/download/${modelName}`, onnxFilePath ? { onnxFilePath } : {}),
  getDownloadProgress: (modelName: string) => api.get(`/Models/download/${modelName}/progress`),
  convert: (modelName: string, targetFormat: 'embedded' | 'external') =>
    api.post(`/Models/${modelName}/convert`, { targetFormat }),
  getConvertProgress: (modelName: string) => api.get(`/Models/${modelName}/convert/progress`),
  addCustom: (huggingFaceId: string, displayName?: string) =>
    api.post('/Models/custom', { huggingFaceId, displayName }),
  delete: (modelName: string) => api.delete(`/Models/${modelName}`),
  getDownloadOptions: (modelName: string) => api.get<ModelDownloadOptions>(`/Models/download-options/${modelName}`),

  // Rerank Models
  getRerankModels: () => api.get<ModelInfo[]>('/Models/rerank'),
  getDownloadedRerankModels: () => api.get<ModelInfo[]>('/Models/rerank/downloaded'),
  getCurrentRerankModel: () => api.get<ModelInfo>('/Models/rerank/current'),
  switchRerankModel: (modelName: string) => api.post(`/Models/rerank/switch`, { modelName }),
  downloadRerankModel: (modelName: string, onnxFilePath?: string) =>
    api.post(`/Models/rerank/download/${modelName}`, onnxFilePath ? { onnxFilePath } : {}),
  getRerankDownloadProgress: (modelName: string) => api.get(`/Models/rerank/download/${modelName}/progress`),
  deleteRerankModel: (modelName: string) => api.delete(`/Models/rerank/${modelName}`),
  getRerankDownloadOptions: (modelName: string) => api.get<ModelDownloadOptions>(`/Models/rerank/download-options/${modelName}`)
}

// Vectors
export const vectorsApi = {
  getStats: () => api.get<VectorStats[]>('/Vectors/stats'),
  getStatsByModel: (modelName: string) => api.get<VectorStats>(`/Vectors/stats/${modelName}`),
  deleteByModel: (modelName: string) => api.delete(`/Vectors/model/${modelName}`),
  deleteOrphaned: () => api.delete('/Vectors/orphaned')
}

// Vector Index
export const vectorIndexApi = {
  // 索引任务管理
  startIndex: (request?: IndexJobRequest) => api.post<IndexJobResponse>('/VectorIndex/index', request || {}),
  getJobs: () => api.get<IndexJobResponse[]>('/VectorIndex/jobs'),
  getAllJobs: () => api.get<import('@/types/api').AllJobsResponse>('/VectorIndex/jobs/all'),
  getJob: (jobId: string) => api.get<IndexJobResponse>(`/VectorIndex/jobs/${jobId}`),
  stopJob: (jobId: string) => api.post(`/VectorIndex/jobs/${jobId}/stop`),
  getCompletedJobs: () => api.get<IndexJobResponse[]>('/VectorIndex/jobs/history'),
  clearCompletedJobs: () => api.delete('/VectorIndex/jobs/history'),

  // 向量索引重建
  rebuild: (request?: RebuildRequest) => api.post<RebuildJob>('/VectorIndex/rebuild', request || {}),
  rebuildSource: (sourceName: string) => api.post<RebuildJob>(`/VectorIndex/rebuild/${sourceName}`),

  // 向量状态查询
  getModels: () => api.get<VectorModelIndex[]>('/VectorIndex/models'),
  getCurrent: () => api.get<VectorModelIndex>('/VectorIndex/current'),
  getSummary: () => api.get<IndexSummary>('/VectorIndex/summary'),

  // 向量索引删除
  deleteByModel: (modelName: string) => api.delete<DeleteResult>(`/VectorIndex/models/${modelName}`),
  deleteAll: () => api.delete<BulkDeleteResult>('/VectorIndex/all'),
  cleanup: () => api.post<CleanupResult>('/VectorIndex/cleanup'),

  // 数据迁移
  migrate: () => api.post<MigrateResult>('/VectorIndex/migrate')
}

// System
export const systemApi = {
  getStatus: () => api.get<SystemStatus>('/system/status'),
  getHealth: () => api.get('/system/health'),
  getMetrics: () => api.get<SystemMetrics>('/system/metrics'),
  getIndexMetrics: () => api.get<IndexMetrics>('/system/metrics/index'),
  getMetricsSummary: () => api.get<MetricsSummary>('/system/metrics/queries'),
  getAlerts: () => api.get<Alert[]>('/system/alerts'),
  checkAlerts: () => api.post<Alert[]>('/system/alerts/check'),
  getAlertRules: () => api.get<AlertRule[]>('/system/alerts/rules'),
  restart: () => api.post<import('@/types/api').RestartResponse>('/system/restart')
}

// BM25 Index
export const bm25IndexApi = {
  getModels: () => api.get<BM25Model[]>('/bm25index/models'),
  createModel: (data: { name: string; description?: string }) => api.post('/bm25index/models', data),
  deleteModel: (name: string) => api.delete(`/bm25index/models/${name}`),
  enableModel: (name: string) => api.post(`/bm25index/models/${name}/enable`),
  disableModel: (name: string) => api.post(`/bm25index/models/${name}/disable`),
  rebuildIndex: (name: string) => api.post(`/bm25index/models/${name}/rebuild`),
  incrementalIndex: (name: string, chunks: string[]) => api.post(`/bm25index/models/${name}/index`, chunks),
  clearModel: (name: string) => api.delete(`/bm25index/models/${name}/index`),
  search: (name: string, query: string, topK?: number) =>
    api.get<BM25SearchResult>(`/bm25index/models/${name}/search`, { params: { query, topK } }),
  // Provider 管理
  getProvider: () => api.get<BM25ProviderInfo>('/bm25index/provider'),
  setProvider: (provider: string) => api.post('/bm25index/provider', { provider })
}

// BM25 Types
export interface BM25Model {
  name: string
  averageDocLength: number
  totalDocuments: number
  vocabularySize: number
  isEnabled: boolean
  createdAt: string
  message?: string
}

export interface BM25SearchResult {
  modelName: string
  query: string
  totalResults: number
  durationMs: number
  results: {
    chunkId: string
    content: string
    score: number
    rank: number
  }[]
}

export interface BM25Config {
  k1: number
  b: number
}

export interface BM25ProviderInfo {
  configuredProvider: string
  activeProvider: string
  isMatch: boolean
  description: string
}

// Rerank Test
export const rerankTestApi = {
  test: (data: import('@/types/api').RerankTestRequest) => api.post<import('@/types/api').RerankTestResult>('/RerankTest/test', data),
  getRecords: (params?: { limit?: number; offset?: number }) => api.get('/RerankTest/records', { params }),
  getPresets: () => api.get<import('@/types/api').RerankPresetInfo[]>('/RerankTest/presets'),
  runPreset: (suiteName: string) => api.post<import('@/types/api').RerankTestResult>(`/RerankTest/preset/${suiteName}`),
  clearRecords: (params?: { before?: string }) => api.delete('/RerankTest/records', { params }),
  getStatistics: (params?: { modelName?: string }) => api.get<import('@/types/api').RerankTestStatistics>('/RerankTest/statistics', { params }),
  benchmark: (data: import('@/types/api').RerankBenchmarkRequest) => api.post<import('@/types/api').RerankBenchmarkResult>('/RerankTest/benchmark', data)
}

// Paths
export const pathsApi = {
  getPaths: () => api.get<PathsResponse>('/paths')
}

export default api
