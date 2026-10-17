import { defineComponent as _defineComponent } from 'vue';
import { ref, h, onMounted } from 'vue';
import { useMessage, useDialog, NButton, NSpace, NTag, NPopconfirm, NInput, NRadioGroup, NRadio, NText } from 'naive-ui';
import { sourcesApi, indexApi, indexJobsApi, settingsApi } from '/app/api/index.js';
import {isDesktop,selectFolder} from '/core/platform.js';
const component = /*@__PURE__*/ _defineComponent({
    __name: 'Sources',
    setup(__props, { expose: __expose }) {
        __expose();
        const message = useMessage();
        const dialog = useDialog();
        const formRef = ref(null);
        const loading = ref(false);
        const creating = ref(false);
        const sources = ref([]);
        // Edit state
        const editingSource = ref(null);
        const editingName = ref('');
        // Vector index state
        const loadingIndex = ref(false);
        const rebuilding = ref(false);
        const cleaning = ref(false);
        const deletingAll = ref(false);
        const quickIndexing = ref(false);
        const vectorOnlyIndexing = ref(false);
        const sourceQuickIndexing = ref(new Set());
        const sourceVectorOnlyIndexing = ref(new Set());
        const indexSummary = ref(null);
        const chunkingConfig = ref(null);
        const newSource = ref({
            path: '',
            name: ''
        });
        const selectDirectory=async()=>{
            try { const path=await selectFolder();if(path)newSource.value.path=path; }
            catch(error){message.error(error.message);}
        };
        const formRules = {
            path: { required: true, message: '请输入文件夹路径', trigger: 'blur' }
        };
        const columns = [
            {
                title: '名称',
                key: 'name',
                width: 160
            },
            {
                title: '路径',
                key: 'path',
                ellipsis: { tooltip: true }
            },
            {
                title: '状态',
                key: 'enabled',
                width: 80,
                render: (row) => h(NTag, { type: row.enabled ? 'success' : 'default', size: 'small' }, {
                    default: () => row.enabled ? '启用' : '停用'
                })
            },
            {
                title: '文件数',
                key: 'fileCount',
                width: 80
            },
            {
                title: '分块数',
                key: 'chunkCount',
                width: 80
            },
            {
                title: '最近索引',
                key: 'lastIndexed',
                width: 160,
                render: (row) => row.lastIndexed
                    ? new Date(row.lastIndexed).toLocaleString('zh-CN')
                    : '-'
            },
            {
                title: '操作',
                key: 'actions',
                width: 380,
                render: (row) => h(NSpace, { size: 'small' }, {
                    default: () => [
                        h(NButton, {
                            size: 'small',
                            onClick: () => handleEdit(row)
                        }, {
                            default: () => '编辑'
                        }),
                        h(NButton, {
                            size: 'small',
                            type: row.enabled ? 'warning' : 'primary',
                            onClick: () => handleToggle(row)
                        }, {
                            default: () => row.enabled ? '停用' : '启用'
                        }),
                        h(NButton, {
                            size: 'small',
                            type: 'success',
                            loading: sourceQuickIndexing.value.has(row.name),
                            onClick: () => handleQuickIndexSource(row)
                        }, {
                            default: () => '增量索引'
                        }),
                        h(NButton, {
                            size: 'small',
                            type: 'info',
                            loading: sourceVectorOnlyIndexing.value.has(row.name),
                            onClick: () => handleVectorOnlyIndexSource(row)
                        }, {
                            default: () => '补全向量'
                        }),
                        h(NButton, {
                            size: 'small',
                            onClick: () => handleReindex(row)
                        }, {
                            default: () => '重建索引'
                        }),
                        h(NButton, {
                            size: 'small',
                            type: 'error',
                            onClick: () => handleDelete(row)
                        }, {
                            default: () => '删除'
                        })
                    ]
                })
            }
        ];
        const indexColumns = [
            {
                title: '模型',
                key: 'modelName',
                render: (row) => h(NSpace, { align: 'center', size: 'small' }, {
                    default: () => [
                        row.isCurrentModel ? h(NTag, { type: 'success', size: 'small' }, { default: () => '当前' }) : null,
                        row.modelName
                    ]
                })
            },
            {
                title: '维度',
                key: 'dimension',
                width: 80
            },
            {
                title: '向量数',
                key: 'vectorCount',
                width: 100,
                render: (row) => row.vectorCount.toLocaleString()
            },
            {
                title: '存储大小',
                key: 'storageMB',
                width: 100,
                render: (row) => `${row.storageMB.toFixed(2)} MB`
            },
            {
                title: '操作',
                key: 'actions',
                width: 120,
                render: (row) => h(NSpace, { size: 'small' }, {
                    default: () => [
                        h(NPopconfirm, {
                            onPositiveClick: () => handleDeleteModelIndex(row.modelName)
                        }, {
                            trigger: () => h(NButton, { size: 'small', type: 'error', disabled: row.isCurrentModel }, { default: () => '删除' }),
                            default: () => `确定要删除模型 "${row.modelName}" 的向量索引吗？`
                        })
                    ]
                })
            }
        ];
        const loadSources = async () => {
            loading.value = true;
            try {
                const response = await sourcesApi.getAll();
                sources.value = response.data;
            }
            catch (error) {
                message.error('加载源列表失败');
            }
            finally {
                loading.value = false;
            }
        };
        const loadVectorIndex = async () => {
            loadingIndex.value = true;
            try {
                const response = await indexApi.getSummary();
                indexSummary.value = response.data;
            }
            catch (error) {
                message.error('加载向量索引信息失败');
            }
            finally {
                loadingIndex.value = false;
            }
        };
        const loadChunkingConfig = async () => {
            try {
                const response = await settingsApi.get();
                chunkingConfig.value = response.data?.chunking ?? null;
            }
            catch {
                chunkingConfig.value = null;
            }
        };
        const handleCreateSource = async () => {
            try {
                await formRef.value?.validate();
            }
            catch {
                return;
            }
            creating.value = true;
            try {
                await sourcesApi.create({
                    path: newSource.value.path,
                    name: newSource.value.name || undefined
                });
                message.success('源添加成功');
                resetForm();
                await loadSources();
            }
            catch (error) {
                const err = error;
                const msg = err.response?.data?.error || '添加源失败';
                message.error(msg);
            }
            finally {
                creating.value = false;
            }
        };
        const resetForm = () => {
            newSource.value = { path: '', name: '' };
            formRef.value?.restoreValidation();
        };
        const handleToggle = async (source) => {
            try {
                await sourcesApi.toggle(source.name, !source.enabled);
                message.success(source.enabled ? '已停用' : '已启用');
                await loadSources();
            }
            catch (error) {
                message.error(error.response?.data?.error || error.message || '操作失败');
            }
        };
        const handleEdit = (source) => {
            editingSource.value = source;
            const currentPatterns = Array.isArray(source.filePatterns) ? source.filePatterns.join(', ') : (source.filePatterns || '');
            const editingPatterns = ref(currentPatterns);
            dialog.create({
                title: `编辑数据源配置 - ${source.name}`,
                content: () => h('div', { style: 'display: flex; flex-direction: column; gap: 12px; margin-top: 8px;' }, [
                    h('div', [
                        h(NText, { depth: '3', style: 'font-size: 12px; display: block; margin-bottom: 4px;' }, '数据源名称（系统内唯一标识，不支持重命名）：'),
                        h(NInput, { value: source.name, disabled: true })
                    ]),
                    h('div', [
                        h(NText, { depth: '3', style: 'font-size: 12px; display: block; margin-bottom: 4px;' }, '文件匹配规则（以逗号或分号分隔，留空使用默认 *.md, *.txt）：'),
                        h(NInput, {
                            value: editingPatterns.value,
                            onUpdateValue: (val) => { editingPatterns.value = val; },
                            placeholder: '例如: *.md, *.txt'
                        })
                    ])
                ]),
                positiveText: '保存配置',
                negativeText: '取消',
                onPositiveClick: async () => {
                    const patterns = editingPatterns.value
                        .split(/[,;\n]/)
                        .map(p => p.trim())
                        .filter(Boolean);
                    try {
                        await sourcesApi.update(source.name, {
                            filePatterns: patterns.length > 0 ? patterns : []
                        });
                        message.success('数据源配置已更新');
                        await loadSources();
                    }
                    catch (error) {
                        message.error(error.response?.data?.error || '更新失败');
                        return false;
                    }
                }
            });
        };
        const handleReindex = (source) => {
            dialog.warning({
                title: '确认重建索引',
                content: `确定要重建 "${source.name}" 的向量索引吗？这会基于当前模型重新生成该数据源的向量。`,
                positiveText: '确定',
                negativeText: '取消',
                onPositiveClick: async () => {
                    try {
                        const response = await indexApi.rebuildSource(source.name);
                        message.success(response.data.message || '重建任务已启动');
                        await loadVectorIndex();
                    }
                    catch (error) {
                        message.error('启动重建失败');
                    }
                }
            });
        };
        const handleDelete = (source) => {
            const deleteDataRef = ref(true);
            dialog.warning({
                title: `删除源 "${source.name}"`,
                content: () => h('div', { style: 'margin-top: 8px;' }, [
                    h('p', { style: 'margin-bottom: 12px; color: #d03050; font-size: 13px;' }, '注意：删除后配置将无法恢复。请选择清理范围：'),
                    h(NRadioGroup, {
                        value: deleteDataRef.value,
                        'onUpdate:value': (val) => { deleteDataRef.value = val; }
                    }, {
                        default: () => h(NSpace, { vertical: true }, {
                            default: () => [
                                h(NRadio, { value: true }, { default: () => '删除源配置 + 清空已索引数据（推荐，防止孤立向量）' }),
                                h(NRadio, { value: false }, { default: () => '仅删除源配置（保留已有索引和分块数据）' })
                            ]
                        })
                    })
                ]),
                positiveText: '确认删除',
                negativeText: '取消',
                onPositiveClick: async () => {
                    const deleteData = deleteDataRef.value;
                    try {
                        await sourcesApi.delete(source.name, deleteData);
                        message.success(deleteData ? `"${source.name}" 及其索引数据已全部删除` : `"${source.name}" 配置已删除（索引数据保留）`);
                        await loadSources();
                        await loadVectorIndex();
                    }
                    catch (error) {
                        message.error(error.response?.data?.error || error.message || '删除失败');
                    }
                },
                onNegativeClick: () => {
                    // 安全取消，不做任何删除
                }
            });
        };
        const handleQuickIndexAll = async () => {
            quickIndexing.value = true;
            try {
                const response = await indexJobsApi.startJob({ force: false });
                message.success(response.data.message || '增量索引任务已启动（跳过未变更文件）');
                await loadVectorIndex();
            }
            catch (error) {
                message.error(error.response?.data?.error || error.message || '启动增量索引失败');
            }
            finally {
                quickIndexing.value = false;
            }
        };
        const handleQuickIndexSource = async (source) => {
            sourceQuickIndexing.value.add(source.name);
            sourceQuickIndexing.value = new Set(sourceQuickIndexing.value);
            try {
                await indexJobsApi.startJob({ sources: [source.name], force: false });
                message.success(`${source.name} 增量索引已启动（跳过未变更文件）`);
                await loadVectorIndex();
            }
            catch (error) {
                message.error(error.response?.data?.error || error.message || `${source.name} 增量索引启动失败`);
            }
            finally {
                sourceQuickIndexing.value.delete(source.name);
                sourceQuickIndexing.value = new Set(sourceQuickIndexing.value);
            }
        };
        const handleVectorOnlyIndexAll = async () => {
            vectorOnlyIndexing.value = true;
            try {
                const response = await indexJobsApi.startJob({ vectorOnly: true });
                message.success(response.data.message || '向量补全任务已启动（仅重推向量，不动分块/BM25/图谱）');
                await loadVectorIndex();
            }
            catch (error) {
                message.error(error.response?.data?.error || error.message || '启动向量补全失败');
            }
            finally {
                vectorOnlyIndexing.value = false;
            }
        };
        const handleVectorOnlyIndexSource = async (source) => {
            sourceVectorOnlyIndexing.value.add(source.name);
            sourceVectorOnlyIndexing.value = new Set(sourceVectorOnlyIndexing.value);
            try {
                await indexJobsApi.startJob({ sources: [source.name], vectorOnly: true });
                message.success(`${source.name} 向量补全已启动`);
                await loadVectorIndex();
            }
            catch (error) {
                message.error(error.response?.data?.error || error.message || `${source.name} 向量补全启动失败`);
            }
            finally {
                sourceVectorOnlyIndexing.value.delete(source.name);
                sourceVectorOnlyIndexing.value = new Set(sourceVectorOnlyIndexing.value);
            }
        };
        const handleRebuildAll = async () => {
            rebuilding.value = true;
            try {
                const response = await indexApi.rebuild();
                message.success(response.data.message || '向量索引重建任务已启动');
                await loadVectorIndex();
            }
            catch (error) {
                message.error(error.response?.data?.error || error.message || '启动重建失败');
            }
            finally {
                rebuilding.value = false;
            }
        };
        const handleCleanup = async () => {
            cleaning.value = true;
            try {
                const response = await indexApi.cleanup();
                message.success(response.data.message);
                await loadVectorIndex();
            }
            catch (error) {
                message.error(error.response?.data?.error || error.message || '清理失败');
            }
            finally {
                cleaning.value = false;
            }
        };
        const handleDeleteAllIndex = async () => {
            deletingAll.value = true;
            try {
                const response = await indexApi.deleteAllModels();
                message.success(`已删除 ${response.data.totalDeleted} 条向量`);
                await loadVectorIndex();
            }
            catch (error) {
                message.error(error.response?.data?.error || error.message || '删除失败');
            }
            finally {
                deletingAll.value = false;
            }
        };
        const handleDeleteModelIndex = async (modelName) => {
            try {
                const response = await indexApi.deleteModel(modelName);
                message.success(response.data.message);
                await loadVectorIndex();
            }
            catch (error) {
                message.error(error.response?.data?.error || error.message || '删除失败');
            }
        };
        onMounted(() => {
            loadSources();
            loadVectorIndex();
            loadChunkingConfig();
        });
        const __returned__ = { isDesktop, selectDirectory, message, dialog, formRef, loading, creating, sources, editingSource, editingName, loadingIndex, rebuilding, cleaning, deletingAll, quickIndexing, vectorOnlyIndexing, sourceQuickIndexing, sourceVectorOnlyIndexing, indexSummary, chunkingConfig, loadChunkingConfig, newSource, formRules, columns, indexColumns, loadSources, loadVectorIndex, handleCreateSource, resetForm, handleToggle, handleEdit, handleReindex, handleDelete, handleQuickIndexAll, handleQuickIndexSource, handleVectorOnlyIndexAll, handleVectorOnlyIndexSource, handleRebuildAll, handleCleanup, handleDeleteAllIndex, handleDeleteModelIndex, get NButton() { return NButton; }, get NSpace() { return NSpace; }, get NTag() { return NTag; }, get NPopconfirm() { return NPopconfirm; }, get NInput() { return NInput; } };
        return __returned__;
    }
});

component.template = "\n  <n-space vertical :size=\"20\">\n    <!-- Add Source -->\n    <n-card title=\"添加源\">\n      <n-space vertical>\n        <n-form\n          ref=\"formRef\"\n          :model=\"newSource\"\n          :rules=\"formRules\"\n          label-placement=\"left\"\n          label-width=\"80\"\n        >\n          <n-form-item :label=\"isDesktop ? '本机目录' : '服务器目录'\" path=\"path\">\n            <n-input v-model:value=\"newSource.path\" placeholder=\"输入文件夹绝对路径（必填）\" /><n-button v-if=\"isDesktop\" @click=\"selectDirectory\">选择文件夹</n-button>\n          </n-form-item>\n          <n-form-item label=\"名称\">\n            <n-input v-model:value=\"newSource.name\" placeholder=\"输入源名称（可选，默认使用文件夹名）\" />\n          </n-form-item>\n          <n-space>\n            <n-button type=\"primary\" :loading=\"creating\" @click=\"handleCreateSource\">\n              添加\n            </n-button>\n            <n-button @click=\"resetForm\">重置</n-button>\n          </n-space>\n        </n-form>\n      </n-space>\n    </n-card>\n\n    <!-- Vector Index Management -->\n    <n-card title=\"向量索引管理\">\n      <template #header-extra>\n        <n-space>\n          <n-button size=\"small\" :loading=\"loadingIndex\" @click=\"() => { loadVectorIndex(); loadChunkingConfig(); }\">\n            刷新\n          </n-button>\n        </n-space>\n      </template>\n      <n-spin :show=\"loadingIndex\">\n        <n-space vertical :size=\"16\">\n          <!-- Current Model Info -->\n          <n-descriptions label-placement=\"left\" :column=\"3\" bordered size=\"small\">\n            <n-descriptions-item label=\"当前模型\">\n              <n-tag type=\"primary\">{{ indexSummary?.currentModel || '-' }}</n-tag>\n            </n-descriptions-item>\n            <n-descriptions-item label=\"向量维度\">\n              {{ indexSummary?.currentDimension || '-' }}\n            </n-descriptions-item>\n            <n-descriptions-item label=\"总文件数\">\n              {{ indexSummary?.totalFiles || 0 }}\n            </n-descriptions-item>\n            <n-descriptions-item label=\"总分块数\">\n              {{ indexSummary?.totalChunks || 0 }}\n            </n-descriptions-item>\n          </n-descriptions>\n\n          <!-- Chunking Config -->\n          <n-descriptions label-placement=\"left\" :column=\"5\" bordered size=\"small\">\n            <n-descriptions-item label=\"最大 Token 数\">\n              {{ chunkingConfig?.maxTokens ?? '-' }}\n            </n-descriptions-item>\n            <n-descriptions-item label=\"最小 Token 数\">\n              {{ chunkingConfig?.minTokens ?? '-' }}\n            </n-descriptions-item>\n            <n-descriptions-item label=\"重叠 Token 数\">\n              {{ chunkingConfig?.overlapTokens ?? '-' }}\n            </n-descriptions-item>\n            <n-descriptions-item label=\"保留标题结构\">\n              <n-tag :type=\"chunkingConfig?.preserveHeadings ? 'success' : 'default'\" size=\"small\">{{ chunkingConfig?.preserveHeadings ? '启用' : '停用' }}</n-tag>\n            </n-descriptions-item>\n            <n-descriptions-item label=\"保留代码块\">\n              <n-tag :type=\"chunkingConfig?.preserveCodeBlocks ? 'success' : 'default'\" size=\"small\">{{ chunkingConfig?.preserveCodeBlocks ? '启用' : '停用' }}</n-tag>\n            </n-descriptions-item>\n          </n-descriptions>\n\n          <!-- Model Stats Table -->\n          <n-data-table\n            :columns=\"indexColumns\"\n            :data=\"indexSummary?.modelStats || []\"\n            :bordered=\"false\"\n            size=\"small\"\n          />\n\n          <!-- Actions -->\n          <n-space>\n            <n-button type=\"success\" :loading=\"quickIndexing\" @click=\"handleQuickIndexAll\">\n              快速增量索引（全局）\n            </n-button>\n            <n-popconfirm @positive-click=\"handleVectorOnlyIndexAll\">\n              <template #trigger>\n                <n-button type=\"info\" :loading=\"vectorOnlyIndexing\">\n                  补全缺失向量（全局）\n                </n-button>\n              </template>\n              将对所有文件重新生成向量，不修改分块/BM25/图谱。适用于向量数少于分块数时补全缺口。\n            </n-popconfirm>\n            <n-popconfirm @positive-click=\"handleRebuildAll\">\n              <template #trigger>\n                <n-button type=\"primary\" :loading=\"rebuilding\">\n                  重建全部向量索引\n                </n-button>\n              </template>\n              确定要使用当前模型重建所有向量索引吗？这将删除现有向量并重新生成。\n            </n-popconfirm>\n            <n-popconfirm @positive-click=\"handleCleanup\">\n              <template #trigger>\n                <n-button type=\"warning\" :loading=\"cleaning\">\n                  清理孤立索引\n                </n-button>\n              </template>\n              确定要清理孤立向量索引（模型已不存在的向量数据）吗？\n            </n-popconfirm>\n            <n-popconfirm @positive-click=\"handleDeleteAllIndex\">\n              <template #trigger>\n                <n-button type=\"error\" :loading=\"deletingAll\">\n                  删除所有向量索引\n                </n-button>\n              </template>\n              确定要删除所有向量索引吗？此操作不可恢复！\n            </n-popconfirm>\n          </n-space>\n        </n-space>\n      </n-spin>\n    </n-card>\n\n    <!-- Sources Table -->\n    <n-card title=\"源列表\">\n      <template #header-extra>\n        <n-button size=\"small\" :loading=\"loading\" @click=\"loadSources\">\n          刷新\n        </n-button>\n      </template>\n      <n-data-table\n        :columns=\"columns\"\n        :data=\"sources\"\n        :loading=\"loading\"\n        :bordered=\"false\"\n        :row-key=\"(row) => row.name\"\n      />\n    </n-card>\n  </n-space>\n";
export default component;
