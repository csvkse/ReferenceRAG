import { defineComponent as _defineComponent } from 'vue';
import { ref, computed, onMounted, onActivated, watch } from 'vue';
import {
    useMessage, NButton, NSpace, NTag, NInput, NInputNumber, NForm, NFormItem,
    NAlert, NCard, NList, NListItem, NThing, NProgress, NModal, NDescriptions,
    NDescriptionsItem, NText, NIcon, NEllipsis, NEmpty, NSpin, NTabs, NTabPane, NSelect
} from 'naive-ui';
import { RefreshOutline, TrashOutline, SearchOutline, LayersOutline } from '@vicons/ionicons5';
import { bm25IndexApi, indexJobsApi, knowledgeBasesApi, knowledgeBaseSearchApi } from '/app/api/index.js';
import { loadKbSpecMap, kbOptionLabel } from '/shared/kb-label.js';
import { knowledgeBaseRequest, knowledgeBaseResponse } from '/app/views/lexical-search-template.js';

const component = /*@__PURE__*/ _defineComponent({
    ...{ name: 'BM25Index' },
    __name: 'BM25Index',
    setup(__props, { expose: __expose }) {
        __expose();
        const message = useMessage();

        // 状态列表
        const loading = ref(false);
        const indexing = ref(false);
        const clearing = ref(false);
        const searching = ref(false);
        const showClearDialog = ref(false);

        // 知识库选择状态
        const knowledgeBases = ref([]);
        const selectedKbId = ref(null);
        const kbSpecMap = ref({});
        const kbOptions = computed(() => [
            { label: '全部知识库 (全局词法检索)', value: '__all__' },
            ...knowledgeBases.value.map(k => ({
                label: kbOptionLabel(k, kbSpecMap.value, { prefix: '📚 ' }),
                value: k.id
            }))
        ]);

        const summary = ref({
            totalIndexedDocuments: 0,
            totalVocabularySize: 0,
            averageDocLength: 0,
            totalFiles: 0,
            totalChunks: 0
        });

        const indexProgress = ref(null);
        const searchResults = ref([]);
        const searchDuration = ref(0);
        const searchForm = ref({
            query: '',
            topK: 10
        });

        const loadKnowledgeBases = async () => {
            try {
                const [res, specs] = await Promise.all([
                    knowledgeBasesApi.getAll(),
                    loadKbSpecMap()
                ]);
                knowledgeBases.value = res.data || [];
                kbSpecMap.value = specs;
                if (!selectedKbId.value && knowledgeBases.value.length > 0) {
                    selectedKbId.value = '__all__';
                }
            } catch (err) {
                console.warn('加载知识库列表失败:', err);
            }
        };

        // 作用域可读说明：让「全部 = N 个库合计」在页面上可见。
        const scopeLabel = computed(() => {
            if (!summary.value) return '加载中…';
            if (summary.value.scope === 'all') {
                const n = (summary.value.knowledgeBases || []).length || (summary.value.knowledgeBaseIds || []).length;
                return `全部知识库（${n} 个库合计）`;
            }
            const id = (summary.value.knowledgeBaseIds || [])[0];
            const kb = knowledgeBases.value.find((k) => k.id === id);
            return kb ? `仅 ${kb.name}` : '单个知识库';
        });

        const loadSummary = async () => {
            loading.value = true;
            try {
                // 原实现把「全部」折叠成 null，后端据此选中第一个库 → 页面显示单库数字。
        // 现在统一转发显式哨兵，后端才能走聚合分支。
                const targetKb = selectedKbId.value || '__all__';
                const response = await bm25IndexApi.getSummary(targetKb);
                summary.value = response.data || summary.value;
            } catch (error) {
                console.warn('加载索引概览失败:', error);
            } finally {
                loading.value = false;
            }
        };

        const handleIndexAll = async () => {
            indexing.value = true;
            indexProgress.value = {
                progressPercent: 0,
                status: 'default',
                message: '正在建立词法索引...',
                totalDocuments: 0,
                totalTerms: 0
            };
            try {
                // 统一转发显式哨兵，后端走聚合分支
                const targetKb = selectedKbId.value || '__all__';
                const response = await bm25IndexApi.indexAll(targetKb);
                const result = response.data;
                const jobId = result.jobId;

                if (jobId) {
                    indexProgress.value = {
                        progressPercent: 10,
                        status: 'default',
                        message: result.message || '正在后台构建 BM25 词法倒排索引...',
                        totalDocuments: result.totalDocuments || 0,
                        totalTerms: result.totalTerms || 0
                    };
                    const pollTimer = setInterval(async () => {
                        try {
                            const jobRes = await indexJobsApi.getJob(jobId);
                            const job = jobRes.data;
                            if (job) {
                                const pct = job.progressPercent || (job.totalFiles > 0 ? Math.round((job.processedFiles / job.totalFiles) * 100) : 50);
                                indexProgress.value = {
                                    progressPercent: Math.min(100, Math.max(pct, 5)),
                                    status: job.status === 'Failed' ? 'error' : (job.status === 'Succeeded' || job.status === 'Completed' ? 'success' : 'default'),
                                    message: job.errors?.length ? job.errors.join('; ') : (job.currentFile || `正在构建: ${job.processedFiles} / ${job.totalFiles}`),
                                    totalDocuments: job.totalFiles || result.totalDocuments || 0,
                                    totalTerms: result.totalTerms || 0
                                };
                                if (job.status === 'Succeeded' || job.status === 'Completed') {
                                    clearInterval(pollTimer);
                                    indexing.value = false;
                                    message.success('词法索引建立完成');
                                    await loadSummary();
                                } else if (job.status === 'Failed' || job.status === 'Cancelled') {
                                    clearInterval(pollTimer);
                                    indexing.value = false;
                                    message.error(`词法索引构建终止: ${job.status}`);
                                    await loadSummary();
                                }
                            }
                        } catch {
                            clearInterval(pollTimer);
                            indexing.value = false;
                            await loadSummary();
                        }
                    }, 1000);
                } else {
                    indexProgress.value = {
                        progressPercent: result.progressPercent || 100,
                        status: 'success',
                        message: result.message,
                        totalDocuments: result.totalDocuments,
                        totalTerms: result.totalTerms
                    };
                    message.success('词法索引建立完成');
                    await loadSummary();
                    indexing.value = false;
                }
            } catch (error) {
                console.error('词法索引建立失败:', error);
                indexProgress.value = {
                    progressPercent: 0,
                    status: 'error',
                    message: `索引失败: ${error.response?.data?.error || error.message}`,
                    totalDocuments: 0,
                    totalTerms: 0
                };
                message.error(`索引失败: ${error.response?.data?.error || error.message}`);
                indexing.value = false;
            }
        };

        const handleClear = async () => {
            clearing.value = true;
            try {
                // 原实现把「全部」折叠成 null，后端据此选中第一个库 → 页面显示单库数字。
        // 现在统一转发显式哨兵，后端才能走聚合分支。
                const targetKb = selectedKbId.value || '__all__';
                await bm25IndexApi.clearIndex(targetKb);
                message.success('词法索引已清空');
                showClearDialog.value = false;
                await loadSummary();
            } catch (error) {
                console.error('清空失败:', error);
                message.error(`清空失败: ${error.response?.data?.error || error.message}`);
            } finally {
                clearing.value = false;
            }
        };

        const handleSearch = async () => {
            if (!searchForm.value.query.trim()) {
                message.error('请输入查询语句');
                return;
            }
            searching.value = true;
            try {
                // 如果选定了知识库，走知识库 FTS5 词法检索
                if (selectedKbId.value && selectedKbId.value !== '__all__') {
                    const start = performance.now();
                    const req = knowledgeBaseRequest(searchForm.value.query, [selectedKbId.value], searchForm.value.topK, 'Lexical');
                    const res = await knowledgeBaseSearchApi.search(req);
                    const mapped = knowledgeBaseResponse(res.data, 'Lexical');
                    searchDuration.value = Math.round(performance.now() - start);
                    searchResults.value = (mapped.chunks || []).map((c, idx) => ({
                        rank: idx + 1,
                        chunkId: c.refId,
                        content: c.content,
                        score: c.score,
                        source: c.filePath
                    }));
                } else if (selectedKbId.value === '__all__' && knowledgeBases.value.length > 0) {
                    const start = performance.now();
                    const allKbIds = knowledgeBases.value.map(k => k.id);
                    const req = knowledgeBaseRequest(searchForm.value.query, allKbIds, searchForm.value.topK, 'Lexical');
                    const res = await knowledgeBaseSearchApi.search(req);
                    const mapped = knowledgeBaseResponse(res.data, 'Lexical');
                    searchDuration.value = Math.round(performance.now() - start);
                    searchResults.value = (mapped.chunks || []).map((c, idx) => ({
                        rank: idx + 1,
                        chunkId: c.refId,
                        content: c.content,
                        score: c.score,
                        source: c.filePath
                    }));
                } else {
                    const response = await bm25IndexApi.search(searchForm.value.query, searchForm.value.topK);
                    const result = response.data;
                    searchResults.value = result.results || [];
                    searchDuration.value = result.durationMs || 0;
                }
            } catch (error) {
                console.error('词法搜索失败:', error);
                message.error(`搜索失败: ${error.response?.data?.error || error.message}`);
                searchResults.value = [];
            } finally {
                searching.value = false;
            }
        };

        watch(selectedKbId, () => {
            loadSummary();
        });

        onMounted(async () => {
            await Promise.all([loadSummary(), loadKnowledgeBases()]);
        });

        onActivated(async () => {
            await Promise.all([loadSummary(), loadKnowledgeBases()]);
        });

        const __returned__ = {
            message, loading, indexing, clearing, searching, showClearDialog, summary,
            indexProgress, searchResults, searchDuration, searchForm, knowledgeBases,
            selectedKbId, kbSpecMap, kbOptions, loadKnowledgeBases, loadSummary, handleIndexAll, scopeLabel,
            handleClear, handleSearch,
            get NButton() { return NButton; },
            get NSpace() { return NSpace; },
            get NTag() { return NTag; },
            get NInput() { return NInput; },
            get NInputNumber() { return NInputNumber; },
            get NForm() { return NForm; },
            get NFormItem() { return NFormItem; },
            get NAlert() { return NAlert; },
            get NCard() { return NCard; },
            get NList() { return NList; },
            get NListItem() { return NListItem; },
            get NThing() { return NThing; },
            get NProgress() { return NProgress; },
            get NModal() { return NModal; },
            get NDescriptions() { return NDescriptions; },
            get NDescriptionsItem() { return NDescriptionsItem; },
            get NText() { return NText; },
            get NIcon() { return NIcon; },
            get NEllipsis() { return NEllipsis; },
            get NEmpty() { return NEmpty; },
            get NSpin() { return NSpin; },
            get NTabs() { return NTabs; },
            get NTabPane() { return NTabPane; },
            get NSelect() { return NSelect; },
            get RefreshOutline() { return RefreshOutline; },
            get TrashOutline() { return TrashOutline; },
            get SearchOutline() { return SearchOutline; },
            get LayersOutline() { return LayersOutline; }
        };
        return __returned__;
    }
});

component.template = `
  <n-space vertical :size="20">
    <!-- Target KnowledgeBase Selector Bar -->
    <n-card size="small">
      <n-space align="center" justify="space-between">
        <n-space align="center" :size="12">
          <n-icon :component="LayersOutline" size="20" />
          <n-text strong>目标知识库作用域：</n-text>
          <n-select
            v-model:value="selectedKbId"
            :options="kbOptions"
            style="width: 320px;"
            size="small"
            placeholder="选择测试或诊断的知识库"
          />
        </n-space>
        <n-space align="center" :size="8">
          <n-tag size="small" round :bordered="false" type="success">{{ scopeLabel }}</n-tag>
          <n-tag type="info" size="small" round secondary>
            SQLite FTS5 + SAG-Wiki 多列加权 (标题 5.0x / 章节 2.5x / 正文 1.0x)
          </n-tag>
        </n-space>
      </n-space>
    </n-card>

    <!-- BM25 Index Management -->
    <n-card title="全文词法索引管理 (BM25 / FTS5)">
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
                <n-descriptions-item label="统计口径">
                  <n-text depth="3" style="font-size: 12px">
                    平均长度按分块数加权；{{ scopeLabel }}
                  </n-text>
                </n-descriptions-item>
                <n-descriptions-item label="BM25F 多列权重">
                  <n-space :size="4">
                    <n-tag size="small" type="primary" :bordered="false">标题 5.0x</n-tag>
                    <n-tag size="small" type="info" :bordered="false">章节 2.5x</n-tag>
                    <n-tag size="small" type="default" :bordered="false">正文 1.0x</n-tag>
                  </n-space>
                </n-descriptions-item>
              </n-descriptions>

              <!-- 逐库明细：聚合值可被拆解核对（全量作用域才有意义） -->
              <n-table v-if="summary.scope === 'all' && summary.knowledgeBases?.length" :bordered="false" :single-line="false" style="margin-top: 12px">
                <thead>
                  <tr>
                    <th>知识库</th>
                    <th>分块数</th>
                    <th>文件数</th>
                    <th>平均长度</th>
                  </tr>
                </thead>
                <tbody>
                  <tr v-for="k in summary.knowledgeBases" :key="k.knowledgeBaseId">
                    <td>{{ (knowledgeBases.find(x => x.id === k.knowledgeBaseId) || {}).name || k.knowledgeBaseId }}</td>
                    <td>{{ k.totalChunks }}</td>
                    <td>{{ k.totalDocuments }}</td>
                    <td>{{ k.averageDocLength?.toFixed(2) }}</td>
                  </tr>
                </tbody>
              </n-table>
            </n-spin>
          </n-space>
        </n-tab-pane>

        <!-- Index Operations Tab -->
        <n-tab-pane name="operations" tab="词法索引操作">
          <n-space vertical :size="16">
            <n-alert type="info">
              <template #header>SAG-Wiki 风格多列 BM25F 倒排索引架构</template>
              <n-space vertical>
                <n-text>在多知识库物理隔离架构下，每个知识库在内部独立构建 FTS5 结构化多列全文索引（标题 5.0x / 章节 2.5x / 正文 1.0x），结合 jieba.NET 中文精确断词与专有名词/短语增强。</n-text>
                <n-text>词法索引无需调用昂贵的嵌入模型，具备亚毫秒级检索响应，并在 Top-1 优势比 > 0.55 时支持级联极速早停短路。</n-text>
              </n-space>
            </n-alert>

            <n-space>
              <n-button
                type="primary"
                :loading="indexing"
                @click="handleIndexAll"
              >
                <template #icon><n-icon :component="RefreshOutline" /></template>
                重建词法索引
              </n-button>
              <n-button
                type="error"
                :loading="clearing"
                @click="showClearDialog = true"
              >
                <template #icon><n-icon :component="TrashOutline" /></template>
                清空词法索引
              </n-button>
              <n-button @click="loadSummary" :loading="loading">
                <template #icon><n-icon :component="RefreshOutline" /></template>
                刷新概览
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
        <n-tab-pane name="search" tab="词法搜索诊断">
          <n-space vertical :size="16">
            <n-form :model="searchForm" inline>
              <n-form-item label="查询语句">
                <n-input
                  v-model:value="searchForm.query"
                  placeholder="输入中文或英文查询关键词..."
                  style="width: 400px"
                  @keydown.enter="handleSearch"
                />
              </n-form-item>
              <n-form-item label="TopK">
                <n-input-number
                  v-model:value="searchForm.topK"
                  :min="1"
                  :max="100"
                  style="width: 100px"
                />
              </n-form-item>
              <n-form-item>
                <n-button type="primary" :loading="searching" @click="handleSearch">
                  <template #icon><n-icon :component="SearchOutline" /></template>
                  词法检索
                </n-button>
              </n-form-item>
            </n-form>

            <!-- Search results -->
            <n-card v-if="searchResults.length > 0" title="检索召回结果">
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
                        <n-tag size="small" type="primary" :bordered="false">BM25F</n-tag>
                        <n-tag type="success" size="small">Score: {{ typeof result.score === 'number' ? result.score.toFixed(4) : result.score }}</n-tag>
                        <n-tag v-if="result.source" size="small" secondary>{{ result.source }}</n-tag>
                      </n-space>
                    </template>
                    <n-ellipsis :line-clamp="3" expand-trigger="click">
                      {{ result.content }}
                    </n-ellipsis>
                  </n-thing>
                </n-list-item>
              </n-list>
            </n-card>

            <n-empty v-else-if="searchForm.query && !searching" description="暂无匹配分块" />
          </n-space>
        </n-tab-pane>
      </n-tabs>
    </n-card>

    <!-- Clear Confirm Dialog -->
    <n-modal v-model:show="showClearDialog" preset="dialog" title="清空索引确认" style="max-width: 95vw">
      <n-alert type="error">
        <template #header>警告</template>
        清空索引将删除词法索引数据，此操作不可恢复！
      </n-alert>
      <template #action>
        <n-button @click="showClearDialog = false">取消</n-button>
        <n-button type="error" :loading="clearing" @click="handleClear">确认清空</n-button>
      </template>
    </n-modal>
  </n-space>
`;

export default component;
