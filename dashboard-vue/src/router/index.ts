import { createRouter, createWebHistory } from 'vue-router'
import type { RouteRecordRaw } from 'vue-router'
import { API_URL } from '@/config/env'
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
      },
      {
        path: 'guide',
        name: 'Guide',
        component: () => import('@/views/Guide.vue'),
        meta: { title: '使用指南' }
      },
      {
        path: 'graph',
        name: 'Graph',
        component: () => import('@/views/Graph.vue'),
        meta: { title: '知识图谱' }
      }
    ]
  }
]

const router = createRouter({
  history: createWebHistory(),
  routes
})

// 标记是否已检查认证状态
let authChecked = false

router.beforeEach(async (to, _from, next) => {
  document.title = `${to.meta.title || 'ReferenceRAG'} - ReferenceRAG`

  // 公开页面直接放行
  if (to.meta.public) {
    next()
    return
  }

  const authStore = useAuthStore()

  // 如果已有 API Key，直接放行（header 已在 store 中设置）
  if (authStore.apiKey) {
    next()
    return
  }

  // 只在首次访问时检查是否需要认证
  if (!authChecked) {
    authChecked = true
    try {
      // 尝试访问一个简单的 API
      const response = await fetch(`${API_URL}/system/status`)
      if (response.status === 401 || response.status === 403) {
        // 需要认证，跳转登录页
        next('/login')
        return
      }
      // 不需要认证，放行
      next()
    } catch {
      // 网络错误等，放行
      next()
    }
  } else {
    next()
  }
})

export default router
