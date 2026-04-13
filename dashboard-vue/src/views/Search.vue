<template>
  <n-space vertical :size="20">
    <!-- Model & Index Status -->
    <n-card size="small">
      <n-space align="center" justify="space-between">
        <n-space align="center" :size="24">
          <!-- Embedding Model -->
          <n-space align="center" :size="8">
            <n-icon :component="CubeOutline" size="18" />
            <n-text>嵌入模型:</n-text>
            <n-tag v-if="searchStatus?.embeddingModel" type="success" size="small" round>
              {{ searchStatus.embeddingModel }} ({{ searchStatus.embeddingDimension }}d)
            </n-tag>
            <n-tag v-else type="warning" size="small" round>未配置</n-tag>
          </n-space>

          <!-- Rerank Model -->
          <n-space align="center" :size="8">
            <n-icon :component="GitMergeOutline" size="18" />
            <n-text>重排模型:</n-text>
            <n-tag v-if="searchStatus?.rerankEnabled && searchStatus?.rerankModel" type="success" size="small" round>
              {{ searchStatus.rerankModel }}
            </n-tag>
            <n-tag v-else type="default" size="small" round>未启用</n-tag>
          </n-space>

          <!-- BM25 Index -->
          <n-space align="center" :size="8">
            <n-icon :component="SearchOutline" size="18" />
            <n-text>BM25:</n-text>
            <n-tag v-if="searchStatus?.bm25HasIndex" type="success" size="small" round>
              {{ searchStatus.bm25IndexedDocuments }} 文档
            </n-tag>
            <n-tag v-else type="warning" size="small" round>无索引</n-tag>
          </n-space>

          <!-- Vector Index -->
          <n-space align="center" :size="8">
            <n-icon :component="LayersOutline" size="18" />
            <n-text>向量:</n-text>
            <n-tag v-if="searchStatus?.vectorHasIndex" type="success" size="small" round>
              {{ searchStatus.vectorIndexedChunks }} 分块
            </n-tag>
            <n-tag v-else type="warning" size="small" round>无索引</n-tag>
          </n-space>
        </n-space>

        <!-- Total Files -->
        <n-text depth="3">共 {{ searchStatus?.totalFiles ?? 0 }} 个文件</n-text>
      </n-space>
    </n-card>

    <!-- Search Input -->
    <n-card>
      <div style="display: flex; flex-direction: column; gap: 8px">
        <div style="display: flex; align-items: flex-start; gap: 12px">
          <n-input
            v-model:value="searchQuery"
            type="textarea"
            placeholder="输入搜索内容... (Enter 搜索, Shift+Enter 换行)"
            :rows="2"
            autosize
            style="flex: 1; min-width: 0"
            @keydown.enter="handleKeyDown"
          >
            <template #suffix>
              <n-button
                v-if="searchQuery"
                text
                @click="handleClearQuery"
                style="padding: 0 4px"
              >
                <template #icon>
                  <n-icon :component="CloseOutline" />
                </template>
              </n-button>
            </template>
          </n-input>
          <n-button type="primary" :loading="loading" @click="handleSearch" :disabled="!searchQuery.trim()">
              <template #icon><n-icon :component="SearchOutline" /></template>
              搜索
            </n-button>
        </div>
        <n-text depth="3" style="font-size: 12px">
          Enter 搜索 | Shift+Enter 换行
        </n-text>
      </div>
    </n-card>

    <!-- Search Options -->
    <n-card title="搜索选项">
      <template #header-extra>
        <n-space align="center" :size="12">
          <n-button text size="small" @click="showModeHelp = !showModeHelp">
            <template #icon><n-icon :component="HelpCircleOutline" /></template>
            模式说明
          </n-button>
          <n-button text size="small" @click="handleReset" :disabled="isDefaultOptions">
            <template #icon><n-icon :component="RefreshOutline" /></template>
            重置为默认
          </n-button>
        </n-space>
      </template>

      <!-- Mode Help Collapse -->
      <n-collapse-transition :show="showModeHelp">
        <n-card size="small" style="margin-bottom: 16px; background: var(--help-card-bg, rgba(255,255,255,0.02))">
          <n-descriptions label-placement="left" :column="1" size="small">
            <n-descriptions-item label="快速模式">
              <n-text depth="3">~1000 tokens，快速返回少量结果，适合简单查询</n-text>
            </n-descriptions-item>
            <n-descriptions-item label="标准模式">
              <n-text depth="3">~3000 tokens，默认模式，平衡速度与结果质量</n-text>
            </n-descriptions-item>
            <n-descriptions-item label="深度模式">
              <n-text depth="3">~6000 tokens，返回更多内容，适合复杂查询</n-text>
            </n-descriptions-item>
            <n-descriptions-item label="混合模式">
              <n-text depth="3">BM25关键词 + 向量语义混合搜索，综合召回能力强</n-text>
            </n-descriptions-item>
            <n-descriptions-item label="两阶段模式">
              <n-text depth="3">召回(BM25+向量) + Rerank精排，精确度最高</n-text>
            </n-descriptions-item>
          </n-descriptions>
        </n-card>
      </n-collapse-transition>
      <n-grid :cols="4" :x-gap="20">
        <n-gi>
          <n-form-item label="查询模式">
            <n-select v-model:value="searchOptions.mode" :options="modeOptions" />
          </n-form-item>
        </n-gi>
        <n-gi>
          <n-form-item label="返回数量 (Top K)">
            <n-input-number v-model:value="searchOptions.topK" :min="1" :max="50" style="width: 100%" />
          </n-form-item>
        </n-gi>
        <n-gi>
          <n-form-item label="上下文窗口">
            <n-input-number v-model:value="searchOptions.contextWindow" :min="0" :max="5" style="width: 100%" />
          </n-form-item>
        </n-gi>
        <n-gi>
          <n-form-item label="源筛选">
            <n-select
              v-model:value="searchOptions.sources"
              :options="sourceOptions"
              multiple
              placeholder="全部源"
              clearable
            />
          </n-form-item>
        </n-gi>
        <n-gi>
          <n-form-item label="路径过滤">
            <n-tree
              v-model:selected-keys="selectedPaths"
              :data="pathTreeData"
              :loading="pathsLoading"
              selectable
              multiple
              clearable
              placeholder="全部路径"
              style="max-height: 200px; overflow-y: auto"
              @update:selected-keys="handlePathSelect"
            />
          </n-form-item>
        </n-gi>
      </n-grid>
    </n-card>

    <!-- Search Stats -->
    <n-card v-if="searchResponse" :title="`搜索结果 (${searchResponse.stats.totalMatches} 条匹配)`">
      <template #header-extra>
        <n-space size="small">
          <n-tag size="small">耗时 {{ searchResponse.stats.durationMs }}ms</n-tag>
          <n-tag size="small">~{{ searchResponse.stats.estimatedTokens }} tokens</n-tag>
        </n-space>
      </template>

      <!-- Context -->
      <n-card v-if="searchResponse.context" title="组装上下文" size="small" style="margin-bottom: 16px">
        <n-input
          :value="searchResponse.context"
          type="textarea"
          :autosize="{ minRows: 2, maxRows: 6 }"
          readonly
        />
      </n-card>

      <!-- Chunk Results -->
      <n-list>
        <n-list-item v-for="chunk in searchResponse.chunks" :key="chunk.refId">
          <n-thing>
            <template #header>
              <n-space align="center">
                <n-tag v-if="chunk.source" size="small" :bordered="false" type="info">{{ chunk.source }}</n-tag>
                <n-text>{{ chunk.title || chunk.filePath }}</n-text>
                <n-text v-if="chunk.headingPath" depth="3" style="font-size: 12px">/ {{ chunk.headingPath }}</n-text>
              </n-space>
            </template>
            <template #header-extra>
              <n-space size="small">
                <n-tag v-if="chunk.bm25Score !== undefined" :type="getScoreType(chunk.bm25Score / 15)" size="small" title="BM25关键词分数">
                  BM25: {{ chunk.bm25Score.toFixed(1) }}
                </n-tag>
                <n-tag :type="getScoreType(chunk.score)" size="small" title="融合分数">
                  {{ (chunk.score * 100).toFixed(1) }}%
                </n-tag>
              </n-space>
            </template>
            <template #description>
              <n-text depth="3" style="white-space: pre-wrap; line-height: 1.6; font-size: 13px">
                {{ truncateContent(chunk.content || '') }}
              </n-text>
              <n-text v-if="chunk.startLine > 0" depth="3" style="display: block; margin-top: 4px; font-size: 12px">
                行 {{ chunk.startLine }}-{{ chunk.endLine }}
              </n-text>
              <n-space style="margin-top: 8px">
                <n-button text size="small" @click="handleDrilldown(chunk)">
                  <template #icon><n-icon :component="ArrowDownOutline" /></template>
                  深入查询
                </n-button>
                <n-button
                  v-if="chunk.obsidianLink"
                  text
                  size="small"
                  tag="a"
                  :href="chunk.obsidianLink"
                  target="_blank"
                >
                  <template #icon><n-icon :component="OpenOutline" /></template>
                  在 Obsidian 中打开
                </n-button>
              </n-space>
            </template>
          </n-thing>
        </n-list-item>
      </n-list>

      <!-- Related Files -->
      <template v-if="searchResponse.files.length > 0">
        <n-divider>相关文件</n-divider>
        <n-space>
          <n-tag v-for="file in searchResponse.files" :key="file.id" size="small" round>
            {{ file.title || file.path }} ({{ file.chunkCount }})
          </n-tag>
        </n-space>
      </template>
    </n-card>

    <!-- Empty State -->
    <n-card v-else-if="searched && !loading">
      <n-empty description="没有找到匹配的结果" />
    </n-card>

    <!-- Drilldown Dialog -->
    <n-modal v-model:show="showDrilldown" preset="card" title="深入查询" style="width: 70vw">
      <n-spin :show="drilldownLoading">
        <n-space v-if="drilldownResponse" vertical>
          <n-card title="扩展上下文" size="small">
            <n-input
              :value="drilldownResponse.fullContext"
              type="textarea"
              :autosize="{ minRows: 3, maxRows: 10 }"
              readonly
            />
          </n-card>
          <n-card v-if="drilldownResponse.expandedChunks && drilldownResponse.expandedChunks.length > 0" title="扩展片段" size="small">
            <n-list>
              <n-list-item v-for="chunk in drilldownResponse.expandedChunks" :key="chunk.refId">
                <n-thing>
                  <template #header>
                    <n-tag size="small" type="info">{{ chunk.title || chunk.filePath || '' }}</n-tag>
                  </template>
                  <template #header-extra>
                    <n-tag size="small">{{ ((chunk.score || 0) * 100).toFixed(1) }}%</n-tag>
                  </template>
                  <template #description>
                    <n-text depth="3">{{ truncateContent(chunk.content || '', 200) }}</n-text>
                  </template>
                </n-thing>
              </n-list-item>
            </n-list>
          </n-card>
        </n-space>
      </n-spin>
    </n-modal>
  </n-space>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import {
  SearchOutline,
  ArrowDownOutline,
  OpenOutline,
  CloseOutline,
  RefreshOutline,
  CubeOutline,
  GitMergeOutline,
  LayersOutline,
  HelpCircleOutline
} from '@vicons/ionicons5'
import { aiQueryApi, sourcesApi, pathsApi, type SearchStatusResponse } from '@/api'
import type { SourceDetail, AIQueryResponse, ChunkResult, DrilldownResponse, QueryMode, SourcePathInfo } from '@/types/api'

const searchQuery = ref('')
const loading = ref(false)
const searched = ref(false)
const showModeHelp = ref(false)
const searchResponse = ref<AIQueryResponse | null>(null)
const sources = ref<SourceDetail[]>([])
const paths = ref<SourcePathInfo[]>([])
const selectedPaths = ref<string[]>([])
const pathsLoading = ref(false)
const searchStatus = ref<SearchStatusResponse | null>(null)

// Default options for reset
const defaultOptions = {
  mode: 'Hybrid' as QueryMode,
  topK: 10,
  contextWindow: 1,
  sources: [] as string[],
  folders: [] as string[]
}

const searchOptions = ref<{
  mode: QueryMode
  topK: number
  contextWindow: number
  sources: string[]
  folders: string[]
}>({ ...defaultOptions })

// Check if current options are default
const isDefaultOptions = computed(() => {
  return (
    searchOptions.value.mode === defaultOptions.mode &&
    searchOptions.value.topK === defaultOptions.topK &&
    searchOptions.value.contextWindow === defaultOptions.contextWindow &&
    searchOptions.value.sources.length === 0 &&
    searchOptions.value.folders.length === 0
  )
})

const modeOptions = [
    { label: '快速 (Quick)', value: 'Quick' },
    { label: '标准 (Standard)', value: 'Standard' },
    { label: '深度 (Deep)', value: 'Deep' },
    { label: '混合 (Hybrid) - 推荐', value: 'Hybrid' },
    { label: '两阶段 (HybridRerank)', value: 'HybridRerank' }
]

const sourceOptions = computed(() =>
  sources.value.map(s => ({ label: s.name, value: s.name }))
)

// 将路径数据转换为树形结构供 n-tree 使用
const pathTreeData = computed(() => {
  return paths.value.map(source => ({
    key: `source:${source.name}`,
    label: source.name,
    children: source.folders.map(folder => ({
      key: `folder:${folder}`,
      label: folder
    }))
  }))
})

// 将选中的路径 key 转换为 folder 路径（folder:/docs -> /docs）
const selectedPathFolders = computed(() => {
  return selectedPaths.value
    .filter(key => key.startsWith('folder:'))
    .map(key => key.replace('folder:', ''))
})

// n-tree 选中变化时处理
const handlePathSelect = (keys: string[]) => {
  selectedPaths.value = keys
  searchOptions.value.folders = selectedPathFolders.value
}

const showDrilldown = ref(false)
const drilldownLoading = ref(false)
const drilldownResponse = ref<DrilldownResponse | null>(null)

const getScoreType = (score: number) => {
  if (score >= 0.8) return 'success'
  if (score >= 0.5) return 'warning'
  return 'error'
}

const truncateContent = (content: string, maxLen = 300) => {
  if (!content) return ''
  return content.length > maxLen ? content.substring(0, maxLen) + '...' : content
}

// Handle Enter key: Shift+Enter for newline, Enter for search
const handleKeyDown = (e: KeyboardEvent) => {
  if (e.shiftKey) {
    // Shift+Enter: allow default behavior (newline)
    return
  }
  // Enter (without Shift): prevent newline and search
  e.preventDefault()
  handleSearch()
}

// Clear search query
const handleClearQuery = () => {
  searchQuery.value = ''
}

// Reset all search options to default
const handleReset = () => {
  searchOptions.value = { ...defaultOptions }
  selectedPaths.value = []
  searchQuery.value = ''
  searchResponse.value = null
  searched.value = false
}

const handleSearch = async () => {
  if (!searchQuery.value.trim()) return
  loading.value = true
  searched.value = true
  try {
    const response = await aiQueryApi.query({
      query: searchQuery.value,
      mode: searchOptions.value.mode,
      topK: searchOptions.value.topK,
      contextWindow: searchOptions.value.contextWindow,
      sources: searchOptions.value.sources.length > 0 ? searchOptions.value.sources : undefined,
      filters: searchOptions.value.folders.length > 0 ? { folders: searchOptions.value.folders } : undefined
    })
    searchResponse.value = response.data
  } catch (error) {
    console.error('Search failed:', error)
    searchResponse.value = null
  } finally {
    loading.value = false
  }
}

const handleDrilldown = async (chunk: ChunkResult) => {
  showDrilldown.value = true
  drilldownLoading.value = true
  drilldownResponse.value = null
  try {
    const response = await aiQueryApi.drilldown({
      query: searchQuery.value,
      refIds: [chunk.refId || '']
    })
    drilldownResponse.value = response.data
  } catch (error) {
    console.error('Drilldown failed:', error)
  } finally {
    drilldownLoading.value = false
  }
}

const loadSources = async () => {
  try {
    const response = await sourcesApi.getAll()
    sources.value = response.data
  } catch (error) {
    console.error('Failed to load sources:', error)
  }
}

const loadPaths = async () => {
  pathsLoading.value = true
  try {
    const response = await pathsApi.getPaths()
    paths.value = response.data.sources
  } catch (error) {
    console.error('Failed to load paths:', error)
  } finally {
    pathsLoading.value = false
  }
}

const loadSearchStatus = async () => {
  try {
    const response = await aiQueryApi.getSearchStatus()
    searchStatus.value = response.data
  } catch (error) {
    console.error('Failed to load search status:', error)
  }
}

onMounted(() => {
  loadSources()
  loadPaths()
  loadSearchStatus()
})
</script>

<style scoped>
/* 浅色模式适配 */
:global(body.light-theme) .n-card[style*="background"] {
  --help-card-bg: rgba(0, 0, 0, 0.02);
}
</style>
