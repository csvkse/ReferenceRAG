import { defineComponent as _defineComponent } from 'vue';
import { ref, computed, onMounted } from 'vue';
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
        const loadDownloadedChatModels = async () => {
            chatModelLoading.value = true;
            try {
                const res = await modelsApi.getDownloadedChatModels();
                downloadedChatModels.value = res.data || [];
                if (downloadedChatModels.value.length > 0 && config.value?.chat?.localGguf) {
                    if (!config.value.chat.localGguf.modelName) {
                        config.value.chat.localGguf.modelName = downloadedChatModels.value[0].name;
                    }
                }
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
        const cudaAvailable = ref(true);
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
                graphExpansionMaxNodes: 3
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
            chat: {
                activeBackend: 'OpenAiApi',
                systemPrompt: '你是 ReferenceRAG 智能助手。',
                temperature: 0.7,
                topP: 0.9,
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
                    if (config.value.chat.idleTimeoutSeconds === undefined) {
                        config.value.chat.idleTimeoutSeconds = 300;
                    }
                    if (config.value.chat.temperature === undefined) {
                        config.value.chat.temperature = 0.7;
                    }
                    if (config.value.chat.topP === undefined) {
                        config.value.chat.topP = 0.9;
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
            try {
                const response = await settingsApi.getHardwareAccelerationStatus();
                const data = response.data;
                hardwareStatus.value = data;
                cudaAvailable.value = !!(data.hasNvidiaGpu && data.availableBackends?.some(b => b.backend === 'Cuda' && b.supported));
                cudaInfo.value = {
                    isAvailable: cudaAvailable.value,
                    message: data.message || '硬件加速探针就绪'
                };
            }
            catch (err) {
                try {
                    const fallback = await settingsApi.getCudaAvailability();
                    cudaAvailable.value = fallback.data.isAvailable;
                    cudaInfo.value = fallback.data;
                } catch (fallbackErr) {
                    cudaAvailable.value = false;
                    const detail = err?.response?.data?.message || err?.message || '探测接口异常';
                    cudaInfo.value = { isAvailable: false, message: `无法探测硬件加速状态 (${detail})` };
                }
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
                if (config.value.chat?.activeBackend === 'LocalGguf') {
                    const localName = config.value.chat.localGguf?.modelName;
                    if (localName) {
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
            models: ['chat', 'embedding', 'rerank', 'retention', 'modelHub'],
            retrieval: ['chunking', 'search'],
            system: ['service', 'data']
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

        onMounted(() => {
            initTabFromUrl();
            loadConfig();
            loadSourceNames();
            loadHardwareStatus();
            loadChatConfig();
            loadDownloadedChatModels();
        });
        const __returned__ = { message, authStore, isDesktop, loading, saving, downloadedChatModels, chatModelLoading, loadDownloadedChatModels, chatModelOptions, onLocalChatModelSelect, chatConfig, loadChatConfig, handleSaveChat, sourceNames, cudaAvailable, cudaInfo, hardwareStatus, backendOptions, originalModelsRootPath, defaultConfig, config, verifyingHfToken, hfTokenStatus, hfEndpointOptions, handleVerifyHfToken, thresholdPercent, mmrLambdaPercent, sourceNameOptions, logLevelOptions, loadConfig, loadSourceNames, loadHardwareStatus, loadCudaAvailability, networkAccessChanged, onNetworkAccessChange, normalizeModelsRootPath, handleSave, adaptiveLevelOptions, adaptivePreview, chatPerformancePreview, updateChatPerformancePreview, activeGroup, activeTab, onGroupChange, onTabChange, syncTabToUrl, initTabFromUrl };
        return __returned__;
    }
});

component.template = "\n  <n-space vertical :size=\"20\">\n    <n-spin :show=\"loading\">\n      <!-- 一级分类导航条 (Segmented 胶囊设计) -->\n      <div style=\"display: flex; align-items: center; justify-content: flex-start; margin-bottom: 14px;\">\n        <n-tabs\n          v-model:value=\"activeGroup\"\n          type=\"segment\"\n          size=\"medium\"\n          @update:value=\"onGroupChange\"\n          style=\"max-width: 540px;\"\n        >\n          <n-tab name=\"models\">🤖 模型与推理</n-tab>\n          <n-tab name=\"retrieval\">🔍 检索与分段</n-tab>\n          <n-tab name=\"system\">⚙️ 系统与存储</n-tab>\n        </n-tabs>\n      </div>\n\n      <!-- 二级面板 1: 模型与推理 -->\n      <div v-show=\"activeGroup === 'models'\">\n        <n-tabs v-model:value=\"activeTab\" type=\"line\" animated @update:value=\"onTabChange\">\n          <n-tab-pane name=\"chat\" tab=\"聊天模型\">\n          <n-card>\n            <n-alert type=\"info\" style=\"margin-bottom: 16px\">\n              <template #header>AI 对话模型后端与硬件加速隔离配置</template>\n              支持本地 GGUF 大模型（借助 llama.cpp/DirectML/CUDA 本地离线推理）与在线 OpenAI 兼容 API 互相切换，两套后端参数互相隔离独立保存。\n            </n-alert>\n            <n-form label-placement=\"left\" label-width=\"140\">\n              <n-form-item label=\"推理模式\">\n                <n-radio-group v-model:value=\"config.chat.activeBackend\" size=\"small\">\n                  <n-radio-button value=\"LocalGguf\">本地 GGUF</n-radio-button>\n                  <n-radio-button value=\"OpenAiApi\">OpenAI 兼容 API</n-radio-button>\n                </n-radio-group>\n              </n-form-item>\n\n              <!-- 本地 GGUF 专属配置 -->\n              <template v-if=\"config.chat.activeBackend === 'LocalGguf'\">\n                <n-form-item label=\"模型名称\">\n                  <n-space vertical style=\"width: 100%\">\n                    <n-select\n                      v-model:value=\"config.chat.localGguf.modelName\"\n                      :options=\"chatModelOptions\"\n                      :loading=\"chatModelLoading\"\n                      placeholder=\"请选择已下载的 GGUF 聊天模型\"\n                      @update:value=\"onLocalChatModelSelect\"\n                    />\n                    <n-alert v-if=\"downloadedChatModels.length === 0 && !chatModelLoading\" type=\"warning\" style=\"margin-top: 6px\">\n                      尚未检测到已下载的本地 GGUF 对话模型。请前往「模型管理」页面搜索并下载 Qwen、Llama 等对话模型变体。\n                    </n-alert>\n                  </n-space>\n                </n-form-item>\n                <n-form-item label=\"硬件加速方案\">\n                  <n-select v-model:value=\"config.chat.localGguf.acceleration.backend\" :options=\"backendOptions\" @update:value=\"updateChatPerformancePreview\" style=\"width: 100%\" />\n                </n-form-item>\n                <n-form-item v-if=\"hardwareStatus\" label=\"硬件就绪状态\">\n                  <n-alert :type=\"hardwareStatus.activeBackend === 'Cpu' ? 'warning' : 'success'\" :title=\"hardwareStatus.message\" style=\"width: 100%\">\n                    <div v-if=\"hardwareStatus.primaryGpu\">检测设备: {{ hardwareStatus.primaryGpu.name }} {{ (hardwareStatus.primaryGpu.totalVramGb && hardwareStatus.primaryGpu.totalVramGb > 0) ? '(' + hardwareStatus.primaryGpu.totalVramGb + ' GB 显存)' : '(集成核显 / 共享系统内存)' }}</div>\n                    <div style=\"font-size: 12px; margin-top: 4px; opacity: 0.85\">当前就绪后端: {{ hardwareStatus.activeBackend }} | 自动回退保护: 开启</div>\n                    <div v-if=\"hardwareStatus.activeBackendReason && hardwareStatus.activeBackend === 'Cpu'\" style=\"font-size: 12px; margin-top: 3px; opacity: 0.9\">诊断详情: {{ hardwareStatus.activeBackendReason }}</div>\n                  </n-alert>\n                </n-form-item>\n                <n-form-item label=\"性能规划模式\">\n                  <n-radio-group v-model:value=\"config.chat.localGguf.performanceMode\" size=\"medium\" @update:value=\"updateChatPerformancePreview\">\n                    <n-radio-button value=\"Adaptive\">自适应规划 (Adaptive - 智能推荐)</n-radio-button>\n                    <n-radio-button value=\"Fixed\">固定参数 (Fixed - 手动精调)</n-radio-button>\n                  </n-radio-group>\n                </n-form-item>\n                <template v-if=\"config.chat.localGguf.performanceMode === 'Adaptive'\">\n                  <n-form-item label=\"自适应偏好策略\">\n                    <n-radio-group v-model:value=\"config.chat.localGguf.adaptiveStrategy\" size=\"small\" @update:value=\"updateChatPerformancePreview\">\n                      <n-radio-button value=\"Balanced\">通用平衡 (Balanced - 8K)</n-radio-button>\n                      <n-radio-button value=\"HighThroughput\">极速吞吐 (High Throughput - 4K)</n-radio-button>\n                      <n-radio-button value=\"LongContext\">深度长文 (Long Context - 16K~32K)</n-radio-button>\n                      <n-radio-button value=\"LowMemory\">轻量低显存 (Low Memory - 2K~4K)</n-radio-button>\n                    </n-radio-group>\n                  </n-form-item>\n                  <n-card size=\"small\" embedded style=\"background: rgba(0, 0, 0, 0.02); margin-bottom: 16px; border-left: 3px solid var(--n-primary-color)\">\n                    <n-space vertical :size=\"8\">\n                      <n-space justify=\"space-between\" align=\"center\">\n                        <n-text strong style=\"font-size: 13px\">实时模型性能画像测算看板 (Live Sweet-Spot Preview)</n-text>\n                        <n-tag size=\"small\" type=\"success\" :bordered=\"false\" round>规划算法: 启发式多维策略</n-tag>\n                      </n-space>\n                      <n-space align=\"center\" :size=\"16\" style=\"font-size: 12px; color: #555\">\n                        <span>推荐上下文: <b style=\"color: var(--n-primary-color)\">{{ chatPerformancePreview.contextSize }} tokens</b></span>\n                        <span>批处理吞吐: <b>{{ chatPerformancePreview.batchSize }}</b></span>\n                        <span>微批大小: <b>{{ chatPerformancePreview.uBatchSize }}</b></span>\n                        <span>GPU 卸载层数: <b>{{ chatPerformancePreview.gpuLayers === -1 ? '全层卸载 (Auto)' : chatPerformancePreview.gpuLayers + ' 层' }}</b></span>\n                      </n-space>\n                      <n-space align=\"center\" :size=\"16\" style=\"font-size: 12px\">\n                        <n-tag size=\"small\" type=\"info\" :bordered=\"false\" round>预估显存: {{ chatPerformancePreview.estimatedVramText }}</n-tag>\n                        <n-text depth=\"3\" style=\"font-size: 12px\">{{ chatPerformancePreview.rationale }}</n-text>\n                      </n-space>\n                      <n-alert v-if=\"chatPerformancePreview.warning\" type=\"warning\" size=\"small\" style=\"margin-top: 4px\">\n                        {{ chatPerformancePreview.warning }}\n                      </n-alert>\n                    </n-space>\n                  </n-card>\n                </template>\n                <template v-else>\n                  <n-form-item label=\"GPU 卸载层数\">\n                    <n-space align=\"center\" style=\"width: 100%\">\n                      <n-slider v-model:value=\"config.chat.localGguf.gpuLayers\" :min=\"-1\" :max=\"99\" :step=\"1\" @update:value=\"updateChatPerformancePreview\" style=\"width: 320px\" />\n                      <n-input-number v-model:value=\"config.chat.localGguf.gpuLayers\" :min=\"-1\" :max=\"99\" @update:value=\"updateChatPerformancePreview\" style=\"width: 100px\" />\n                      <n-text depth=\"3\" style=\"font-size: 12px\">层（-1 为全量卸载至显存，0 为纯 CPU 运算）</n-text>\n                    </n-space>\n                  </n-form-item>\n                  <n-form-item label=\"上下文窗口大小\">\n                    <n-space align=\"center\" style=\"width: 100%\">\n                      <n-input-number v-model:value=\"config.chat.localGguf.contextSize\" :min=\"512\" :max=\"65536\" :step=\"512\" @update:value=\"updateChatPerformancePreview\" style=\"width: 200px\" />\n                      <n-text depth=\"3\" style=\"font-size: 12px; margin-left: 8px\">tokens（KV Cache 随长度线性增加，建议 4096~16384）</n-text>\n                    </n-space>\n                  </n-form-item>\n                  <n-form-item label=\"批处理大小 (Batch)\">\n                    <n-space align=\"center\" style=\"width: 100%\">\n                      <n-input-number v-model:value=\"config.chat.localGguf.batchSize\" :min=\"128\" :max=\"4096\" :step=\"128\" @update:value=\"updateChatPerformancePreview\" style=\"width: 200px\" />\n                      <n-text depth=\"3\" style=\"font-size: 12px; margin-left: 8px\">tokens（Prompt 预处理批大小，推荐 1024~2048）</n-text>\n                    </n-space>\n                  </n-form-item>\n                  <n-form-item label=\"微批大小 (uBatch)\">\n                    <n-space align=\"center\" style=\"width: 100%\">\n                      <n-input-number v-model:value=\"config.chat.localGguf.uBatchSize\" :min=\"64\" :max=\"2048\" :step=\"64\" @update:value=\"updateChatPerformancePreview\" style=\"width: 200px\" />\n                      <n-text depth=\"3\" style=\"font-size: 12px; margin-left: 8px\">tokens（物理执行单步微批大小，推荐 256~512）</n-text>\n                    </n-space>\n                  </n-form-item>\n                  <n-card size=\"small\" embedded style=\"background: rgba(0, 0, 0, 0.02); margin-bottom: 16px; border-left: 3px solid #f0a020\">\n                    <n-space vertical :size=\"8\">\n                      <n-space justify=\"space-between\" align=\"center\">\n                        <n-text strong style=\"font-size: 13px\">手动参数显存校验 (VRAM Safety Check)</n-text>\n                        <n-tag size=\"small\" :type=\"chatPerformancePreview.warning ? 'warning' : 'success'\" :bordered=\"false\" round>\n                          {{ chatPerformancePreview.warning ? '存在显存压力告警' : '参数在安全边界内' }}\n                        </n-tag>\n                      </n-space>\n                      <n-space align=\"center\" :size=\"16\" style=\"font-size: 12px\">\n                        <n-tag size=\"small\" type=\"info\" :bordered=\"false\" round>预估显存占用: {{ chatPerformancePreview.estimatedVramText }}</n-tag>\n                        <n-text depth=\"3\" style=\"font-size: 12px\">{{ chatPerformancePreview.rationale }}</n-text>\n                      </n-space>\n                      <n-alert v-if=\"chatPerformancePreview.warning\" type=\"warning\" size=\"small\" style=\"margin-top: 4px\">\n                        {{ chatPerformancePreview.warning }}\n                      </n-alert>\n                    </n-space>\n                  </n-card>\n                </template>\n                <n-form-item label=\"空闲自动卸载\">\n                  <n-input-number v-model:value=\"config.chat.idleTimeoutSeconds\" :min=\"30\" :max=\"3600\" :step=\"30\" style=\"width: 200px\" />\n                  <n-text depth=\"3\" style=\"font-size: 12px; margin-left: 8px\">秒（默认 300 秒无请求自动释放显存）</n-text>\n                </n-form-item>\n              </template>\n\n              <!-- API 专属配置 -->\n              <template v-else>\n                <n-form-item label=\"API 地址\">\n                  <n-input v-model:value=\"config.chat.openAiApi.endpoint\" placeholder=\"https://api.openai.com/v1\" />\n                </n-form-item>\n                <n-form-item label=\"API Key\">\n                  <n-input-group style=\"width: 100%\">\n                    <n-input\n                      v-model:value=\"config.chat.openAiApi.apiKey\"\n                      type=\"password\"\n                      show-password-on=\"click\"\n                      :placeholder=\"chatConfig.apiKeyConfigured ? '已配置密钥（如不修改请留空保持）' : '请输入 API Key（如 sk-...）'\"\n                    />\n                    <n-button\n                      v-if=\"config.chat.openAiApi.apiKey || chatConfig.apiKeyConfigured\"\n                      type=\"warning\"\n                      style=\"margin-left: 8px\"\n                      @click=\"config.chat.openAiApi.apiKey = '__CLEAR__'; chatConfig.apiKey = '__CLEAR__'; chatConfig.apiKeyConfigured = false; message.info('已标记清除 API Key，保存后生效');\"\n                    >\n                      清除\n                    </n-button>\n                  </n-input-group>\n                </n-form-item>\n                <n-form-item label=\"\">\n                  <n-text depth=\"3\" style=\"font-size: 12px\">\n                    {{ config.chat.openAiApi.apiKey === '__CLEAR__' ? '⚠️ 已标记清除 API Key，保存后将移除该密钥' : (config.chat.openAiApi.apiKey ? '已输入新密钥，保存后更新' : (chatConfig.apiKeyConfigured ? '✅ 已配置有效 API Key（出于安全脱敏显示，留空保存将保持原有密钥）' : '未配置 API Key')) }}\n                  </n-text>\n                </n-form-item>\n                <n-form-item label=\"模型名称\">\n                  <n-input v-model:value=\"config.chat.openAiApi.model\" placeholder=\"gpt-4o-mini / qwen-plus\" />\n                </n-form-item>\n                <n-form-item label=\"请求超时时间\">\n                  <n-input-number v-model:value=\"config.chat.openAiApi.timeoutSeconds\" :min=\"10\" :max=\"300\" style=\"width: 200px\" />\n                  <n-text depth=\"3\" style=\"font-size: 12px; margin-left: 8px\">秒（默认 60 秒）</n-text>\n                </n-form-item>\n              </template>\n\n              <!-- 通用对话推理参数 -->\n              <n-divider>对话推理参数</n-divider>\n              <n-form-item label=\"系统提示词\">\n                <n-input\n                  v-model:value=\"config.chat.systemPrompt\"\n                  type=\"textarea\"\n                  :autosize=\"{ minRows: 3, maxRows: 6 }\"\n                  placeholder=\"你是 ReferenceRAG 智能助手...\"\n                />\n              </n-form-item>\n              <n-form-item label=\"生成温度\">\n                <n-space align=\"center\" style=\"width: 100%\">\n                  <n-slider v-model:value=\"config.chat.temperature\" :min=\"0\" :max=\"2\" :step=\"0.05\" style=\"width: 280px\" />\n                  <n-input-number v-model:value=\"config.chat.temperature\" :min=\"0\" :max=\"2\" :step=\"0.05\" style=\"width: 90px\" />\n                  <n-text depth=\"3\" style=\"font-size: 12px\">较低更确定，较高更多样（默认 0.7）</n-text>\n                </n-space>\n              </n-form-item>\n              <n-form-item label=\"核采样 (Top-P)\">\n                <n-space align=\"center\" style=\"width: 100%\">\n                  <n-slider v-model:value=\"config.chat.topP\" :min=\"0.05\" :max=\"1\" :step=\"0.05\" style=\"width: 280px\" />\n                  <n-input-number v-model:value=\"config.chat.topP\" :min=\"0.05\" :max=\"1\" :step=\"0.05\" style=\"width: 90px\" />\n                  <n-text depth=\"3\" style=\"font-size: 12px\">仅从累积概率达标候选词采样（默认 0.9）</n-text>\n                </n-space>\n              </n-form-item>\n            </n-form>\n          </n-card>\n        </n-tab-pane>\n          <n-tab-pane name=\"embedding\" tab=\"嵌入模型\">\n          <n-card>\n            <n-form label-placement=\"left\" label-width=\"140\">\n              <!-- 推理模式切换 -->\n              <n-form-item label=\"推理模式\">\n                <n-radio-group v-model:value=\"config.embedding.mode\" size=\"small\">\n                  <n-radio-button value=\"onnx\">本地 ONNX</n-radio-button>\n                  <n-radio-button value=\"openai\">OpenAI 兼容 API</n-radio-button>\n                </n-radio-group>\n              </n-form-item>\n\n              <!-- ONNX 模式专属字段 -->\n              <template v-if=\"config.embedding.mode !== 'openai'\">\n                <n-form-item label=\"模型路径\">\n                  <n-input v-model:value=\"config.embedding.localOnnx.modelPath\" placeholder=\"ONNX 模型路径\" />\n                </n-form-item>\n                <n-form-item label=\"模型名称\">\n                  <n-input v-model:value=\"config.embedding.localOnnx.modelName\" placeholder=\"bge-small-zh-v1.5\" />\n                </n-form-item>\n                <n-form-item label=\"硬件加速方案\">\n                  <n-select v-model:value=\"config.embedding.acceleration.backend\" :options=\"backendOptions\" style=\"width: 100%\" />\n                </n-form-item>\n                <n-form-item v-if=\"hardwareStatus\" label=\"硬件就绪状态\">\n                  <n-alert :type=\"hardwareStatus.activeBackend === 'Cpu' ? 'warning' : 'success'\" :title=\"hardwareStatus.message\" style=\"width: 100%\">\n                    <div v-if=\"hardwareStatus.primaryGpu\">检测设备: {{ hardwareStatus.primaryGpu.name }} {{ (hardwareStatus.primaryGpu.totalVramGb && hardwareStatus.primaryGpu.totalVramGb > 0) ? '(' + hardwareStatus.primaryGpu.totalVramGb + ' GB 显存)' : '(集成核显 / 共享系统内存)' }}</div>\n                    <div style=\"font-size: 12px; margin-top: 4px; opacity: 0.85\">当前就绪后端: {{ hardwareStatus.activeBackend }} | 自动回退保护: 开启</div>\n                    <div v-if=\"hardwareStatus.activeBackendReason && hardwareStatus.activeBackend === 'Cpu'\" style=\"font-size: 12px; margin-top: 3px; opacity: 0.9\">诊断详情: {{ hardwareStatus.activeBackendReason }}</div>\n                  </n-alert>\n                </n-form-item>\n                <n-form-item v-else-if=\"cudaInfo\" label=\"CUDA 状态\">\n                  <n-text depth=\"3\" style=\"font-size: 12px\">{{ cudaInfo.message }}</n-text>\n                </n-form-item>\n                <n-form-item v-if=\"config.embedding.acceleration.backend !== 'Cpu'\" label=\"计算设备 ID\">\n                  <n-input-number v-model:value=\"config.embedding.acceleration.deviceId\" :min=\"0\" :max=\"7\" style=\"width: 200px\" />\n                </n-form-item>\n                <n-form-item v-if=\"config.embedding.acceleration.backend === 'Cuda'\" label=\"CUDA 库路径\">\n                  <n-input v-model:value=\"config.embedding.acceleration.customLibraryPath\" placeholder=\"CUDA DLL 所在目录（留空自动探测）\" />\n                </n-form-item>\n                <n-form-item v-if=\"config.embedding.acceleration.backend !== 'Cpu'\" label=\"平滑降级保护\">\n                  <n-space align=\"center\">\n                    <n-switch v-model:value=\"config.embedding.acceleration.fallbackToCpuOnFailure\" />\n                    <n-text depth=\"3\" style=\"font-size: 12px\">硬件加速不可用时自动回退至 CPU 运算</n-text>\n                  </n-space>\n                </n-form-item>\n                <n-form-item label=\"最大序列长度\">\n                  <n-input-number v-model:value=\"config.embedding.maxSequenceLength\" :min=\"32\" :max=\"2048\" style=\"width: 100%\" />\n                </n-form-item>\n              </template>\n\n              <!-- API 模式专属字段 -->\n              <template v-else>\n                <n-form-item label=\"API 地址\">\n                  <n-input v-model:value=\"config.embedding.openAiApi.baseUrl\" placeholder=\"http://localhost:11434/v1\" />\n                </n-form-item>\n                <n-form-item label=\"API Key\">\n                  <n-input v-model:value=\"config.embedding.openAiApi.apiKey\" type=\"password\" show-password-on=\"click\" placeholder=\"留空则不发送 Authorization 头\" />\n                </n-form-item>\n                <n-form-item label=\"模型名称\">\n                  <n-input v-model:value=\"config.embedding.openAiApi.modelName\" placeholder=\"text-embedding-3-small\" />\n                </n-form-item>\n                <n-form-item label=\"向量维度\">\n                  <n-input-number v-model:value=\"config.embedding.openAiApi.dimension\" :min=\"1\" :max=\"8192\" placeholder=\"留空则自动探测\" style=\"width: 100%\" />\n                  <n-text depth=\"3\" style=\"font-size: 12px; margin-left: 8px\">留空将在首次调用时自动探测</n-text>\n                </n-form-item>\n              </template>\n\n              <!-- 两种模式均适用 -->\n              <n-form-item label=\"批处理大小\">\n                <n-input-number v-model:value=\"config.embedding.batchSize\" :min=\"1\" :max=\"256\" style=\"width: 100%\" />\n              </n-form-item>\n            </n-form>\n          </n-card>\n        </n-tab-pane>\n          <n-tab-pane name=\"rerank\" tab=\"重排模型\">\n          <n-card v-if=\"config.rerank\">\n            <n-form label-placement=\"left\" label-width=\"140\">\n              <n-form-item label=\"启用重排\">\n                <n-switch v-model:value=\"config.rerank.enabled\" />\n              </n-form-item>\n\n              <!-- 推理模式切换 -->\n              <n-form-item label=\"推理模式\">\n                <n-radio-group v-model:value=\"config.rerank.mode\" size=\"small\">\n                  <n-radio-button value=\"onnx\">本地 ONNX</n-radio-button>\n                  <n-radio-button value=\"openai\">OpenAI 兼容 API</n-radio-button>\n                </n-radio-group>\n              </n-form-item>\n\n              <!-- ONNX 模式专属字段 -->\n              <template v-if=\"config.rerank.mode !== 'openai'\">\n                <n-form-item label=\"模型名称\">\n                  <n-input v-model:value=\"config.rerank.localOnnx.modelName\" placeholder=\"bge-reranker-base\" />\n                </n-form-item>\n                <n-form-item label=\"当前模型\">\n                  <n-input v-model:value=\"config.rerank.currentModel\" placeholder=\"当前使用的重排模型\" disabled />\n                </n-form-item>\n                <n-form-item label=\"模型路径\">\n                  <n-input v-model:value=\"config.rerank.localOnnx.modelPath\" placeholder=\"重排模型 ONNX 文件路径\" />\n                </n-form-item>\n                <n-form-item label=\"硬件加速方案\">\n                  <n-select v-model:value=\"config.rerank.acceleration.backend\" :options=\"backendOptions\" style=\"width: 100%\" />\n                </n-form-item>\n                <n-form-item v-if=\"hardwareStatus\" label=\"硬件就绪状态\">\n                  <n-alert :type=\"hardwareStatus.activeBackend === 'Cpu' ? 'warning' : 'success'\" :title=\"hardwareStatus.message\" style=\"width: 100%\">\n                    <div v-if=\"hardwareStatus.primaryGpu\">检测设备: {{ hardwareStatus.primaryGpu.name }} {{ (hardwareStatus.primaryGpu.totalVramGb && hardwareStatus.primaryGpu.totalVramGb > 0) ? '(' + hardwareStatus.primaryGpu.totalVramGb + ' GB 显存)' : '(集成核显 / 共享系统内存)' }}</div>\n                    <div style=\"font-size: 12px; margin-top: 4px; opacity: 0.85\">当前就绪后端: {{ hardwareStatus.activeBackend }} | 自动回退保护: 开启</div>\n                    <div v-if=\"hardwareStatus.activeBackendReason && hardwareStatus.activeBackend === 'Cpu'\" style=\"font-size: 12px; margin-top: 3px; opacity: 0.9\">诊断详情: {{ hardwareStatus.activeBackendReason }}</div>\n                  </n-alert>\n                </n-form-item>\n                <n-form-item v-else-if=\"cudaInfo\" label=\"CUDA 状态\">\n                  <n-text depth=\"3\" style=\"font-size: 12px\">{{ cudaInfo.message }}</n-text>\n                </n-form-item>\n                <n-form-item v-if=\"config.rerank.acceleration.backend !== 'Cpu'\" label=\"计算设备 ID\">\n                  <n-input-number v-model:value=\"config.rerank.acceleration.deviceId\" :min=\"0\" :max=\"7\" style=\"width: 200px\" />\n                </n-form-item>\n                <n-form-item v-if=\"config.rerank.acceleration.backend === 'Cuda'\" label=\"CUDA 库路径\">\n                  <n-input v-model:value=\"config.rerank.acceleration.customLibraryPath\" placeholder=\"CUDA DLL 所在目录（留空自动探测）\" />\n                </n-form-item>\n                <n-form-item v-if=\"config.rerank.acceleration.backend !== 'Cpu'\" label=\"平滑降级保护\">\n                  <n-space align=\"center\">\n                    <n-switch v-model:value=\"config.rerank.acceleration.fallbackToCpuOnFailure\" />\n                    <n-text depth=\"3\" style=\"font-size: 12px\">硬件加速不可用时自动回退至 CPU 运算</n-text>\n                  </n-space>\n                </n-form-item>\n              </template>\n\n              <!-- API 模式专属字段 -->\n              <template v-else>\n                <n-form-item label=\"API 地址\">\n                  <n-input v-model:value=\"config.rerank.openAiApi.baseUrl\" placeholder=\"http://localhost:11434/v1\" />\n                </n-form-item>\n                <n-form-item label=\"API Key\">\n                  <n-input v-model:value=\"config.rerank.openAiApi.apiKey\" type=\"password\" show-password-on=\"click\" placeholder=\"留空则不发送 Authorization 头\" />\n                </n-form-item>\n                <n-form-item label=\"模型名称\">\n                  <n-input v-model:value=\"config.rerank.openAiApi.modelName\" placeholder=\"jina-reranker-v2-base-multilingual\" />\n                </n-form-item>\n              </template>\n\n              <!-- 两种模式均适用 -->\n              <n-form-item label=\"重排返回数量\">\n                <n-input-number v-model:value=\"config.rerank.topN\" :min=\"1\" :max=\"100\" style=\"width: 100%\" />\n              </n-form-item>\n              <n-form-item label=\"召回倍数\">\n                <n-input-number v-model:value=\"config.rerank.recallFactor\" :min=\"1\" :max=\"10\" style=\"width: 100%\" />\n                <n-text depth=\"3\" style=\"font-size: 12px; margin-left: 8px\">候选文档数 = TopN × 召回倍数</n-text>\n              </n-form-item>\n            </n-form>\n          </n-card>\n          <n-card v-else>\n            <n-text depth=\"3\">重排模型配置未加载</n-text>\n          </n-card>\n        </n-tab-pane>\n          <n-tab-pane name=\"retention\" tab=\"显存与驻留\">\n          <n-card>\n            <n-alert type=\"info\" style=\"margin-bottom: 16px\">\n              <template #header>模型常规驻留方案与显存自动释放策略</template>\n              控制本地向量嵌入模型（Embedding）与重排模型（Reranker）的生命周期与显存占用策略。修改保存后立即热生效，无需重启服务。\n            </n-alert>\n            <n-form label-placement=\"left\" label-width=\"160\">\n              <n-form-item label=\"驻留方案\">\n                <n-radio-group v-model:value=\"config.modelRetentionStrategy\" size=\"medium\">\n                  <n-radio-button value=\"SlidingExpiration\">滑动过期（按需释放显存）</n-radio-button>\n                  <n-radio-button value=\"Permanent\">固定常驻（零等待高响应）</n-radio-button>\n                </n-radio-group>\n              </n-form-item>\n\n              <template v-if=\"config.modelRetentionStrategy === 'SlidingExpiration'\">\n                <n-form-item label=\"滑动窗口时长\">\n                  <n-input-number\n                    v-model:value=\"config.modelIdleTimeoutSeconds\"\n                    :min=\"10\"\n                    :max=\"3600\"\n                    :step=\"30\"\n                    style=\"width: 200px\"\n                  />\n                  <n-text depth=\"3\" style=\"font-size: 12px; margin-left: 8px\">\n                    秒（默认 300 秒即 5 分钟无调用自动卸载）\n                  </n-text>\n                </n-form-item>\n                <n-form-item label=\"仅最小化托盘生效\">\n                  <n-space align=\"center\">\n                    <n-switch v-model:value=\"config.unloadOnlyWhenMinimizedToTray\" />\n                    <n-text depth=\"3\" style=\"font-size: 12px\">\n                      {{ config.unloadOnlyWhenMinimizedToTray\n                        ? '【推荐】窗口在前台打开使用期间保持常驻（零冷启动秒响应）；仅在最小化到系统托盘或后台时开始滑动倒计时释放显存'\n                        : '窗口在前台或后台均启用滑动计时，只要空闲超时即自动释放显存' }}\n                    </n-text>\n                  </n-space>\n                </n-form-item>\n              </template>\n\n              <template v-else>\n                <n-form-item label=\"方案说明\">\n                  <n-text depth=\"3\" style=\"font-size: 13px\">\n                    固定常驻模式下，嵌入与重排模型在首次请求加载后将始终驻留在显存/内存中，后续所有搜索均零冷启动等待，响应极速。适合显存充裕或需要高频搜索的场景。\n                  </n-text>\n                </n-form-item>\n              </template>\n            </n-form>\n          </n-card>\n        </n-tab-pane>\n          <n-tab-pane name=\"modelHub\" tab=\"模型仓库\">\n          <n-card v-if=\"config.modelHub\">\n            <n-alert type=\"info\" style=\"margin-bottom: 16px\">\n              <template #header>HuggingFace 令牌与模型下载设置</template>\n              当下载受协议限制（如 xVITA-Embed、Llama 等 Gated 模型）或私有授权模型时，需提供 Hugging Face Access Token。<br>\n              国内网络推荐使用高速镜像源（默认 https://hf-mirror.com）。\n            </n-alert>\n            <n-form label-placement=\"left\" label-width=\"140\">\n              <n-form-item label=\"HuggingFace Token\">\n                <n-space vertical style=\"width: 100%\">\n                  <n-input-group style=\"width: 100%\">\n                    <n-input\n                      v-model:value=\"config.modelHub.hfToken\"\n                      type=\"password\"\n                      show-password-on=\"click\"\n                      placeholder=\"例如: hf_xxxxxxxxxxxxxxxxxxxxxxxxxxxx\"\n                      style=\"flex: 1; max-width: 450px\"\n                    />\n                    <n-button\n                      type=\"primary\"\n                      ghost\n                      :loading=\"verifyingHfToken\"\n                      @click=\"handleVerifyHfToken\"\n                      style=\"margin-left: 8px\"\n                    >\n                      验证 Token\n                    </n-button>\n                    <n-button\n                      v-if=\"config.modelHub.hfToken\"\n                      type=\"warning\"\n                      style=\"margin-left: 8px\"\n                      @click=\"config.modelHub.hfToken = ''; hfTokenStatus = null;\"\n                    >\n                      清除\n                    </n-button>\n                  </n-input-group>\n                  <n-alert v-if=\"hfTokenStatus\" :type=\"hfTokenStatus.type\" style=\"margin-top: 6px\">\n                    {{ hfTokenStatus.message }}\n                  </n-alert>\n                  <n-text depth=\"3\" style=\"font-size: 12px\">\n                    {{ config.modelHub.hfToken ? 'Token 已配置，将自动挂载用于 Gated/私有模型下载与授权鉴权' : '未设置 Token，仅能访问无协议限制的公开模型。' }}\n                    获取 Token：可前往 <a href=\"https://huggingface.co/settings/tokens\" target=\"_blank\" style=\"color: var(--n-primary-color); text-decoration: underline\">huggingface.co/settings/tokens</a> 申请（选择 <strong>Read</strong> 权限即可）。\n                  </n-text>\n                </n-space>\n              </n-form-item>\n              <n-form-item label=\"下载端点 / 镜像源\">\n                <n-select\n                  v-model:value=\"config.modelHub.hfEndpoint\"\n                  :options=\"hfEndpointOptions\"\n                  tag\n                  filterable\n                  placeholder=\"https://hf-mirror.com\"\n                  style=\"width: 100%; max-width: 580px\"\n                />\n              </n-form-item>\n              <n-form-item label=\"\">\n                <n-text depth=\"3\" style=\"font-size: 12px\">\n                  推荐使用国内高速镜像源（hf-mirror.com），支持大模型多线程断点分块加速。亦可直接输入自定义反向代理地址。\n                </n-text>\n              </n-form-item>\n              <n-form-item label=\"请求超时时间\">\n                <n-input-number v-model:value=\"config.modelHub.timeoutSeconds\" :min=\"10\" :max=\"300\" style=\"width: 200px\" />\n                <n-text depth=\"3\" style=\"font-size: 12px; margin-left: 8px\">秒（默认 30 秒）</n-text>\n              </n-form-item>\n            </n-form>\n          </n-card>\n        </n-tab-pane>\n        </n-tabs>\n      </div>\n\n      <!-- 二级面板 2: 检索与分段 -->\n      <div v-show=\"activeGroup === 'retrieval'\">\n        <n-tabs v-model:value=\"activeTab\" type=\"line\" animated @update:value=\"onTabChange\">\n          <n-tab-pane name=\"chunking\" tab=\"分段设置\">\n          <n-card>\n            <n-alert type=\"info\" style=\"margin-bottom: 16px\">\n              <template #header>自适应分段与存储空间优化</template>\n              自适应模式根据当前嵌入模型的物理上限自动计算注意力黄金区间，防止尾部硬截断；同时保护代码块与表格完整性，可减少 15%~75% 冗余向量存储占用。\n            </n-alert>\n            <n-form label-placement=\"left\" label-width=\"160\">\n              <n-form-item label=\"分段切分模式\">\n                <n-radio-group v-model:value=\"config.chunking.mode\" size=\"medium\">\n                  <n-radio-button value=\"Adaptive\">自适应分段 (Adaptive - 智能推荐)</n-radio-button>\n                  <n-radio-button value=\"Fixed\">固定分段 (Fixed - 传统模式)</n-radio-button>\n                </n-radio-group>\n              </n-form-item>\n\n              <template v-if=\"config.chunking.mode === 'Adaptive'\">\n                <n-form-item label=\"自适应等级\">\n                  <n-select v-model:value=\"config.chunking.adaptiveLevel\" :options=\"adaptiveLevelOptions\" style=\"width: 100%\" />\n                </n-form-item>\n                <n-form-item label=\"适应风格类型\">\n                  <n-radio-group v-model:value=\"config.chunking.adaptiveStrategyType\" size=\"small\">\n                    <n-radio-button value=\"Balanced\">通用平衡 (推荐)</n-radio-button>\n                    <n-radio-button value=\"PrecisionDense\">高精度紧凑</n-radio-button>\n                    <n-radio-button value=\"DeepNarrative\">深度长文叙事</n-radio-button>\n                    <n-radio-button value=\"CodeFirst\">代码与结构优先</n-radio-button>\n                  </n-radio-group>\n                </n-form-item>\n                <n-form-item label=\"模型窗口利用率\">\n                  <n-space vertical style=\"width: 100%\">\n                    <n-slider v-model:value=\"config.chunking.targetWindowUtilizationPercent\" :min=\"50\" :max=\"90\" :step=\"5\" />\n                    <n-text depth=\"3\" style=\"font-size: 12px\">当前设定: {{ config.chunking.targetWindowUtilizationPercent }}% (推荐 65%~75%，为章节与路径元数据预留安全冗余)</n-text>\n                  </n-space>\n                </n-form-item>\n                <n-form-item label=\"动态重叠率\">\n                  <n-space vertical style=\"width: 100%\">\n                    <n-slider v-model:value=\"config.chunking.adaptiveOverlapPercent\" :min=\"0\" :max=\"25\" :step=\"1\" />\n                    <n-text depth=\"3\" style=\"font-size: 12px\">当前设定: {{ config.chunking.adaptiveOverlapPercent }}% (代码块自动采用 0% 零重叠，避免语法破坏)</n-text>\n                  </n-space>\n                </n-form-item>\n\n                <!-- 实时模型画像测算看板 -->\n                <n-card size=\"small\" embedded style=\"background: rgba(0, 0, 0, 0.02); margin-top: 8px\">\n                  <n-space vertical :size=\"6\">\n                    <n-text strong style=\"font-size: 13px\">实时模型画像测算看板 (Live Sweet-Spot Preview)</n-text>\n                    <n-space align=\"center\" :size=\"16\" style=\"font-size: 12px; color: #555\">\n                      <span>当前模型: <b>{{ adaptivePreview.modelName }}</b> ({{ adaptivePreview.physicalMax }} tokens)</span>\n                      <span>计算目标: <b>{{ adaptivePreview.targetTokens }} tokens</b></span>\n                      <span>最小聚合: <b>{{ adaptivePreview.minTokens }} tokens</b></span>\n                      <span>动态重叠: <b>{{ adaptivePreview.overlapTokens }} tokens</b></span>\n                    </n-space>\n                    <n-space align=\"center\" :size=\"16\" style=\"font-size: 12px\">\n                      <n-tag size=\"small\" type=\"success\" :bordered=\"false\" round>预计防硬截断率: 100%</n-tag>\n                      <n-tag size=\"small\" type=\"info\" :bordered=\"false\" round>预计空间优化: {{ adaptivePreview.spaceSaving }}</n-tag>\n                      <n-tag size=\"small\" :bordered=\"false\" round>代码完整性保护: 启用</n-tag>\n                    </n-space>\n                  </n-space>\n                </n-card>\n              </template>\n\n              <template v-else>\n                <n-form-item label=\"最大 Token 数\">\n                  <n-input-number v-model:value=\"config.chunking.maxTokens\" :min=\"64\" :max=\"4096\" :step=\"64\" style=\"width: 100%\" />\n                </n-form-item>\n                <n-form-item label=\"最小 Token 数\">\n                  <n-input-number v-model:value=\"config.chunking.minTokens\" :min=\"10\" :max=\"512\" :step=\"10\" style=\"width: 100%\" />\n                </n-form-item>\n                <n-form-item label=\"重叠 Token 数\">\n                  <n-input-number v-model:value=\"config.chunking.overlapTokens\" :min=\"0\" :max=\"512\" :step=\"10\" style=\"width: 100%\" />\n                </n-form-item>\n              </template>\n\n              <n-form-item label=\"保留标题结构\" style=\"margin-top: 8px\">\n                <n-switch v-model:value=\"config.chunking.preserveHeadings\" />\n              </n-form-item>\n              <n-form-item label=\"保留代码块\">\n                <n-switch v-model:value=\"config.chunking.preserveCodeBlocks\" />\n              </n-form-item>\n            </n-form>\n          </n-card>\n        </n-tab-pane>\n          <n-tab-pane name=\"search\" tab=\"搜索设置\">\n          <n-card>\n            <n-form label-placement=\"left\" label-width=\"140\">\n              <n-form-item label=\"默认返回数量\">\n                <n-input-number v-model:value=\"config.search.defaultTopK\" :min=\"1\" :max=\"100\" style=\"width: 100%\" />\n              </n-form-item>\n              <n-form-item label=\"上下文窗口\">\n                <n-input-number v-model:value=\"config.search.contextWindow\" :min=\"0\" :max=\"5\" style=\"width: 100%\" />\n              </n-form-item>\n              <n-form-item label=\"相似度阈值\">\n                <n-slider v-model:value=\"thresholdPercent\" :min=\"0\" :max=\"100\" :step=\"1\" />\n              </n-form-item>\n              <n-form-item label=\"启用 MMR 多样性\">\n                <n-switch v-model:value=\"config.search.enableMmr\" />\n              </n-form-item>\n              <n-form-item v-if=\"config.search.enableMmr\" label=\"MMR Lambda\">\n                <n-slider v-model:value=\"mmrLambdaPercent\" :min=\"0\" :max=\"100\" :step=\"1\" />\n              </n-form-item>\n              <n-form-item label=\"默认搜索源\">\n                <n-select\n                  v-model:value=\"config.search.defaultSources\"\n                  multiple\n                  placeholder=\"全部源\"\n                  clearable\n                  :options=\"sourceNameOptions\"\n                />\n              </n-form-item>\n\n              <n-divider>知识图谱扩展召回</n-divider>\n\n              <n-form-item label=\"启用图扩展\">\n                <n-space align=\"center\">\n                  <n-switch v-model:value=\"config.search.enableGraphExpansion\" />\n                  <n-text depth=\"3\" style=\"font-size: 12px\">\n                    {{ config.search.enableGraphExpansion\n                      ? '检索时自动补充 wiki-link 关联文档（需先完成索引建图）'\n                      : '不使用知识图谱扩展' }}\n                  </n-text>\n                </n-space>\n              </n-form-item>\n\n              <template v-if=\"config.search.enableGraphExpansion\">\n                <n-form-item label=\"遍历深度\">\n                  <n-input-number\n                    v-model:value=\"config.search.graphExpansionDepth\"\n                    :min=\"1\" :max=\"2\" style=\"width: 120px\"\n                  />\n                  <n-text depth=\"3\" style=\"margin-left:8px;font-size:12px\">层（推荐 1）</n-text>\n                </n-form-item>\n                <n-form-item label=\"最大邻居节点数\">\n                  <n-input-number\n                    v-model:value=\"config.search.graphExpansionMaxNodes\"\n                    :min=\"1\" :max=\"10\" style=\"width: 120px\"\n                  />\n                  <n-text depth=\"3\" style=\"margin-left:8px;font-size:12px\">个/结果（推荐 3）</n-text>\n                </n-form-item>\n              </template>\n\n            </n-form>\n          </n-card>\n        </n-tab-pane>\n        </n-tabs>\n      </div>\n\n      <!-- 二级面板 3: 系统与存储 -->\n      <div v-show=\"activeGroup === 'system'\">\n        <n-tabs v-model:value=\"activeTab\" type=\"line\" animated @update:value=\"onTabChange\">\n          <n-tab-pane name=\"service\" tab=\"服务设置\">\n          <n-card>\n            <n-form label-placement=\"left\" label-width=\"140\">\n              <n-form-item v-if=\"isDesktop\" label=\"启用 HTTP 服务\"><n-space align=\"center\"><n-switch v-model:value=\"config.service.enableHttpService\" /><n-text depth=\"3\" style=\"font-size: 12px\">{{ config.service.enableHttpService? '开启后额外监听 HTTP，可用浏览器/curl/Swagger 调试 — 修改后需重启': '关闭（默认）：仅进程内通信，不占用端口' }}</n-text></n-space></n-form-item><n-form-item v-if=\"isDesktop && !config.service.enableHttpService\" label=\"\"><n-text depth=\"3\" style=\"font-size: 12px;color: var(--n-warning-color)\">桌面端当前未监听 HTTP 端口，下方端口/外网访问设置不生效</n-text></n-form-item><n-form-item label=\"监听端口\">\n                <n-input-number v-model:value=\"config.service.port\" :min=\"1024\" :max=\"65535\" style=\"width: 200px\" />\n              </n-form-item>\n              <n-form-item label=\"允许外网访问\">\n                <n-space align=\"center\">\n                  <n-switch\n                    v-model:value=\"config.service.allowNetworkAccess\"\n                    @update:value=\"onNetworkAccessChange\"\n                  />\n                  <n-text depth=\"3\" style=\"font-size: 12px\">\n                    {{ config.service.allowNetworkAccess\n                      ? '监听 0.0.0.0（局域网/外网可访问）— 修改后需重启'\n                      : '仅监听 localhost（本机访问）' }}\n                  </n-text>\n                </n-space>\n              </n-form-item>\n              <n-form-item label=\"启用 CORS\">\n                <n-switch v-model:value=\"config.service.enableCors\" />\n              </n-form-item>\n              <n-form-item label=\"启用 Swagger\">\n                <n-switch v-model:value=\"config.service.enableSwagger\" />\n              </n-form-item>\n              <n-form-item label=\"日志级别\">\n                <n-select\n                  v-model:value=\"config.service.logLevel\"\n                  :options=\"logLevelOptions\"\n                  style=\"width: 200px\"\n                />\n              </n-form-item>\n              <n-form-item label=\"API Key\">\n                <n-input-group>\n                  <n-input\n                    v-model:value=\"config.service.apiKey\"\n                    type=\"password\"\n                    show-password-on=\"click\"\n                    placeholder=\"留空则不启用认证\"\n                    style=\"width: 300px\"\n                  />\n                  <n-button\n                    v-if=\"config.service.apiKey\"\n                    type=\"warning\"\n                    style=\"margin-left: 8px\"\n                    @click=\"config.service.apiKey = ''\"\n                  >\n                    清除\n                  </n-button>\n                </n-input-group>\n              </n-form-item>\n              <n-form-item label=\"\">\n                <n-text depth=\"3\" style=\"font-size: 12px\">\n                  {{ config.service.apiKey ? 'API Key 已设置，写操作需要认证' : '未设置 API Key，所有接口无需认证' }}\n                </n-text>\n              </n-form-item>\n            </n-form>\n          </n-card>\n        </n-tab-pane>\n          <n-tab-pane name=\"data\" tab=\"数据路径\">\n          <n-card>\n            <n-form label-placement=\"left\" label-width=\"140\">\n              <n-form-item label=\"数据存储路径\">\n                <n-input v-model:value=\"config.dataPath\" placeholder=\"data\" />\n              </n-form-item>\n              <n-form-item label=\"模型存储路径\">\n                <n-input v-model:value=\"config.modelsRootPath\" placeholder=\"默认: models\" />\n              </n-form-item>\n            </n-form>\n          </n-card>\n        </n-tab-pane>\n        </n-tabs>\n      </div>\n\n      <!-- Save Button -->\n      <n-space style=\"margin-top: 16px\" justify=\"space-between\" align=\"center\">\n        <n-text depth=\"3\" style=\"font-size: 12px\">\n          聊天模型、嵌入/重排模型及其他配置已支持即时热生效；仅修改服务端口或外网访问需重启服务\n        </n-text>\n        <n-button type=\"primary\" :loading=\"saving\" @click=\"handleSave\">保存配置</n-button>\n      </n-space>\n    </n-spin>\n  </n-space>\n";
export default component;
