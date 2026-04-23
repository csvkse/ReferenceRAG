<template>
  <div class="graph-view">
    <n-space vertical size="large">
      <!-- 顶部统计 -->
      <n-grid :cols="3" :x-gap="12">
        <n-grid-item>
          <n-statistic label="节点数" :value="stats.nodeCount" />
        </n-grid-item>
        <n-grid-item>
          <n-statistic label="边数" :value="stats.edgeCount" />
        </n-grid-item>
        <n-grid-item>
          <n-statistic label="当前查询深度" :value="depth" />
        </n-grid-item>
      </n-grid>

      <!-- 搜索 / 遍历面板 -->
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

          <!-- 搜索结果列表 -->
          <n-list v-if="searchResults.length" bordered>
            <n-list-item v-for="node in searchResults" :key="node.id">
              <n-space justify="space-between" align="center" style="width:100%">
                <div>
                  <n-text strong>{{ node.title }}</n-text>
                  <n-text depth="3" style="margin-left:8px;font-size:12px">{{ node.id }}</n-text>
                </div>
                <n-button size="small" @click="loadNeighbors(node.id)">遍历邻居</n-button>
              </n-space>
            </n-list-item>
          </n-list>

          <!-- 直接输入节点 ID -->
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
              style="width:80px"
              placeholder="深度"
            />
            <n-button type="primary" @click="loadNeighbors(nodeId)" :loading="traversing">遍历</n-button>
          </n-input-group>
        </n-space>
      </n-card>

      <!-- 遍历结果 -->
      <n-card v-if="traversalResult" :title="`遍历结果 — 根节点: ${traversalResult.rootId}`">
        <n-tabs type="segment">
          <!-- 节点列表 -->
          <n-tab-pane name="nodes" :tab="`节点 (${traversalResult.nodes.length})`">
            <n-data-table
              :columns="nodeColumns"
              :data="traversalResult.nodes"
              :pagination="{ pageSize: 20 }"
              size="small"
            />
          </n-tab-pane>

          <!-- 边列表 -->
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
import { ref, onMounted, h } from 'vue'
import { useMessage, NTag } from 'naive-ui'
import type { DataTableColumns } from 'naive-ui'
import { graphApi } from '@/api'

interface GraphNode { id: string; title: string; type: string }
interface GraphEdge { fromId: string; toId: string; type: string; lineNumber: number }
interface TraversalResult { rootId: string; depth: number; nodes: GraphNode[]; edges: GraphEdge[] }

const message = useMessage()
const searchQuery = ref('')
const nodeId = ref('')
const depth = ref(1)
const searching = ref(false)
const traversing = ref(false)
const searchResults = ref<GraphNode[]>([])
const traversalResult = ref<TraversalResult | null>(null)
const stats = ref({ nodeCount: 0, edgeCount: 0 })

const nodeColumns: DataTableColumns<GraphNode> = [
  { title: 'ID', key: 'id', ellipsis: { tooltip: true } },
  { title: '标题', key: 'title' },
  {
    title: '类型',
    key: 'type',
    width: 100,
    render: (row) => h(NTag, { size: 'small', type: row.type === 'document' ? 'info' : 'default' }, () => row.type)
  },
  {
    title: '操作',
    key: 'actions',
    width: 80,
    render: (row) => h('a', {
      style: 'cursor:pointer;color:#63e2b7',
      onClick: () => { nodeId.value = row.id; loadNeighbors(row.id) }
    }, '遍历')
  }
]

const edgeColumns: DataTableColumns<GraphEdge> = [
  { title: '来源', key: 'fromId', ellipsis: { tooltip: true } },
  { title: '目标', key: 'toId', ellipsis: { tooltip: true } },
  {
    title: '类型',
    key: 'type',
    width: 100,
    render: (row) => {
      const typeMap: Record<string, 'info'|'success'|'warning'|'default'> = {
        wikilink: 'info', embed: 'warning', tag: 'success'
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
  try {
    const res = await graphApi.stats()
    stats.value = res.data as { nodeCount: number; edgeCount: number }
  } catch { /* ignore */ }
}

onMounted(loadStats)
</script>

<style scoped>
.graph-view {
  padding: 16px;
  max-width: 1200px;
  margin: 0 auto;
}
</style>
