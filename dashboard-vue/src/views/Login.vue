<template>
  <div class="login-container">
    <n-card title="Obsidian RAG 登录" style="width: 400px">
      <n-form @submit.prevent="handleLogin">
        <n-form-item label="API Key">
          <n-input
            v-model:value="apiKey"
            type="password"
            show-password-on="click"
            placeholder="请输入 API Key"
            @keyup.enter="handleLogin"
          />
        </n-form-item>
        <n-space vertical>
          <n-button type="primary" block :loading="loading" @click="handleLogin">
            登录
          </n-button>
          <n-text v-if="errorMsg" type="error" style="text-align: center; display: block">
            {{ errorMsg }}
          </n-text>
        </n-space>
      </n-form>
      <template #footer>
        <n-text depth="3" style="font-size: 12px">
          API Key 在服务端配置文件中设置，若未配置则无需登录
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
  if (!apiKey.value.trim()) {
    errorMsg.value = '请输入 API Key'
    return
  }

  loading.value = true
  errorMsg.value = ''

  try {
    // 保存 API Key 到 store
    authStore.setApiKey(apiKey.value.trim())

    // 验证 API Key 是否有效
    const valid = await authStore.verifyApiKey()

    if (valid) {
      message.success('登录成功')
      router.push('/')
    } else {
      authStore.clearApiKey()
      errorMsg.value = 'API Key 无效'
    }
  } catch (error: any) {
    authStore.clearApiKey()
    errorMsg.value = error.response?.data?.error || '验证失败'
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
