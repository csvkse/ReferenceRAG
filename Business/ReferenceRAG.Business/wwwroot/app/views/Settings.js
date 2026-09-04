import { defineComponent as _defineComponent } from 'vue';
import { ref, computed, onMounted } from 'vue';
import { useMessage } from 'naive-ui';
import { settingsApi, sourcesApi } from '/app/api/index.js';
import { API_URL } from '/app/config/env.js';
import { useAuthStore } from '/app/stores/auth.js';
const component = /*@__PURE__*/ _defineComponent({
    __name: 'Settings',
    setup(__props, { expose: __expose }) {
        __expose();
        const message = useMessage();
        const authStore = useAuthStore();
        const loading = ref(false);
        const saving = ref(false);
        const chatConfig = ref({
            endpoint: '',
            apiKey: '',
            model: '',
            systemPrompt: ''
        });
        const loadChatConfig = async () => {
            try {
                const headers = {};
                if (authStore.apiKey)
                    headers['X-API-Key'] = authStore.apiKey;
                const res = await fetch(`${API_URL}/chat/config`, { headers });
                if (!res.ok)
                    return;
                const data = await res.json();
                chatConfig.value = {
                    endpoint: data.endpoint ?? data.Endpoint ?? '',
                    apiKey: data.apiKey ?? data.ApiKey ?? '',
                    model: data.model ?? data.Model ?? '',
                    systemPrompt: data.systemPrompt ?? data.SystemPrompt ?? ''
                };
            }
            catch { /* ignore */ }
        };
        const handleSaveChat = async () => {
            const headers = { 'Content-Type': 'application/json' };
            if (authStore.apiKey)
                headers['X-API-Key'] = authStore.apiKey;
            const res = await fetch(`${API_URL}/chat/config`, {
                method: 'POST',
                headers,
                body: JSON.stringify({
                    Endpoint: chatConfig.value.endpoint,
                    ApiKey: chatConfig.value.apiKey,
                    Model: chatConfig.value.model,
                    SystemPrompt: chatConfig.value.systemPrompt
                })
            });
            const data = await res.json();
            if (!res.ok)
                throw new Error(data.error || '聊天配置保存失败');
        };
        const sourceNames = ref([]);
        const cudaAvailable = ref(true);
        const originalModelsRootPath = ref('models');
        const defaultConfig = {
            dataPath: 'data',
            sources: [],
            embedding: {
                mode: 'onnx',
                modelPath: '',
                modelName: 'bge-small-zh-v1.5',
                useCuda: false,
                cudaDeviceId: 0,
                maxSequenceLength: 512,
                batchSize: 32,
                apiBaseUrl: '',
                apiKey: ''
            },
            chunking: {
                maxTokens: 512,
                minTokens: 50,
                overlapTokens: 50,
                preserveHeadings: true,
                preserveCodeBlocks: true
            },
            search: {
                defaultTopK: 10,
                contextWindow: 1,
                similarityThreshold: 0.5,
                enableMmr: true,
                mmrLambda: 0.7,
                defaultSources: [],
                enableGraphExpansion: false,
                graphExpansionDepth: 1,
                graphExpansionMaxNodes: 3
            },
            service: {
                port: 5000,
                host: 'localhost',
                allowNetworkAccess: false,
                enableCors: true,
                enableSwagger: true,
                logLevel: 'Information',
                apiKey: ''
            },
            rerank: {
                mode: 'onnx',
                enabled: false,
                modelName: 'bge-reranker-base',
                currentModel: '',
                modelPath: '',
                useCuda: false,
                cudaDeviceId: 0,
                topN: 10,
                recallFactor: 3,
                apiBaseUrl: '',
                apiKey: ''
            }
        };
        const config = ref(JSON.parse(JSON.stringify(defaultConfig)));
        const thresholdPercent = computed({
            get: () => Math.round(config.value.search.similarityThreshold * 100),
            set: (v) => { config.value.search.similarityThreshold = v / 100; }
        });
        const mmrLambdaPercent = computed({
            get: () => Math.round(config.value.search.mmrLambda * 100),
            set: (v) => { config.value.search.mmrLambda = v / 100; }
        });
        const sourceNameOptions = computed(() => sourceNames.value.map(n => ({ label: n, value: n })));
        const logLevelOptions = [
            { label: 'Debug', value: 'Debug' },
            { label: 'Information', value: 'Information' },
            { label: 'Warning', value: 'Warning' },
            { label: 'Error', value: 'Error' },
            { label: 'Trace', value: 'Trace' },
            { label: 'Critical', value: 'Critical' },
            { label: 'None', value: 'None' }
        ];
        const loadConfig = async () => {
            loading.value = true;
            try {
                const response = await settingsApi.get();
                const data = response.data;
                config.value = { ...JSON.parse(JSON.stringify(defaultConfig)), ...data };
                if (!config.value.modelsRootPath) {
                    config.value.modelsRootPath = 'models';
                }
                originalModelsRootPath.value = config.value.modelsRootPath;
            }
            catch (error) {
                message.error('加载配置失败，使用默认值');
            }
            finally {
                loading.value = false;
            }
        };
        const loadSourceNames = async () => {
            try {
                const response = await sourcesApi.getAll();
                sourceNames.value = response.data.map(s => s.name);
            }
            catch {
                // ignore
            }
        };
        const loadCudaAvailability = async () => {
            try {
                const response = await settingsApi.getCudaAvailability();
                const data = response.data;
                cudaAvailable.value = data.isAvailable;
            }
            catch {
                cudaAvailable.value = false;
            }
        };
        const networkAccessChanged = ref(false);
        const onNetworkAccessChange = () => {
            networkAccessChanged.value = true;
        };
        const normalizeModelsRootPath = (path) => (path || '')
            .trim()
            .replace(/\//g, '\\')
            .replace(/\\+$/g, '');
        const handleSave = async () => {
            saving.value = true;
            try {
                await handleSaveChat();
                const modelsPath = config.value.modelsRootPath?.trim();
                const modelsPathChanged = normalizeModelsRootPath(modelsPath) !== normalizeModelsRootPath(originalModelsRootPath.value);
                if (modelsPathChanged && modelsPath) {
                    await settingsApi.updateModelsPath(modelsPath);
                }
                await settingsApi.save(config.value);
                originalModelsRootPath.value = config.value.modelsRootPath || 'models';
                if (networkAccessChanged.value) {
                    message.warning('配置已保存 — 监听地址已变更，需要重启服务才能生效');
                    networkAccessChanged.value = false;
                }
                else {
                    message.success('配置已保存，模型页面将自动刷新模型列表');
                }
                // 通知模型页面刷新（通过 localStorage 事件）
                localStorage.setItem('modelsPathChanged', Date.now().toString());
            }
            catch (error) {
                message.error(error.response?.data?.error || '保存配置失败');
            }
            finally {
                saving.value = false;
            }
        };
        onMounted(() => {
            loadConfig();
            loadSourceNames();
            loadCudaAvailability();
            loadChatConfig();
        });
        const __returned__ = { message, authStore, loading, saving, chatConfig, loadChatConfig, handleSaveChat, sourceNames, cudaAvailable, originalModelsRootPath, defaultConfig, config, thresholdPercent, mmrLambdaPercent, sourceNameOptions, logLevelOptions, loadConfig, loadSourceNames, loadCudaAvailability, networkAccessChanged, onNetworkAccessChange, normalizeModelsRootPath, handleSave };
        return __returned__;
    }
});

component.template = "\n  <n-space vertical :size=\"20\">\n    <n-spin :show=\"loading\">\n      <n-tabs type=\"line\" animated>\n        <!-- Embedding Settings -->\n        <n-tab-pane name=\"embedding\" tab=\"嵌入模型\">\n          <n-card>\n            <n-form label-placement=\"left\" label-width=\"140\">\n              <!-- 推理模式切换 -->\n              <n-form-item label=\"推理模式\">\n                <n-radio-group v-model:value=\"config.embedding.mode\" size=\"small\">\n                  <n-radio-button value=\"onnx\">本地 ONNX</n-radio-button>\n                  <n-radio-button value=\"openai\">OpenAI 兼容 API</n-radio-button>\n                </n-radio-group>\n              </n-form-item>\n\n              <!-- ONNX 模式专属字段 -->\n              <template v-if=\"config.embedding.mode !== 'openai'\">\n                <n-form-item label=\"模型路径\">\n                  <n-input v-model:value=\"config.embedding.modelPath\" placeholder=\"ONNX 模型路径\" />\n                </n-form-item>\n                <n-form-item label=\"模型名称\">\n                  <n-input v-model:value=\"config.embedding.modelName\" placeholder=\"bge-small-zh-v1.5\" />\n                </n-form-item>\n                <n-form-item label=\"使用 CUDA\">\n                  <n-switch v-model:value=\"config.embedding.useCuda\" :disabled=\"!cudaAvailable\" />\n                </n-form-item>\n                <n-form-item v-if=\"!cudaAvailable\" label=\"\">\n                  <n-text depth=\"3\" style=\"font-size: 12px;color: var(--n-warning-color)\">系统未检测到 CUDA/GPU 支持，Embedding CUDA 已禁用</n-text>\n                </n-form-item>\n                <n-form-item v-if=\"config.embedding.useCuda && cudaAvailable\" label=\"CUDA 设备 ID\">\n                  <n-input-number v-model:value=\"config.embedding.cudaDeviceId\" :min=\"0\" :max=\"7\" style=\"width: 200px\" />\n                </n-form-item>\n                <n-form-item v-if=\"config.embedding.useCuda && cudaAvailable\" label=\"CUDA 库路径\">\n                  <n-input v-model:value=\"config.embedding.cudaLibraryPath\" placeholder=\"CUDA DLL 所在目录（可选）\" />\n                </n-form-item>\n                <n-form-item label=\"最大序列长度\">\n                  <n-input-number v-model:value=\"config.embedding.maxSequenceLength\" :min=\"32\" :max=\"2048\" style=\"width: 100%\" />\n                </n-form-item>\n              </template>\n\n              <!-- API 模式专属字段 -->\n              <template v-else>\n                <n-form-item label=\"API 地址\">\n                  <n-input v-model:value=\"config.embedding.apiBaseUrl\" placeholder=\"http://localhost:11434/v1\" />\n                </n-form-item>\n                <n-form-item label=\"API Key\">\n                  <n-input v-model:value=\"config.embedding.apiKey\" type=\"password\" show-password-on=\"click\" placeholder=\"留空则不发送 Authorization 头\" />\n                </n-form-item>\n                <n-form-item label=\"模型名称\">\n                  <n-input v-model:value=\"config.embedding.modelName\" placeholder=\"bge-m3\" />\n                </n-form-item>\n                <n-form-item label=\"向量维度\">\n                  <n-input-number v-model:value=\"config.embedding.apiDimension\" :min=\"1\" :max=\"8192\" placeholder=\"留空则自动探测\" style=\"width: 100%\" />\n                  <n-text depth=\"3\" style=\"font-size: 12px; margin-left: 8px\">留空将在首次调用时自动探测</n-text>\n                </n-form-item>\n              </template>\n\n              <!-- 两种模式均适用 -->\n              <n-form-item label=\"批处理大小\">\n                <n-input-number v-model:value=\"config.embedding.batchSize\" :min=\"1\" :max=\"256\" style=\"width: 100%\" />\n              </n-form-item>\n            </n-form>\n          </n-card>\n        </n-tab-pane>\n\n        <!-- Chunking Settings -->\n        <n-tab-pane name=\"chunking\" tab=\"分段设置\">\n          <n-card>\n            <n-form label-placement=\"left\" label-width=\"140\">\n              <n-form-item label=\"最大 Token 数\">\n                <n-input-number v-model:value=\"config.chunking.maxTokens\" :min=\"64\" :max=\"4096\" :step=\"64\" style=\"width: 100%\" />\n              </n-form-item>\n              <n-form-item label=\"最小 Token 数\">\n                <n-input-number v-model:value=\"config.chunking.minTokens\" :min=\"10\" :max=\"512\" :step=\"10\" style=\"width: 100%\" />\n              </n-form-item>\n              <n-form-item label=\"重叠 Token 数\">\n                <n-input-number v-model:value=\"config.chunking.overlapTokens\" :min=\"0\" :max=\"512\" :step=\"10\" style=\"width: 100%\" />\n              </n-form-item>\n              <n-form-item label=\"保留标题结构\">\n                <n-switch v-model:value=\"config.chunking.preserveHeadings\" />\n              </n-form-item>\n              <n-form-item label=\"保留代码块\">\n                <n-switch v-model:value=\"config.chunking.preserveCodeBlocks\" />\n              </n-form-item>\n            </n-form>\n          </n-card>\n        </n-tab-pane>\n\n        <!-- Search Settings -->\n        <n-tab-pane name=\"search\" tab=\"搜索设置\">\n          <n-card>\n            <n-form label-placement=\"left\" label-width=\"140\">\n              <n-form-item label=\"默认返回数量\">\n                <n-input-number v-model:value=\"config.search.defaultTopK\" :min=\"1\" :max=\"100\" style=\"width: 100%\" />\n              </n-form-item>\n              <n-form-item label=\"上下文窗口\">\n                <n-input-number v-model:value=\"config.search.contextWindow\" :min=\"0\" :max=\"5\" style=\"width: 100%\" />\n              </n-form-item>\n              <n-form-item label=\"相似度阈值\">\n                <n-slider v-model:value=\"thresholdPercent\" :min=\"0\" :max=\"100\" :step=\"1\" />\n              </n-form-item>\n              <n-form-item label=\"启用 MMR 多样性\">\n                <n-switch v-model:value=\"config.search.enableMmr\" />\n              </n-form-item>\n              <n-form-item v-if=\"config.search.enableMmr\" label=\"MMR Lambda\">\n                <n-slider v-model:value=\"mmrLambdaPercent\" :min=\"0\" :max=\"100\" :step=\"1\" />\n              </n-form-item>\n              <n-form-item label=\"默认搜索源\">\n                <n-select\n                  v-model:value=\"config.search.defaultSources\"\n                  multiple\n                  placeholder=\"全部源\"\n                  clearable\n                  :options=\"sourceNameOptions\"\n                />\n              </n-form-item>\n\n              <n-divider>知识图谱扩展召回</n-divider>\n\n              <n-form-item label=\"启用图扩展\">\n                <n-space align=\"center\">\n                  <n-switch v-model:value=\"config.search.enableGraphExpansion\" />\n                  <n-text depth=\"3\" style=\"font-size: 12px\">\n                    {{ config.search.enableGraphExpansion\n                      ? '检索时自动补充 wiki-link 关联文档（需先完成索引建图）'\n                      : '不使用知识图谱扩展' }}\n                  </n-text>\n                </n-space>\n              </n-form-item>\n\n              <template v-if=\"config.search.enableGraphExpansion\">\n                <n-form-item label=\"遍历深度\">\n                  <n-input-number\n                    v-model:value=\"config.search.graphExpansionDepth\"\n                    :min=\"1\" :max=\"2\" style=\"width: 120px\"\n                  />\n                  <n-text depth=\"3\" style=\"margin-left:8px;font-size:12px\">层（推荐 1）</n-text>\n                </n-form-item>\n                <n-form-item label=\"最大邻居节点数\">\n                  <n-input-number\n                    v-model:value=\"config.search.graphExpansionMaxNodes\"\n                    :min=\"1\" :max=\"10\" style=\"width: 120px\"\n                  />\n                  <n-text depth=\"3\" style=\"margin-left:8px;font-size:12px\">个/结果（推荐 3）</n-text>\n                </n-form-item>\n              </template>\n\n            </n-form>\n          </n-card>\n        </n-tab-pane>\n\n        <!-- Service Settings -->\n        <n-tab-pane name=\"service\" tab=\"服务设置\">\n          <n-card>\n            <n-form label-placement=\"left\" label-width=\"140\">\n              <n-form-item label=\"监听端口\">\n                <n-input-number v-model:value=\"config.service.port\" :min=\"1024\" :max=\"65535\" style=\"width: 200px\" />\n              </n-form-item>\n              <n-form-item label=\"允许外网访问\">\n                <n-space align=\"center\">\n                  <n-switch\n                    v-model:value=\"config.service.allowNetworkAccess\"\n                    @update:value=\"onNetworkAccessChange\"\n                  />\n                  <n-text depth=\"3\" style=\"font-size: 12px\">\n                    {{ config.service.allowNetworkAccess\n                      ? '监听 0.0.0.0（局域网/外网可访问）— 修改后需重启'\n                      : '仅监听 localhost（本机访问）' }}\n                  </n-text>\n                </n-space>\n              </n-form-item>\n              <n-form-item label=\"启用 CORS\">\n                <n-switch v-model:value=\"config.service.enableCors\" />\n              </n-form-item>\n              <n-form-item label=\"启用 Swagger\">\n                <n-switch v-model:value=\"config.service.enableSwagger\" />\n              </n-form-item>\n              <n-form-item label=\"日志级别\">\n                <n-select\n                  v-model:value=\"config.service.logLevel\"\n                  :options=\"logLevelOptions\"\n                  style=\"width: 200px\"\n                />\n              </n-form-item>\n              <n-form-item label=\"API Key\">\n                <n-input-group>\n                  <n-input\n                    v-model:value=\"config.service.apiKey\"\n                    type=\"password\"\n                    show-password-on=\"click\"\n                    placeholder=\"留空则不启用认证\"\n                    style=\"width: 300px\"\n                  />\n                  <n-button\n                    v-if=\"config.service.apiKey\"\n                    type=\"warning\"\n                    style=\"margin-left: 8px\"\n                    @click=\"config.service.apiKey = ''\"\n                  >\n                    清除\n                  </n-button>\n                </n-input-group>\n              </n-form-item>\n              <n-form-item label=\"\">\n                <n-text depth=\"3\" style=\"font-size: 12px\">\n                  {{ config.service.apiKey ? 'API Key 已设置，写操作需要认证' : '未设置 API Key，所有接口无需认证' }}\n                </n-text>\n              </n-form-item>\n            </n-form>\n          </n-card>\n        </n-tab-pane>\n\n        <!-- Rerank Settings -->\n        <n-tab-pane name=\"rerank\" tab=\"重排模型\">\n          <n-card v-if=\"config.rerank\">\n            <n-form label-placement=\"left\" label-width=\"140\">\n              <n-form-item label=\"启用重排\">\n                <n-switch v-model:value=\"config.rerank.enabled\" />\n              </n-form-item>\n\n              <!-- 推理模式切换 -->\n              <n-form-item label=\"推理模式\">\n                <n-radio-group v-model:value=\"config.rerank.mode\" size=\"small\">\n                  <n-radio-button value=\"onnx\">本地 ONNX</n-radio-button>\n                  <n-radio-button value=\"openai\">OpenAI 兼容 API</n-radio-button>\n                </n-radio-group>\n              </n-form-item>\n\n              <!-- ONNX 模式专属字段 -->\n              <template v-if=\"config.rerank.mode !== 'openai'\">\n                <n-form-item label=\"模型名称\">\n                  <n-input v-model:value=\"config.rerank.modelName\" placeholder=\"bge-reranker-base\" />\n                </n-form-item>\n                <n-form-item label=\"当前模型\">\n                  <n-input v-model:value=\"config.rerank.currentModel\" placeholder=\"当前使用的重排模型\" disabled />\n                </n-form-item>\n                <n-form-item label=\"模型路径\">\n                  <n-input v-model:value=\"config.rerank.modelPath\" placeholder=\"重排模型 ONNX 文件路径\" />\n                </n-form-item>\n                <n-form-item label=\"使用 CUDA\">\n                  <n-switch v-model:value=\"config.rerank.useCuda\" :disabled=\"!cudaAvailable\" />\n                </n-form-item>\n                <n-form-item v-if=\"!cudaAvailable\" label=\"\">\n                  <n-text depth=\"3\" style=\"font-size: 12px;color: var(--n-warning-color)\">系统未检测到 CUDA/GPU 支持，重排 CUDA 已禁用</n-text>\n                </n-form-item>\n                <n-form-item v-if=\"config.rerank.useCuda && cudaAvailable\" label=\"CUDA 设备 ID\">\n                  <n-input-number v-model:value=\"config.rerank.cudaDeviceId\" :min=\"0\" :max=\"7\" style=\"width: 200px\" />\n                </n-form-item>\n              </template>\n\n              <!-- API 模式专属字段 -->\n              <template v-else>\n                <n-form-item label=\"API 地址\">\n                  <n-input v-model:value=\"config.rerank.apiBaseUrl\" placeholder=\"http://localhost:11434/v1\" />\n                </n-form-item>\n                <n-form-item label=\"API Key\">\n                  <n-input v-model:value=\"config.rerank.apiKey\" type=\"password\" show-password-on=\"click\" placeholder=\"留空则不发送 Authorization 头\" />\n                </n-form-item>\n                <n-form-item label=\"模型名称\">\n                  <n-input v-model:value=\"config.rerank.modelName\" placeholder=\"jina-reranker-v2-base-multilingual\" />\n                </n-form-item>\n              </template>\n\n              <!-- 两种模式均适用 -->\n              <n-form-item label=\"重排返回数量\">\n                <n-input-number v-model:value=\"config.rerank.topN\" :min=\"1\" :max=\"100\" style=\"width: 100%\" />\n              </n-form-item>\n              <n-form-item label=\"召回倍数\">\n                <n-input-number v-model:value=\"config.rerank.recallFactor\" :min=\"1\" :max=\"10\" style=\"width: 100%\" />\n                <n-text depth=\"3\" style=\"font-size: 12px; margin-left: 8px\">候选文档数 = TopN × 召回倍数</n-text>\n              </n-form-item>\n            </n-form>\n          </n-card>\n          <n-card v-else>\n            <n-text depth=\"3\">重排模型配置未加载</n-text>\n          </n-card>\n        </n-tab-pane>\n\n        <!-- Chat Config -->\n        <n-tab-pane name=\"chat\" tab=\"聊天模型\">\n          <n-card>\n            <n-alert type=\"info\" style=\"margin-bottom: 16px\">\n              配置 AI 对话功能的 LLM 后端。修改后需要重启服务才能生效。\n            </n-alert>\n            <n-form label-placement=\"left\" label-width=\"140\">\n              <n-form-item label=\"API 地址\">\n                <n-input v-model:value=\"chatConfig.endpoint\" placeholder=\"https://api.openai.com/v1\" />\n              </n-form-item>\n              <n-form-item label=\"API Key\">\n                <n-input\n                  v-model:value=\"chatConfig.apiKey\"\n                  type=\"password\"\n                  show-password-on=\"click\"\n                  placeholder=\"your-api-key\"\n                />\n              </n-form-item>\n              <n-form-item label=\"模型名称\">\n                <n-input v-model:value=\"chatConfig.model\" placeholder=\"gpt-4o-mini\" />\n              </n-form-item>\n              <n-form-item label=\"系统提示词\">\n                <n-input\n                  v-model:value=\"chatConfig.systemPrompt\"\n                  type=\"textarea\"\n                  :autosize=\"{ minRows: 3, maxRows: 6 }\"\n                  placeholder=\"你是智能助手...\"\n                />\n              </n-form-item>\n            </n-form>\n          </n-card>\n        </n-tab-pane>\n\n        <!-- Data Path -->\n        <n-tab-pane name=\"data\" tab=\"数据路径\">\n          <n-card>\n            <n-form label-placement=\"left\" label-width=\"140\">\n              <n-form-item label=\"数据存储路径\">\n                <n-input v-model:value=\"config.dataPath\" placeholder=\"data\" />\n              </n-form-item>\n              <n-form-item label=\"模型存储路径\">\n                <n-input v-model:value=\"config.modelsRootPath\" placeholder=\"默认: models\" />\n              </n-form-item>\n            </n-form>\n          </n-card>\n        </n-tab-pane>\n      </n-tabs>\n\n      <!-- Save Button -->\n      <n-space style=\"margin-top: 16px\" justify=\"space-between\" align=\"center\">\n        <n-text depth=\"3\" style=\"font-size: 12px\">\n          部分配置（端口、聊天模型等）需重启服务后生效\n        </n-text>\n        <n-button type=\"primary\" :loading=\"saving\" @click=\"handleSave\">保存配置</n-button>\n      </n-space>\n    </n-spin>\n  </n-space>\n";
export default component;
import {ragFetch as fetch} from '/core/transport/index.js';
