import { defineComponent as _defineComponent } from 'vue';
import { ref, h, onMounted, onUnmounted, onActivated, computed } from 'vue';
import { NTag, NButton, NSpace, NProgress, NAlert, NCode, NIcon, NPopconfirm, NRadioGroup, NRadio, NFormItem, NSpin, NSteps, NStep, useMessage } from 'naive-ui';
import { RefreshOutline, AddOutline, InformationCircleOutline, SearchOutline } from '@vicons/ionicons5';
import { modelsApi, settingsApi } from '/app/api/index.js';
const component = /*@__PURE__*/ _defineComponent({
    ...{ name: 'Models' },
    __name: 'Models',
    setup(__props, { expose: __expose }) {
        __expose();
        const message = useMessage();
        const getInitialModelTab = () => {
            try {
                const hash = window.location.hash || '';
                const qIndex = hash.indexOf('?');
                if (qIndex !== -1) {
                    const params = new URLSearchParams(hash.substring(qIndex + 1));
                    const t = params.get('tab');
                    if (t === 'rerank' || t === 'embedding' || t === 'chat') return t;
                }
            } catch {}
            return 'embedding';
        };
        const activeModelTab = ref(getInitialModelTab());
        const modelsPath = ref('');
        const modelsPathLoading = ref(false);
        const savingModelsPath = ref(false);
        const embeddingApiMode = ref(false);
        const rerankApiMode = ref(false);
        const chatApiMode = ref(false);
        const models = ref([]);
        const currentModel = ref(null);
        const modelsLoading = ref(false);
        const currentLoading = ref(false);
        const chatModels = ref([]);
        const currentChatModel = ref(null);
        const chatModelsLoading = ref(false);
        const chatCurrentLoading = ref(false);
        const showSwitchDialog = ref(false);
        const showDownloadDialog = ref(false);
        const showConvertDialog = ref(false);
        const showAddCustomDialog = ref(false);
        const showDeleteDialog = ref(false);
        const selectedModel = ref(null);
        const switching = ref(false);
        const converting = ref(false);
        const deleting = ref(false);
        const addingCustom = ref(false);
        const targetFormat = ref('embedded');
        const deleteOldVectors = ref(false);
        const downloadProgress = ref(new Map());
        const downloadOptions = ref(null);
        const selectedFile = ref(null);
        const loadingOptions = ref(false);
        const rowLoadingMap = ref({});
        const downloadState = ref('idle');
        const showDownloadModal = ref(false);
        const activeDownloadModel = ref(null);
        const currentAbortController = ref(null);
        const showVariantSearchModal = ref(false);
        const variantSearchQuery = ref('');
        const variantSearchFormat = ref('all');
        const variantSearchResults = ref([]);
        const variantSearching = ref(false);
        const selectedVariantDetail = ref(null);
        const loadingVariantDetail = ref(false);
        const runtimeStatus = ref(null);
        const runtimeStatusLoading = ref(false);
        const switchVariantDialog = ref({
            show: false,
            model: null,
            variant: null,
            isRerank: false,
            loading: false
        });

        const loadRuntimeStatus = async () => {
            runtimeStatusLoading.value = true;
            try {
                const response = await modelsApi.getRuntimeStatus();
                runtimeStatus.value = response.data;
            } catch (error) {
                console.error('Failed to load models runtime status:', error);
            } finally {
                runtimeStatusLoading.value = false;
            }
        };

        const handleSwitchVariant = (model, variant, isRerank = false) => {
            switchVariantDialog.value = {
                show: true,
                model,
                variant,
                isRerank,
                loading: false
            };
        };

        const confirmSwitchVariant = async () => {
            const { model, variant, isRerank } = switchVariantDialog.value;
            if (!model || !variant) return;
            switchVariantDialog.value.loading = true;
            try {
                const isChat = isRerank === 'chat' || model.modelType === 'chat';
                const res = await modelsApi.switchVariant(model.name, variant.fileName, isRerank === true, isChat ? 'chat' : null);
                message.success(res.data?.message || `已成功切换到版本 ${variant.fileName}`);
                switchVariantDialog.value.show = false;
                await loadRuntimeStatus();
                if (isChat) {
                    await loadCurrentChatModel();
                    await loadChatModels();
                } else if (isRerank) {
                    await loadCurrentRerankModel();
                    await loadRerankModels();
                } else {
                    await loadCurrentModel();
                    await loadModels();
                }
            } catch (error) {
                console.error('Failed to switch variant:', error);
                message.error(`切换版本失败: ${error.response?.data?.error || error.message}`);
            } finally {
                switchVariantDialog.value.loading = false;
            }
        };

        const handleDeleteVariant = async (model, variant, isRerank = false) => {
            if (!model || !variant) return;
            try {
                const isChat = isRerank === 'chat' || model.modelType === 'chat';
                const res = await modelsApi.deleteVariant(model.name, variant.fileName, isRerank === true);
                message.success(res.data?.message || `已删除版本变体 ${variant.fileName}`);
                await loadRuntimeStatus();
                if (isChat) {
                    await loadChatModels();
                    await loadCurrentChatModel();
                } else if (isRerank) {
                    await loadRerankModels();
                    await loadCurrentRerankModel();
                } else {
                    await loadModels();
                    await loadCurrentModel();
                }
            } catch (error) {
                console.error('Failed to delete variant:', error);
                message.error(`删除版本失败: ${error.response?.data?.error || error.message}`);
            }
        };

        const getRuntimeStatusTagType = (state) => {
            switch (state) {
                case 2: return 'success';
                case 3: return 'info';
                case 1: return 'warning';
                case 4: return 'warning';
                case 5: return 'error';
                case 0:
                default: return 'default';
            }
        };

        const getRuntimeStatusText = (statusObj) => {
            if (!statusObj) return '未加载';
            if (statusObj.stateName === 'Ready') return '常驻就绪';
            if (statusObj.stateName === 'Inferring') return '推理中';
            if (statusObj.stateName === 'Unloaded') return '未加载 / 休眠释放';
            if (statusObj.stateName === 'Loading') return '加载中...';
            if (statusObj.stateName === 'Faulted') return '故障异常';
            return statusObj.stateDescription || statusObj.stateName || '未加载';
        };

        const getRetentionText = (statusObj) => {
            if (!statusObj || !statusObj.retentionStrategy) return '未配置';
            if (statusObj.retentionStrategy === 'Permanent') return '常驻 (永不自动释放)';
            if (statusObj.retentionStrategy === 'SlidingExpiration') {
                const timeout = statusObj.idleTimeoutSeconds;
                return `滑动过期 (${timeout}s空闲释放${statusObj.unloadOnlyWhenMinimizedToTray ? '，仅托盘生效' : ''})`;
            }
            return statusObj.retentionStrategy;
        };
        const loadModelsPath = async () => {
            modelsPathLoading.value = true;
            try {
                const response = await settingsApi.get();
                const config = response.data;
                modelsPath.value = config.modelsRootPath || 'models';
                embeddingApiMode.value = config.embedding?.mode === 'openai';
                rerankApiMode.value = config.rerank?.mode === 'openai';
                chatApiMode.value = config.chat?.activeBackend === 'OpenAiApi' || (!config.chat?.activeBackend && !!config.chat?.endpoint);
            }
            catch (error) {
                console.error('Failed to load models path:', error);
                modelsPath.value = 'models';
            }
            finally {
                modelsPathLoading.value = false;
            }
        };
        const handleSaveModelsPath = async () => {
            savingModelsPath.value = true;
            try {
                const response = await settingsApi.updateModelsPath(modelsPath.value);
                const data = response.data;
                message.success(data.message || '模型路径已保存');
                // 直接使用返回的模型列表，避免额外的 API 调用
                // 智能合并模型列表：保留本地预定义模型元数据，只更新下载状态 // OPT-20260412-001: Issue-3 前端智能合并
                if (data.embeddingModels) {
                    const newModels = data.embeddingModels;
                    models.value = models.value.map(m => {
                        const updated = newModels.find(n => n.name === m.name);
                        if (updated) {
                            return { ...m, isDownloaded: updated.isDownloaded, localPath: updated.localPath };
                        }
                        return m;
                    });
                }
                if (data.rerankModels) {
                    const newRerankModels = data.rerankModels;
                    rerankModels.value = rerankModels.value.map(m => {
                        const updated = newRerankModels.find(n => n.name === m.name);
                        if (updated) {
                            return { ...m, isDownloaded: updated.isDownloaded, localPath: updated.localPath };
                        }
                        return m;
                    });
                }
                if (data.chatModels) {
                    const newChatModels = data.chatModels;
                    chatModels.value = chatModels.value.map(m => {
                        const updated = newChatModels.find(n => n.name === m.name);
                        if (updated) {
                            return { ...m, isDownloaded: updated.isDownloaded, localPath: updated.localPath };
                        }
                        return m;
                    });
                }
            }
            catch (error) {
                console.error('Failed to save models path:', error);
                message.error(`保存失败: ${error.response?.data?.error || error.message}`);
            }
            finally {
                savingModelsPath.value = false;
            }
        };
        const handleScanModels = async () => {
            await loadModels();
            await loadRerankModels();
            await loadChatModels();
            message.success('模型列表已刷新');
        };
        const customModelForm = ref({
            huggingFaceId: '',
            displayName: ''
        });
        // Rerank model state
        const rerankModels = ref([]);
        const currentRerankModel = ref(null);
        const rerankModelsLoading = ref(false);
        const rerankCurrentLoading = ref(false);
        let progressInterval = null;
        const selectedFormatFilter = ref('all');
        const activeDownloads = computed(() => {
            return Array.from(downloadProgress.value.values()).filter(d => d.status === 'downloading' || d.status === 'completed' || d.status === 'failed');
        });
        const displayOptions = computed(() => {
            if (!downloadOptions.value || !downloadOptions.value.allOptions)
                return [];
            if (selectedFormatFilter.value === 'gguf') {
                return (downloadOptions.value.ggufOptions && downloadOptions.value.ggufOptions.length > 0)
                    ? downloadOptions.value.ggufOptions
                    : downloadOptions.value.allOptions.filter(o => o.format === 'gguf');
            }
            if (selectedFormatFilter.value === 'onnx') {
                return (downloadOptions.value.onnxOptions && downloadOptions.value.onnxOptions.length > 0)
                    ? downloadOptions.value.onnxOptions
                    : downloadOptions.value.allOptions.filter(o => o.format === 'onnx');
            }
            return downloadOptions.value.allOptions;
        });
        const onFormatFilterChange = (filter) => {
            selectedFormatFilter.value = filter;
            const currentList = displayOptions.value;
            if (currentList.length > 0) {
                const found = currentList.find(o => o.path === selectedFile.value);
                if (!found) {
                    const rec = currentList.find(o => o.isRecommended) || currentList[0];
                    selectedFile.value = rec.path;
                }
            }
        };
        const selectedFileDescription = computed(() => {
            if (!selectedFile.value || !downloadOptions.value)
                return null;
            const option = downloadOptions.value.allOptions.find(o => o.path === selectedFile.value);
            return option?.description || null;
        });
        const getFormatTagType = (format) => {
            switch (format) {
                case 'embedded': return 'success';
                case 'external': return 'warning';
                case 'gguf': return 'info';
                default: return 'default';
            }
        };
        const getFormatText = (format) => {
            switch (format) {
                case 'embedded': return '嵌入式';
                case 'external': return '外部数据';
                case 'gguf': return 'GGUF (免转)';
                default: return format || '未知';
            }
        };
        // Rerank model table columns
        const rerankModelColumns = [
            {
                title: '模型名称',
                key: 'displayName',
                render(row) {
                    return h(NSpace, { align: 'center' }, {
                        default: () => [
                            h('span', row.displayName || row.name),
                            row.name === currentRerankModel.value?.name ? h(NTag, { type: 'success', size: 'small' }, { default: () => '当前' }) : null
                        ]
                    });
                }
            },
            {
                title: '描述',
                key: 'description',
                ellipsis: { tooltip: true }
            },
            {
                title: '维度',
                key: 'dimension',
                width: 70
            },
            {
                title: '大小',
                key: 'modelSizeBytes',
                width: 100,
                render(row) {
                    return row.modelSizeBytes ? formatBytes(row.modelSizeBytes) : '-';
                }
            },
            {
                title: '状态',
                key: 'isDownloaded',
                width: 90,
                render(row) {
                    return h(NTag, { type: row.isDownloaded ? 'success' : 'warning', size: 'small' }, {
                        default: () => row.isDownloaded ? '已下载' : '未下载'
                    });
                }
            },
            {
                title: '已安装变体',
                key: 'installedVariants',
                width: 250,
                render(row) {
                    if (!row.installedVariants || row.installedVariants.length === 0) {
                        return h('span', { style: 'color: #999; font-size: 12px' }, row.isDownloaded ? '单一默认版本' : '-');
                    }
                    const nodes = [];
                    for (const v of row.installedVariants) {
                        const isCurrentActive = v.isActive;
                        const label = v.quantization || v.format?.toUpperCase() || '版本';
                        const sizeStr = v.fileSizeBytes > 0 ? ` (${formatBytes(v.fileSizeBytes)})` : '';
                        if (isCurrentActive) {
                            nodes.push(h(NTag, {
                                type: 'success',
                                size: 'small',
                                style: 'margin-right: 4px; margin-bottom: 4px;'
                            }, {
                                default: () => `✓ ${label}${sizeStr}`
                            }));
                        } else {
                            nodes.push(h(NSpace, { inline: true, size: 2, style: 'margin-right: 4px; margin-bottom: 4px' }, {
                                default: () => [
                                    h(NButton, {
                                        size: 'tiny',
                                        secondary: true,
                                        type: 'default',
                                        onClick: () => handleSwitchVariant(row, v, true)
                                    }, {
                                        default: () => `${label}${sizeStr}`
                                    }),
                                    h(NPopconfirm, {
                                        onPositiveClick: () => handleDeleteVariant(row, v, true)
                                    }, {
                                        trigger: () => h(NButton, { size: 'tiny', text: true, type: 'error', style: 'padding: 0 2px' }, { default: () => '×' }),
                                        default: () => `确定删除变体文件 ${v.fileName}？此操作不可恢复。`
                                    })
                                ]
                            }));
                        }
                    }
                    if (row.isDownloaded) {
                        nodes.push(h(NButton, {
                            size: 'tiny',
                            ghost: true,
                            type: 'primary',
                            style: 'margin-bottom: 4px',
                            onClick: () => handleRerankDownload(row)
                        }, {
                            default: () => '+ 下载新变体'
                        }));
                    }
                    return h('div', { style: 'display: flex; flex-wrap: wrap; align-items: center;' }, nodes);
                }
            },
            {
                title: '路径',
                key: 'localPath',
                ellipsis: { tooltip: true },
                width: 200
            },
            {
                title: '操作',
                key: 'actions',
                width: 200,
                render(row) {
                    if (row.name === currentRerankModel.value?.name) {
                        return h(NTag, { type: 'default', size: 'small' }, { default: () => '使用中' });
                    }
                    const buttons = [];
                    if (!row.isDownloaded) {
                        const isDownloading = downloadProgress.value.get(`rerank_${row.name}`)?.status === 'downloading';
                        if (isDownloading) {
                            buttons.push(h(NButton, {
                                type: 'warning',
                                size: 'small',
                                ghost: true,
                                onClick: () => openDownloadModalForModel(row, true)
                            }, { default: () => '查看进度' }));
                        } else {
                            buttons.push(h(NButton, {
                                type: 'primary',
                                size: 'small',
                                loading: !!rowLoadingMap.value[row.name],
                                onClick: () => handleRerankDownload(row)
                            }, { default: () => '下载' }));
                        }
                    }
                    if (row.isDownloaded) {
                        if (row.name !== currentRerankModel.value?.name) {
                            buttons.push(h(NButton, {
                                type: 'primary',
                                size: 'small',
                                onClick: () => handleRerankSwitch(row)
                            }, { default: () => '切换' }));
                        }
                        // 删除按钮
                        buttons.push(h(NPopconfirm, {
                            onPositiveClick: () => handleRerankDelete(row)
                        }, {
                            trigger: () => h(NButton, { size: 'small', type: 'error' }, { default: () => '删除' }),
                            default: () => `确定删除重排模型 "${row.displayName || row.name}"？此操作不可恢复。`
                        }));
                    }
                    return h(NSpace, { size: 'small' }, { default: () => buttons });
                }
            }
        ];
        // Chat model table columns
        const chatModelColumns = [
            {
                title: '模型名称',
                key: 'displayName',
                render(row) {
                    return h(NSpace, { align: 'center' }, {
                        default: () => [
                            h('span', row.displayName || row.name),
                            row.name === currentChatModel.value?.name ? h(NTag, { type: 'success', size: 'small' }, { default: () => '当前' }) : null
                        ]
                    });
                }
            },
            {
                title: '描述',
                key: 'description',
                ellipsis: { tooltip: true }
            },
            {
                title: '上下文长度',
                key: 'maxSequenceLength',
                width: 110,
                render(row) {
                    return row.maxSequenceLength ? `${row.maxSequenceLength} tokens` : '-';
                }
            },
            {
                title: '大小',
                key: 'modelSizeBytes',
                width: 100,
                render(row) {
                    return row.modelSizeBytes ? formatBytes(row.modelSizeBytes) : '-';
                }
            },
            {
                title: '状态',
                key: 'isDownloaded',
                width: 90,
                render(row) {
                    const progress = downloadProgress.value.get(`chat_${row.name}`) || downloadProgress.value.get(row.name);
                    if (progress && progress.status === 'downloading') {
                        return h(NProgress, {
                            type: 'line',
                            percentage: progress.progress,
                            status: 'default',
                            style: 'width: 70px'
                        });
                    }
                    return h(NTag, { type: row.isDownloaded ? 'success' : 'warning', size: 'small' }, {
                        default: () => row.isDownloaded ? '已下载' : '未下载'
                    });
                }
            },
            {
                title: '已安装变体',
                key: 'installedVariants',
                width: 250,
                render(row) {
                    if (!row.installedVariants || row.installedVariants.length === 0) {
                        return h('span', { style: 'color: #999; font-size: 12px' }, row.isDownloaded ? '单一默认版本' : '-');
                    }
                    const nodes = [];
                    for (const v of row.installedVariants) {
                        const isCurrentActive = v.isActive;
                        const label = v.quantization || v.format?.toUpperCase() || '版本';
                        const sizeStr = v.fileSizeBytes > 0 ? ` (${formatBytes(v.fileSizeBytes)})` : '';
                        if (isCurrentActive) {
                            nodes.push(h(NTag, {
                                type: 'success',
                                size: 'small',
                                style: 'margin-right: 4px; margin-bottom: 4px;'
                            }, {
                                default: () => `✓ ${label}${sizeStr}`
                            }));
                        } else {
                            nodes.push(h(NSpace, { inline: true, size: 2, style: 'margin-right: 4px; margin-bottom: 4px' }, {
                                default: () => [
                                    h(NButton, {
                                        size: 'tiny',
                                        secondary: true,
                                        type: 'default',
                                        onClick: () => handleSwitchVariant(row, v, 'chat')
                                    }, {
                                        default: () => `${label}${sizeStr}`
                                    }),
                                    h(NPopconfirm, {
                                        onPositiveClick: () => handleDeleteVariant(row, v, 'chat')
                                    }, {
                                        trigger: () => h(NButton, { size: 'tiny', text: true, type: 'error', style: 'padding: 0 2px' }, { default: () => '×' }),
                                        default: () => `确定删除变体文件 ${v.fileName}？此操作不可恢复。`
                                    })
                                ]
                            }));
                        }
                    }
                    if (row.isDownloaded) {
                        nodes.push(h(NButton, {
                            size: 'tiny',
                            ghost: true,
                            type: 'primary',
                            style: 'margin-bottom: 4px',
                            onClick: () => handleChatDownload(row)
                        }, {
                            default: () => '+ 下载新变体'
                        }));
                    }
                    return h('div', { style: 'display: flex; flex-wrap: wrap; align-items: center;' }, nodes);
                }
            },
            {
                title: '路径',
                key: 'localPath',
                ellipsis: { tooltip: true },
                width: 200
            },
            {
                title: '操作',
                key: 'actions',
                width: 200,
                render(row) {
                    if (row.name === currentChatModel.value?.name) {
                        return h(NTag, { type: 'default', size: 'small' }, { default: () => '使用中' });
                    }
                    const buttons = [];
                    const isDownloading = downloadProgress.value.get(`chat_${row.name}`)?.status === 'downloading' || downloadProgress.value.get(row.name)?.status === 'downloading';
                    if (!row.isDownloaded) {
                        if (isDownloading) {
                            buttons.push(h(NButton, {
                                type: 'warning',
                                size: 'small',
                                ghost: true,
                                onClick: () => openDownloadModalForModel(row, 'chat')
                            }, { default: () => '查看进度' }));
                        } else {
                            buttons.push(h(NButton, {
                                type: 'primary',
                                size: 'small',
                                loading: !!rowLoadingMap.value[row.name],
                                onClick: () => handleChatDownload(row)
                            }, { default: () => '下载' }));
                        }
                    }
                    if (row.isDownloaded) {
                        if (row.name !== currentChatModel.value?.name) {
                            buttons.push(h(NButton, {
                                type: 'primary',
                                size: 'small',
                                onClick: () => handleChatSwitch(row)
                            }, { default: () => '切换' }));
                        }
                        // 删除按钮
                        buttons.push(h(NPopconfirm, {
                            onPositiveClick: () => handleChatDelete(row)
                        }, {
                            trigger: () => h(NButton, { size: 'small', type: 'error' }, { default: () => '删除' }),
                            default: () => `确定删除聊天模型 "${row.displayName || row.name}"？此操作不可恢复。`
                        }));
                    }
                    return h(NSpace, { size: 'small' }, { default: () => buttons });
                }
            }
        ];
        const modelColumns = [
            {
                title: '模型名称',
                key: 'displayName',
                render(row) {
                    return h(NSpace, { align: 'center' }, {
                        default: () => [
                            h('span', row.displayName || row.name),
                            row.name === currentModel.value?.name ? h(NTag, { type: 'success', size: 'small' }, { default: () => '当前' }) : null
                        ]
                    });
                }
            },
            {
                title: '描述',
                key: 'description',
                ellipsis: { tooltip: true }
            },
            {
                title: '维度',
                key: 'dimension',
                width: 70
            },
            {
                title: '格式',
                key: 'onnxFormat',
                width: 100,
                render(row) {
                    if (!row.isDownloaded)
                        return '-';
                    return h(NTag, { type: getFormatTagType(row.onnxFormat), size: 'small' }, {
                        default: () => getFormatText(row.onnxFormat)
                    });
                }
            },
            {
                title: '大小',
                key: 'modelSizeBytes',
                width: 100,
                render(row) {
                    return row.modelSizeBytes ? formatBytes(row.modelSizeBytes) : '-';
                }
            },
            {
                title: '状态',
                key: 'isDownloaded',
                width: 90,
                render(row) {
                    const progress = downloadProgress.value.get(row.name || '');
                    if (progress && progress.status === 'downloading') {
                        return h(NProgress, {
                            type: 'line',
                            percentage: progress.progress,
                            status: 'default',
                            style: 'width: 70px'
                        });
                    }
                    return h(NTag, { type: row.isDownloaded ? 'success' : 'warning', size: 'small' }, {
                        default: () => row.isDownloaded ? '已下载' : '未下载'
                    });
                }
            },
            {
                title: '已安装变体',
                key: 'installedVariants',
                width: 250,
                render(row) {
                    if (!row.installedVariants || row.installedVariants.length === 0) {
                        return h('span', { style: 'color: #999; font-size: 12px' }, row.isDownloaded ? '单一默认版本' : '-');
                    }
                    const nodes = [];
                    for (const v of row.installedVariants) {
                        const isCurrentActive = v.isActive;
                        const label = v.quantization || v.format?.toUpperCase() || '版本';
                        const sizeStr = v.fileSizeBytes > 0 ? ` (${formatBytes(v.fileSizeBytes)})` : '';
                        if (isCurrentActive) {
                            nodes.push(h(NTag, {
                                type: 'success',
                                size: 'small',
                                style: 'margin-right: 4px; margin-bottom: 4px;'
                            }, {
                                default: () => `✓ ${label}${sizeStr}`
                            }));
                        } else {
                            nodes.push(h(NSpace, { inline: true, size: 2, style: 'margin-right: 4px; margin-bottom: 4px' }, {
                                default: () => [
                                    h(NButton, {
                                        size: 'tiny',
                                        secondary: true,
                                        type: 'default',
                                        onClick: () => handleSwitchVariant(row, v, false)
                                    }, {
                                        default: () => `${label}${sizeStr}`
                                    }),
                                    h(NPopconfirm, {
                                        onPositiveClick: () => handleDeleteVariant(row, v, false)
                                    }, {
                                        trigger: () => h(NButton, { size: 'tiny', text: true, type: 'error', style: 'padding: 0 2px' }, { default: () => '×' }),
                                        default: () => `确定删除变体文件 ${v.fileName}？此操作不可恢复。`
                                    })
                                ]
                            }));
                        }
                    }
                    if (row.isDownloaded) {
                        nodes.push(h(NButton, {
                            size: 'tiny',
                            ghost: true,
                            type: 'primary',
                            style: 'margin-bottom: 4px',
                            onClick: () => handleDownload(row)
                        }, {
                            default: () => '+ 下载新变体'
                        }));
                    }
                    return h('div', { style: 'display: flex; flex-wrap: wrap; align-items: center;' }, nodes);
                }
            },
            {
                title: '操作',
                key: 'actions',
                width: 200,
                render(row) {
                    const progress = downloadProgress.value.get(row.name || '');
                    const isDownloading = progress && progress.status === 'downloading';
                    if (row.name === currentModel.value?.name) {
                        return h(NTag, { type: 'default', size: 'small' }, { default: () => '使用中' });
                    }
                    const buttons = [];
                    if (isDownloading) {
                        buttons.push(h(NButton, {
                            type: 'warning',
                            size: 'small',
                            ghost: true,
                            onClick: () => openDownloadModalForModel(row, false)
                        }, { default: () => '查看进度' }));
                    }
                    else if (!row.isDownloaded) {
                        buttons.push(h(NButton, {
                            type: 'primary',
                            size: 'small',
                            loading: !!rowLoadingMap.value[row.name],
                            onClick: () => handleDownload(row)
                        }, { default: () => '下载' }));
                    }
                    if (row.isDownloaded && !isDownloading) {
                        buttons.push(h(NButton, {
                            type: 'primary',
                            size: 'small',
                            onClick: () => handleSwitch(row)
                        }, { default: () => '切换' }));
                        // 格式转换按钮
                        buttons.push(h(NButton, {
                            size: 'small',
                            onClick: () => handleConvert(row)
                        }, { default: () => '转换' }));
                        // 删除按钮
                        buttons.push(h(NPopconfirm, {
                            onPositiveClick: () => handleDelete(row)
                        }, {
                            trigger: () => h(NButton, { size: 'small', type: 'error' }, { default: () => '删除' }),
                            default: () => `确定删除模型 "${row.displayName || row.name}"？此操作不可恢复。`
                        }));
                    }
                    return h(NSpace, { size: 'small' }, { default: () => buttons });
                }
            }
        ];
        const getModelDisplayName = (modelName) => {
            const model = models.value.find(m => m.name === modelName);
            return model?.displayName || modelName;
        };
        const getDownloadStatusType = (status) => {
            switch (status) {
                case 'downloading': return 'info';
                case 'completed': return 'success';
                case 'failed': return 'error';
                case 'cancelled': return 'warning';
                default: return 'default';
            }
        };
        const getDownloadStatusText = (status) => {
            switch (status) {
                case 'downloading': return '处理中';
                case 'completed': return '已完成';
                case 'failed': return '失败';
                case 'cancelled': return '已取消';
                default: return '空闲';
            }
        };
        const getProgressStatus = (status) => {
            switch (status) {
                case 'completed': return 'success';
                case 'failed': return 'error';
                case 'cancelled': return 'warning';
                default: return 'default';
            }
        };
        const getStageTagType = (stage) => {
            switch (stage) {
                case 1: case 'ResolvingManifest': return 'info';
                case 2: case 'ProbingMirrors': return 'warning';
                case 3: case 'DownloadingFiles': return 'primary';
                case 4: case 'VerifyingFiles': return 'info';
                case 5: case 'Converting': return 'warning';
                case 6: case 'Completed': return 'success';
                case 7: case 'Failed': return 'error';
                case 8: case 'Cancelled': return 'default';
                default: return 'default';
            }
        };
        const getStageText = (stage) => {
            switch (stage) {
                case 1: case 'ResolvingManifest': return '解析清单';
                case 2: case 'ProbingMirrors': return '探测镜像';
                case 3: case 'DownloadingFiles': return '正在下载';
                case 4: case 'VerifyingFiles': return '校验文件';
                case 5: case 'Converting': return '格式转换';
                case 6: case 'Completed': return '已完成';
                case 7: case 'Failed': return '失败';
                case 8: case 'Cancelled': return '已取消';
                default: return '准备中';
            }
        };
        const handleCancelDownload = async (download) => {
            const isChat = download.modelName?.startsWith('chat_') || download.isChat || download.modelType === 'chat';
            const isRerank = !isChat && (download.modelName?.startsWith('rerank_') || download.isRerank || download.modelType === 'reranker');
            const modelName = isChat ? download.modelName.replace('chat_', '') : (isRerank ? download.modelName.replace('rerank_', '') : download.modelName);
            try {
                if (isChat) {
                    await modelsApi.cancelChatDownload(modelName);
                } else if (isRerank) {
                    await modelsApi.cancelRerankDownload(modelName);
                } else {
                    await modelsApi.cancelDownload(modelName);
                }
                message.warning(`已取消模型 ${getModelDisplayName(modelName)} 的下载`);
            } catch (error) {
                console.error('Failed to cancel download:', error);
                if (error.response?.status === 404) {
                    message.info(`任务未在后台运行，已重置`);
                } else {
                    message.warning(`已提交取消请求`);
                }
            } finally {
                download.status = 'cancelled';
                download.stage = 'Cancelled';
                download.stageMessage = '下载已由用户取消';
            }
        };
        // 错误码对应的帮助信息
        const getErrorHelp = (errorCode) => {
            switch (errorCode) {
                case 3: return '文件下载不完整，请检查网络连接后重试。';
                case 4: return 'ONNX转换失败，请确保Python环境已正确配置。';
                case 5: return 'Python环境不可用，请安装Python 3.8+。';
                case 6: return 'Python依赖缺失，请运行: pip install torch transformers optimum onnx';
                case 7: return '模型过大不适合嵌入式格式，请使用"外部数据"格式重试。';
                case 8: return '网络错误，请检查网络连接后重试。';
                case 9: return '存储空间不足，请清理磁盘空间后重试。';
                case 10: return '模型属于 HuggingFace 协议限制/授权私有模型 (HTTP 401)。请前往【系统设置 - 模型仓库】配置有效的 HF_TOKEN。';
                default: return null;
            }
        };
        const formatBytes = (bytes) => {
            if (bytes === 0)
                return '0 B';
            const k = 1024;
            const sizes = ['B', 'KB', 'MB', 'GB'];
            const i = Math.floor(Math.log(bytes) / Math.log(k));
            return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
        };
        const loadModels = async () => {
            modelsLoading.value = true;
            try {
                const response = await modelsApi.getAll();
                models.value = response.data;
            }
            catch (error) {
                console.error('Failed to load models:', error);
                message.error('加载模型列表失败');
            }
            finally {
                modelsLoading.value = false;
            }
        };
        const loadCurrentModel = async () => {
            currentLoading.value = true;
            try {
                const response = await modelsApi.getCurrent();
                currentModel.value = response.data;
                loadRuntimeStatus();
            }
            catch (error) {
                console.error('Failed to load current model:', error);
            }
            finally {
                currentLoading.value = false;
            }
        };
        const formatSpeed = (speed) => {
            if (!speed || speed <= 0) return '-';
            return formatBytes(speed) + '/s';
        };

        const formatRemainingTime = (sec) => {
            if (sec == null || sec < 0) return '-';
            if (sec < 60) return `${Math.round(sec)} 秒`;
            const m = Math.floor(sec / 60);
            const s = Math.round(sec % 60);
            return `${m} 分 ${s} 秒`;
        };

        const modalTitle = computed(() => {
            const name = selectedModel.value?.displayName || selectedModel.value?.name || '';
            switch (downloadState.value) {
                case 'probing': return `探测模型镜像源 - ${name}`;
                case 'options_ready': return `选择模型格式与变体 - ${name}`;
                case 'downloading': return `正在下载模型 - ${name}`;
                case 'verifying': return `正在校验模型 - ${name}`;
                case 'converting': return `正在转换模型格式 - ${name}`;
                case 'completed': return `模型已就绪 - ${name}`;
                case 'failed': return `下载未完成 - ${name}`;
                case 'cancelling': return `正在取消下载 - ${name}`;
                default: return `模型下载 - ${name}`;
            }
        });

        const modalCurrentStepNumber = computed(() => {
            switch (downloadState.value) {
                case 'probing': return 1;
                case 'options_ready': return 2;
                case 'downloading': return 3;
                case 'verifying': case 'converting': case 'completed': return 4;
                default: return 1;
            }
        });

        const modalStepStatus = computed(() => {
            if (downloadState.value === 'failed') return 'error';
            if (downloadState.value === 'completed') return 'finish';
            return 'process';
        });

        const modalProgressStatus = computed(() => {
            if (downloadState.value === 'completed') return 'success';
            if (downloadState.value === 'failed') return 'error';
            if (downloadState.value === 'cancelling') return 'warning';
            return 'default';
        });

        const currentModalProgress = computed(() => {
            if (!selectedModel.value?.name) return null;
            const isChat = selectedModel.value.modelType === 'chat';
            const isRerank = !isChat && selectedModel.value.modelType === 'reranker';
            const key = isChat ? `chat_${selectedModel.value.name}` : (isRerank ? `rerank_${selectedModel.value.name}` : selectedModel.value.name);
            return downloadProgress.value.get(key) || (isChat ? downloadProgress.value.get(selectedModel.value.name) : null);
        });

        const openDownloadModalForModel = (model, modelTypeOrIsRerank = false) => {
            activeDownloadModel.value = model;
            selectedModel.value = model;
            const isChat = modelTypeOrIsRerank === 'chat' || model.modelType === 'chat';
            const isRerank = !isChat && (modelTypeOrIsRerank === true || model.modelType === 'reranker');
            const progressKey = isChat ? `chat_${model.name}` : (isRerank ? `rerank_${model.name}` : model.name);
            const current = downloadProgress.value.get(progressKey) || (isChat ? downloadProgress.value.get(model.name) : null);
            if (current) {
                if (current.status === 'completed') downloadState.value = 'completed';
                else if (current.status === 'failed') downloadState.value = 'failed';
                else if (current.stage === 'VerifyingFiles' || current.stage === 4) downloadState.value = 'verifying';
                else if (current.stage === 'Converting' || current.stage === 5) downloadState.value = 'converting';
                else downloadState.value = 'downloading';
            } else {
                downloadState.value = 'downloading';
            }
            showDownloadModal.value = true;
        };

        const openDownloadModalByProgressKey = (progressKey) => {
            const isChat = progressKey.startsWith('chat_');
            const isRerank = !isChat && progressKey.startsWith('rerank_');
            const modelName = isChat ? progressKey.replace('chat_', '') : (isRerank ? progressKey.replace('rerank_', '') : progressKey);
            const model = isChat
                ? chatModels.value.find(m => m.name === modelName)
                : (isRerank
                    ? rerankModels.value.find(m => m.name === modelName)
                    : models.value.find(m => m.name === modelName));
            const targetModel = model || { name: modelName, displayName: getModelDisplayName(modelName), modelType: isChat ? 'chat' : (isRerank ? 'reranker' : 'embedding') };
            openDownloadModalForModel(targetModel, isChat ? 'chat' : isRerank);
        };

        const startDownloadFlow = async (model, modelTypeOrIsRerank = false) => {
            if (!model || !model.name) return;
            const isChat = modelTypeOrIsRerank === 'chat' || model.modelType === 'chat';
            const isRerank = !isChat && (modelTypeOrIsRerank === true || model.modelType === 'reranker');
            const progressKey = isChat ? `chat_${model.name}` : (isRerank ? `rerank_${model.name}` : model.name);
            const existing = downloadProgress.value.get(progressKey) || (isChat ? downloadProgress.value.get(model.name) : null);
            if (existing && existing.status === 'downloading') {
                openDownloadModalForModel(model, isChat ? 'chat' : isRerank);
                return;
            }

            rowLoadingMap.value[model.name] = true;
            activeDownloadModel.value = model;
            selectedModel.value = model;
            selectedFile.value = null;
            downloadOptions.value = null;
            downloadState.value = 'probing';
            showDownloadModal.value = true;

            currentAbortController.value = new AbortController();

            try {
                const response = isChat
                    ? await modelsApi.getChatDownloadOptions(model.name, { signal: currentAbortController.value.signal })
                    : (isRerank
                        ? await modelsApi.getRerankDownloadOptions(model.name, { signal: currentAbortController.value.signal })
                        : await modelsApi.getDownloadOptions(model.name, { signal: currentAbortController.value.signal }));

                downloadOptions.value = response.data;
                if (downloadOptions.value?.hasBothFormats) {
                    selectedFormatFilter.value = 'all';
                } else if (downloadOptions.value?.hasGguf) {
                    selectedFormatFilter.value = 'gguf';
                } else {
                    selectedFormatFilter.value = 'onnx';
                }

                if (downloadOptions.value && !downloadOptions.value.needsUserSelection && downloadOptions.value.recommendedOption) {
                    selectedFile.value = downloadOptions.value.recommendedOption.path;
                } else if (downloadOptions.value && downloadOptions.value.allOptions.length > 0) {
                    const recommended = downloadOptions.value.allOptions.find(o => o.isRecommended);
                    selectedFile.value = recommended?.path || downloadOptions.value.allOptions[0].path;
                }

                downloadState.value = 'options_ready';
            } catch (error) {
                if (error.name === 'CanceledError' || error.name === 'AbortError') {
                    downloadState.value = 'idle';
                    showDownloadModal.value = false;
                } else {
                    console.error('Failed to get download options:', error);
                    message.error(`探测下载选项失败: ${error.response?.data?.error || error.message}`);
                    downloadState.value = 'failed';
                }
            } finally {
                rowLoadingMap.value[model.name] = false;
                currentAbortController.value = null;
            }
        };

        const handleDownload = (model) => startDownloadFlow(model, false);
        const handleRerankDownload = (model) => startDownloadFlow(model, true);
        const handleChatDownload = (model) => startDownloadFlow(model, 'chat');

        const openVariantSearch = (initialQuery = '', preferredFormat = 'all') => {
            variantSearchQuery.value = initialQuery || selectedModel.value?.displayName || selectedModel.value?.name || '';
            variantSearchFormat.value = preferredFormat || (downloadOptions.value?.hasGguf ? 'gguf' : 'all');
            variantSearchResults.value = [];
            selectedVariantDetail.value = null;
            showVariantSearchModal.value = true;
            if (variantSearchQuery.value) {
                handleSearchVariants();
            }
        };

        const handleSearchVariants = async () => {
            if (!variantSearchQuery.value || !variantSearchQuery.value.trim()) {
                message.warning('请输入搜索关键词');
                return;
            }
            variantSearching.value = true;
            selectedVariantDetail.value = null;
            try {
                const fmt = variantSearchFormat.value === 'all' ? undefined : variantSearchFormat.value;
                const res = await modelsApi.searchVariants(variantSearchQuery.value.trim(), fmt, 25);
                variantSearchResults.value = res.data || [];
                if (variantSearchResults.value.length === 0) {
                    message.info('未找到匹配的社区变种仓库');
                }
            } catch (err) {
                console.error('Failed to search variants:', err);
                message.error(`搜索变种失败: ${err.response?.data?.error || err.message}`);
            } finally {
                variantSearching.value = false;
            }
        };

        const inspectVariantRepo = async (repoId) => {
            loadingVariantDetail.value = true;
            try {
                const res = await modelsApi.getVariantDetail(repoId);
                selectedVariantDetail.value = res.data;
            } catch (err) {
                console.error('Failed to load variant detail:', err);
                message.error(`获取仓库详情失败: ${err.response?.data?.error || err.message}`);
            } finally {
                loadingVariantDetail.value = false;
            }
        };

        const selectVariantFileForDownload = (fileOption, repoId) => {
            if (!fileOption || !repoId) return;

            if (!selectedModel.value) {
                const existing = models.value.find(m => m.name === repoId || m.name === fileOption.fileName);
                if (existing) {
                    selectedModel.value = existing;
                } else {
                    selectedModel.value = {
                        name: repoId,
                        displayName: fileOption.fileName,
                        modelType: 'embedding',
                        isDownloaded: false
                    };
                }
            }

            if (!downloadOptions.value) {
                downloadOptions.value = { allOptions: [], ggufOptions: [], onnxOptions: [] };
            }
            if (!downloadOptions.value.allOptions) downloadOptions.value.allOptions = [];
            if (!downloadOptions.value.ggufOptions) downloadOptions.value.ggufOptions = [];
            if (!downloadOptions.value.onnxOptions) downloadOptions.value.onnxOptions = [];

            const opt = {
                path: fileOption.path,
                fileName: fileOption.fileName,
                displayName: fileOption.fileName,
                fileSize: fileOption.fileSize,
                size: fileOption.fileSize,
                format: fileOption.format,
                quantization: fileOption.quantization,
                repository: repoId,
                description: `来自社区源: ${repoId} (${fileOption.quantization || fileOption.format})`
            };

            const exists = downloadOptions.value.allOptions.find(o => o.path === opt.path && o.repository === repoId);
            if (!exists) {
                downloadOptions.value.allOptions.unshift(opt);
                if (opt.format === 'gguf') {
                    downloadOptions.value.ggufOptions.unshift(opt);
                    downloadOptions.value.hasGguf = true;
                } else {
                    downloadOptions.value.onnxOptions.unshift(opt);
                    downloadOptions.value.hasOnnx = true;
                }
            }

            selectedFile.value = opt.path;
            if (opt.format === 'gguf') {
                selectedFormatFilter.value = 'gguf';
            } else if (opt.format === 'onnx') {
                selectedFormatFilter.value = 'onnx';
            }

            downloadState.value = 'options_ready';
            showVariantSearchModal.value = false;
            showDownloadModal.value = true;
            message.success(`已选定变种: ${fileOption.fileName} (${repoId})`);
        };

        const confirmModalDownload = async () => {
            if (!selectedModel.value || !selectedModel.value.name) return;
            const isChat = selectedModel.value.modelType === 'chat';
            const isRerank = !isChat && selectedModel.value.modelType === 'reranker';
            const modelName = selectedModel.value.name;
            const progressKey = isChat ? `chat_${modelName}` : (isRerank ? `rerank_${modelName}` : modelName);

            downloadState.value = 'downloading';
            message.info(`开始下载模型: ${selectedModel.value.displayName || modelName}`);

            try {
                const selectedOpt = downloadOptions.value?.allOptions?.find(o => o.path === selectedFile.value);
                const format = selectedOpt?.format;
                const repository = selectedOpt?.repository;

                downloadProgress.value.set(progressKey, {
                    modelName: modelName,
                    status: 'downloading',
                    stage: 1,
                    stageMessage: '正在连接模型源解析清单...',
                    progress: 0,
                    bytesReceived: 0,
                    totalBytes: selectedModel.value.modelSizeBytes || 0,
                    speedBytesPerSecond: 0,
                    estimatedSecondsRemaining: null
                });
                startProgressPolling();

                let response;
                if (isChat) {
                    response = await modelsApi.downloadChatModel(modelName, selectedFile.value || undefined, format, repository);
                } else if (isRerank) {
                    response = await modelsApi.downloadRerankModel(modelName, selectedFile.value || undefined, format, repository);
                } else {
                    response = await modelsApi.download(modelName, selectedFile.value || undefined, format, repository);
                }

                if (response?.data?.isAlreadyDownloaded) {
                    message.info(`模型或所选变体已存在: ${selectedModel.value.displayName || modelName}`);
                    downloadProgress.value.delete(progressKey);
                    downloadState.value = 'completed';
                    if (isChat) {
                        await loadChatModels();
                    } else if (isRerank) {
                        await loadRerankModels();
                    } else {
                        await loadModels();
                    }
                }
            } catch (error) {
                console.error('Failed to start download:', error);
                message.error(`下载启动失败: ${error.response?.data?.error || error.message}`);
                downloadState.value = 'failed';
            }
        };

        const cancelModalDownload = async () => {
            if (downloadState.value === 'probing') {
                if (currentAbortController.value) {
                    currentAbortController.value.abort();
                }
                downloadState.value = 'idle';
                showDownloadModal.value = false;
                message.info('已取消探测');
                return;
            }

            if (!selectedModel.value) {
                showDownloadModal.value = false;
                return;
            }

            const isChat = selectedModel.value.modelType === 'chat';
            const isRerank = !isChat && selectedModel.value.modelType === 'reranker';
            const modelName = selectedModel.value.name;
            const progressKey = isChat ? `chat_${modelName}` : (isRerank ? `rerank_${modelName}` : modelName);

            downloadState.value = 'cancelling';
            try {
                if (isChat) {
                    await modelsApi.cancelChatDownload(modelName);
                } else if (isRerank) {
                    await modelsApi.cancelRerankDownload(modelName);
                } else {
                    await modelsApi.cancelDownload(modelName);
                }
                message.warning(`已取消模型 ${getModelDisplayName(modelName)} 的下载`);
            } catch (error) {
                console.error('Failed to cancel download:', error);
                if (error.response?.status === 404) {
                    message.info(`任务未在后台运行，已重置`);
                } else {
                    message.warning(`已提交取消请求`);
                }
            } finally {
                const item = downloadProgress.value.get(progressKey);
                if (item) {
                    item.status = 'cancelled';
                    item.stage = 'Cancelled';
                    item.stageMessage = '下载已由用户取消';
                }
                downloadState.value = 'cancelled';
                setTimeout(() => {
                    if (showDownloadModal.value && (downloadState.value === 'cancelled' || downloadState.value === 'cancelling')) {
                        showDownloadModal.value = false;
                        downloadState.value = 'idle';
                    }
                }, 600);
            }
        };

        const retryDownload = () => {
            if (selectedModel.value) {
                const isChat = selectedModel.value.modelType === 'chat';
                const isRerank = !isChat && selectedModel.value.modelType === 'reranker';
                startDownloadFlow(selectedModel.value, isChat ? 'chat' : isRerank);
            }
        };

        const handleSwitchFromModal = async () => {
            if (!selectedModel.value) return;
            showDownloadModal.value = false;
            const isChat = selectedModel.value.modelType === 'chat';
            const isRerank = !isChat && selectedModel.value.modelType === 'reranker';
            if (isChat) {
                await handleChatSwitch(selectedModel.value);
            } else if (isRerank) {
                await handleRerankSwitch(selectedModel.value);
            } else {
                handleSwitch(selectedModel.value);
            }
        };
        const handleSwitch = (model) => {
            selectedModel.value = model;
            deleteOldVectors.value = false;
            showSwitchDialog.value = true;
        };
        const confirmSwitch = async () => {
            if (!selectedModel.value || !selectedModel.value.name)
                return;
            switching.value = true;
            try {
                const response = await modelsApi.switch(selectedModel.value.name, deleteOldVectors.value);
                message.success(response.data.message || `已切换到 ${selectedModel.value.displayName}`);
                showSwitchDialog.value = false;
                await loadCurrentModel();
                await loadModels();
            }
            catch (error) {
                console.error('Failed to switch model:', error);
                message.error(`切换失败: ${error.response?.data?.error || error.message}`);
            }
            finally {
                switching.value = false;
            }
        };
        const handleConvert = (model) => {
            selectedModel.value = model;
            targetFormat.value = model.onnxFormat === 'embedded' ? 'external' : 'embedded';
            showConvertDialog.value = true;
        };
        const confirmConvert = async () => {
            if (!selectedModel.value || !selectedModel.value.name)
                return;
            converting.value = true;
            try {
                await modelsApi.convert(selectedModel.value.name, targetFormat.value);
                downloadProgress.value.set(`convert_${selectedModel.value.name}`, {
                    modelName: selectedModel.value.name,
                    status: 'downloading',
                    progress: 0,
                    bytesReceived: 0,
                    totalBytes: 0,
                    speedBytesPerSecond: 0,
                    estimatedSecondsRemaining: null
                });
                showConvertDialog.value = false;
                message.info(`开始转换模型格式: ${selectedModel.value.displayName}`);
                // Poll for convert progress
                const pollConvertProgress = async () => {
                    try {
                        const response = await modelsApi.getConvertProgress(selectedModel.value.name);
                        const progress = response.data;
                        downloadProgress.value.set(`convert_${selectedModel.value.name}`, progress);
                        if (progress.status === 'completed') {
                            message.success('模型格式转换完成');
                            await loadModels();
                            await loadCurrentModel();
                        }
                        else if (progress.status === 'failed') {
                            message.error(`转换失败: ${progress.errorMessage}`);
                        }
                        else {
                            setTimeout(pollConvertProgress, 2000);
                        }
                    }
                    catch (error) {
                        console.error('Failed to get convert progress:', error);
                    }
                };
                setTimeout(pollConvertProgress, 2000);
            }
            catch (error) {
                console.error('Failed to start convert:', error);
                message.error(`转换失败: ${error.response?.data?.error || error.message}`);
            }
            finally {
                converting.value = false;
            }
        };
        const handleDelete = async (model) => {
            if (!model.name)
                return;
            deleting.value = true;
            try {
                await modelsApi.delete(model.name);
                message.success(`已删除模型: ${model.displayName || model.name}`);
                await loadModels();
            }
            catch (error) {
                console.error('Failed to delete model:', error);
                message.error(`删除失败: ${error.response?.data?.error || error.message}`);
            }
            finally {
                deleting.value = false;
            }
        };
        const confirmDelete = async () => {
            if (!selectedModel.value)
                return;
            await handleDelete(selectedModel.value);
            showDeleteDialog.value = false;
        };
        const confirmAddCustom = async () => {
            if (!customModelForm.value.huggingFaceId) {
                message.error('请输入 HuggingFace 模型 ID');
                return;
            }
            addingCustom.value = true;
            try {
                const response = await modelsApi.addCustom(customModelForm.value.huggingFaceId, customModelForm.value.displayName || undefined);
                message.success(response.data.message || '已添加自定义模型');
                showAddCustomDialog.value = false;
                customModelForm.value = { huggingFaceId: '', displayName: '' };
                await loadModels();
            }
            catch (error) {
                console.error('Failed to add custom model:', error);
                message.error(`添加失败: ${error.response?.data?.error || error.message}`);
            }
            finally {
                addingCustom.value = false;
            }
        };
        // ============== Rerank Model Functions ==============
        const loadRerankModels = async () => {
            rerankModelsLoading.value = true;
            try {
                const response = await modelsApi.getRerankModels();
                rerankModels.value = response.data;
            }
            catch (error) {
                console.error('Failed to load rerank models:', error);
                message.error('加载重排模型列表失败');
            }
            finally {
                rerankModelsLoading.value = false;
            }
        };
        const loadCurrentRerankModel = async () => {
            rerankCurrentLoading.value = true;
            try {
                const response = await modelsApi.getCurrentRerankModel();
                currentRerankModel.value = response.data;
                loadRuntimeStatus();
            }
            catch (error) {
                console.error('Failed to load current rerank model:', error);
            }
            finally {
                rerankCurrentLoading.value = false;
            }
        };
        const handleRerankSwitch = async (model) => {
            if (!model.name)
                return;
            try {
                const response = await modelsApi.switchRerankModel(model.name);
                message.success(response.data.message || `已切换到重排模型: ${model.displayName || model.name}`);
                await loadCurrentRerankModel();
                await loadRerankModels();
                await loadRuntimeStatus();
            }
            catch (error) {
                console.error('Failed to switch rerank model:', error);
                message.error(`切换失败: ${error.response?.data?.error || error.message}`);
            }
        };
        const handleRerankDelete = async (model) => {
            if (!model.name)
                return;
            try {
                await modelsApi.deleteRerankModel(model.name);
                message.success(`已删除重排模型: ${model.displayName || model.name}`);
                await loadRerankModels();
                await loadRuntimeStatus();
            }
            catch (error) {
                console.error('Failed to delete rerank model:', error);
                message.error(`删除失败: ${error.response?.data?.error || error.message}`);
            }
        };
        // ============== Chat Model Functions ==============
        const loadChatModels = async () => {
            chatModelsLoading.value = true;
            try {
                const response = await modelsApi.getChatModels();
                chatModels.value = response.data;
            }
            catch (error) {
                console.error('Failed to load chat models:', error);
                message.error('加载聊天模型列表失败');
            }
            finally {
                chatModelsLoading.value = false;
            }
        };
        const loadCurrentChatModel = async () => {
            chatCurrentLoading.value = true;
            try {
                const response = await modelsApi.getCurrentChatModel();
                currentChatModel.value = response.data;
                loadRuntimeStatus();
            }
            catch (error) {
                console.error('Failed to load current chat model:', error);
            }
            finally {
                chatCurrentLoading.value = false;
            }
        };
        const handleChatSwitch = async (model) => {
            if (!model.name)
                return;
            try {
                const response = await modelsApi.switchChatModel(model.name);
                message.success(response.data.message || `已切换到聊天模型: ${model.displayName || model.name}`);
                await loadCurrentChatModel();
                await loadChatModels();
                await loadRuntimeStatus();
            }
            catch (error) {
                console.error('Failed to switch chat model:', error);
                message.error(`切换失败: ${error.response?.data?.error || error.message}`);
            }
        };
        const handleChatDelete = async (model) => {
            if (!model.name)
                return;
            try {
                await modelsApi.deleteChatModel(model.name);
                message.success(`已删除聊天模型: ${model.displayName || model.name}`);
                await loadChatModels();
                await loadRuntimeStatus();
            }
            catch (error) {
                console.error('Failed to delete chat model:', error);
                message.error(`删除失败: ${error.response?.data?.error || error.message}`);
            }
        };
        // ============== Progress Polling ==============
        const startProgressPolling = () => {
            if (progressInterval)
                return;
            progressInterval = window.setInterval(async () => {
                const downloadingModels = Array.from(downloadProgress.value.keys())
                    .filter(name => {
                    const p = downloadProgress.value.get(name);
                    return p && (p.status === 'downloading');
                });
                if (downloadingModels.length === 0) {
                    stopProgressPolling();
                    return;
                }
                for (const progressKey of downloadingModels) {
                    try {
                        // 判断是聊天、重排模型还是嵌入模型
                        const isChat = progressKey.startsWith('chat_');
                        const isRerank = !isChat && progressKey.startsWith('rerank_');
                        const modelName = isChat ? progressKey.replace('chat_', '') : (isRerank ? progressKey.replace('rerank_', '') : progressKey);
                        // 使用正确的 API 获取进度
                        const response = isChat
                            ? await modelsApi.getChatDownloadProgress(modelName)
                            : (isRerank
                                ? await modelsApi.getRerankDownloadProgress(modelName)
                                : await modelsApi.getDownloadProgress(modelName));
                        downloadProgress.value.set(progressKey, response.data);
                        if (selectedModel.value && (selectedModel.value.name === modelName)) {
                            if (response.data.status === 'completed') downloadState.value = 'completed';
                            else if (response.data.status === 'failed') downloadState.value = 'failed';
                            else if (response.data.status === 'cancelled') downloadState.value = 'cancelled';
                            else if (response.data.stage === 'VerifyingFiles' || response.data.stage === 4) downloadState.value = 'verifying';
                            else if (response.data.stage === 'Converting' || response.data.stage === 5) downloadState.value = 'converting';
                            else if (response.data.status === 'downloading' && downloadState.value !== 'cancelling') downloadState.value = 'downloading';
                            else if (response.data.status === 'idle') {
                                downloadProgress.value.delete(progressKey);
                                downloadState.value = 'idle';
                            }
                        }
                        if (response.data.status === 'completed') {
                            message.success(`模型 ${getModelDisplayName(modelName)} 下载完成`);
                            // 刷新对应的模型列表
                            if (isChat) {
                                await loadChatModels();
                                await loadCurrentChatModel();
                            }
                            else if (isRerank) {
                                await loadRerankModels();
                                await loadCurrentRerankModel();
                            }
                            else {
                                await loadModels();
                                await loadCurrentModel();
                            }
                            await loadRuntimeStatus();
                        }
                        else if (response.data.status === 'failed') {
                            message.error(`模型 ${getModelDisplayName(modelName)} 下载失败`);
                            if (isChat) {
                                await loadChatModels();
                            }
                            else if (isRerank) {
                                await loadRerankModels();
                            }
                            else {
                                await loadModels();
                            }
                        }
                    }
                    catch (error) {
                        console.error('Failed to get progress:', error);
                    }
                }
            }, 2000);
        };
        const stopProgressPolling = () => {
            if (progressInterval) {
                clearInterval(progressInterval);
                progressInterval = null;
            }
        };
        onMounted(async () => {
            loadModelsPath();
            loadModels();
            loadCurrentModel();
            loadRerankModels();
            loadCurrentRerankModel();
            loadChatModels();
            loadCurrentChatModel();
            loadRuntimeStatus();
            // 恢复页面导航前正在进行的下载（前端内存被清空，从后端重建）
            try {
                const res = await modelsApi.getActiveDownloads();
                if (res.data.length > 0) {
                    for (const item of res.data) {
                        downloadProgress.value.set(item.key, item.progress);
                    }
                    startProgressPolling();
                }
            }
            catch {
                // 忽略恢复失败，不影响正常使用
            }
            window.addEventListener('storage', handleStorageChange);
        });
        onActivated(async () => {
            loadCurrentModel();
            loadCurrentRerankModel();
            loadCurrentChatModel();
            loadRuntimeStatus();
        });
        const handleStorageChange = (e) => {
            if (e.key === 'modelsPathChanged') {
                loadModels();
                loadRerankModels();
                loadChatModels();
                loadModelsPath();
                loadRuntimeStatus();
            }
        };
        onUnmounted(() => {
            stopProgressPolling();
            window.removeEventListener('storage', handleStorageChange);
        });
        const __returned__ = { activeModelTab, message, modelsPath, modelsPathLoading, savingModelsPath, embeddingApiMode, rerankApiMode, chatApiMode, models, currentModel, modelsLoading, currentLoading, chatModels, currentChatModel, chatModelsLoading, chatCurrentLoading, chatModelColumns, runtimeStatus, runtimeStatusLoading, loadRuntimeStatus, switchVariantDialog, handleSwitchVariant, confirmSwitchVariant, handleDeleteVariant, getRuntimeStatusTagType, getRuntimeStatusText, getRetentionText, showSwitchDialog, showDownloadDialog, showDownloadModal, downloadState, activeDownloadModel, rowLoadingMap, modalTitle, modalCurrentStepNumber, modalStepStatus, modalProgressStatus, currentModalProgress, formatSpeed, formatRemainingTime, openDownloadModalForModel, openDownloadModalByProgressKey, confirmModalDownload, cancelModalDownload, retryDownload, handleSwitchFromModal, showVariantSearchModal, variantSearchQuery, variantSearchFormat, variantSearchResults, variantSearching, selectedVariantDetail, loadingVariantDetail, openVariantSearch, handleSearchVariants, inspectVariantRepo, selectVariantFileForDownload, showConvertDialog, showAddCustomDialog, showDeleteDialog, selectedModel, switching, converting, deleting, addingCustom, targetFormat, deleteOldVectors, downloadProgress, downloadOptions, selectedFile, loadingOptions, selectedFormatFilter, displayOptions, onFormatFilterChange, loadModelsPath, handleSaveModelsPath, handleScanModels, customModelForm, rerankModels, currentRerankModel, rerankModelsLoading, rerankCurrentLoading, get progressInterval() { return progressInterval; }, set progressInterval(v) { progressInterval = v; }, activeDownloads, selectedFileDescription, getFormatTagType, getFormatText, rerankModelColumns, modelColumns, getModelDisplayName, getDownloadStatusType, getDownloadStatusText, getProgressStatus, getStageTagType, getStageText, handleCancelDownload, getErrorHelp, formatBytes, loadModels, loadCurrentModel, handleDownload, handleSwitch, confirmSwitch, handleConvert, confirmConvert, handleDelete, confirmDelete, confirmAddCustom, loadRerankModels, loadCurrentRerankModel, handleRerankDownload, handleRerankSwitch, handleRerankDelete, loadChatModels, loadCurrentChatModel, handleChatDownload, handleChatSwitch, handleChatDelete, startProgressPolling, stopProgressPolling, get NTag() { return NTag; }, get NButton() { return NButton; }, get NSpace() { return NSpace; }, get NProgress() { return NProgress; }, get NAlert() { return NAlert; }, get NCode() { return NCode; }, get NIcon() { return NIcon; }, get NRadioGroup() { return NRadioGroup; }, get NRadio() { return NRadio; }, get NFormItem() { return NFormItem; }, get NSpin() { return NSpin; }, get NSteps() { return NSteps; }, get NStep() { return NStep; }, get RefreshOutline() { return RefreshOutline; }, get AddOutline() { return AddOutline; }, get InformationCircleOutline() { return InformationCircleOutline; }, get SearchOutline() { return SearchOutline; } };
        return __returned__;
    }
});

component.template = "\n  <n-space vertical :size=\"16\">\n    <!-- Model Root Directory -->\n    <n-card title=\"模型根目录\">\n      <n-form label-placement=\"left\" label-width=\"120\">\n        <n-form-item label=\"模型存储路径\">\n          <n-space align=\"center\" style=\"width: 100%\">\n            <n-input\n              v-model:value=\"modelsPath\"\n              placeholder=\"默认: models\"\n              style=\"width: 300px\"\n            />\n            <n-button\n              type=\"primary\"\n              :loading=\"savingModelsPath\"\n              @click=\"handleSaveModelsPath\"\n            >\n              保存\n            </n-button>\n            <n-button\n              @click=\"handleScanModels\"\n              :loading=\"modelsPathLoading\"\n            >\n              扫描模型目录\n            </n-button>\n          </n-space>\n        </n-form-item>\n      </n-form>\n      <n-text depth=\"3\" style=\"font-size: 12px\">\n        设置模型文件的存储根路径，修改后需要重新扫描以更新模型列表\n      </n-text>\n    </n-card>\n\n    <!-- Download/Convert Progress -->\n    <n-card v-if=\"activeDownloads.length > 0\" title=\"任务进度\">\n      <n-list>\n        <n-list-item v-for=\"download in activeDownloads\" :key=\"download.modelName\">\n          <n-thing>\n            <template #header>\n              <n-space align=\"center\" justify=\"space-between\" style=\"width: 100%\">\n                <n-space align=\"center\">\n                  <span style=\"font-weight: 500\">{{ getModelDisplayName(download.modelName) }}</span>\n                  <n-tag :type=\"getDownloadStatusType(download.status)\" size=\"small\">\n                    {{ getDownloadStatusText(download.status) }}\n                  </n-tag>\n                  <n-tag v-if=\"download.stage\" :type=\"getStageTagType(download.stage)\" size=\"small\">\n                    {{ getStageText(download.stage) }}\n                  </n-tag>\n                </n-space>\n                <n-space align=\"center\">\n                  <n-button \n                    size=\"tiny\" \n                    type=\"info\" \n                    ghost \n                    @click=\"openDownloadModalByProgressKey(download.modelName)\">\n                    展开监控\n                  </n-button>\n                  <n-button \n                    v-if=\"download.status === 'downloading'\" \n                    size=\"tiny\" \n                    type=\"warning\" \n                    ghost \n                    @click=\"handleCancelDownload(download)\">\n                    取消下载\n                  </n-button>\n                </n-space>\n              </n-space>\n            </template>\n            \n            <!-- Progress bar -->\n            <n-progress \n              type=\"line\" \n              :percentage=\"download.progress\" \n              :status=\"getProgressStatus(download.status)\"\n              :indicator-placement=\"'inside'\"\n              style=\"margin: 12px 0\"\n            />\n            \n            <!-- Stage message & file details -->\n            <div v-if=\"download.stageMessage\" style=\"font-size: 12px; margin-bottom: 6px\">\n              <n-text depth=\"3\">{{ download.stageMessage }}</n-text>\n            </div>\n            \n            <!-- Error message -->\n            <n-alert v-if=\"download.status === 'failed' && download.errorMessage\" type=\"error\" style=\"margin-top: 8px\">\n              <template #header>操作失败</template>\n              <n-text>{{ download.errorMessage }}</n-text>\n              <n-text v-if=\"getErrorHelp(download.errorCode)\" depth=\"3\" style=\"display: block; margin-top: 8px; font-size: 12px\">\n                <n-icon :component=\"InformationCircleOutline\" style=\"vertical-align: middle; margin-right: 4px\" />\n                {{ getErrorHelp(download.errorCode) }}\n              </n-text>\n            </n-alert>\n          </n-thing>\n        </n-list-item>\n      </n-list>\n    </n-card>\n\n    <!-- Model Category Tab Navigation -->\n    <n-tabs v-model:value=\"activeModelTab\" type=\"segment\" size=\"large\" style=\"margin-bottom: 4px\">\n      <n-tab-pane name=\"embedding\">\n        <template #tab>\n          <n-space align=\"center\" :size=\"8\">\n            <span style=\"font-weight: 600\">嵌入模型 (Embedding)</span>\n            <n-tag size=\"small\" round :bordered=\"false\" type=\"info\">{{ models.length }}</n-tag>\n          </n-space>\n        </template>\n        <n-space vertical :size=\"16\" style=\"margin-top: 12px\">\n          <!-- Current Model -->\n    <n-card title=\"当前嵌入模型\">\n      <template #header-extra>\n        <n-space align=\"center\">\n          <n-tag v-if=\"runtimeStatus?.embedding\" :type=\"getRuntimeStatusTagType(runtimeStatus.embedding.state)\" size=\"small\" round>\n            {{ getRuntimeStatusText(runtimeStatus.embedding) }}\n            <span v-if=\"runtimeStatus.embedding.activeLeases > 0\"> [{{ runtimeStatus.embedding.activeLeases }} 借用]</span>\n          </n-tag>\n          <n-tag v-if=\"runtimeStatus?.embedding?.isGpuMode\" type=\"success\" size=\"small\" round>\n            🚀 {{ runtimeStatus.embedding.executionDevice || 'GPU' }}\n          </n-tag>\n          <n-tag v-else-if=\"runtimeStatus?.embedding\" type=\"default\" size=\"small\" round>\n            💻 CPU\n          </n-tag>\n          <n-button text size=\"small\" @click=\"loadRuntimeStatus\" :loading=\"runtimeStatusLoading\">\n            <template #icon><n-icon :component=\"RefreshOutline\" /></template>\n            刷新状态\n          </n-button>\n        </n-space>\n      </template>\n      <n-spin :show=\"currentLoading || runtimeStatusLoading\">\n        <n-alert v-if=\"embeddingApiMode\" type=\"info\" :show-icon=\"false\" style=\"margin-bottom: 12px\">\n          当前为 <strong>OpenAI 兼容 API 模式</strong>，本地模型管理不适用。\n          请在「设置 → 嵌入模型」中配置 API 地址和模型名称。\n        </n-alert>\n        <n-descriptions v-if=\"currentModel && !embeddingApiMode\" :column=\"4\" label-placement=\"left\">\n          <n-descriptions-item label=\"模型名称\">\n            <n-tag type=\"primary\">{{ currentModel.displayName || currentModel.name }}</n-tag>\n          </n-descriptions-item>\n          <n-descriptions-item label=\"当前激活变体\">\n            <n-tag type=\"info\" size=\"small\">\n              {{ runtimeStatus?.embedding?.activeVariant || currentModel.activeVariantFileName || '-' }}\n            </n-tag>\n          </n-descriptions-item>\n          <n-descriptions-item label=\"量化与格式\">\n            <n-space :size=\"4\">\n              <n-tag v-if=\"runtimeStatus?.embedding?.quantization\" size=\"small\">\n                {{ runtimeStatus.embedding.quantization }}\n              </n-tag>\n              <n-tag :type=\"getFormatTagType(currentModel.onnxFormat)\" size=\"small\">\n                {{ getFormatText(currentModel.onnxFormat) }}\n              </n-tag>\n            </n-space>\n          </n-descriptions-item>\n          <n-descriptions-item label=\"规格\">\n            {{ currentModel.dimension }} 维 / {{ currentModel.maxSequenceLength }} tokens\n          </n-descriptions-item>\n          <n-descriptions-item label=\"驻留策略\" :span=\"2\">\n            {{ getRetentionText(runtimeStatus?.embedding) }}\n          </n-descriptions-item>\n          <n-descriptions-item label=\"硬件加速\" :span=\"2\">\n            {{ runtimeStatus?.embedding?.offloadDescription || (runtimeStatus?.embedding?.isGpuMode ? 'GPU 模式' : '纯 CPU 模式') }}\n            <span v-if=\"runtimeStatus?.embedding?.totalVramGb\" style=\"color: #888; font-size: 11px; margin-left: 6px\">\n              (显存: {{ runtimeStatus.embedding.totalVramGb }} GB)\n            </span>\n          </n-descriptions-item>\n        </n-descriptions>\n        <n-text v-else depth=\"3\">未配置模型</n-text>\n      </n-spin>\n    </n-card>\n          <!-- Available Models -->\n    <n-card title=\"可用模型\">\n      <template #header-extra>\n        <n-space>\n          <n-button text @click=\"openVariantSearch('')\">\n            <template #icon><n-icon :component=\"SearchOutline\" /></template>\n            搜索社区变体\n          </n-button>\n          <n-button text @click=\"showAddCustomDialog = true\">\n            <template #icon><n-icon :component=\"AddOutline\" /></template>\n            添加模型\n          </n-button>\n          <n-button text @click=\"loadModels\">\n            <template #icon><n-icon :component=\"RefreshOutline\" /></template>\n            刷新\n          </n-button>\n        </n-space>\n      </template>\n      \n      <!-- Info alert -->\n      <n-alert type=\"info\" style=\"margin-bottom: 16px\">\n        <template #header>提示</template>\n        <n-text>\n          部分模型需要从 PyTorch 格式转换为 ONNX 格式，转换过程需要 Python 环境。<br>\n          支持同一个模型目录下载多个 GGUF 量化版本 (如 Q4_K_M / Q8_0) 并即时切换使用。\n        </n-text>\n      </n-alert>\n      \n      <n-data-table\n        :columns=\"modelColumns\"\n        :data=\"models\"\n        :loading=\"modelsLoading\"\n        :row-key=\"(row) => row.name || ''\"\n      />\n    </n-card>\n        </n-space>\n      </n-tab-pane>\n\n      <n-tab-pane name=\"rerank\">\n        <template #tab>\n          <n-space align=\"center\" :size=\"8\">\n            <span style=\"font-weight: 600\">重排模型 (Rerank)</span>\n            <n-tag size=\"small\" round :bordered=\"false\" type=\"warning\">{{ rerankModels.length }}</n-tag>\n          </n-space>\n        </template>\n        <n-space vertical :size=\"16\" style=\"margin-top: 12px\">\n          <!-- Current Rerank Model -->\n    <n-card title=\"当前重排模型\">\n      <template #header-extra>\n        <n-space align=\"center\">\n          <n-tag v-if=\"runtimeStatus?.rerank\" :type=\"getRuntimeStatusTagType(runtimeStatus.rerank.state)\" size=\"small\" round>\n            {{ getRuntimeStatusText(runtimeStatus.rerank) }}\n            <span v-if=\"runtimeStatus.rerank.activeLeases > 0\"> [{{ runtimeStatus.rerank.activeLeases }} 借用]</span>\n          </n-tag>\n          <n-tag v-if=\"runtimeStatus?.rerank?.isGpuMode\" type=\"success\" size=\"small\" round>\n            🚀 {{ runtimeStatus.rerank.executionDevice || 'GPU' }}\n          </n-tag>\n          <n-tag v-else-if=\"runtimeStatus?.rerank\" type=\"default\" size=\"small\" round>\n            💻 CPU\n          </n-tag>\n          <n-button text size=\"small\" @click=\"loadRuntimeStatus\" :loading=\"runtimeStatusLoading\">\n            <template #icon><n-icon :component=\"RefreshOutline\" /></template>\n            刷新状态\n          </n-button>\n        </n-space>\n      </template>\n      <n-spin :show=\"rerankCurrentLoading || runtimeStatusLoading\">\n        <n-alert v-if=\"rerankApiMode\" type=\"info\" :show-icon=\"false\" style=\"margin-bottom: 12px\">\n          当前为 <strong>OpenAI 兼容 API 模式</strong>，本地重排模型管理不适用。\n          请在「设置 → 重排模型」中配置 API 地址和模型名称。\n        </n-alert>\n        <n-descriptions v-if=\"currentRerankModel && !rerankApiMode\" :column=\"4\" label-placement=\"left\">\n          <n-descriptions-item label=\"模型名称\">\n            <n-tag type=\"info\">{{ currentRerankModel.displayName || currentRerankModel.name }}</n-tag>\n          </n-descriptions-item>\n          <n-descriptions-item label=\"当前激活变体\">\n            <n-tag type=\"info\" size=\"small\">\n              {{ runtimeStatus?.rerank?.activeVariant || currentRerankModel.activeVariantFileName || '-' }}\n            </n-tag>\n          </n-descriptions-item>\n          <n-descriptions-item label=\"量化与格式\">\n            <n-space :size=\"4\">\n              <n-tag v-if=\"runtimeStatus?.rerank?.quantization\" size=\"small\">\n                {{ runtimeStatus.rerank.quantization }}\n              </n-tag>\n              <n-tag size=\"small\">\n                {{ runtimeStatus?.rerank?.modelFormat?.toUpperCase() || (currentRerankModel.localPath?.endsWith('.gguf') ? 'GGUF' : 'ONNX') }}\n              </n-tag>\n            </n-space>\n          </n-descriptions-item>\n          <n-descriptions-item label=\"规格\">\n            Cross-Encoder / {{ currentRerankModel.maxSequenceLength }} tokens\n          </n-descriptions-item>\n          <n-descriptions-item label=\"驻留策略\" :span=\"2\">\n            {{ getRetentionText(runtimeStatus?.rerank) }}\n          </n-descriptions-item>\n          <n-descriptions-item label=\"硬件状态\" :span=\"2\">\n            {{ runtimeStatus?.rerank?.offloadDescription || (runtimeStatus?.rerank?.isGpuMode ? 'GPU 模式' : '纯 CPU 模式') }}\n          </n-descriptions-item>\n        </n-descriptions>\n        <n-text v-else depth=\"3\">未配置重排模型</n-text>\n      </n-spin>\n    </n-card>\n          <!-- Rerank Models -->\n    <n-card title=\"重排模型管理\">\n      <template #header-extra>\n        <n-button text @click=\"loadRerankModels\">\n          <template #icon><n-icon :component=\"RefreshOutline\" /></template>\n          刷新\n        </n-button>\n      </template>\n      \n      <n-alert type=\"info\" style=\"margin-bottom: 16px\">\n        <template #header>说明</template>\n        <n-text>\n          重排模型（Reranker）用于对检索结果进行二次排序，提升检索精度。<br>\n          基于 Cross-Encoder 架构，能够更精确地计算 Query-Document 相关性。支持同一个模型下载与共存多个量化版本并即时切换。\n        </n-text>\n      </n-alert>\n      \n      <n-data-table\n        :columns=\"rerankModelColumns\"\n        :data=\"rerankModels\"\n        :loading=\"rerankModelsLoading\"\n        :row-key=\"(row) => row.name || ''\"\n      />\n    </n-card>\n        </n-space>\n      </n-tab-pane>\n\n      <n-tab-pane name=\"chat\">\n        <template #tab>\n          <n-space align=\"center\" :size=\"8\">\n            <span style=\"font-weight: 600\">聊天大模型 (LLM)</span>\n            <n-tag size=\"small\" round :bordered=\"false\" type=\"success\">{{ chatModels.length }}</n-tag>\n          </n-space>\n        </template>\n        <n-space vertical :size=\"16\" style=\"margin-top: 12px\">\n          <!-- Current Chat Model -->\n    <n-card title=\"当前聊天模型\">\n      <template #header-extra>\n        <n-space align=\"center\">\n          <n-tag v-if=\"runtimeStatus?.chat\" :type=\"getRuntimeStatusTagType(runtimeStatus.chat.state)\" size=\"small\" round>\n            {{ getRuntimeStatusText(runtimeStatus.chat) }}\n          </n-tag>\n          <n-tag v-if=\"runtimeStatus?.chat?.isGpuMode\" type=\"success\" size=\"small\" round>\n            🚀 {{ runtimeStatus.chat.executionDevice || 'GPU' }}\n          </n-tag>\n          <n-tag v-else-if=\"runtimeStatus?.chat\" type=\"default\" size=\"small\" round>\n            💻 CPU\n          </n-tag>\n          <n-button text size=\"small\" @click=\"loadRuntimeStatus\" :loading=\"runtimeStatusLoading\">\n            <template #icon><n-icon :component=\"RefreshOutline\" /></template>\n            刷新状态\n          </n-button>\n        </n-space>\n      </template>\n      <n-spin :show=\"chatCurrentLoading || runtimeStatusLoading\">\n        <n-alert v-if=\"chatApiMode\" type=\"info\" :show-icon=\"false\" style=\"margin-bottom: 12px\">\n          当前为 <strong>OpenAI 兼容 API 模式</strong>，本地聊天模型管理不适用。\n          请在「设置 → 聊天模型」中配置 API 地址和模型名称。\n        </n-alert>\n        <n-descriptions v-if=\"currentChatModel && !chatApiMode\" :column=\"4\" label-placement=\"left\">\n          <n-descriptions-item label=\"模型名称\">\n            <n-tag type=\"success\">{{ currentChatModel.displayName || currentChatModel.name }}</n-tag>\n          </n-descriptions-item>\n          <n-descriptions-item label=\"当前激活变体\">\n            <n-tag type=\"info\" size=\"small\">\n              {{ runtimeStatus?.chat?.activeVariant || currentChatModel.activeVariantFileName || '-' }}\n            </n-tag>\n          </n-descriptions-item>\n          <n-descriptions-item label=\"量化与格式\">\n            <n-space :size=\"4\">\n              <n-tag v-if=\"runtimeStatus?.chat?.quantization\" size=\"small\">\n                {{ runtimeStatus.chat.quantization }}\n              </n-tag>\n              <n-tag size=\"small\">\n                {{ runtimeStatus?.chat?.modelFormat?.toUpperCase() || (currentChatModel.localPath?.endsWith('.gguf') ? 'GGUF' : 'ONNX-GENAI') }}\n              </n-tag>\n            </n-space>\n          </n-descriptions-item>\n          <n-descriptions-item label=\"规格\">\n            {{ currentChatModel.maxSequenceLength ? (currentChatModel.maxSequenceLength + ' tokens') : '大语言模型' }}\n          </n-descriptions-item>\n          <n-descriptions-item label=\"驻留策略\" :span=\"2\">\n            {{ getRetentionText(runtimeStatus?.chat) }}\n          </n-descriptions-item>\n          <n-descriptions-item label=\"硬件状态\" :span=\"2\">\n            {{ runtimeStatus?.chat?.offloadDescription || (runtimeStatus?.chat?.isGpuMode ? 'GPU 模式' : '纯 CPU 模式') }}\n          </n-descriptions-item>\n        </n-descriptions>\n        <n-text v-else depth=\"3\">未配置本地聊天模型</n-text>\n      </n-spin>\n    </n-card>\n          <!-- Chat Models Table -->\n    <n-card title=\"聊天大模型管理\">\n      <template #header-extra>\n        <n-space>\n          <n-button text @click=\"openVariantSearch('')\">\n            <template #icon><n-icon :component=\"SearchOutline\" /></template>\n            搜索社区变体\n          </n-button>\n          <n-button text @click=\"loadChatModels\">\n            <template #icon><n-icon :component=\"RefreshOutline\" /></template>\n            刷新\n          </n-button>\n        </n-space>\n      </template>\n      \n      <n-alert type=\"info\" style=\"margin-bottom: 16px\">\n        <template #header>说明</template>\n        <n-text>\n          聊天大模型（LLM）用于执行高质量 RAG 生成、推理思考与多轮交互问答。<br>\n          支持 GGUF（基于 LLamaSharp 原生驱动）及 ONNX GenAI 格式，可自由下载多个量化等级（如 Q4_K_M / Q8_0）并即时热切换。\n        </n-text>\n      </n-alert>\n      \n      <n-data-table\n        :columns=\"chatModelColumns\"\n        :data=\"chatModels\"\n        :loading=\"chatModelsLoading\"\n        :row-key=\"(row) => row.name || ''\"\n      />\n    </n-card>\n        </n-space>\n      </n-tab-pane>\n    </n-tabs>\n\n    <!-- Switch Model Dialog -->\n    <n-modal v-model:show=\"showSwitchDialog\" preset=\"dialog\" title=\"切换模型\">\n      <n-space vertical>\n        <n-text>确定要切换到模型 <strong>{{ selectedModel?.displayName }}</strong> 吗？</n-text>\n        <n-alert v-if=\"selectedModel && !selectedModel.isDownloaded\" type=\"warning\" title=\"注意\">\n          该模型尚未下载，将先下载模型文件。<br>\n          <n-text depth=\"3\" v-if=\"!selectedModel.hasOnnx\">\n            此模型需要从 PyTorch 转换为 ONNX，请确保 Python 环境已配置。\n          </n-text>\n        </n-alert>\n        <n-alert type=\"info\" title=\"提示\">\n          切换模型后需要重新索引数据以获得最佳效果。\n        </n-alert>\n        <n-form-item v-if=\"selectedModel && currentModel && selectedModel.dimension !== currentModel.dimension\" label=\"旧向量数据\">\n          <n-radio-group v-model:value=\"deleteOldVectors\">\n            <n-space>\n              <n-radio :value=\"false\">保留旧向量数据</n-radio>\n              <n-radio :value=\"true\">删除旧向量数据</n-radio>\n            </n-space>\n          </n-radio-group>\n        </n-form-item>\n        <n-alert v-if=\"deleteOldVectors && selectedModel && currentModel && selectedModel.dimension !== currentModel.dimension\" type=\"error\" style=\"margin-top: 8px\">\n          删除后将无法恢复旧模型的向量数据\n        </n-alert>\n      </n-space>\n      <template #action>\n        <n-button @click=\"showSwitchDialog = false\">取消</n-button>\n        <n-button type=\"primary\" :loading=\"switching\" @click=\"confirmSwitch\">确认切换</n-button>\n      </template>\n    </n-modal>\n\n    <!-- Switch Variant Dialog -->\n    <n-modal v-model:show=\"switchVariantDialog.show\" preset=\"dialog\" title=\"切换模型版本变体\">\n      <n-space vertical>\n        <n-text>\n          确定将模型 <strong>{{ switchVariantDialog.model?.displayName || switchVariantDialog.model?.name }}</strong> 切换为以下版本变体吗？\n        </n-text>\n        <n-descriptions :column=\"1\" bordered size=\"small\" style=\"margin: 8px 0\">\n          <n-descriptions-item label=\"变体文件名\">\n            <code>{{ switchVariantDialog.variant?.fileName }}</code>\n          </n-descriptions-item>\n          <n-descriptions-item label=\"量化等级\">\n            <n-tag size=\"small\">{{ switchVariantDialog.variant?.quantization || '未标注' }}</n-tag>\n          </n-descriptions-item>\n          <n-descriptions-item label=\"模型格式\">\n            <n-tag size=\"small\" type=\"info\">{{ switchVariantDialog.variant?.format?.toUpperCase() }}</n-tag>\n          </n-descriptions-item>\n          <n-descriptions-item label=\"文件大小\">\n            {{ formatBytes(switchVariantDialog.variant?.fileSizeBytes || 0) }}\n          </n-descriptions-item>\n        </n-descriptions>\n        <n-alert type=\"info\">\n          切换后，模型服务将平滑释放旧版本并即时加载新变体，无需重启应用。\n        </n-alert>\n      </n-space>\n      <template #action>\n        <n-button @click=\"switchVariantDialog.show = false\">取消</n-button>\n        <n-button type=\"primary\" :loading=\"switchVariantDialog.loading\" @click=\"confirmSwitchVariant\">确认切换</n-button>\n      </template>\n    </n-modal>\n\n    <!-- Download State Machine Modal -->\n    <n-modal v-model:show=\"showDownloadModal\" preset=\"card\" :title=\"modalTitle\" style=\"width: 680px; max-width: 95vw\" :mask-closable=\"false\">\n      <!-- Steps Progress Indicator -->\n      <n-steps :current=\"modalCurrentStepNumber\" :status=\"modalStepStatus\" size=\"small\" style=\"margin-bottom: 20px\">\n        <n-step title=\"探测源\" description=\"短路探测镜像源\" />\n        <n-step title=\"选格式\" description=\"GGUF/ONNX选择\" />\n        <n-step title=\"下载中\" description=\"高速流式拉取\" />\n        <n-step title=\"就绪\" description=\"校验或格式就绪\" />\n      </n-steps>\n\n      <!-- Step 1: Probing State -->\n      <div v-if=\"downloadState === 'probing'\" style=\"text-align: center; padding: 28px 0\">\n        <n-spin size=\"large\">\n          <template #description>\n            <div style=\"margin-top: 14px; font-weight: 500; font-size: 14px\">正在并发探测可用镜像源与模型格式 (GGUF / ONNX)...</div>\n            <div style=\"margin-top: 6px; color: #888; font-size: 12px\">已启用 3.5s 超时短路机制，如需放弃可随时取消探测</div>\n          </template>\n        </n-spin>\n      </div>\n\n      <!-- Step 2: Options Ready -->\n      <div v-else-if=\"downloadState === 'options_ready'\">\n        <n-space vertical :size=\"12\">\n          <n-space align=\"center\" justify=\"space-between\" style=\"width: 100%\">\n            <n-text>即将下载模型 <strong>{{ selectedModel?.displayName || selectedModel?.name }}</strong></n-text>\n            <n-button size=\"small\" type=\"primary\" secondary @click=\"openVariantSearch(selectedModel?.displayName || selectedModel?.name)\">\n              <template #icon><n-icon :component=\"SearchOutline\" /></template>\n              🔍 搜索社区变体与量化版本 (GGUF / ONNX)\n            </n-button>\n          </n-space>\n\n          <template v-if=\"downloadOptions\">\n            <!-- Gated 受限授权警告 -->\n            <n-alert v-if=\"downloadOptions.isGatedOrUnauthorized\" type=\"error\" title=\"受限授权模型 (Gated Repository)\">\n              <n-text>\n                此模型在 HuggingFace 属于受限模型，未授权请求将返回 401 错误。<br>\n                请在系统设置中配置 <code>HF_TOKEN</code>，或在 HuggingFace 页面先申请并同意协议。\n              </n-text>\n            </n-alert>\n\n            <!-- 需要 PyTorch 转换 -->\n            <n-alert v-else-if=\"downloadOptions.needsConversion\" type=\"warning\" title=\"官方仅提供 PyTorch 权重\">\n              <n-text>\n                此模型官方仅发布了 PyTorch 原生权重，未提供预构建的 ONNX 或 GGUF 文件。<br>\n                系统将尝试通过本地 Python 环境导出为 ONNX，请确保已安装 Python 依赖：<br>\n                <n-code code=\"pip install torch transformers onnx\" language=\"bash\" style=\"margin-top: 8px\" /><br>\n                <n-text depth=\"3\">提示：系统原生支持 GGUF 格式。若存在社区量化版，可点击【搜索社区变体】挑选对应 -GGUF 仓库免转换直接运行。</n-text>\n              </n-text>\n            </n-alert>\n\n            <!-- 有多个选项需要用户选择 -->\n            <template v-else-if=\"downloadOptions.needsUserSelection || downloadOptions.hasBothFormats\">\n              <n-form-item v-if=\"downloadOptions.hasBothFormats\" label=\"格式选择\">\n                <n-radio-group v-model:value=\"selectedFormatFilter\" @update:value=\"onFormatFilterChange\">\n                  <n-space>\n                    <n-radio value=\"all\">全部 ({{ downloadOptions.allOptions.length }})</n-radio>\n                    <n-radio value=\"gguf\">GGUF 免转推荐 ({{ downloadOptions.ggufOptions.length }})</n-radio>\n                    <n-radio value=\"onnx\">ONNX ({{ downloadOptions.onnxOptions.length }})</n-radio>\n                  </n-space>\n                </n-radio-group>\n              </n-form-item>\n\n              <n-form-item label=\"选择要下载的具体模型\">\n                <n-radio-group v-model:value=\"selectedFile\" style=\"width: 100%\">\n                  <n-space vertical style=\"width: 100%\">\n                    <n-radio\n                      v-for=\"option in displayOptions\"\n                      :key=\"option.path\"\n                      :value=\"option.path\"\n                      style=\"width: 100%\"\n                    >\n                      <n-space align=\"center\" justify=\"space-between\" style=\"width: 100%\">\n                        <n-space align=\"center\">\n                          <span>{{ option.displayName }}</span>\n                          <n-tag v-if=\"option.isRecommended\" type=\"success\" size=\"small\">推荐</n-tag>\n                          <n-tag v-if=\"option.format === 'gguf'\" type=\"info\" size=\"small\">GGUF 免转</n-tag>\n                          <n-tag v-else-if=\"option.hasExternalData\" type=\"warning\" size=\"small\">外部格式</n-tag>\n                          <n-tag v-else type=\"info\" size=\"small\">嵌入式</n-tag>\n                          <n-tag v-if=\"option.isQuantized\" type=\"warning\" size=\"small\">量化</n-tag>\n                          <n-tag v-if=\"option.targetPlatform\" type=\"default\" size=\"small\">{{ option.targetPlatform }}</n-tag>\n                          <n-text depth=\"3\" v-if=\"option.size\" style=\"font-size: 12px\">({{ formatBytes(option.size) }})</n-text>\n                          <n-text depth=\"3\" v-if=\"option.repository && option.repository !== selectedModel?.name && option.repository !== selectedModel?.downloadUrl\" style=\"font-size: 11px\">[{{ option.repository }}]</n-text>\n                        </n-space>\n                      </n-space>\n                    </n-radio>\n                  </n-space>\n                </n-radio-group>\n              </n-form-item>\n\n              <n-alert v-if=\"selectedFileDescription\" type=\"info\" style=\"margin-top: 8px\">\n                {{ selectedFileDescription }}\n              </n-alert>\n            </template>\n\n            <!-- 只有一个选项，无需选择 -->\n            <template v-else>\n              <n-descriptions :column=\"1\" label-placement=\"left\">\n                <n-descriptions-item label=\"版本\">\n                  <n-space align=\"center\">\n                    <span>{{ downloadOptions.recommendedOption?.displayName }}</span>\n                    <n-tag v-if=\"downloadOptions.recommendedOption?.format === 'gguf'\" type=\"info\" size=\"small\">GGUF 免转</n-tag>\n                    <n-tag v-else-if=\"downloadOptions.recommendedOption?.hasExternalData\" type=\"warning\" size=\"small\">外部格式</n-tag>\n                    <n-tag v-else type=\"info\" size=\"small\">嵌入式</n-tag>\n                  </n-space>\n                </n-descriptions-item>\n                <n-descriptions-item label=\"维度\">{{ selectedModel?.dimension }}</n-descriptions-item>\n                <n-descriptions-item label=\"大小\">{{ formatBytes(selectedModel?.modelSizeBytes || 0) }}</n-descriptions-item>\n              </n-descriptions>\n            </template>\n          </template>\n\n          <n-alert type=\"info\" style=\"margin-top: 8px\">\n            <template #header>格式说明</template>\n            <n-text style=\"font-size: 12px\">\n              <strong>GGUF</strong>：LLamaSharp 原生免转加载，支持动态 GPU Offload，零 Python 依赖。<br>\n              <strong>嵌入式 ONNX</strong>：单个 ONNX 文件，兼容性好，适合 2GB 内模型。<br>\n              <strong>外部格式 ONNX</strong>：分离的权重文件，支持大模型，下载后可转换为嵌入式。\n            </n-text>\n          </n-alert>\n        </n-space>\n      </div>\n\n      <!-- Step 3 & 4: Downloading / Verifying / Converting / Completed / Failed / Cancelling -->\n      <div v-else>\n        <n-space vertical :size=\"14\">\n          <n-alert v-if=\"downloadState === 'completed'\" type=\"success\" title=\"模型下载完成\">\n            模型文件校验通过，已成功加入本地模型库并就绪可用！\n          </n-alert>\n          <n-alert v-else-if=\"downloadState === 'failed'\" type=\"error\" title=\"下载遇到问题\">\n            <n-text>{{ currentModalProgress?.errorMessage || '下载或校验失败，请检查网络后重试。' }}</n-text>\n            <n-text v-if=\"getErrorHelp(currentModalProgress?.errorCode)\" depth=\"3\" style=\"display: block; margin-top: 6px; font-size: 12px\">\n              <n-icon :component=\"InformationCircleOutline\" style=\"vertical-align: middle; margin-right: 4px\" />\n              {{ getErrorHelp(currentModalProgress.errorCode) }}\n            </n-text>\n          </n-alert>\n          <n-alert v-else-if=\"downloadState === 'cancelling'\" type=\"warning\" title=\"正在取消下载\">\n            正在停止后台下载任务并释放网络与文件资源...\n          </n-alert>\n\n          <n-progress\n            type=\"line\"\n            :percentage=\"currentModalProgress ? currentModalProgress.progress : (downloadState === 'completed' ? 100 : 0)\"\n            :status=\"modalProgressStatus\"\n            :indicator-placement=\"'inside'\"\n            style=\"margin: 8px 0\"\n          />\n\n          <n-descriptions :column=\"2\" label-placement=\"left\" size=\"small\" bordered>\n            <n-descriptions-item label=\"当前阶段\">\n              <n-tag v-if=\"currentModalProgress?.stage\" :type=\"getStageTagType(currentModalProgress.stage)\" size=\"small\">\n                {{ getStageText(currentModalProgress.stage) }}\n              </n-tag>\n              <span v-else style=\"color: #888\">-</span>\n            </n-descriptions-item>\n            <n-descriptions-item label=\"下载状态\">\n              <n-tag :type=\"getDownloadStatusType(currentModalProgress?.status || (downloadState === 'completed' ? 'completed' : 'downloading'))\" size=\"small\">\n                {{ getDownloadStatusText(currentModalProgress?.status || (downloadState === 'completed' ? 'completed' : 'downloading')) }}\n              </n-tag>\n            </n-descriptions-item>\n            <n-descriptions-item label=\"传输速率\">\n              {{ formatSpeed(currentModalProgress?.speed) }}\n            </n-descriptions-item>\n            <n-descriptions-item label=\"预估剩余时间\">\n              {{ formatRemainingTime(currentModalProgress?.remainingSeconds) }}\n            </n-descriptions-item>\n            <n-descriptions-item label=\"已下载大小\" :span=\"2\">\n              <span>{{ formatBytes(currentModalProgress?.downloadedBytes || 0) }}</span>\n              <span v-if=\"currentModalProgress?.totalBytes\" style=\"color: #888\"> / {{ formatBytes(currentModalProgress.totalBytes) }}</span>\n            </n-descriptions-item>\n          </n-descriptions>\n\n          <div v-if=\"currentModalProgress?.stageMessage\" style=\"font-size: 12px; background: rgba(128, 128, 128, 0.08); padding: 8px 12px; border-radius: 4px; border: 1px solid rgba(128, 128, 128, 0.16)\">\n            <n-text depth=\"2\">{{ currentModalProgress.stageMessage }}</n-text>\n          </div>\n        </n-space>\n      </div>\n\n      <!-- Action Footer -->\n      <template #action>\n        <n-space justify=\"space-between\" align=\"center\" style=\"width: 100%\">\n          <div>\n            <n-button\n              v-if=\"['downloading', 'verifying', 'converting'].includes(downloadState)\"\n              size=\"small\"\n              @click=\"showDownloadModal = false\"\n            >\n              后台运行（收起弹窗）\n            </n-button>\n          </div>\n          <n-space>\n            <!-- 探测阶段取消 -->\n            <n-button\n              v-if=\"downloadState === 'probing'\"\n              @click=\"cancelModalDownload\"\n            >\n              取消探测\n            </n-button>\n\n            <!-- 选项就绪阶段：取消 or 开始下载 -->\n            <template v-else-if=\"downloadState === 'options_ready'\">\n              <n-button @click=\"cancelModalDownload\">取消</n-button>\n              <n-button\n                type=\"primary\"\n                @click=\"confirmModalDownload\"\n                :disabled=\"!selectedFile && !downloadOptions?.needsConversion\"\n              >\n                开始下载\n              </n-button>\n            </template>\n\n            <!-- 下载中：取消下载 -->\n            <template v-else-if=\"['downloading', 'verifying', 'converting'].includes(downloadState)\">\n              <n-button\n                type=\"warning\"\n                ghost\n                @click=\"cancelModalDownload\"\n              >\n                取消下载\n              </n-button>\n            </template>\n\n            <!-- 取消中 -->\n            <template v-else-if=\"downloadState === 'cancelling'\">\n              <n-button disabled loading>正在取消...</n-button>\n            </template>\n\n            <!-- 失败：重试 or 关闭 -->\n            <template v-else-if=\"downloadState === 'failed'\">\n              <n-button @click=\"showDownloadModal = false\">关闭</n-button>\n              <n-button type=\"primary\" @click=\"retryDownload\">重新尝试</n-button>\n            </template>\n\n            <!-- 完成：立即切换 or 关闭 -->\n            <template v-else-if=\"downloadState === 'completed'\">\n              <n-button @click=\"showDownloadModal = false\">关闭</n-button>\n              <n-button type=\"primary\" @click=\"handleSwitchFromModal\">立即切换使用</n-button>\n            </template>\n          </n-space>\n        </n-space>\n      </template>\n    </n-modal>\n\n    <!-- Convert Format Dialog -->\n    <n-modal v-model:show=\"showConvertDialog\" preset=\"dialog\" title=\"转换模型格式\">\n      <n-space vertical>\n        <n-text>转换模型 <strong>{{ selectedModel?.displayName }}</strong> 的 ONNX 格式</n-text>\n        <n-descriptions :column=\"1\" label-placement=\"left\">\n          <n-descriptions-item label=\"当前格式\">\n            <n-tag :type=\"getFormatTagType(selectedModel?.onnxFormat)\" size=\"small\">\n              {{ getFormatText(selectedModel?.onnxFormat) }}\n            </n-tag>\n          </n-descriptions-item>\n          <n-descriptions-item label=\"目标格式\">\n            <n-radio-group v-model:value=\"targetFormat\">\n              <n-space>\n                <n-radio value=\"embedded\">\n                  <n-space align=\"center\">\n                    <span>嵌入式</span>\n                    <n-text depth=\"3\" style=\"font-size: 12px\">(CUDA 兼容性好)</n-text>\n                  </n-space>\n                </n-radio>\n                <n-radio value=\"external\">\n                  <n-space align=\"center\">\n                    <span>外部数据</span>\n                    <n-text depth=\"3\" style=\"font-size: 12px\">(支持大模型)</n-text>\n                  </n-space>\n                </n-radio>\n              </n-space>\n            </n-radio-group>\n          </n-descriptions-item>\n        </n-descriptions>\n        <n-alert type=\"info\" title=\"说明\">\n          <n-text>\n            <strong>嵌入式格式</strong>：单个 ONNX 文件，CUDA 兼容性好，但有 2GB 限制。<br>\n            <strong>外部数据格式</strong>：分离的权重文件，支持大模型，但 CUDA 动态 batch 可能有问题。\n          </n-text>\n        </n-alert>\n        <n-alert v-if=\"selectedModel && selectedModel.onnxFormat === targetFormat\" type=\"warning\">\n          模型已是目标格式，无需转换。\n        </n-alert>\n      </n-space>\n      <template #action>\n        <n-button @click=\"showConvertDialog = false\">取消</n-button>\n        <n-button \n          type=\"primary\" \n          :disabled=\"selectedModel?.onnxFormat === targetFormat\"\n          :loading=\"converting\" \n          @click=\"confirmConvert\">\n          开始转换\n        </n-button>\n      </template>\n    </n-modal>\n\n    <!-- Add Custom Model Dialog -->\n    <n-modal v-model:show=\"showAddCustomDialog\" preset=\"dialog\" title=\"添加自定义模型\">\n      <n-space vertical>\n        <n-form :model=\"customModelForm\">\n          <n-form-item label=\"HuggingFace 模型 ID\" required>\n            <n-input \n              v-model:value=\"customModelForm.huggingFaceId\" \n              placeholder=\"例如: BAAI/bge-base-zh-v1.5\"\n            />\n          </n-form-item>\n          <n-form-item label=\"显示名称（可选）\">\n            <n-input \n              v-model:value=\"customModelForm.displayName\" \n              placeholder=\"留空则使用模型名称\"\n            />\n          </n-form-item>\n        </n-form>\n        <n-alert type=\"info\">\n          <n-text>\n            输入 HuggingFace 上的模型 ID，系统将自动获取模型信息并添加到列表中。<br>\n            格式：<n-code code=\"owner/model-name\" :inline=\"true\" />\n          </n-text>\n        </n-alert>\n      </n-space>\n      <template #action>\n        <n-button @click=\"showAddCustomDialog = false\">取消</n-button>\n        <n-button type=\"primary\" :loading=\"addingCustom\" @click=\"confirmAddCustom\">添加</n-button>\n      </template>\n    </n-modal>\n\n    <!-- Delete Model Dialog -->\n    <n-modal v-model:show=\"showDeleteDialog\" preset=\"dialog\" title=\"删除模型\">\n      <n-space vertical>\n        <n-text>确定要删除模型 <strong>{{ selectedModel?.displayName }}</strong> 吗？</n-text>\n        <n-alert type=\"warning\" title=\"警告\">\n          删除后将同时删除模型文件夹及其所有内容，此操作不可恢复。\n        </n-alert>\n      </n-space>\n      <template #action>\n        <n-button @click=\"showDeleteDialog = false\">取消</n-button>\n        <n-button type=\"error\" :loading=\"deleting\" @click=\"confirmDelete\">确认删除</n-button>\n      </template>\n    </n-modal>\n\n    <!-- Community Variant Search Modal -->\n    <n-modal v-model:show=\"showVariantSearchModal\" preset=\"card\" title=\"搜索开源社区模型变体与量化版本 (GGUF / ONNX)\" style=\"width: 820px; max-width: 95vw\">\n      <n-space vertical :size=\"16\">\n        <n-alert type=\"info\">\n          当官方库未提供满意的量化版本（例如只有 FP32 / FP16 原始权重或官方库无 GGUF）时，可在 HuggingFace / 镜像源上直接搜索开源社区贡献的 GGUF 量化版本 (如 Q4_K_M, Q8_0) 或 ONNX 格式，选定后可直接一键下载并纳入模型统一管理。\n        </n-alert>\n\n        <n-space align=\"center\" style=\"width: 100%\">\n          <n-input\n            v-model:value=\"variantSearchQuery\"\n            placeholder=\"输入模型名称或作者关键词 (例如: bge-m3 或 Qwen2.5-Coder)\"\n            style=\"flex: 1; min-width: 280px\"\n            @keyup.enter=\"handleSearchVariants\"\n          />\n          <n-radio-group v-model:value=\"variantSearchFormat\">\n            <n-space>\n              <n-radio value=\"all\">全部格式</n-radio>\n              <n-radio value=\"gguf\">仅 GGUF (免转推荐)</n-radio>\n              <n-radio value=\"onnx\">仅 ONNX</n-radio>\n            </n-space>\n          </n-radio-group>\n          <n-button type=\"primary\" :loading=\"variantSearching\" @click=\"handleSearchVariants\">\n            <template #icon><n-icon :component=\"SearchOutline\" /></template>\n            搜索变体\n          </n-button>\n        </n-space>\n\n        <!-- Search Results List -->\n        <n-spin :show=\"variantSearching\">\n          <div v-if=\"variantSearchResults.length > 0\" style=\"max-height: 480px; overflow-y: auto; padding-right: 4px\">\n            <n-list hoverable clickable>\n              <n-list-item v-for=\"item in variantSearchResults\" :key=\"item.id\" style=\"padding: 12px\">\n                <n-thing>\n                  <template #header>\n                    <n-space align=\"center\" justify=\"space-between\" style=\"width: 100%\">\n                      <n-space align=\"center\">\n                        <span style=\"font-weight: 600; font-size: 15px\">{{ item.id }}</span>\n                        <n-tag v-if=\"item.hasGguf\" type=\"info\" size=\"small\">GGUF</n-tag>\n                        <n-tag v-if=\"item.hasOnnx\" type=\"warning\" size=\"small\">ONNX</n-tag>\n                        <n-tag size=\"small\" type=\"default\">作者: {{ item.author || '社区' }}</n-tag>\n                      </n-space>\n                      <n-space align=\"center\">\n                        <n-text depth=\"3\" style=\"font-size: 12px\">📥 {{ item.downloads?.toLocaleString() }} 下载</n-text>\n                        <n-text depth=\"3\" style=\"font-size: 12px\">❤️ {{ item.likes }}</n-text>\n                        <n-button size=\"small\" type=\"primary\" secondary @click=\"inspectVariantRepo(item.id)\">\n                          查看量化文件列表\n                        </n-button>\n                      </n-space>\n                    </n-space>\n                  </template>\n\n                  <template #description>\n                    <n-text v-if=\"item.description\" depth=\"3\" style=\"font-size: 12px; display: block; margin-top: 4px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap\">\n                      {{ item.description }}\n                    </n-text>\n                  </template>\n\n                  <!-- Expanded Repository Details Drawer/Card -->\n                  <div v-if=\"selectedVariantDetail && selectedVariantDetail.id === item.id\" style=\"margin-top: 12px; padding: 14px; background: rgba(128, 128, 128, 0.08); border-radius: 6px; border: 1px solid rgba(128, 128, 128, 0.18)\">\n                    <n-spin :show=\"loadingVariantDetail\">\n                      <n-space vertical :size=\"10\">\n                        <n-space justify=\"space-between\" align=\"center\">\n                          <n-text strong>仓库文件清单 (共 {{ selectedVariantDetail.totalFilesCount }} 个文件)</n-text>\n                          <n-text depth=\"3\" style=\"font-size: 12px\">来源: {{ selectedVariantDetail.sourceEndpoint }}</n-text>\n                        </n-space>\n\n                        <!-- GGUF Files Section -->\n                        <div v-if=\"selectedVariantDetail.ggufFiles?.length > 0\">\n                          <n-text type=\"info\" strong style=\"font-size: 13px; display: block; margin-bottom: 6px\">\n                            📦 GGUF 量化变体 ({{ selectedVariantDetail.ggufFiles.length }} 个文件)\n                          </n-text>\n                          <n-space vertical :size=\"6\">\n                            <div\n                              v-for=\"file in selectedVariantDetail.ggufFiles\"\n                              :key=\"file.path\"\n                              style=\"display: flex; justify-content: space-between; align-items: center; padding: 8px 12px; background: rgba(128, 128, 128, 0.12); border: 1px solid rgba(128, 128, 128, 0.18); border-radius: 4px\"\n                            >\n                              <n-space align=\"center\">\n                                <n-text strong style=\"font-family: monospace; font-size: 13px\">{{ file.fileName }}</n-text>\n                                <n-tag v-if=\"file.quantization\" size=\"small\" type=\"info\">{{ file.quantization }}</n-tag>\n                                <n-tag v-if=\"file.isRecommended\" size=\"small\" type=\"success\">推荐</n-tag>\n                                <n-text depth=\"3\" style=\"font-size: 12px\">({{ formatBytes(file.fileSize) }})</n-text>\n                              </n-space>\n                              <n-button size=\"tiny\" type=\"primary\" @click=\"selectVariantFileForDownload(file, selectedVariantDetail.id)\">\n                                选用此变体下载\n                              </n-button>\n                            </div>\n                          </n-space>\n                        </div>\n\n                        <!-- ONNX Files Section -->\n                        <div v-if=\"selectedVariantDetail.onnxFiles?.length > 0\" style=\"margin-top: 8px\">\n                          <n-text type=\"warning\" strong style=\"font-size: 13px; display: block; margin-bottom: 6px\">\n                            ⚡ ONNX 变体 ({{ selectedVariantDetail.onnxFiles.length }} 个文件)\n                          </n-text>\n                          <n-space vertical :size=\"6\">\n                            <div\n                              v-for=\"file in selectedVariantDetail.onnxFiles\"\n                              :key=\"file.path\"\n                              style=\"display: flex; justify-content: space-between; align-items: center; padding: 8px 12px; background: rgba(128, 128, 128, 0.12); border: 1px solid rgba(128, 128, 128, 0.18); border-radius: 4px\"\n                            >\n                              <n-space align=\"center\">\n                                <n-text strong style=\"font-family: monospace; font-size: 13px\">{{ file.fileName }}</n-text>\n                                <n-tag v-if=\"file.quantization\" size=\"small\" type=\"warning\">{{ file.quantization }}</n-tag>\n                                <n-text depth=\"3\" style=\"font-size: 12px\">({{ formatBytes(file.fileSize) }})</n-text>\n                              </n-space>\n                              <n-button size=\"tiny\" type=\"primary\" @click=\"selectVariantFileForDownload(file, selectedVariantDetail.id)\">\n                                选用此变体下载\n                              </n-button>\n                            </div>\n                          </n-space>\n                        </div>\n\n                        <div v-if=\"!selectedVariantDetail.ggufFiles?.length && !selectedVariantDetail.onnxFiles?.length\" style=\"padding: 8px\">\n                          <n-text depth=\"3\" style=\"font-size: 12px\">该仓库未包含直接可用的 .gguf 或 .onnx 权重文件。</n-text>\n                        </div>\n                      </n-space>\n                    </n-spin>\n                  </div>\n                </n-thing>\n              </n-list-item>\n            </n-list>\n          </div>\n          <div v-else-if=\"!variantSearching\" style=\"text-align: center; padding: 32px 0\">\n            <n-text depth=\"3\">输入关键词并点击搜索，发现社区丰富的 GGUF 量化与 ONNX 变体模型</n-text>\n          </div>\n        </n-spin>\n      </n-space>\n      <template #action>\n        <n-space justify=\"end\">\n          <n-button @click=\"showVariantSearchModal = false\">关闭</n-button>\n        </n-space>\n      </template>\n    </n-modal>\n  </n-space>";
export default component;
