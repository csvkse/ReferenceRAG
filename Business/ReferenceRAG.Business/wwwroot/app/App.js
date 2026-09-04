import { defineComponent as _defineComponent } from 'vue';
import { ref, computed, onMounted, provide } from 'vue';
import { darkTheme, zhCN, dateZhCN } from 'naive-ui';
const component = /*@__PURE__*/ _defineComponent({
    __name: 'App',
    setup(__props, { expose: __expose }) {
        __expose();
        const isDark = ref(true);
        const currentTheme = computed(() => isDark.value ? darkTheme : null);
        const toggleTheme = () => {
            isDark.value = !isDark.value;
            localStorage.setItem('rag_theme', isDark.value ? 'dark' : 'light');
            updateBodyClass();
        };
        const updateBodyClass = () => {
            if (isDark.value) {
                document.body.classList.add('dark-theme');
                document.body.classList.remove('light-theme');
            }
            else {
                document.body.classList.add('light-theme');
                document.body.classList.remove('dark-theme');
            }
        };
        // 提供给子组件使用
        provide('themeContext', {
            isDark,
            toggleTheme
        });
        onMounted(() => {
            const savedTheme = localStorage.getItem('rag_theme');
            if (savedTheme) {
                isDark.value = savedTheme === 'dark';
            }
            updateBodyClass();
        });
        const __returned__ = { isDark, currentTheme, toggleTheme, updateBodyClass, get zhCN() { return zhCN; }, get dateZhCN() { return dateZhCN; } };
        return __returned__;
    }
});

component.template = "\n  <n-config-provider :theme=\"currentTheme\" :locale=\"zhCN\" :date-locale=\"dateZhCN\">\n    <n-loading-bar-provider>\n      <n-message-provider>\n        <n-notification-provider>\n          <n-dialog-provider>\n            <router-view />\n          </n-dialog-provider>\n        </n-notification-provider>\n      </n-message-provider>\n    </n-loading-bar-provider>\n  </n-config-provider>\n";
export default component;
