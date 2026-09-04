import axios from 'axios';
import { API_URL } from '/app/config/env.js';
// PascalCase ↔ camelCase conversion utilities
// Backend uses PropertyNamingPolicy = null (PascalCase), frontend uses camelCase
const toCamel = (str) => str.charAt(0).toLowerCase() + str.slice(1);
const toPascal = (str) => str.charAt(0).toUpperCase() + str.slice(1);
function transformKeysDeep(obj, keyFn) {
    if (obj === null || obj === undefined)
        return obj;
    if (typeof obj === 'string' || typeof obj === 'number' || typeof obj === 'boolean')
        return obj;
    if (Array.isArray(obj))
        return obj.map((item) => transformKeysDeep(item, keyFn));
    if (obj instanceof Date)
        return obj;
    const result = {};
    for (const [k, v] of Object.entries(obj)) {
        result[keyFn(k)] = transformKeysDeep(v, keyFn);
    }
    return result;
}
const api = axios.create({
    baseURL: API_URL,
    timeout: 60000,
    headers: { 'Content-Type': 'application/json' }
});
// Transform outgoing request body: camelCase → PascalCase
api.interceptors.request.use((config) => {
    if (config.data && typeof config.data === 'object') {
        config.data = transformKeysDeep(config.data, toPascal);
    }
    return config;
});
// Transform incoming response data: PascalCase → camelCase
api.interceptors.response.use((response) => {
    if (response.data && typeof response.data === 'object') {
        response.data = transformKeysDeep(response.data, toCamel);
    }
    return response;
}, (error) => {
    console.error('API Error:', error.response?.data || error.message);
    // 401 未授权，跳转登录页
    if (error.response?.status === 401) {
        // 清除本地存储的 API Key
        localStorage.removeItem('reference_rag_api_key');
        // 跳转登录页（避免重复跳转）
        if (window.location.pathname !== '/login') {
            window.location.href = window.location.protocol === 'app:' ? '/index.html#/login' : '/login';
        }
    }
    return Promise.reject(error);
});
// AI Query
export const aiQueryApi = {
    query: (data) => api.post('/ai/query', data),
    drilldown: (data) => api.post('/ai/drill-down', data),
    getSearchStatus: () => api.get('/ai/status')
};
// Dashboard API removed — use indexApi.getSummary() + sourcesApi.getAll()
// Sources
export const sourcesApi = {
    getAll: () => api.get('/Sources'),
    getByName: (name) => api.get(`/Sources/${name}`),
    create: (data) => api.post('/Sources', data),
    update: (name, data) => api.put(`/Sources/${name}`, data),
    delete: (name, deleteData = false) => api.delete(`/Sources/${name}?deleteData=${deleteData}`),
    toggle: (name, enabled) => api.patch(`/Sources/${name}/toggle`, { enabled }),
    startIndex: (name, force = false) => api.post(`/Sources/${name}/index`, { force }),
    scan: (name) => api.get(`/Sources/${name}/scan`),
    getFiles: (name) => api.get(`/Sources/${name}/files`)
};
// Index Jobs — /api/index/jobs
export const indexJobsApi = {
    startJob: (request) => api.post('/index/jobs', request || {}),
    getActive: () => api.get('/index/jobs'),
    getAll: () => api.get('/index/jobs/all'),
    getHistory: () => api.get('/index/jobs/history'),
    clearHistory: () => api.delete('/index/jobs/history'),
    getJob: (jobId) => api.get(`/index/jobs/${jobId}`),
    cancelJob: (jobId) => api.post(`/index/jobs/${jobId}/cancel`)
};
// Index Management — /api/index
export const indexApi = {
    getSummary: () => api.get('/index/summary'),
    getModels: () => api.get('/index/models'),
    getCurrentModel: () => api.get('/index/models/current'),
    deleteModel: (modelName) => api.delete(`/index/models/${modelName}`),
    deleteAllModels: () => api.delete('/index/models'),
    deleteAllData: () => api.delete('/index/data'),
    rebuild: (request) => api.post('/index/rebuild', request || {}),
    rebuildSource: (sourceName) => api.post(`/index/rebuild/${sourceName}`),
    cleanup: () => api.post('/index/cleanup')
};
export const settingsApi = {
    get: () => api.get('/Settings'),
    save: (config) => api.post('/Settings', config),
    updateModelsPath: (modelsPath, migrateExisting = false) => api.patch('/Settings/models-path', { modelsPath, migrateExisting }),
    getCudaAvailability: () => api.get('/Settings/cuda-availability')
};
// Performance
export const performanceApi = {
    benchmark: (data) => api.post('/Performance/benchmark', data || {}),
    quickTest: (textLength = 10000) => api.get(`/Performance/quick-test?textLength=${textLength}`),
    batchSizes: (data) => api.post('/Performance/batch-sizes', data || {}),
    memoryTest: (vectorCount = 1000, dimension = 512) => api.get(`/Performance/memory-test?vectorCount=${vectorCount}&dimension=${dimension}`),
    getSearchTrace: () => api.get('/performance/search-trace'),
    resetSearchTrace: () => api.delete('/performance/search-trace')
};
export const semanticTestApi = {
    shortText: (data) => api.post('/SemanticTest/short-text', data),
    longText: (data) => api.post('/SemanticTest/long-text', data),
    getRecords: () => api.get('/SemanticTest/records'),
    clearRecords: () => api.delete('/SemanticTest/records'),
    getPresets: () => api.get('/SemanticTest/presets'),
    runPreset: (suiteName) => api.post(`/SemanticTest/preset/${suiteName}`),
    getStatistics: () => api.get('/SemanticTest/statistics'),
    modelProbe: () => api.get('/SemanticTest/model-probe')
};
// Models
export const modelsApi = {
    getAll: () => api.get('/Models'),
    getCurrent: () => api.get('/Models/current'),
    switch: (modelName, deleteOldVectors = false) => api.post(`/Models/switch`, { modelName, deleteOldVectors }),
    download: (modelName, onnxFilePath) => api.post(`/Models/download/${modelName}`, onnxFilePath ? { onnxFilePath } : {}),
    getDownloadProgress: (modelName) => api.get(`/Models/download/${modelName}/progress`),
    getActiveDownloads: () => api.get('/Models/downloads/active'),
    convert: (modelName, targetFormat) => api.post(`/Models/${modelName}/convert`, { targetFormat }),
    getConvertProgress: (modelName) => api.get(`/Models/${modelName}/convert/progress`),
    addCustom: (huggingFaceId, displayName) => api.post('/Models/custom', { huggingFaceId, displayName }),
    delete: (modelName) => api.delete(`/Models/${modelName}`),
    getDownloadOptions: (modelName) => api.get(`/Models/download-options/${modelName}`),
    // Rerank Models
    getRerankModels: () => api.get('/Models/rerank'),
    getDownloadedRerankModels: () => api.get('/Models/rerank/downloaded'),
    getCurrentRerankModel: () => api.get('/Models/rerank/current'),
    switchRerankModel: (modelName) => api.post(`/Models/rerank/switch`, { modelName }),
    downloadRerankModel: (modelName, onnxFilePath) => api.post(`/Models/rerank/download/${modelName}`, onnxFilePath ? { onnxFilePath } : {}),
    getRerankDownloadProgress: (modelName) => api.get(`/Models/rerank/download/${modelName}/progress`),
    deleteRerankModel: (modelName) => api.delete(`/Models/rerank/${modelName}`),
    getRerankDownloadOptions: (modelName) => api.get(`/Models/rerank/download-options/${modelName}`),
    // 扫描模型目录
    scanModels: () => api.post('/Models/scan')
};
// vectorsApi / vectorIndexApi removed — use indexJobsApi + indexApi
// System
export const systemApi = {
    getStatus: () => api.get('/system/status'),
    getHealth: () => api.get('/system/health'),
    getMetrics: () => api.get('/system/metrics'),
    getIndexMetrics: () => api.get('/system/metrics/index'),
    getMetricsSummary: () => api.get('/system/metrics/queries'),
    getAlerts: () => api.get('/system/alerts'),
    checkAlerts: () => api.post('/system/alerts/check'),
    getAlertRules: () => api.get('/system/alerts/rules'),
    restart: () => api.post('/system/restart')
};
// BM25 Index
export const bm25IndexApi = {
    // 索引操作
    indexAll: () => api.post('/bm25index/index'),
    indexDocument: (chunkId, content) => api.post(`/bm25index/documents/${chunkId}`, { content }),
    clearIndex: () => api.delete('/bm25index/index'),
    // 搜索
    search: (query, topK) => api.get('/bm25index/search', { params: { query, topK } }),
    // 统计
    getSummary: () => api.get('/bm25index/summary')
};
// Rerank Test
export const rerankTestApi = {
    test: (data) => api.post('/RerankTest/test', data),
    getRecords: (params) => api.get('/RerankTest/records', { params }),
    getPresets: () => api.get('/RerankTest/presets'),
    runPreset: (suiteName) => api.post(`/RerankTest/preset/${suiteName}`),
    clearRecords: (params) => api.delete('/RerankTest/records', { params }),
    getStatistics: (params) => api.get('/RerankTest/statistics', { params }),
    benchmark: (data) => api.post('/RerankTest/benchmark', data)
};
// Paths
export const pathsApi = {
    getPaths: () => api.get('/paths')
};
// Knowledge Graph
export const graphApi = {
    stats: () => api.get('/graph/stats'),
    node: (nodeId) => api.get('/graph/node', { params: { nodeId } }),
    neighbors: (nodeId, depth = 1, edgeTypes) => api.get('/graph/neighbors', { params: { nodeId, depth, edgeTypes } }),
    search: (q, limit = 10) => api.get('/graph/search', { params: { q, limit } }),
    subgraph: (rootIds, depth = 1) => api.post('/graph/subgraph', { rootIds, depth }),
    rebuild: () => api.post('/graph/rebuild'),
    rebuildStatus: () => api.get('/graph/rebuild/status')
};
export default api;
