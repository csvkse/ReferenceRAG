import { defineComponent as _defineComponent } from 'vue';
import { ref, onMounted, h } from 'vue';
import { useRouter } from 'vue-router';
import { NTag, NButton } from 'naive-ui';
import { FolderOutline, DocumentTextOutline, GridOutline, SearchOutline, PlayOutline } from '@vicons/ionicons5';
import { indexApi, sourcesApi, indexJobsApi } from '/app/api/index.js';
import { useIndexStore } from '/app/stores/index.js';
const component = /*@__PURE__*/ _defineComponent({
    __name: 'Dashboard',
    setup(__props, { expose: __expose }) {
        __expose();
        const router = useRouter();
        const indexStore = useIndexStore();
        const stats = ref(null);
        const sources = ref([]);
        const loadingSources = ref(false);
        const isIndexing = ref(false);
        const sourceColumns = [
            { title: '名称', key: 'name' },
            { title: '路径', key: 'path', ellipsis: { tooltip: true } },
            {
                title: '状态',
                key: 'enabled',
                width: 80,
                render: (row) => h(NTag, { type: row.enabled ? 'success' : 'default', size: 'small' }, {
                    default: () => row.enabled ? '启用' : '停用'
                })
            },
            { title: '文件数', key: 'fileCount', width: 80 },
            { title: '分块数', key: 'chunkCount', width: 80 }
        ];
        const loadStats = async () => {
            try {
                const response = await indexApi.getSummary();
                stats.value = response.data;
            }
            catch (error) {
                console.error('Failed to load stats:', error);
            }
        };
        const loadSources = async () => {
            loadingSources.value = true;
            try {
                const response = await sourcesApi.getAll();
                sources.value = response.data;
            }
            catch (error) {
                console.error('Failed to load sources:', error);
            }
            finally {
                loadingSources.value = false;
            }
        };
        const handleStartIndex = async () => {
            isIndexing.value = true;
            try {
                await indexJobsApi.startJob();
            }
            catch (error) {
                console.error('Failed to start index:', error);
            }
            finally {
                isIndexing.value = false;
            }
        };
        onMounted(async () => {
            await Promise.all([loadStats(), loadSources()]);
            await indexStore.connect();
        });
        const __returned__ = { router, indexStore, stats, sources, loadingSources, isIndexing, sourceColumns, loadStats, loadSources, handleStartIndex, get NButton() { return NButton; }, get FolderOutline() { return FolderOutline; }, get DocumentTextOutline() { return DocumentTextOutline; }, get GridOutline() { return GridOutline; }, get SearchOutline() { return SearchOutline; }, get PlayOutline() { return PlayOutline; } };
        return __returned__;
    }
});

component.template = "\n  <n-space vertical :size=\"20\">\n    <!-- Stats Cards -->\n    <n-grid :cols=\"4\" :x-gap=\"20\" :y-gap=\"20\">\n      <n-gi>\n        <n-card>\n          <n-statistic label=\"源数量\" :value=\"stats?.sourceCount || 0\">\n            <template #prefix>\n              <n-icon :component=\"FolderOutline\" />\n            </template>\n          </n-statistic>\n        </n-card>\n      </n-gi>\n      <n-gi>\n        <n-card>\n          <n-statistic label=\"文件数量\" :value=\"stats?.totalFiles || 0\">\n            <template #prefix>\n              <n-icon :component=\"DocumentTextOutline\" />\n            </template>\n          </n-statistic>\n        </n-card>\n      </n-gi>\n      <n-gi>\n        <n-card>\n          <n-statistic label=\"分块数量\" :value=\"stats?.totalChunks || 0\">\n            <template #prefix>\n              <n-icon :component=\"GridOutline\" />\n            </template>\n          </n-statistic>\n        </n-card>\n      </n-gi>\n      <n-gi>\n        <n-card>\n          <n-statistic label=\"平均查询时间\" :value=\"(stats?.avgQueryTime || 0).toFixed(1)\">\n            <template #prefix>\n              <n-icon :component=\"SearchOutline\" />\n            </template>\n            <template #suffix>ms</template>\n          </n-statistic>\n        </n-card>\n      </n-gi>\n    </n-grid>\n\n    <!-- Quick Actions -->\n    <n-card title=\"快速操作\">\n      <n-space>\n        <n-button type=\"primary\" @click=\"handleStartIndex\" :loading=\"isIndexing\">\n          <template #icon><n-icon :component=\"PlayOutline\" /></template>\n          开始索引\n        </n-button>\n        <n-button @click=\"router.push('/search')\">\n          <template #icon><n-icon :component=\"SearchOutline\" /></template>\n          向量搜索\n        </n-button>\n        <n-button @click=\"router.push('/sources')\">\n          <template #icon><n-icon :component=\"FolderOutline\" /></template>\n          管理源\n        </n-button>\n      </n-space>\n    </n-card>\n\n    <!-- Index Progress -->\n    <n-card v-if=\"indexStore.progressUpdates.length > 0\" title=\"索引进度\">\n      <n-list>\n        <n-list-item v-for=\"progress in indexStore.progressUpdates\" :key=\"progress.sourceId\">\n          <n-thing :title=\"progress.sourceName\">\n            <template #description>\n              <n-space vertical>\n                <n-progress\n                  type=\"line\"\n                  :percentage=\"Math.round((progress.processedFiles / progress.totalFiles) * 100)\"\n                  :status=\"progress.status === 'failed' ? 'error' : progress.status === 'completed' ? 'success' : 'default'\"\n                />\n                <n-text depth=\"3\">{{ progress.currentFile }}</n-text>\n                <n-text v-if=\"progress.error\" type=\"error\">{{ progress.error }}</n-text>\n              </n-space>\n            </template>\n          </n-thing>\n        </n-list-item>\n      </n-list>\n    </n-card>\n\n    <!-- Sources List -->\n    <n-card title=\"源列表\">\n      <template #header-extra>\n        <n-button text @click=\"router.push('/sources')\">\n          查看全部\n        </n-button>\n      </template>\n      <n-data-table\n        :columns=\"sourceColumns\"\n        :data=\"sources\"\n        :loading=\"loadingSources\"\n        :bordered=\"false\"\n      />\n    </n-card>\n\n    <!-- System Status -->\n    <n-card title=\"系统状态\">\n      <n-descriptions :column=\"3\" label-placement=\"left\">\n        <n-descriptions-item label=\"文件总数\">\n          {{ stats?.totalFiles || 0 }}\n        </n-descriptions-item>\n        <n-descriptions-item label=\"分块总数\">\n          {{ stats?.totalChunks || 0 }}\n        </n-descriptions-item>\n        <n-descriptions-item label=\"平均查询时间\">\n          {{ (stats?.avgQueryTime || 0).toFixed(2) }} ms\n        </n-descriptions-item>\n      </n-descriptions>\n    </n-card>\n  </n-space>\n";
export default component;
