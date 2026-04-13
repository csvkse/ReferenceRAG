<template>
  <n-space vertical :size="20">
    <!-- BM25 Index Management -->
    <n-card title="BM25索引管理">
      <n-tabs type="line" animated>
        <!-- Summary Tab -->
        <n-tab-pane name="summary" tab="索引概览">
          <n-space vertical :size="16">
            <n-spin :show="loading">
              <n-descriptions label-placement="left" :column="2" bordered>
                <n-descriptions-item label="已索引文档">
                  <n-text strong>{{ summary.totalIndexedDocuments }}</n-text>
                </n-descriptions-item>
                <n-descriptions-item label="词汇量">
                  <n-text strong>{{ summary.totalVocabularySize }}</n-text>
                </n-descriptions-item>
                <n-descriptions-item label="平均文档长度">
                  <n-text strong>{{ summary.averageDocLength?.toFixed(2) || '-' }}</n-text>
                </n-descriptions-item>
                <n-descriptions-item label="总文件数">
                  <n-text strong>{{ summary.totalFiles }}</n-text>
                </n-descriptions-item>
                <n-descriptions-item label="总分块数">
                  <n-text strong>{{ summary.totalChunks }}</n-text>
                </n-descriptions-item>
              </n-descriptions>
            </n-spin>
          </n-space>
        </n-tab-pane>

        <!-- Index Operations Tab -->
        <n-tab-pane name="operations" tab="索引操作">
          <n-space vertical :size="16">
            <n-alert type="info">
              <template #header>提示</template>
              <n-space vertical>
                <n-text>全量索引将重新处理所有已存储的文档并建立 BM25 索引。</n-text>
                <n-text>清空索引将删除所有 BM25 索引数据，此操作不可恢复。</n-text>
              </n-space>
            </n-alert>

            <n-space>
              <n-button
                type="primary"
                :loading="indexing"
                @click="handleIndexAll"
              >
                <template #icon><n-icon :component="RefreshOutline" /></template>
                全量索引
              </n-button>
              <n-button
                type="error"
                :loading="clearing"
                @click="showClearDialog = true"
              >
                <template #icon><n-icon :component="TrashOutline" /></template>
                清空索引
              </n-button>
              <n-button @click="loadSummary" :loading="loading">
                <template #icon><n-icon :component="RefreshOutline" /></template>
                刷新
              </n-button>
            </n-space>

            <!-- Index progress -->
            <n-card v-if="indexProgress" title="索引进度">
              <n-progress
                type="line"
                :percentage="indexProgress.progressPercent"
                :status="indexProgress.status"
                :indicator-placement="'inside'"
              />
              <n-space vertical :size="8" style="margin-top: 12px">
                <n-text depth="3">{{ indexProgress.message }}</n-text>
                <n-text depth="3">文档数: {{ indexProgress.totalDocuments }}</n-text>
                <n-text depth="3">词汇量: {{ indexProgress.totalTerms }}</n-text>
              </n-space>
            </n-card>
          </n-space>
        </n-tab-pane>

        <!-- Search Tab -->
        <n-tab-pane name="search" tab="搜索测试">
          <n-space vertical :size="16">
            <n-form :model="searchForm" inline>
              <n-form-item label="查询语句">
                <n-input
                  v-model:value="searchForm.query"
                  placeholder="输入查询关键词"
                  style="width: 400px"
                />
              </n-form-item>
              <n-form-item label="返回数量">
                <n-input-number
                  v-model:value="searchForm.topK"
                  :min="1"
                  :max="100"
                  style="width: 100px"
                />
              </n-form-item>
              <n-form-item>
                <n-button type="primary" :loading="searching" @click="handleSearch">
                  搜索
                </n-button>
              </n-form-item>
            </n-form>

            <!-- Search results -->
            <n-card v-if="searchResults.length > 0" title="搜索结果">
              <template #header-extra>
                <n-text depth="3">耗时: {{ searchDuration }}ms</n-text>
              </template>
              <n-list>
                <n-list-item v-for="(result, index) in searchResults" :key="index">
                  <n-thing>
                    <template #header>
                      <n-space align="center">
                        <n-tag type="info" size="small">#{{ result.rank }}</n-tag>
                        <n-text code>{{ result.chunkId }}</n-text>
                        <n-tag type="success" size="small">Score: {{ result.score.toFixed(4) }}</n-tag>
                      </n-space>
                    </template>
                    <n-ellipsis :line-clamp="3" expand-trigger="click">
                      {{ result.content }}
                    </n-ellipsis>
                  </n-thing>
                </n-list-item>
              </n-list>
            </n-card>

            <n-empty v-else-if="searchForm.query && !searching" description="暂无搜索结果" />
          </n-space>
        </n-tab-pane>

      </n-tabs>
    </n-card>

    <!-- Clear Confirm Dialog -->
    <n-modal v-model:show="showClearDialog" preset="dialog" title="清空索引确认">
      <n-alert type="error">
        <template #header>警告</template>
        清空索引将删除所有 BM25 索引数据，此操作不可恢复！
      </n-alert>
      <template #action>
        <n-button @click="showClearDialog = false">取消</n-button>
        <n-button type="error" :loading="clearing" @click="handleClear">确认清空</n-button>
      </template>
    </n-modal>
  </n-space>
</template>

<script setup lang="ts">
import { ref, onMounted } from 'vue'
import {
  NButton,
  NSpace,
  NTag,
  NInput,
  NInputNumber,
  NForm,
  NFormItem,
  NAlert,
  NCard,
  NList,
  NListItem,
  NThing,
  NProgress,
  NModal,
  NDescriptions,
  NDescriptionsItem,
  NText,
  NIcon,
  NEllipsis,
  NEmpty,
  NSpin,
  useMessage
} from 'naive-ui'
import { RefreshOutline, TrashOutline } from '@vicons/ionicons5'
import { bm25IndexApi, type BM25Summary, type BM25IndexProgress } from '@/api'

const message = useMessage()

// State
const loading = ref(false)
const indexing = ref(false)
const clearing = ref(false)
const searching = ref(false)
const showClearDialog = ref(false)

const summary = ref<BM25Summary>({
  totalIndexedDocuments: 0,
  totalVocabularySize: 0,
  averageDocLength: 0,
  totalFiles: 0,
  totalChunks: 0
})

const indexProgress = ref<{
  progressPercent: number
  status: 'default' | 'success' | 'error'
  message: string
  totalDocuments: number
  totalTerms: number
} | null>(null)

const searchResults = ref<{ chunkId: string; content: string; score: number; rank: number }[]>([])
const searchDuration = ref(0)

const searchForm = ref({
  query: '',
  topK: 10
})

// Methods
const loadSummary = async () => {
  loading.value = true
  try {
    const response = await bm25IndexApi.getSummary()
    summary.value = response.data
  } catch (error) {
    console.error('Failed to load summary:', error)
    message.error('加载索引概览失败')
  } finally {
    loading.value = false
  }
}

const handleIndexAll = async () => {
  indexing.value = true
  indexProgress.value = {
    progressPercent: 0,
    status: 'default',
    message: '正在建立索引...',
    totalDocuments: 0,
    totalTerms: 0
  }

  try {
    const response = await bm25IndexApi.indexAll()
    const result: BM25IndexProgress = response.data
    indexProgress.value = {
      progressPercent: result.progressPercent,
      status: 'success',
      message: result.message,
      totalDocuments: result.totalDocuments,
      totalTerms: result.totalTerms
    }
    message.success('索引建立完成')
    await loadSummary()
  } catch (error: any) {
    console.error('Failed to index:', error)
    indexProgress.value = {
      progressPercent: 0,
      status: 'error',
      message: `索引失败: ${error.response?.data?.error || error.message}`,
      totalDocuments: 0,
      totalTerms: 0
    }
    message.error(`索引失败: ${error.response?.data?.error || error.message}`)
  } finally {
    indexing.value = false
  }
}

const handleClear = async () => {
  clearing.value = true
  try {
    await bm25IndexApi.clearIndex()
    message.success('索引已清空')
    showClearDialog.value = false
    await loadSummary()
  } catch (error: any) {
    console.error('Failed to clear index:', error)
    message.error(`清空失败: ${error.response?.data?.error || error.message}`)
  } finally {
    clearing.value = false
  }
}

const handleSearch = async () => {
  if (!searchForm.value.query) {
    message.error('请输入查询语句')
    return
  }

  searching.value = true
  try {
    const response = await bm25IndexApi.search(
      searchForm.value.query,
      searchForm.value.topK
    )
    const result = response.data
    searchResults.value = result.results
    searchDuration.value = result.durationMs
  } catch (error: any) {
    console.error('Failed to search:', error)
    message.error(`搜索失败: ${error.response?.data?.error || error.message}`)
    searchResults.value = []
  } finally {
    searching.value = false
  }
}

// Lifecycle
onMounted(() => {
  loadSummary()
})
</script>
