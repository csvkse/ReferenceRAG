import { defineComponent as _defineComponent } from 'vue';
import { computed, h, onMounted, onUnmounted, ref, watch } from 'vue';
import { NIcon, NPopconfirm, NTag, NSelect, useMessage } from 'naive-ui';
import { RefreshOutline, LayersOutline, AddOutline, TrashOutline } from '@vicons/ionicons5';
import { graphApi, knowledgeBasesApi } from '/app/api/index.js';
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

        // 知识库作用域状态
        const knowledgeBases = ref([]);
        const selectedKbId = ref(null);
        const kbOptions = computed(() => [
            { label: '全部知识库 (聚合统计)', value: '__all__' },
            ...knowledgeBases.value.map((k) => ({
                label: `📚 ${k.name} (${k.embeddingSpecId || '默认规范'})`,
                value: k.id
            }))
        ]);

        const getActiveKbId = () => {
            return selectedKbId.value && selectedKbId.value !== '__all__' ? selectedKbId.value : null;
        };

        const loadKnowledgeBases = async () => {
            try {
                const res = await knowledgeBasesApi.getAll();
                knowledgeBases.value = res.data || [];
                if (!selectedKbId.value && knowledgeBases.value.length > 0) {
                    selectedKbId.value = '__all__';
                }
            } catch (err) {
                console.warn('加载知识库列表失败:', err);
            }
        };

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
                const res = await graphApi.search(searchQuery.value.trim(), 10, getActiveKbId());
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
                const res = await graphApi.neighbors(id.trim(), depth.value, undefined, getActiveKbId());
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
                const res = await graphApi.stats(getActiveKbId());
                stats.value = res.data;
            }
            catch {
                // ignore
            }
            finally {
                statsLoading.value = false;
            }
        };
        // 手动添加实体与关系
        const showAddModal = ref(false);
        const addTargetType = ref('node'); // 'node' | 'edge'
        const newNodeForm = ref({ id: '', title: '', type: 'entity' });
        const newEdgeForm = ref({ fromId: '', toId: '', type: 'related' });
        const savingGraphItem = ref(false);

        const handleCreateNode = async () => {
            if (!newNodeForm.value.id.trim()) {
                message.error('实体 ID 不能为空');
                return;
            }
            savingGraphItem.value = true;
            try {
                await graphApi.createOrUpdateNode({
                    id: newNodeForm.value.id.trim(),
                    title: newNodeForm.value.title?.trim() || newNodeForm.value.id.trim(),
                    type: newNodeForm.value.type || 'entity'
                }, getActiveKbId());
                message.success('实体节点已创建/更新');
                showAddModal.value = false;
                newNodeForm.value = { id: '', title: '', type: 'entity' };
                await loadStats();
            } catch (err) {
                message.error('保存实体失败: ' + (err.response?.data?.error || err.message));
            } finally {
                savingGraphItem.value = false;
            }
        };

        const handleCreateEdge = async () => {
            if (!newEdgeForm.value.fromId.trim() || !newEdgeForm.value.toId.trim()) {
                message.error('源实体与目标实体 ID 均不能为空');
                return;
            }
            savingGraphItem.value = true;
            try {
                await graphApi.createEdge({
                    fromId: newEdgeForm.value.fromId.trim(),
                    toId: newEdgeForm.value.toId.trim(),
                    type: newEdgeForm.value.type?.trim() || 'related'
                }, getActiveKbId());
                message.success('关系边已创建');
                showAddModal.value = false;
                newEdgeForm.value = { fromId: '', toId: '', type: 'related' };
                await loadStats();
                if (traversalResult.value) {
                    await loadNeighbors(traversalResult.value.rootId);
                }
            } catch (err) {
                message.error('创建关系失败: ' + (err.response?.data?.error || err.message));
            } finally {
                savingGraphItem.value = false;
            }
        };

        const handleDeleteNode = async (nodeIdToDelete) => {
            try {
                await graphApi.deleteNode(nodeIdToDelete, getActiveKbId());
                message.success('实体已删除');
                await loadStats();
                if (traversalResult.value && traversalResult.value.rootId === nodeIdToDelete) {
                    traversalResult.value = null;
                } else if (traversalResult.value) {
                    await loadNeighbors(traversalResult.value.rootId);
                }
                if (searchQuery.value) {
                    await searchNodes();
                }
            } catch (err) {
                message.error('删除实体失败: ' + (err.response?.data?.error || err.message));
            }
        };

        const handleDeleteEdge = async (edge) => {
            try {
                await graphApi.deleteEdge(edge.fromId, edge.toId, edge.type, getActiveKbId());
                message.success('关系边已删除');
                await loadStats();
                if (traversalResult.value) {
                    await loadNeighbors(traversalResult.value.rootId);
                }
            } catch (err) {
                message.error('删除边失败: ' + (err.response?.data?.error || err.message));
            }
        };

        const startRebuild = async () => {
            rebuilding.value = true;
            try {
                await graphApi.rebuild(getActiveKbId());
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
        watch(selectedKbId, () => {
            searchResults.value = [];
            traversalResult.value = null;
            loadStats();
        });
        onMounted(async () => {
            await indexStore.connect();
            await loadKnowledgeBases();
            loadStats();
        });
        onUnmounted(() => {
            if (rebuildPollTimer)
                clearInterval(rebuildPollTimer);
        });
        const __returned__ = {
            nodeTypeMeta, nodeTypeOptions, message, indexStore, searchQuery, nodeId, depth,
            searching, traversing, statsLoading, rebuilding, selectedNodeTypes,
            knowledgeBases, selectedKbId, kbOptions, loadKnowledgeBases, getActiveKbId,
            get rebuildPollTimer() { return rebuildPollTimer; }, set rebuildPollTimer(v) { rebuildPollTimer = v; },
            searchResults, traversalResult, stats, filteredSearchResults, filteredTraversalNodes,
            normalizeNodeId, getShortNodeId, renderNodeIdCell, nodeColumns, edgeColumns,
            searchNodes, loadNeighbors, loadStats, startRebuild,
            showAddModal, addTargetType, newNodeForm, newEdgeForm, savingGraphItem,
            handleCreateNode, handleCreateEdge, handleDeleteNode, handleDeleteEdge,
            get AddOutline() { return AddOutline; }, get TrashOutline() { return TrashOutline; },
            get NIcon() { return NIcon; }, get NPopconfirm() { return NPopconfirm; },
            get NTag() { return NTag; }, get NSelect() { return NSelect; },
            get RefreshOutline() { return RefreshOutline; }, get LayersOutline() { return LayersOutline; }
        };
        return __returned__;
    }
});

component.template = `
  <div class="graph-view">
    <n-space vertical size="large">
      <!-- Target KnowledgeBase Selector Bar -->
      <n-card size="small">
        <n-space align="center" justify="space-between">
          <n-space align="center" :size="12">
            <n-icon :component="LayersOutline" size="20" />
            <n-text strong>目标知识库图谱：</n-text>
            <n-select
              v-model:value="selectedKbId"
              :options="kbOptions"
              style="width: 320px;"
              size="small"
              placeholder="选择查看或重建图谱的知识库"
            />
          </n-space>
          <n-tag type="info" size="small" round secondary>
            物理知识库专属图谱存储 (graph_nodes / graph_edges)
          </n-tag>
        </n-space>
      </n-card>

      <n-card :bordered="false" style="padding: 0">
        <n-space justify="space-between" align="center">
          <n-grid :cols="6" :x-gap="12" style="flex: 1">
            <n-grid-item>
              <n-statistic label="节点总数" :value="stats.nodeCount" />
            </n-grid-item>
            <n-grid-item>
              <n-statistic label="文档节点" :value="stats.docCount" />
            </n-grid-item>
            <n-grid-item>
              <n-statistic label="标签节点" :value="stats.tagCount" />
            </n-grid-item>
            <n-grid-item>
              <n-statistic label="标题节点" :value="stats.headingCount" />
            </n-grid-item>
            <n-grid-item>
              <n-statistic label="外链节点" :value="stats.externalCount" />
            </n-grid-item>
            <n-grid-item>
              <n-statistic label="边数" :value="stats.edgeCount" />
            </n-grid-item>
          </n-grid>
          <n-space>
            <n-button type="primary" size="small" @click="showAddModal = true">
              <template #icon><n-icon :component="AddOutline" /></template>
              手动干预实体/关系
            </n-button>
            <n-button
              circle
              :loading="statsLoading"
              @click="loadStats"
              title="刷新统计"
            >
              <template #icon><n-icon :component="RefreshOutline" /></template>
            </n-button>
            <n-popconfirm @positive-click="startRebuild">
              <template #trigger>
                <n-button
                  type="warning"
                  size="small"
                  :loading="rebuilding"
                  :disabled="rebuilding"
                >
                  {{ rebuilding ? '重建中…' : '重建图谱' }}
                </n-button>
              </template>
              重建将重新扫描选定知识库所有文档的 wiki-link，无需 GPU，通常需要几十秒。确认吗？
            </n-popconfirm>
          </n-space>
        </n-space>
        <n-text v-if="rebuilding" type="warning" style="font-size: 12px; margin-top: 4px">
          图谱重建中，完成后自动刷新统计…
        </n-text>
        <n-text v-else-if="indexStore.isIndexing" type="warning" style="font-size: 12px; margin-top: 4px">
          索引正在进行，完成后将自动刷新…
        </n-text>
      </n-card>

      <n-card title="节点查找与遍历">
        <n-space vertical>
          <n-input-group>
            <n-input
              v-model:value="searchQuery"
              placeholder="按标题搜索节点…"
              clearable
              @keyup.enter="searchNodes"
            />
            <n-button type="primary" @click="searchNodes" :loading="searching">搜索</n-button>
          </n-input-group>

          <n-space vertical size="small">
            <div class="filter-row">
              <n-text depth="2" class="filter-label">节点类型</n-text>
              <n-checkbox-group v-model:value="selectedNodeTypes">
                <n-space wrap size="small">
                  <n-checkbox
                    v-for="option in nodeTypeOptions"
                    :key="option.value"
                    :value="option.value"
                  >
                    {{ option.label }}
                  </n-checkbox>
                </n-space>
              </n-checkbox-group>
            </div>
          </n-space>

          <n-list v-if="filteredSearchResults.length" bordered>
            <n-list-item v-for="node in filteredSearchResults" :key="node.id">
              <n-space justify="space-between" align="center" style="width: 100%">
                <div class="graph-node-summary">
                  <n-space align="center" size="small" wrap>
                    <n-text strong>{{ node.title || getShortNodeId(node.id) }}</n-text>
                    <n-tag size="small" :type="nodeTypeMeta[node.type]?.tagType ?? 'default'">
                      {{ nodeTypeMeta[node.type]?.label ?? node.type }}
                    </n-tag>
                  </n-space>
                  <n-text depth="3" class="graph-node-id" :title="node.id">
                    {{ getShortNodeId(node.id) }}
                  </n-text>
                </div>
                <n-space size="small">
                  <n-button size="small" secondary @click="loadNeighbors(node.id)">遍历邻居</n-button>
                  <n-popconfirm @positive-click="handleDeleteNode(node.id)">
                    <template #trigger>
                      <n-button size="small" quaternary type="error">删除实体</n-button>
                    </template>
                    确定要删除该实体节点及其所有关联边吗？
                  </n-popconfirm>
                </n-space>
              </n-space>
            </n-list-item>
          </n-list>

          <n-empty
            v-else-if="searchResults.length && !filteredSearchResults.length"
            description="当前筛选条件下没有搜索结果"
          />

          <n-divider>或直接输入节点 ID</n-divider>
          <n-input-group>
            <n-input
              v-model:value="nodeId"
              placeholder="节点 ID（如 Projects/foo.md）"
              clearable
            />
            <n-input-number
              v-model:value="depth"
              :min="1"
              :max="3"
              style="width: 80px"
              placeholder="深度"
            />
            <n-button type="primary" @click="loadNeighbors(nodeId)" :loading="traversing">遍历</n-button>
          </n-input-group>
        </n-space>
      </n-card>

      <n-card
        v-if="traversalResult"
        :title="\`遍历结果 - 根节点: \${getShortNodeId(traversalResult.rootId)}\`"
      >
        <n-space vertical size="small" style="margin-bottom: 12px">
          <n-text depth="3" class="graph-node-id" :title="traversalResult.rootId">
            完整路径：{{ traversalResult.rootId }}
          </n-text>
          <div class="filter-row">
            <n-text depth="2" class="filter-label">节点类型</n-text>
            <n-checkbox-group v-model:value="selectedNodeTypes">
              <n-space wrap size="small">
                <n-checkbox
                  v-for="option in nodeTypeOptions"
                  :key="\`traversal-\${option.value}\`"
                  :value="option.value"
                >
                  {{ option.label }}
                </n-checkbox>
              </n-space>
            </n-checkbox-group>
          </div>
        </n-space>

        <n-tabs type="segment">
          <n-tab-pane name="nodes" :tab="\`节点 (\${filteredTraversalNodes.length}/\${traversalResult.nodes.length})\`">
            <n-data-table
              :columns="nodeColumns"
              :data="filteredTraversalNodes"
              :pagination="{ pageSize: 20 }"
              size="small"
            />
          </n-tab-pane>

          <n-tab-pane name="edges" :tab="\`边 (\${traversalResult.edges.length})\`">
            <n-data-table
              :columns="edgeColumns"
              :data="traversalResult.edges"
              :pagination="{ pageSize: 20 }"
              size="small"
            />
          </n-tab-pane>
        </n-tabs>
      </n-card>

      <n-empty v-else-if="!traversing && !searching" description="搜索节点或输入节点 ID 开始遍历知识图谱" />
    <!-- 手动干预模态框 -->
    <n-modal v-model:show="showAddModal" preset="card" title="知识图谱人工干预" style="max-width: 520px">
      <n-tabs v-model:value="addTargetType" type="segment" style="margin-bottom: 16px">
        <n-tab-pane name="node" tab="新增/修正实体节点" />
        <n-tab-pane name="edge" tab="新增/修正关系边" />
      </n-tabs>

      <n-form v-if="addTargetType === 'node'" label-placement="left" label-width="90">
        <n-form-item label="实体 ID" required>
          <n-input v-model:value="newNodeForm.id" placeholder="例如 entity_qiaofeng 或 路径" />
        </n-form-item>
        <n-form-item label="实体标题" required>
          <n-input v-model:value="newNodeForm.title" placeholder="例如 乔峰" />
        </n-form-item>
        <n-form-item label="实体类别">
          <n-input v-model:value="newNodeForm.type" placeholder="例如 person, sect, document, concept" />
        </n-form-item>
        <n-space justify="end" style="margin-top: 12px">
          <n-button @click="showAddModal = false">取消</n-button>
          <n-button type="primary" :loading="savingGraphItem" @click="handleCreateNode">保存实体</n-button>
        </n-space>
      </n-form>

      <n-form v-else label-placement="left" label-width="90">
        <n-form-item label="源实体 ID" required>
          <n-input v-model:value="newEdgeForm.fromId" placeholder="例如 entity_qiaofeng" />
        </n-form-item>
        <n-form-item label="目标实体 ID" required>
          <n-input v-model:value="newEdgeForm.toId" placeholder="例如 entity_gaibang" />
        </n-form-item>
        <n-form-item label="关系类型" required>
          <n-input v-model:value="newEdgeForm.type" placeholder="例如 leader_of, friend_of, wikilink" />
        </n-form-item>
        <n-space justify="end" style="margin-top: 12px">
          <n-button @click="showAddModal = false">取消</n-button>
          <n-button type="primary" :loading="savingGraphItem" @click="handleCreateEdge">保存关系边</n-button>
        </n-space>
      </n-form>
    </n-modal>
  </n-space>
</div>
`;

component.__scopeId = "data-v-6f979cfa";
export default component;
