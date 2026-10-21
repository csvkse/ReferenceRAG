import { defineComponent as _defineComponent, ref, computed, watch, onMounted } from 'vue';
import { NModal, NCard, NSpace, NButton, NIcon, NInput, NAlert, NSpin, NText, NEmpty, NTag, useMessage } from 'naive-ui';
import { RefreshOutline } from '@vicons/ionicons5';
import { fileSystemApi } from '/app/api/index.js';

export function buildFileBreadcrumbs(path) {
    if (!path || path === '/') {
        return [{ name: '根目录', path: '/' }];
    }
    const nativeRoot = path.match(/^[a-zA-Z]:\//)?.[0] || path.match(/^\/\/[^/]+\/[^/]+\//)?.[0];
    const root = nativeRoot || '/';
    const parts = [{ name: nativeRoot ? '磁盘' : '/', path: '/' }];
    if (nativeRoot) {
        parts.push({ name: root, path: root });
    }
    let current = root.replace(/\/$/, '');
    for (const name of path.slice(root.length).split('/').filter(Boolean)) {
        current += '/' + name;
        parts.push({ name, path: current });
    }
    return parts;
}

const component = /*@__PURE__*/ _defineComponent({
    name: 'FolderPickerModal',
    props: {
        show: { type: Boolean, default: false },
        initialPath: { type: String, default: '' },
        title: { type: String, default: '选择服务器文件夹' }
    },
    emits: ['update:show', 'select'],
    setup(props, { emit }) {
        const message = useMessage();
        const show = computed(() => props.show);
        const title = computed(() => props.title);
        const currentPath = ref(props.initialPath || '/');
        const entries = ref([]);
        const loading = ref(false);
        const error = ref('');
        const virtualRoot = ref(false);
        const breadcrumbs = ref([]);
        const showNewFolderInput = ref(false);
        const newFolderName = ref('');
        const creatingFolder = ref(false);

        const loadDirectory = async (target = currentPath.value) => {
            loading.value = true;
            error.value = '';
            try {
                const response = await fileSystemApi.browse(target, true);
                const data = response.data;
                if (data && Array.isArray(data.entries)) {
                    entries.value = data.entries;
                    currentPath.value = data.path;
                    virtualRoot.value = data.isVirtualRoot === true;
                    breadcrumbs.value = buildFileBreadcrumbs(data.path);
                    return;
                }
                error.value = '目录列表数据格式无效';
            } catch (err) {
                error.value = err.response?.data?.error || err.message || '目录加载失败';
            } finally {
                loading.value = false;
            }
        };

        const goPath = (path) => {
            loadDirectory(path);
        };

        const goUp = () => {
            if (breadcrumbs.value.length > 1) {
                const target = breadcrumbs.value[breadcrumbs.value.length - 2].path;
                goPath(target);
            }
        };

        const openEntry = (entry) => {
            if (entry.isDirectory) {
                goPath(entry.path);
            }
        };

        const selectEntry = (entry) => {
            emit('select', entry.path);
            emit('update:show', false);
        };

        const confirmCurrent = () => {
            if (loading.value || error.value || virtualRoot.value || !currentPath.value || currentPath.value === '/') {
                return;
            }
            emit('select', currentPath.value);
            emit('update:show', false);
        };

        const close = () => {
            showNewFolderInput.value = false;
            newFolderName.value = '';
            emit('update:show', false);
        };

        const cancelCreateFolder = () => {
            showNewFolderInput.value = false;
            newFolderName.value = '';
        };

        const handleCreateFolder = async () => {
            const name = (newFolderName.value || '').trim();
            if (!name) {
                message.warning('请输入新文件夹名称');
                return;
            }
            if (name.includes('/') || name.includes('\\') || name.includes('..')) {
                message.warning('文件夹名称不能包含斜杠或目录穿越字符');
                return;
            }
            const targetPath = currentPath.value.replace(/\/$/, '') + '/' + name;
            creatingFolder.value = true;
            try {
                await fileSystemApi.mkdir(targetPath);
                message.success(`文件夹 "${name}" 创建成功`);
                showNewFolderInput.value = false;
                newFolderName.value = '';
                await loadDirectory(currentPath.value);
            } catch (err) {
                message.error(err.response?.data?.error || err.message || '创建文件夹失败');
            } finally {
                creatingFolder.value = false;
            }
        };

        const formatTime = (t) => {
            if (!t || t.startsWith('0001')) return '-';
            const d = new Date(t);
            const pad = (x) => String(x).padStart(2, '0');
            return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}`;
        };

        watch(() => props.show, (newVal) => {
            if (newVal) {
                showNewFolderInput.value = false;
                newFolderName.value = '';
                const start = props.initialPath ? props.initialPath : (currentPath.value || '/');
                loadDirectory(start);
            }
        });

        onMounted(() => {
            if (props.show) {
                loadDirectory(props.initialPath || '/');
            }
        });

        const __returned__ = {
            show,
            title,
            message,
            currentPath,
            entries,
            loading,
            error,
            virtualRoot,
            breadcrumbs,
            showNewFolderInput,
            newFolderName,
            creatingFolder,
            loadDirectory,
            goPath,
            goUp,
            openEntry,
            selectEntry,
            confirmCurrent,
            close,
            cancelCreateFolder,
            handleCreateFolder,
            formatTime,
            get RefreshOutline() { return RefreshOutline; },
            get NModal() { return NModal; },
            get NCard() { return NCard; },
            get NSpace() { return NSpace; },
            get NButton() { return NButton; },
            get NIcon() { return NIcon; },
            get NInput() { return NInput; },
            get NAlert() { return NAlert; },
            get NSpin() { return NSpin; },
            get NText() { return NText; },
            get NEmpty() { return NEmpty; },
            get NTag() { return NTag; }
        };
        return __returned__;
    }
});

component.template = "\n  <n-modal\n    :show=\"show\"\n    preset=\"card\"\n    :title=\"title\"\n    :style=\"{ width: '750px', maxWidth: '92vw' }\"\n    :segmented=\"{ content: 'soft', footer: 'soft' }\"\n    :mask-closable=\"false\"\n    @update:show=\"close\"\n  >\n    <template #header-extra>\n      <n-space align=\"center\" :size=\"8\">\n        <n-button size=\"tiny\" secondary @click=\"loadDirectory(currentPath)\">\n          <template #icon><n-icon :component=\"RefreshOutline\" /></template>\n          刷新\n        </n-button>\n      </n-space>\n    </template>\n\n    <n-space vertical :size=\"12\">\n      <div style=\"display: flex; align-items: center; justify-content: space-between; gap: 8px; flex-wrap: wrap; background: rgba(128,128,128,0.08); padding: 8px 12px; border-radius: 6px;\">\n        <n-space align=\"center\" :size=\"6\" style=\"flex: 1; min-width: 200px; overflow-x: auto;\">\n          <n-button size=\"tiny\" :disabled=\"!breadcrumbs.length || breadcrumbs.length <= 1\" @click=\"goUp\">\n            ⬆ 上一级\n          </n-button>\n          <div style=\"display: flex; align-items: center; gap: 4px; overflow-x: auto; white-space: nowrap; max-width: 520px;\">\n            <template v-for=\"(crumb, idx) in breadcrumbs\" :key=\"crumb.path\">\n              <span v-if=\"idx > 0\" style=\"color: #888; font-size: 12px;\">/</span>\n              <n-button text size=\"tiny\" type=\"primary\" @click=\"goPath(crumb.path)\">\n                {{ crumb.name }}\n              </n-button>\n            </template>\n          </div>\n        </n-space>\n\n        <n-space align=\"center\" :size=\"6\">\n          <n-button v-if=\"!showNewFolderInput\" size=\"tiny\" secondary type=\"info\" :disabled=\"virtualRoot\" @click=\"showNewFolderInput = true\">\n            + 新建文件夹\n          </n-button>\n        </n-space>\n      </div>\n\n      <div v-if=\"showNewFolderInput\" style=\"display: flex; align-items: center; gap: 8px; background: rgba(32,128,240,0.08); padding: 8px 12px; border-radius: 6px;\">\n        <span style=\"font-size: 12px; white-space: nowrap;\">文件夹名称:</span>\n        <n-input v-model:value=\"newFolderName\" size=\"small\" placeholder=\"输入新文件夹名称\" @keyup.enter=\"handleCreateFolder\" style=\"flex: 1;\" />\n        <n-button size=\"small\" type=\"primary\" :loading=\"creatingFolder\" @click=\"handleCreateFolder\">确定</n-button>\n        <n-button size=\"small\" @click=\"cancelCreateFolder\">取消</n-button>\n      </div>\n\n      <n-alert v-if=\"error\" type=\"error\" :show-icon=\"true\" closable @close=\"error = ''\">\n        {{ error }}\n      </n-alert>\n\n      <n-spin :show=\"loading\">\n        <div style=\"max-height: 360px; min-height: 200px; overflow-y: auto; border: 1px solid rgba(128,128,128,0.2); border-radius: 6px;\">\n          <table style=\"width: 100%; border-collapse: collapse; font-size: 13px;\">\n            <thead>\n              <tr style=\"border-bottom: 1px solid rgba(128,128,128,0.2); background: rgba(128,128,128,0.05); text-align: left;\">\n                <th style=\"padding: 8px 12px;\">名称</th>\n                <th style=\"padding: 8px 12px; width: 160px;\">修改时间</th>\n                <th style=\"padding: 8px 12px; width: 80px; text-align: right;\">操作</th>\n              </tr>\n            </thead>\n            <tbody>\n              <tr v-if=\"!entries.length && !loading\">\n                <td colspan=\"3\" style=\"text-align: center; padding: 32px; color: #888;\">\n                  {{ virtualRoot ? '未检测到可用磁盘' : '此目录下没有子文件夹' }}\n                </td>\n              </tr>\n              <tr\n                v-for=\"entry in entries\"\n                :key=\"entry.path\"\n                style=\"border-bottom: 1px solid rgba(128,128,128,0.1); cursor: pointer; transition: background 0.2s;\"\n                @click=\"openEntry(entry)\"\n              >\n                <td style=\"padding: 8px 12px; display: flex; align-items: center; gap: 8px;\">\n                  <span>📁</span>\n                  <span style=\"font-weight: 500;\">{{ entry.name }}</span>\n                </td>\n                <td style=\"padding: 8px 12px; color: #888; font-size: 12px;\">\n                  {{ formatTime(entry.modified) }}\n                </td>\n                <td style=\"padding: 8px 12px; text-align: right;\">\n                  <n-button size=\"tiny\" secondary type=\"success\" @click.stop=\"selectEntry(entry)\">\n                    选择\n                  </n-button>\n                </td>\n              </tr>\n            </tbody>\n          </table>\n        </div>\n      </n-spin>\n    </n-space>\n\n    <template #footer>\n      <div style=\"display: flex; align-items: center; justify-content: space-between; width: 100%; gap: 12px;\">\n        <n-text depth=\"3\" style=\"font-size: 12px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; max-width: 450px;\" :title=\"currentPath\">\n          当前路径：{{ currentPath || '/' }}\n        </n-text>\n        <n-space :size=\"8\">\n          <n-button @click=\"close\">取消</n-button>\n          <n-button type=\"primary\" :disabled=\"loading || virtualRoot || !currentPath || currentPath === '/'\" @click=\"confirmCurrent\">\n            选择当前文件夹\n          </n-button>\n        </n-space>\n      </div>\n    </template>\n  </n-modal>\n";

export default component;
