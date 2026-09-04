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
// 标记是否已检查认证状态
let authChecked = false;
router.beforeEach(async (to, _from, next) => {
    document.title = `${to.meta.title || 'ReferenceRAG'} - ReferenceRAG`;
    // 公开页面直接放行
    if (to.meta.public) {
        next();
        return;
    }
    const authStore = useAuthStore();
    // 如果已有 API Key，直接放行（header 已在 store 中设置）
    if (authStore.apiKey) {
        next();
        return;
    }
    // 只在首次访问时检查是否需要认证
    if (!authChecked) {
        authChecked = true;
        try {
            // 尝试访问一个简单的 API
            const response = await fetch(`${API_URL}/system/status`);
            if (response.status === 401 || response.status === 403) {
                // 需要认证，跳转登录页
                next('/login');
                return;
            }
            // 不需要认证，放行
            next();
        }
        catch {
            // 网络错误等，放行
            next();
        }
    }
    else {
        next();
    }
});
export default router;
