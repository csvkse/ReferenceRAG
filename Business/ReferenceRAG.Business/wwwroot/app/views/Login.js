import { defineComponent as _defineComponent } from 'vue';
import { ref } from 'vue';
import { useRouter } from 'vue-router';
import { useMessage } from 'naive-ui';
import { useAuthStore } from '/app/stores/auth.js';
const component = /*@__PURE__*/ _defineComponent({
    __name: 'Login',
    setup(__props, { expose: __expose }) {
        __expose();
        const router = useRouter();
        const message = useMessage();
        const authStore = useAuthStore();
        const apiKey = ref('');
        const loading = ref(false);
        const errorMsg = ref('');
        const handleLogin = async () => {
            loading.value = true;
            errorMsg.value = '';
            const targetRedirect = router.currentRoute.value.query?.redirect || '/';
            try {
                // 检查服务端是否要求认证
                const authRequired = await authStore.checkAuthRequired();
                if (!authRequired) {
                    authStore.clearApiKey();
                    message.success('登录成功');
                    router.push(targetRedirect);
                    return;
                }
                // 服务端需要认证，检查用户输入
                if (!apiKey.value.trim()) {
                    errorMsg.value = '服务端已开启认证，请输入 API 密钥。';
                    return;
                }
                // 验证 API 密钥
                authStore.setApiKey(apiKey.value.trim());
                const valid = await authStore.verifyApiKey();
                if (valid) {
                    message.success('登录成功');
                    router.push(targetRedirect);
                }
                else {
                    authStore.clearApiKey();
                    errorMsg.value = 'API 密钥无效，请重新输入。';
                }
            }
            catch (error) {
                authStore.clearApiKey();
                errorMsg.value = error.response?.data?.error || '连接失败，请检查服务是否正常运行。';
            }
            finally {
                loading.value = false;
            }
        };
        const __returned__ = { router, message, authStore, apiKey, loading, errorMsg, handleLogin };
        return __returned__;
    }
});

component.template = "\n  <div class=\"login-container\">\n    <n-card title=\"ReferenceRAG Login\" style=\"width: 400px\">\n      <n-form @submit.prevent=\"handleLogin\">\n        <n-form-item label=\"API Key (optional)\">\n          <n-input\n            v-model:value=\"apiKey\"\n            type=\"password\"\n            show-password-on=\"click\"\n            placeholder=\"Leave empty if API Key is not configured\"\n            @keyup.enter=\"handleLogin\"\n          />\n        </n-form-item>\n        <n-space vertical>\n          <n-button type=\"primary\" block :loading=\"loading\" @click=\"handleLogin\">\n            Login\n          </n-button>\n          <n-text v-if=\"errorMsg\" type=\"error\" style=\"text-align: center; display: block\">\n            {{ errorMsg }}\n          </n-text>\n        </n-space>\n      </n-form>\n      <template #footer>\n        <n-text depth=\"3\" style=\"font-size: 12px\">\n          If API Key is not configured on server, leave empty to login directly\n        </n-text>\n      </template>\n    </n-card>\n  </div>\n";
component.__scopeId = "data-v-7c5ca534";
export default component;
