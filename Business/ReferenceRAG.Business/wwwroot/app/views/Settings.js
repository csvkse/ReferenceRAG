import { defineComponent as _defineComponent } from 'vue';
import { ref, computed, onMounted, onActivated, onUnmounted } from 'vue';
import { useMessage } from 'naive-ui';
import { settingsApi, sourcesApi, modelsApi } from '/app/api/index.js';
import { API_URL } from '/app/config/env.js';
import { useAuthStore } from '/app/stores/auth.js';
import { isDesktop, ragFetch as fetch } from '/core/transport/index.js';
const component = /*@__PURE__*/ _defineComponent({
    __name: 'Settings',
    setup(__props, { expose: __expose }) {
        __expose();
        const message = useMessage();
        const authStore = useAuthStore();
        const loading = ref(false);
        const saving = ref(false);
        const downloadedChatModels = ref([]);
        const chatModelLoading = ref(false);
        const chatCatalog = ref([]);
        const embeddingCatalog = ref([]);
        const rerankCatalog = ref([]);
        const catalogsLoading = ref(false);

        const formatCatalogOptions = (groups, emptyText) => {
            if (!groups || groups.length === 0) {
                return [{ label: emptyText, value: '', disabled: true }];
            }
            const formatted = groups
                .filter(g => g.options && g.options.length > 0)
                .map(g => ({
                    type: 'group',
                    label: g.label,
                    key: g.label,
                    children: g.options.map(opt => ({
                        label: opt.details ? `${opt.label} (${opt.details})` : opt.label,
                        value: opt.value,
                        disabled: opt.disabled
                    }))
                }));
            return formatted.length > 0 ? formatted : [{ label: emptyText, value: '', disabled: true }];
        };

        const chatCatalogOptions = computed(() => {
            return formatCatalogOptions(chatCatalog.value, '暂无可用聊天模型 (请前往「模型管理」下载或添加在线服务商)');
        });

        const embeddingCatalogOptions = computed(() => {
            return formatCatalogOptions(embeddingCatalog.value, '暂无可用嵌入模型 (请前往「模型管理」下载或添加在线服务商)');
        });

        const rerankCatalogOptions = computed(() => {
            return formatCatalogOptions(rerankCatalog.value, '暂无可用重排模型 (请前往「模型管理」下载或添加在线服务商)');
        });

        const isLocalChatModel = computed(() => {
            return (config.value?.chat?.activeModelUrn || '').startsWith('local:');
        });

        const isLocalEmbeddingModel = computed(() => {
            return (config.value?.embedding?.activeModelUrn || '').startsWith('local:');
        });

        const isLocalRerankModel = computed(() => {
            return (config.value?.rerank?.activeModelUrn || '').startsWith('local:');
        });

        const onChatModelUrnChange = (urn) => {
            if (!config.value?.chat) return;
            config.value.chat.activeModelUrn = urn;
            if (urn.startsWith('local:')) {
                config.value.chat.activeBackend = 'LocalGguf';
                const modelName = urn.replace(/^local:/, '');
                onLocalChatModelSelect(modelName);
            } else {
                config.value.chat.activeBackend = 'OpenAiApi';
            }
        };

        const onEmbeddingModelUrnChange = (urn) => {
            if (!config.value?.embedding) return;
            config.value.embedding.activeModelUrn = urn;
            if (urn.startsWith('local:')) {
                config.value.embedding.activeBackend = 'LocalOnnx';
                config.value.embedding.mode = 'onnx';
            } else {
                config.value.embedding.activeBackend = 'OpenAiApi';
                config.value.embedding.mode = 'openai';
            }
        };

        const onRerankModelUrnChange = (urn) => {
            if (!config.value?.rerank) return;
            config.value.rerank.activeModelUrn = urn;
            if (urn.startsWith('local:')) {
                config.value.rerank.activeBackend = 'LocalOnnx';
                config.value.rerank.mode = 'onnx';
            } else {
                config.value.rerank.activeBackend = 'OpenAiApi';
                config.value.rerank.mode = 'openai';
            }
        };

        const loadCatalogs = async () => {
            catalogsLoading.value = true;
            try {
                const [chatRes, embRes, rerankRes] = await Promise.all([
                    modelsApi.getCatalog('chat').catch(() => ({ data: { groups: [] } })),
                    modelsApi.getCatalog('embedding').catch(() => ({ data: { groups: [] } })),
                    modelsApi.getCatalog('rerank').catch(() => ({ data: { groups: [] } }))
                ]);
                chatCatalog.value = chatRes.data?.groups || [];
                embeddingCatalog.value = embRes.data?.groups || [];
                rerankCatalog.value = rerankRes.data?.groups || [];
            } catch (e) {
                console.warn('[Settings] 获取模型 Catalog 失败:', e);
            } finally {
                catalogsLoading.value = false;
            }
        };

        const goToModelManagement = (tab = 'chat') => {
            window.location.href = isDesktop ? `#/models?tab=${tab}` : `/models?tab=${tab}`;
        };

        const ensureValidChatModelSelection = () => {
            if (!config.value?.chat) return;
            if (!config.value.chat.activeModelUrn) {
                if (downloadedChatModels.value.length > 0) {
                    onChatModelUrnChange(`local:${downloadedChatModels.value[0].name}`);
                }
            } else if (config.value.chat.activeModelUrn.startsWith('local:')) {
                const localName = config.value.chat.activeModelUrn.replace(/^local:/, '');
                if (downloadedChatModels.value.length > 0) {
                    const exists = downloadedChatModels.value.some(m => m.name === localName);
                    if (!exists) {
                        onChatModelUrnChange(`local:${downloadedChatModels.value[0].name}`);
                    }
                }
            }
        };

        const loadDownloadedChatModels = async () => {
            chatModelLoading.value = true;
            try {
                const res = await modelsApi.getDownloadedChatModels();
                downloadedChatModels.value = res.data || [];
                ensureValidChatModelSelection();
            } catch (e) {
                console.warn('[Settings] 获取已下载聊天模型失败', e);
            } finally {
                chatModelLoading.value = false;
            }
        };
        const chatModelOptions = computed(() => {
            const list = downloadedChatModels.value || [];
            if (list.length === 0) {
                return [{
                    label: '暂无已下载模型 (请前往「模型管理」下载)',
                    value: '',
                    disabled: true
                }];
            }
            return list.map(m => ({
                label: `${m.displayName || m.name} (${(m.format || 'gguf').toUpperCase()})`,
                value: m.name
            }));
        });
        const onLocalChatModelSelect = (selectedModelName) => {
            const model = (downloadedChatModels.value || []).find(m => m.name === selectedModelName);
            if (model && config.value?.chat?.localGguf) {
                config.value.chat.localGguf.modelName = model.name;
                config.value.chat.localGguf.modelPath = model.localPath || '';
                const activeVar = (model.installedVariants || []).find(v => v.isActive) || model.installedVariants?.[0];
                if (activeVar) {
                    config.value.chat.localGguf.variant = activeVar.fileName;
                }
                updateChatPerformancePreview();
            }
        };
        const chatConfig = ref({
            apiKeyConfigured: false
        });
        const loadChatConfig = () => {
            if (config.value?.chat?.openAiApi) {
                chatConfig.value.apiKeyConfigured = !!config.value.chat.openAiApi.apiKeyConfigured;
            }
        };
        const handleSaveChat = async () => {};

        // 聊天模型自适应规划与实时性能画像
        const chatPerformancePreview = ref({
            contextSize: 8192,
            batchSize: 2048,
            uBatchSize: 512,
            gpuLayers: -1,
            estimatedVramBytes: 0,
            estimatedVramText: '计算中...',
            rationale: '自适应配置 8192 (8K) 黄金平衡上下文与 2048 批大小',
            warning: null
        });

        let chatPreviewTimer = null;
        const updateChatPerformancePreview = () => {
            if (chatPreviewTimer) clearTimeout(chatPreviewTimer);
            chatPreviewTimer = setTimeout(async () => {
                try {
                    const lg = config.value?.chat?.localGguf;
                    if (!lg) return;
                    const res = await settingsApi.previewChatPerformance({
                        modelName: lg.modelName,
                        modelPath: lg.modelPath,
                        performanceMode: lg.performanceMode || 'Adaptive',
                        adaptiveStrategy: lg.adaptiveStrategy || 'Balanced',
                        userContextSize: (lg.contextSize || 0) > 0 ? lg.contextSize : null,
                        userGpuLayers: (lg.gpuLayers !== undefined && lg.gpuLayers >= 0) ? lg.gpuLayers : null,
                        userBatchSize: (lg.batchSize || 0) > 0 ? lg.batchSize : null,
                        userUBatchSize: (lg.uBatchSize || 0) > 0 ? lg.uBatchSize : null,
                        useGpu: lg.acceleration?.backend !== 'Cpu'
                    });
                    if (res?.data) {
                        const p = res.data;
                        const vramText = p.estimatedVramBytes > 0
                            ? (p.estimatedVramBytes / (1024 * 1024 * 1024)).toFixed(2) + ' GB'
                            : '纯内存运算 (CPU)';
                        chatPerformancePreview.value = {
                            contextSize: p.contextSize,
                            batchSize: p.batchSize,
                            uBatchSize: p.uBatchSize,
                            gpuLayers: p.gpuLayers,
                            estimatedVramBytes: p.estimatedVramBytes,
                            estimatedVramText: vramText,
                            rationale: p.rationale,
                            warning: p.warning
                        };
                    }
                } catch (e) {
                    console.error('[Settings] updateChatPerformancePreview error', e);
                }
            }, 80);
        };
        const sourceNames = ref([]);
        const cudaAvailable = ref(false);
        const hardwareProbeLoading = ref(false);
        const hardwareProbeError = ref(null);
        const isNvidiaGpu = computed(() => {
            if (!hardwareStatus.value) return false;
            if (hardwareStatus.value.hasNvidiaGpu) return true;
            const primary = hardwareStatus.value.primaryGpu;
            if (!primary) return false;
            const vendor = (primary.vendor || '').toLowerCase();
            const name = (primary.name || '').toLowerCase();
            return vendor.includes('nvidia') ||
                   name.includes('nvidia') ||
                   name.includes('geforce') ||
                   name.includes('rtx') ||
                   name.includes('gtx') ||
                   name.includes('quadro') ||
                   name.includes('tesla');
        });
        const shouldShowCudaStatus = computed(() => {
            return isNvidiaGpu.value;
        });
        const cudaInfo = ref(null);
        const hardwareStatus = ref(null);
        const backendOptions = [
            { label: '🚀 自动感知协商 (Auto - 智能推荐，平滑降级)', value: 'Auto' },
            { label: '🟢 NVIDIA CUDA (RTX/GTX 满血版)', value: 'Cuda' },
            { label: '🔵 Intel OpenVINO (Arc独显 / 酷睿核显)', value: 'OpenVino' },
            { label: '🟣 通用 DirectML (AMD/Intel/通用版)', value: 'DirectML' },
            { label: '⚪ 纯 CPU 运算 (兼容兜底)', value: 'Cpu' }
        ];
        const originalModelsRootPath = ref('models');
        const defaultConfig = {
            dataPath: 'data',
            sources: [],
            embedding: {
                activeModelUrn: 'local:bge-small-zh-v1.5',
                activeBackend: 'LocalOnnx',
                mode: 'onnx',
                modelPath: '',
                modelName: 'bge-small-zh-v1.5',
                localOnnx: {
                    modelName: 'bge-small-zh-v1.5',
                    modelPath: '',
                    maxSequenceLength: 512,
                    batchSize: 32,
                    acceleration: {
                        backend: 'Auto',
                        deviceId: 0,
                        customLibraryPath: '',
                        fallbackToCpuOnFailure: true
                    }
                },
                openAiApi: {
                    baseUrl: '',
                    apiKey: '',
                    modelName: 'text-embedding-3-small',
                    dimension: null
                },
                acceleration: {
                    backend: 'Auto',
                    deviceId: 0,
                    customLibraryPath: '',
                    fallbackToCpuOnFailure: true
                },
                useCuda: true,
                cudaDeviceId: 0,
                cudaLibraryPath: '',
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
                preserveCodeBlocks: true,
                mode: 'Adaptive',
                adaptiveLevel: 'Level2',
                adaptiveStrategyType: 'Balanced',
                targetWindowUtilizationPercent: 70,
                adaptiveOverlapPercent: 10
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
                graphExpansionMaxNodes: 3,
                enableAutomaticFallback: true,
                embeddingTimeoutMs: 2500,
                circuitBreakerThreshold: 3,
                circuitBreakerDurationSeconds: 30,
                enableDppDiversity: true,
                enableStatisticalPrf: true,
                prfMaxExpansionTerms: 3,
                cognitiveLexical: {
                    backend: 'Disabled',
                    maxHops: 2,
                    acceptanceThreshold: 0.50,
                    earlyExitThreshold: 0.85,
                    batchSize: 10,
                    enableFallback: true
                }
            },
            service: {
                port: 5000,
                host: 'localhost',
                allowNetworkAccess: false,
                enableHttpService: false,
                enableCors: true,
                enableSwagger: true,
                logLevel: 'Information',
                apiKey: ''
            },
            rerank: {
                activeModelUrn: 'local:bge-reranker-base',
                activeBackend: 'LocalOnnx',
                mode: 'onnx',
                enabled: false,
                modelName: 'bge-reranker-base',
                currentModel: '',
                modelPath: '',
                localOnnx: {
                    modelName: 'bge-reranker-base',
                    currentModel: '',
                    modelPath: '',
                    acceleration: {
                        backend: 'Auto',
                        deviceId: 0,
                        customLibraryPath: '',
                        fallbackToCpuOnFailure: true
                    }
                },
                openAiApi: {
                    baseUrl: '',
                    apiKey: '',
                    modelName: 'bge-reranker-v2-m3'
                },
                acceleration: {
                    backend: 'Auto',
                    deviceId: 0,
                    customLibraryPath: '',
                    fallbackToCpuOnFailure: true
                },
                useCuda: true,
                cudaDeviceId: 0,
                cudaLibraryPath: '',
                topN: 10,
                recallFactor: 3,
                apiBaseUrl: '',
                apiKey: ''
            },
            modelHub: {
                hfToken: '',
                hfEndpoint: 'https://hf-mirror.com',
                timeoutSeconds: 30
            },
            modelRetentionStrategy: 'SlidingExpiration',
            modelIdleTimeoutSeconds: 300,
            unloadOnlyWhenMinimizedToTray: true,
            network: {
                proxy: {
                    enabled: false,
                    proxyUrl: '',
                    bypassHosts: 'localhost;127.0.0.1;*.local;api.siliconflow.cn;dashscope.aliyuncs.com',
                    username: '',
                    password: ''
                }
            },
            systemOne: {
                enabled: true,
                baseUrl: 'https://api.typesafe.ai/v1/systemone',
                apiKey: '',
                apiKeyConfigured: false,
                model: 'jev-latest',
                timeoutMs: 5000,
                retrievalConfidenceThreshold: 0.65,
                proxyPolicy: 'Inherit',
                customProxyUrl: ''
            },
            chat: {
                activeModelUrn: 'local:qwen2.5-7b-instruct-q4_k_m',
                activeBackend: 'LocalGguf',
                enableIntentRouting: true,
                systemPrompt: '你是 ReferenceRAG 智能助手。',
                temperature: 0.7,
                topP: 0.9,
                repeatPenalty: 1.1,
                frequencyPenalty: 0.2,
                presencePenalty: 0.1,
                penaltyCount: 128,
                enableRepetitionCircuitBreaker: true,
                idleTimeoutSeconds: 300,
                localGguf: {
                    modelName: 'Qwen2.5-7B-Instruct-GGUF',
                    variant: '',
                    modelPath: '',
                    performanceMode: 'Adaptive',
                    adaptiveStrategy: 'Balanced',
                    gpuLayers: -1,
                    contextSize: 0,
                    batchSize: 2048,
                    uBatchSize: 512,
                    acceleration: {
                        backend: 'Auto',
                        deviceId: 0,
                        customLibraryPath: '',
                        fallbackToCpuOnFailure: true
                    }
                },
                openAiApi: {
                    endpoint: 'https://api.openai.com/v1',
                    apiKey: '',
                    model: 'gpt-4o-mini',
                    timeoutSeconds: 60
                }
            }
        };
        const config = ref(JSON.parse(JSON.stringify(defaultConfig)));
        const verifyingHfToken = ref(false);
        const hfTokenStatus = ref(null);
        const hfEndpointOptions = [
            { label: '国内高速镜像 (https://hf-mirror.com - 推荐，无需代理)', value: 'https://hf-mirror.com' },
            { label: 'HuggingFace 官方 (https://huggingface.co - 需国际网络畅通)', value: 'https://huggingface.co' }
        ];
        const handleVerifyHfToken = async () => {
            const token = config.value.modelHub?.hfToken?.trim();
            if (!token) {
                message.warning('请先输入 HuggingFace 访问令牌 (HF_TOKEN)');
                return;
            }
            verifyingHfToken.value = true;
            hfTokenStatus.value = null;
            try {
                const endpoint = config.value.modelHub?.hfEndpoint || 'https://hf-mirror.com';
                const response = await settingsApi.verifyHfToken(token, endpoint);
                const data = response.data;
                if (data.valid) {
                    hfTokenStatus.value = {
                        type: 'success',
                        message: data.message || `Token 验证成功！用户: ${data.username} (${data.role})`
                    };
                    message.success(hfTokenStatus.value.message);
                } else {
                    hfTokenStatus.value = {
                        type: 'error',
                        message: data.message || 'Token 验证失败，请检查 Token 是否有效或是否包含读取权限'
                    };
                    message.error(hfTokenStatus.value.message);
                }
            } catch (err) {
                hfTokenStatus.value = {
                    type: 'error',
                    message: `请求失败: ${err.response?.data?.message || err.message}`
                };
                message.error(hfTokenStatus.value.message);
            } finally {
                verifyingHfToken.value = false;
            }
        };
        const testingSystemOne = ref(false);
        const systemOneTestResult = ref(null);
        const systemOneTestQuery = ref('帮我查一下知识库里的技术架构设计规范');
        const handleTestSystemOne = async () => {
            testingSystemOne.value = true;
            systemOneTestResult.value = null;
            try {
                const res = await settingsApi.testSystemOne({
                    baseUrl: config.value.systemOne?.baseUrl,
                    apiKey: config.value.systemOne?.apiKey,
                    model: config.value.systemOne?.model,
                    query: systemOneTestQuery.value,
                    threshold: config.value.systemOne?.retrievalConfidenceThreshold,
                    proxyPolicy: config.value.systemOne?.proxyPolicy,
                    customProxyUrl: config.value.systemOne?.customProxyUrl
                });
                systemOneTestResult.value = res.data;
                if (res.data?.success) {
                    message.success('System One 连接测试成功');
                } else {
                    message.warning(res.data?.error || res.data?.message || '测试未成功');
                }
            } catch (err) {
                message.error(err?.response?.data?.message || err.message || '网络连接失败');
                systemOneTestResult.value = { success: false, error: err.message };
            } finally {
                testingSystemOne.value = false;
            }
        };
        const proxyPolicyOptions = [
            { label: '🌐 继承统一全局代理 (推荐)', value: 'Inherit' },
            { label: '⚡ 强制直连 (不走代理)', value: 'Direct' },
            { label: '🎯 自定义专属代理', value: 'Custom' }
        ];
        const testingProxy = ref(false);
        const proxyTestResult = ref(null);
        const handleTestProxy = async () => {
            if (!config.value.network?.proxy?.proxyUrl) {
                message.warning('请输入代理服务器地址，如 http://127.0.0.1:7890');
                return;
            }
            testingProxy.value = true;
            proxyTestResult.value = null;
            try {
                const res = await settingsApi.testProxy({
                    proxyUrl: config.value.network.proxy.proxyUrl,
                    username: config.value.network.proxy.username,
                    password: config.value.network.proxy.password
                });
                proxyTestResult.value = res.data;
                if (res.data?.success) {
                    message.success(res.data.message || '代理连接成功');
                } else {
                    message.error('代理测试失败: ' + (res.data?.error || '未知错误'));
                }
            } catch (err) {
                message.error('代理测试失败: ' + (err?.response?.data?.message || err.message));
                proxyTestResult.value = { success: false, error: err.message };
            } finally {
                testingProxy.value = false;
            }
        };
        const testingCognitiveBackend = ref(false);
        const cognitiveBackendTestResult = ref(null);
        const handleTestCognitiveBackend = async () => {
            testingCognitiveBackend.value = true;
            cognitiveBackendTestResult.value = null;
            try {
                const res = await settingsApi.testCognitiveBackend({
                    backend: config.value.search?.cognitiveLexical?.backend || 'Disabled',
                    acceptanceThreshold: config.value.search?.cognitiveLexical?.acceptanceThreshold || 0.50
                });
                cognitiveBackendTestResult.value = res.data;
                if (res.data?.success) {
                    message.success(res.data.message || '认知后端测试通过');
                } else {
                    message.warning(res.data?.message || '认知后端测试未通过');
                }
            } catch (err) {
                message.error('测试认知后端失败: ' + (err?.response?.data?.message || err.message));
                cognitiveBackendTestResult.value = { success: false, message: err.message };
            } finally {
                testingCognitiveBackend.value = false;
            }
        };
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
        const adaptiveLevelOptions = [
            { label: 'Level 1: 模型感知对齐（根据当前模型最大长度动态缩放，100% 杜绝硬截断）', value: 'Level1' },
            { label: 'Level 2: 结构句法自适应（推荐！保护代码块/表格/列表完整性，短节智能聚合）', value: 'Level2' },
            { label: 'Level 3: 深度语义内聚（结合语义信息密度与上下文自然标点断点自适应）', value: 'Level3' }
        ];
        const adaptivePreview = computed(() => {
            const modelName = (config.value.embedding?.modelName || 'bge-small-zh-v1.5').toLowerCase();
            const isLong = modelName.includes('bge-m3') || modelName.includes('jina') || modelName.includes('text-embedding-3') || ((config.value.embedding?.maxSequenceLength || 0) >= 4096);
            const physicalMax = isLong ? 8192 : (config.value.embedding?.maxSequenceLength || 512);
            const strategy = config.value.chunking?.adaptiveStrategyType || 'Balanced';
            const util = (config.value.chunking?.targetWindowUtilizationPercent || 70) / 100;
            const overlapPct = (config.value.chunking?.adaptiveOverlapPercent ?? 10) / 100;

            let targetTokens = 360;
            let overlapTokens = 36;
            let spaceSaving = '约 15% ~ 25%';

            if (strategy === 'PrecisionDense') {
                targetTokens = isLong ? 500 : Math.round(physicalMax * 0.45);
                overlapTokens = Math.round(targetTokens * 0.06);
                spaceSaving = '高精度微调 (+10% ~ -10%)';
            } else if (strategy === 'DeepNarrative') {
                targetTokens = isLong ? 3000 : Math.round(physicalMax * 0.82);
                overlapTokens = Math.round(targetTokens * 0.15);
                spaceSaving = isLong ? '约 70% ~ 80%' : '约 30% ~ 45%';
            } else if (strategy === 'CodeFirst') {
                targetTokens = isLong ? 1200 : Math.round(physicalMax * 0.72);
                overlapTokens = 0;
                spaceSaving = '约 20% ~ 30% (代码 0% 重叠)';
            } else {
                targetTokens = isLong ? Math.round(physicalMax * 0.20 * (util / 0.70)) : Math.round(physicalMax * util);
                overlapTokens = Math.round(targetTokens * overlapPct);
                spaceSaving = isLong ? '约 60% ~ 75%' : '约 15% ~ 25%';
            }

            return {
                modelName: config.value.embedding?.modelName || 'bge-small-zh-v1.5',
                physicalMax,
                targetTokens,
                minTokens: Math.max(30, Math.round(targetTokens * 0.15)),
                overlapTokens,
                spaceSaving
            };
        });
        const loadConfig = async () => {
            loading.value = true;
            try {
                const response = await settingsApi.get();
                const data = response.data;
                config.value = { ...JSON.parse(JSON.stringify(defaultConfig)), ...data };
                if (!config.value.embedding) {
                    config.value.embedding = { ...defaultConfig.embedding };
                }
                if (!config.value.embedding.acceleration) {
                    config.value.embedding.acceleration = {
                        backend: config.value.embedding.useCuda ? 'Auto' : 'Cpu',
                        deviceId: config.value.embedding.cudaDeviceId || 0,
                        customLibraryPath: config.value.embedding.cudaLibraryPath || '',
                        fallbackToCpuOnFailure: true
                    };
                } else {
                    if (!config.value.embedding.acceleration.backend) {
                        config.value.embedding.acceleration.backend = config.value.embedding.useCuda ? 'Auto' : 'Cpu';
                    }
                    if (config.value.embedding.acceleration.deviceId === undefined) {
                        config.value.embedding.acceleration.deviceId = config.value.embedding.cudaDeviceId || 0;
                    }
                    if (config.value.embedding.acceleration.customLibraryPath === undefined) {
                        config.value.embedding.acceleration.customLibraryPath = config.value.embedding.cudaLibraryPath || '';
                    }
                    if (config.value.embedding.acceleration.fallbackToCpuOnFailure === undefined) {
                        config.value.embedding.acceleration.fallbackToCpuOnFailure = true;
                    }
                }
                if (!config.value.embedding.localOnnx) {
                    config.value.embedding.localOnnx = {
                        modelName: config.value.embedding.modelName || 'bge-small-zh-v1.5',
                        modelPath: config.value.embedding.modelPath || '',
                        maxSequenceLength: config.value.embedding.maxSequenceLength || 512,
                        batchSize: config.value.embedding.batchSize || 32,
                        acceleration: config.value.embedding.acceleration
                    };
                }
                if (!config.value.embedding.openAiApi) {
                    config.value.embedding.openAiApi = {
                        baseUrl: config.value.embedding.apiBaseUrl || '',
                        apiKey: config.value.embedding.apiKey || '',
                        modelName: config.value.embedding.mode === 'openai' ? (config.value.embedding.modelName || 'text-embedding-3-small') : 'text-embedding-3-small',
                        dimension: config.value.embedding.apiDimension || null
                    };
                }
                if (config.value.rerank) {
                    if (!config.value.rerank.acceleration) {
                        config.value.rerank.acceleration = {
                            backend: config.value.rerank.useCuda ? 'Auto' : 'Cpu',
                            deviceId: config.value.rerank.cudaDeviceId || 0,
                            customLibraryPath: config.value.rerank.cudaLibraryPath || '',
                            fallbackToCpuOnFailure: true
                        };
                    } else {
                        if (!config.value.rerank.acceleration.backend) {
                            config.value.rerank.acceleration.backend = config.value.rerank.useCuda ? 'Auto' : 'Cpu';
                        }
                        if (config.value.rerank.acceleration.deviceId === undefined) {
                            config.value.rerank.acceleration.deviceId = config.value.rerank.cudaDeviceId || 0;
                        }
                        if (config.value.rerank.acceleration.customLibraryPath === undefined) {
                            config.value.rerank.acceleration.customLibraryPath = config.value.rerank.cudaLibraryPath || '';
                        }
                        if (config.value.rerank.acceleration.fallbackToCpuOnFailure === undefined) {
                            config.value.rerank.acceleration.fallbackToCpuOnFailure = true;
                        }
                    }
                    if (!config.value.rerank.localOnnx) {
                        config.value.rerank.localOnnx = {
                            modelName: config.value.rerank.modelName || 'bge-reranker-base',
                            currentModel: config.value.rerank.currentModel || '',
                            modelPath: config.value.rerank.modelPath || '',
                            acceleration: config.value.rerank.acceleration
                        };
                    }
                    if (!config.value.rerank.openAiApi) {
                        config.value.rerank.openAiApi = {
                            baseUrl: config.value.rerank.apiBaseUrl || '',
                            apiKey: config.value.rerank.apiKey || '',
                            modelName: config.value.rerank.mode === 'openai' ? (config.value.rerank.modelName || 'bge-reranker-v2-m3') : 'bge-reranker-v2-m3'
                        };
                    }
                }
                if (!config.value.chunking) {
                    config.value.chunking = { ...defaultConfig.chunking };
                } else {
                    if (!config.value.chunking.mode) config.value.chunking.mode = 'Adaptive';
                    if (!config.value.chunking.adaptiveLevel) config.value.chunking.adaptiveLevel = 'Level2';
                    if (!config.value.chunking.adaptiveStrategyType) config.value.chunking.adaptiveStrategyType = 'Balanced';
                    if (config.value.chunking.targetWindowUtilizationPercent === undefined) config.value.chunking.targetWindowUtilizationPercent = 70;
                    if (config.value.chunking.adaptiveOverlapPercent === undefined) config.value.chunking.adaptiveOverlapPercent = 10;
                }
                if (!config.value.modelHub) {
                    config.value.modelHub = { hfToken: '', hfEndpoint: 'https://hf-mirror.com', timeoutSeconds: 30 };
                }
                if (!config.value.modelsRootPath) {
                    config.value.modelsRootPath = 'models';
                }
                if (config.value.chat && !config.value.chat.activeModelUrn) {
                    if (config.value.chat.activeBackend === 'OpenAiApi' && config.value.chat.openAiApi?.model) {
                        config.value.chat.activeModelUrn = `provider:openai:${config.value.chat.openAiApi.model}`;
                    } else if (config.value.chat.localGguf?.modelName) {
                        config.value.chat.activeModelUrn = `local:${config.value.chat.localGguf.modelName}`;
                    } else {
                        config.value.chat.activeModelUrn = 'local:qwen2.5-7b-instruct-q4_k_m';
                    }
                }
                if (config.value.embedding && !config.value.embedding.activeModelUrn) {
                    if (config.value.embedding.mode === 'openai' && config.value.embedding.openAiApi?.modelName) {
                        config.value.embedding.activeModelUrn = `provider:openai:${config.value.embedding.openAiApi.modelName}`;
                    } else if (config.value.embedding.localOnnx?.modelName || config.value.embedding.modelName) {
                        config.value.embedding.activeModelUrn = `local:${config.value.embedding.localOnnx?.modelName || config.value.embedding.modelName}`;
                    } else {
                        config.value.embedding.activeModelUrn = 'local:bge-small-zh-v1.5';
                    }
                }
                if (config.value.rerank && !config.value.rerank.activeModelUrn) {
                    if (config.value.rerank.mode === 'openai' && config.value.rerank.openAiApi?.modelName) {
                        config.value.rerank.activeModelUrn = `provider:openai:${config.value.rerank.openAiApi.modelName}`;
                    } else if (config.value.rerank.localOnnx?.modelName || config.value.rerank.modelName) {
                        config.value.rerank.activeModelUrn = `local:${config.value.rerank.localOnnx?.modelName || config.value.rerank.modelName}`;
                    } else {
                        config.value.rerank.activeModelUrn = 'local:bge-reranker-base';
                    }
                }
                originalModelsRootPath.value = config.value.modelsRootPath;
                if (!config.value.chat) {
                    config.value.chat = JSON.parse(JSON.stringify(defaultConfig.chat));
                } else {
                    if (!config.value.chat.activeBackend) {
                        config.value.chat.activeBackend = 'OpenAiApi';
                    }
                    if (!config.value.chat.localGguf) {
                        config.value.chat.localGguf = JSON.parse(JSON.stringify(defaultConfig.chat.localGguf));
                    } else {
                        if (!config.value.chat.localGguf.performanceMode) {
                            config.value.chat.localGguf.performanceMode = (config.value.chat.localGguf.contextSize > 0) ? 'Fixed' : 'Adaptive';
                        }
                        if (!config.value.chat.localGguf.adaptiveStrategy) {
                            config.value.chat.localGguf.adaptiveStrategy = 'Balanced';
                        }
                        if (config.value.chat.localGguf.gpuLayers === undefined) {
                            config.value.chat.localGguf.gpuLayers = -1;
                        }
                        if (!config.value.chat.localGguf.contextSize && config.value.chat.localGguf.contextSize !== 0) {
                            config.value.chat.localGguf.contextSize = 8192;
                        }
                        if (!config.value.chat.localGguf.batchSize && config.value.chat.localGguf.batchSize !== 0) {
                            config.value.chat.localGguf.batchSize = 2048;
                        }
                        if (!config.value.chat.localGguf.uBatchSize && config.value.chat.localGguf.uBatchSize !== 0) {
                            config.value.chat.localGguf.uBatchSize = 512;
                        }
                    }
                    if (!config.value.chat.openAiApi) {
                        config.value.chat.openAiApi = JSON.parse(JSON.stringify(defaultConfig.chat.openAiApi));
                    }
                    if (!config.value.chat.localGguf.acceleration) {
                        config.value.chat.localGguf.acceleration = {
                            backend: 'Auto',
                            deviceId: 0,
                            customLibraryPath: '',
                            fallbackToCpuOnFailure: true
                        };
                    }
                    if (config.value.chat.enableIntentRouting === undefined) {
                        config.value.chat.enableIntentRouting = true;
                    }
                    if (config.value.chat.idleTimeoutSeconds === undefined) {
                        config.value.chat.idleTimeoutSeconds = 300;
                    }
                    if (config.value.chat.temperature === undefined) {
                        config.value.chat.temperature = 0.7;
                    }
                    if (config.value.chat.topP === undefined) {
                        config.value.chat.topP = 0.9;
                    }
                    if (config.value.chat.repeatPenalty === undefined) {
                        config.value.chat.repeatPenalty = 1.1;
                    }
                    if (config.value.chat.frequencyPenalty === undefined) {
                        config.value.chat.frequencyPenalty = 0.2;
                    }
                    if (config.value.chat.presencePenalty === undefined) {
                        config.value.chat.presencePenalty = 0.1;
                    }
                    if (config.value.chat.penaltyCount === undefined) {
                        config.value.chat.penaltyCount = 128;
                    }
                    if (config.value.chat.enableRepetitionCircuitBreaker === undefined) {
                        config.value.chat.enableRepetitionCircuitBreaker = true;
                    }
                }
                if (config.value.chat?.openAiApi) {
                    chatConfig.value.apiKeyConfigured = !!config.value.chat.openAiApi.apiKeyConfigured;
                }
                if (!config.value.modelRetentionStrategy) {
                    config.value.modelRetentionStrategy = 'SlidingExpiration';
                } else if (config.value.modelRetentionStrategy === 0) {
                    config.value.modelRetentionStrategy = 'Permanent';
                } else if (config.value.modelRetentionStrategy === 1) {
                    config.value.modelRetentionStrategy = 'SlidingExpiration';
                }
                if (!config.value.modelIdleTimeoutSeconds && config.value.modelIdleTimeoutSeconds !== 0) {
                    config.value.modelIdleTimeoutSeconds = 300;
                }
                if (config.value.unloadOnlyWhenMinimizedToTray === undefined) {
                    config.value.unloadOnlyWhenMinimizedToTray = true;
                }
                if (!config.value.network) {
                    config.value.network = JSON.parse(JSON.stringify(defaultConfig.network));
                }
                if (!config.value.network.proxy) {
                    config.value.network.proxy = JSON.parse(JSON.stringify(defaultConfig.network.proxy));
                }
                if (!config.value.systemOne) {
                    config.value.systemOne = JSON.parse(JSON.stringify(defaultConfig.systemOne));
                }
                if (config.value.systemOne.enabled === undefined) config.value.systemOne.enabled = true;
                if (!config.value.systemOne.baseUrl) config.value.systemOne.baseUrl = 'https://api.typesafe.ai/v1/systemone';
                if (!config.value.systemOne.model) config.value.systemOne.model = 'jev-latest';
                if (!config.value.systemOne.timeoutMs) config.value.systemOne.timeoutMs = 5000;
                if (!config.value.systemOne.retrievalConfidenceThreshold) config.value.systemOne.retrievalConfidenceThreshold = 0.65;
                if (!config.value.systemOne.proxyPolicy) config.value.systemOne.proxyPolicy = 'Inherit';
                if (config.value.systemOne.customProxyUrl === undefined) config.value.systemOne.customProxyUrl = '';

                if (!config.value.search) {
                    config.value.search = JSON.parse(JSON.stringify(defaultConfig.search));
                }
                if (config.value.search.circuitBreakerThreshold === undefined) config.value.search.circuitBreakerThreshold = 3;
                if (config.value.search.circuitBreakerDurationSeconds === undefined) config.value.search.circuitBreakerDurationSeconds = 30;
                if (config.value.search.enableDppDiversity === undefined) config.value.search.enableDppDiversity = true;
                if (config.value.search.enableStatisticalPrf === undefined) config.value.search.enableStatisticalPrf = true;
                if (config.value.search.prfMaxExpansionTerms === undefined) config.value.search.prfMaxExpansionTerms = 3;
                if (config.value.search.enableAutomaticFallback === undefined) config.value.search.enableAutomaticFallback = true;
                if (config.value.search.embeddingTimeoutMs === undefined) config.value.search.embeddingTimeoutMs = 2500;
                if (!config.value.search.cognitiveLexical) {
                    config.value.search.cognitiveLexical = JSON.parse(JSON.stringify(defaultConfig.search.cognitiveLexical));
                }
                if (config.value.search.cognitiveLexical.backend === undefined) config.value.search.cognitiveLexical.backend = 'Disabled';
                if (config.value.search.cognitiveLexical.maxHops === undefined) config.value.search.cognitiveLexical.maxHops = 2;
                if (config.value.search.cognitiveLexical.acceptanceThreshold === undefined) config.value.search.cognitiveLexical.acceptanceThreshold = 0.50;
                if (config.value.search.cognitiveLexical.earlyExitThreshold === undefined) config.value.search.cognitiveLexical.earlyExitThreshold = 0.85;
                if (config.value.search.cognitiveLexical.enableFallback === undefined) config.value.search.cognitiveLexical.enableFallback = true;
                ensureValidChatModelSelection();
            }
            catch (error) {
                message.error('加载配置失败，使用默认值');
            }
            finally {
                loading.value = false;
                updateChatPerformancePreview();
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
        const loadHardwareStatus = async () => {
            hardwareProbeLoading.value = true;
            hardwareProbeError.value = null;
            try {
                const response = await settingsApi.getHardwareAccelerationStatus();
                const data = response.data;
                hardwareStatus.value = data;
                cudaAvailable.value = !!(data.hasNvidiaGpu && data.availableBackends?.some(b => b.backend === 'Cuda' && b.supported));
                if (data.hasNvidiaGpu) {
                    cudaInfo.value = {
                        isAvailable: cudaAvailable.value,
                        message: data.message || 'NVIDIA CUDA 探针就绪'
                    };
                } else {
                    cudaInfo.value = null; // 非 NV 显卡绝对不显示 CUDA 状态
                }
            }
            catch (err) {
                console.warn('[Settings] 硬件加速状态探测异常:', err);
                const detail = err?.response?.data?.message || err?.message || '探测接口异常';
                hardwareProbeError.value = '硬件加速探针响应异常 (' + detail + ')';
                cudaAvailable.value = false;
                cudaInfo.value = null; // 探针异常绝不作为 CUDA 状态显示
            }
            finally {
                hardwareProbeLoading.value = false;
            }
        };
        const loadCudaAvailability = loadHardwareStatus;
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
                if (config.value.chat?.activeModelUrn?.startsWith('local:')) {
                    const localName = config.value.chat.activeModelUrn.replace(/^local:/, '');
                    const isDownloaded = (downloadedChatModels.value || []).some(m => m.name === localName);
                    if (localName && isDownloaded) {
                        try {
                            await modelsApi.switchChatModel(localName);
                        } catch (e) {
                            console.warn('[Settings] 切换本地聊天模型提示:', e);
                        }
                    }
                }
                const modelsPath = config.value.modelsRootPath?.trim();
                const modelsPathChanged = normalizeModelsRootPath(modelsPath) !== normalizeModelsRootPath(originalModelsRootPath.value);
                if (modelsPathChanged && modelsPath) {
                    await settingsApi.updateModelsPath(modelsPath);
                }
                if (config.value.embedding?.acceleration) {
                    config.value.embedding.useCuda = config.value.embedding.acceleration.backend !== 'Cpu';
                    config.value.embedding.cudaDeviceId = config.value.embedding.acceleration.deviceId || 0;
                    config.value.embedding.cudaLibraryPath = config.value.embedding.acceleration.customLibraryPath || '';
                }
                if (config.value.rerank?.acceleration) {
                    config.value.rerank.useCuda = config.value.rerank.acceleration.backend !== 'Cpu';
                    config.value.rerank.cudaDeviceId = config.value.rerank.acceleration.deviceId || 0;
                    config.value.rerank.cudaLibraryPath = config.value.rerank.acceleration.customLibraryPath || '';
                }
                if (config.value.embedding) {
                    if (config.value.embedding.mode === 'openai') {
                        config.value.embedding.activeBackend = 'OpenAiApi';
                        config.value.embedding.modelName = config.value.embedding.openAiApi?.modelName || '';
                        config.value.embedding.apiBaseUrl = config.value.embedding.openAiApi?.baseUrl || '';
                        config.value.embedding.apiKey = config.value.embedding.openAiApi?.apiKey || '';
                        config.value.embedding.apiDimension = config.value.embedding.openAiApi?.dimension || null;
                    } else {
                        config.value.embedding.activeBackend = 'LocalOnnx';
                        config.value.embedding.modelName = config.value.embedding.localOnnx?.modelName || '';
                        config.value.embedding.modelPath = config.value.embedding.localOnnx?.modelPath || '';
                    }
                }
                if (config.value.rerank) {
                    if (config.value.rerank.mode === 'openai') {
                        config.value.rerank.activeBackend = 'OpenAiApi';
                        config.value.rerank.modelName = config.value.rerank.openAiApi?.modelName || '';
                        config.value.rerank.apiBaseUrl = config.value.rerank.openAiApi?.baseUrl || '';
                        config.value.rerank.apiKey = config.value.rerank.openAiApi?.apiKey || '';
                    } else {
                        config.value.rerank.activeBackend = 'LocalOnnx';
                        config.value.rerank.modelName = config.value.rerank.localOnnx?.modelName || '';
                        config.value.rerank.modelPath = config.value.rerank.localOnnx?.modelPath || '';
                    }
                }
                const saveResponse = await settingsApi.save(config.value);
                const saveResult = saveResponse.data || {};
                originalModelsRootPath.value = config.value.modelsRootPath || 'models';
                // 同步本地 API Key，防止因修改 API Key 导致后续请求 401
                const newApiKey = config.value.service?.apiKey?.trim() || null;
                if (newApiKey && newApiKey !== authStore.apiKey) {
                    authStore.setApiKey(newApiKey);
                } else if (!newApiKey && authStore.apiKey) {
                    authStore.clearApiKey();
                }
                if (networkAccessChanged.value || saveResult.requiresRestart) {
                    message.warning(saveResult.message || '配置已保存，网络端口/外网设置需重启服务后生效');
                    networkAccessChanged.value = false;
                }
                else if (saveResult.indexRequiresRebuild) {
                    message.warning(saveResult.message || '配置已即时热生效。嵌入模型已切换，检索前建议重建向量索引');
                    networkAccessChanged.value = false;
                }
                else {
                    message.success(saveResult.message || '配置已保存并即时生效');
                }
                // 同步前端脱敏状态
                if (config.value.chat?.openAiApi) {
                    if (config.value.chat.openAiApi.apiKey && config.value.chat.openAiApi.apiKey !== '__CLEAR__') {
                        chatConfig.value.apiKeyConfigured = true;
                        config.value.chat.openAiApi.apiKeyConfigured = true;
                        config.value.chat.openAiApi.apiKey = '';
                    } else if (config.value.chat.openAiApi.apiKey === '__CLEAR__') {
                        chatConfig.value.apiKeyConfigured = false;
                        config.value.chat.openAiApi.apiKeyConfigured = false;
                        config.value.chat.openAiApi.apiKey = '';
                    }
                }
                if (config.value.systemOne) {
                    if (config.value.systemOne.apiKey && config.value.systemOne.apiKey !== '__CLEAR__') {
                        config.value.systemOne.apiKeyConfigured = true;
                        config.value.systemOne.apiKey = '';
                    } else if (config.value.systemOne.apiKey === '__CLEAR__') {
                        config.value.systemOne.apiKeyConfigured = false;
                        config.value.systemOne.apiKey = '';
                    }
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
        const activeGroup = ref('models');
        const activeTab = ref('chat');

        const groupTabsMap = {
            models: ['chat', 'systemOne', 'embedding', 'rerank', 'retention', 'modelHub'],
            retrieval: ['chunking', 'search'],
            system: ['service', 'network', 'data']
        };

        const getGroupByTab = (tab) => {
            for (const [group, tabs] of Object.entries(groupTabsMap)) {
                if (tabs.includes(tab)) return group;
            }
            return 'models';
        };

        const onGroupChange = (newGroup) => {
            activeGroup.value = newGroup;
            const currentGroupTabs = groupTabsMap[newGroup] || [];
            if (!currentGroupTabs.includes(activeTab.value)) {
                activeTab.value = currentGroupTabs[0] || 'chat';
            }
            syncTabToUrl(activeTab.value);
        };

        const onTabChange = (newTab) => {
            activeTab.value = newTab;
            activeGroup.value = getGroupByTab(newTab);
            syncTabToUrl(newTab);
            if (['chat', 'embedding', 'rerank'].includes(newTab)) {
                loadCatalogs();
            }
            if (newTab === 'chat') {
                loadDownloadedChatModels().then(() => {
                    ensureValidChatModelSelection();
                });
            }
        };

        const syncTabToUrl = (tab) => {
            try {
                const hash = window.location.hash || '';
                const [p, queryStr] = hash.split('?');
                const searchParams = new URLSearchParams(queryStr || '');
                searchParams.set('tab', tab);
                const newHash = `${p}?${searchParams.toString()}`;
                if (window.location.hash !== newHash) {
                    window.history.replaceState(null, '', newHash);
                }
            } catch (e) {
                // ignore
            }
        };

        const initTabFromUrl = () => {
            try {
                const hash = window.location.hash || '';
                const [, queryStr] = hash.split('?');
                const searchParams = new URLSearchParams(queryStr || window.location.search || '');
                const targetTab = searchParams.get('tab');
                if (targetTab) {
                    activeGroup.value = getGroupByTab(targetTab);
                    activeTab.value = targetTab;
                }
            } catch (e) {
                // ignore
            }
        };

        let modelSyncChannel = null;
        try {
            if (typeof BroadcastChannel !== 'undefined') {
                modelSyncChannel = new BroadcastChannel('referencerag_model_sync');
                modelSyncChannel.onmessage = async (e) => {
                    if (e.data?.type === 'MODEL_DOWNLOADED' || e.data?.type === 'MODEL_UPDATED') {
                        await Promise.all([loadDownloadedChatModels(), loadConfig()]);
                        ensureValidChatModelSelection();
                    }
                };
            }
        } catch (e) {
            console.warn('[Settings] BroadcastChannel 初始化失败', e);
        }

        const handleStorageChange = async (e) => {
            if (e.key === 'referencerag_model_updated' || e.key === 'modelsPathChanged') {
                await Promise.all([loadDownloadedChatModels(), loadConfig()]);
                ensureValidChatModelSelection();
            }
        };

        onMounted(async () => {
            initTabFromUrl();
            await Promise.all([loadConfig(), loadDownloadedChatModels(), loadCatalogs()]);
            ensureValidChatModelSelection();
            loadSourceNames();
            loadHardwareStatus();
            loadChatConfig();
            window.addEventListener('storage', handleStorageChange);
        });

        onActivated(async () => {
            initTabFromUrl();
            await Promise.all([loadConfig(), loadDownloadedChatModels(), loadCatalogs()]);
            ensureValidChatModelSelection();
            loadSourceNames();
            loadHardwareStatus();
            loadChatConfig();
        });

        onUnmounted(() => {
            window.removeEventListener('storage', handleStorageChange);
            if (modelSyncChannel) {
                try { modelSyncChannel.close(); } catch {}
            }
        });
        const __returned__ = { message, authStore, isDesktop, loading, saving, downloadedChatModels, chatModelLoading, loadDownloadedChatModels, ensureValidChatModelSelection, goToModelManagement, chatModelOptions, onLocalChatModelSelect, chatConfig, loadChatConfig, handleSaveChat, sourceNames, cudaAvailable, cudaInfo, hardwareStatus, isNvidiaGpu, shouldShowCudaStatus, hardwareProbeLoading, hardwareProbeError, backendOptions, originalModelsRootPath, defaultConfig, config, verifyingHfToken, hfTokenStatus, hfEndpointOptions, handleVerifyHfToken, thresholdPercent, mmrLambdaPercent, sourceNameOptions, logLevelOptions, loadConfig, loadSourceNames, loadHardwareStatus, loadCudaAvailability, networkAccessChanged, onNetworkAccessChange, normalizeModelsRootPath, handleSave, adaptiveLevelOptions, adaptivePreview, chatPerformancePreview, updateChatPerformancePreview, activeGroup, activeTab, onGroupChange, onTabChange, syncTabToUrl, initTabFromUrl, chatCatalog, embeddingCatalog, rerankCatalog, catalogsLoading, loadCatalogs, chatCatalogOptions, embeddingCatalogOptions, rerankCatalogOptions, isLocalChatModel, isLocalEmbeddingModel, isLocalRerankModel, onChatModelUrnChange, onEmbeddingModelUrnChange, onRerankModelUrnChange, testingSystemOne, systemOneTestResult, systemOneTestQuery, handleTestSystemOne, proxyPolicyOptions, testingProxy, proxyTestResult, handleTestProxy, testingCognitiveBackend, cognitiveBackendTestResult, handleTestCognitiveBackend };
        return __returned__;
    }
});

component.template = "\n  <n-space vertical :size=\"20\">\n    <n-spin :show=\"loading\">\n      <!-- 一级分类导航条 (Segmented 胶囊设计) -->\n      <div style=\"display: flex; align-items: center; justify-content: flex-start; margin-bottom: 14px;\">\n        <n-tabs\n          v-model:value=\"activeGroup\"\n          type=\"segment\"\n          size=\"medium\"\n          @update:value=\"onGroupChange\"\n          style=\"max-width: 540px;\"\n        >\n          <n-tab name=\"models\">🤖 模型与推理</n-tab>\n          <n-tab name=\"retrieval\">🔍 检索与分段</n-tab>\n          <n-tab name=\"system\">⚙️ 系统与存储</n-tab>\n        </n-tabs>\n      </div>\n\n      <!-- 二级面板 1: 模型与推理 -->\n      <div v-show=\"activeGroup === 'models'\">\n        <n-tabs v-model:value=\"activeTab\" type=\"line\" animated @update:value=\"onTabChange\">\n          <n-tab-pane name=\"chat\" tab=\"聊天模型\">\n          <n-card>\n            <n-alert type=\"info\" style=\"margin-bottom: 16px\">\n              <template #header>统一聊天模型选择与参数管理</template>\n              支持本地已就绪 GGUF 大模型与「模型管理」已授权的在线服务商模型。通过统一模型 ID (URN) 集中受控关联，免去重复输入端点与密钥。\n            </n-alert>\n            <n-form label-placement=\"left\" label-width=\"140\">\n              <n-form-item label=\"当前生效模型\">\n                <n-space vertical style=\"width: 100%\">\n                  <n-select\n                    v-model:value=\"config.chat.activeModelUrn\"\n                    :options=\"chatCatalogOptions\"\n                    :loading=\"catalogsLoading\"\n                    filterable\n                    placeholder=\"请选择本地 GGUF 聊天模型或在线服务商模型\"\n                    @update:value=\"onChatModelUrnChange\"\n                  />\n                  <div style=\"display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: 8px; margin-top: 4px;\">\n                    <n-tag v-if=\"isLocalChatModel\" size=\"small\" type=\"success\" :bordered=\"false\" round>💻 本地模型 (GGUF 离线加速)</n-tag>\n                    <n-tag v-else size=\"small\" type=\"info\" :bordered=\"false\" round>🌐 在线服务商模型 (受控 API)</n-tag>\n                    <n-button size=\"tiny\" type=\"primary\" secondary @click=\"goToModelManagement('chat')\">前往「模型管理」下载模型或配置服务商 ↗</n-button>\n                  </div>\n                </n-space>\n              </n-form-item>\n\n              <!-- 本地 GGUF 专属硬件加速与上下文配置 -->\n              <template v-if=\"isLocalChatModel\">\n                <n-form-item label=\"硬件加速方案\">\n                  <n-select v-model:value=\"config.chat.localGguf.acceleration.backend\" :options=\"backendOptions\" @update:value=\"updateChatPerformancePreview\" style=\"width: 100%\" />\n                </n-form-item>\n                <n-form-item v-if=\"hardwareStatus\" label=\"硬件就绪状态\">\n                  <n-alert :type=\"hardwareStatus.activeBackend === 'Cpu' ? 'warning' : 'success'\" :title=\"hardwareStatus.message\" style=\"width: 100%\">\n                    <div v-if=\"hardwareStatus.primaryGpu\">检测设备: {{ hardwareStatus.primaryGpu.name }} {{ (hardwareStatus.primaryGpu.totalVramGb && hardwareStatus.primaryGpu.totalVramGb > 0) ? '(' + hardwareStatus.primaryGpu.totalVramGb + ' GB 显存)' : '(集成核显 / 共享系统内存)' }}</div>\n                    <div style=\"font-size: 12px; margin-top: 4px; opacity: 0.85\">当前就绪后端: {{ hardwareStatus.activeBackend }} | 自动回退保护: 开启</div>\n                    <div v-if=\"hardwareStatus.activeBackendReason && hardwareStatus.activeBackend === 'Cpu'\" style=\"font-size: 12px; margin-top: 3px; opacity: 0.9\">诊断详情: {{ hardwareStatus.activeBackendReason }}</div>\n                  </n-alert>\n                </n-form-item>\n                <n-form-item label=\"性能规划模式\">\n                  <n-radio-group v-model:value=\"config.chat.localGguf.performanceMode\" size=\"medium\" @update:value=\"updateChatPerformancePreview\">\n                    <n-radio-button value=\"Adaptive\">自适应规划 (Adaptive - 智能推荐)</n-radio-button>\n                    <n-radio-button value=\"Fixed\">固定参数 (Fixed - 手动精调)</n-radio-button>\n                  </n-radio-group>\n                </n-form-item>\n                <template v-if=\"config.chat.localGguf.performanceMode === 'Adaptive'\">\n                  <n-form-item label=\"自适应偏好策略\">\n                    <n-radio-group v-model:value=\"config.chat.localGguf.adaptiveStrategy\" size=\"small\" @update:value=\"updateChatPerformancePreview\">\n                      <n-radio-button value=\"Balanced\">通用平衡 (Balanced - 8K)</n-radio-button>\n                      <n-radio-button value=\"HighThroughput\">极速吞吐 (High Throughput - 4K)</n-radio-button>\n                      <n-radio-button value=\"LongContext\">深度长文 (Long Context - 16K~32K)</n-radio-button>\n                      <n-radio-button value=\"LowMemory\">轻量低显存 (Low Memory - 2K~4K)</n-radio-button>\n                    </n-radio-group>\n                  </n-form-item>\n                  <n-card size=\"small\" embedded style=\"background: rgba(0, 0, 0, 0.02); margin-bottom: 16px; border-left: 3px solid var(--n-primary-color)\">\n                    <n-space vertical :size=\"8\">\n                      <n-space justify=\"space-between\" align=\"center\">\n                        <n-text strong style=\"font-size: 13px\">实时模型性能画像测算看板 (Live Sweet-Spot Preview)</n-text>\n                        <n-tag size=\"small\" type=\"success\" :bordered=\"false\" round>规划算法: 启发式多维策略</n-tag>\n                      </n-space>\n                      <n-space align=\"center\" :size=\"16\" style=\"font-size: 12px; color: #555\">\n                        <span>推荐上下文: <b style=\"color: var(--n-primary-color)\">{{ chatPerformancePreview.contextSize }} tokens</b></span>\n                        <span>批处理吞吐: <b>{{ chatPerformancePreview.batchSize }}</b></span>\n                        <span>微批大小: <b>{{ chatPerformancePreview.uBatchSize }}</b></span>\n                        <span>GPU 卸载层数: <b>{{ chatPerformancePreview.gpuLayers === -1 ? '全层卸载 (Auto)' : chatPerformancePreview.gpuLayers + ' 层' }}</b></span>\n                      </n-space>\n                      <n-space align=\"center\" :size=\"16\" style=\"font-size: 12px\">\n                        <n-tag size=\"small\" type=\"info\" :bordered=\"false\" round>预估显存: {{ chatPerformancePreview.estimatedVramText }}</n-tag>\n                        <n-text depth=\"3\" style=\"font-size: 12px\">{{ chatPerformancePreview.rationale }}</n-text>\n                      </n-space>\n                      <n-alert v-if=\"chatPerformancePreview.warning\" type=\"warning\" size=\"small\" style=\"margin-top: 4px\">\n                        {{ chatPerformancePreview.warning }}\n                      </n-alert>\n                    </n-space>\n                  </n-card>\n                </template>\n                <template v-else>\n                  <n-form-item label=\"GPU 卸载层数\">\n                    <n-space align=\"center\" style=\"width: 100%\">\n                      <n-slider v-model:value=\"config.chat.localGguf.gpuLayers\" :min=\"-1\" :max=\"99\" :step=\"1\" @update:value=\"updateChatPerformancePreview\" style=\"width: 320px\" />\n                      <n-input-number v-model:value=\"config.chat.localGguf.gpuLayers\" :min=\"-1\" :max=\"99\" @update:value=\"updateChatPerformancePreview\" style=\"width: 100px\" />\n                      <n-text depth=\"3\" style=\"font-size: 12px\">层（-1 为全量卸载至显存，0 为纯 CPU 运算）</n-text>\n                    </n-space>\n                  </n-form-item>\n                  <n-form-item label=\"上下文窗口大小\">\n                    <n-space align=\"center\" style=\"width: 100%\">\n                      <n-input-number v-model:value=\"config.chat.localGguf.contextSize\" :min=\"512\" :max=\"65536\" :step=\"512\" @update:value=\"updateChatPerformancePreview\" style=\"width: 200px\" />\n                      <n-text depth=\"3\" style=\"font-size: 12px; margin-left: 8px\">tokens（KV Cache 随长度线性增加，建议 4096~16384）</n-text>\n                    </n-space>\n                  </n-form-item>\n                  <n-form-item label=\"批处理大小 (Batch)\">\n                    <n-space align=\"center\" style=\"width: 100%\">\n                      <n-input-number v-model:value=\"config.chat.localGguf.batchSize\" :min=\"128\" :max=\"4096\" :step=\"128\" @update:value=\"updateChatPerformancePreview\" style=\"width: 200px\" />\n                      <n-text depth=\"3\" style=\"font-size: 12px; margin-left: 8px\">tokens（Prompt 预处理批大小，推荐 1024~2048）</n-text>\n                    </n-space>\n                  </n-form-item>\n                  <n-form-item label=\"微批大小 (uBatch)\">\n                    <n-space align=\"center\" style=\"width: 100%\">\n                      <n-input-number v-model:value=\"config.chat.localGguf.uBatchSize\" :min=\"64\" :max=\"2048\" :step=\"64\" @update:value=\"updateChatPerformancePreview\" style=\"width: 200px\" />\n                      <n-text depth=\"3\" style=\"font-size: 12px; margin-left: 8px\">tokens（物理执行单步微批大小，推荐 256~512）</n-text>\n                    </n-space>\n                  </n-form-item>\n                  <n-card size=\"small\" embedded style=\"background: rgba(0, 0, 0, 0.02); margin-bottom: 16px; border-left: 3px solid #f0a020\">\n                    <n-space vertical :size=\"8\">\n                      <n-space justify=\"space-between\" align=\"center\">\n                        <n-text strong style=\"font-size: 13px\">手动参数显存校验 (VRAM Safety Check)</n-text>\n                        <n-tag size=\"small\" :type=\"chatPerformancePreview.warning ? 'warning' : 'success'\" :bordered=\"false\" round>\n                          {{ chatPerformancePreview.warning ? '存在显存压力告警' : '参数在安全边界内' }}\n                        </n-tag>\n                      </n-space>\n                      <n-space align=\"center\" :size=\"16\" style=\"font-size: 12px\">\n                        <n-tag size=\"small\" type=\"info\" :bordered=\"false\" round>预估显存占用: {{ chatPerformancePreview.estimatedVramText }}</n-tag>\n                        <n-text depth=\"3\" style=\"font-size: 12px\">{{ chatPerformancePreview.rationale }}</n-text>\n                      </n-space>\n                      <n-alert v-if=\"chatPerformancePreview.warning\" type=\"warning\" size=\"small\" style=\"margin-top: 4px\">\n                        {{ chatPerformancePreview.warning }}\n                      </n-alert>\n                    </n-space>\n                  </n-card>\n                </template>\n                <n-form-item label=\"空闲自动卸载\">\n                  <n-input-number v-model:value=\"config.chat.idleTimeoutSeconds\" :min=\"30\" :max=\"3600\" :step=\"30\" style=\"width: 200px\" />\n                  <n-text depth=\"3\" style=\"font-size: 12px; margin-left: 8px\">秒（默认 300 秒无请求自动释放显存）</n-text>\n                </n-form-item>\n              </template>\n\n              <!-- 在线服务商专属配置 -->\n              <template v-else>\n                <n-form-item label=\"请求超时时间\">\n                  <n-input-number v-model:value=\"config.chat.openAiApi.timeoutSeconds\" :min=\"10\" :max=\"300\" style=\"width: 200px\" />\n                  <n-text depth=\"3\" style=\"font-size: 12px; margin-left: 8px\">秒（默认 60 秒）</n-text>\n                </n-form-item>\n                <n-alert type=\"success\" size=\"small\" style=\"margin-bottom: 16px\">\n                  所选在线服务商的端点、API 密钥与模型列表已在「模型管理」集中授权管辖，保存设置即可直接路由调用。\n                </n-alert>\n              </template>\n\n              <!-- 通用对话推理参数 -->\n              <n-divider>对话推理参数</n-divider>\n              <n-form-item label=\"意图识别与路由\">\n                <n-space align=\"center\">\n                  <n-switch v-model:value=\"config.chat.enableIntentRouting\" />\n                  <n-text depth=\"3\" style=\"font-size: 12px\">\n                    {{ config.chat.enableIntentRouting\n                      ? '开启（推荐）：依据语义相似度智能路由意图并按需裁剪加载工具，提升推理效率并保障闲聊无工具纯净响应'\n                      : '关闭：跳过意图分类，始终全量挂载所有已注册工具，由大模型自主决定是否发起工具调用' }}\n                  </n-text>\n                </n-space>\n              </n-form-item>\n              <n-form-item label=\"系统提示词\">\n                <n-input\n                  v-model:value=\"config.chat.systemPrompt\"\n                  type=\"textarea\"\n                  :autosize=\"{ minRows: 3, maxRows: 6 }\"\n                  placeholder=\"你是 ReferenceRAG 智能助手...\"\n                />\n              </n-form-item>\n              <n-form-item label=\"生成温度\">\n                <n-space align=\"center\" style=\"width: 100%\">\n                  <n-slider v-model:value=\"config.chat.temperature\" :min=\"0\" :max=\"2\" :step=\"0.05\" style=\"width: 280px\" />\n                  <n-input-number v-model:value=\"config.chat.temperature\" :min=\"0\" :max=\"2\" :step=\"0.05\" style=\"width: 90px\" />\n                  <n-text depth=\"3\" style=\"font-size: 12px\">较低更确定，较高更多样（默认 0.7）</n-text>\n                </n-space>\n              </n-form-item>\n              <n-form-item label=\"核采样 (Top-P)\">\n                <n-space align=\"center\" style=\"width: 100%\">\n                  <n-slider v-model:value=\"config.chat.topP\" :min=\"0.05\" :max=\"1\" :step=\"0.05\" style=\"width: 280px\" />\n                  <n-input-number v-model:value=\"config.chat.topP\" :min=\"0.05\" :max=\"1\" :step=\"0.05\" style=\"width: 90px\" />\n                  <n-text depth=\"3\" style=\"font-size: 12px\">仅从累积概率达标候选词采样（默认 0.9）</n-text>\n                </n-space>\n              </n-form-item>\n              <n-form-item label=\"重复惩罚 (Repeat)\">\n                <n-space align=\"center\" style=\"width: 100%\">\n                  <n-slider v-model:value=\"config.chat.repeatPenalty\" :min=\"1.0\" :max=\"1.5\" :step=\"0.05\" style=\"width: 280px\" />\n                  <n-input-number v-model:value=\"config.chat.repeatPenalty\" :min=\"1.0\" :max=\"2.0\" :step=\"0.05\" style=\"width: 90px\" />\n                  <n-text depth=\"3\" style=\"font-size: 12px\">倍数惩罚已生成的 token，防止自回归死循环退化（默认 1.10）</n-text>\n                </n-space>\n              </n-form-item>\n              <n-form-item label=\"频率惩罚 (Frequency)\">\n                <n-space align=\"center\" style=\"width: 100%\">\n                  <n-slider v-model:value=\"config.chat.frequencyPenalty\" :min=\"0\" :max=\"1.0\" :step=\"0.05\" style=\"width: 280px\" />\n                  <n-input-number v-model:value=\"config.chat.frequencyPenalty\" :min=\"0\" :max=\"2.0\" :step=\"0.05\" style=\"width: 90px\" />\n                  <n-text depth=\"3\" style=\"font-size: 12px\">按频次线性惩罚重复 token（默认 0.20）</n-text>\n                </n-space>\n              </n-form-item>\n              <n-form-item label=\"存在惩罚 (Presence)\">\n                <n-space align=\"center\" style=\"width: 100%\">\n                  <n-slider v-model:value=\"config.chat.presencePenalty\" :min=\"0\" :max=\"1.0\" :step=\"0.05\" style=\"width: 280px\" />\n                  <n-input-number v-model:value=\"config.chat.presencePenalty\" :min=\"0\" :max=\"2.0\" :step=\"0.05\" style=\"width: 90px\" />\n                  <n-text depth=\"3\" style=\"font-size: 12px\">出现即施加固定惩罚，鼓励引入新词（默认 0.10）</n-text>\n                </n-space>\n              </n-form-item>\n              <n-form-item label=\"流式重复熔断保护\">\n                <n-space align=\"center\">\n                  <n-switch v-model:value=\"config.chat.enableRepetitionCircuitBreaker\" />\n                  <n-text depth=\"3\" style=\"font-size: 12px\">\n                    {{ config.chat.enableRepetitionCircuitBreaker\n                      ? '开启（推荐）：流式输出时实时检测周期性死循环短语或重复句段，一旦检测到退化毫秒级平滑截断，避免算力浪费与无限刷屏'\n                      : '关闭：不进行流式周期检测，完全依赖模型生成自身结束标记' }}\n                  </n-text>\n                </n-space>\n              </n-form-item>\n            </n-form>\n          </n-card>\n        </n-tab-pane>\n          <n-tab-pane name=\"systemOne\" tab=\"System One 认知 (Jev)\">\n          <n-card>\n            <n-alert type=\"info\" style=\"margin-bottom: 16px\">\n              <template #header>在线认知路由与意图决策服务 (Jev)</template>\n              用于意图快速分类、自适应召回策略裁决与智能跳过无意义检索。支持动态热重载，无需重启应用。\n            </n-alert>\n            <n-form label-placement=\"left\" label-width=\"160\">\n              <n-form-item label=\"启用 System One\">\n                <n-space align=\"center\">\n                  <n-switch v-model:value=\"config.systemOne.enabled\" />\n                  <n-text depth=\"3\" style=\"font-size: 12px\">\n                    {{ config.systemOne.enabled ? '已开启：通过低延迟 Jev 服务进行高敏意图识别与 RAG 决策分流' : '已关闭：所有会话直接走本地规则决策' }}\n                  </n-text>\n                </n-space>\n              </n-form-item>\n              <template v-if=\"config.systemOne.enabled\">\n                <n-form-item label=\"服务端点 (Base URL)\">\n                  <n-input v-model:value=\"config.systemOne.baseUrl\" placeholder=\"https://api.typesafe.ai/v1/systemone\" style=\"max-width: 500px;\" />\n                </n-form-item>\n                <n-form-item label=\"API 密钥 (API Key)\">\n                  <n-space vertical style=\"width: 100%\">\n                    <n-input-group style=\"max-width: 500px;\">\n                      <n-input\n                        v-model:value=\"config.systemOne.apiKey\"\n                        type=\"password\"\n                        show-password-on=\"click\"\n                        :placeholder=\"config.systemOne.apiKeyConfigured ? '已安全配置 (留空保留现有密钥)' : '输入 API 密钥'\"\n                      />\n                      <n-button\n                        v-if=\"config.systemOne.apiKeyConfigured || config.systemOne.apiKey\"\n                        type=\"warning\"\n                        @click=\"config.systemOne.apiKey = '__CLEAR__'; config.systemOne.apiKeyConfigured = false;\"\n                      >\n                        清除\n                      </n-button>\n                    </n-input-group>\n                    <n-text depth=\"3\" style=\"font-size: 12px\">\n                      {{ config.systemOne.apiKeyConfigured ? '✅ 密钥已受控保存并脱敏保护' : '未配置 API 密钥' }}\n                    </n-text>\n                  </n-space>\n                </n-form-item>\n                <n-form-item label=\"代理路由策略\">\n                  <n-select v-model:value=\"config.systemOne.proxyPolicy\" :options=\"proxyPolicyOptions\" style=\"max-width: 300px;\" />\n                </n-form-item>\n                <n-form-item v-if=\"config.systemOne.proxyPolicy === 'Custom'\" label=\"专属代理地址\">\n                  <n-input v-model:value=\"config.systemOne.customProxyUrl\" placeholder=\"如 http://127.0.0.1:7890 或 socks5://127.0.0.1:1080\" style=\"max-width: 500px;\" />\n                </n-form-item>\n                <n-form-item label=\"服务模型\">\n                  <n-input v-model:value=\"config.systemOne.model\" placeholder=\"jev-latest\" style=\"max-width: 300px;\" />\n                </n-form-item>\n                <n-form-item label=\"置信度阈值\">\n                  <n-space align=\"center\" style=\"width: 100%\">\n                    <n-slider v-model:value=\"config.systemOne.retrievalConfidenceThreshold\" :min=\"0.1\" :max=\"1.0\" :step=\"0.05\" style=\"width: 280px\" />\n                    <n-input-number v-model:value=\"config.systemOne.retrievalConfidenceThreshold\" :min=\"0.1\" :max=\"1.0\" :step=\"0.05\" style=\"width: 90px\" />\n                    <n-text depth=\"3\" style=\"font-size: 12px\">高于此置信度自动判定是否需要 RAG 检索（默认 0.65）</n-text>\n                  </n-space>\n                </n-form-item>\n                <n-form-item label=\"超时时间 (毫秒)\">\n                  <n-input-number v-model:value=\"config.systemOne.timeoutMs\" :min=\"500\" :max=\"30000\" :step=\"500\" style=\"width: 200px;\" />\n                </n-form-item>\n                <n-form-item label=\"连通性探测\">\n                  <n-space vertical style=\"width: 100%\">\n                    <n-space align=\"center\">\n                      <n-input v-model:value=\"systemOneTestQuery\" placeholder=\"测试探针输入文本\" style=\"width: 300px;\" />\n                      <n-button type=\"info\" ghost :loading=\"testingSystemOne\" @click=\"handleTestSystemOne\">测试 Jev 连接与决策</n-button>\n                    </n-space>\n                    <n-alert v-if=\"systemOneTestResult\" :type=\"systemOneTestResult.success ? 'success' : 'warning'\" style=\"margin-top: 8px\">\n                      <div v-if=\"systemOneTestResult.success\">\n                        <strong>探测成功！</strong> 耗时: {{ systemOneTestResult.latencyMs }}ms | 决策意图: {{ systemOneTestResult.decision?.intent }} | 置信度: {{ systemOneTestResult.decision?.confidence }} | 推荐跳过检索: {{ systemOneTestResult.decision?.skipRetrieval ? '是' : '否' }}\n                      </div>\n                      <div v-else>\n                        <strong>探测失败:</strong> {{ systemOneTestResult.error }}\n                      </div>\n                    </n-alert>\n                  </n-space>\n                </n-form-item>\n              </template>\n            </n-form>\n          </n-card>\n        </n-tab-pane>\n          <n-tab-pane name=\"embedding\" tab=\"默认嵌入引擎与硬件加速\">\n          <n-card>\n            <n-alert type=\"info\" style=\"margin-bottom: 16px\">\n              <template #header>系统级推理引擎与新建知识库默认模板</template>\n              控制宿主机 GPU/DirectML/CUDA 硬件加速驱动、批处理并行度与显存常驻策略；作为新建知识库未指定专属模型时的默认模板，并为系统级通用向量计算（如语义相似度计算、/v1/embeddings）提供算力底座。已创建的独立知识库将遵循各自绑定的向量空间规范（EmbeddingSpec）独立运行。\n            </n-alert>\n            <n-form label-placement=\"left\" label-width=\"140\">\n              <n-form-item label=\"默认嵌入模型\">\n                <n-space vertical style=\"width: 100%\">\n                  <n-select\n                    v-model:value=\"config.embedding.activeModelUrn\"\n                    :options=\"embeddingCatalogOptions\"\n                    :loading=\"catalogsLoading\"\n                    filterable\n                    placeholder=\"请选择本地 ONNX 向量模型或在线服务商模型\"\n                    @update:value=\"onEmbeddingModelUrnChange\"\n                  />\n                  <div style=\"display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: 8px; margin-top: 4px;\">\n                    <n-tag v-if=\"isLocalEmbeddingModel\" size=\"small\" type=\"success\" :bordered=\"false\" round>💻 本地模型 (ONNX 原生/硬件加速)</n-tag>\n                    <n-tag v-else size=\"small\" type=\"info\" :bordered=\"false\" round>🌐 在线服务商模型 (受控 API)</n-tag>\n                    <n-button size=\"tiny\" type=\"primary\" secondary @click=\"goToModelManagement('embedding')\">前往「模型管理」管理模型与服务商 ↗</n-button>\n                  </div>\n                </n-space>\n              </n-form-item>\n\n              <!-- 本地 ONNX 专属硬件加速 -->\n              <template v-if=\"isLocalEmbeddingModel\">\n                <n-form-item label=\"硬件加速方案\">\n                  <n-select v-model:value=\"config.embedding.acceleration.backend\" :options=\"backendOptions\" style=\"width: 100%\" />\n                </n-form-item>\n                <n-form-item v-if=\"hardwareStatus\" label=\"硬件就绪状态\">\n                  <n-alert :type=\"hardwareStatus.activeBackend === 'Cpu' ? 'warning' : 'success'\" :title=\"hardwareStatus.message\" style=\"width: 100%\">\n                    <div v-if=\"hardwareStatus.primaryGpu\">检测设备: {{ hardwareStatus.primaryGpu.name }} {{ (hardwareStatus.primaryGpu.totalVramGb && hardwareStatus.primaryGpu.totalVramGb > 0) ? '(' + hardwareStatus.primaryGpu.totalVramGb + ' GB 显存)' : '(集成核显 / 共享系统内存)' }}</div>\n                    <div style=\"font-size: 12px; margin-top: 4px; opacity: 0.85\">当前就绪后端: {{ hardwareStatus.activeBackend }} | 自动回退保护: 开启</div>\n                    <div v-if=\"hardwareStatus.activeBackendReason && hardwareStatus.activeBackend === 'Cpu'\" style=\"font-size: 12px; margin-top: 3px; opacity: 0.9\">诊断详情: {{ hardwareStatus.activeBackendReason }}</div>\n                  </n-alert>\n                </n-form-item>\n                <n-form-item v-else-if=\"hardwareProbeError\" label=\"硬件就绪状态\">\n                  <n-alert type=\"warning\" :title=\"hardwareProbeError\" style=\"width: 100%\">\n                    <div style=\"font-size: 12px; opacity: 0.85\">硬件加速探针暂时不可达，系统已安全使用纯 CPU 运算。</div>\n                  </n-alert>\n                </n-form-item>\n                <n-form-item v-else-if=\"shouldShowCudaStatus && cudaInfo\" label=\"CUDA 状态\">\n                  <n-text depth=\"3\" style=\"font-size: 12px\">{{ cudaInfo.message }}</n-text>\n                </n-form-item>\n                <n-form-item v-if=\"config.embedding.acceleration.backend !== 'Cpu'\" label=\"计算设备 ID\">\n                  <n-input-number v-model:value=\"config.embedding.acceleration.deviceId\" :min=\"0\" :max=\"7\" style=\"width: 200px\" />\n                </n-form-item>\n                <n-form-item v-if=\"isNvidiaGpu && config.embedding.acceleration.backend === 'Cuda'\" label=\"CUDA 库路径\">\n                  <n-input v-model:value=\"config.embedding.acceleration.customLibraryPath\" placeholder=\"CUDA DLL 所在目录（留空自动探测）\" />\n                </n-form-item>\n                <n-form-item v-if=\"config.embedding.acceleration.backend !== 'Cpu'\" label=\"平滑降级保护\">\n                  <n-space align=\"center\">\n                    <n-switch v-model:value=\"config.embedding.acceleration.fallbackToCpuOnFailure\" />\n                    <n-text depth=\"3\" style=\"font-size: 12px\">硬件加速不可用时自动回退至 CPU 运算</n-text>\n                  </n-space>\n                </n-form-item>\n                <n-form-item label=\"最大序列长度\">\n                  <n-input-number v-model:value=\"config.embedding.maxSequenceLength\" :min=\"32\" :max=\"8192\" style=\"width: 100%\" />\n                </n-form-item>\n              </template>\n\n              <!-- 在线服务商专属向量维度提示 -->\n              <template v-else>\n                <n-form-item label=\"向量维度\">\n                  <n-input-number v-model:value=\"config.embedding.openAiApi.dimension\" :min=\"1\" :max=\"8192\" placeholder=\"留空则自动探测\" style=\"width: 100%\" />\n                  <n-text depth=\"3\" style=\"font-size: 12px; margin-left: 8px\">留空将在首次调用时自动探测</n-text>\n                </n-form-item>\n              </template>\n\n              <!-- 两种模式均适用 -->\n              <n-form-item label=\"批处理大小\">\n                <n-input-number v-model:value=\"config.embedding.batchSize\" :min=\"1\" :max=\"256\" style=\"width: 100%\" />\n              </n-form-item>\n            </n-form>\n          </n-card>\n        </n-tab-pane>\n          <n-tab-pane name=\"rerank\" tab=\"重排模型\">\n          <n-card v-if=\"config.rerank\">\n            <n-alert type=\"info\" style=\"margin-bottom: 16px\">\n              <template #header>无状态跨库检索二次精排服务</template>\n              重排模型（Cross-Encoder）不参与分块存储，不产生持久化数据，作为全系统共享的检索增强算力通道，负责对多知识库召回的多路候选文档进行二次交叉注意力打分与精细重排。\n            </n-alert>\n            <n-form label-placement=\"left\" label-width=\"140\">\n              <n-form-item label=\"启用重排\">\n                <n-switch v-model:value=\"config.rerank.enabled\" />\n              </n-form-item>\n\n              <template v-if=\"config.rerank.enabled\">\n                <n-form-item label=\"当前生效模型\">\n                  <n-space vertical style=\"width: 100%\">\n                    <n-select\n                      v-model:value=\"config.rerank.activeModelUrn\"\n                      :options=\"rerankCatalogOptions\"\n                      :loading=\"catalogsLoading\"\n                      filterable\n                      placeholder=\"请选择本地 ONNX 重排模型或在线服务商模型\"\n                      @update:value=\"onRerankModelUrnChange\"\n                    />\n                    <div style=\"display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: 8px; margin-top: 4px;\">\n                      <n-tag v-if=\"isLocalRerankModel\" size=\"small\" type=\"success\" :bordered=\"false\" round>💻 本地模型 (ONNX 跨注意力推理)</n-tag>\n                      <n-tag v-else size=\"small\" type=\"info\" :bordered=\"false\" round>🌐 在线服务商模型 (受控 API)</n-tag>\n                      <n-button size=\"tiny\" type=\"primary\" secondary @click=\"goToModelManagement('rerank')\">前往「模型管理」管理重排模型 ↗</n-button>\n                    </div>\n                  </n-space>\n                </n-form-item>\n\n                <!-- 本地 ONNX 专属硬件加速 -->\n                <template v-if=\"isLocalRerankModel\">\n                  <n-form-item label=\"硬件加速方案\">\n                    <n-select v-model:value=\"config.rerank.acceleration.backend\" :options=\"backendOptions\" style=\"width: 100%\" />\n                  </n-form-item>\n                  <n-form-item v-if=\"hardwareStatus\" label=\"硬件就绪状态\">\n                    <n-alert :type=\"hardwareStatus.activeBackend === 'Cpu' ? 'warning' : 'success'\" :title=\"hardwareStatus.message\" style=\"width: 100%\">\n                      <div v-if=\"hardwareStatus.primaryGpu\">检测设备: {{ hardwareStatus.primaryGpu.name }} {{ (hardwareStatus.primaryGpu.totalVramGb && hardwareStatus.primaryGpu.totalVramGb > 0) ? '(' + hardwareStatus.primaryGpu.totalVramGb + ' GB 显存)' : '(集成核显 / 共享系统内存)' }}</div>\n                      <div style=\"font-size: 12px; margin-top: 4px; opacity: 0.85\">当前就绪后端: {{ hardwareStatus.activeBackend }} | 自动回退保护: 开启</div>\n                      <div v-if=\"hardwareStatus.activeBackendReason && hardwareStatus.activeBackend === 'Cpu'\" style=\"font-size: 12px; margin-top: 3px; opacity: 0.9\">诊断详情: {{ hardwareStatus.activeBackendReason }}</div>\n                    </n-alert>\n                  </n-form-item>\n                  <n-form-item v-if=\"config.rerank.acceleration.backend !== 'Cpu'\" label=\"计算设备 ID\">\n                    <n-input-number v-model:value=\"config.rerank.acceleration.deviceId\" :min=\"0\" :max=\"7\" style=\"width: 200px\" />\n                  </n-form-item>\n                  <n-form-item v-if=\"isNvidiaGpu && config.rerank.acceleration.backend === 'Cuda'\" label=\"CUDA 库路径\">\n                    <n-input v-model:value=\"config.rerank.acceleration.customLibraryPath\" placeholder=\"CUDA DLL 所在目录（留空自动探测）\" />\n                  </n-form-item>\n                  <n-form-item v-if=\"config.rerank.acceleration.backend !== 'Cpu'\" label=\"平滑降级保护\">\n                    <n-space align=\"center\">\n                      <n-switch v-model:value=\"config.rerank.acceleration.fallbackToCpuOnFailure\" />\n                      <n-text depth=\"3\" style=\"font-size: 12px\">硬件加速不可用时自动回退至 CPU 运算</n-text>\n                    </n-space>\n                  </n-form-item>\n                </template>\n\n                <!-- 两种模式均适用 -->\n                <n-form-item label=\"重排返回数量\">\n                  <n-input-number v-model:value=\"config.rerank.topN\" :min=\"1\" :max=\"100\" style=\"width: 100%\" />\n                </n-form-item>\n                <n-form-item label=\"召回倍数\">\n                  <n-input-number v-model:value=\"config.rerank.recallFactor\" :min=\"1\" :max=\"10\" style=\"width: 100%\" />\n                  <n-text depth=\"3\" style=\"font-size: 12px; margin-left: 8px\">候选文档数 = TopN × 召回倍数</n-text>\n                </n-form-item>\n              </template>\n            </n-form>\n          </n-card>\n          <n-card v-else>\n            <n-text depth=\"3\">重排模型配置未加载</n-text>\n          </n-card>\n        </n-tab-pane>\n          <n-tab-pane name=\"retention\" tab=\"显存与驻留\">\n          <n-card>\n            <n-alert type=\"info\" style=\"margin-bottom: 16px\">\n              <template #header>模型常规驻留方案与显存自动释放策略</template>\n              控制本地向量嵌入模型（Embedding）与重排模型（Reranker）的生命周期与显存占用策略。修改保存后立即热生效，无需重启服务。\n            </n-alert>\n            <n-form label-placement=\"left\" label-width=\"160\">\n              <n-form-item label=\"驻留方案\">\n                <n-radio-group v-model:value=\"config.modelRetentionStrategy\" size=\"medium\">\n                  <n-radio-button value=\"SlidingExpiration\">滑动过期（按需释放显存）</n-radio-button>\n                  <n-radio-button value=\"Permanent\">固定常驻（零等待高响应）</n-radio-button>\n                </n-radio-group>\n              </n-form-item>\n\n              <template v-if=\"config.modelRetentionStrategy === 'SlidingExpiration'\">\n                <n-form-item label=\"滑动窗口时长\">\n                  <n-input-number\n                    v-model:value=\"config.modelIdleTimeoutSeconds\"\n                    :min=\"10\"\n                    :max=\"3600\"\n                    :step=\"30\"\n                    style=\"width: 200px\"\n                  />\n                  <n-text depth=\"3\" style=\"font-size: 12px; margin-left: 8px\">\n                    秒（默认 300 秒即 5 分钟无调用自动卸载）\n                  </n-text>\n                </n-form-item>\n                <n-form-item label=\"仅最小化托盘生效\">\n                  <n-space align=\"center\">\n                    <n-switch v-model:value=\"config.unloadOnlyWhenMinimizedToTray\" />\n                    <n-text depth=\"3\" style=\"font-size: 12px\">\n                      {{ config.unloadOnlyWhenMinimizedToTray\n                        ? '【推荐】窗口在前台打开使用期间保持常驻（零冷启动秒响应）；仅在最小化到系统托盘或后台时开始滑动倒计时释放显存'\n                        : '窗口在前台或后台均启用滑动计时，只要空闲超时即自动释放显存' }}\n                    </n-text>\n                  </n-space>\n                </n-form-item>\n              </template>\n\n              <template v-else>\n                <n-form-item label=\"方案说明\">\n                  <n-text depth=\"3\" style=\"font-size: 13px\">\n                    固定常驻模式下，嵌入与重排模型在首次请求加载后将始终驻留在显存/内存中，后续所有搜索均零冷启动等待，响应极速。适合显存充裕或需要高频搜索的场景。\n                  </n-text>\n                </n-form-item>\n              </template>\n            </n-form>\n          </n-card>\n        </n-tab-pane>\n          <n-tab-pane name=\"modelHub\" tab=\"模型仓库\">\n          <n-card v-if=\"config.modelHub\">\n            <n-alert type=\"info\" style=\"margin-bottom: 16px\">\n              <template #header>HuggingFace 令牌与模型下载设置</template>\n              当下载受协议限制（如 xVITA-Embed、Llama 等 Gated 模型）或私有授权模型时，需提供 Hugging Face Access Token。<br>\n              国内网络推荐使用高速镜像源（默认 https://hf-mirror.com）。\n            </n-alert>\n            <n-form label-placement=\"left\" label-width=\"140\">\n              <n-form-item label=\"HuggingFace Token\">\n                <n-space vertical style=\"width: 100%\">\n                  <n-input-group style=\"width: 100%\">\n                    <n-input\n                      v-model:value=\"config.modelHub.hfToken\"\n                      type=\"password\"\n                      show-password-on=\"click\"\n                      placeholder=\"例如: hf_xxxxxxxxxxxxxxxxxxxxxxxxxxxx\"\n                      style=\"flex: 1; max-width: 450px\"\n                    />\n                    <n-button\n                      type=\"primary\"\n                      ghost\n                      :loading=\"verifyingHfToken\"\n                      @click=\"handleVerifyHfToken\"\n                      style=\"margin-left: 8px\"\n                    >\n                      验证 Token\n                    </n-button>\n                    <n-button\n                      v-if=\"config.modelHub.hfToken\"\n                      type=\"warning\"\n                      style=\"margin-left: 8px\"\n                      @click=\"config.modelHub.hfToken = ''; hfTokenStatus = null;\"\n                    >\n                      清除\n                    </n-button>\n                  </n-input-group>\n                  <n-alert v-if=\"hfTokenStatus\" :type=\"hfTokenStatus.type\" style=\"margin-top: 6px\">\n                    {{ hfTokenStatus.message }}\n                  </n-alert>\n                  <n-text depth=\"3\" style=\"font-size: 12px\">\n                    {{ config.modelHub.hfToken ? 'Token 已配置，将自动挂载用于 Gated/私有模型下载与授权鉴权' : '未设置 Token，仅能访问无协议限制的公开模型。' }}\n                    获取 Token：可前往 <a href=\"https://huggingface.co/settings/tokens\" target=\"_blank\" style=\"color: var(--n-primary-color); text-decoration: underline\">huggingface.co/settings/tokens</a> 申请（选择 <strong>Read</strong> 权限即可）。\n                  </n-text>\n                </n-space>\n              </n-form-item>\n              <n-form-item label=\"下载端点 / 镜像源\">\n                <n-select\n                  v-model:value=\"config.modelHub.hfEndpoint\"\n                  :options=\"hfEndpointOptions\"\n                  tag\n                  filterable\n                  placeholder=\"https://hf-mirror.com\"\n                  style=\"width: 100%; max-width: 580px\"\n                />\n              </n-form-item>\n              <n-form-item label=\"\">\n                <n-text depth=\"3\" style=\"font-size: 12px\">\n                  推荐使用国内高速镜像源（hf-mirror.com），支持大模型多线程断点分块加速。亦可直接输入自定义反向代理地址。\n                </n-text>\n              </n-form-item>\n              <n-form-item label=\"请求超时时间\">\n                <n-input-number v-model:value=\"config.modelHub.timeoutSeconds\" :min=\"10\" :max=\"300\" style=\"width: 200px\" />\n                <n-text depth=\"3\" style=\"font-size: 12px; margin-left: 8px\">秒（默认 30 秒）</n-text>\n              </n-form-item>\n            </n-form>\n          </n-card>\n        </n-tab-pane>\n        </n-tabs>\n      </div>\n\n      <!-- 二级面板 2: 检索与分段 -->\n      <div v-show=\"activeGroup === 'retrieval'\">\n        <n-tabs v-model:value=\"activeTab\" type=\"line\" animated @update:value=\"onTabChange\">\n          <n-tab-pane name=\"chunking\" tab=\"分段设置\">\n          <n-card>\n            <n-alert type=\"info\" style=\"margin-bottom: 16px\">\n              <template #header>自适应分段与存储空间优化</template>\n              自适应模式根据当前嵌入模型的物理上限自动计算注意力黄金区间，防止尾部硬截断；同时保护代码块与表格完整性，可减少 15%~75% 冗余向量存储占用。\n            </n-alert>\n            <n-form label-placement=\"left\" label-width=\"160\">\n              <n-form-item label=\"分段切分模式\">\n                <n-radio-group v-model:value=\"config.chunking.mode\" size=\"medium\">\n                  <n-radio-button value=\"Adaptive\">自适应分段 (Adaptive - 智能推荐)</n-radio-button>\n                  <n-radio-button value=\"Fixed\">固定分段 (Fixed - 传统模式)</n-radio-button>\n                </n-radio-group>\n              </n-form-item>\n\n              <template v-if=\"config.chunking.mode === 'Adaptive'\">\n                <n-form-item label=\"自适应等级\">\n                  <n-select v-model:value=\"config.chunking.adaptiveLevel\" :options=\"adaptiveLevelOptions\" style=\"width: 100%\" />\n                </n-form-item>\n                <n-form-item label=\"适应风格类型\">\n                  <n-radio-group v-model:value=\"config.chunking.adaptiveStrategyType\" size=\"small\">\n                    <n-radio-button value=\"Balanced\">通用平衡 (推荐)</n-radio-button>\n                    <n-radio-button value=\"PrecisionDense\">高精度紧凑</n-radio-button>\n                    <n-radio-button value=\"DeepNarrative\">深度长文叙事</n-radio-button>\n                    <n-radio-button value=\"CodeFirst\">代码与结构优先</n-radio-button>\n                  </n-radio-group>\n                </n-form-item>\n                <n-form-item label=\"模型窗口利用率\">\n                  <n-space vertical style=\"width: 100%\">\n                    <n-slider v-model:value=\"config.chunking.targetWindowUtilizationPercent\" :min=\"50\" :max=\"90\" :step=\"5\" />\n                    <n-text depth=\"3\" style=\"font-size: 12px\">当前设定: {{ config.chunking.targetWindowUtilizationPercent }}% (推荐 65%~75%，为章节与路径元数据预留安全冗余)</n-text>\n                  </n-space>\n                </n-form-item>\n                <n-form-item label=\"动态重叠率\">\n                  <n-space vertical style=\"width: 100%\">\n                    <n-slider v-model:value=\"config.chunking.adaptiveOverlapPercent\" :min=\"0\" :max=\"25\" :step=\"1\" />\n                    <n-text depth=\"3\" style=\"font-size: 12px\">当前设定: {{ config.chunking.adaptiveOverlapPercent }}% (代码块自动采用 0% 零重叠，避免语法破坏)</n-text>\n                  </n-space>\n                </n-form-item>\n\n                <!-- 实时模型画像测算看板 -->\n                <n-card size=\"small\" embedded style=\"background: rgba(0, 0, 0, 0.02); margin-top: 8px\">\n                  <n-space vertical :size=\"6\">\n                    <n-text strong style=\"font-size: 13px\">实时模型画像测算看板 (Live Sweet-Spot Preview)</n-text>\n                    <n-space align=\"center\" :size=\"16\" style=\"font-size: 12px; color: #555\">\n                      <span>当前模型: <b>{{ adaptivePreview.modelName }}</b> ({{ adaptivePreview.physicalMax }} tokens)</span>\n                      <span>计算目标: <b>{{ adaptivePreview.targetTokens }} tokens</b></span>\n                      <span>最小聚合: <b>{{ adaptivePreview.minTokens }} tokens</b></span>\n                      <span>动态重叠: <b>{{ adaptivePreview.overlapTokens }} tokens</b></span>\n                    </n-space>\n                    <n-space align=\"center\" :size=\"16\" style=\"font-size: 12px\">\n                      <n-tag size=\"small\" type=\"success\" :bordered=\"false\" round>预计防硬截断率: 100%</n-tag>\n                      <n-tag size=\"small\" type=\"info\" :bordered=\"false\" round>预计空间优化: {{ adaptivePreview.spaceSaving }}</n-tag>\n                      <n-tag size=\"small\" :bordered=\"false\" round>代码完整性保护: 启用</n-tag>\n                    </n-space>\n                  </n-space>\n                </n-card>\n              </template>\n\n              <template v-else>\n                <n-form-item label=\"最大 Token 数\">\n                  <n-input-number v-model:value=\"config.chunking.maxTokens\" :min=\"64\" :max=\"4096\" :step=\"64\" style=\"width: 100%\" />\n                </n-form-item>\n                <n-form-item label=\"最小 Token 数\">\n                  <n-input-number v-model:value=\"config.chunking.minTokens\" :min=\"10\" :max=\"512\" :step=\"10\" style=\"width: 100%\" />\n                </n-form-item>\n                <n-form-item label=\"重叠 Token 数\">\n                  <n-input-number v-model:value=\"config.chunking.overlapTokens\" :min=\"0\" :max=\"512\" :step=\"10\" style=\"width: 100%\" />\n                </n-form-item>\n              </template>\n\n              <n-form-item label=\"保留标题结构\" style=\"margin-top: 8px\">\n                <n-switch v-model:value=\"config.chunking.preserveHeadings\" />\n              </n-form-item>\n              <n-form-item label=\"保留代码块\">\n                <n-switch v-model:value=\"config.chunking.preserveCodeBlocks\" />\n              </n-form-item>\n            </n-form>\n          </n-card>\n        </n-tab-pane>\n          <n-tab-pane name=\"search\" tab=\"搜索设置\">\n          <n-card>\n            <n-form label-placement=\"left\" label-width=\"140\">\n              <n-form-item label=\"默认返回数量\">\n                <n-input-number v-model:value=\"config.search.defaultTopK\" :min=\"1\" :max=\"100\" style=\"width: 100%\" />\n              </n-form-item>\n              <n-form-item label=\"上下文窗口\">\n                <n-input-number v-model:value=\"config.search.contextWindow\" :min=\"0\" :max=\"5\" style=\"width: 100%\" />\n              </n-form-item>\n              <n-form-item label=\"相似度阈值\">\n                <n-slider v-model:value=\"thresholdPercent\" :min=\"0\" :max=\"100\" :step=\"1\" />\n              </n-form-item>\n              <n-form-item label=\"启用 MMR 多样性\">\n                <n-switch v-model:value=\"config.search.enableMmr\" />\n              </n-form-item>\n              <n-form-item v-if=\"config.search.enableMmr\" label=\"MMR Lambda\">\n                <n-slider v-model:value=\"mmrLambdaPercent\" :min=\"0\" :max=\"100\" :step=\"1\" />\n              </n-form-item>\n              <n-form-item label=\"默认搜索源\">\n                <n-select\n                  v-model:value=\"config.search.defaultSources\"\n                  multiple\n                  placeholder=\"全部源\"\n                  clearable\n                  :options=\"sourceNameOptions\"\n                />\n              </n-form-item>\n\n              <n-divider>知识图谱扩展召回</n-divider>\n\n              <n-form-item label=\"启用图扩展\">\n                <n-space align=\"center\">\n                  <n-switch v-model:value=\"config.search.enableGraphExpansion\" />\n                  <n-text depth=\"3\" style=\"font-size: 12px\">\n                    {{ config.search.enableGraphExpansion\n                      ? '检索时自动补充 wiki-link 关联文档（需先完成索引建图）'\n                      : '不使用知识图谱扩展' }}\n                  </n-text>\n                </n-space>\n              </n-form-item>\n\n              <template v-if=\"config.search.enableGraphExpansion\">\n                <n-form-item label=\"遍历深度\">\n                  <n-input-number\n                    v-model:value=\"config.search.graphExpansionDepth\"\n                    :min=\"1\" :max=\"2\" style=\"width: 120px\"\n                  />\n                  <n-text depth=\"3\" style=\"margin-left:8px;font-size:12px\">层（推荐 1）</n-text>\n                </n-form-item>\n                <n-form-item label=\"最大邻居节点数\">\n                  <n-input-number\n                    v-model:value=\"config.search.graphExpansionMaxNodes\"\n                    :min=\"1\" :max=\"10\" style=\"width: 120px\"\n                  />\n                  <n-text depth=\"3\" style=\"margin-left:8px;font-size:12px\">个/结果（推荐 3）</n-text>\n                </n-form-item>\n              </template>\n\n              <n-divider>检索弹性熔断与多样性采样</n-divider>\n\n              <n-form-item label=\"故障自动降级\">\n                <n-space align=\"center\">\n                  <n-switch v-model:value=\"config.search.enableAutomaticFallback\" />\n                  <n-text depth=\"3\" style=\"font-size: 12px\">\n                    {{ config.search.enableAutomaticFallback ? '开启（推荐）：当密集向量计算超时或模型异常时，自动无感降级为词法检索并触发重试保护' : '关闭：遇到向量异常直接抛出错误' }}\n                  </n-text>\n                </n-space>\n              </n-form-item>\n              <n-form-item label=\"连续失败熔断阈值\">\n                <n-input-number v-model:value=\"config.search.circuitBreakerThreshold\" :min=\"1\" :max=\"20\" style=\"width: 200px\" />\n                <n-text depth=\"3\" style=\"font-size: 12px; margin-left: 8px\">次（连续失败达标后熔断进入冷静期）</n-text>\n              </n-form-item>\n              <n-form-item label=\"熔断冷却时长\">\n                <n-input-number v-model:value=\"config.search.circuitBreakerDurationSeconds\" :min=\"5\" :max=\"300\" :step=\"5\" style=\"width: 200px\" />\n                <n-text depth=\"3\" style=\"font-size: 12px; margin-left: 8px\">秒（熔断期间直接路由词法引擎，冷却后半开探针恢复）</n-text>\n              </n-form-item>\n              <n-form-item label=\"DPP 多样性采样\">\n                <n-space align=\"center\">\n                  <n-switch v-model:value=\"config.search.enableDppDiversity\" />\n                  <n-text depth=\"3\" style=\"font-size: 12px\">\n                    {{ config.search.enableDppDiversity ? '开启：采用行列式点过程 (DPP) 重排，在高召回场景深度消减语义冗余度' : '关闭：使用传统 MMR 线性打分' }}\n                  </n-text>\n                </n-space>\n              </n-form-item>\n              <n-form-item label=\"统计伪相关反馈 (PRF)\">\n                <n-space align=\"center\">\n                  <n-switch v-model:value=\"config.search.enableStatisticalPrf\" />\n                  <n-text depth=\"3\" style=\"font-size: 12px\">\n                    {{ config.search.enableStatisticalPrf ? '开启：长句与复杂提问时自动基于 Top-K 词频互信息扩展检索词' : '关闭' }}\n                  </n-text>\n                </n-space>\n              </n-form-item>\n              <n-form-item v-if=\"config.search.enableStatisticalPrf\" label=\"PRF 扩展词上限\">\n                <n-input-number v-model:value=\"config.search.prfMaxExpansionTerms\" :min=\"1\" :max=\"10\" style=\"width: 200px\" />\n                <n-text depth=\"3\" style=\"font-size: 12px; margin-left: 8px\">个词</n-text>\n              </n-form-item>\n\n              <n-divider>认知增强词法检索 (Cognitive-Enhanced Lexical)</n-divider>\n              <n-form-item label=\"认知判别后端\">\n                <n-radio-group v-model:value=\"config.search.cognitiveLexical.backend\" size=\"medium\">\n                  <n-space vertical>\n                    <n-radio value=\"Disabled\">\n                      <strong>纯本地极速词法 (BM25 + DPP)</strong>\n                      <div style=\"font-size: 12px; opacity: 0.75;\">0 显存，&lt;5ms 超低延迟，字面匹配 + 稀疏核多样性去重</div>\n                    </n-radio>\n                    <n-radio value=\"CrossEncoder\">\n                      <strong>Cross-Encoder 重排模型 (本地 / 在线)</strong>\n                      <n-tag size=\"tiny\" type=\"primary\" round style=\"margin-left: 6px;\">推荐</n-tag>\n                      <div style=\"font-size: 12px; opacity: 0.75;\">复用系统重排模型，深度交叉语义注意力，支持完全离线运行</div>\n                    </n-radio>\n                    <n-radio value=\"Jev\">\n                      <strong>TypeSafe AI Jev (System One)</strong>\n                      <n-tag size=\"tiny\" type=\"info\" round style=\"margin-left: 6px;\">SAG2 原生方案</n-tag>\n                      <div style=\"font-size: 12px; opacity: 0.75;\">云端轻量极速事实必要性概率推演，0 本地显存占用</div>\n                    </n-radio>\n                    <n-radio value=\"Statistical\">\n                      <strong>启发式规则与统计增强 (Heuristic PRF)</strong>\n                      <div style=\"font-size: 12px; opacity: 0.75;\">零 AI 模型开销，基于上下文连续性与专有名词衔接扩展</div>\n                    </n-radio>\n                  </n-space>\n                </n-radio-group>\n              </n-form-item>\n\n              <template v-if=\"config.search.cognitiveLexical.backend !== 'Disabled'\">\n                <n-form-item label=\"多跳推理深度\">\n                  <n-radio-group v-model:value=\"config.search.cognitiveLexical.maxHops\" size=\"small\">\n                    <n-radio-button :value=\"1\">单跳精排 (1 Hop - 极速)</n-radio-button>\n                    <n-radio-button :value=\"2\">标准多跳 (2 Hops - 推荐)</n-radio-button>\n                    <n-radio-button :value=\"3\">深度多跳 (3 Hops - 复杂实体)</n-radio-button>\n                  </n-radio-group>\n                </n-form-item>\n                <n-form-item label=\"证据保留阈值\">\n                  <n-space align=\"center\" style=\"width: 100%\">\n                    <n-slider v-model:value=\"config.search.cognitiveLexical.acceptanceThreshold\" :min=\"0.1\" :max=\"0.9\" :step=\"0.05\" style=\"width: 240px\" />\n                    <n-input-number v-model:value=\"config.search.cognitiveLexical.acceptanceThreshold\" :min=\"0.1\" :max=\"0.9\" :step=\"0.05\" size=\"small\" style=\"width: 80px\" />\n                    <n-text depth=\"3\" style=\"font-size: 12px\">低于此置信度分值的候选将被剔除（默认 0.50）</n-text>\n                  </n-space>\n                </n-form-item>\n                <n-form-item label=\"早停强置信阈值\">\n                  <n-space align=\"center\" style=\"width: 100%\">\n                    <n-slider v-model:value=\"config.search.cognitiveLexical.earlyExitThreshold\" :min=\"0.6\" :max=\"0.99\" :step=\"0.05\" style=\"width: 240px\" />\n                    <n-input-number v-model:value=\"config.search.cognitiveLexical.earlyExitThreshold\" :min=\"0.6\" :max=\"0.99\" :step=\"0.05\" size=\"small\" style=\"width: 80px\" />\n                    <n-text depth=\"3\" style=\"font-size: 12px\">Hop 0 命中强置信证据时跳过多跳直接输出（默认 0.85）</n-text>\n                  </n-space>\n                </n-form-item>\n                <n-form-item label=\"异常自动降级\">\n                  <n-space align=\"center\">\n                    <n-switch v-model:value=\"config.search.cognitiveLexical.enableFallback\" />\n                    <n-text depth=\"3\" style=\"font-size: 12px\">若后端模型超时或抛错，自动降级为纯 BM25 检索，确保业务不中断</n-text>\n                  </n-space>\n                </n-form-item>\n                <n-form-item label=\"后端连通性测试\">\n                  <n-space vertical style=\"width: 100%\">\n                    <n-button type=\"info\" secondary :loading=\"testingCognitiveBackend\" @click=\"handleTestCognitiveBackend\">\n                      测试当前认知后端连通性与打分\n                    </n-button>\n                    <n-alert v-if=\"cognitiveBackendTestResult\" :type=\"cognitiveBackendTestResult.success ? 'success' : 'error'\" style=\"margin-top: 8px\">\n                      <template #header>{{ cognitiveBackendTestResult.success ? '认知后端测试通过' : '认知后端测试异常' }}</template>\n                      <div>{{ cognitiveBackendTestResult.message }}</div>\n                      <div v-if=\"cognitiveBackendTestResult.sampleResults\" style=\"margin-top: 6px; font-size: 12px\">\n                        <div v-for=\"s in cognitiveBackendTestResult.sampleResults\" :key=\"s.chunkId\">\n                          • <b>{{ s.title }}</b>: 得分 {{ s.score.toFixed(3) }} ({{ s.isAccepted ? '保留' : '剔除' }}) - {{ s.reason }}\n                        </div>\n                      </div>\n                    </n-alert>\n                  </n-space>\n                </n-form-item>\n              </template>\n\n            </n-form>\n          </n-card>\n        </n-tab-pane>\n        </n-tabs>\n      </div>\n\n      <!-- 二级面板 3: 系统与存储 -->\n      <div v-show=\"activeGroup === 'system'\">\n        <n-tabs v-model:value=\"activeTab\" type=\"line\" animated @update:value=\"onTabChange\">\n          <n-tab-pane name=\"service\" tab=\"服务设置\">\n          <n-card>\n            <n-form label-placement=\"left\" label-width=\"140\">\n              <n-form-item v-if=\"isDesktop\" label=\"启用 HTTP 服务\"><n-space align=\"center\"><n-switch v-model:value=\"config.service.enableHttpService\" /><n-text depth=\"3\" style=\"font-size: 12px\">{{ config.service.enableHttpService? '开启后额外监听 HTTP，可用浏览器/curl/Swagger 调试 — 修改后需重启': '关闭（默认）：仅进程内通信，不占用端口' }}</n-text></n-space></n-form-item><n-form-item v-if=\"isDesktop && !config.service.enableHttpService\" label=\"\"><n-text depth=\"3\" style=\"font-size: 12px;color: var(--n-warning-color)\">桌面端当前未监听 HTTP 端口，下方端口/外网访问设置不生效</n-text></n-form-item><n-form-item label=\"监听端口\">\n                <n-input-number v-model:value=\"config.service.port\" :min=\"1024\" :max=\"65535\" style=\"width: 200px\" />\n              </n-form-item>\n              <n-form-item label=\"允许外网访问\">\n                <n-space align=\"center\">\n                  <n-switch\n                    v-model:value=\"config.service.allowNetworkAccess\"\n                    @update:value=\"onNetworkAccessChange\"\n                  />\n                  <n-text depth=\"3\" style=\"font-size: 12px\">\n                    {{ config.service.allowNetworkAccess\n                      ? '监听 0.0.0.0（局域网/外网可访问）— 修改后需重启'\n                      : '仅监听 localhost（本机访问）' }}\n                  </n-text>\n                </n-space>\n              </n-form-item>\n              <n-form-item label=\"启用 CORS\">\n                <n-switch v-model:value=\"config.service.enableCors\" />\n              </n-form-item>\n              <n-form-item label=\"启用 Swagger\">\n                <n-switch v-model:value=\"config.service.enableSwagger\" />\n              </n-form-item>\n              <n-form-item label=\"日志级别\">\n                <n-select\n                  v-model:value=\"config.service.logLevel\"\n                  :options=\"logLevelOptions\"\n                  style=\"width: 200px\"\n                />\n              </n-form-item>\n              <n-form-item label=\"API Key\">\n                <n-input-group>\n                  <n-input\n                    v-model:value=\"config.service.apiKey\"\n                    type=\"password\"\n                    show-password-on=\"click\"\n                    placeholder=\"留空则不启用认证\"\n                    style=\"width: 300px\"\n                  />\n                  <n-button\n                    v-if=\"config.service.apiKey\"\n                    type=\"warning\"\n                    style=\"margin-left: 8px\"\n                    @click=\"config.service.apiKey = ''\"\n                  >\n                    清除\n                  </n-button>\n                </n-input-group>\n              </n-form-item>\n              <n-form-item label=\"\">\n                <n-text depth=\"3\" style=\"font-size: 12px\">\n                  {{ config.service.apiKey ? 'API Key 已设置，写操作需要认证' : '未设置 API Key，所有接口无需认证' }}\n                </n-text>\n              </n-form-item>\n            </n-form>\n          </n-card>\n        </n-tab-pane>\n          <n-tab-pane name=\"network\" tab=\"网络与代理\">\n          <n-card>\n            <n-alert type=\"info\" style=\"margin-bottom: 16px\">\n              <template #header>系统统一网络代理设置</template>\n              配置全局 HTTP/HTTPS/SOCKS5 代理。各功能模块（如 System One 认知决策引擎、模型下载等）默认继承此处的统一代理，有效消除海外服务 HTTP 451 区域封锁。\n            </n-alert>\n            <n-form label-placement=\"left\" label-width=\"140\">\n              <n-form-item label=\"启用统一代理\">\n                <n-space align=\"center\">\n                  <n-switch v-model:value=\"config.network.proxy.enabled\" />\n                  <n-text depth=\"3\" style=\"font-size: 12px\">{{ config.network.proxy.enabled ? '已开启统一代理热重载' : '未启用（默认直连）' }}</n-text>\n                </n-space>\n              </n-form-item>\n              <n-form-item label=\"代理服务器地址\">\n                <n-input v-model:value=\"config.network.proxy.proxyUrl\" placeholder=\"例如 http://127.0.0.1:7890 或 socks5://127.0.0.1:1080\" />\n              </n-form-item>\n              <n-form-item label=\"免代理白名单\">\n                <n-input v-model:value=\"config.network.proxy.bypassHosts\" placeholder=\"分号分隔，例如 localhost;127.0.0.1;*.local;api.siliconflow.cn\" />\n              </n-form-item>\n              <n-form-item label=\"代理认证用户名\">\n                <n-input v-model:value=\"config.network.proxy.username\" placeholder=\"可选，留空则免密\" style=\"width: 260px\" />\n              </n-form-item>\n              <n-form-item label=\"代理认证密码\">\n                <n-input v-model:value=\"config.network.proxy.password\" type=\"password\" show-password-on=\"click\" placeholder=\"可选认证密码\" style=\"width: 260px\" />\n              </n-form-item>\n              <n-form-item label=\"连通性探测\">\n                <n-space vertical style=\"width: 100%\">\n                  <n-button type=\"info\" secondary :loading=\"testingProxy\" @click=\"handleTestProxy\">测试代理连通性</n-button>\n                  <n-alert v-if=\"proxyTestResult\" :type=\"proxyTestResult.success ? 'success' : 'error'\" style=\"margin-top: 8px\">\n                    <template #header>{{ proxyTestResult.success ? '代理探测成功' : '代理探测失败' }}</template>\n                    <div>{{ proxyTestResult.message || proxyTestResult.error }} (耗时: {{ proxyTestResult.latencyMs }}ms)</div>\n                  </n-alert>\n                </n-space>\n              </n-form-item>\n            </n-form>\n          </n-card>\n        </n-tab-pane>\n          <n-tab-pane name=\"data\" tab=\"数据路径\">\n          <n-card>\n            <n-form label-placement=\"left\" label-width=\"140\">\n              <n-form-item label=\"数据存储路径\">\n                <n-input v-model:value=\"config.dataPath\" placeholder=\"data\" />\n              </n-form-item>\n              <n-form-item label=\"模型存储路径\">\n                <n-input v-model:value=\"config.modelsRootPath\" placeholder=\"默认: models\" />\n              </n-form-item>\n            </n-form>\n          </n-card>\n        </n-tab-pane>\n        </n-tabs>\n      </div>\n\n      <!-- Save Button -->\n      <n-space style=\"margin-top: 16px\" justify=\"space-between\" align=\"center\">\n        <n-text depth=\"3\" style=\"font-size: 12px\">\n          聊天模型、嵌入/重排模型及其他配置已支持即时热生效；仅修改服务端口或外网访问需重启服务\n        </n-text>\n        <n-button type=\"primary\" :loading=\"saving\" @click=\"handleSave\">保存配置</n-button>\n      </n-space>\n    </n-spin>\n  </n-space>\n";
export default component;
