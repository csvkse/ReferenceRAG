<template>
  <n-config-provider :theme="currentTheme" :locale="zhCN" :date-locale="dateZhCN">
    <n-loading-bar-provider>
      <n-message-provider>
        <n-notification-provider>
          <n-dialog-provider>
            <router-view />
          </n-dialog-provider>
        </n-notification-provider>
      </n-message-provider>
    </n-loading-bar-provider>
  </n-config-provider>
</template>

<script setup lang="ts">
import { ref, computed, onMounted, provide } from 'vue'
import { darkTheme, zhCN, dateZhCN } from 'naive-ui'

const isDark = ref(true)

const currentTheme = computed(() => isDark.value ? darkTheme : null)

const toggleTheme = () => {
  isDark.value = !isDark.value
  localStorage.setItem('rag_theme', isDark.value ? 'dark' : 'light')
  updateBodyClass()
}

const updateBodyClass = () => {
  if (isDark.value) {
    document.body.classList.add('dark-theme')
    document.body.classList.remove('light-theme')
  } else {
    document.body.classList.add('light-theme')
    document.body.classList.remove('dark-theme')
  }
}

// 提供给子组件使用
provide('themeContext', {
  isDark,
  toggleTheme
})

onMounted(() => {
  const savedTheme = localStorage.getItem('rag_theme')
  if (savedTheme) {
    isDark.value = savedTheme === 'dark'
  }
  updateBodyClass()
})
</script>

<style>
* {
  margin: 0;
  padding: 0;
  box-sizing: border-box;
}

html, body, #app {
  height: 100%;
  width: 100%;
  overflow: hidden;
}

body.dark-theme {
  background-color: #1a1a1a;
}

body.light-theme {
  background-color: #f5f5f5;
}
</style>
