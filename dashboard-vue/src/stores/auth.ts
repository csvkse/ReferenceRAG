import { defineStore } from 'pinia'
import { ref, computed } from 'vue'
import axios from 'axios'

const API_KEY_STORAGE_KEY = 'obsidian_rag_api_key'

export const useAuthStore = defineStore('auth', () => {
  const apiKey = ref<string | null>(localStorage.getItem(API_KEY_STORAGE_KEY))
  const isAuthenticated = computed(() => !!apiKey.value)

  // 设置 axios 默认 header
  const updateAuthHeader = (key: string | null) => {
    if (key) {
      axios.defaults.headers.common['X-API-Key'] = key
    } else {
      delete axios.defaults.headers.common['X-API-Key']
    }
  }

  // 初始化时设置 header
  if (apiKey.value) {
    updateAuthHeader(apiKey.value)
  }

  const setApiKey = (key: string) => {
    apiKey.value = key
    localStorage.setItem(API_KEY_STORAGE_KEY, key)
    updateAuthHeader(key)
  }

  const clearApiKey = () => {
    apiKey.value = null
    localStorage.removeItem(API_KEY_STORAGE_KEY)
    updateAuthHeader(null)
  }

  const verifyApiKey = async (): Promise<boolean> => {
    try {
      // 使用一个简单的 API 调用来验证 API Key
      await axios.get('/api/system/status')
      return true
    } catch (error: any) {
      if (error.response?.status === 401 || error.response?.status === 403) {
        return false
      }
      // 其他错误（如网络错误）可能不是 API Key 问题
      throw error
    }
  }

  const logout = () => {
    clearApiKey()
  }

  return {
    apiKey,
    isAuthenticated,
    setApiKey,
    clearApiKey,
    verifyApiKey,
    logout
  }
})
