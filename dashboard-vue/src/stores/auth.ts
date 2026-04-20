import { defineStore } from 'pinia'
import { ref, computed } from 'vue'
import axios from 'axios'
import { API_URL } from '@/config/env'

const API_KEY_STORAGE_KEY = 'reference_rag_api_key'

export const useAuthStore = defineStore('auth', () => {
  const apiKey = ref<string | null>(localStorage.getItem(API_KEY_STORAGE_KEY))
  const isAuthenticated = computed(() => !!apiKey.value)

  // Set axios default header
  const updateAuthHeader = (key: string | null) => {
    if (key) {
      axios.defaults.headers.common['X-API-Key'] = key
    } else {
      delete axios.defaults.headers.common['X-API-Key']
    }
  }

  // Initialize header on load
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

  // Check if authentication is required by server
  const checkAuthRequired = async (): Promise<boolean> => {
    try {
      const response = await axios.get('/api/auth/check')
      return response.data.authRequired === true
    } catch {
      // If endpoint fails, assume auth is required
      return true
    }
  }

  const verifyApiKey = async (): Promise<boolean> => {
    try {
      // Use a simple API call to verify API Key
      await axios.get(`${API_URL}/system/status`)
      return true
    } catch (error: any) {
      if (error.response?.status === 401 || error.response?.status === 403) {
        return false
      }
      // Other errors (like network) may not be API Key issue
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
    checkAuthRequired,
    verifyApiKey,
    logout
  }
})
