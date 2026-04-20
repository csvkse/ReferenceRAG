/**
 * 环境配置
 * 统一管理 API 和 SignalR Hub 的基础 URL
 */

// 后端服务地址
export const API_BASE_URL = 'http://localhost:7897'
export const API_BASE_PATH = '/api'
export const HUB_BASE_PATH = '/hubs'

// 完整的 API URL
export const API_URL = `${API_BASE_URL}${API_BASE_PATH}`

// SignalR Hub URLs
export const HUB_URLS = {
  index: `${API_BASE_URL}${HUB_BASE_PATH}/index`
}
