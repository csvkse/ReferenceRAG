<template>
  <n-layout has-sider style="height: 100vh">
    <n-layout-sider
      bordered
      collapse-mode="width"
      :collapsed-width="64"
      :width="240"
      :collapsed="collapsed"
      show-trigger
      @collapse="collapsed = true"
      @expand="collapsed = false"
    >
      <div class="logo">
        <n-icon size="28" color="#63e2b7">
          <BookOutline />
        </n-icon>
        <span v-show="!collapsed" class="logo-text">Obsidian RAG</span>
      </div>
      <n-menu
        :collapsed="collapsed"
        :collapsed-width="64"
        :collapsed-icon-size="22"
        :options="menuOptions"
        :value="currentKey"
        @update:value="handleMenuSelect"
      />
    </n-layout-sider>
    <n-layout>
      <n-layout-header bordered style="height: 60px; padding: 0 20px; display: flex; align-items: center; justify-content: space-between">
        <n-breadcrumb>
          <n-breadcrumb-item>Obsidian RAG</n-breadcrumb-item>
          <n-breadcrumb-item>{{ currentTitle }}</n-breadcrumb-item>
        </n-breadcrumb>
        <n-space align="center">
          <n-badge :value="connectionStatus" :type="connectionStatus === 'connected' ? 'success' : 'error'" />
          <n-button text @click="toggleTheme">
            <template #icon>
              <n-icon><MoonOutline /></n-icon>
            </template>
          </n-button>
        </n-space>
      </n-layout-header>
      <n-layout-content style="padding: 20px; overflow: auto">
        <router-view />
      </n-layout-content>
    </n-layout>
  </n-layout>
</template>

<script setup lang="ts">
import { ref, computed, h } from 'vue'
import { useRouter, useRoute } from 'vue-router'
import { NIcon } from 'naive-ui'
import type { MenuOption } from 'naive-ui'
import {
  BookOutline,
  MoonOutline,
  HomeOutline,
  SearchOutline,
  FolderOutline,
  SettingsOutline,
  SpeedometerOutline,
  CodeSlashOutline,
  CubeOutline,
  PulseOutline
} from '@vicons/ionicons5'
import { useIndexStore } from '@/stores/index'

const router = useRouter()
const route = useRoute()
const indexStore = useIndexStore()

const collapsed = ref(false)

const connectionStatus = computed(() => indexStore.isConnected ? 'connected' : 'disconnected')
const currentKey = computed(() => route.name as string)
const currentTitle = computed(() => route.meta.title as string || 'Dashboard')

const renderIcon = (icon: typeof HomeOutline) => {
  return () => h(NIcon, null, { default: () => h(icon) })
}

const menuOptions: MenuOption[] = [
  {
    label: 'Dashboard',
    key: 'Dashboard',
    icon: renderIcon(HomeOutline)
  },
  {
    label: '向量搜索',
    key: 'Search',
    icon: renderIcon(SearchOutline)
  },
  {
    label: '源管理',
    key: 'Sources',
    icon: renderIcon(FolderOutline)
  },
  {
    label: '模型管理',
    key: 'Models',
    icon: renderIcon(CubeOutline)
  },
  {
    label: '系统监控',
    key: 'System',
    icon: renderIcon(PulseOutline)
  },
  {
    label: '设置',
    key: 'Settings',
    icon: renderIcon(SettingsOutline)
  },
  {
    label: '性能测试',
    key: 'Performance',
    icon: renderIcon(SpeedometerOutline)
  },
  {
    label: 'API 文档',
    key: 'ApiHelp',
    icon: renderIcon(CodeSlashOutline)
  }
]

const handleMenuSelect = (key: string) => {
  router.push({ name: key })
}

const toggleTheme = () => {
  // Theme toggle placeholder
}
</script>

<style scoped>
.logo {
  height: 60px;
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 10px;
  border-bottom: 1px solid rgba(255, 255, 255, 0.1);
}

.logo-text {
  font-size: 18px;
  font-weight: 600;
  color: #fff;
}
</style>
