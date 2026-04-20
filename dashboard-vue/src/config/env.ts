/**
 * 环境配置
 * 统一管理 API 和 SignalR Hub 的基础 URL
 */

// 后端服务地址
// 开发环境：设为 http://localhost:7897，通过 vite proxy 转发
// 生产环境：设为空字符串，使用相对路径（同域名同端口）
const rawBaseUrl = (import.meta.env.VITE_API_BASE_URL as string | undefined) ?? ''

export const API_BASE_URL = rawBaseUrl
export const API_BASE_PATH = '/api'
export const HUB_BASE_PATH = '/hubs'

// 完整的 API URL（相对或绝对路径）
export const API_URL = rawBaseUrl ? `${rawBaseUrl}${API_BASE_PATH}` : API_BASE_PATH

// SignalR Hub URLs
export const HUB_URLS = {
  index: rawBaseUrl ? `${rawBaseUrl}${HUB_BASE_PATH}/index` : `${HUB_BASE_PATH}/index`
}
