import { defineComponent as _defineComponent } from 'vue';
import { ref, computed, h, inject, onMounted, onUnmounted } from 'vue';
import { useRouter, useRoute } from 'vue-router';
import { NIcon } from 'naive-ui';
import { BookOutline, MoonOutline, SunnyOutline, LogOutOutline, HomeOutline, SearchOutline, FolderOutline, SettingsOutline, SpeedometerOutline, CodeSlashOutline, CubeOutline, PulseOutline, HelpCircleOutline, LayersOutline, ConstructOutline, TerminalOutline, InformationCircleOutline, GitNetworkOutline, ChatbubblesOutline } from '@vicons/ionicons5';
import { useIndexStore } from '/app/stores/index.js';
import { useAuthStore } from '/app/stores/auth.js';
const component = /*@__PURE__*/ _defineComponent({
    __name: 'MainLayout',
    setup(__props, { expose: __expose }) {
        __expose();
        const router = useRouter();
        const route = useRoute();
        const indexStore = useIndexStore();
        onMounted(() => indexStore.connect());
        onUnmounted(() => indexStore.disconnect());
        const authStore = useAuthStore();
        const collapsed = ref(false);
        // 获取主题上下文
        const themeContext = inject('themeContext');
        const connectionStatus = computed(() => indexStore.isConnected ? 'connected' : 'disconnected');
        const currentKey = computed(() => route.name);
        const currentTitle = computed(() => route.meta.title || 'Dashboard');
        const renderIcon = (icon) => {
            return () => h(NIcon, null, { default: () => h(icon) });
        };
        const menuOptions = [
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
                    },
                    {
                        label: '知识图谱',
                        key: 'Graph',
                        icon: renderIcon(GitNetworkOutline)
                    },
                    {
                        label: 'AI 对话',
                        key: 'Chat',
                        icon: renderIcon(ChatbubblesOutline)
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
        ];
        const handleMenuSelect = (key) => {
            router.push({ name: key });
        };
        const toggleTheme = () => {
            themeContext?.toggleTheme();
        };
        const handleLogout = () => {
            authStore.logout();
            router.push('/login');
        };
        const goHome = () => {
            router.push({ name: 'Dashboard' });
        };
        const __returned__ = { router, route, indexStore, authStore, collapsed, themeContext, connectionStatus, currentKey, currentTitle, renderIcon, menuOptions, handleMenuSelect, toggleTheme, handleLogout, goHome, get NIcon() { return NIcon; }, get BookOutline() { return BookOutline; }, get MoonOutline() { return MoonOutline; }, get SunnyOutline() { return SunnyOutline; }, get LogOutOutline() { return LogOutOutline; } };
        return __returned__;
    }
});

component.template = "\n  <n-layout has-sider style=\"height: 100vh\">\n    <n-layout-sider\n      bordered\n      collapse-mode=\"width\"\n      :collapsed-width=\"64\"\n      :width=\"240\"\n      :collapsed=\"collapsed\"\n      show-trigger\n      @collapse=\"collapsed = true\"\n      @expand=\"collapsed = false\"\n    >\n      <div class=\"logo\">\n        <n-icon size=\"28\" color=\"#63e2b7\">\n          <BookOutline />\n        </n-icon>\n        <span v-show=\"!collapsed\" class=\"logo-text\">ReferenceRAG</span>\n      </div>\n      <n-menu\n        :collapsed=\"collapsed\"\n        :collapsed-width=\"64\"\n        :collapsed-icon-size=\"22\"\n        :options=\"menuOptions\"\n        :value=\"currentKey\"\n        @update:value=\"handleMenuSelect\"\n      />\n    </n-layout-sider>\n    <n-layout>\n      <n-layout-header bordered style=\"height: 60px; padding: 0 20px; display: flex; align-items: center; justify-content: space-between\">\n        <n-breadcrumb>\n          <n-breadcrumb-item @click=\"goHome\" style=\"cursor: pointer\">ReferenceRAG</n-breadcrumb-item>\n          <n-breadcrumb-item>{{ currentTitle }}</n-breadcrumb-item>\n        </n-breadcrumb>\n        <n-space align=\"center\">\n          <n-badge :value=\"connectionStatus\" :type=\"connectionStatus === 'connected' ? 'success' : 'error'\" />\n          <n-button text @click=\"toggleTheme\">\n            <template #icon>\n              <n-icon>\n                <MoonOutline v-if=\"!themeContext?.isDark.value\" />\n                <SunnyOutline v-else />\n              </n-icon>\n            </template>\n          </n-button>\n          <n-button v-if=\"authStore.isAuthenticated\" text @click=\"handleLogout\">\n            <template #icon>\n              <n-icon><LogOutOutline /></n-icon>\n            </template>\n          </n-button>\n        </n-space>\n      </n-layout-header>\n      <n-layout-content style=\"padding: 20px; overflow: auto\">\n        <keep-alive :include=\"['Chat']\">\n          <router-view />\n        </keep-alive>\n      </n-layout-content>\n    </n-layout>\n  </n-layout>\n";
component.__scopeId = "data-v-74870106";
export default component;
