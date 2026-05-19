import axios from 'axios'
import { API_URL } from '@/config/env'
import type {
  AddSourceRequest,
  SourceDetail,
  AIQueryRequest,
  AIQueryResponse,
  DrilldownRequest,
  DrilldownResponse,
  ReferenceRAGConfig,
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
  IndexSummary,
  IndexJobRequest,
  IndexJobResponse,
  AllJobsResponse,
  PathsResponse,
  SearchPhaseReport
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
  baseURL: API_URL,
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
  drilldown: (data: DrilldownRequest) => api.post<DrilldownResponse>('/ai/drill-down', data),
  getSearchStatus: () => api.get<SearchStatusResponse>('/ai/status')
}

// Search Status
export interface SearchStatusResponse {
  embeddingModel?: string
  embeddingDimension: number
  rerankModel?: string
  rerankEnabled: boolean
  bm25IndexedDocuments: number
  bm25HasIndex: boolean
  vectorIndexedChunks: number
  vectorHasIndex: boolean
  totalFiles: number
}

// Dashboard API removed — use indexApi.getSummary() + sourcesApi.getAll()

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

// Index Jobs — /api/index/jobs
export const indexJobsApi = {
  startJob: (request?: IndexJobRequest) => api.post<IndexJobResponse>('/index/jobs', request || {}),
  getActive: () => api.get<IndexJobResponse[]>('/index/jobs'),
  getAll: () => api.get<AllJobsResponse>('/index/jobs/all'),
  getHistory: () => api.get<IndexJobResponse[]>('/index/jobs/history'),
  clearHistory: () => api.delete('/index/jobs/history'),
  getJob: (jobId: string) => api.get<IndexJobResponse>(`/index/jobs/${jobId}`),
  cancelJob: (jobId: string) => api.post(`/index/jobs/${jobId}/cancel`)
}

// Index Management — /api/index
export const indexApi = {
  getSummary: () => api.get<IndexSummary>('/index/summary'),
  getModels: () => api.get<VectorModelIndex[]>('/index/models'),
  getCurrentModel: () => api.get<VectorModelIndex>('/index/models/current'),
  deleteModel: (modelName: string) => api.delete<DeleteResult>(`/index/models/${modelName}`),
  deleteAllModels: () => api.delete<BulkDeleteResult>('/index/models'),
  deleteAllData: () => api.delete('/index/data'),
  rebuild: (request?: RebuildRequest) => api.post<RebuildJob>('/index/rebuild', request || {}),
  rebuildSource: (sourceName: string) => api.post<RebuildJob>(`/index/rebuild/${sourceName}`),
  cleanup: () => api.post<CleanupResult>('/index/cleanup')
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
    api.get<MemoryTestResult>(`/Performance/memory-test?vectorCount=${vectorCount}&dimension=${dimension}`),
  getSearchTrace: () => api.get<SearchPhaseReport>('/performance/search-trace'),
  resetSearchTrace: () => api.delete('/performance/search-trace')
}

// Semantic Test
export interface ModelProbeResult {
  modelName: string
  isSimulationMode: boolean
  dimension: number
  supportsAsymmetric: boolean
  selfSimilarity: number
  highSimilarityActual: number
  highSimilarityExpected: number
  lowSimilarityActual: number
  lowSimilarityExpected: number
  vectorSample: number[]
  healthy: boolean
  error?: string
  timestamp: string
}

export const semanticTestApi = {
  shortText: (data: ShortTextTestRequest) => api.post<SemanticTestResult>('/SemanticTest/short-text', data),
  longText: (data: LongTextTestRequest) => api.post<LongTextTestResult>('/SemanticTest/long-text', data),
  getRecords: () => api.get<SemanticTestResult[]>('/SemanticTest/records'),
  clearRecords: () => api.delete('/SemanticTest/records'),
  getPresets: () => api.get('/SemanticTest/presets'),
  runPreset: (suiteName: string) => api.post(`/SemanticTest/preset/${suiteName}`),
  getStatistics: () => api.get('/SemanticTest/statistics'),
  modelProbe: () => api.get<ModelProbeResult>('/SemanticTest/model-probe')
}

// Models
export const modelsApi = {
  getAll: () => api.get<ModelInfo[]>('/Models'),
  getCurrent: () => api.get<ModelInfo>('/Models/current'),
  switch: (modelName: string, deleteOldVectors = false) => api.post(`/Models/switch`, { modelName, deleteOldVectors }),
  download: (modelName: string, onnxFilePath?: string) =>
    api.post(`/Models/download/${modelName}`, onnxFilePath ? { onnxFilePath } : {}),
  getDownloadProgress: (modelName: string) => api.get(`/Models/download/${modelName}/progress`),
  getActiveDownloads: () => api.get<Array<{ key: string; progress: import('@/types/api').DownloadProgress }>>('/Models/downloads/active'),
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
  getRerankDownloadOptions: (modelName: string) => api.get<ModelDownloadOptions>(`/Models/rerank/download-options/${modelName}`),

  // 扫描模型目录
  scanModels: () => api.post('/Models/scan')
}

// vectorsApi / vectorIndexApi removed — use indexJobsApi + indexApi

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
  // 索引操作
  indexAll: () => api.post<BM25IndexProgress>('/bm25index/index'),
  indexDocument: (chunkId: string, content: string) => api.post(`/bm25index/documents/${chunkId}`, { content }),
  clearIndex: () => api.delete('/bm25index/index'),

  // 搜索
  search: (query: string, topK?: number) =>
    api.get<BM25SearchResult>('/bm25index/search', { params: { query, topK } }),

  // 统计
  getSummary: () => api.get<BM25Summary>('/bm25index/summary')
}

// BM25 Types
export interface BM25IndexProgress {
  totalDocuments: number
  processedDocuments: number
  progressPercent: number
  totalTerms: number
  message: string
}

export interface BM25SearchResult {
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

export interface BM25Summary {
  totalIndexedDocuments: number
  totalVocabularySize: number
  averageDocLength: number
  totalFiles: number
  totalChunks: number
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

// Knowledge Graph
export const graphApi = {
  stats: () => api.get('/graph/stats'),
  node: (nodeId: string) => api.get('/graph/node', { params: { nodeId } }),
  neighbors: (nodeId: string, depth = 1, edgeTypes?: string) =>
    api.get('/graph/neighbors', { params: { nodeId, depth, edgeTypes } }),
  search: (q: string, limit = 10) => api.get('/graph/search', { params: { q, limit } }),
  subgraph: (rootIds: string[], depth = 1) => api.post('/graph/subgraph', { rootIds, depth }),
  rebuild: () => api.post('/graph/rebuild'),
  rebuildStatus: () => api.get<{ isRebuilding: boolean }>('/graph/rebuild/status')
}

export default api
