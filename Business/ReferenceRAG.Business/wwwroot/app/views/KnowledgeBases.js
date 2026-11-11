import { defineComponent as _defineComponent } from 'vue';
import { ref, onMounted, onUnmounted, onActivated, h, computed } from 'vue';
import { useRouter } from 'vue-router';
import { NTag, NButton, useMessage, useDialog } from 'naive-ui';
import { BookOutline, CubeOutline, FolderOutline, GridOutline, LayersOutline, PlayOutline, TrashOutline, RefreshOutline, AddOutline, SearchOutline, SettingsOutline, CreateOutline, DocumentTextOutline, CheckmarkCircleOutline } from '@vicons/ionicons5';
import { knowledgeBasesApi, modelsApi } from '/app/api/index.js';
import { isDesktop, selectFolder } from '/core/platform.js';
import FolderPickerModal from '/app/components/FolderPickerModal.js';

const component = /*@__PURE__*/ _defineComponent({
    ...{ name: 'KnowledgeBases' },
    components: { FolderPickerModal },
    __name: 'KnowledgeBases',
    setup(__props, { expose: __expose }) {
        __expose();
        const router = useRouter();
        const message = useMessage();
        const dialog = useDialog();

        // 状态列表
        const loading = ref(false);
        const knowledgeBases = ref([]);
        const connectorsMap = ref({});
        const embeddingSpecs = ref([]);

        // 新建向导模态框状态
        const showCreateModal = ref(false);
        const creating = ref(false);
        const fetchingModels = ref(false);
        const probingDimension = ref(false);
        const remoteModelOptions = ref([]);

        // 独立分段规格与配置偏离诊断状态
        const chunkingSpecs = ref([]);
        const driftMap = ref({});
        const showSwitchModelModal = ref(false);
        const switchModelTargetKb = ref(null);
        const selectedTargetSpecId = ref('');
        const reindexing = ref(false);

        const createForm = ref({
            name: '',
            description: '',
            bindingMode: 'default', // 'default' | 'local' | 'online'
            localSpecId: '',
            onlineSpecId: '',
            chunkingSpecId: 'spec_chunk_default',
            initialFolder: '',
            initialPattern: '*.md,*.txt,*.pdf'
        });

        const chunkingSpecOptions = computed(() => {
            if (chunkingSpecs.value.length === 0) {
                return [
                    { label: '段落自然凝聚 (DiscourseParagraph) - spec_chunk_lexical_paragraph', value: 'spec_chunk_lexical_paragraph' },
                    { label: '均衡自适应 (Balanced) - 默认推荐', value: 'spec_chunk_default' },
                    { label: '代码优先自适应 (CodeFirst) - 保护Markdown与代码块', value: 'spec_chunk_code_first' },
                    { label: '高密度自适应 (PrecisionDense) - 细粒度事实问答', value: 'spec_chunk_precision_dense' },
                    { label: '细粒度滑动窗口 (SlidingWindow) - spec_chunk_lexical_dense', value: 'spec_chunk_lexical_dense' },
                    { label: '长篇叙事自适应 (DeepNarrative) - 大段落连续上下文', value: 'spec_chunk_narrative' }
                ];
            }
            return chunkingSpecs.value.map(s => ({
                label: `${s.strategyType || s.StrategyType || s.id} (${s.mode || s.Mode}) - ${s.id}`,
                value: s.id || s.Id
            }));
        });

        const allEmbeddingModelOptions = computed(() => {
            const list = embeddingSpecs.value.map(s => ({
                label: `${s.modelName || s.ModelName || s.id} (${s.dimension || s.Dimension || 0}d - ${s.searchBackend || s.ingestionBackend || 'Local'})`,
                value: s.id
            }));
            if (!list.some(s => s.value === 'spec_lexical_only')) {
                list.unshift({
                    label: '⚡ 纯词法模式 (FTS5 BM25 - 解绑/免模型/0显存)',
                    value: 'spec_lexical_only'
                });
            }
            return list;
        });

        const localSpecs = computed(() => {
            return embeddingSpecs.value.filter(s => s.id.startsWith('local_') || s.searchBackend === 'LocalOnnx');
        });

        const onlineSpecs = computed(() => {
            return embeddingSpecs.value.filter(s => s.id.startsWith('prov_') || s.ingestionBackend === 'OpenAiApi');
        });

        const goToModelManagement = (tab = 'embedding') => {
            showCreateModal.value = false;
            router.push(tab === 'providers' ? '/models?tab=providers' : '/models');
        };

        const resetCreateForm = () => {
            createForm.value = {
                name: '',
                description: '',
                bindingMode: 'default',
                localSpecId: localSpecs.value[0]?.id || '',
                onlineSpecId: onlineSpecs.value[0]?.id || '',
                chunkingSpecId: 'spec_chunk_default',
                initialFolder: '',
                initialPattern: '*.md,*.txt,*.pdf'
            };
        };

        // 连接器抽屉状态
        const showConnectorDrawer = ref(false);
        const currentKb = ref(null);
        const currentConnectors = ref([]);
        const loadingConnectors = ref(false);
        const newConnectorPath = ref('');
        const newConnectorPattern = ref('*.md,*.txt,*.pdf');

        // 文件夹选择器状态
        const showFolderPicker = ref(false);
        const folderPickerTarget = ref('connector'); // 'initial' | 'connector'

        const handleFolderSelected = (selectedPath) => {
            if (selectedPath) {
                if (folderPickerTarget.value === 'initial') {
                    createForm.value.initialFolder = selectedPath;
                } else {
                    newConnectorPath.value = selectedPath;
                }
            }
        };

        const selectInitialDirectory = async () => {
            folderPickerTarget.value = 'initial';
            if (isDesktop) {
                try {
                    const path = await selectFolder();
                    if (path) handleFolderSelected(path);
                } catch (error) {
                    message.error(error.message);
                }
            } else {
                showFolderPicker.value = true;
            }
        };

        const selectConnectorDirectory = async () => {
            folderPickerTarget.value = 'connector';
            if (isDesktop) {
                try {
                    const path = await selectFolder();
                    if (path) handleFolderSelected(path);
                } catch (error) {
                    message.error(error.message);
                }
            } else {
                showFolderPicker.value = true;
            }
        };

        // 加载所有知识库及连接器
        const loadKnowledgeBases = async () => {
            loading.value = true;
            try {
                const res = await knowledgeBasesApi.getAll();
                const list = res.data || [];
                knowledgeBases.value = list.map(k => ({
                    id: k.id || k.Id,
                    name: k.name || k.Name,
                    description: k.description || k.Description,
                    enabled: k.enabled !== undefined ? k.enabled : k.Enabled,
                    embeddingSpecId: k.embeddingSpecId || k.EmbeddingSpecId,
                    chunkingSpecId: k.chunkingSpecId || k.ChunkingSpecId || 'spec_chunk_default',
                    createdAt: k.createdAt || k.CreatedAt,
                    updatedAt: k.updatedAt || k.UpdatedAt
                }));

                // 异步探测各知识库的配置偏离健康状态
                knowledgeBases.value.forEach(async (kb) => {
                    try {
                        const driftRes = await knowledgeBasesApi.checkDrift(kb.id);
                        if (driftRes.data) {
                            driftMap.value[kb.id] = driftRes.data;
                        }
                    } catch {}
                });
                
                // 并发获取各库连接器信息
                const map = {};
                await Promise.all(knowledgeBases.value.map(async (kb) => {
                    try {
                        const connRes = await knowledgeBasesApi.getConnectors(kb.id);
                        const cList = connRes.data || [];
                        map[kb.id] = cList.map(c => ({
                            id: c.id || c.Id,
                            kind: c.kind || c.Kind,
                            targetPath: c.targetPath || c.TargetPath,
                            filePattern: c.filePattern || c.FilePattern,
                            syncState: c.syncState || c.SyncState
                        }));
                    } catch (e) {
                        map[kb.id] = [];
                    }
                }));
                connectorsMap.value = map;
            } catch (err) {
                console.error('加载知识库失败:', err);
                message.error('加载知识库失败: ' + (err.response?.data?.message || err.message));
            } finally {
                loading.value = false;
            }
        };

        // 加载预设模型规范
        const loadEmbeddingSpecs = async () => {
            try {
                const res = await knowledgeBasesApi.getEmbeddingSpecs();
                embeddingSpecs.value = res.data || [];
                if (!createForm.value.localSpecId && localSpecs.value.length > 0) {
                    createForm.value.localSpecId = localSpecs.value[0].id;
                }
                if (!createForm.value.onlineSpecId && onlineSpecs.value.length > 0) {
                    createForm.value.onlineSpecId = onlineSpecs.value[0].id;
                }
            } catch (err) {
                console.warn('获取预设 EmbeddingSpecs 失败:', err);
            }
        };

        // 加载分段策略规范
        const loadChunkingSpecs = async () => {
            try {
                const res = await knowledgeBasesApi.getChunkingSpecs();
                chunkingSpecs.value = res.data || [];
            } catch (err) {
                console.warn('获取分段策略失败:', err);
            }
        };

        // 打开更换模型弹窗
        const openSwitchModelModal = (kb) => {
            switchModelTargetKb.value = kb;
            selectedTargetSpecId.value = kb.embeddingSpecId || embeddingSpecs.value[0]?.id || '';
            showSwitchModelModal.value = true;
        };

        // 提交触发零停机蓝绿影子重构
        const handleTriggerReindex = async () => {
            if (!switchModelTargetKb.value) return;
            reindexing.value = true;
            try {
                const res = await knowledgeBasesApi.reindex(switchModelTargetKb.value.id, {
                    targetEmbeddingSpecId: selectedTargetSpecId.value
                });
                message.success(res.data?.message || '已成功触发零停机蓝绿影子重构！');
                showSwitchModelModal.value = false;
                await loadKnowledgeBases();
            } catch (err) {
                message.error('重构失败: ' + (err.response?.data?.message || err.message));
            } finally {
                reindexing.value = false;
            }
        };

        // 解绑向量模型降级为纯词法检索
        const handleUnbindModel = () => {
            if (!switchModelTargetKb.value) return;
            dialog.warning({
                title: '确认解绑向量模型？',
                content: `将知识库 [${switchModelTargetKb.value.name}] 降级为纯词法检索模式（FTS5 BM25）。解绑后不再占用向量显存，历史切片文本与词法索引保持完整可用。后续随时可重新绑定模型。`,
                positiveText: '确认解绑降级',
                negativeText: '取消',
                onPositiveClick: async () => {
                    reindexing.value = true;
                    try {
                        const res = await knowledgeBasesApi.unbindModel(switchModelTargetKb.value.id);
                        message.success(res.data?.message || '已成功解绑模型，知识库已切换为纯词法检索模式');
                        showSwitchModelModal.value = false;
                        await loadKnowledgeBases();
                    } catch (err) {
                        message.error('解绑模型失败: ' + (err.response?.data?.message || err.message));
                    } finally {
                        reindexing.value = false;
                    }
                }
            });
        };

        // 切换线上服务商快捷地址
        const handleProviderChange = (provider) => {
            createForm.value.onlineProvider = provider;
            if (provider === 'siliconflow') {
                createForm.value.onlineBaseUrl = 'https://api.siliconflow.cn/v1';
                createForm.value.onlineModelName = 'BAAI/bge-m3';
                createForm.value.detectedDimension = 1024;
            } else if (provider === 'ollama') {
                createForm.value.onlineBaseUrl = 'http://localhost:11434/v1';
                createForm.value.onlineModelName = 'bge-m3';
                createForm.value.detectedDimension = 1024;
            } else if (provider === 'openai') {
                createForm.value.onlineBaseUrl = 'https://api.openai.com/v1';
                createForm.value.onlineModelName = 'text-embedding-3-small';
                createForm.value.detectedDimension = 1536;
            } else if (provider === 'deepseek') {
                createForm.value.onlineBaseUrl = 'https://api.deepseek.com/v1';
                createForm.value.onlineModelName = 'deepseek-chat';
                createForm.value.detectedDimension = 1024;
            }
        };

        // 动态拉取远程模型列表
        const handleDiscoverRemoteModels = async () => {
            if (!createForm.value.onlineBaseUrl) {
                message.warning('请先填写 API 基础地址');
                return;
            }
            fetchingModels.value = true;
            try {
                const res = await modelsApi.discoverRemoteModels({
                    baseUrl: createForm.value.onlineBaseUrl,
                    apiKey: createForm.value.onlineApiKey
                });
                const list = res.data?.models || [];
                remoteModelOptions.value = list.map(m => ({ label: m, value: m }));
                message.success(`成功拉取到 ${list.length} 个远端模型`);
                if (list.length > 0 && !createForm.value.onlineModelName) {
                    createForm.value.onlineModelName = list[0];
                }
            } catch (err) {
                console.error('拉取远端模型列表失败:', err);
                message.error('拉取模型失败: ' + (err.response?.data?.error || err.message));
            } finally {
                fetchingModels.value = false;
            }
        };

        // 自动探测向量维度
        const handleProbeDimension = async () => {
            if (!createForm.value.onlineBaseUrl || !createForm.value.onlineModelName) {
                message.warning('请先指定 API 地址与模型名称');
                return;
            }
            probingDimension.value = true;
            try {
                const res = await modelsApi.probeDimension({
                    baseUrl: createForm.value.onlineBaseUrl,
                    modelName: createForm.value.onlineModelName,
                    apiKey: createForm.value.onlineApiKey
                });
                if (res.data?.success && res.data.dimension > 0) {
                    createForm.value.detectedDimension = res.data.dimension;
                    message.success(`探针测试成功！已精准测得向量维度为: ${res.data.dimension} 维`);
                } else {
                    message.warning('探针未返回有效维度');
                }
            } catch (err) {
                console.error('探测维度失败:', err);
                message.error('探测失败: ' + (err.response?.data?.error || err.message));
            } finally {
                probingDimension.value = false;
            }
        };

        // 提交新建知识库
        const handleCreateSubmit = async () => {
            if (!createForm.value.name.trim()) {
                message.warning('知识库名称不能为空');
                return;
            }

            creating.value = true;
            try {
                let payload = {
                    name: createForm.value.name.trim(),
                    description: createForm.value.description.trim()
                };

                if (createForm.value.bindingMode === 'lexical') {
                    payload.embeddingSpecId = 'spec_lexical_only';
                    payload.chunkingSpecId = createForm.value.chunkingSpecId || 'spec_chunk_lexical_paragraph';
                } else if (createForm.value.bindingMode === 'local') {
                    if (!createForm.value.localSpecId) {
                        message.warning('请选择已就绪的本地模型');
                        creating.value = false;
                        return;
                    }
                    payload.embeddingSpecId = createForm.value.localSpecId;
                } else if (createForm.value.bindingMode === 'online') {
                    if (!createForm.value.onlineSpecId) {
                        message.warning('请选择在线服务商纳管模型');
                        creating.value = false;
                        return;
                    }
                    payload.embeddingSpecId = createForm.value.onlineSpecId;
                } else {
                    // 默认模板模式
                    payload.embeddingSpecId = 'default-spec';
                }

                // 注入独立分段策略规格
                payload.chunkingSpecId = payload.chunkingSpecId || createForm.value.chunkingSpecId || 'spec_chunk_default';

                const res = await knowledgeBasesApi.create(payload);
                const newKb = res.data;
                const kbId = newKb.id || newKb.Id;
                const kbName = newKb.name || newKb.Name;
                message.success(`知识库「${kbName}」创建成功`);

                // 若指定了初始本地文件夹，自动绑定连接器
                if (createForm.value.initialFolder.trim()) {
                    try {
                        await knowledgeBasesApi.addConnector(kbId, {
                            kind: 'Folder',
                            targetPath: createForm.value.initialFolder.trim(),
                            filePattern: createForm.value.initialPattern.trim() || null
                        });
                        message.info('初始数据目录已成功挂载为连接器');
                    } catch (connErr) {
                        console.warn('挂载初始连接器失败:', connErr);
                    }
                }

                showCreateModal.value = false;
                // 重置表单
                createForm.value.name = '';
                createForm.value.description = '';
                createForm.value.initialFolder = '';
                await loadKnowledgeBases();
            } catch (err) {
                console.error('创建知识库失败:', err);
                message.error('创建失败: ' + (err.response?.data?.message || err.message));
            } finally {
                creating.value = false;
            }
        };

        // 打开连接器抽屉
        const openConnectorsDrawer = async (kb) => {
            currentKb.value = kb;
            showConnectorDrawer.value = true;
            newConnectorPath.value = '';
            await loadCurrentConnectors();
        };

        const loadCurrentConnectors = async () => {
            if (!currentKb.value) return;
            loadingConnectors.value = true;
            try {
                const res = await knowledgeBasesApi.getConnectors(currentKb.value.id);
                currentConnectors.value = res.data || [];
            } catch (err) {
                message.error('加载连接器列表失败');
            } finally {
                loadingConnectors.value = false;
            }
        };

        // 添加连接器
        const handleAddConnector = async () => {
            if (!newConnectorPath.value.trim()) {
                message.warning('请输入目标文件夹路径');
                return;
            }
            try {
                await knowledgeBasesApi.addConnector(currentKb.value.id, {
                    kind: 'Folder',
                    targetPath: newConnectorPath.value.trim(),
                    filePattern: newConnectorPattern.value.trim() || null
                });
                message.success('已成功挂载连接器');
                newConnectorPath.value = '';
                await loadCurrentConnectors();
                await loadKnowledgeBases();
            } catch (err) {
                message.error('添加连接器失败: ' + (err.response?.data?.message || err.message));
            }
        };

        // 删除连接器 (支持级联物理清理)
        const handleDeleteConnector = (connector) => {
            const connId = typeof connector === 'object' ? connector.id : connector;
            const targetPath = typeof connector === 'object' ? connector.targetPath : connId;
            dialog.warning({
                title: '解挂连接器与级联物理清理',
                content: `确定解挂并移除连接器 [${targetPath}] 吗？系统将彻底清理该连接器同步的历史文件、切片与词法/向量索引，杜绝幽灵切片残留。`,
                positiveText: '级联物理清理',
                negativeText: '取消',
                onPositiveClick: async () => {
                    try {
                        // 级联物理清理 (cascade = true)
                        await knowledgeBasesApi.deleteConnector(currentKb.value.id, connId, true);
                        message.success('连接器已解除挂载并完成级联数据清理');
                        await loadCurrentConnectors();
                        await loadKnowledgeBases();
                    } catch (err) {
                        message.error('删除连接器失败: ' + (err.response?.data?.message || err.message));
                    }
                }
            });
        };

        // 触发单个同步作业
        const handleTriggerSync = async (kbId, connectorId) => {
            try {
                const res = await knowledgeBasesApi.triggerSync(kbId, connectorId);
                message.success('已触发异步同步任务: ' + res.data.id);
            } catch (err) {
                message.error('触发同步失败: ' + (err.response?.data?.message || err.message));
            }
        };

        // 同步当前知识库的所有连接器
        const handleSyncAllConnectors = async () => {
            if (!currentKb.value || currentConnectors.value.length === 0) return;
            let successCount = 0;
            for (const conn of currentConnectors.value) {
                try {
                    await knowledgeBasesApi.triggerSync(currentKb.value.id, conn.id);
                    successCount++;
                } catch (e) {
                    console.error('Trigger sync failed for connector:', conn.id, e);
                }
            }
            message.success(`已触发 ${successCount} 个连接器的同步任务`);
        };

        const getReachabilityTagType = (conn) => {
            if (!conn) return 'default';
            const state = conn.reachability || (conn.isReachable ? 'Reachable' : (conn.isReachable === false ? 'Unreachable' : 'Unknown'));
            if (state === 'Reachable') return 'success';
            if (state === 'AccessDenied') return 'warning';
            if (state === 'Unreachable') return 'error';
            return 'default';
        };

        const getReachabilityText = (conn) => {
            if (!conn) return '? 状态未决';
            const state = conn.reachability || (conn.isReachable ? 'Reachable' : (conn.isReachable === false ? 'Unreachable' : 'Unknown'));
            if (state === 'Reachable') return '● 目录可达';
            if (state === 'AccessDenied') return '▲ 权限不足';
            if (state === 'Unreachable') return '▲ 目录不可达';
            return '? 状态未决';
        };

        // ==================== 知识库编辑与启停 ====================
        const showEditModal = ref(false);
        const updatingKb = ref(false);
        const editForm = ref({ id: '', name: '', description: '', enabled: true, embeddingSpecId: '', chunkingSpecId: 'spec_chunk_default' });

        const openEditModal = (kb) => {
            editForm.value = {
                id: kb.id,
                name: kb.name,
                description: kb.description || '',
                enabled: kb.enabled,
                embeddingSpecId: kb.embeddingSpecId,
                chunkingSpecId: kb.chunkingSpecId || 'spec_chunk_default'
            };
            showEditModal.value = true;
        };

        const handleUpdateKbSubmit = async () => {
            if (!editForm.value.name.trim()) {
                message.error('知识库名称不能为空');
                return;
            }
            updatingKb.value = true;
            try {
                await knowledgeBasesApi.update(editForm.value.id, {
                    name: editForm.value.name.trim(),
                    description: editForm.value.description?.trim(),
                    enabled: editForm.value.enabled,
                    embeddingSpecId: editForm.value.embeddingSpecId,
                    chunkingSpecId: editForm.value.chunkingSpecId
                });
                message.success('知识库已更新');
                showEditModal.value = false;
                await loadKnowledgeBases();
            } catch (err) {
                message.error('更新知识库失败: ' + (err.response?.data?.message || err.message));
            } finally {
                updatingKb.value = false;
            }
        };

        // ==================== 知识库重新分段 (Re-chunking) ====================
        const showRechunkModal = ref(false);
        const rechunkTargetKb = ref(null);
        const selectedRechunkSpecId = ref('');
        const rechunking = ref(false);

        const openRechunkModal = (kb) => {
            rechunkTargetKb.value = kb;
            selectedRechunkSpecId.value = kb.chunkingSpecId || 'spec_chunk_default';
            showRechunkModal.value = true;
        };

        const handleTriggerRechunk = async () => {
            if (!rechunkTargetKb.value) return;
            rechunking.value = true;
            try {
                const res = await knowledgeBasesApi.rechunk(rechunkTargetKb.value.id, {
                    targetChunkingSpecId: selectedRechunkSpecId.value
                });
                message.success(res.data?.message || '已成功启动全库重新分段与索引！');
                showRechunkModal.value = false;
                await loadKnowledgeBases();
            } catch (err) {
                message.error('重新分段失败: ' + (err.response?.data?.message || err.message));
            } finally {
                rechunking.value = false;
            }
        };

        const handleToggleKb = async (kb, enabled) => {
            try {
                await knowledgeBasesApi.toggle(kb.id, enabled);
                message.success(enabled ? '知识库已启用' : '知识库已停用');
                kb.enabled = enabled;
            } catch (err) {
                message.error('切换知识库状态失败: ' + (err.response?.data?.message || err.message));
            }
        };

        // ==================== 连接器编辑 ====================
        const showEditConnectorModal = ref(false);
        const updatingConn = ref(false);
        const editConnForm = ref({ id: '', targetPath: '', filePattern: '', enabled: true });

        const openEditConnectorModal = (conn) => {
            editConnForm.value = {
                id: conn.id,
                targetPath: conn.targetPath,
                filePattern: conn.filePattern || '*.md,*.txt,*.pdf',
                enabled: conn.enabled
            };
            showEditConnectorModal.value = true;
        };

        const handleUpdateConnectorSubmit = async () => {
            if (!editConnForm.value.targetPath.trim()) {
                message.error('连接器目标路径不能为空');
                return;
            }
            updatingConn.value = true;
            try {
                await knowledgeBasesApi.updateConnector(currentKb.value.id, editConnForm.value.id, {
                    targetPath: editConnForm.value.targetPath.trim(),
                    filePattern: editConnForm.value.filePattern?.trim(),
                    enabled: editConnForm.value.enabled
                });
                message.success('连接器配置已更新');
                showEditConnectorModal.value = false;
                await loadCurrentConnectors();
            } catch (err) {
                message.error('更新连接器失败: ' + (err.response?.data?.message || err.message));
            } finally {
                updatingConn.value = false;
            }
        };

        // ==================== 文档与切片管理 (HITL Explorer) ====================
        const showExplorerDrawer = ref(false);
        const explorerTab = ref('chunks'); // 'documents' | 'chunks'
        const explorerKb = ref(null);

        // 文档状态
        const documents = ref([]);
        const loadingDocs = ref(false);
        const docSearch = ref('');
        const docPage = ref(1);
        const docPageSize = ref(20);
        const docTotal = ref(0);

        // 切片状态
        const chunks = ref([]);
        const loadingChunks = ref(false);
        const chunkSearch = ref('');
        const chunkDocFilter = ref(null);
        const chunkEnabledOnly = ref(false);
        const chunkPage = ref(1);
        const chunkPageSize = ref(20);
        const chunkTotal = ref(0);

        // 单切片编辑与添加
        const editingChunkId = ref(null);
        const editingChunkText = ref('');
        const recomputeEmbeddingImmediately = ref(true);
        const savingChunk = ref(false);

        const showAddChunkModal = ref(false);
        const addingChunk = ref(false);
        const newChunkForm = ref({ text: '', title: '' });

        const showAddDocModal = ref(false);
        const addingDoc = ref(false);
        const newDocForm = ref({ sourceKey: '', content: '' });

        const handleAddDoc = async () => {
            if (!newDocForm.value.sourceKey.trim()) {
                message.warning('请输入文档标识 (SourceKey) 或文件路径');
                return;
            }
            if (!newDocForm.value.content.trim()) {
                message.warning('请输入文档正文内容');
                return;
            }
            addingDoc.value = true;
            try {
                await knowledgeBasesApi.createDocument(explorerKb.value.id, {
                    sourceKey: newDocForm.value.sourceKey.trim(),
                    content: newDocForm.value.content.trim()
                });
                message.success('已录入单篇文档并自动切块建引');
                showAddDocModal.value = false;
                newDocForm.value = { sourceKey: '', content: '' };
                await Promise.all([loadExplorerDocuments(), loadExplorerChunks()]);
                await loadKnowledgeBases();
            } catch (err) {
                message.error('录入文档失败: ' + (err.response?.data?.message || err.message));
            } finally {
                addingDoc.value = false;
            }
        };

        const openExplorerDrawer = async (kb) => {
            explorerKb.value = kb;
            explorerTab.value = 'chunks';
            chunkDocFilter.value = null;
            chunkSearch.value = '';
            chunkPage.value = 1;
            docPage.value = 1;
            docSearch.value = '';
            showExplorerDrawer.value = true;
            await Promise.all([loadExplorerDocuments(), loadExplorerChunks()]);
        };

        const loadExplorerDocuments = async () => {
            if (!explorerKb.value) return;
            loadingDocs.value = true;
            try {
                const res = await knowledgeBasesApi.getDocuments(explorerKb.value.id, {
                    page: docPage.value,
                    pageSize: docPageSize.value,
                    search: docSearch.value || undefined
                });
                documents.value = res.data.items || [];
                docTotal.value = res.data.total || 0;
            } catch (err) {
                message.error('获取文档列表失败: ' + (err.response?.data?.message || err.message));
            } finally {
                loadingDocs.value = false;
            }
        };

        const loadExplorerChunks = async () => {
            if (!explorerKb.value) return;
            loadingChunks.value = true;
            try {
                const res = await knowledgeBasesApi.getChunks(explorerKb.value.id, {
                    documentId: chunkDocFilter.value || undefined,
                    page: chunkPage.value,
                    pageSize: chunkPageSize.value,
                    search: chunkSearch.value || undefined,
                    enabledOnly: chunkEnabledOnly.value ? true : undefined
                });
                chunks.value = res.data.items || [];
                chunkTotal.value = res.data.total || 0;
            } catch (err) {
                message.error('获取切片列表失败: ' + (err.response?.data?.message || err.message));
            } finally {
                loadingChunks.value = false;
            }
        };

        const filterChunksByDoc = (doc) => {
            chunkDocFilter.value = doc.id;
            explorerTab.value = 'chunks';
            chunkPage.value = 1;
            loadExplorerChunks();
        };

        const handleDeleteDoc = (doc) => {
            dialog.warning({
                title: '确认删除文档',
                content: `确定要删除文档「${doc.sourceKey}」吗？这将同时清理其关联的所有分块与向量。 `,
                positiveText: '确认删除',
                negativeText: '取消',
                onPositiveClick: async () => {
                    try {
                        await knowledgeBasesApi.deleteDocument(explorerKb.value.id, doc.id);
                        message.success('文档已删除');
                        await loadExplorerDocuments();
                        if (chunkDocFilter.value === doc.id) {
                            chunkDocFilter.value = null;
                        }
                        await loadExplorerChunks();
                    } catch (err) {
                        message.error('删除文档失败: ' + (err.response?.data?.message || err.message));
                    }
                }
            });
        };

        const handleToggleChunk = async (chunk, enabled) => {
            try {
                await knowledgeBasesApi.toggleChunk(explorerKb.value.id, chunk.id, enabled);
                chunk.enabled = enabled;
                message.success(enabled ? '切片已启用' : '切片已停用');
            } catch (err) {
                message.error('切换切片状态失败: ' + (err.response?.data?.message || err.message));
            }
        };

        const startEditChunk = (chunk) => {
            editingChunkId.value = chunk.id;
            editingChunkText.value = chunk.text;
            recomputeEmbeddingImmediately.value = true;
        };

        const cancelEditChunk = () => {
            editingChunkId.value = null;
            editingChunkText.value = '';
        };

        const saveEditChunk = async (chunk) => {
            if (!editingChunkText.value.trim()) {
                message.error('切片文本不能为空');
                return;
            }
            savingChunk.value = true;
            try {
                const res = await knowledgeBasesApi.updateChunk(explorerKb.value.id, chunk.id, {
                    text: editingChunkText.value.trim(),
                    recomputeEmbeddingImmediately: recomputeEmbeddingImmediately.value
                });
                message.success('切片已更新并同步刷新全文索引与向量');
                chunk.text = res.data.text;
                chunk.tokenCount = res.data.tokenCount;
                chunk.hasVector = res.data.hasVector;
                editingChunkId.value = null;
            } catch (err) {
                message.error('保存切片失败: ' + (err.response?.data?.message || err.message));
            } finally {
                savingChunk.value = false;
            }
        };

        const handleDeleteChunk = (chunk) => {
            dialog.warning({
                title: '确认删除切片',
                content: '确定要删除该切片分段吗？此操作不可逆。',
                positiveText: '确认删除',
                negativeText: '取消',
                onPositiveClick: async () => {
                    try {
                        await knowledgeBasesApi.deleteChunk(explorerKb.value.id, chunk.id);
                        message.success('切片已删除');
                        await loadExplorerChunks();
                    } catch (err) {
                        message.error('删除切片失败: ' + (err.response?.data?.message || err.message));
                    }
                }
            });
        };

        const handleAddChunk = async () => {
            if (!newChunkForm.value.text.trim()) {
                message.error('切片内容不能为空');
                return;
            }
            addingChunk.value = true;
            try {
                await knowledgeBasesApi.createChunk(explorerKb.value.id, {
                    text: newChunkForm.value.text.trim(),
                    documentId: chunkDocFilter.value || null
                });
                message.success('已成功添加切片');
                showAddChunkModal.value = false;
                newChunkForm.value = { text: '', title: '' };
                await loadExplorerChunks();
            } catch (err) {
                message.error('添加切片失败: ' + (err.response?.data?.message || err.message));
            } finally {
                addingChunk.value = false;
            }
        };

        // 删除知识库
        const handleDeleteKnowledgeBase = (kb) => {
            dialog.warning({
                title: '确认删除知识库',
                content: `确定要删除知识库「${kb.name}」吗？这将彻底清理其物理存储数据库(kb.db)与全部索引。`,
                positiveText: '确认删除',
                negativeText: '取消',
                onPositiveClick: async () => {
                    try {
                        await knowledgeBasesApi.delete(kb.id);
                        message.success(`知识库「${kb.name}」已安全删除`);
                        await loadKnowledgeBases();
                    } catch (err) {
                        message.error('删除知识库失败: ' + (err.response?.data?.message || err.message));
                    }
                }
            });
        };

        onMounted(async () => {
            await Promise.all([loadKnowledgeBases(), loadEmbeddingSpecs(), loadChunkingSpecs()]);
        });

        onActivated(async () => {
            await Promise.all([loadKnowledgeBases(), loadEmbeddingSpecs(), loadChunkingSpecs()]);
        });

        const __returned__ = {
            chunkingSpecs, chunkingSpecOptions, driftMap, showSwitchModelModal, switchModelTargetKb, selectedTargetSpecId,
            reindexing, openSwitchModelModal, handleTriggerReindex, handleUnbindModel, allEmbeddingModelOptions, loadChunkingSpecs,
            showRechunkModal, rechunkTargetKb, selectedRechunkSpecId, rechunking, openRechunkModal, handleTriggerRechunk,
            showEditModal, updatingKb, editForm, openEditModal, handleUpdateKbSubmit, handleToggleKb,
            showEditConnectorModal, updatingConn, editConnForm, openEditConnectorModal, handleUpdateConnectorSubmit,
            showExplorerDrawer, explorerTab, explorerKb, openExplorerDrawer,
            documents, loadingDocs, docSearch, docPage, docPageSize, docTotal, loadExplorerDocuments, filterChunksByDoc, handleDeleteDoc,
            chunks, loadingChunks, chunkSearch, chunkDocFilter, chunkEnabledOnly, chunkPage, chunkPageSize, chunkTotal, loadExplorerChunks,
            editingChunkId, editingChunkText, recomputeEmbeddingImmediately, savingChunk, startEditChunk, cancelEditChunk, saveEditChunk,
            handleToggleChunk, handleDeleteChunk, showAddChunkModal, addingChunk, newChunkForm, handleAddChunk, showAddDocModal, addingDoc, newDocForm, handleAddDoc,
            get CreateOutline() { return CreateOutline; },
            get DocumentTextOutline() { return DocumentTextOutline; },
            get CheckmarkCircleOutline() { return CheckmarkCircleOutline; },
            

            router, message, dialog, loading, knowledgeBases, connectorsMap, embeddingSpecs, localSpecs, onlineSpecs, goToModelManagement, resetCreateForm,
            showCreateModal, creating, fetchingModels, probingDimension, remoteModelOptions, createForm,
            showConnectorDrawer, currentKb, currentConnectors, loadingConnectors, newConnectorPath, newConnectorPattern,
            showFolderPicker, folderPickerTarget, handleFolderSelected, selectInitialDirectory, selectConnectorDirectory,
            loadKnowledgeBases, loadEmbeddingSpecs, handleProviderChange, handleDiscoverRemoteModels, handleProbeDimension,
            handleCreateSubmit, openConnectorsDrawer, loadCurrentConnectors, handleAddConnector, handleDeleteConnector,
            handleTriggerSync, handleSyncAllConnectors, getReachabilityTagType, getReachabilityText, handleDeleteKnowledgeBase,
            get FolderPickerModal() { return FolderPickerModal; },
            get NTag() { return NTag; },
            get NButton() { return NButton; },
            get BookOutline() { return BookOutline; },
            get CubeOutline() { return CubeOutline; },
            get FolderOutline() { return FolderOutline; },
            get GridOutline() { return GridOutline; },
            get LayersOutline() { return LayersOutline; },
            get PlayOutline() { return PlayOutline; },
            get TrashOutline() { return TrashOutline; },
            get RefreshOutline() { return RefreshOutline; },
            get AddOutline() { return AddOutline; },
            get SearchOutline() { return SearchOutline; },
            get SettingsOutline() { return SettingsOutline; }
        };
        return __returned__;
    }
});

component.template = `
  <n-space vertical :size="20">
    <!-- 顶部概览与操作栏 -->
    <n-card>
      <n-page-header title="知识库管理 (Physical Knowledge Bases)" subtitle="物理单库隔离 (kb.db) 架构，支持按库独立绑定本地/线上模型，彻底告别单库维度混杂">
        <template #extra>
          <n-space>
            <n-button @click="loadKnowledgeBases" :loading="loading">
              <template #icon><n-icon :component="RefreshOutline" /></template>
              刷新
            </n-button>
            <n-button type="primary" @click="showCreateModal = true">
              <template #icon><n-icon :component="AddOutline" /></template>
              新建知识库
            </n-button>
          </n-space>
        </template>
      </n-page-header>
    </n-card>

    <!-- 知识库卡片网格 -->
    <n-spin :show="loading">
      <n-grid :cols="3" :x-gap="16" :y-gap="16">
        <n-gi v-for="kb in knowledgeBases" :key="kb.id">
          <n-card :title="kb.name" hoverable>
            <template #header-extra>
              <n-tag :type="kb.enabled ? 'success' : 'default'" size="small">
                {{ kb.enabled ? '已就绪' : '停用' }}
              </n-tag>
            </template>

            <n-space vertical :size="12">
              <n-text depth="3">{{ kb.description || '暂无描述' }}</n-text>

              <n-descriptions :column="1" size="small" label-placement="left">
                <n-descriptions-item label="物理存储">
                  <n-text code>data/kbs/{{ kb.id }}/kb.db</n-text>
                </n-descriptions-item>
                <n-descriptions-item label="绑定模型">
                  <n-space align="center" :size="4">
                    <n-tag :type="kb.embeddingSpecId === 'spec_lexical_only' ? 'success' : 'info'" size="small">
                      <template #icon><n-icon :component="CubeOutline" /></template>
                      {{ kb.embeddingSpecId === 'spec_lexical_only' ? '纯词法 (FTS5 BM25)' : kb.embeddingSpecId }}
                    </n-tag>
                    <n-tag v-if="kb.embeddingSpecId === 'spec_lexical_only'" size="small" type="info" :bordered="false" round>免模型/0显存</n-tag>
                    <n-tag v-else-if="driftMap[kb.id]?.status === 'Aligned'" size="small" type="success" :bordered="false" round>对齐</n-tag>
                    <n-tag v-else-if="driftMap[kb.id]?.status === 'Drifted'" size="small" type="warning" round style="cursor: pointer" @click="openSwitchModelModal(kb)">偏离(建议重排)</n-tag>
                    <n-tag v-else-if="driftMap[kb.id]?.status === 'Customized'" size="small" type="info" :bordered="false" round>定制</n-tag>
                  </n-space>
                </n-descriptions-item>
                <n-descriptions-item label="分段策略">
                  <n-tag size="small" type="default">
                    <template #icon><n-icon :component="LayersOutline" /></template>
                    {{ kb.chunkingSpecId || 'spec_chunk_default' }}
                  </n-tag>
                </n-descriptions-item>
                <n-descriptions-item label="连接器通道">
                  <n-text depth="2">{{ (connectorsMap[kb.id] || []).length }} 个数据源挂载</n-text>
                </n-descriptions-item>
              </n-descriptions>
            </n-space>

            <template #action>
              <n-space justify="space-between" align="center">
                <n-space size="small" align="center">
                  <n-button size="small" secondary @click="openSwitchModelModal(kb)">
                    <template #icon><n-icon :component="RefreshOutline" /></template>
                    {{ kb.embeddingSpecId === 'spec_lexical_only' ? '绑定模型' : '更换模型' }}
                  </n-button>
                  <n-button size="small" secondary type="warning" @click="openRechunkModal(kb)">
                    <template #icon><n-icon :component="LayersOutline" /></template>
                    重新分段
                  </n-button>
                  <n-button size="small" secondary @click="openExplorerDrawer(kb)">
                    <template #icon><n-icon :component="DocumentTextOutline" /></template>
                    文档切片
                  </n-button>
                  <n-button size="small" secondary @click="openConnectorsDrawer(kb)">
                    <template #icon><n-icon :component="FolderOutline" /></template>
                    连接器 ({{ (connectorsMap[kb.id] || []).length }})
                  </n-button>
                  <n-button size="small" secondary @click="openEditModal(kb)">
                    <template #icon><n-icon :component="CreateOutline" /></template>
                    编辑
                  </n-button>
                  <n-button size="small" type="primary" secondary @click="router.push('/search')">
                    <template #icon><n-icon :component="SearchOutline" /></template>
                    检索
                  </n-button>
                </n-space>
                <n-button size="small" quaternary type="error" @click="handleDeleteKnowledgeBase(kb)">
                  <template #icon><n-icon :component="TrashOutline" /></template>
                  删除
                </n-button>
              </n-space>
            </template>
          </n-card>
        </n-gi>
      </n-grid>

      <n-empty v-if="knowledgeBases.length === 0 && !loading" description="暂无知识库，请点击上方「新建知识库」创建首个物理隔离知识库" style="margin: 40px 0;">
        <template #extra>
          <n-button type="primary" @click="showCreateModal = true">
            <template #icon><n-icon :component="AddOutline" /></template>
            立即创建
          </n-button>
        </template>
      </n-empty>
    </n-spin>

    <!-- 新建知识库向导模态框 -->
    <n-modal v-model:show="showCreateModal" preset="card" title="新建知识库 (New Knowledge Base)" style="width: 680px;" :mask-closable="false">
      <n-form label-placement="top">
        <n-form-item label="知识库名称" required>
          <n-input v-model:value="createForm.name" placeholder="例如：企业核心技术文档库 / 法律合同审查库" />
        </n-form-item>

        <n-form-item label="知识库描述">
          <n-input v-model:value="createForm.description" type="textarea" placeholder="描述此知识库的数据范畴与主要用途" :rows="2" />
        </n-form-item>

        <n-divider title-placement="left">向量空间规范 (Embedding & Vector Space)</n-divider>

        <n-form-item label="模型来源模式">
          <n-radio-group v-model:value="createForm.bindingMode" name="bindingMode">
            <n-radio-button value="default">🌟 系统默认模板 (极速零配置)</n-radio-button>
            <n-radio-button value="local">💻 本地已就绪模型 (Local ONNX)</n-radio-button>
            <n-radio-button value="online">🌐 在线服务商模型 (Cloud Providers)</n-radio-button>
            <n-radio-button value="lexical">⚡ 纯词法检索 (免模型/零显存)</n-radio-button>
          </n-radio-group>
        </n-form-item>

        <!-- 模式一：系统默认模板 -->
        <div v-if="createForm.bindingMode === 'default'">
          <n-alert type="info" style="margin-bottom: 12px;">
            直接继承系统默认嵌入模型与宿主机 GPU/CPU 硬件加速。适合绝大多数通用知识库场景，零门槛秒级创建。
          </n-alert>
        </div>

        <!-- 模式二：本地已就绪模型 (硬门禁) -->
        <div v-else-if="createForm.bindingMode === 'local'">
          <div v-if="localSpecs.length === 0">
            <n-alert type="warning" style="margin-bottom: 12px;">
              <span>本地暂未检测到已就绪且启用的嵌入模型。</span>
              <n-button size="tiny" type="primary" secondary style="margin-left: 8px;" @click="goToModelManagement('embedding')">
                前往「模型管理」下载
              </n-button>
            </n-alert>
          </div>
          <n-form-item v-else label="选择本地嵌入模型">
            <n-select
              v-model:value="createForm.localSpecId"
              :options="localSpecs.map(s => ({ label: s.modelName + ' (' + s.dimension + ' 维) [本地 ONNX]', value: s.id }))"
              placeholder="选择已就绪的本地模型"
            />
          </n-form-item>
        </div>

        <!-- 模式三：集中式在线服务商统一资产 -->
        <div v-else-if="createForm.bindingMode === 'online'">
          <div v-if="onlineSpecs.length === 0">
            <n-alert type="warning" style="margin-bottom: 12px;">
              <span>尚未在系统中配置在线服务商（如 SiliconFlow、OpenAI、Ollama 等）。</span>
              <n-button size="tiny" type="primary" secondary style="margin-left: 8px;" @click="goToModelManagement('providers')">
                前往「模型管理」添加在线服务商
              </n-button>
            </n-alert>
          </div>
          <n-form-item v-else label="选择服务商纳管模型">
            <n-select
              v-model:value="createForm.onlineSpecId"
              :options="onlineSpecs.map(s => ({ label: s.modelName + ' (' + s.dimension + ' 维) [云端]', value: s.id }))"
              placeholder="从已接入的服务商模型资产中选择"
            />
          </n-form-item>
        </div>

        <!-- 模式四：纯词法免模型 -->
        <div v-else-if="createForm.bindingMode === 'lexical'">
          <n-alert type="success" style="margin-bottom: 12px;">
            ⚡ <b>纯词法检索模式</b>：利用内置 SQLite FTS5 建立多字段加权倒排索引（BM25F）。无需加载任何向量模型，零显存消耗，入库吞吐量提升 50~100 倍，极速检索专有名词与代码符号。日后可随时一键【绑定模型】平滑升级为混合检索。
          </n-alert>
        </div>

        <n-divider title-placement="left">自适应分段策略规范 (Chunking Strategy)</n-divider>

        <n-form-item label="分段策略预设">
          <n-select
            v-model:value="createForm.chunkingSpecId"
            :options="chunkingSpecOptions"
            placeholder="选择该知识库采用的语义分块策略"
          />
        </n-form-item>

        <n-divider title-placement="left">初始数据连接器 (可选)</n-divider>

        <n-grid :cols="2" :x-gap="12">
          <n-gi>
            <n-form-item label="本地目标文件夹路径">
              <n-input-group style="width: 100%;">
                <n-input v-model:value="createForm.initialFolder" placeholder="例如: D:\\Documents\\TechNotes" />
                <n-button secondary type="primary" @click="selectInitialDirectory">选择文件夹</n-button>
              </n-input-group>
            </n-form-item>
          </n-gi>
          <n-gi>
            <n-form-item label="文件匹配模式 (Glob)">
              <n-input v-model:value="createForm.initialPattern" placeholder="*.md,*.txt,*.pdf" />
            </n-form-item>
          </n-gi>
        </n-grid>
      </n-form>

      <template #footer>
        <n-space justify="flex-end">
          <n-button @click="showCreateModal = false">取消</n-button>
          <n-button type="primary" :loading="creating" @click="handleCreateSubmit">
            立即创建
          </n-button>
        </n-space>
      </template>
    </n-modal>

    <!-- 更换向量模型模态框 (蓝绿影子重构) -->
    <n-modal v-model:show="showSwitchModelModal" preset="card" :title="switchModelTargetKb?.embeddingSpecId === 'spec_lexical_only' ? '为纯词法库绑定向量模型 (平滑升级为混合检索)' : '更换知识库向量模型 (蓝绿影子重构)'" style="width: 580px; max-width: 95vw;" :mask-closable="false">
      <n-space vertical :size="16">
        <n-alert v-if="switchModelTargetKb?.embeddingSpecId === 'spec_lexical_only'" type="success">
          💡 <b>为纯词法库绑定向量模型 (平滑升级为混合检索)</b>：系统将在后台影子表中分批计算新模型的切片向量。<b>计算重构期间该知识库纯词法检索完全不受影响</b>，全部切片处理完毕后在单个事务内原子切换为混合检索。
        </n-alert>
        <n-alert v-else type="info">
          💡 <b>零停机蓝绿影子重构</b>：系统将在后台影子表中分批计算新模型的切片向量。<b>计算重构期间该知识库检索完全不受影响</b>，全部切片处理完毕后在单个事务内原子切换生效。
        </n-alert>
        <n-descriptions :column="1" size="small" label-placement="left">
          <n-descriptions-item label="知识库名称">
            <n-text strong>{{ switchModelTargetKb?.name }}</n-text>
          </n-descriptions-item>
          <n-descriptions-item label="当前向量规格">
            <n-tag size="small" type="default">{{ switchModelTargetKb?.embeddingSpecId }}</n-tag>
          </n-descriptions-item>
          <n-descriptions-item label="配置健康状态">
            <n-tag v-if="driftMap[switchModelTargetKb?.id]?.status === 'Aligned'" size="small" type="success" :bordered="false" round>🟢 已对齐 (Aligned)</n-tag>
            <n-tag v-else-if="driftMap[switchModelTargetKb?.id]?.status === 'Drifted'" size="small" type="warning" round>🟡 全局模型已升级 (Drifted) - 建议重构</n-tag>
            <n-tag v-else-if="driftMap[switchModelTargetKb?.id]?.status === 'Customized'" size="small" type="info" :bordered="false" round>🔵 自定义独立模型 (Customized)</n-tag>
            <n-text v-else depth="3">探测中...</n-text>
          </n-descriptions-item>
        </n-descriptions>
        <n-form label-placement="top">
          <n-form-item label="目标向量模型规格 (Target Embedding Spec)" required>
            <n-select
              v-model:value="selectedTargetSpecId"
              :options="allEmbeddingModelOptions"
              placeholder="请选择要迁移的目标向量模型规格"
            />
          </n-form-item>
        </n-form>
      </n-space>
      <template #footer>
        <n-space justify="space-between" align="center" style="width: 100%;">
          <div>
            <n-button
              v-if="switchModelTargetKb?.embeddingSpecId !== 'spec_lexical_only'"
              type="warning"
              secondary
              :loading="reindexing"
              @click="handleUnbindModel"
            >
              解绑模型 (降级为纯词法)
            </n-button>
          </div>
          <n-space>
            <n-button @click="showSwitchModelModal = false">取消</n-button>
            <n-button type="primary" :loading="reindexing" @click="handleTriggerReindex">
              {{ switchModelTargetKb?.embeddingSpecId === 'spec_lexical_only' ? '确认绑定并计算切片向量' : '确认开始后台影子重构' }}
            </n-button>
          </n-space>
        </n-space>
      </template>
    </n-modal>

    <!-- 连接器管理抽屉 -->
    <n-drawer v-model:show="showConnectorDrawer" :width="560" placement="right">
      <n-drawer-content :title="'连接器管理 - ' + (currentKb?.name || '')">
        <n-space vertical :size="16">
          <n-alert type="info">
            连接器是知识库的数据输入管道。一个知识库可挂载多个本地目录，各目录变更将增量同步到此库专属的底层数据库中。
          </n-alert>

          <!-- 添加新连接器表单 -->
          <n-card title="挂载本地新目录" size="small">
            <n-space vertical :size="10">
              <n-input-group style="width: 100%;">
                <n-input v-model:value="newConnectorPath" placeholder="本地完整路径 (如 E:\\Notes)" />
                <n-button secondary type="primary" @click="selectConnectorDirectory">选择文件夹</n-button>
              </n-input-group>
              <n-input v-model:value="newConnectorPattern" placeholder="文件规则 (默认 *.md,*.txt,*.pdf)" />
              <n-button type="primary" block @click="handleAddConnector">
                <template #icon><n-icon :component="AddOutline" /></template>
                添加连接器
              </n-button>
            </n-space>
          </n-card>

          <!-- 已挂载连接器列表 -->
          <n-card title="已挂载连接器列表" size="small">
            <template #header-extra>
              <n-button v-if="currentConnectors.length > 0" size="small" type="primary" secondary @click="handleSyncAllConnectors">
                <template #icon><n-icon :component="PlayOutline" /></template>
                同步全部连接器
              </n-button>
            </template>
            <n-spin :show="loadingConnectors">
              <n-list v-if="currentConnectors.length > 0">
                <n-list-item v-for="conn in currentConnectors" :key="conn.id">
                  <n-thing :title="conn.targetPath">
                    <template #description>
                      <n-space align="center" :size="8" style="margin-top: 4px;">
                        <n-tooltip trigger="hover">
                          <template #trigger>
                            <n-tag size="small" :type="getReachabilityTagType(conn)" :bordered="false" round style="cursor: pointer;">
                              {{ getReachabilityText(conn) }}
                            </n-tag>
                          </template>
                          <span>{{ conn.diagnosticMessage || (conn.isReachable ? '物理目录正常访问' : '目录未就绪或无法访问') }}</span>
                        </n-tooltip>
                        <n-tag size="small" type="info" :bordered="false" round>
                          文档: {{ conn.documentCount || 0 }}
                        </n-tag>
                        <n-tag size="small" type="default" :bordered="false" round>
                          切片: {{ conn.chunkCount || 0 }}
                        </n-tag>
                        <n-text depth="3" style="font-size: 12px;">规则: {{ conn.filePattern || '全部文件' }}</n-text>
                      </n-space>
                    </template>
                    <template #extra>
                      <n-space>
                        <n-button size="small" type="primary" secondary @click="handleTriggerSync(currentKb.id, conn.id)">
                          <template #icon><n-icon :component="PlayOutline" /></template>
                          立即同步
                        </n-button>
                        <n-button size="small" secondary @click="openEditConnectorModal(conn)">
                          <template #icon><n-icon :component="CreateOutline" /></template>
                          编辑
                        </n-button>
                        <n-button size="small" quaternary type="error" @click="handleDeleteConnector(conn)">
                          <template #icon><n-icon :component="TrashOutline" /></template>
                        </n-button>
                      </n-space>
                    </template>
                  </n-thing>
                </n-list-item>
              </n-list>
              <n-empty v-else description="暂无挂载连接器" />
            </n-spin>
          </n-card>
        </n-space>
      </n-drawer-content>
    </n-drawer>

    <!-- Folder Picker Modal for Web Mode -->
    <folder-picker-modal
      v-model:show="showFolderPicker"
      :initial-path="folderPickerTarget === 'initial' ? createForm.initialFolder : newConnectorPath"
      @select="handleFolderSelected"
    />
  
    <!-- 编辑知识库模态框 -->
    <n-modal v-model:show="showEditModal" preset="card" title="编辑知识库设置" style="max-width: 560px">
      <n-form :model="editForm" label-placement="left" label-width="110">
        <n-form-item label="知识库名称" required>
          <n-input v-model:value="editForm.name" placeholder="请输入知识库名称" />
        </n-form-item>
        <n-form-item label="知识库描述">
          <n-input v-model:value="editForm.description" type="textarea" :rows="2" placeholder="知识库简介与用途说明" />
        </n-form-item>
        <n-form-item label="启用状态">
          <n-switch v-model:value="editForm.enabled">
            <template #checked>已启用 (参与检索与监控)</template>
            <template #unchecked>已停用 (暂停服务)</template>
          </n-switch>
        </n-form-item>
        <n-form-item label="模型规格 ID">
          <n-input v-model:value="editForm.embeddingSpecId" placeholder="例如 bge-m3-default" />
        </n-form-item>
        <n-form-item label="分段策略预设">
          <n-select v-model:value="editForm.chunkingSpecId" :options="chunkingSpecOptions" placeholder="选择分段策略" />
        </n-form-item>
      </n-form>
      <template #footer>
        <n-space justify="end">
          <n-button @click="showEditModal = false">取消</n-button>
          <n-button type="primary" :loading="updatingKb" @click="handleUpdateKbSubmit">保存修改</n-button>
        </n-space>
      </template>
    </n-modal>

    <!-- 重新分段模态框 -->
    <n-modal v-model:show="showRechunkModal" preset="card" title="知识库重新分段与重索引 (Re-chunking & Re-indexing)" style="width: 580px; max-width: 95vw;" :mask-closable="false">
      <n-space vertical :size="16">
        <n-alert v-if="rechunkTargetKb?.embeddingSpecId === 'spec_lexical_only'" type="info">
          💡 <b>纯词法重新分段全生命周期</b>：当前知识库为纯词法模式，重新分段不会触发向量计算，耗时极短（仅需毫秒级重构 SQLite FTS5 倒排索引）。分段大小建议控制在 400~800 Tokens，以保证最佳 BM25 词频饱和度与下游大模型问答上下文效果。
        </n-alert>
        <n-alert v-else type="warning">
          💡 <b>重新分段全生命周期</b>：系统将依据目标策略重新扫描并切分该知识库所有挂载文档，<b>重新计算语法块保护、行号区间（Start/EndLine）与标题层级</b>，并同步更新向量与词法索引。
        </n-alert>
        <n-descriptions :column="1" size="small" label-placement="left">
          <n-descriptions-item label="知识库名称">
            <b>{{ rechunkTargetKb?.name }}</b>
          </n-descriptions-item>
          <n-descriptions-item label="当前绑定模型">
            <n-tag size="small" :type="rechunkTargetKb?.embeddingSpecId === 'spec_lexical_only' ? 'success' : 'primary'">
              {{ rechunkTargetKb?.embeddingSpecId === 'spec_lexical_only' ? '纯词法 (FTS5 BM25 - 免模型)' : (rechunkTargetKb?.embeddingSpecId || 'default-spec') }}
            </n-tag>
          </n-descriptions-item>
          <n-descriptions-item label="当前分段规格">
            <n-tag size="small" type="info">{{ rechunkTargetKb?.chunkingSpecId || 'spec_chunk_default' }}</n-tag>
          </n-descriptions-item>
        </n-descriptions>
        <n-form label-placement="top">
          <n-form-item label="目标分段策略预设 (Target Chunking Strategy)" required>
            <n-select v-model:value="selectedRechunkSpecId" :options="chunkingSpecOptions" placeholder="请选择目标分段策略" />
          </n-form-item>
        </n-form>
      </n-space>
      <template #footer>
        <n-space justify="flex-end">
          <n-button @click="showRechunkModal = false">取消</n-button>
          <n-button type="warning" :loading="rechunking" @click="handleTriggerRechunk">
            确认并启动全库重新分段
          </n-button>
        </n-space>
      </template>
    </n-modal>

    <!-- 编辑连接器模态框 -->
    <n-modal v-model:show="showEditConnectorModal" preset="card" title="编辑连接器规则" style="max-width: 540px">
      <n-form :model="editConnForm" label-placement="left" label-width="100">
        <n-form-item label="目标路径" required>
          <n-input v-model:value="editConnForm.targetPath" placeholder="本地绝对路径" />
        </n-form-item>
        <n-form-item label="文件过滤模式">
          <n-input v-model:value="editConnForm.filePattern" placeholder="例如 *.md,*.txt,*.pdf" />
        </n-form-item>
        <n-form-item label="启用同步">
          <n-switch v-model:value="editConnForm.enabled" />
        </n-form-item>
      </n-form>
      <template #footer>
        <n-space justify="end">
          <n-button @click="showEditConnectorModal = false">取消</n-button>
          <n-button type="primary" :loading="updatingConn" @click="handleUpdateConnectorSubmit">保存连接器</n-button>
        </n-space>
      </template>
    </n-modal>

    <!-- 文档与切片精细化治理抽屉 (Documents & Chunks HITL Explorer) -->
    <n-drawer v-model:show="showExplorerDrawer" :width="900" placement="right">
      <n-drawer-content :title="'知识库治理与切片微调 - ' + (explorerKb ? explorerKb.name : '')" closable>
        <n-tabs v-model:value="explorerTab" type="line" animated>
          <!-- 文档视图 -->
          <n-tab-pane name="documents" tab="收录文档列表">
            <n-space vertical :size="12">
              <n-space justify="space-between" align="center">
                <n-input v-model:value="docSearch" placeholder="搜索文档名或路径..." clearable @keydown.enter="loadExplorerDocuments" style="width: 300px">
                  <template #prefix><n-icon :component="SearchOutline" /></template>
                </n-input>
                <n-space>
                  <n-button type="primary" size="small" @click="showAddDocModal = true">
                    <template #icon><n-icon :component="AddOutline" /></template>
                    + 录入单篇文档
                  </n-button>
                  <n-button secondary size="small" @click="loadExplorerDocuments" :loading="loadingDocs">
                    <template #icon><n-icon :component="RefreshOutline" /></template>
                    刷新文档
                  </n-button>
                </n-space>
              </n-space>

              <n-spin :show="loadingDocs">
                <div v-if="documents.length === 0" style="padding: 40px 0; text-align: center;">
                  <n-text depth="3">暂无收录文档</n-text>
                </div>
                <n-list v-else hoverable clickable>
                  <n-list-item v-for="doc in documents" :key="doc.id">
                    <n-thing :title="doc.sourceKey" :description="'ID: ' + doc.id">
                      <template #header-extra>
                        <n-space align="center">
                          <n-tag size="small" type="info">{{ doc.chunkCount }} 个切片</n-tag>
                          <n-tag size="small" type="default">v{{ doc.documentVersion }}</n-tag>
                        </n-space>
                      </template>
                      <template #action>
                        <n-space justify="space-between" align="center">
                          <n-text depth="3" style="font-size: 12px">更新时间: {{ new Date(doc.updatedAt).toLocaleString() }}</n-text>
                          <n-space size="small">
                            <n-button size="small" secondary type="primary" @click="filterChunksByDoc(doc)">
                              查看切片
                            </n-button>
                            <n-button size="small" quaternary type="error" @click="handleDeleteDoc(doc)">
                              删除文档
                            </n-button>
                          </n-space>
                        </n-space>
                      </template>
                    </n-thing>
                  </n-list-item>
                </n-list>
              </n-spin>

              <n-pagination
                v-if="docTotal > docPageSize"
                v-model:page="docPage"
                :page-size="docPageSize"
                :item-count="docTotal"
                @update:page="loadExplorerDocuments"
              />
            </n-space>
          </n-tab-pane>

          <!-- 切片管理与纠错视图 -->
          <n-tab-pane name="chunks" tab="切片管理与纠错 (HITL)">
            <n-space vertical :size="12">
              <n-space justify="space-between" align="center">
                <n-space align="center" :size="12">
                  <n-input v-model:value="chunkSearch" placeholder="搜索切片内容..." clearable @keydown.enter="loadExplorerChunks" style="width: 260px">
                    <template #prefix><n-icon :component="SearchOutline" /></template>
                  </n-input>
                  <n-tag v-if="chunkDocFilter" closable @close="chunkDocFilter = null; loadExplorerChunks()">
                    过滤文档: {{ documents.find(d => d.id === chunkDocFilter)?.sourceKey || chunkDocFilter.slice(0, 8) }}
                  </n-tag>
                  <n-checkbox v-model:checked="chunkEnabledOnly" @update:checked="loadExplorerChunks">
                    仅有效切片
                  </n-checkbox>
                </n-space>
                <n-space align="center">
                  <n-button type="primary" size="small" @click="showAddChunkModal = true">
                    <template #icon><n-icon :component="AddOutline" /></template>
                    + 手动添加分段
                  </n-button>
                  <n-button secondary size="small" @click="loadExplorerChunks" :loading="loadingChunks">
                    <template #icon><n-icon :component="RefreshOutline" /></template>
                    刷新
                  </n-button>
                </n-space>
              </n-space>

              <n-spin :show="loadingChunks">
                <div v-if="chunks.length === 0" style="padding: 40px 0; text-align: center;">
                  <n-text depth="3">暂无匹配分块记录</n-text>
                </div>
                <div v-else style="display: flex; flex-direction: column; gap: 12px;">
                  <n-card v-for="chk in chunks" :key="chk.id" size="small" :bordered="true" style="border-radius: 6px;">
                    <template #header>
                      <n-space align="center" justify="space-between">
                        <n-space align="center" :size="8">
                          <n-tag size="small" type="default">#{{ chk.ordinal }}</n-tag>
                          <n-tag size="small" type="info">{{ chk.tokenCount }} 字/Tokens</n-tag>
                          <n-tag size="small" :type="chk.hasVector ? 'success' : 'warning'">
                            {{ chk.hasVector ? '向量就绪' : '无向量' }}
                          </n-tag>
                          <n-tag v-if="chk.startLine > 0" size="small" :bordered="false">行 {{ chk.startLine }}-{{ chk.endLine }}</n-tag>
                          <n-text v-if="chk.headingPath" depth="3" style="font-size: 12px;">/ {{ chk.headingPath }}</n-text>
                          <n-text v-if="chk.documentSourceKey" depth="3" style="font-size: 12px;">[{{ chk.documentSourceKey }}]</n-text>
                        </n-space>
                        <n-space align="center" :size="8">
                          <n-switch :value="chk.enabled" size="small" @update:value="val => handleToggleChunk(chk, val)">
                            <template #checked>已启用</template>
                            <template #unchecked>已下线</template>
                          </n-switch>
                          <n-button v-if="editingChunkId !== chk.id" size="tiny" secondary @click="startEditChunk(chk)">
                            <template #icon><n-icon :component="CreateOutline" /></template>
                            微调
                          </n-button>
                          <n-button size="tiny" quaternary type="error" @click="handleDeleteChunk(chk)">
                            <template #icon><n-icon :component="TrashOutline" /></template>
                          </n-button>
                        </n-space>
                      </n-space>
                    </template>

                    <!-- 查看模式 -->
                    <div v-if="editingChunkId !== chk.id" style="white-space: pre-wrap; font-size: 13px; line-height: 1.6; color: #ddd; max-height: 180px; overflow-y: auto;">
                      {{ chk.text }}
                    </div>

                    <!-- 原地编辑模式 (HITL) -->
                    <div v-else style="display: flex; flex-direction: column; gap: 8px;">
                      <n-input
                        v-model:value="editingChunkText"
                        type="textarea"
                        :autosize="{ minRows: 3, maxRows: 8 }"
                        placeholder="编辑切片文本内容..."
                      />
                      <n-space justify="space-between" align="center">
                        <n-checkbox v-model:checked="recomputeEmbeddingImmediately">
                          即时重算密集向量 (300ms 刷新)
                        </n-checkbox>
                        <n-space size="small">
                          <n-button size="small" @click="cancelEditChunk">取消</n-button>
                          <n-button size="small" type="primary" :loading="savingChunk" @click="saveEditChunk(chk)">
                            保存修改并重新索引
                          </n-button>
                        </n-space>
                      </n-space>
                    </div>
                  </n-card>
                </div>
              </n-spin>

              <n-pagination
                v-if="chunkTotal > chunkPageSize"
                v-model:page="chunkPage"
                :page-size="chunkPageSize"
                :item-count="chunkTotal"
                @update:page="loadExplorerChunks"
              />
            </n-space>
          </n-tab-pane>
        </n-tabs>
      </n-drawer-content>
    </n-drawer>

    <!-- 手动添加切片模态框 -->
    <n-modal v-model:show="showAddChunkModal" preset="card" title="手动补充知识分段" style="max-width: 600px">
      <n-form :model="newChunkForm" label-placement="top">
        <n-form-item label="分段文本内容 (将即时计算向量并加入 BM25 索引)" required>
          <n-input
            v-model:value="newChunkForm.text"
            type="textarea"
            :autosize="{ minRows: 5, maxRows: 10 }"
            placeholder="输入规章制度、问答对或业务知识补充项..."
          />
        </n-form-item>
      </n-form>
      <template #footer>
        <n-space justify="end">
          <n-button @click="showAddChunkModal = false">取消</n-button>
          <n-button type="primary" :loading="addingChunk" @click="handleAddChunk">确认添加</n-button>
        </n-space>
      </template>
    </n-modal>

    <!-- 手动录入单篇文档模态框 -->
    <n-modal v-model:show="showAddDocModal" preset="card" title="录入单篇文档并自动分块" style="max-width: 640px">
      <n-form :model="newDocForm" label-placement="top">
        <n-form-item label="文档标识或路径 (SourceKey)" required>
          <n-input v-model:value="newDocForm.sourceKey" placeholder="例如: docs/quickstart.md 或 manual/faq.txt" />
        </n-form-item>
        <n-form-item label="文档正文内容" required>
          <n-input
            v-model:value="newDocForm.content"
            type="textarea"
            :autosize="{ minRows: 6, maxRows: 14 }"
            placeholder="输入文档的 Markdown 或纯文本正文内容，系统将根据分块策略自动切分并构建词法索引..."
          />
        </n-form-item>
      </n-form>
      <template #footer>
        <n-space justify="end">
          <n-button @click="showAddDocModal = false">取消</n-button>
          <n-button type="primary" :loading="addingDoc" @click="handleAddDoc">确认录入并切片</n-button>
        </n-space>
      </template>
    </n-modal>

  </n-space>
`;

export default component;
