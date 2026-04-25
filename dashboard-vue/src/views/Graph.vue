<template>
  <div class="graph-view">
    <n-space vertical size="large">
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
              重建将重新扫描所有文档的 wiki-link，无需 GPU，通常需要几十秒。确认吗？
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
                <n-button size="small" @click="loadNeighbors(node.id)">遍历邻居</n-button>
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
        :title="`遍历结果 - 根节点: ${getShortNodeId(traversalResult.rootId)}`"
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
                  :key="`traversal-${option.value}`"
                  :value="option.value"
                >
                  {{ option.label }}
                </n-checkbox>
              </n-space>
            </n-checkbox-group>
          </div>
        </n-space>

        <n-tabs type="segment">
          <n-tab-pane name="nodes" :tab="`节点 (${filteredTraversalNodes.length}/${traversalResult.nodes.length})`">
            <n-data-table
              :columns="nodeColumns"
              :data="filteredTraversalNodes"
              :pagination="{ pageSize: 20 }"
              size="small"
            />
          </n-tab-pane>

          <n-tab-pane name="edges" :tab="`边 (${traversalResult.edges.length})`">
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
    </n-space>
  </div>
</template>

<script setup lang="ts">
import { computed, h, onMounted, onUnmounted, ref, watch } from 'vue'
import { NIcon, NPopconfirm, NTag, useMessage } from 'naive-ui'
import type { DataTableColumns } from 'naive-ui'
import { RefreshOutline } from '@vicons/ionicons5'
import { graphApi } from '@/api'
import { useIndexStore } from '@/stores'

interface GraphNode {
  id: string
  title: string
  type: string
}

interface GraphEdge {
  fromId: string
  toId: string
  type: string
  lineNumber: number
}

interface TraversalResult {
  rootId: string
  depth: number
  nodes: GraphNode[]
  edges: GraphEdge[]
}

interface GraphStats {
  nodeCount: number
  docCount: number
  tagCount: number
  headingCount: number
  externalCount: number
  edgeCount: number
}

const nodeTypeMeta: Record<string, { label: string; tagType: 'default' | 'info' | 'success' | 'warning' | 'error' }> = {
  document: { label: '文档', tagType: 'info' },
  tag: { label: '标签', tagType: 'success' },
  heading: { label: '标题', tagType: 'warning' },
  external: { label: '外链', tagType: 'error' }
}

const nodeTypeOptions = [
  { label: '文档', value: 'document' },
  { label: '标签', value: 'tag' },
  { label: '标题', value: 'heading' },
  { label: '外链', value: 'external' }
]

const message = useMessage()
const indexStore = useIndexStore()

const searchQuery = ref('')
const nodeId = ref('')
const depth = ref(1)
const searching = ref(false)
const traversing = ref(false)
const statsLoading = ref(false)
const rebuilding = ref(false)
const selectedNodeTypes = ref<string[]>(nodeTypeOptions.map((option) => option.value))

let rebuildPollTimer: ReturnType<typeof setInterval> | null = null

const searchResults = ref<GraphNode[]>([])
const traversalResult = ref<TraversalResult | null>(null)
const stats = ref<GraphStats>({
  nodeCount: 0,
  docCount: 0,
  tagCount: 0,
  headingCount: 0,
  externalCount: 0,
  edgeCount: 0
})

const filteredSearchResults = computed(() =>
  searchResults.value.filter((node) => selectedNodeTypes.value.includes(node.type))
)

const filteredTraversalNodes = computed(() =>
  traversalResult.value?.nodes.filter((node) => selectedNodeTypes.value.includes(node.type)) ?? []
)

const normalizeNodeId = (id: string) => id.replace(/\\/g, '/')

const getShortNodeId = (id: string) => {
  const [pathPart, heading] = id.split('#', 2)
  const segments = normalizeNodeId(pathPart).split('/').filter(Boolean)
  const shortPath = segments.slice(Math.max(segments.length - 2, 0)).join('/')
  const base = shortPath || pathPart
  return heading ? `${base}#${heading}` : base
}

const renderNodeIdCell = (id: string) =>
  h(
    'span',
    {
      title: id,
      class: 'graph-node-id'
    },
    getShortNodeId(id)
  )

const nodeColumns: DataTableColumns<GraphNode> = [
  { title: 'ID', key: 'id', render: (row) => renderNodeIdCell(row.id) },
  { title: '标题', key: 'title' },
  {
    title: '类型',
    key: 'type',
    width: 100,
    render: (row) => {
      const meta = nodeTypeMeta[row.type] ?? { label: row.type, tagType: 'default' as const }
      return h(NTag, { size: 'small', type: meta.tagType }, () => meta.label)
    }
  },
  {
    title: '操作',
    key: 'actions',
    width: 80,
    render: (row) =>
      h(
        'a',
        {
          style: 'cursor:pointer;color:#63e2b7',
          onClick: () => {
            nodeId.value = row.id
            loadNeighbors(row.id)
          }
        },
        '遍历'
      )
  }
]

const edgeColumns: DataTableColumns<GraphEdge> = [
  { title: '来源', key: 'fromId', render: (row) => renderNodeIdCell(row.fromId) },
  { title: '目标', key: 'toId', render: (row) => renderNodeIdCell(row.toId) },
  {
    title: '类型',
    key: 'type',
    width: 100,
    render: (row) => {
      const typeMap: Record<string, 'info' | 'success' | 'warning' | 'default'> = {
        wikilink: 'info',
        embed: 'warning',
        tag: 'success'
      }
      return h(NTag, { size: 'small', type: typeMap[row.type] ?? 'default' }, () => row.type)
    }
  },
  { title: '行号', key: 'lineNumber', width: 70 }
]

const searchNodes = async () => {
  if (!searchQuery.value.trim()) return
  searching.value = true
  try {
    const res = await graphApi.search(searchQuery.value.trim())
    searchResults.value = res.data as GraphNode[]
  } catch {
    message.error('搜索失败')
  } finally {
    searching.value = false
  }
}

const loadNeighbors = async (id: string) => {
  if (!id.trim()) return
  traversing.value = true
  traversalResult.value = null
  try {
    const res = await graphApi.neighbors(id.trim(), depth.value)
    traversalResult.value = res.data as TraversalResult
  } catch {
    message.error('遍历失败，节点可能不存在')
  } finally {
    traversing.value = false
  }
}

const loadStats = async () => {
  statsLoading.value = true
  try {
    const res = await graphApi.stats()
    stats.value = res.data as GraphStats
  } catch {
    // ignore
  } finally {
    statsLoading.value = false
  }
}

const startRebuild = async () => {
  rebuilding.value = true
  try {
    await graphApi.rebuild()
    message.info('图谱重建已启动')
    rebuildPollTimer = setInterval(async () => {
      try {
        const res = await graphApi.rebuildStatus()
        if (!res.data.isRebuilding) {
          clearInterval(rebuildPollTimer!)
          rebuildPollTimer = null
          rebuilding.value = false
          await loadStats()
          message.success('图谱重建完成')
        }
      } catch {
        // ignore poll errors
      }
    }, 3000)
  } catch (err: any) {
    rebuilding.value = false
    message.error(err.response?.data?.error || '重建失败')
  }
}

watch(
  () => indexStore.isIndexing,
  (now, prev) => {
    if (prev === true && now === false) loadStats()
  }
)

onMounted(async () => {
  await indexStore.connect()
  loadStats()
})

onUnmounted(() => {
  if (rebuildPollTimer) clearInterval(rebuildPollTimer)
})
</script>

<style scoped>
.graph-view {
  padding: 16px;
  max-width: 1200px;
  margin: 0 auto;
}

.filter-row {
  display: flex;
  align-items: center;
  gap: 12px;
  flex-wrap: wrap;
}

.filter-label {
  min-width: 60px;
  font-size: 13px;
}

.graph-node-summary {
  display: flex;
  flex-direction: column;
  gap: 4px;
  min-width: 0;
}

.graph-node-id {
  max-width: 100%;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
</style>
