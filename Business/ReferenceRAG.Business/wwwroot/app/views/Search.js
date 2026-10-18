import { defineComponent as _defineComponent } from 'vue';
import { ref, computed, onMounted, watch } from 'vue';
import { useMessage } from 'naive-ui';
import { SearchOutline, ArrowDownOutline, OpenOutline, CloseOutline, RefreshOutline, CubeOutline, GitMergeOutline, LayersOutline, OptionsOutline } from '@vicons/ionicons5';
import { aiQueryApi, sourcesApi, pathsApi } from '/app/api/index.js';
const component = /*@__PURE__*/ _defineComponent({
    __name: 'Search',
    setup(__props, { expose: __expose }) {
        __expose();
        const message = useMessage();
        const searchQuery = ref('');
        const loading = ref(false);
        const searched = ref(false);
        const searchError = ref('');
        const showAdvanced = ref(false);
        const searchResponse = ref(null);
        const sources = ref([]);
        const paths = ref([]);
        const selectedPaths = ref([]);
        const pathsLoading = ref(false);
        const searchStatus = ref(null);
        // Mode default configurations - keep UI small and let backend fill the rest
        const modeDefaults = {
            Quick: { topK: 5, enableRerank: false },
            Standard: { topK: 10, enableRerank: null },
            Deep: { topK: 20, enableRerank: true },
            Hybrid: { topK: 15, enableRerank: null },
            HybridRerank: { topK: 10, enableRerank: true }
        };
        // Default options for reset
        const defaultOptions = {
            mode: 'HybridRerank',
            topK: 10,
            enableRerank: true,
            sources: [],
            folders: []
        };
        const searchOptions = ref({ ...defaultOptions });
        // Watch mode changes and update defaults
        watch(() => searchOptions.value.mode, (newMode) => {
            const defaults = modeDefaults[newMode];
            searchOptions.value.topK = defaults.topK;
            searchOptions.value.enableRerank = defaults.enableRerank;
        });
        // Check if current options are default
        const isDefaultOptions = computed(() => {
            return (searchOptions.value.mode === defaultOptions.mode &&
                searchOptions.value.topK === defaultOptions.topK &&
                searchOptions.value.enableRerank === defaultOptions.enableRerank &&
                searchOptions.value.sources.length === 0 &&
                searchOptions.value.folders.length === 0);
        });
        const sourceOptions = computed(() => sources.value.map(s => ({ label: s.name, value: s.name })));
        // 将路径数据转换为树形结构供 n-tree 使用
        const pathTreeData = computed(() => {
            return paths.value.map(source => ({
                key: `source:${source.name}`,
                label: source.name,
                children: source.folders.map(folder => ({
                    key: `folder:${folder}`,
                    label: folder
                }))
            }));
        });
        // 将选中的路径 key 转换为 folder 路径（包括直接选中的数据源及其子目录）
        const selectedPathFolders = computed(() => {
            const result = new Set();
            for (const key of selectedPaths.value) {
                if (key.startsWith('folder:')) {
                    result.add(key.replace('folder:', ''));
                } else if (key.startsWith('source:')) {
                    const sourceName = key.replace('source:', '');
                    const source = paths.value.find(s => s.name === sourceName);
                    if (source?.rootPath) {
                        result.add(source.rootPath);
                    }
                    if (source?.folders?.length) {
                        source.folders.forEach(f => result.add(f));
                    }
                }
            }
            return Array.from(result);
        });
        // n-tree 选中变化时处理
        const handlePathSelect = (keys) => {
            selectedPaths.value = keys;
            searchOptions.value.folders = selectedPathFolders.value;
        };
        const showDrilldown = ref(false);
        const drilldownLoading = ref(false);
        const drilldownResponse = ref(null);
        const phaseItems = (p) => [
            { label: '模型加载', ms: p.modelLoadMs, cold: true },
            { label: '嵌入', ms: p.embedMs },
            { label: 'BM25', ms: p.bm25Ms },
            { label: '向量', ms: p.vectorMs },
            { label: '混合融合', ms: p.hybridMs },
            { label: '标题', ms: p.titleMs },
            { label: '图扩展', ms: p.graphMs },
            { label: '重排', ms: p.rerankMs },
        ];
        const hasAnyPhase = (p) => (p.modelLoadMs || 0) > 0 || (p.embedMs || 0) > 0 || (p.bm25Ms || 0) > 0 || (p.titleMs || 0) > 0 || (p.vectorMs || 0) > 0 || (p.hybridMs || 0) > 0 || (p.graphMs || 0) > 0 || (p.rerankMs || 0) > 0;
        const getScoreType = (score) => {
            if (score >= 0.8)
                return 'success';
            if (score >= 0.5)
                return 'warning';
            return 'error';
        };
        const truncateContent = (content, maxLen = 300) => {
            if (!content)
                return '';
            return content.length > maxLen ? content.substring(0, maxLen) + '...' : content;
        };
        const getStrategyDisplayName = (name) => {
            if (!name)
                return '默认策略';
            switch (name) {
                case 'HybridRerank':
                case 'HybridRerankSearchStrategy': return '混合多路重排 (BM25 + 向量 + 重排)';
                case 'Hybrid':
                case 'HybridSearchStrategy': return '双路混合融合 (BM25 + 向量)';
                case 'Standard':
                case 'StandardSearchStrategy': return '标准向量检索 (Dense)';
                case 'Quick':
                case 'QuickSearchStrategy': return '快速检索 (Fast)';
                case 'Deep':
                case 'DeepSearchStrategy': return '深度混合重排 (Deep BM25 + 向量 + 重排)';
                default: return name;
            }
        };
        const getStepTagType = (phase) => {
            switch (phase) {
                case 'QueryPrepared': return 'info';
                case 'ParallelRetrieving': return 'primary';
                case 'HybridFusing': return 'warning';
                case 'GraphExpanding': return 'default';
                case 'Reranking': return 'success';
                case 'AssemblingResults': return 'info';
                case 'Completed': return 'success';
                default: return 'default';
            }
        };
        const displayChunks = computed(() => {
            if (!searchResponse.value?.chunks)
                return [];
            return searchResponse.value.chunks.filter(c => {
                if (c.content) {
                    const text = c.content.trim();
                    if (/^={3,}\s*(?:\(\d+(?:-\d+)?\))?\s*={3,}$/.test(text))
                        return false;
                    if (/^[-*#~=]{3,}$/.test(text))
                        return false;
                }
                return true;
            });
        });
        // Handle Enter key: Shift+Enter for newline, Enter for search
        const handleKeyDown = (e) => {
            if (e.shiftKey) {
                return;
            }
            e.preventDefault();
            handleSearch();
        };
        // Clear search query
        const handleClearQuery = () => {
            searchQuery.value = '';
        };
        // Reset all search options to default
        const handleReset = () => {
            searchOptions.value = { ...defaultOptions };
            selectedPaths.value = [];
            searchQuery.value = '';
            searchResponse.value = null;
            searched.value = false;
            searchError.value = '';
        };
        const handleSearch = async () => {
            if (!searchQuery.value.trim())
                return;
            loading.value = true;
            searched.value = true;
            searchError.value = '';
            try {
                const response = await aiQueryApi.query({
                    query: searchQuery.value,
                    mode: searchOptions.value.mode,
                    topK: searchOptions.value.topK,
                    enableRerank: searchOptions.value.enableRerank,
                    sources: searchOptions.value.sources.length > 0 ? searchOptions.value.sources : undefined,
                    filters: searchOptions.value.folders.length > 0 ? { folders: searchOptions.value.folders } : undefined
                });
                searchResponse.value = response.data;
                await loadSearchStatus();
            }
            catch (error) {
                console.error('Search failed:', error);
                searchResponse.value = null;
                const msg = error?.response?.data?.message || error?.error || error?.message || '搜索请求执行失败，请查看服务端日志';
                searchError.value = msg;
                message?.error?.(`搜索失败: ${msg}`);
            }
            finally {
                loading.value = false;
            }
        };
        const handleDrilldown = async (chunk) => {
            showDrilldown.value = true;
            drilldownLoading.value = true;
            drilldownResponse.value = null;
            try {
                const response = await aiQueryApi.drilldown({
                    query: searchQuery.value,
                    refIds: [chunk.refId || '']
                });
                drilldownResponse.value = response.data;
            }
            catch (error) {
                console.error('Drilldown failed:', error);
            }
            finally {
                drilldownLoading.value = false;
            }
        };
        const loadSources = async () => {
            try {
                const response = await sourcesApi.getAll();
                sources.value = response.data;
            }
            catch (error) {
                console.error('Failed to load sources:', error);
            }
        };
        const loadPaths = async () => {
            pathsLoading.value = true;
            try {
                const response = await pathsApi.getPaths();
                paths.value = response.data.sources;
            }
            catch (error) {
                console.error('Failed to load paths:', error);
            }
            finally {
                pathsLoading.value = false;
            }
        };
        const loadSearchStatus = async () => {
            try {
                const response = await aiQueryApi.getSearchStatus();
                searchStatus.value = response.data;
            }
            catch (error) {
                console.error('Failed to load search status:', error);
            }
        };
        onMounted(() => {
            loadSources();
            loadPaths();
            loadSearchStatus();
        });
        const __returned__ = { searchQuery, loading, searched, searchError, showAdvanced, searchResponse, sources, paths, selectedPaths, pathsLoading, searchStatus, modeDefaults, defaultOptions, searchOptions, isDefaultOptions, sourceOptions, pathTreeData, selectedPathFolders, handlePathSelect, showDrilldown, drilldownLoading, drilldownResponse, phaseItems, hasAnyPhase, getScoreType, truncateContent, handleKeyDown, handleClearQuery, handleReset, handleSearch, handleDrilldown, loadSources, loadPaths, loadSearchStatus, getStrategyDisplayName, getStepTagType, displayChunks, get SearchOutline() { return SearchOutline; }, get ArrowDownOutline() { return ArrowDownOutline; }, get OpenOutline() { return OpenOutline; }, get CloseOutline() { return CloseOutline; }, get RefreshOutline() { return RefreshOutline; }, get CubeOutline() { return CubeOutline; }, get GitMergeOutline() { return GitMergeOutline; }, get LayersOutline() { return LayersOutline; }, get OptionsOutline() { return OptionsOutline; } };
        return __returned__;
    }
});

component.template = "\n  <n-space vertical :size=\"20\">\n    <!-- Model & Index Status -->\n    <n-card size=\"small\">\n      <n-space align=\"center\" justify=\"space-between\">\n        <n-space align=\"center\" :size=\"24\">\n          <!-- Embedding Model -->\n          <n-space align=\"center\" :size=\"8\">\n            <n-icon :component=\"CubeOutline\" size=\"18\" />\n            <n-text>嵌入模型:</n-text>\n            <template v-if=\"searchStatus?.embeddingModel\">\n              <n-tag type=\"success\" size=\"small\" round>\n                {{ searchStatus.embeddingModel }} ({{ searchStatus.embeddingDimension }}d)\n              </n-tag>\n              <n-tag v-if=\"searchStatus.embeddingVariant\" size=\"small\" :bordered=\"false\" round type=\"info\">\n                {{ searchStatus.embeddingVariant }}\n              </n-tag>\n              <n-tag size=\"small\" :type=\"searchStatus.embeddingIsGpu ? 'success' : 'default'\" :bordered=\"false\" round>\n                {{ searchStatus.embeddingDevice || (searchStatus.embeddingIsGpu ? 'GPU' : 'CPU') }}\n              </n-tag>\n              <n-tag size=\"small\" :type=\"searchStatus.embeddingState === '就绪' || searchStatus.embeddingState === 'Ready' ? 'success' : 'warning'\" :bordered=\"false\" round>\n                {{ searchStatus.embeddingState || '就绪' }}\n              </n-tag>\n            </template>\n            <n-tag v-else type=\"warning\" size=\"small\" round>未配置</n-tag>\n          </n-space>\n\n          <!-- Rerank Model -->\n          <n-space align=\"center\" :size=\"8\">\n            <n-icon :component=\"GitMergeOutline\" size=\"18\" />\n            <n-text>重排模型:</n-text>\n            <template v-if=\"searchStatus?.rerankEnabled && searchStatus?.rerankModel\">\n              <n-tag type=\"success\" size=\"small\" round>\n                {{ searchStatus.rerankModel }}\n              </n-tag>\n              <n-tag v-if=\"searchStatus.rerankVariant\" size=\"small\" :bordered=\"false\" round type=\"info\">\n                {{ searchStatus.rerankVariant }}\n              </n-tag>\n              <n-tag size=\"small\" :type=\"searchStatus.rerankIsGpu ? 'success' : 'default'\" :bordered=\"false\" round>\n                {{ searchStatus.rerankDevice || (searchStatus.rerankIsGpu ? 'GPU' : 'CPU') }}\n              </n-tag>\n              <n-tag size=\"small\" :type=\"searchStatus.rerankLoaded ? 'success' : 'default'\" :bordered=\"false\" round>\n                {{ searchStatus.rerankState || (searchStatus.rerankLoaded ? '已加载' : '未加载') }}\n              </n-tag>\n            </template>\n            <n-tag v-else type=\"default\" size=\"small\" round>未启用</n-tag>\n          </n-space>\n\n          <!-- BM25 Index -->\n          <n-space align=\"center\" :size=\"8\">\n            <n-icon :component=\"SearchOutline\" size=\"18\" />\n            <n-text>BM25:</n-text>\n            <n-tag v-if=\"searchStatus?.bm25HasIndex\" type=\"success\" size=\"small\" round>\n              {{ searchStatus.bm25IndexedDocuments }} 文档\n            </n-tag>\n            <n-tag v-else type=\"warning\" size=\"small\" round>无索引</n-tag>\n          </n-space>\n\n          <!-- Vector Index -->\n          <n-space align=\"center\" :size=\"8\">\n            <n-icon :component=\"LayersOutline\" size=\"18\" />\n            <n-text>向量:</n-text>\n            <n-tag v-if=\"searchStatus?.indexRequiresRebuild\" type=\"error\" size=\"small\" round>\n              模型不匹配，需重建\n            </n-tag>\n            <n-tag v-else-if=\"searchStatus?.vectorHasIndex\" type=\"success\" size=\"small\" round>\n              {{ searchStatus.vectorIndexedChunks }} 分块\n            </n-tag>\n            <n-tag v-else type=\"warning\" size=\"small\" round>无索引</n-tag>\n          </n-space>\n        </n-space>\n\n        <!-- Total Files -->\n        <n-text depth=\"3\">共 {{ searchStatus?.totalFiles ?? 0 }} 个文件</n-text>\n      </n-space>\n    </n-card>\n\n    <!-- Search Input + Options -->\n    <n-card>\n      <div style=\"display: flex; flex-direction: column; gap: 10px\">\n        <!-- Textarea + Button -->\n        <div style=\"display: flex; align-items: flex-start; gap: 12px\">\n          <n-input\n            v-model:value=\"searchQuery\"\n            type=\"textarea\"\n            placeholder=\"输入搜索内容... (Enter 搜索, Shift+Enter 换行)\"\n            :rows=\"2\"\n            autosize\n            style=\"flex: 1; min-width: 0\"\n            @keydown.enter=\"handleKeyDown\"\n          >\n            <template #suffix>\n              <n-button\n                v-if=\"searchQuery\"\n                text\n                @click=\"handleClearQuery\"\n                style=\"padding: 0 4px\"\n              >\n                <template #icon>\n                  <n-icon :component=\"CloseOutline\" />\n                </template>\n              </n-button>\n            </template>\n          </n-input>\n          <n-button type=\"primary\" :loading=\"loading\" @click=\"handleSearch\" :disabled=\"!searchQuery.trim()\">\n            <template #icon><n-icon :component=\"SearchOutline\" /></template>\n            搜索\n          </n-button>\n        </div>\n\n        <!-- Mode toggle + Advanced -->\n        <div style=\"display: flex; align-items: center; justify-content: space-between\">\n          <n-radio-group v-model:value=\"searchOptions.mode\" size=\"small\">\n            <n-radio-button value=\"Quick\" label=\"快速\" />\n            <n-radio-button value=\"HybridRerank\" label=\"平衡\" />\n            <n-radio-button value=\"Deep\" label=\"深度\" />\n          </n-radio-group>\n          <n-button text size=\"small\" @click=\"showAdvanced = !showAdvanced\">\n            <template #icon>\n              <n-icon :component=\"OptionsOutline\" />\n            </template>\n            {{ showAdvanced ? '收起' : '高级' }}\n          </n-button>\n        </div>\n\n        <!-- Advanced collapse -->\n        <n-collapse-transition :show=\"showAdvanced\">\n          <n-divider style=\"margin: 0 0 12px\" />\n          <n-grid :cols=\"3\" :x-gap=\"20\">\n            <n-gi>\n              <n-form-item label=\"返回数量\">\n                <n-input-number v-model:value=\"searchOptions.topK\" :min=\"1\" :max=\"50\" style=\"width: 100%\" />\n              </n-form-item>\n            </n-gi>\n            <n-gi>\n              <n-form-item label=\"源筛选\">\n                <n-select\n                  v-model:value=\"searchOptions.sources\"\n                  :options=\"sourceOptions\"\n                  multiple\n                  placeholder=\"全部源\"\n                  clearable\n                />\n              </n-form-item>\n            </n-gi>\n            <n-gi>\n              <n-form-item label=\"路径过滤\">\n                <n-tree\n                  v-model:selected-keys=\"selectedPaths\"\n                  :data=\"pathTreeData\"\n                  :loading=\"pathsLoading\"\n                  selectable\n                  multiple\n                  clearable\n                  placeholder=\"全部路径\"\n                  style=\"max-height: 200px; overflow-y: auto\"\n                  @update:selected-keys=\"handlePathSelect\"\n                />\n              </n-form-item>\n            </n-gi>\n          </n-grid>\n          <div style=\"text-align: right; margin-top: -8px\">\n            <n-button text size=\"small\" @click=\"handleReset\" :disabled=\"isDefaultOptions\">\n              <template #icon><n-icon :component=\"RefreshOutline\" /></template>\n              重置为默认\n            </n-button>\n          </div>\n        </n-collapse-transition>\n\n        <n-text depth=\"3\" style=\"font-size: 12px\">Enter 搜索 | Shift+Enter 换行</n-text>\n      </div>\n    </n-card>\n\n    <!-- Search Stats -->\n    <n-card v-if=\"searchResponse\" :title=\"`搜索结果 (${displayChunks.length} 条有效匹配)`\">\n      <template #header-extra>\n        <n-space size=\"small\">\n          <n-tag size=\"small\">耗时 {{ searchResponse.stats.durationMs }}ms</n-tag>\n          <n-tag size=\"small\">~{{ searchResponse.stats.estimatedTokens }} tokens</n-tag>\n        </n-space>\n      </template>\n\n      <!-- Pipeline Telemetry & State Machine Board -->\n      <n-card v-if=\"searchResponse.stats.pipelineTelemetry\" size=\"small\" embedded style=\"margin-bottom: 12px; background: rgba(0, 0, 0, 0.02)\">\n        <n-space vertical :size=\"8\">\n          <div style=\"display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: 8px\">\n            <n-space align=\"center\" :size=\"8\">\n              <n-icon :component=\"GitMergeOutline\" size=\"16\" />\n              <n-text strong style=\"font-size: 13px\">搜索执行策略:</n-text>\n              <n-tag type=\"primary\" size=\"small\" :bordered=\"false\" round>\n                {{ getStrategyDisplayName(searchResponse.stats.pipelineTelemetry.strategyName) }}\n              </n-tag>\n            </n-space>\n            <n-space align=\"center\" :size=\"12\">\n              <span v-if=\"searchResponse.stats.pipelineTelemetry.embeddingModel\" style=\"font-size: 12px; color: #666\">\n                嵌入: <b>{{ searchResponse.stats.pipelineTelemetry.embeddingModel.modelName }}</b> [{{ searchResponse.stats.pipelineTelemetry.embeddingModel.variant }} / {{ searchResponse.stats.pipelineTelemetry.embeddingModel.device }}]\n              </span>\n              <span v-if=\"searchResponse.stats.pipelineTelemetry.rerankModel\" style=\"font-size: 12px; color: #666\">\n                重排: <b>{{ searchResponse.stats.pipelineTelemetry.rerankModel.modelName }}</b> [{{ searchResponse.stats.pipelineTelemetry.rerankModel.variant }} / {{ searchResponse.stats.pipelineTelemetry.rerankModel.device }}]\n              </span>\n            </n-space>\n          </div>\n\n          <!-- Phase Trace Row (耗时类型明细) -->\n          <div v-if=\"searchResponse.stats.phases && hasAnyPhase(searchResponse.stats.phases)\"\n               style=\"display: flex; align-items: center; gap: 8px; flex-wrap: wrap; margin-top: 2px\">\n            <n-text depth=\"3\" style=\"font-size: 12px; white-space: nowrap\">耗时类型:</n-text>\n            <template v-for=\"p in phaseItems(searchResponse.stats.phases)\" :key=\"p.label\">\n              <n-tag\n                v-if=\"p.ms > 0\"\n                size=\"small\"\n                :type=\"p.cold ? 'warning' : p.ms > 500 ? 'error' : p.ms > 200 ? 'warning' : 'default'\"\n                :bordered=\"false\"\n                round\n              >\n                {{ p.label }} {{ p.ms }}ms\n              </n-tag>\n            </template>\n            <n-tag v-if=\"searchResponse.stats.phases.modelLoadMs > 0\" size=\"small\" type=\"info\" :bordered=\"false\" round>\n              纯检索 {{ Math.max(0, searchResponse.stats.durationMs - searchResponse.stats.phases.modelLoadMs) }}ms\n            </n-tag>\n          </div>\n\n          <!-- State Machine Steps Timeline (状态机流转时序) -->\n          <div v-if=\"searchResponse.stats.pipelineTelemetry.steps && searchResponse.stats.pipelineTelemetry.steps.length > 0\"\n               style=\"display: flex; align-items: center; gap: 6px; flex-wrap: wrap; margin-top: 2px\">\n            <n-text depth=\"3\" style=\"font-size: 12px; white-space: nowrap\">状态机流转:</n-text>\n            <template v-for=\"(step, idx) in searchResponse.stats.pipelineTelemetry.steps\" :key=\"step.phase\">\n              <n-tag size=\"small\" :type=\"getStepTagType(step.phase)\" :bordered=\"false\" round :title=\"step.details || step.phaseName\">\n                {{ step.phaseName || step.phase }} {{ step.durationMs }}ms ({{ step.outputCandidates !== undefined && step.outputCandidates !== null ? step.outputCandidates : (step.inputCandidates || 0) }}条)\n              </n-tag>\n              <span v-if=\"idx < searchResponse.stats.pipelineTelemetry.steps.length - 1\" style=\"color: #999; font-size: 12px\">→</span>\n            </template>\n          </div>\n        </n-space>\n      </n-card>\n\n      <!-- Legacy Phase Trace Row (fallback if pipelineTelemetry not present) -->\n      <div v-else-if=\"searchResponse.stats.phases && hasAnyPhase(searchResponse.stats.phases)\"\n           style=\"display: flex; align-items: center; gap: 8px; margin-bottom: 12px; flex-wrap: wrap\">\n        <n-text depth=\"3\" style=\"font-size: 12px; white-space: nowrap\">耗时类型:</n-text>\n        <template v-for=\"p in phaseItems(searchResponse.stats.phases)\" :key=\"p.label\">\n          <n-tag\n            v-if=\"p.ms > 0\"\n            size=\"small\"\n            :type=\"p.cold ? 'warning' : p.ms > 500 ? 'error' : p.ms > 200 ? 'warning' : 'default'\"\n            :bordered=\"false\"\n            round\n          >\n            {{ p.label }} {{ p.ms }}ms\n          </n-tag>\n        </template>\n        <n-tag v-if=\"searchResponse.stats.phases.modelLoadMs > 0\" size=\"small\" type=\"info\" :bordered=\"false\" round>\n          纯检索 {{ Math.max(0, searchResponse.stats.durationMs - searchResponse.stats.phases.modelLoadMs) }}ms\n        </n-tag>\n      </div>\n\n      <!-- Context -->\n      <n-card v-if=\"searchResponse.context\" title=\"组装上下文\" size=\"small\" style=\"margin-bottom: 16px\">\n        <n-input\n          :value=\"searchResponse.context\"\n          type=\"textarea\"\n          :autosize=\"{ minRows: 2, maxRows: 6 }\"\n          readonly\n        />\n      </n-card>\n\n      <!-- Chunk Results -->\n      <n-list>\n        <n-list-item v-for=\"chunk in displayChunks\" :key=\"chunk.refId\">\n          <n-thing>\n            <template #header>\n              <n-space align=\"center\">\n                <n-tag v-if=\"chunk.source\" size=\"small\" :bordered=\"false\" type=\"info\">{{ chunk.source }}</n-tag>\n                <n-text>{{ chunk.title || chunk.filePath }}</n-text>\n                <n-text v-if=\"chunk.headingPath\" depth=\"3\" style=\"font-size: 12px\">/ {{ chunk.headingPath }}</n-text>\n              </n-space>\n            </template>\n            <template #header-extra>\n              <n-space size=\"small\">\n                <n-tag v-if=\"chunk.bm25Score !== undefined\" :type=\"getScoreType(chunk.bm25Score / 15)\" size=\"small\" title=\"BM25关键词分数\">\n                  BM25: {{ chunk.bm25Score.toFixed(1) }}\n                </n-tag>\n                <n-tag :type=\"getScoreType(chunk.score)\" size=\"small\" title=\"融合分数\">\n                  {{ (chunk.score * 100).toFixed(1) }}%\n                </n-tag>\n              </n-space>\n            </template>\n            <template #description>\n              <n-text depth=\"3\" style=\"white-space: pre-wrap; line-height: 1.6; font-size: 13px\">\n                {{ truncateContent(chunk.content || '') }}\n              </n-text>\n              <n-text v-if=\"chunk.startLine > 0\" depth=\"3\" style=\"display: block; margin-top: 4px; font-size: 12px\">\n                行 {{ chunk.startLine }}-{{ chunk.endLine }}\n              </n-text>\n              <n-space style=\"margin-top: 8px\">\n                <n-button text size=\"small\" @click=\"handleDrilldown(chunk)\">\n                  <template #icon><n-icon :component=\"ArrowDownOutline\" /></template>\n                  深入查询\n                </n-button>\n                <n-button\n                  v-if=\"chunk.obsidianLink\"\n                  text\n                  size=\"small\"\n                  tag=\"a\"\n                  :href=\"chunk.obsidianLink\"\n                  target=\"_blank\"\n                >\n                  <template #icon><n-icon :component=\"OpenOutline\" /></template>\n                  在 Obsidian 中打开\n                </n-button>\n              </n-space>\n            </template>\n          </n-thing>\n        </n-list-item>\n      </n-list>\n\n      <!-- Related Files -->\n      <template v-if=\"searchResponse.files.length > 0\">\n        <n-divider>相关文件</n-divider>\n        <n-space>\n          <n-tag v-for=\"file in searchResponse.files\" :key=\"file.id\" size=\"small\" round>\n            {{ file.title || file.path }} ({{ file.chunkCount }})\n          </n-tag>\n        </n-space>\n      </template>\n    </n-card>\n\n    <!-- Error State -->\n    <n-card v-if=\"searchError && !loading\" size=\"small\">\n      <n-alert type=\"error\" title=\"搜索失败\" :bordered=\"false\">\n        {{ searchError }}\n      </n-alert>\n    </n-card>\n\n    <!-- Empty State -->\n    <n-card v-else-if=\"searched && !loading && !searchError\">\n      <n-empty description=\"没有找到匹配的结果\" />\n    </n-card>\n\n    <!-- Drilldown Dialog -->\n    <n-modal v-model:show=\"showDrilldown\" preset=\"card\" title=\"深入查询\" style=\"width: 70vw\">\n      <n-spin :show=\"drilldownLoading\">\n        <n-space v-if=\"drilldownResponse\" vertical>\n          <n-card title=\"扩展上下文\" size=\"small\">\n            <n-input\n              :value=\"drilldownResponse.fullContext\"\n              type=\"textarea\"\n              :autosize=\"{ minRows: 3, maxRows: 10 }\"\n              readonly\n            />\n          </n-card>\n          <n-card v-if=\"drilldownResponse.expandedChunks && drilldownResponse.expandedChunks.length > 0\" title=\"扩展片段\" size=\"small\">\n            <n-list>\n              <n-list-item v-for=\"chunk in drilldownResponse.expandedChunks\" :key=\"chunk.refId\">\n                <n-thing>\n                  <template #header>\n                    <n-space align=\"center\">\n                      <n-tag size=\"small\" :type=\"chunk.score >= 0.99 ? 'primary' : 'default'\">\n                        {{ chunk.title || chunk.filePath || '文档片段' }}\n                      </n-tag>\n                      <n-text v-if=\"chunk.headingPath\" depth=\"3\" style=\"font-size: 12px\">/ {{ chunk.headingPath }}</n-text>\n                      <n-text v-if=\"chunk.startLine > 0\" depth=\"3\" style=\"font-size: 12px\">(行 {{ chunk.startLine }}-{{ chunk.endLine }})</n-text>\n                    </n-space>\n                  </template>\n                  <template #header-extra>\n                    <n-space size=\"small\">\n                      <n-tag v-if=\"chunk.score >= 0.99\" size=\"small\" type=\"success\" :bordered=\"false\" round>\n                        当前命中\n                      </n-tag>\n                      <n-tag v-else size=\"small\" type=\"info\" :bordered=\"false\" round>\n                        上下文扩展\n                      </n-tag>\n                    </n-space>\n                  </template>\n                  <template #description>\n                    <n-text depth=\"3\" style=\"white-space: pre-wrap; line-height: 1.6; font-size: 13px\">{{ truncateContent(chunk.content || '', 300) }}</n-text>\n                  </template>\n                </n-thing>\n              </n-list-item>\n            </n-list>\n          </n-card>\n        </n-space>\n      </n-spin>\n    </n-modal>\n  </n-space>\n";
export default component;
