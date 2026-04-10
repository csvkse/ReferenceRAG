import axios from 'axios'
import type {
  AddSourceRequest,
  SourceDetail,
  AIQueryRequest,
  AIQueryResponse,
  DrilldownRequest,
  DrilldownResponse,
  DashboardStats,
  ObsidianRagConfig,
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
  IndexJobResponse
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
export const settingsApi = {
  get: () => api.get<ObsidianRagConfig>('/Settings'),
  save: (config: ObsidianRagConfig) => api.post('/Settings', config),
  updateModelsPath: (modelsPath: string, migrateExisting = false) =>
    api.patch('/Settings/models-path', { modelsPath, migrateExisting })
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
  getDownloadOptions: (modelName: string) => api.get<ModelDownloadOptions>(`/Models/download-options/${modelName}`)
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
  getJob: (jobId: string) => api.get<IndexJobResponse>(`/VectorIndex/jobs/${jobId}`),
  stopJob: (jobId: string) => api.post(`/VectorIndex/jobs/${jobId}/stop`),

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
  getAlertRules: () => api.get<AlertRule[]>('/system/alerts/rules')
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
  clearModel: (name: string) => api.post(`/bm25index/models/${name}/clear`),
  search: (name: string, query: string, topK?: number) =>
    api.get<BM25SearchResult>(`/bm25index/models/${name}/search`, { params: { query, topK } }),
  getConfig: () => api.get<BM25Config>('/bm25index/config'),
  saveConfig: (config: BM25Config) => api.post('/bm25index/config', config)
}

// BM25 Types
export interface BM25Model {
  name: string
  k1: number
  b: number
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

export default api
