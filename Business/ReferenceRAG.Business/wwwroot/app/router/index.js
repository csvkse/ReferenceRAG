import { createRouter, createWebHistory, createWebHashHistory } from 'vue-router';
import {ragFetch as fetch, isDesktop} from '/core/transport/index.js';
import { API_URL } from '/app/config/env.js';
import { useAuthStore } from '/app/stores/auth.js';
const routes = [
    {
        path: '/login',
        name: 'Login',
        component: () => import('/app/views/Login.js'),
        meta: { title: '登录', public: true }
    },
    {
        path: '/',
        name: 'Layout',
        component: () => import('/app/layouts/MainLayout.js'),
        redirect: '/dashboard',
        children: [
            {
                path: 'dashboard',
                name: 'Dashboard',
                component: () => import('/app/views/Dashboard.js'),
                meta: { title: 'Dashboard' }
            },
            {
                path: 'search',
                name: 'Search',
                component: () => import('/app/views/Search.js'),
                meta: { title: '向量搜索' }
            },
            {
                path: 'sources',
                name: 'Sources',
                component: () => import('/app/views/Sources.js'),
                meta: { title: '源管理' }
            },
            {
                path: 'settings',
                name: 'Settings',
                component: () => import('/app/views/Settings.js'),
                meta: { title: '设置' }
            },
            {
                path: 'performance',
                name: 'Performance',
                component: () => import('/app/views/Performance.js'),
                meta: { title: '性能测试' }
            },
            {
                path: 'api-help',
                name: 'ApiHelp',
                component: () => import('/app/views/ApiHelp.js'),
                meta: { title: 'API 文档' }
            },
            {
                path: 'models',
                name: 'Models',
                component: () => import('/app/views/Models.js'),
                meta: { title: '模型管理' }
            },
            {
                path: 'system',
                name: 'System',
                component: () => import('/app/views/System.js'),
                meta: { title: '系统监控' }
            },
            {
                path: 'bm25-index',
                name: 'BM25Index',
                component: () => import('/app/views/BM25Index.js'),
                meta: { title: 'BM25索引' }
            },
            {
                path: 'guide',
                name: 'Guide',
                component: () => import('/app/views/Guide.js'),
                meta: { title: '使用指南' }
            },
            {
                path: 'graph',
                name: 'Graph',
                component: () => import('/app/views/Graph.js'),
                meta: { title: '知识图谱' }
            },
            {
                path: 'chat',
                name: 'Chat',
                component: () => import('/app/views/Chat.js'),
                meta: { title: 'AI 对话' }
            }
        ]
    }
];
const router = createRouter({
    history: isDesktop ? createWebHashHistory() : createWebHistory(),
    routes
});
// 缓存服务端是否需要认证的状态
let serverAuthRequired = null;

router.beforeEach(async (to, _from, next) => {
    document.title = `${to.meta.title || 'ReferenceRAG'} - ReferenceRAG`;
    const authStore = useAuthStore();

    // 首次访问时检查服务端是否启用了 API Key 认证
    if (serverAuthRequired === null) {
        try {
            serverAuthRequired = await authStore.checkAuthRequired();
        } catch {
            serverAuthRequired = false;
        }
    }

    // 服务端未启用认证时，所有路由皆可自由访问
    if (!serverAuthRequired) {
        if (to.path === '/login') {
            next('/');
            return;
        }
        next();
        return;
    }

    // 服务端启用认证时的公开页面（如登录页）
    if (to.meta.public) {
        if (to.path === '/login' && authStore.isAuthenticated) {
            next('/');
            return;
        }
        next();
        return;
    }

    // 受保护页面：必须具备 API Key
    if (authStore.isAuthenticated) {
        next();
        return;
    }

    // 未登录，拦截并跳转登录页
    next({ path: '/login', query: to.fullPath !== '/' ? { redirect: to.fullPath } : undefined });
});
export default router;
