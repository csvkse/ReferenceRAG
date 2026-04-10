// ============================================
// Types aligned with Swagger v1
// ============================================

// --- Source ---

export interface SourceFolder {
  path: string
  name: string
  enabled: boolean
  filePatterns: string[]
  recursive: boolean
  excludeDirs: string[]
  excludeFiles: string[]
  type: string
  tags: string[]
  priority: number
}

export interface SourceDetail {
  name: string
  path: string
  type: string
  enabled: boolean
  recursive: boolean
  filePatterns: string[]
  fileCount: number
  chunkCount: number
  lastIndexed?: string
}

export interface SourceInfo {
  name: string
  path: string
  type: string
  enabled: boolean
  fileCount: number
  chunkCount: number
}

export interface AddSourceRequest {
  path: string
  name?: string
  type?: string
  recursive?: boolean
}

// --- Search ---

export interface ChunkResult {
  refId?: string
  fileId?: string
  filePath?: string
  source?: string
  title?: string
  content?: string
  score: number
  bm25Score?: number
  embeddingScore?: number
  startLine: number
  endLine: number
  headingPath?: string
  obsidianLink?: string
}

export interface FileSummary {
  id?: string
  path?: string
  title?: string
  chunkCount: number
}

export interface SearchStats {
  totalMatches: number
  durationMs: number
  estimatedTokens: number
}

export interface QueryOptions {
  includeRecent?: boolean
  debiasPopularity?: boolean
}

export type QueryMode = 'Quick' | 'Standard' | 'Deep' | 'Hybrid'

export interface SearchFilter {
  tags?: string[]
  folders?: string[]
  dateRange?: { start?: string; end?: string }
}

export interface AIQueryRequest {
  query: string
  mode?: QueryMode
  topK?: number
  contextWindow?: number
  maxTokens?: number
  sources?: string[]
  filters?: SearchFilter
  options?: QueryOptions
}

export interface AIQueryResponse {
  query: string
  mode: QueryMode
  context: string
  prompt: string
  chunks: ChunkResult[]
  files: FileSummary[]
  stats: SearchStats
  hasMore: boolean
  suggestion?: string
}

export interface DrilldownRequest {
  query?: string
  refIds?: string[]
  expandContext?: number
}

export interface DrilldownResponse {
  expandedChunks?: ChunkResult[]
  fullContext?: string
}

// --- Index ---

export interface IndexRequest {
  sources?: string[]
  force?: boolean
}

export type IndexStatus = 'Pending' | 'Running' | 'Completed' | 'Failed' | 'Cancelled'

export interface IndexJob {
  id?: string
  status?: IndexStatus
  request?: IndexRequest
  startTime?: string
  endTime?: string
  duration?: string
  totalFiles: number
  processedFiles: number
  errors: number
  currentFile?: string
  errorMessage?: string
  progressPercent: number
}

// --- Dashboard ---

export interface DashboardStats {
  totalFiles: number
  totalChunks: number
  sourceCount: number
  avgQueryTime: number
}

// --- Settings ---

export interface ObsidianRagConfig {
  dataPath?: string
  sources?: SourceFolder[]
  embedding: EmbeddingConfig & { modelsPath?: string }
  chunking: ChunkingConfig
  search: SearchConfig
  service: ServiceConfig
  vaultPath?: string
}

export interface EmbeddingConfig {
  modelPath?: string
  modelName?: string
  useCuda: boolean
  cudaDeviceId: number
  cudaLibraryPath?: string
  maxSequenceLength: number
  batchSize: number
}

export interface ChunkingConfig {
  maxTokens: number
  minTokens: number
  overlapTokens: number
  preserveHeadings: boolean
  preserveCodeBlocks: boolean
}

export interface SearchConfig {
  defaultTopK: number
  contextWindow: number
  similarityThreshold: number
  enableMmr: boolean
  mmrLambda: number
  defaultSources?: string[]
}

export interface ServiceConfig {
  port: number
  host: string
  enableCors: boolean
  enableSwagger: boolean
  logLevel: string
}

// --- Performance ---

export interface BenchmarkRequest {
  textLength?: number
  chunkSize?: number
  overlapSize?: number
  batchSize?: number
  includeCodeBlocks?: boolean
  includeHeadings?: boolean
}

export interface BenchmarkResult {
  textLength: number
  chunkSize: number
  overlapSize: number
  batchSize: number
  textGenerationMs: number
  chunkingMs: number
  tokenCountMs: number
  embeddingMs: number
  storagePrepMs: number
  totalMs: number
  chunkCount: number
  totalChars: number
  totalTokens: number
  avgChunkTokens: number
  vectorCount: number
  vectorDimension: number
  tokensPerSecond: number
  charsPerSecond: number
  chunksPerSecond: number
  vectorsPerSecond: number
  estimatedMemoryMB: number
}

export interface QuickTestResult {
  textLength: number
  chunkingTimeMs: number
  chunkCount: number
  sampleEmbeddingTimeMs: number
  estimatedFullTimeMs: number
}

export interface BatchOptimizationRequest {
  textLength?: number
}

export interface BatchOptimizationResult {
  textLength: number
  chunkCount: number
  results: BatchSizeResult[]
}

export interface BatchSizeResult {
  batchSize: number
  avgTimeMs: number
  totalBatches: number
  estimatedTotalTimeMs: number
}

export interface MemoryTestResult {
  vectorCount: number
  dimension: number
  memoryBytes: number
  memoryMB: number
  allocationTimeMs: number
}

// --- Semantic Test ---

export interface ShortTextTestRequest {
  query?: string
  candidates?: { text?: string; expectedSimilarity?: number }[]
}

export interface SemanticTestResult {
  testType?: string
  modelName?: string
  timestamp?: string
  queryEmbeddingMs?: number
  queryTokenCount?: number
  totalEmbeddingMs?: number
  avgEmbeddingMs?: number
  candidates?: {
    text?: string
    similarity?: number
    expectedSimilarity?: number
    deviation?: number
    tokenCount?: number
  }[]
  meanAbsoluteError?: number
  rootMeanSquareError?: number
  correlationCoefficient?: number
  rankingAccuracy?: number
}

export interface LongTextTestRequest {
  query?: string
  passages?: { id?: string; text?: string; isRelevant?: boolean }[]
  topK?: number
  modelName?: string
}

export interface LongTextTestResult {
  testType?: string
  modelName?: string
  timestamp?: string
  queryEmbeddingMs?: number
  queryTokenCount?: number
  totalEmbeddingMs?: number
  avgEmbeddingMs?: number
  passages?: {
    id?: string
    text?: string
    fullTextLength?: number
    similarity?: number
    isRelevant?: boolean
    tokenCount?: number
  }[]
  precision?: number
  recall?: number
  f1Score?: number
  ndcg?: number
  mrr?: number
}

// --- Models ---

export interface ModelInfo {
  name?: string
  displayName?: string
  description?: string
  dimension: number
  maxSequenceLength: number
  modelType?: string
  isQuantized: boolean
  quantizationType?: string
  modelSizeBytes?: number
  downloadUrl?: string
  isDownloaded: boolean
  localPath?: string
  isGpuSupported: boolean
  languages?: string[]
  benchmarkScore?: number
  hasOnnx?: boolean
  onnxFormat?: string  // "embedded" | "external" | "unknown"
  canConvertFormat?: boolean
  hasAsymmetricEncoding?: boolean
}

// --- Model Download ---

export interface DownloadProgress {
  modelName: string
  status: string  // "idle" | "downloading" | "completed" | "failed" | "cancelled"
  progress: number  // 0-100
  bytesReceived: number
  totalBytes: number
  speedBytesPerSecond: number
  estimatedSecondsRemaining: number | null
  errorMessage?: string
  errorCode?: number
  startTime?: string
  endTime?: string
  downloadOptions?: ModelDownloadOptions
}

export interface OnnxFileOption {
  path: string
  displayName: string
  description?: string
  size: number
  isQuantized: boolean
  targetPlatform?: string
  hasExternalData: boolean
  externalDataPath?: string
  isInSubfolder: boolean
  isRecommended: boolean
}

export interface ModelDownloadOptions {
  modelName: string
  hasOnnx: boolean
  needsConversion: boolean
  rootOptions: OnnxFileOption[]
  subfolderOptions: OnnxFileOption[]
  allOptions: OnnxFileOption[]
  needsUserSelection: boolean
  recommendedOption?: OnnxFileOption
  estimatedSize: number
}

export interface ConvertFormatRequest {
  targetFormat: 'embedded' | 'external'
}

export interface AddCustomModelRequest {
  huggingFaceId: string
  displayName?: string
}

// --- System ---

export interface SystemStatus {
  status?: string
  system?: SystemMetrics
  index?: IndexMetrics
  activeAlerts?: Alert[]
}

export interface SystemMetrics {
  cpuUsagePercent?: number
  memoryUsedBytes?: number
  memoryTotalBytes?: number
  diskUsedBytes?: number
  diskTotalBytes?: number
  uptimeSeconds?: number
  processMemoryBytes?: number
  threadCount?: number
}

export interface IndexMetrics {
  totalFiles?: number
  totalChunks?: number
  totalVectors?: number
  indexSizeBytes?: number
  lastIndexTime?: string
  sourcesCount?: number
}

export interface MetricsSummary {
  totalQueries: number
  avgQueryLatencyMs: number
  p95QueryLatencyMs: number
  p99QueryLatencyMs: number
  avgResultsPerQuery: number
}

export interface Alert {
  name?: string
  severity?: 'Info' | 'Warning' | 'Critical'
  message?: string
  triggeredAt?: string
  value?: number
  threshold?: number
}

export interface AlertRule {
  name: string
  metricName: string
  operator: 'GreaterThan' | 'LessThan' | 'Equals'
  threshold: number
  severity: 'Info' | 'Warning' | 'Critical'
  enabled: boolean
  cooldownMinutes?: number
}

// --- Vector Stats ---

export interface VectorStats {
  modelName: string
  dimension: number
  vectorCount: number
  storageBytes: number
  modelExists: boolean
  lastUpdated: string | null
}

// --- Vector Index ---

export interface VectorModelIndex {
  modelName: string
  dimension: number
  vectorCount: number
  storageBytes: number
  lastUpdated: string | null
  isCurrentModel: boolean
  dimensionMatch: boolean
}

export interface DeleteResult {
  modelName: string
  deletedCount: number
  message: string
}

export interface BulkDeleteResult {
  results: DeleteResult[]
  totalDeleted: number
}

export interface CleanupResult {
  deletedCount: number
  message: string
}

export interface RebuildJob {
  jobId: string
  status: string
  modelName: string
  dimension: number
  sources: string[]
  message: string
}

export interface RebuildRequest {
  sources?: string[]
  deleteExisting?: boolean
}

export interface MigrateResult {
  success: boolean
  message: string
}

export interface IndexSummary {
  currentModel: string
  currentDimension: number
  totalFiles: number
  totalChunks: number
  modelStats: ModelStat[]
}

export interface ModelStat {
  modelName: string
  dimension: number
  vectorCount: number
  storageMB: number
  isCurrentModel: boolean
}

// --- Index Job ---

export interface IndexJobRequest {
  sources?: string[]
  force?: boolean
}

export interface IndexJobResponse {
  jobId: string
  status: string
  sources: string[]
  force: boolean
  message: string
  totalFiles: number
  processedFiles: number
  progressPercent: number
  currentFile?: string
  errors: number
  startTime?: string
  endTime?: string
  duration?: string
  errorMessage?: string
}

// --- Rerank Test ---

export interface RerankTestRequest {
  query: string
  documents: { id?: string; text: string; expectedRelevance?: number }[]
  modelName?: string
}

export interface RerankTestResult {
  testType: 'rerank'
  modelName: string
  timestamp: string
  queryMs: number
  documents: RerankDocumentResult[]
  ndcg: number
  mrr: number
  map: number
  rankingAccuracy: number
  meanAbsoluteError: number
}

export interface RerankDocumentResult {
  id?: string
  text: string
  relevanceScore: number
  expectedRelevance?: number
  deviation?: number
  rank: number
}
