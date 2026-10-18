/**
 * 资源失效映射注册表 (InvalidationStrategyRegistry)
 * 定义当某类型后台任务完成时，哪些前端数据标签应当自动失效并静默重载。
 */
export const InvalidationRegistry = {
  // 切片重建：需要刷新数据源列表、向量统计、仪表盘
  Rechunk: ['sources', 'vector_index', 'dashboard_stats'],
  
  // 增量索引：刷新数据源、向量统计、仪表盘
  Incremental: ['sources', 'vector_index', 'dashboard_stats'],
  
  // 全量重建：刷新数据源、向量统计、仪表盘
  FullRebuild: ['sources', 'vector_index', 'dashboard_stats'],
  
  // 仅向量补全：不需要改分块，只刷向量统计与仪表盘
  VectorOnly: ['vector_index', 'dashboard_stats'],
  
  // 孤立数据清理：刷新向量统计、数据源列表、仪表盘
  Cleanup: ['sources', 'vector_index', 'dashboard_stats'],
  
  // 模型下载：刷新模型列表、模型状态与硬件可用性
  ModelDownload: ['models_list', 'model_status', 'cuda_availability'],

  // 默认兜底
  Default: ['sources', 'vector_index', 'dashboard_stats']
};

export function getInvalidationTags(jobType) {
  return InvalidationRegistry[jobType] || InvalidationRegistry.Default;
}
