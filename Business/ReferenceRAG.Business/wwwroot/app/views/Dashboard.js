import { defineComponent as _defineComponent } from 'vue';
import { ref, computed, onMounted, onUnmounted, onActivated, h } from 'vue';
import { useRouter } from 'vue-router';
import { NTag, NButton, NSpace, NCard, NGrid, NGi, NStatistic, NIcon, NProgress, NList, NListItem, NThing, NText, NDataTable, NDescriptions, NDescriptionsItem, useMessage } from 'naive-ui';
import { LayersOutline, FolderOutline, DocumentTextOutline, GridOutline, SearchOutline, AddOutline, ChatbubblesOutline } from '@vicons/ionicons5';
import { knowledgeBasesApi, indexApi } from '/app/api/index.js';
import { useIndexStore } from '/app/stores/index.js';
import { JobCenter } from '/core/jobs/JobCenter.js';

const component = /*@__PURE__*/ _defineComponent({
    ...{ name: 'Dashboard' },
    __name: 'Dashboard',
    setup(__props, { expose: __expose }) {
        __expose();
        const router = useRouter();
        const message = useMessage();
        const indexStore = useIndexStore();

        const stats = ref(null);
        const knowledgeBases = ref([]);
        const connectorsMap = ref({});
        const loadingKbs = ref(false);

        const totalConnectors = computed(() => {
            return Object.values(connectorsMap.value).reduce((sum, list) => sum + (list?.length || 0), 0);
        });

        const kbColumns = [
            {
                title: '知识库名称',
                key: 'name',
                render: (row) => h('span', { style: 'font-weight: 600;' }, row.name)
            },
            {
                title: '嵌入模型规范',
                key: 'embeddingSpecId',
                render: (row) => h(NTag, { type: 'info', size: 'small', secondary: true }, {
                    default: () => row.embeddingSpecId || '默认规范'
                })
            },
            {
                title: '挂载连接器',
                key: 'connectorCount',
                width: 120,
                render: (row) => {
                    const count = connectorsMap.value[row.id]?.length ?? 0;
                    return h(NTag, { size: 'small', round: true }, {
                        default: () => `📁 ${count} 个目录`
                    });
                }
            },
            {
                title: '状态',
                key: 'enabled',
                width: 90,
                render: (row) => h(NTag, { type: row.enabled !== false ? 'success' : 'default', size: 'small' }, {
                    default: () => row.enabled !== false ? '就绪' : '停用'
                })
            },
            {
                title: '操作',
                key: 'actions',
                width: 180,
                render: (row) => h(NSpace, { size: 'small' }, {
                    default: () => [
                        h(NButton, {
                            size: 'small',
                            type: 'primary',
                            secondary: true,
                            onClick: () => router.push('/search')
                        }, {
                            default: () => '去检索'
                        }),
                        h(NButton, {
                            size: 'small',
                            onClick: () => router.push('/knowledge-bases')
                        }, {
                            default: () => '管理'
                        })
                    ]
                })
            }
        ];

        const loadStats = async () => {
            try {
                const response = await indexApi.getSummary();
                stats.value = response.data;
            }
            catch (error) {
                console.warn('加载检索性能数据失败:', error);
            }
        };

        const loadKnowledgeBases = async (silent = false) => {
            if (!silent) loadingKbs.value = true;
            try {
                const response = await knowledgeBasesApi.getAll();
                knowledgeBases.value = response.data || [];

                const map = {};
                await Promise.all(knowledgeBases.value.map(async (kb) => {
                    try {
                        const connRes = await knowledgeBasesApi.getConnectors(kb.id);
                        map[kb.id] = connRes.data || [];
                    } catch (e) {
                        map[kb.id] = [];
                    }
                }));
                connectorsMap.value = map;
            }
            catch (error) {
                console.error('加载知识库列表失败:', error);
            }
            finally {
                if (!silent) loadingKbs.value = false;
            }
        };

        let unbindDashboardStats = null;
        onMounted(async () => {
            await Promise.all([loadStats(), loadKnowledgeBases()]);
            await indexStore.connect();
            try {
                await JobCenter.initialize();
                unbindDashboardStats = JobCenter.registerRefresher('dashboard_stats', async () => {
                    await Promise.all([loadStats(), loadKnowledgeBases(true)]);
                });
            } catch (err) {
                console.warn('JobCenter 初始化失败:', err);
            }
        });

        onUnmounted(() => {
            unbindDashboardStats?.();
        });

        onActivated(async () => {
            await Promise.all([loadStats(), loadKnowledgeBases(true)]);
        });

        const __returned__ = {
            router, message, indexStore, stats, knowledgeBases, connectorsMap, loadingKbs,
            totalConnectors, kbColumns, loadStats, loadKnowledgeBases,
            get NButton() { return NButton; },
            get NSpace() { return NSpace; },
            get NCard() { return NCard; },
            get NGrid() { return NGrid; },
            get NGi() { return NGi; },
            get NStatistic() { return NStatistic; },
            get NIcon() { return NIcon; },
            get NProgress() { return NProgress; },
            get NList() { return NList; },
            get NListItem() { return NListItem; },
            get NThing() { return NThing; },
            get NText() { return NText; },
            get NDataTable() { return NDataTable; },
            get NDescriptions() { return NDescriptions; },
            get NDescriptionsItem() { return NDescriptionsItem; },
            get LayersOutline() { return LayersOutline; },
            get FolderOutline() { return FolderOutline; },
            get DocumentTextOutline() { return DocumentTextOutline; },
            get GridOutline() { return GridOutline; },
            get SearchOutline() { return SearchOutline; },
            get AddOutline() { return AddOutline; },
            get ChatbubblesOutline() { return ChatbubblesOutline; }
        };
        return __returned__;
    }
});

component.template = `
  <n-space vertical :size="20">
    <!-- Stats Cards -->
    <n-grid :cols="4" :x-gap="20" :y-gap="20">
      <n-gi>
        <n-card>
          <n-statistic label="知识库总数" :value="knowledgeBases?.length || 0">
            <template #prefix>
              <n-icon :component="LayersOutline" />
            </template>
          </n-statistic>
        </n-card>
      </n-gi>
      <n-gi>
        <n-card>
          <n-statistic label="已挂载连接器" :value="totalConnectors || 0">
            <template #prefix>
              <n-icon :component="FolderOutline" />
            </template>
          </n-statistic>
        </n-card>
      </n-gi>
      <n-gi>
        <n-card>
          <n-statistic label="总文件索引数" :value="stats?.totalFiles || 0">
            <template #prefix>
              <n-icon :component="DocumentTextOutline" />
            </template>
          </n-statistic>
        </n-card>
      </n-gi>
      <n-gi>
        <n-card>
          <n-statistic label="平均检索时间" :value="(stats?.avgQueryTime || 0).toFixed(1)">
            <template #prefix>
              <n-icon :component="SearchOutline" />
            </template>
            <template #suffix>ms</template>
          </n-statistic>
        </n-card>
      </n-gi>
    </n-grid>

    <!-- Quick Actions -->
    <n-card title="快速操作">
      <n-space>
        <n-button type="primary" @click="router.push('/knowledge-bases')">
          <template #icon><n-icon :component="LayersOutline" /></template>
          管理知识库
        </n-button>
        <n-button @click="router.push('/search')">
          <template #icon><n-icon :component="SearchOutline" /></template>
          多库检索 (RRF)
        </n-button>
        <n-button @click="router.push('/chat')">
          <template #icon><n-icon :component="ChatbubblesOutline" /></template>
          AI 对话
        </n-button>
        <n-button secondary @click="router.push('/sources')">
          <template #icon><n-icon :component="FolderOutline" /></template>
          底层数据源
        </n-button>
      </n-space>
    </n-card>

    <!-- Index Progress -->
    <n-card v-if="indexStore.progressUpdates.length > 0" title="任务执行进度">
      <n-list>
        <n-list-item v-for="progress in indexStore.progressUpdates" :key="progress.indexId">
          <n-thing :title="'任务 ' + progress.indexId">
            <template #description>
              <n-space vertical>
                <n-progress
                  type="line"
                  :percentage="progress.progressPercent !== undefined ? Math.round(progress.progressPercent) : (progress.totalFiles > 0 ? Math.round((progress.processedFiles / progress.totalFiles) * 100) : 0)"
                  status="default"
                />
                <n-text depth="3">{{ progress.currentFile }}</n-text>
              </n-space>
            </template>
          </n-thing>
        </n-list-item>
      </n-list>
    </n-card>

    <!-- Knowledge Bases Summary Table -->
    <n-card title="知识库概览">
      <template #header-extra>
        <n-button text @click="router.push('/knowledge-bases')">
          查看全部
        </n-button>
      </template>
      <n-data-table
        :columns="kbColumns"
        :data="knowledgeBases"
        :loading="loadingKbs"
        :bordered="false"
        :row-key="(row) => row.id"
      />
    </n-card>

    <!-- System Status -->
    <n-card title="系统概况">
      <n-descriptions :column="3" label-placement="left">
        <n-descriptions-item label="活跃知识库">
          {{ knowledgeBases?.length || 0 }} 个
        </n-descriptions-item>
        <n-descriptions-item label="挂载数据连接器">
          {{ totalConnectors || 0 }} 个
        </n-descriptions-item>
        <n-descriptions-item label="平均查询延迟">
          {{ (stats?.avgQueryTime || 0).toFixed(2) }} ms
        </n-descriptions-item>
      </n-descriptions>
    </n-card>
  </n-space>
`;

export default component;
