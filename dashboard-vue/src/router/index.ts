import { createRouter, createWebHistory } from 'vue-router'
import type { RouteRecordRaw } from 'vue-router'
import { useAuthStore } from '@/stores/auth'

const routes: RouteRecordRaw[] = [
  {
    path: '/login',
    name: 'Login',
    component: () => import('@/views/Login.vue'),
    meta: { title: '登录', public: true }
  },
  {
    path: '/',
    name: 'Layout',
    component: () => import('@/layouts/MainLayout.vue'),
    redirect: '/dashboard',
    children: [
      {
        path: 'dashboard',
        name: 'Dashboard',
        component: () => import('@/views/Dashboard.vue'),
        meta: { title: 'Dashboard' }
      },
      {
        path: 'search',
        name: 'Search',
        component: () => import('@/views/Search.vue'),
        meta: { title: '向量搜索' }
      },
      {
        path: 'sources',
        name: 'Sources',
        component: () => import('@/views/Sources.vue'),
        meta: { title: '源管理' }
      },
      {
        path: 'settings',
        name: 'Settings',
        component: () => import('@/views/Settings.vue'),
        meta: { title: '设置' }
      },
      {
        path: 'performance',
        name: 'Performance',
        component: () => import('@/views/Performance.vue'),
        meta: { title: '性能测试' }
      },
      {
        path: 'api-help',
        name: 'ApiHelp',
        component: () => import('@/views/ApiHelp.vue'),
        meta: { title: 'API 文档' }
      },
      {
        path: 'models',
        name: 'Models',
        component: () => import('@/views/Models.vue'),
        meta: { title: '模型管理' }
      },
      {
        path: 'system',
        name: 'System',
        component: () => import('@/views/System.vue'),
        meta: { title: '系统监控' }
      },
      {
        path: 'bm25-index',
        name: 'BM25Index',
        component: () => import('@/views/BM25Index.vue'),
        meta: { title: 'BM25索引' }
      }
    ]
  }
]

const router = createRouter({
  history: createWebHistory(),
  routes
})

router.beforeEach(async (to, _from, next) => {
  document.title = `${to.meta.title || 'Obsidian RAG'} - Obsidian RAG`

  // 公开页面直接放行
  if (to.meta.public) {
    next()
    return
  }

  const authStore = useAuthStore()

  // 尝试验证 API Key 是否有效
  if (authStore.apiKey) {
    try {
      // 静默验证，不显示错误
      await authStore.verifyApiKey()
      next()
    } catch {
      // 验证失败，跳转登录
      next('/login')
    }
  } else {
    // 没有 API Key，尝试访问看是否需要认证
    try {
      await fetch('/api/system/status')
      next()
    } catch (error: any) {
      if (error.status === 401 || error.status === 403) {
        next('/login')
      } else {
        next()
      }
    }
  }
})

export default router
