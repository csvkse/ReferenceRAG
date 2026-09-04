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
            try {
                // First check if server requires authentication
                const authRequired = await authStore.checkAuthRequired();
                if (!authRequired) {
                    // Server doesn't require auth, login directly
                    authStore.clearApiKey();
                    message.success('Login successful');
                    router.push('/');
                    return;
                }
                // Server requires auth, validate API Key
                if (!apiKey.value.trim()) {
                    errorMsg.value = 'API Key is required. Please enter your API Key.';
                    return;
                }
                // Save and verify API Key
                authStore.setApiKey(apiKey.value.trim());
                const valid = await authStore.verifyApiKey();
                if (valid) {
                    message.success('Login successful');
                    router.push('/');
                }
                else {
                    authStore.clearApiKey();
                    errorMsg.value = 'Invalid API Key. Please try again.';
                }
            }
            catch (error) {
                authStore.clearApiKey();
                errorMsg.value = error.response?.data?.error || 'Connection failed. Please check if service is running.';
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
