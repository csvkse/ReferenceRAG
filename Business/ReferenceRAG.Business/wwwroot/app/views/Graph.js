import { defineComponent as _defineComponent } from 'vue';
import { computed, h, onMounted, onUnmounted, ref, watch } from 'vue';
import { NIcon, NPopconfirm, NTag, useMessage } from 'naive-ui';
import { RefreshOutline } from '@vicons/ionicons5';
import { graphApi } from '/app/api/index.js';
import { useIndexStore } from '/app/stores/index.js';
const component = /*@__PURE__*/ _defineComponent({
    __name: 'Graph',
    setup(__props, { expose: __expose }) {
        __expose();
        const nodeTypeMeta = {
            document: { label: '文档', tagType: 'info' },
            tag: { label: '标签', tagType: 'success' },
            heading: { label: '标题', tagType: 'warning' },
            external: { label: '外链', tagType: 'error' }
        };
        const nodeTypeOptions = [
            { label: '文档', value: 'document' },
            { label: '标签', value: 'tag' },
            { label: '标题', value: 'heading' },
            { label: '外链', value: 'external' }
        ];
        const message = useMessage();
        const indexStore = useIndexStore();
        const searchQuery = ref('');
        const nodeId = ref('');
        const depth = ref(1);
        const searching = ref(false);
        const traversing = ref(false);
        const statsLoading = ref(false);
        const rebuilding = ref(false);
        const selectedNodeTypes = ref(nodeTypeOptions.map((option) => option.value));
        let rebuildPollTimer = null;
        const searchResults = ref([]);
        const traversalResult = ref(null);
        const stats = ref({
            nodeCount: 0,
            docCount: 0,
            tagCount: 0,
            headingCount: 0,
            externalCount: 0,
            edgeCount: 0
        });
        const filteredSearchResults = computed(() => searchResults.value.filter((node) => selectedNodeTypes.value.includes(node.type)));
        const filteredTraversalNodes = computed(() => traversalResult.value?.nodes.filter((node) => selectedNodeTypes.value.includes(node.type)) ?? []);
        const normalizeNodeId = (id) => id.replace(/\\/g, '/');
        const getShortNodeId = (id) => {
            const [pathPart, heading] = id.split('#', 2);
            const segments = normalizeNodeId(pathPart).split('/').filter(Boolean);
            const shortPath = segments.slice(Math.max(segments.length - 2, 0)).join('/');
            const base = shortPath || pathPart;
            return heading ? `${base}#${heading}` : base;
        };
        const renderNodeIdCell = (id) => h('span', {
            title: id,
            class: 'graph-node-id'
        }, getShortNodeId(id));
        const nodeColumns = [
            { title: 'ID', key: 'id', render: (row) => renderNodeIdCell(row.id) },
            { title: '标题', key: 'title' },
            {
                title: '类型',
                key: 'type',
                width: 100,
                render: (row) => {
                    const meta = nodeTypeMeta[row.type] ?? { label: row.type, tagType: 'default' };
                    return h(NTag, { size: 'small', type: meta.tagType }, () => meta.label);
                }
            },
            {
                title: '操作',
                key: 'actions',
                width: 80,
                render: (row) => h('a', {
                    style: 'cursor:pointer;color:#63e2b7',
                    onClick: () => {
                        nodeId.value = row.id;
                        loadNeighbors(row.id);
                    }
                }, '遍历')
            }
        ];
        const edgeColumns = [
            { title: '来源', key: 'fromId', render: (row) => renderNodeIdCell(row.fromId) },
            { title: '目标', key: 'toId', render: (row) => renderNodeIdCell(row.toId) },
            {
                title: '类型',
                key: 'type',
                width: 100,
                render: (row) => {
                    const typeMap = {
                        wikilink: 'info',
                        embed: 'warning',
                        tag: 'success'
                    };
                    return h(NTag, { size: 'small', type: typeMap[row.type] ?? 'default' }, () => row.type);
                }
            },
            { title: '行号', key: 'lineNumber', width: 70 }
        ];
        const searchNodes = async () => {
            if (!searchQuery.value.trim())
                return;
            searching.value = true;
            try {
                const res = await graphApi.search(searchQuery.value.trim());
                searchResults.value = res.data;
            }
            catch {
                message.error('搜索失败');
            }
            finally {
                searching.value = false;
            }
        };
        const loadNeighbors = async (id) => {
            if (!id.trim())
                return;
            traversing.value = true;
            traversalResult.value = null;
            try {
                const res = await graphApi.neighbors(id.trim(), depth.value);
                traversalResult.value = res.data;
            }
            catch {
                message.error('遍历失败，节点可能不存在');
            }
            finally {
                traversing.value = false;
            }
        };
        const loadStats = async () => {
            statsLoading.value = true;
            try {
                const res = await graphApi.stats();
                stats.value = res.data;
            }
            catch {
                // ignore
            }
            finally {
                statsLoading.value = false;
            }
        };
        const startRebuild = async () => {
            rebuilding.value = true;
            try {
                await graphApi.rebuild();
                message.info('图谱重建已启动');
                rebuildPollTimer = setInterval(async () => {
                    try {
                        const res = await graphApi.rebuildStatus();
                        if (!res.data.isRebuilding) {
                            clearInterval(rebuildPollTimer);
                            rebuildPollTimer = null;
                            rebuilding.value = false;
                            await loadStats();
                            message.success('图谱重建完成');
                        }
                    }
                    catch {
                        // ignore poll errors
                    }
                }, 3000);
            }
            catch (err) {
                rebuilding.value = false;
                message.error(err.response?.data?.error || '重建失败');
            }
        };
        watch(() => indexStore.isIndexing, (now, prev) => {
            if (prev === true && now === false)
                loadStats();
        });
        onMounted(async () => {
            await indexStore.connect();
            loadStats();
        });
        onUnmounted(() => {
            if (rebuildPollTimer)
                clearInterval(rebuildPollTimer);
        });
        const __returned__ = { nodeTypeMeta, nodeTypeOptions, message, indexStore, searchQuery, nodeId, depth, searching, traversing, statsLoading, rebuilding, selectedNodeTypes, get rebuildPollTimer() { return rebuildPollTimer; }, set rebuildPollTimer(v) { rebuildPollTimer = v; }, searchResults, traversalResult, stats, filteredSearchResults, filteredTraversalNodes, normalizeNodeId, getShortNodeId, renderNodeIdCell, nodeColumns, edgeColumns, searchNodes, loadNeighbors, loadStats, startRebuild, get NIcon() { return NIcon; }, get NPopconfirm() { return NPopconfirm; }, get NTag() { return NTag; }, get RefreshOutline() { return RefreshOutline; } };
        return __returned__;
    }
});

component.template = "\n  <div class=\"graph-view\">\n    <n-space vertical size=\"large\">\n      <n-card :bordered=\"false\" style=\"padding: 0\">\n        <n-space justify=\"space-between\" align=\"center\">\n          <n-grid :cols=\"6\" :x-gap=\"12\" style=\"flex: 1\">\n            <n-grid-item>\n              <n-statistic label=\"节点总数\" :value=\"stats.nodeCount\" />\n            </n-grid-item>\n            <n-grid-item>\n              <n-statistic label=\"文档节点\" :value=\"stats.docCount\" />\n            </n-grid-item>\n            <n-grid-item>\n              <n-statistic label=\"标签节点\" :value=\"stats.tagCount\" />\n            </n-grid-item>\n            <n-grid-item>\n              <n-statistic label=\"标题节点\" :value=\"stats.headingCount\" />\n            </n-grid-item>\n            <n-grid-item>\n              <n-statistic label=\"外链节点\" :value=\"stats.externalCount\" />\n            </n-grid-item>\n            <n-grid-item>\n              <n-statistic label=\"边数\" :value=\"stats.edgeCount\" />\n            </n-grid-item>\n          </n-grid>\n          <n-space>\n            <n-button\n              circle\n              :loading=\"statsLoading\"\n              @click=\"loadStats\"\n              title=\"刷新统计\"\n            >\n              <template #icon><n-icon :component=\"RefreshOutline\" /></template>\n            </n-button>\n            <n-popconfirm @positive-click=\"startRebuild\">\n              <template #trigger>\n                <n-button\n                  type=\"warning\"\n                  size=\"small\"\n                  :loading=\"rebuilding\"\n                  :disabled=\"rebuilding\"\n                >\n                  {{ rebuilding ? '重建中…' : '重建图谱' }}\n                </n-button>\n              </template>\n              重建将重新扫描所有文档的 wiki-link，无需 GPU，通常需要几十秒。确认吗？\n            </n-popconfirm>\n          </n-space>\n        </n-space>\n        <n-text v-if=\"rebuilding\" type=\"warning\" style=\"font-size: 12px; margin-top: 4px\">\n          图谱重建中，完成后自动刷新统计…\n        </n-text>\n        <n-text v-else-if=\"indexStore.isIndexing\" type=\"warning\" style=\"font-size: 12px; margin-top: 4px\">\n          索引正在进行，完成后将自动刷新…\n        </n-text>\n      </n-card>\n\n      <n-card title=\"节点查找与遍历\">\n        <n-space vertical>\n          <n-input-group>\n            <n-input\n              v-model:value=\"searchQuery\"\n              placeholder=\"按标题搜索节点…\"\n              clearable\n              @keyup.enter=\"searchNodes\"\n            />\n            <n-button type=\"primary\" @click=\"searchNodes\" :loading=\"searching\">搜索</n-button>\n          </n-input-group>\n\n          <n-space vertical size=\"small\">\n            <div class=\"filter-row\">\n              <n-text depth=\"2\" class=\"filter-label\">节点类型</n-text>\n              <n-checkbox-group v-model:value=\"selectedNodeTypes\">\n                <n-space wrap size=\"small\">\n                  <n-checkbox\n                    v-for=\"option in nodeTypeOptions\"\n                    :key=\"option.value\"\n                    :value=\"option.value\"\n                  >\n                    {{ option.label }}\n                  </n-checkbox>\n                </n-space>\n              </n-checkbox-group>\n            </div>\n          </n-space>\n\n          <n-list v-if=\"filteredSearchResults.length\" bordered>\n            <n-list-item v-for=\"node in filteredSearchResults\" :key=\"node.id\">\n              <n-space justify=\"space-between\" align=\"center\" style=\"width: 100%\">\n                <div class=\"graph-node-summary\">\n                  <n-space align=\"center\" size=\"small\" wrap>\n                    <n-text strong>{{ node.title || getShortNodeId(node.id) }}</n-text>\n                    <n-tag size=\"small\" :type=\"nodeTypeMeta[node.type]?.tagType ?? 'default'\">\n                      {{ nodeTypeMeta[node.type]?.label ?? node.type }}\n                    </n-tag>\n                  </n-space>\n                  <n-text depth=\"3\" class=\"graph-node-id\" :title=\"node.id\">\n                    {{ getShortNodeId(node.id) }}\n                  </n-text>\n                </div>\n                <n-button size=\"small\" @click=\"loadNeighbors(node.id)\">遍历邻居</n-button>\n              </n-space>\n            </n-list-item>\n          </n-list>\n\n          <n-empty\n            v-else-if=\"searchResults.length && !filteredSearchResults.length\"\n            description=\"当前筛选条件下没有搜索结果\"\n          />\n\n          <n-divider>或直接输入节点 ID</n-divider>\n          <n-input-group>\n            <n-input\n              v-model:value=\"nodeId\"\n              placeholder=\"节点 ID（如 Projects/foo.md）\"\n              clearable\n            />\n            <n-input-number\n              v-model:value=\"depth\"\n              :min=\"1\"\n              :max=\"3\"\n              style=\"width: 80px\"\n              placeholder=\"深度\"\n            />\n            <n-button type=\"primary\" @click=\"loadNeighbors(nodeId)\" :loading=\"traversing\">遍历</n-button>\n          </n-input-group>\n        </n-space>\n      </n-card>\n\n      <n-card\n        v-if=\"traversalResult\"\n        :title=\"`遍历结果 - 根节点: ${getShortNodeId(traversalResult.rootId)}`\"\n      >\n        <n-space vertical size=\"small\" style=\"margin-bottom: 12px\">\n          <n-text depth=\"3\" class=\"graph-node-id\" :title=\"traversalResult.rootId\">\n            完整路径：{{ traversalResult.rootId }}\n          </n-text>\n          <div class=\"filter-row\">\n            <n-text depth=\"2\" class=\"filter-label\">节点类型</n-text>\n            <n-checkbox-group v-model:value=\"selectedNodeTypes\">\n              <n-space wrap size=\"small\">\n                <n-checkbox\n                  v-for=\"option in nodeTypeOptions\"\n                  :key=\"`traversal-${option.value}`\"\n                  :value=\"option.value\"\n                >\n                  {{ option.label }}\n                </n-checkbox>\n              </n-space>\n            </n-checkbox-group>\n          </div>\n        </n-space>\n\n        <n-tabs type=\"segment\">\n          <n-tab-pane name=\"nodes\" :tab=\"`节点 (${filteredTraversalNodes.length}/${traversalResult.nodes.length})`\">\n            <n-data-table\n              :columns=\"nodeColumns\"\n              :data=\"filteredTraversalNodes\"\n              :pagination=\"{ pageSize: 20 }\"\n              size=\"small\"\n            />\n          </n-tab-pane>\n\n          <n-tab-pane name=\"edges\" :tab=\"`边 (${traversalResult.edges.length})`\">\n            <n-data-table\n              :columns=\"edgeColumns\"\n              :data=\"traversalResult.edges\"\n              :pagination=\"{ pageSize: 20 }\"\n              size=\"small\"\n            />\n          </n-tab-pane>\n        </n-tabs>\n      </n-card>\n\n      <n-empty v-else-if=\"!traversing && !searching\" description=\"搜索节点或输入节点 ID 开始遍历知识图谱\" />\n    </n-space>\n  </div>\n";
component.__scopeId = "data-v-6f979cfa";
export default component;
