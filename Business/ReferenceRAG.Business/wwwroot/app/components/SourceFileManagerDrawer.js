import { defineComponent as _defineComponent, ref, computed, watch, h } from 'vue';
import {
    NDrawer, NDrawerContent, NCard, NSpace, NButton, NIcon, NInput, NAlert, NSpin,
    NText, NEmpty, NTag, NDataTable, NModal, NPopconfirm, useMessage, useDialog
} from 'naive-ui';
import { fileSystemApi } from '/app/api/index.js';

export default _defineComponent({
    name: 'SourceFileManagerDrawer',
    props: {
        show: { type: Boolean, default: false },
        source: { type: Object, default: () => null }
    },
    emits: ['update:show', 'file-changed'],
    setup(props, { emit }) {
        const message = useMessage();
        const dialog = useDialog();

        const currentPath = ref('');
        const rootPath = computed(() => props.source?.path?.replace(/\\/g, '/').replace(/\/$/, '') || '');
        const sourceName = computed(() => props.source?.name || '未知知识源');

        const entries = ref([]);
        const loading = ref(false);
        const error = ref('');

        // 新建文件夹
        const showMkdirModal = ref(false);
        const newFolderName = ref('');
        const creatingFolder = ref(false);

        // 新建文件
        const showNewFileModal = ref(false);
        const newFileName = ref('');

        // 重命名
        const showRenameModal = ref(false);
        const renameTarget = ref(null);
        const renameNewName = ref('');
        const renaming = ref(false);

        // 在线文本编辑
        const showEditorModal = ref(false);
        const editorFilePath = ref('');
        const editorFileName = ref('');
        const editorContent = ref('');
        const editorLoading = ref(false);
        const editorSaving = ref(false);

        // 文件上传 input
        const fileInputRef = ref(null);
        const uploading = ref(false);

        // 关键字/正则搜索状态
        const searchQuery = ref('');
        const searchIsRegex = ref(false);
        const searchCaseSensitive = ref(false);
        const searchLoading = ref(false);
        const showSearchModal = ref(false);
        const searchResult = ref(null);

        // 大纲查看状态
        const showOutlineModal = ref(false);
        const outlineLoading = ref(false);
        const outlineData = ref(null);

        // 计算面包屑（以 rootPath 为根）
        const breadcrumbs = computed(() => {
            if (!rootPath.value || !currentPath.value) return [];
            const normRoot = rootPath.value;
            const normCurrent = currentPath.value.replace(/\\/g, '/').replace(/\/$/, '');

            if (normCurrent === normRoot || !normCurrent.startsWith(normRoot)) {
                return [{ name: sourceName.value, path: normRoot }];
            }

            const relative = normCurrent.substring(normRoot.length).replace(/^\//, '');
            const parts = [{ name: sourceName.value, path: normRoot }];
            let accumulated = normRoot;

            for (const seg of relative.split('/').filter(Boolean)) {
                accumulated += '/' + seg;
                parts.push({ name: seg, path: accumulated });
            }
            return parts;
        });

        const loadDirectory = async (target = currentPath.value) => {
            if (!target) return;
            loading.value = true;
            error.value = '';
            try {
                const response = await fileSystemApi.browse(target, false);
                const data = response.data;
                if (data && Array.isArray(data.entries)) {
                    entries.value = data.entries;
                    currentPath.value = data.path;
                } else {
                    error.value = '返回的目录格式不正确';
                }
            } catch (err) {
                error.value = err.response?.data?.error || err.message || '加载目录失败';
            } finally {
                loading.value = false;
            }
        };

        watch(() => props.show, (newVal) => {
            if (newVal && props.source?.path) {
                currentPath.value = rootPath.value;
                loadDirectory(rootPath.value);
            }
        });

        const navigateTo = (path) => {
            if (path && (path === rootPath.value || path.startsWith(rootPath.value + '/'))) {
                loadDirectory(path);
            }
        };

        const goUp = () => {
            if (breadcrumbs.value.length > 1) {
                const parent = breadcrumbs.value[breadcrumbs.value.length - 2];
                navigateTo(parent.path);
            }
        };

        const formatBytes = (n) => {
            if (!n || n <= 0) return '-';
            const units = ['B', 'KB', 'MB', 'GB'];
            let v = n, i = 0;
            while (v >= 1024 && i < units.length - 1) { v /= 1024; i++; }
            return v.toFixed(i === 0 ? 0 : 1) + ' ' + units[i];
        };

        // 新建文件夹
        const handleCreateFolder = async () => {
            if (!newFolderName.value.trim()) {
                message.warning('请输入文件夹名称');
                return;
            }
            creatingFolder.value = true;
            try {
                const target = `${currentPath.value}/${newFolderName.value.trim()}`;
                await fileSystemApi.mkdir(target);
                message.success('创建文件夹成功');
                showMkdirModal.value = false;
                newFolderName.value = '';
                await loadDirectory();
            } catch (err) {
                message.error(err.response?.data?.error || err.message || '创建文件夹失败');
            } finally {
                creatingFolder.value = false;
            }
        };

        // 新建文件
        const handleCreateFile = async () => {
            if (!newFileName.value.trim()) {
                message.warning('请输入文件名称');
                return;
            }
            const target = `${currentPath.value}/${newFileName.value.trim()}`;
            showNewFileModal.value = false;
            editorFilePath.value = target;
            editorFileName.value = newFileName.value.trim();
            editorContent.value = '';
            showEditorModal.value = true;
            newFileName.value = '';
        };

        // 文本预览与编辑
        const handleOpenEditor = async (row) => {
            editorFilePath.value = row.path;
            editorFileName.value = row.name;
            editorContent.value = '';
            showEditorModal.value = true;
            editorLoading.value = true;
            try {
                const res = await fileSystemApi.readText(row.path, rootPath.value);
                editorContent.value = res.data.content || '';
            } catch (err) {
                const errData = err.response?.data;
                if (errData?.isBinary) {
                    message.warning('二进制文件无法在线编辑，请使用下载功能');
                    showEditorModal.value = false;
                } else if (errData?.isTooLarge) {
                    message.warning('文件超过 2MB 大小限制，无法在线编辑，请直接下载');
                    showEditorModal.value = false;
                } else {
                    message.error(errData?.error || err.message || '读取文件失败');
                }
            } finally {
                editorLoading.value = false;
            }
        };

        // 保存文本文件
        const handleSaveContent = async () => {
            editorSaving.value = true;
            try {
                await fileSystemApi.writeText(editorFilePath.value, editorContent.value, rootPath.value);
                message.success('文件保存成功');
                showEditorModal.value = false;
                await loadDirectory();
                emit('file-changed', editorFilePath.value);
            } catch (err) {
                message.error(err.response?.data?.error || err.message || '保存文件失败');
            } finally {
                editorSaving.value = false;
            }
        };

        // 重命名
        const handleOpenRename = (row) => {
            renameTarget.value = row;
            renameNewName.value = row.name;
            showRenameModal.value = true;
        };

        const handleConfirmRename = async () => {
            if (!renameNewName.value.trim() || renameNewName.value.trim() === renameTarget.value.name) {
                showRenameModal.value = false;
                return;
            }
            renaming.value = true;
            try {
                const parentDir = currentPath.value;
                const newPath = `${parentDir}/${renameNewName.value.trim()}`;
                await fileSystemApi.executeAction('rename', renameTarget.value.path, newPath, false, rootPath.value);
                message.success('重命名成功');
                showRenameModal.value = false;
                await loadDirectory();
                emit('file-changed', renameTarget.value.path);
            } catch (err) {
                message.error(err.response?.data?.error || err.message || '重命名失败');
            } finally {
                renaming.value = false;
            }
        };

        // 删除项
        const handleDeleteEntry = async (row) => {
            try {
                await fileSystemApi.executeAction('delete', row.path, null, row.isDirectory, rootPath.value);
                message.success(`已删除 ${row.name}`);
                await loadDirectory();
                emit('file-changed', row.path);
            } catch (err) {
                message.error(err.response?.data?.error || err.message || '删除失败');
            }
        };

        // 上传文件
        const triggerUpload = () => {
            if (fileInputRef.value) {
                fileInputRef.value.click();
            }
        };

        const handleFileSelected = async (e) => {
            const files = e.target.files;
            if (!files || files.length === 0) return;

            uploading.value = true;
            try {
                for (let i = 0; i < files.length; i++) {
                    const formData = new FormData();
                    formData.append('file', files[i]);
                    await fileSystemApi.upload(currentPath.value, formData, rootPath.value);
                }
                message.success(`成功上传 ${files.length} 个文件`);
                await loadDirectory();
                emit('file-changed', currentPath.value);
            } catch (err) {
                message.error(err.response?.data?.error || err.message || '上传文件失败');
            } finally {
                uploading.value = false;
                if (fileInputRef.value) fileInputRef.value.value = '';
            }
        };

        // 打包下载当前目录
        const handleDownloadZip = () => {
            const url = fileSystemApi.getDownloadZipUrl(currentPath.value, rootPath.value);
            window.open(url, '_blank');
        };

        // 单文件下载
        const handleDownloadFile = (row) => {
            const url = fileSystemApi.getDownloadUrl(row.path, rootPath.value);
            window.open(url, '_blank');
        };

        // 文件内容/正则搜索
        const handleSearch = async () => {
            if (!searchQuery.value.trim()) {
                message.warning('请输入搜索关键词或正则表达式');
                return;
            }
            searchLoading.value = true;
            try {
                const subPath = (currentPath.value && rootPath.value && currentPath.value !== rootPath.value)
                    ? currentPath.value.substring(rootPath.value.length).replace(/^\//, '')
                    : null;

                const res = await fileSystemApi.search({
                    pattern: searchQuery.value.trim(),
                    isRegex: searchIsRegex.value,
                    caseSensitive: searchCaseSensitive.value,
                    sources: props.source?.name ? [props.source.name] : [],
                    subPath: subPath,
                    contextLines: 1,
                    maxMatches: 100
                });
                searchResult.value = res.data;
                showSearchModal.value = true;
            } catch (err) {
                message.error(err.response?.data?.error || err.message || '搜索执行失败');
            } finally {
                searchLoading.value = false;
            }
        };

        const openEditorFromSearch = (fullPath, relPath) => {
            const name = relPath.split('/').pop() || relPath;
            handleOpenEditor({ path: fullPath, name: name });
        };

        // 查看大纲
        const handleOpenOutline = async (row) => {
            outlineLoading.value = true;
            showOutlineModal.value = true;
            outlineData.value = null;
            try {
                const res = await fileSystemApi.getMetadata(row.path, rootPath.value);
                outlineData.value = res.data;
            } catch (err) {
                message.error(err.response?.data?.error || err.message || '获取文档大纲失败');
                showOutlineModal.value = false;
            } finally {
                outlineLoading.value = false;
            }
        };

        // 表格列定义
        const columns = [
            {
                title: '名称',
                key: 'name',
                render: (row) => h('div', {
                    style: { display: 'flex', alignItems: 'center', cursor: row.isDirectory ? 'pointer' : 'default' },
                    onClick: () => row.isDirectory ? navigateTo(row.path) : handleOpenEditor(row)
                }, [
                    h('span', { style: { marginRight: '8px', fontSize: '16px' } }, row.isDirectory ? '📁' : '📄'),
                    h('span', { style: { color: row.isDirectory ? '#18a058' : 'inherit', fontWeight: row.isDirectory ? '500' : 'normal' } }, row.name)
                ])
            },
            {
                title: '大小',
                key: 'size',
                width: 100,
                render: (row) => row.isDirectory ? '-' : formatBytes(row.size)
            },
            {
                title: '修改时间',
                key: 'modified',
                width: 160,
                render: (row) => row.modified && row.modified !== '0001-01-01T00:00:00Z'
                    ? new Date(row.modified).toLocaleString('zh-CN')
                    : '-'
            },
            {
                title: '操作',
                key: 'actions',
                width: 270,
                render: (row) => h(NSpace, { size: 'small' }, {
                    default: () => [
                        !row.isDirectory ? h(NButton, {
                            size: 'tiny',
                            type: 'primary',
                            secondary: true,
                            onClick: () => handleOpenEditor(row)
                        }, { default: () => '查看/编辑' }) : null,
                        !row.isDirectory && row.name.toLowerCase().endsWith('.md') ? h(NButton, {
                            size: 'tiny',
                            type: 'info',
                            secondary: true,
                            onClick: () => handleOpenOutline(row)
                        }, { default: () => '大纲' }) : null,
                        !row.isDirectory ? h(NButton, {
                            size: 'tiny',
                            secondary: true,
                            onClick: () => handleDownloadFile(row)
                        }, { default: () => '下载' }) : null,
                        h(NButton, {
                            size: 'tiny',
                            onClick: () => handleOpenRename(row)
                        }, { default: () => '重命名' }),
                        h(NPopconfirm, {
                            onPositiveClick: () => handleDeleteEntry(row)
                        }, {
                            trigger: () => h(NButton, { size: 'tiny', type: 'error', secondary: true }, { default: () => '删除' }),
                            default: () => `确定要彻底删除「${row.name}」吗？`
                        })
                    ].filter(Boolean)
                })
            }
        ];

        return {
            sourceName,
            currentPath,
            rootPath,
            breadcrumbs,
            entries,
            loading,
            error,
            columns,
            showMkdirModal,
            newFolderName,
            creatingFolder,
            showNewFileModal,
            newFileName,
            showRenameModal,
            renameNewName,
            renaming,
            showEditorModal,
            editorFileName,
            editorFilePath,
            editorContent,
            editorLoading,
            editorSaving,
            fileInputRef,
            uploading,
            searchQuery,
            searchIsRegex,
            searchCaseSensitive,
            searchLoading,
            showSearchModal,
            searchResult,
            showOutlineModal,
            outlineLoading,
            outlineData,
            navigateTo,
            goUp,
            loadDirectory,
            handleCreateFolder,
            handleCreateFile,
            handleOpenEditor,
            handleSaveContent,
            handleOpenRename,
            handleConfirmRename,
            handleDeleteEntry,
            triggerUpload,
            handleFileSelected,
            handleDownloadZip,
            handleSearch,
            openEditorFromSearch,
            handleOpenOutline
        };
    },
    template: `
    <n-drawer :show="show" @update:show="$emit('update:show', $event)" width="920" placement="right">
      <n-drawer-content :title="'📁 知识源文件管理: ' + sourceName" closable>
        <template #header>
          <div style="display: flex; align-items: center; justify-content: space-between; width: 100%;">
            <n-space align="center">
              <span style="font-weight: 600; font-size: 16px;">📁 源文件管理: {{ sourceName }}</span>
              <n-tag type="info" size="small">{{ rootPath }}</n-tag>
            </n-space>
          </div>
        </template>

        <!-- 工具栏 -->
        <n-space vertical :size="10" style="margin-bottom: 12px;">
          <!-- 顶部搜索工具栏 (Agent 级文件搜索) -->
          <div style="display: flex; align-items: center; gap: 8px; background: rgba(128, 128, 128, 0.05); padding: 8px 12px; border-radius: 6px;">
            <n-input
              v-model:value="searchQuery"
              size="small"
              placeholder="搜索文件文本或正则 (如: TODO:.*, class Controller)..."
              style="flex: 1;"
              clearable
              @keydown.enter="handleSearch"
            />
            <n-button
              size="small"
              :type="searchIsRegex ? 'primary' : 'default'"
              :secondary="!searchIsRegex"
              @click="searchIsRegex = !searchIsRegex"
            >
              .* 正则
            </n-button>
            <n-button
              size="small"
              :type="searchCaseSensitive ? 'primary' : 'default'"
              :secondary="!searchCaseSensitive"
              @click="searchCaseSensitive = !searchCaseSensitive"
            >
              Aa 大小写
            </n-button>
            <n-button size="small" type="primary" :loading="searchLoading" @click="handleSearch">
              🔍 搜索
            </n-button>
          </div>

          <n-space justify="space-between" align="center">
            <n-space>
              <n-button size="small" type="primary" @click="showNewFileModal = true">
                新建文件
              </n-button>
              <n-button size="small" @click="showMkdirModal = true">
                新建文件夹
              </n-button>
              <n-button size="small" type="info" :loading="uploading" @click="triggerUpload">
                上传文件
              </n-button>
              <input
                ref="fileInputRef"
                type="file"
                multiple
                style="display: none;"
                @change="handleFileSelected"
              />
              <n-button size="small" type="warning" secondary @click="handleDownloadZip">
                打包下载
              </n-button>
              <n-button size="small" :loading="loading" @click="loadDirectory()">
                刷新
              </n-button>
            </n-space>
            <n-button size="small" :disabled="breadcrumbs.length <= 1" @click="goUp">
              返回上一级
            </n-button>
          </n-space>

          <!-- 面包屑路径导航 -->
          <div style="background: rgba(128, 128, 128, 0.08); padding: 6px 12px; border-radius: 6px; display: flex; align-items: center; flex-wrap: wrap; gap: 6px; font-size: 13px;">
            <span style="color: #888;">位置：</span>
            <template v-for="(crumb, idx) in breadcrumbs" :key="crumb.path">
              <span
                :style="{ cursor: idx < breadcrumbs.length - 1 ? 'pointer' : 'default', color: idx === breadcrumbs.length - 1 ? '#18a058' : '#2080f0', fontWeight: idx === breadcrumbs.length - 1 ? 'bold' : 'normal' }"
                @click="navigateTo(crumb.path)"
              >
                {{ crumb.name }}
              </span>
              <span v-if="idx < breadcrumbs.length - 1" style="color: #999;">/</span>
            </template>
          </div>
        </n-space>

        <!-- 错误提示 -->
        <n-alert v-if="error" type="error" closable style="margin-bottom: 12px;">
          {{ error }}
        </n-alert>

        <!-- 文件表格 -->
        <n-spin :show="loading">
          <n-data-table
            :columns="columns"
            :data="entries"
            :pagination="false"
            size="small"
            :max-height="620"
            :empty="() => '该目录下暂无文件或子目录'"
          />
        </n-spin>

        <!-- 搜索结果弹窗 -->
        <n-modal v-model:show="showSearchModal" preset="card" title="🔍 搜索匹配结果" style="width: 860px; max-width: 90vw;">
          <div v-if="searchResult">
            <div style="margin-bottom: 12px; display: flex; justify-content: space-between; align-items: center;">
              <n-text depth="3">
                找到 {{ searchResult.totalMatches }} 处匹配（涉及 {{ searchResult.matchedFilesCount }} 个文件，扫描 {{ searchResult.totalFilesScanned }} 个文件，耗时 {{ searchResult.elapsedMilliseconds }}ms）
              </n-text>
              <n-tag v-if="searchResult.truncated" type="warning" size="small">匹配数达上限已早中断</n-tag>
            </div>

            <div v-if="searchResult.files && searchResult.files.length > 0" style="max-height: 520px; overflow-y: auto;">
              <div v-for="file in searchResult.files" :key="file.fullPath" style="margin-bottom: 12px; border: 1px solid rgba(128,128,128,0.2); border-radius: 6px; padding: 10px;">
                <div style="display: flex; justify-content: space-between; align-items: center; margin-bottom: 6px;">
                  <span style="font-weight: 600; cursor: pointer; color: #18a058;" @click="openEditorFromSearch(file.fullPath, file.relativePath)">
                    📄 {{ file.relativePath }} ({{ file.matchCount }} 处命中)
                  </span>
                  <n-button size="tiny" secondary type="primary" @click="openEditorFromSearch(file.fullPath, file.relativePath)">打开并查看</n-button>
                </div>
                <div v-for="(hunk, hIdx) in file.hunks" :key="hIdx" style="background: rgba(0,0,0,0.03); border-radius: 4px; padding: 6px 10px; margin-top: 6px; font-family: Consolas, monospace; font-size: 12px;">
                  <div style="color: #888; font-size: 11px; margin-bottom: 4px;">@@ 行 {{ hunk.startLine }} - {{ hunk.endLine }} @@</div>
                  <div v-for="line in hunk.lines" :key="line.lineNumber" :style="{ background: line.isMatch ? 'rgba(24, 160, 88, 0.15)' : 'transparent', padding: '1px 4px', display: 'flex' }">
                    <span style="width: 45px; color: #999; user-select: none;">{{ line.lineNumber }}:</span>
                    <span :style="{ fontWeight: line.isMatch ? 'bold' : 'normal', color: line.isMatch ? '#18a058' : 'inherit' }">{{ line.text }}</span>
                  </div>
                </div>
              </div>
            </div>
            <n-empty v-else description="未找到匹配项" />
          </div>
        </n-modal>

        <!-- 文档大纲弹窗 -->
        <n-modal v-model:show="showOutlineModal" preset="card" title="📑 文档大纲与元数据" style="width: 650px; max-width: 90vw;">
          <n-spin :show="outlineLoading">
            <div v-if="outlineData">
              <div style="margin-bottom: 12px;">
                <n-space align="center">
                  <span style="font-weight: bold; font-size: 15px;">{{ outlineData.name }}</span>
                  <n-tag size="small" type="info">{{ outlineData.totalLines }} 行</n-tag>
                  <n-tag v-if="outlineData.ragChunkCount != null" size="small" type="success">向量分块: {{ outlineData.ragChunkCount }} 个</n-tag>
                </n-space>
              </div>
              <div v-if="outlineData.frontmatter && Object.keys(outlineData.frontmatter).length > 0" style="margin-bottom: 12px; background: rgba(128,128,128,0.06); padding: 8px; border-radius: 6px;">
                <div style="font-weight: bold; font-size: 12px; color: #888; margin-bottom: 4px;">Frontmatter:</div>
                <div v-for="(v, k) in outlineData.frontmatter" :key="k" style="font-size: 12px;">
                  <span style="color: #2080f0;">{{ k }}:</span> {{ v }}
                </div>
              </div>
              <div v-if="outlineData.headings && outlineData.headings.length > 0" style="max-height: 400px; overflow-y: auto;">
                <div v-for="hItem in outlineData.headings" :key="hItem.lineNumber" :style="{ paddingLeft: (hItem.level - 1) * 16 + 'px' }" style="margin: 6px 0;">
                  <span style="color: #888; font-size: 11px; margin-right: 6px;">L{{ hItem.lineNumber }}</span>
                  <span style="font-weight: 500;">{{ '#'.repeat(hItem.level) }} {{ hItem.title }}</span>
                </div>
              </div>
              <n-empty v-else description="该文档未检测到 Markdown 标题大纲" />
            </div>
          </n-spin>
        </n-modal>

        <!-- 新建文件夹弹窗 -->
        <n-modal v-model:show="showMkdirModal" preset="dialog" title="新建文件夹" positive-text="创建" negative-text="取消" :loading="creatingFolder" @positive-click="handleCreateFolder">
          <n-input v-model:value="newFolderName" placeholder="输入文件夹名称" autofocus @keydown.enter="handleCreateFolder" />
        </n-modal>

        <!-- 新建文件弹窗 -->
        <n-modal v-model:show="showNewFileModal" preset="dialog" title="新建文件" positive-text="创建并编辑" negative-text="取消" @positive-click="handleCreateFile">
          <n-input v-model:value="newFileName" placeholder="例如: new_note.md" autofocus @keydown.enter="handleCreateFile" />
        </n-modal>

        <!-- 重命名弹窗 -->
        <n-modal v-model:show="showRenameModal" preset="dialog" title="重命名" positive-text="确定" negative-text="取消" :loading="renaming" @positive-click="handleConfirmRename">
          <n-input v-model:value="renameNewName" placeholder="输入新名称" autofocus @keydown.enter="handleConfirmRename" />
        </n-modal>

        <!-- 文本在线编辑/预览弹窗 -->
        <n-modal v-model:show="showEditorModal" preset="card" :title="'编辑: ' + editorFileName" style="width: 800px; max-width: 90vw;">
          <n-spin :show="editorLoading">
            <n-space vertical :size="12">
              <div style="display: flex; justify-content: space-between; align-items: center;">
                <n-tag type="info" size="small">{{ editorFilePath }}</n-tag>
                <n-text depth="3" style="font-size: 12px;">（支持 Markdown / 纯文本，上限 2MB）</n-text>
              </div>
              <n-input
                v-model:value="editorContent"
                type="textarea"
                placeholder="输入文本内容..."
                :autosize="{ minRows: 16, maxRows: 26 }"
                style="font-family: Consolas, 'Fira Code', Monaco, monospace; font-size: 13px;"
              />
              <n-space justify="end">
                <n-button @click="showEditorModal = false">取消</n-button>
                <n-button type="primary" :loading="editorSaving" @click="handleSaveContent">保存文件</n-button>
              </n-space>
            </n-space>
          </n-spin>
        </n-modal>
      </n-drawer-content>
    </n-drawer>
    `
});
