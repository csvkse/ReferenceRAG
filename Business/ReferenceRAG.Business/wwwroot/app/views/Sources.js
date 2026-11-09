import { defineComponent as _defineComponent } from 'vue';
import { ref, computed, h, onMounted, onUnmounted, onActivated } from 'vue';
import { useRouter } from 'vue-router';
import { useMessage, useDialog, NButton, NSpace, NTag, NPopconfirm, NInput, NInputGroup, NRadioGroup, NRadio, NText, NAlert, NCard, NForm, NFormItem, NDataTable } from 'naive-ui';
import { sourcesApi } from '/app/api/index.js';
import { isDesktop, selectFolder } from '/core/platform.js';
import { JobCenter } from '/core/jobs/JobCenter.js';
import FolderPickerModal from '/app/components/FolderPickerModal.js';
import SourceFileManagerDrawer from '/app/components/SourceFileManagerDrawer.js';

const component = /*@__PURE__*/ _defineComponent({
    ...{ name: 'Sources' },
    components: { FolderPickerModal, SourceFileManagerDrawer },
    __name: 'Sources',
    setup(__props, { expose: __expose }) {
        __expose();
        const router = useRouter();
        const message = useMessage();
        const dialog = useDialog();
        const formRef = ref(null);
        const loading = ref(false);
        const creating = ref(false);
        const sources = ref([]);

        // File Manager Drawer state
        const showFileManager = ref(false);
        const fileManagerSource = ref(null);
        const handleOpenFileManager = (source) => {
            fileManagerSource.value = source;
            showFileManager.value = true;
        };

        // Edit state
        const editingSource = ref(null);
        const editingName = ref('');

        const newSource = ref({
            path: '',
            name: '',
            filePatterns: ''
        });
        const showFolderPicker = ref(false);
        const handleFolderSelected = (selectedPath) => {
            if (selectedPath) {
                newSource.value.path = selectedPath;
                if (!newSource.value.name) {
                    const clean = selectedPath.replace(/\\/g, '/').replace(/\/$/, '');
                    const autoName = clean.split('/').pop() || '';
                    if (autoName && autoName !== ':') {
                        newSource.value.name = autoName;
                    }
                }
            }
        };

        const selectDirectory = async () => {
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
                width: 100,
                render: (row) => row.fileCount != null ? row.fileCount.toLocaleString() : '-'
            },
            {
                title: '分块数',
                key: 'chunkCount',
                width: 100,
                render: (row) => row.chunkCount != null ? row.chunkCount.toLocaleString() : '-'
            },
            {
                title: '操作',
                key: 'actions',
                width: 320,
                render: (row) => h(NSpace, { size: 'small' }, {
                    default: () => [
                        h(NButton, {
                            size: 'small',
                            type: 'primary',
                            secondary: true,
                            onClick: () => handleOpenFileManager(row)
                        }, {
                            default: () => '文件管理'
                        }),
                        h(NButton, {
                            size: 'small',
                            onClick: () => handleEdit(row)
                        }, {
                            default: () => '编辑'
                        }),
                        h(NButton, {
                            size: 'small',
                            type: 'info',
                            onClick: () => handleInspectSource(row)
                        }, {
                            default: () => '探查'
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
                            type: 'error',
                            onClick: () => handleDelete(row)
                        }, {
                            default: () => '删除'
                        })
                    ]
                })
            }
        ];

        const loadSources = async (silent = false) => {
            if (!silent) loading.value = true;
            try {
                const response = await sourcesApi.getAll();
                sources.value = response.data;
            }
            catch (error) {
                if (!silent) message.error('加载源列表失败');
            }
            finally {
                if (!silent) loading.value = false;
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
                const patterns = newSource.value.filePatterns
                    ? newSource.value.filePatterns.split(/[,;\n]/).map(p => p.trim()).filter(Boolean)
                    : undefined;
                await sourcesApi.create({
                    path: newSource.value.path,
                    name: newSource.value.name || undefined,
                    filePatterns: patterns && patterns.length > 0 ? patterns : undefined
                });
                message.success('源添加成功！可在【知识库管理】中将其挂载为连接器');
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
            newSource.value = { path: '', name: '', filePatterns: '' };
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

        const handleInspectSource = async (source) => {
            const loadingMsg = message.loading(`正在探查数据源 "${source.name}" 的物理文件...`, { duration: 0 });
            try {
                const response = await sourcesApi.scan(source.name);
                loadingMsg.destroy();
                const res = response.data;
                const extSummary = Object.entries(res.extensions || {})
                    .map(([ext, count]) => `${ext} (${count}个)`)
                    .join(', ') || '无';
                const diskExtSummary = Object.entries(res.diskExtensions || {})
                    .map(([ext, count]) => `${ext} (${count}个)`)
                    .join(', ') || '无';

                const fileItems = (res.files || []).slice(0, 30).map(f =>
                    h('div', { style: 'font-size: 12px; display: flex; justify-content: space-between; padding: 3px 0; border-bottom: 1px dashed rgba(128,128,128,0.15);' }, [
                        h('span', { style: 'overflow: hidden; text-overflow: ellipsis; white-space: nowrap; max-width: 420px;' }, `📄 ${f.path}`),
                        h('span', { style: 'color: #888; margin-left: 8px;' }, `${(f.size / 1024).toFixed(1)} KB`)
                    ])
                );

                dialog.info({
                    title: `物理探查报告 - ${source.name}`,
                    style: { width: '620px', maxWidth: '92vw' },
                    content: () => h('div', { style: 'display: flex; flex-direction: column; gap: 10px; margin-top: 8px;' }, [
                        h('div', { style: 'background: rgba(128,128,128,0.06); padding: 8px 12px; border-radius: 6px; font-size: 13px;' }, [
                            h('div', `📂 物理路径: ${source.path}`),
                            h('div', `🎯 匹配规则: ${Array.isArray(source.filePatterns) ? source.filePatterns.join(', ') : (source.filePatterns || '*.md, *.txt')}`),
                            h('div', { style: 'margin-top: 4px; font-weight: bold; color: ' + (res.totalFiles > 0 ? '#18a058' : '#d03050') }, `📊 ${res.message || (res.totalFiles + ' 个文件匹配')}`)
                        ]),
                        h('div', { style: 'font-size: 12px;' }, [
                            h('div', `磁盘总文件数: ${res.totalDiskFiles ?? res.totalFiles} 个 (包含的所有后缀: ${diskExtSummary})`),
                            h('div', `规则匹配文件数: ${res.totalFiles} 个 (匹配的后缀: ${extSummary})`)
                        ]),
                        res.files && res.files.length > 0 ? h('div', [
                            h('div', { style: 'font-size: 12px; font-weight: bold; margin-bottom: 4px;' }, `匹配文件预览 (前 ${fileItems.length} 个):`),
                            h('div', { style: 'max-height: 200px; overflow-y: auto; background: rgba(128,128,128,0.04); padding: 6px 10px; border-radius: 4px;' }, fileItems)
                        ]) : null
                    ]),
                    positiveText: '关闭'
                });
            } catch (err) {
                loadingMsg.destroy();
                message.error(err.response?.data?.error || err.message || '探查失败');
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
                                h(NRadio, { value: true }, { default: () => '删除源配置 + 清空关联历史切片（推荐）' }),
                                h(NRadio, { value: false }, { default: () => '仅删除源配置（保留已有文件数据）' })
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
                        message.success(deleteData ? `"${source.name}" 及其数据已全部删除` : `"${source.name}" 配置已删除`);
                        await loadSources();
                    }
                    catch (error) {
                        message.error(error.response?.data?.error || error.message || '删除失败');
                    }
                },
                onNegativeClick: () => {
                    // 安全取消
                }
            });
        };

        let unbindSources = null;
        onMounted(async () => {
            loadSources();
            try {
                await JobCenter.initialize();
                unbindSources = JobCenter.registerRefresher('sources', () => loadSources(true));
            } catch (err) {
                console.warn('JobCenter 初始化失败:', err);
            }
        });

        onUnmounted(() => {
            unbindSources?.();
        });

        onActivated(() => {
            loadSources(true);
        });

        const __returned__ = {
            router, isDesktop, selectDirectory, showFolderPicker, handleFolderSelected,
            message, dialog, formRef, loading, creating, sources, editingSource, editingName,
            newSource, formRules, columns, loadSources, handleCreateSource, resetForm,
            handleToggle, handleEdit, handleInspectSource, handleDelete, showFileManager,
            fileManagerSource, handleOpenFileManager,
            get FolderPickerModal() { return FolderPickerModal; },
            get SourceFileManagerDrawer() { return SourceFileManagerDrawer; },
            get NButton() { return NButton; },
            get NSpace() { return NSpace; },
            get NTag() { return NTag; },
            get NPopconfirm() { return NPopconfirm; },
            get NInput() { return NInput; },
            get NInputGroup() { return NInputGroup; },
            get NAlert() { return NAlert; },
            get NCard() { return NCard; },
            get NForm() { return NForm; },
            get NFormItem() { return NFormItem; },
            get NDataTable() { return NDataTable; }
        };
        return __returned__;
    }
});

component.template = `
  <n-space vertical :size="20">
    <!-- Architecture Notice Banner -->
    <n-alert type="info" title="知识库架构升级提示">
      系统已全面升级为多知识库物理隔离架构。数据源现作为连接器挂载于各独立知识库中。如需执行增量同步、重建切片或管理向量索引，请前往
      <n-button text type="primary" style="font-weight: bold; margin-left: 4px;" @click="router.push('/knowledge-bases')">
        知识库管理
      </n-button>
    </n-alert>

    <!-- Add Source -->
    <n-card title="添加数据源">
      <n-space vertical>
        <n-form
          ref="formRef"
          :model="newSource"
          :rules="formRules"
          label-placement="left"
          label-width="80"
        >
          <n-form-item :label="isDesktop ? '本机目录' : '服务器目录'" path="path">
            <n-input-group style="width: 100%;">
              <n-input v-model:value="newSource.path" placeholder="输入文件夹绝对路径（必填）" />
              <n-button secondary type="primary" @click="selectDirectory">选择文件夹</n-button>
            </n-input-group>
          </n-form-item>
          <n-form-item label="名称">
            <n-input v-model:value="newSource.name" placeholder="输入源名称（可选，默认使用文件夹名）" />
          </n-form-item>
          <n-form-item label="文件规则">
            <n-input v-model:value="newSource.filePatterns" placeholder="文件匹配后缀（可选，留空默认 *.md, *.txt；多项以逗号分隔，如 *.md, *.txt, *.c, *.py）" />
          </n-form-item>
          <n-space>
            <n-button type="primary" :loading="creating" @click="handleCreateSource">
              添加
            </n-button>
            <n-button @click="resetForm">重置</n-button>
          </n-space>
        </n-form>
      </n-space>
    </n-card>

    <!-- Sources Table -->
    <n-card title="数据源列表">
      <template #header-extra>
        <n-button size="small" :loading="loading" @click="loadSources">
          刷新
        </n-button>
      </template>
      <n-data-table
        :columns="columns"
        :data="sources"
        :loading="loading"
        :bordered="false"
        :row-key="(row) => row.name"
      />
    </n-card>

    <!-- Folder Picker Modal for Web Mode -->
    <folder-picker-modal
      v-model:show="showFolderPicker"
      :initial-path="newSource.path"
      @select="handleFolderSelected"
    />

    <!-- Source File Manager Drawer -->
    <source-file-manager-drawer
      v-model:show="showFileManager"
      :source="fileManagerSource"
      @file-changed="() => loadSources(true)"
    />
  </n-space>
`;

export default component;
