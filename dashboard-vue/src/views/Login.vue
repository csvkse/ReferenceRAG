<template>
  <div class="login-container">
    <n-card title="ReferenceRAG Login" style="width: 400px">
      <n-form @submit.prevent="handleLogin">
        <n-form-item label="API Key (optional)">
          <n-input
            v-model:value="apiKey"
            type="password"
            show-password-on="click"
            placeholder="Leave empty if API Key is not configured"
            @keyup.enter="handleLogin"
          />
        </n-form-item>
        <n-space vertical>
          <n-button type="primary" block :loading="loading" @click="handleLogin">
            Login
          </n-button>
          <n-text v-if="errorMsg" type="error" style="text-align: center; display: block">
            {{ errorMsg }}
          </n-text>
        </n-space>
      </n-form>
      <template #footer>
        <n-text depth="3" style="font-size: 12px">
          If API Key is not configured on server, leave empty to login directly
        </n-text>
      </template>
    </n-card>
  </div>
</template>

<script setup lang="ts">
import { ref } from 'vue'
import { useRouter } from 'vue-router'
import { useMessage } from 'naive-ui'
import { useAuthStore } from '@/stores/auth'

const router = useRouter()
const message = useMessage()
const authStore = useAuthStore()

const apiKey = ref('')
const loading = ref(false)
const errorMsg = ref('')

const handleLogin = async () => {
  loading.value = true
  errorMsg.value = ''

  try {
    // First check if server requires authentication
    const authRequired = await authStore.checkAuthRequired()

    if (!authRequired) {
      // Server doesn't require auth, login directly
      authStore.clearApiKey()
      message.success('Login successful')
      router.push('/')
      return
    }

    // Server requires auth, validate API Key
    if (!apiKey.value.trim()) {
      errorMsg.value = 'API Key is required. Please enter your API Key.'
      return
    }

    // Save and verify API Key
    authStore.setApiKey(apiKey.value.trim())
    const valid = await authStore.verifyApiKey()

    if (valid) {
      message.success('Login successful')
      router.push('/')
    } else {
      authStore.clearApiKey()
      errorMsg.value = 'Invalid API Key. Please try again.'
    }
  } catch (error: any) {
    authStore.clearApiKey()
    errorMsg.value = error.response?.data?.error || 'Connection failed. Please check if service is running.'
  } finally {
    loading.value = false
  }
}
</script>

<style scoped>
.login-container {
  display: flex;
  justify-content: center;
  align-items: center;
  min-height: 100vh;
  background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
}
</style>
