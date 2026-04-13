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
        <span v-show="!collapsed" class="logo-text">ReferenceRAG</span>
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
          <n-breadcrumb-item @click="goHome" style="cursor: pointer">ReferenceRAG</n-breadcrumb-item>
          <n-breadcrumb-item>{{ currentTitle }}</n-breadcrumb-item>
        </n-breadcrumb>
        <n-space align="center">
          <n-badge :value="connectionStatus" :type="connectionStatus === 'connected' ? 'success' : 'error'" />
          <n-button text @click="toggleTheme">
            <template #icon>
              <n-icon>
                <MoonOutline v-if="!themeContext?.isDark.value" />
                <SunnyOutline v-else />
              </n-icon>
            </template>
          </n-button>
          <n-button v-if="authStore.isAuthenticated" text @click="handleLogout">
            <template #icon>
              <n-icon><LogOutOutline /></n-icon>
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
import { ref, computed, h, inject } from 'vue'
import { useRouter, useRoute } from 'vue-router'
import { NIcon } from 'naive-ui'
import type { MenuOption } from 'naive-ui'
import {
  BookOutline,
  MoonOutline,
  SunnyOutline,
  LogOutOutline,
  HomeOutline,
  SearchOutline,
  FolderOutline,
  SettingsOutline,
  SpeedometerOutline,
  CodeSlashOutline,
  CubeOutline,
  PulseOutline,
  HelpCircleOutline,
  LayersOutline,
  ConstructOutline,
  TerminalOutline,
  InformationCircleOutline
} from '@vicons/ionicons5'
import { useIndexStore } from '@/stores/index'
import { useAuthStore } from '@/stores/auth'

const router = useRouter()
const route = useRoute()
const indexStore = useIndexStore()
const authStore = useAuthStore()

const collapsed = ref(false)

// 获取主题上下文
const themeContext = inject<{
  isDark: { value: boolean }
  toggleTheme: () => void
}>('themeContext')

const connectionStatus = computed(() => indexStore.isConnected ? 'connected' : 'disconnected')
const currentKey = computed(() => route.name as string)
const currentTitle = computed(() => route.meta.title as string || 'Dashboard')

const renderIcon = (icon: typeof HomeOutline) => {
  return () => h(NIcon, null, { default: () => h(icon) })
}

const menuOptions: MenuOption[] = [
  {
    label: '核心功能',
    key: 'core',
    icon: renderIcon(LayersOutline),
    children: [
      {
        label: 'Dashboard',
        key: 'Dashboard',
        icon: renderIcon(HomeOutline)
      },
      {
        label: '向量搜索',
        key: 'Search',
        icon: renderIcon(SearchOutline)
      }
    ]
  },
  {
    label: '数据管理',
    key: 'data',
    icon: renderIcon(FolderOutline),
    children: [
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
        label: 'BM25索引',
        key: 'BM25Index',
        icon: renderIcon(BookOutline)
      }
    ]
  },
  {
    label: '系统管理',
    key: 'system',
    icon: renderIcon(ConstructOutline),
    children: [
      {
        label: '系统监控',
        key: 'System',
        icon: renderIcon(PulseOutline)
      },
      {
        label: '性能测试',
        key: 'Performance',
        icon: renderIcon(SpeedometerOutline)
      },
      {
        label: '设置',
        key: 'Settings',
        icon: renderIcon(SettingsOutline)
      }
    ]
  },
  {
    label: '开发工具',
    key: 'dev',
    icon: renderIcon(TerminalOutline),
    children: [
      {
        label: 'API 文档',
        key: 'ApiHelp',
        icon: renderIcon(CodeSlashOutline)
      }
    ]
  },
  {
    label: '帮助',
    key: 'help',
    icon: renderIcon(InformationCircleOutline),
    children: [
      {
        label: '使用指南',
        key: 'Guide',
        icon: renderIcon(HelpCircleOutline)
      }
    ]
  }
]

const handleMenuSelect = (key: string) => {
  router.push({ name: key })
}

const toggleTheme = () => {
  themeContext?.toggleTheme()
}

const handleLogout = () => {
  authStore.logout()
  router.push('/login')
}

const goHome = () => {
  router.push({ name: 'Dashboard' })
}
</script>

<style scoped>
.logo {
  height: 60px;
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 10px;
  border-bottom: 1px solid var(--logo-border, rgba(255, 255, 255, 0.1));
}

.logo-text {
  font-size: 18px;
  font-weight: 600;
  color: var(--logo-color, #fff);
}

/* 浅色模式 */
:global(body.light-theme) .logo {
  --logo-border: rgba(0, 0, 0, 0.1);
  --logo-color: #1a1a1a;
}

/* 面包屑首页链接样式 */
:deep(.n-breadcrumb-item:first-child .n-breadcrumb-item__link) {
  cursor: pointer;
  transition: color 0.2s;
}

:deep(.n-breadcrumb-item:first-child .n-breadcrumb-item__link:hover) {
  color: #63e2b7;
}
</style>
